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
using System.Text.Json;
using System.Globalization;
using System.Text.Json.Serialization;

namespace QuantConnect.Util
{
    /// <summary>
    /// Provides a json converter that allows defining the date time format used
    /// </summary>
    public class DateTimeJsonConverter : JsonConverterFactory
    {
        private readonly string[] _formats;

        /// <summary>
        /// Creates a new instance
        /// </summary>
        public DateTimeJsonConverter(string format)
        {
            _formats = new[] { format };
        }

        /// <summary>
        /// Creates a new instance
        /// </summary>
        public DateTimeJsonConverter(string format, string format2)
        {
            _formats = new[] { format, format2 };
        }

        /// <summary>
        /// Creates a new instance
        /// </summary>
        public DateTimeJsonConverter(string format, string format2, string format3)
        {
            _formats = new[] { format, format2, format3 };
        }

        /// <summary>
        /// Determines whether this instance can convert the specified object type
        /// </summary>
        public override bool CanConvert(Type typeToConvert)
        {
            return typeToConvert == typeof(DateTime) || typeToConvert == typeof(DateTime?);
        }

        /// <summary>
        /// Creates the converter for the given type
        /// </summary>
        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            return typeToConvert == typeof(DateTime) ? new Converter(_formats) : new NullableConverter(_formats);
        }

        private static DateTime? ReadValue(ref Utf8JsonReader reader, string[] formats, bool nullable)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                if (nullable)
                {
                    return null;
                }
                throw new JsonException($"Cannot convert null value to {typeof(DateTime)}.");
            }
            if (reader.TokenType == JsonTokenType.String)
            {
                // newtonsoft parsed iso dates before handing them to the converter
                if (reader.TryGetDateTime(out var isoDate))
                {
                    return isoDate;
                }
                var text = reader.GetString();
                if (nullable && string.IsNullOrEmpty(text))
                {
                    return null;
                }
                foreach (var format in formats)
                {
                    if (DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result))
                    {
                        return result;
                    }
                }
            }
            throw new JsonException($"Unexpected value when converting date. Expected formats: {string.Join(",", formats)}");
        }

        private class Converter : JsonConverter<DateTime>
        {
            private readonly string[] _formats;

            public Converter(string[] formats)
            {
                _formats = formats;
            }

            public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                return ReadValue(ref reader, _formats, false).Value;
            }

            public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
            {
                writer.WriteStringValue(value.ToString(_formats[0], CultureInfo.InvariantCulture));
            }
        }

        private class NullableConverter : JsonConverter<DateTime?>
        {
            private readonly string[] _formats;

            public NullableConverter(string[] formats)
            {
                _formats = formats;
            }

            public override bool HandleNull => true;

            public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                return ReadValue(ref reader, _formats, true);
            }

            public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
            {
                if (value == null)
                {
                    writer.WriteNullValue();
                    return;
                }
                writer.WriteStringValue(value.Value.ToString(_formats[0], CultureInfo.InvariantCulture));
            }
        }
    }
}
