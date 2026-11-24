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
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuantConnect.Util.Json
{
    /// <summary>
    /// Enum converter matching Newtonsoft.Json: enums are written as numbers unless the string format is requested
    /// and are read from numbers, numeric strings or (case insensitive) names, including <see cref="EnumMemberAttribute"/> values
    /// </summary>
    public class StringEnumJsonConverter : JsonConverterFactory
    {
        private readonly bool _writeAsString;
        private readonly bool _camelCase;

        /// <summary>
        /// Creates a converter that writes enum names
        /// </summary>
        public StringEnumJsonConverter() : this(false)
        {
        }

        /// <summary>
        /// Creates a converter that writes enum names
        /// </summary>
        /// <param name="camelCase">True to camel case the written names</param>
        public StringEnumJsonConverter(bool camelCase) : this(true, camelCase)
        {
        }

        /// <summary>
        /// Creates a converter that writes enum names, the type parameter is used for compatibility with
        /// the naming strategy argument of the old Newtonsoft.Json converter
        /// </summary>
        public StringEnumJsonConverter(Type namingStrategy) : this(true, namingStrategy?.Name.StartsWith("CamelCase", StringComparison.Ordinal) == true)
        {
        }

        internal StringEnumJsonConverter(bool writeAsString, bool camelCase)
        {
            _writeAsString = writeAsString;
            _camelCase = camelCase;
        }

        /// <summary>Determines whether the type is an enum</summary>
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

        /// <summary>Creates the converter for the enum type</summary>
        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            return (JsonConverter)Activator.CreateInstance(typeof(EnumConverter<>).MakeGenericType(typeToConvert), _writeAsString, _camelCase);
        }

        private sealed class EnumConverter<T> : JsonConverter<T> where T : struct, Enum
        {
            private static readonly bool IsFlags = typeof(T).IsDefined(typeof(FlagsAttribute), false);
            private static readonly TypeCode UnderlyingType = Type.GetTypeCode(Enum.GetUnderlyingType(typeof(T)));
            private readonly bool _writeAsString;
            private readonly Dictionary<T, string> _names = new();
            private readonly Dictionary<string, T> _values = new(StringComparer.OrdinalIgnoreCase);

            public EnumConverter(bool writeAsString, bool camelCase)
            {
                _writeAsString = writeAsString;
                foreach (var field in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    var value = (T)field.GetValue(null);
                    var name = field.GetCustomAttribute<EnumMemberAttribute>()?.Value;
                    if (name == null)
                    {
                        name = camelCase ? NewtonsoftCamelCaseNamingPolicy.ToCamelCase(field.Name) : field.Name;
                    }
                    _names.TryAdd(value, name);
                    _values.TryAdd(name, value);
                    _values.TryAdd(field.Name, value);
                }
            }

            public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.Number:
                        return FromNumber(ref reader);
                    case JsonTokenType.String:
                        return Parse(reader.GetString());
                    default:
                        throw new JsonException($"Unexpected token {reader.TokenType} when parsing enum {typeof(T).Name}.");
                }
            }

            public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            {
                if (_writeAsString && TryGetName(value, out var name))
                {
                    writer.WriteStringValue(name);
                    return;
                }
                switch (UnderlyingType)
                {
                    case TypeCode.UInt64:
                        writer.WriteNumberValue(Convert.ToUInt64(value, CultureInfo.InvariantCulture));
                        break;
                    default:
                        writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                        break;
                }
            }

            public override T ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                return Parse(reader.GetString());
            }

            public override void WriteAsPropertyName(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            {
                writer.WritePropertyName(TryGetName(value, out var name) ? name : Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
            }

            private bool TryGetName(T value, out string name)
            {
                if (_names.TryGetValue(value, out name))
                {
                    return true;
                }
                if (IsFlags)
                {
                    var parts = value.ToString().Split(", ");
                    if (parts.All(x => _values.ContainsKey(x)))
                    {
                        name = string.Join(", ", parts.Select(x => _names[_values[x]]));
                        return true;
                    }
                }
                return false;
            }

            private static T FromNumber(ref Utf8JsonReader reader)
            {
                if (reader.TryGetInt64(out var value))
                {
                    return (T)Enum.ToObject(typeof(T), value);
                }
                if (reader.TryGetUInt64(out var unsigned))
                {
                    return (T)Enum.ToObject(typeof(T), unsigned);
                }
                throw new JsonException($"Unable to convert number to enum {typeof(T).Name}.");
            }

            private T Parse(string text)
            {
                if (string.IsNullOrEmpty(text))
                {
                    throw new JsonException($"Unable to convert empty string to enum {typeof(T).Name}.");
                }
                if (_values.TryGetValue(text.Trim(), out var value))
                {
                    return value;
                }
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                {
                    return (T)Enum.ToObject(typeof(T), number);
                }
                if (IsFlags)
                {
                    ulong result = 0;
                    foreach (var part in text.Split(','))
                    {
                        if (!_values.TryGetValue(part.Trim(), out var flag))
                        {
                            throw new JsonException($"Requested value '{text}' was not found in enum {typeof(T).Name}.");
                        }
                        result |= Convert.ToUInt64(flag, CultureInfo.InvariantCulture);
                    }
                    return (T)Enum.ToObject(typeof(T), result);
                }
                throw new JsonException($"Requested value '{text}' was not found in enum {typeof(T).Name}.");
            }
        }
    }

    /// <summary>
    /// The default enum handling of Newtonsoft.Json: written as numbers, read from numbers or names
    /// </summary>
    public sealed class LeanEnumConverter : StringEnumJsonConverter
    {
        /// <summary>
        /// Creates a new instance
        /// </summary>
        public LeanEnumConverter() : base(false, false)
        {
        }

        /// <summary>Determines whether the type is an enum without its own converter</summary>
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum && !typeToConvert.IsDefined(typeof(JsonConverterAttribute), false);
    }
}
