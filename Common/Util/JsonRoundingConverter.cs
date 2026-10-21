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
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuantConnect.Util.Json;

namespace QuantConnect.Util
{
    /// <summary>
    /// Helper <see cref="JsonConverter"/> that will round decimal and double types,
    /// to <see cref="FractionalDigits"/> fractional digits
    /// </summary>
    public class JsonRoundingConverter : JsonConverterFactory
    {
        /// <summary>
        /// The number of fractional digits to round to
        /// </summary>
        public const int FractionalDigits = 4;

        /// <summary>
        /// Determines whether this instance can convert the specified object type.
        /// </summary>
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(decimal) || objectType == typeof(double) || objectType == typeof(int);
        }

        /// <summary>
        /// Creates the converter for the given type
        /// </summary>
        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            if (typeToConvert == typeof(int))
            {
                return new Int32Converter();
            }
            return typeToConvert == typeof(double) ? new DoubleConverter() : new DecimalConverter();
        }

        private sealed class Int32Converter : JsonConverter<int>
        {
            public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                return JsonSerializer.Deserialize<int>(ref reader, options);
            }

            public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
            {
                writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
            }
        }

        private sealed class DecimalConverter : JsonConverter<decimal>
        {
            private static readonly LeanDecimalConverter Reader = new();

            public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                return Reader.Read(ref reader, typeToConvert, options);
            }

            public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
            {
                // we serialize decimal as string so that json doesn't use exponential notation which actually will lose precision
                writer.WriteStringValue(Math.Round(value, FractionalDigits).ToString(CultureInfo.InvariantCulture));
            }
        }

        private sealed class DoubleConverter : JsonConverter<double>
        {
            private static readonly LeanDoubleConverter Reader = new();

            public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                return Reader.Read(ref reader, typeToConvert, options);
            }

            public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
            {
                writer.WriteStringValue(Math.Round(value, FractionalDigits).ToString(CultureInfo.InvariantCulture));
            }
        }
    }
}
