/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace QuantConnect.Util.Json
{
    /// <summary>
    /// Builds System.Text.Json contracts that follow the Newtonsoft.Json default contract resolver rules,
    /// so the serialized output of existing types does not change:
    /// member order, non public members with <see cref="JsonPropertyNameAttribute"/>/<see cref="JsonIncludeAttribute"/>,
    /// non public setters and constructors, <see cref="DefaultValueAttribute"/> aware default value handling,
    /// population of get only collections and serialization of the runtime type of the values
    /// </summary>
    public static class LeanJsonContract
    {
        private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly ConditionalWeakTable<JsonSerializerOptions, ConcurrentDictionary<Type, JsonTypeInfo>> DefaultContracts = new();
        private static readonly MethodInfo CreateDefaultTypeInfoMethod = typeof(LeanJsonContract).GetMethod(nameof(CreateDefaultTypeInfo), BindingFlags.NonPublic | BindingFlags.Static);

        /// <summary>
        /// Type info modifier to be used with <see cref="DefaultJsonTypeInfoResolver"/>
        /// </summary>
        public static void Modify(JsonTypeInfo info)
        {
            if (info.Kind != JsonTypeInfoKind.Object)
            {
                return;
            }
            ConfigureProperties(info);
            ConfigureCreator(info);
            ConfigurePolymorphism(info);
        }

        /// <summary>
        /// Gets an object contract for the given type that ignores any converter registered for it. Allows converters
        /// to fall back to the default serialization of the type they handle
        /// </summary>
        public static JsonTypeInfo GetDefaultTypeInfo(Type type, JsonSerializerOptions options)
        {
            var contracts = DefaultContracts.GetValue(options, _ => new ConcurrentDictionary<Type, JsonTypeInfo>());
            return contracts.GetOrAdd(type, t => (JsonTypeInfo)CreateDefaultTypeInfoMethod.MakeGenericMethod(t).Invoke(null, new object[] { options }));
        }

        /// <summary>
        /// Serializes the value using the default object contract of its runtime type, ignoring converters registered for it
        /// </summary>
        public static void WriteDefault(this Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            if (value == null)
            {
                writer.WriteNullValue();
                return;
            }
            JsonSerializer.Serialize(writer, value, GetDefaultTypeInfo(value.GetType(), options));
        }

        /// <summary>
        /// Deserializes the value using the default object contract of the given type, ignoring converters registered for it
        /// </summary>
        public static object ReadDefault(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            return JsonSerializer.Deserialize(ref reader, GetDefaultTypeInfo(type, options));
        }

        private static JsonTypeInfo CreateDefaultTypeInfo<T>(JsonSerializerOptions options)
        {
            var creator = CreateConstructorDelegate(typeof(T));
            var info = JsonMetadataServices.CreateObjectInfo(options, new JsonObjectInfoValues<T>
            {
                ObjectCreator = creator == null ? null : () => (T)creator(),
                PropertyMetadataInitializer = _ => Array.Empty<JsonPropertyInfo>()
            });
            ConfigureProperties(info);
            return info;
        }

        internal static void ConfigureProperties(JsonTypeInfo info)
        {
            var type = info.Type;
            var policy = info.Options.PropertyNamingPolicy;
            var existing = new Dictionary<(Type, string), JsonPropertyInfo>();
            foreach (var property in info.Properties)
            {
                if (property.AttributeProvider is MemberInfo member)
                {
                    existing[(member.DeclaringType, member.Name)] = property;
                }
            }

            var result = new List<JsonPropertyInfo>();
            var names = new HashSet<string>();
            var order = 0;
            var hasParameterlessConstructor = type.IsValueType || type.GetConstructor(InstanceMembers, null, Type.EmptyTypes, null) != null;
            foreach (var member in GetMembersInNewtonsoftOrder(type))
            {
                var ignore = member.GetCustomAttribute<JsonIgnoreAttribute>();
                if (ignore != null && ignore.Condition == JsonIgnoreCondition.Always)
                {
                    continue;
                }
                var attributeName = member.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
                var attributed = attributeName != null || member.IsDefined(typeof(JsonIncludeAttribute));
                var memberType = member is FieldInfo field ? field.FieldType : ((PropertyInfo)member).PropertyType;
                if (!attributed && !IsPublic(member) || memberType.IsByRefLike || memberType.IsPointer || memberType.IsByRef)
                {
                    continue;
                }

                var name = attributeName ?? member.Name;
                if (policy != null)
                {
                    name = policy.ConvertName(name);
                }
                if (!names.Add(name))
                {
                    continue;
                }

                if (!existing.TryGetValue((member.DeclaringType, member.Name), out var property))
                {
                    property = info.CreateJsonPropertyInfo(memberType, name);
                    property.AttributeProvider = member;
                    var converter = member.GetCustomAttribute<JsonConverterAttribute>();
                    if (converter != null)
                    {
                        property.CustomConverter = converter.CreateConverter(memberType) ?? (JsonConverter)Activator.CreateInstance(converter.ConverterType);
                    }
                    if (ignore?.Condition == JsonIgnoreCondition.WhenWritingNull)
                    {
                        property.ShouldSerialize = (_, value) => value != null;
                    }
                }
                property.Name = name;
                property.Order = order++;

                if (property.Get == null && (member is FieldInfo || attributed))
                {
                    property.Get = CreateGetter(member);
                }
                if (property.Set == null && CanSet(member, attributed))
                {
                    property.Set = CreateSetter(member);
                }
                if (ignore?.Condition == JsonIgnoreCondition.WhenWritingDefault)
                {
                    var defaultValue = member.GetCustomAttribute<DefaultValueAttribute>() is { } attribute
                        ? attribute.Value
                        : memberType.IsValueType ? Activator.CreateInstance(memberType) : null;
                    property.ShouldSerialize = (_, value) => !ValueEquals(value, defaultValue);
                }
                if (property.Set == null && property.Get != null && hasParameterlessConstructor && property.CustomConverter == null && CanPopulate(memberType))
                {
                    property.ObjectCreationHandling = JsonObjectCreationHandling.Populate;
                }
                result.Add(property);
            }

            info.Properties.Clear();
            foreach (var property in result)
            {
                info.Properties.Add(property);
            }
        }

        private static void ConfigureCreator(JsonTypeInfo info)
        {
            var type = info.Type;
            if (info.CreateObject != null || type.IsAbstract || type.IsInterface || type.IsValueType)
            {
                return;
            }
            var constructors = type.GetConstructors(InstanceMembers);
            if (constructors.Any(x => x.IsDefined(typeof(JsonConstructorAttribute))))
            {
                return;
            }
            var publicConstructors = constructors.Where(x => x.IsPublic).ToList();
            if (publicConstructors.Count == 1 && publicConstructors[0].GetParameters().Length > 0)
            {
                return;
            }
            var creator = CreateConstructorDelegate(type);
            if (creator != null)
            {
                info.CreateObject = creator;
            }
        }

        private static void ConfigurePolymorphism(JsonTypeInfo info)
        {
            var type = info.Type;
            if (type.IsSealed || type.IsValueType || type == typeof(object) || info.PolymorphismOptions != null
                || type.IsDefined(typeof(JsonPolymorphicAttribute)) || type.IsDefined(typeof(JsonDerivedTypeAttribute)) || IsFrameworkType(type))
            {
                return;
            }
            var derivedTypes = DerivedTypeIndex.Get(type);
            if (derivedTypes.Count == 0)
            {
                return;
            }
            var polymorphism = new JsonPolymorphismOptions { UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToBaseType };
            foreach (var derivedType in derivedTypes)
            {
                polymorphism.DerivedTypes.Add(new JsonDerivedType(derivedType));
            }
            info.PolymorphismOptions = polymorphism;
        }

        /// <summary>
        /// Gets the instance fields and properties of the type in the order Newtonsoft.Json serialized them:
        /// fields first, then properties, followed by private members of the base types
        /// </summary>
        public static IEnumerable<MemberInfo> GetMembersInNewtonsoftOrder(Type type)
        {
            var members = new List<MemberInfo>(type.GetFields(InstanceMembers).Where(x => !x.IsDefined(typeof(CompilerGeneratedAttribute))));
            for (var baseType = type.BaseType; baseType != null; baseType = baseType.BaseType)
            {
                members.AddRange(baseType.GetFields(InstanceMembers & ~BindingFlags.Public).Where(x => x.IsPrivate && !x.IsDefined(typeof(CompilerGeneratedAttribute))));
            }
            var properties = type.GetProperties(InstanceMembers).Where(x => x.GetIndexParameters().Length == 0).ToList();
            for (var baseType = type.BaseType; baseType != null; baseType = baseType.BaseType)
            {
                foreach (var property in baseType.GetProperties(InstanceMembers & ~BindingFlags.Public))
                {
                    if (property.GetIndexParameters().Length == 0 && !IsPublic(property) && properties.All(x => x.Name != property.Name))
                    {
                        properties.Add(property);
                    }
                }
            }
            members.AddRange(properties);
            var seen = new HashSet<string>();
            return members.Where(x => seen.Add(x.Name));
        }

        private static bool IsPublic(MemberInfo member)
        {
            return member switch
            {
                FieldInfo field => field.IsPublic,
                PropertyInfo property => property.GetMethod?.IsPublic == true || property.SetMethod?.IsPublic == true,
                _ => false
            };
        }

        private static bool CanSet(MemberInfo member, bool attributed)
        {
            return member switch
            {
                FieldInfo field => !field.IsInitOnly || attributed,
                PropertyInfo property => property.SetMethod != null && (property.SetMethod.IsPublic || attributed),
                _ => false
            };
        }

        private static bool CanPopulate(Type type)
        {
            if (!type.IsClass && !type.IsInterface || type.IsArray || type == typeof(string) || type.IsDefined(typeof(JsonConverterAttribute))
                || type.Name.StartsWith("ReadOnly", StringComparison.Ordinal) || type.Name.StartsWith("Immutable", StringComparison.Ordinal))
            {
                return false;
            }
            var interfaces = type.IsInterface ? type.GetInterfaces().Append(type) : type.GetInterfaces();
            return interfaces.Any(x => x.IsGenericType && (x.GetGenericTypeDefinition() == typeof(ICollection<>) || x.GetGenericTypeDefinition() == typeof(IDictionary<,>)));
        }

        private static bool ValueEquals(object value, object defaultValue)
        {
            if (value == null || defaultValue == null)
            {
                return value == null && defaultValue == null;
            }
            if (value.GetType() != defaultValue.GetType() && IsNumeric(value) && IsNumeric(defaultValue))
            {
                try
                {
                    return Convert.ToDecimal(value) == Convert.ToDecimal(defaultValue);
                }
                catch (OverflowException)
                {
                    return false;
                }
            }
            return value.Equals(defaultValue);
        }

        private static bool IsNumeric(object value)
        {
            return Type.GetTypeCode(value.GetType()) is >= TypeCode.SByte and <= TypeCode.Decimal;
        }

        internal static bool IsFrameworkType(Type type)
        {
            var name = type.Assembly.GetName().Name;
            return name == null || name.StartsWith("System", StringComparison.Ordinal) || name.StartsWith("Microsoft", StringComparison.Ordinal)
                || name == "mscorlib" || name == "netstandard";
        }

        private static Func<object> CreateConstructorDelegate(Type type)
        {
            if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters)
            {
                return null;
            }
            if (type.IsValueType)
            {
                return () => Activator.CreateInstance(type);
            }
            var constructor = type.GetConstructor(InstanceMembers, null, Type.EmptyTypes, null);
            if (constructor == null)
            {
                return null;
            }
            return Expression.Lambda<Func<object>>(Expression.Convert(Expression.New(constructor), typeof(object))).Compile();
        }

        private static Func<object, object> CreateGetter(MemberInfo member)
        {
            try
            {
                var instance = Expression.Parameter(typeof(object));
                var typed = Expression.Convert(instance, member.DeclaringType);
                var access = member is FieldInfo field ? Expression.Field(typed, field) : Expression.Property(typed, (PropertyInfo)member);
                return Expression.Lambda<Func<object, object>>(Expression.Convert(access, typeof(object)), instance).Compile();
            }
            catch (Exception)
            {
                return member is FieldInfo field ? field.GetValue : ((PropertyInfo)member).GetValue;
            }
        }

        private static Action<object, object> CreateSetter(MemberInfo member)
        {
            if (member.DeclaringType.IsValueType)
            {
                return member is FieldInfo field ? field.SetValue : ((PropertyInfo)member).SetValue;
            }
            try
            {
                var instance = Expression.Parameter(typeof(object));
                var value = Expression.Parameter(typeof(object));
                var typed = Expression.Convert(instance, member.DeclaringType);
                Expression assign;
                if (member is FieldInfo field)
                {
                    if (field.IsInitOnly)
                    {
                        return field.SetValue;
                    }
                    assign = Expression.Assign(Expression.Field(typed, field), Expression.Convert(value, field.FieldType));
                }
                else
                {
                    var property = (PropertyInfo)member;
                    assign = Expression.Call(typed, property.SetMethod, Expression.Convert(value, property.PropertyType));
                }
                return Expression.Lambda<Action<object, object>>(assign, instance, value).Compile();
            }
            catch (Exception)
            {
                return member is FieldInfo field ? field.SetValue : ((PropertyInfo)member).SetValue;
            }
        }

        /// <summary>
        /// Index of the loaded types deriving from a given type, used to serialize values with their runtime type
        /// </summary>
        private static class DerivedTypeIndex
        {
            private static readonly ConcurrentDictionary<Assembly, Type[]> AssemblyTypes = new();

            public static List<Type> Get(Type baseType)
            {
                var result = new List<Type>();
                var baseAssembly = baseType.Assembly.GetName().Name;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.IsDynamic || assembly != baseType.Assembly && !assembly.GetReferencedAssemblies().Any(x => x.Name == baseAssembly))
                    {
                        continue;
                    }
                    foreach (var type in AssemblyTypes.GetOrAdd(assembly, GetTypes))
                    {
                        if (type != baseType && baseType.IsAssignableFrom(type) && !type.ContainsGenericParameters && !type.IsByRefLike
                            && !type.IsDefined(typeof(CompilerGeneratedAttribute)))
                        {
                            result.Add(type);
                        }
                    }
                }
                return result;
            }

            private static Type[] GetTypes(Assembly assembly)
            {
                try
                {
                    return assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    return e.Types.Where(x => x != null).ToArray();
                }
            }
        }
    }
}
