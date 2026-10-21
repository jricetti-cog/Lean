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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace QuantConnect.Util.Json
{
    /// <summary>
    /// Deserializes types whose single public constructor has parameters without a matching member, binding the matching
    /// parameters by name and passing default values for the rest, like Newtonsoft.Json did. System.Text.Json throws instead
    /// </summary>
    public sealed class ConstructorBindingJsonConverterFactory : JsonConverterFactory
    {
        private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly ConcurrentDictionary<Type, ConstructorInfo> Constructors = new();
        private static readonly ConcurrentDictionary<(JsonSerializerOptions, JsonConverter), JsonSerializerOptions> ConverterOptions = new();

        /// <summary>Determines whether System.Text.Json would fail to bind the constructor of the type</summary>
        public override bool CanConvert(Type typeToConvert)
        {
            return GetConstructor(typeToConvert) != null;
        }

        /// <summary>Creates the converter for the type</summary>
        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            return (JsonConverter)Activator.CreateInstance(typeof(ConstructorBindingConverter<>).MakeGenericType(typeToConvert), GetConstructor(typeToConvert));
        }

        private static ConstructorInfo GetConstructor(Type type)
        {
            return Constructors.GetOrAdd(type, t =>
            {
                if (!t.IsClass || t.IsAbstract || t.IsArray || t.IsGenericTypeDefinition || t.Namespace == null
                    || !t.Namespace.StartsWith("QuantConnect", StringComparison.Ordinal) || t.IsDefined(typeof(JsonConverterAttribute), true))
                {
                    return null;
                }
                var constructors = t.GetConstructors(InstanceMembers);
                // newtonsoft bound [JsonConstructor] parameters by name only, STJ also requires the exact member type
                var constructor = constructors.FirstOrDefault(x => x.IsDefined(typeof(JsonConstructorAttribute)));
                if (constructor == null)
                {
                    if (t.GetConstructor(Type.EmptyTypes) != null)
                    {
                        return null;
                    }
                    var publicConstructors = constructors.Where(x => x.IsPublic).ToList();
                    if (publicConstructors.Count != 1)
                    {
                        return null;
                    }
                    constructor = publicConstructors[0];
                }
                if (constructor.GetParameters().Length == 0)
                {
                    return null;
                }
                var members = t.GetMembers(InstanceMembers).Where(m => m is PropertyInfo || m is FieldInfo)
                    .Where(m => m.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition != JsonIgnoreCondition.Always).ToList();
                var bindsAll = constructor.GetParameters().All(p => members.Any(m => GetMemberType(m) == p.ParameterType
                    && (string.Equals(m.Name, p.Name, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(m.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name, p.Name, StringComparison.OrdinalIgnoreCase))));
                return bindsAll ? null : constructor;
            });
        }

        private static Type GetMemberType(MemberInfo member)
        {
            return member is FieldInfo field ? field.FieldType : ((PropertyInfo)member).PropertyType;
        }

        private static string FindKey(JsonObject json, string name)
        {
            if (name == null)
            {
                return null;
            }
            if (json.ContainsKey(name))
            {
                return name;
            }
            return json.Select(x => x.Key).FirstOrDefault(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        }

        private static object Deserialize(JsonNode node, Type type, JsonConverter converter, JsonSerializerOptions options)
        {
            if (converter != null)
            {
                options = ConverterOptions.GetOrAdd((options, converter), key =>
                {
                    var result = new JsonSerializerOptions(key.Item1);
                    result.Converters.Insert(0, key.Item2);
                    return result;
                });
            }
            var value = node?.Deserialize(type, options);
            return value == null && type.IsValueType ? Activator.CreateInstance(type) : value;
        }

        private sealed class ConstructorBindingConverter<T> : JsonConverter<T>
        {
            private readonly ConstructorInfo _constructor;

            public ConstructorBindingConverter(ConstructorInfo constructor)
            {
                _constructor = constructor;
            }

            public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (JsonNode.Parse(ref reader) is not JsonObject json)
                {
                    throw new JsonException($"Unexpected token when deserializing {typeof(T).Name}");
                }
                var info = LeanJsonContract.GetDefaultTypeInfo(typeof(T), options);
                var used = new HashSet<string>(StringComparer.Ordinal);
                var parameters = _constructor.GetParameters();
                var arguments = new object[parameters.Length];
                for (var i = 0; i < parameters.Length; i++)
                {
                    var parameter = parameters[i];
                    var property = info.Properties.FirstOrDefault(x => string.Equals((x.AttributeProvider as MemberInfo)?.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));
                    var key = FindKey(json, parameter.Name) ?? FindKey(json, property?.Name);
                    if (key != null)
                    {
                        used.Add(key);
                        arguments[i] = Deserialize(json[key], parameter.ParameterType, property?.CustomConverter, options);
                    }
                    else
                    {
                        arguments[i] = parameter.HasDefaultValue ? parameter.DefaultValue
                            : parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null;
                    }
                }

                var result = (T)_constructor.Invoke(arguments);
                foreach (var property in info.Properties)
                {
                    var key = property.Set == null ? null : FindKey(json, property.Name);
                    if (key != null && used.Add(key))
                    {
                        property.Set(result, Deserialize(json[key], property.PropertyType, property.CustomConverter, options));
                    }
                }
                return result;
            }

            public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            {
                writer.WriteDefault(value, options);
            }
        }
    }

    /// <summary>
    /// Serializes the type with case sensitive property names, for types with members whose names only differ by casing,
    /// which System.Text.Json rejects when reading case insensitively
    /// </summary>
    public sealed class CaseSensitiveJsonConverter<T> : JsonConverter<T>
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> Options = new();

        private static JsonSerializerOptions GetOptions(JsonSerializerOptions options)
        {
            return Options.GetValue(options, x => new JsonSerializerOptions(x) { PropertyNameCaseInsensitive = false });
        }

        /// <summary>Reads the value</summary>
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return (T)LeanJsonContract.ReadDefault(ref reader, typeToConvert, GetOptions(options));
        }

        /// <summary>Writes the value</summary>
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            writer.WriteDefault(value, GetOptions(options));
        }
    }
}
