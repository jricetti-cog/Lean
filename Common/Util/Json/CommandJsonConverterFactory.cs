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
using System.Collections.Generic;
using System.Dynamic;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using QuantConnect.Commands;

namespace QuantConnect.Util.Json
{
    /// <summary>
    /// Replicates Newtonsoft's dynamic contract for <see cref="Command"/> types: json members matching a public writable member are
    /// assigned to it, the rest are stored as dynamic properties. Writes every dynamic member name the command exposes
    /// </summary>
    public sealed class CommandJsonConverterFactory : JsonConverterFactory
    {
        /// <summary>
        /// Determines whether this instance can convert the specified object type
        /// </summary>
        public override bool CanConvert(Type typeToConvert)
        {
            return typeof(Command).IsAssignableFrom(typeToConvert) && !typeToConvert.IsAbstract;
        }

        /// <summary>
        /// Creates the converter for the given command type
        /// </summary>
        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            return (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert));
        }

        private sealed class Converter<T> : JsonConverter<T> where T : Command
        {
            private const BindingFlags PublicMembers = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

            public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                var node = JsonNode.Parse(ref reader);
                if (node == null)
                {
                    return null;
                }
                if (node is not JsonObject jObject)
                {
                    throw new JsonException($"Unexpected json value when reading {typeToConvert.Name}, expected an object.");
                }

                var command = (T)Activator.CreateInstance(typeToConvert, nonPublic: true);
                foreach (var kvp in jObject)
                {
                    var property = typeToConvert.GetProperty(kvp.Key, PublicMembers);
                    if (property != null && property.GetIndexParameters().Length == 0 && property.SetMethod?.IsPublic == true)
                    {
                        property.SetValue(command, kvp.Value?.Deserialize(property.PropertyType, options));
                        continue;
                    }
                    var field = typeToConvert.GetField(kvp.Key, PublicMembers);
                    if (field != null && !field.IsInitOnly)
                    {
                        field.SetValue(command, kvp.Value?.Deserialize(field.FieldType, options));
                        continue;
                    }
                    command.SetProperty(kvp.Key, kvp.Value is JsonObject or JsonArray ? kvp.Value : kvp.Value?.Deserialize<object>(options));
                }
                return command;
            }

            public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            {
                var metaObject = ((IDynamicMetaObjectProvider)value).GetMetaObject(Expression.Constant(value));
                writer.WriteStartObject();
                foreach (var name in metaObject.GetDynamicMemberNames())
                {
                    var member = value.GetProperty(name);
                    writer.WritePropertyName(options.DictionaryKeyPolicy?.ConvertName(name) ?? name);
                    JsonSerializer.Serialize(writer, member, member?.GetType() ?? typeof(object), options);
                }
                writer.WriteEndObject();
            }
        }
    }
}
