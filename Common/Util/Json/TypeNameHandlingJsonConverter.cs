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
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Reflection;
using System.Collections;
using System.Text.Json.Nodes;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace QuantConnect.Util.Json
{
    /// <summary>
    /// Replicates Newtonsoft's TypeNameHandling.All: objects are written with a leading "$type" property holding the runtime type name,
    /// and read back as that type. Only types assignable to the converted (or configured base) type are resolved.
    /// </summary>
    public class TypeNameHandlingJsonConverter : JsonConverterFactory
    {
        /// <summary>
        /// The type discriminator property name
        /// </summary>
        public const string TypeProperty = "$type";

        /// <summary>
        /// The collection values property name
        /// </summary>
        public const string ValuesProperty = "$values";

        private readonly Type _baseType;

        /// <summary>
        /// Creates a converter which applies to the decorated member type
        /// </summary>
        public TypeNameHandlingJsonConverter()
        {
        }

        /// <summary>
        /// Creates a converter which applies to all types assignable to the given base type
        /// </summary>
        public TypeNameHandlingJsonConverter(Type baseType)
        {
            _baseType = baseType;
        }

        /// <summary>
        /// Determines whether this instance can convert the specified object type
        /// </summary>
        public override bool CanConvert(Type typeToConvert)
        {
            if (_baseType != null)
            {
                return _baseType.IsAssignableFrom(typeToConvert);
            }
            return (typeToConvert.IsClass || typeToConvert.IsInterface) && typeToConvert != typeof(string)
                && !typeof(IEnumerable).IsAssignableFrom(typeToConvert);
        }

        /// <summary>
        /// Creates the converter for the given type
        /// </summary>
        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            return (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert));
        }

        /// <summary>
        /// Deserializes a json payload which might be a "$type" annotated object or collection ("$values"),
        /// resolving only types assignable to the given base type or collections of it. Returns the parsed json node otherwise
        /// </summary>
        public static object Deserialize(string json, Type baseType, JsonSerializerOptions options)
        {
            var node = JsonSerialization.Parse(json);
            if (node is JsonObject jObject && jObject.TryGetPropertyValue(TypeProperty, out var typeNode) && typeNode != null)
            {
                var type = ResolveType(typeNode.GetValue<string>());
                if (jObject.TryGetPropertyValue(ValuesProperty, out var values))
                {
                    var elementType = type?.GetInterfaces().Concat(new[] { type })
                        .FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0];
                    if (elementType == null || !baseType.IsAssignableFrom(elementType))
                    {
                        throw new JsonException($"Type specified in JSON '{typeNode}' is not compatible with '{baseType}'.");
                    }
                    return values.Deserialize(type, options);
                }
                if (type == null || !baseType.IsAssignableFrom(type))
                {
                    throw new JsonException($"Type specified in JSON '{typeNode}' is not compatible with '{baseType}'.");
                }
                return ReadObject(jObject, type, options);
            }
            return node;
        }

        /// <summary>
        /// Gets the type name the way newtonsoft wrote it, full type name and simple assembly name
        /// </summary>
        public static string GetTypeName(Type type)
        {
            var name = type.AssemblyQualifiedName;
            var builder = new StringBuilder();
            // Newtonsoft ReflectionUtils.RemoveAssemblyDetails
            var writingAssemblyName = false;
            var skippingAssemblyDetails = false;
            var followBrackets = false;
            foreach (var current in name)
            {
                switch (current)
                {
                    case '[':
                        writingAssemblyName = false;
                        skippingAssemblyDetails = false;
                        followBrackets = true;
                        builder.Append(current);
                        break;
                    case ']':
                        writingAssemblyName = false;
                        skippingAssemblyDetails = false;
                        followBrackets = false;
                        builder.Append(current);
                        break;
                    case ',':
                        if (followBrackets)
                        {
                            builder.Append(current);
                        }
                        else if (!writingAssemblyName)
                        {
                            writingAssemblyName = true;
                            builder.Append(current);
                        }
                        else
                        {
                            skippingAssemblyDetails = true;
                        }
                        break;
                    default:
                        followBrackets = false;
                        if (!skippingAssemblyDetails)
                        {
                            builder.Append(current);
                        }
                        break;
                }
            }
            return builder.ToString();
        }

        /// <summary>
        /// Resolves the type from a "Full.Type.Name, AssemblyName" type name
        /// </summary>
        public static Type ResolveType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return null;
            }
            var type = Type.GetType(typeName, false);
            if (type != null)
            {
                return type;
            }
            var separator = typeName.LastIndexOf(',');
            if (separator < 0 || typeName.IndexOf(']', separator) >= 0)
            {
                return AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(typeName, false)).FirstOrDefault(t => t != null);
            }
            var name = typeName.Substring(0, separator).Trim();
            var assemblyName = typeName.Substring(separator + 1).Trim();
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == assemblyName);
            if (assembly == null)
            {
                try
                {
                    assembly = Assembly.Load(assemblyName);
                }
                catch
                {
                    return null;
                }
            }
            return assembly.GetType(name, false);
        }

        private static object ReadObject(JsonObject jObject, Type type, JsonSerializerOptions options)
        {
            var copy = new JsonObject();
            foreach (var kvp in jObject)
            {
                if (kvp.Key != TypeProperty)
                {
                    copy[kvp.Key] = kvp.Value?.DeepClone();
                }
            }
            return copy.Deserialize(LeanJsonContract.GetDefaultTypeInfo(type, options));
        }

        private class Converter<T> : JsonConverter<T>
        {
            public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                var node = JsonNode.Parse(ref reader);
                if (node is not JsonObject jObject)
                {
                    return node == null ? default : node.Deserialize<T>(JsonSerialization.Create());
                }
                var type = typeToConvert;
                if (jObject.TryGetPropertyValue(TypeProperty, out var typeNode) && typeNode != null)
                {
                    var resolved = ResolveType(typeNode.GetValue<string>());
                    if (resolved == null || !typeToConvert.IsAssignableFrom(resolved))
                    {
                        throw new JsonException($"Type specified in JSON '{typeNode}' is not compatible with '{typeToConvert}'.");
                    }
                    type = resolved;
                }
                return (T)ReadObject(jObject, type, options);
            }

            public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            {
                var type = value.GetType();
                var node = JsonSerializer.SerializeToNode(value, LeanJsonContract.GetDefaultTypeInfo(type, options));
                writer.WriteStartObject();
                writer.WriteString(TypeProperty, GetTypeName(type));
                foreach (var kvp in node.AsObject())
                {
                    writer.WritePropertyName(kvp.Key);
                    if (kvp.Value == null)
                    {
                        writer.WriteNullValue();
                    }
                    else
                    {
                        kvp.Value.WriteTo(writer, options);
                    }
                }
                writer.WriteEndObject();
            }
        }
    }
}
