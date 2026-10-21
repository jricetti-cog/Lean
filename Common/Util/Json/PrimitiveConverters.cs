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
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuantConnect.Util.Json
{
    /// <summary>
    /// Writes and reads decimals the way Newtonsoft.Json did: whole numbers keep a ".0" suffix and
    /// numbers given as strings are accepted
    /// </summary>
    public sealed class LeanDecimalConverter : JsonConverter<decimal>
    {
        /// <summary>Reads a decimal</summary>
        public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Number:
                    if (reader.TryGetDecimal(out var value))
                    {
                        return value;
                    }
                    return Convert.ToDecimal(reader.GetDouble());
                case JsonTokenType.String:
                    return decimal.Parse(reader.GetString(), NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture);
                default:
                    throw new JsonException($"Unexpected token {reader.TokenType} when parsing decimal.");
            }
        }

        /// <summary>Writes a decimal</summary>
        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
        {
            Span<char> buffer = stackalloc char[64];
            value.TryFormat(buffer, out var written, provider: CultureInfo.InvariantCulture);
            writer.WriteRawValue(NumberFormatting.EnsureDecimalPlace(buffer, written), skipInputValidation: true);
        }

        /// <summary>Reads a dictionary key</summary>
        public override decimal ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return decimal.Parse(reader.GetString(), NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture);
        }

        /// <summary>Writes a dictionary key</summary>
        public override void WriteAsPropertyName(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
        {
            writer.WritePropertyName(value.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Writes and reads doubles the way Newtonsoft.Json did: round trip format, whole numbers keep a ".0" suffix
    /// and NaN/Infinity are written as strings
    /// </summary>
    public sealed class LeanDoubleConverter : JsonConverter<double>
    {
        /// <summary>Reads a double</summary>
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.TokenType switch
            {
                JsonTokenType.Number => reader.GetDouble(),
                JsonTokenType.String => NumberFormatting.ParseDouble(reader.GetString()),
                _ => throw new JsonException($"Unexpected token {reader.TokenType} when parsing double.")
            };
        }

        /// <summary>Writes a double</summary>
        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        {
            if (!double.IsFinite(value))
            {
                writer.WriteStringValue(value.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            Span<char> buffer = stackalloc char[64];
            value.TryFormat(buffer, out var written, "R", CultureInfo.InvariantCulture);
            writer.WriteRawValue(NumberFormatting.EnsureDecimalPlace(buffer, written), skipInputValidation: true);
        }

        /// <summary>Reads a dictionary key</summary>
        public override double ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return NumberFormatting.ParseDouble(reader.GetString());
        }

        /// <summary>Writes a dictionary key</summary>
        public override void WriteAsPropertyName(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        {
            writer.WritePropertyName(value.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Writes and reads floats the way Newtonsoft.Json did
    /// </summary>
    public sealed class LeanSingleConverter : JsonConverter<float>
    {
        /// <summary>Reads a float</summary>
        public override float Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.TokenType switch
            {
                JsonTokenType.Number => (float)reader.GetDouble(),
                JsonTokenType.String => (float)NumberFormatting.ParseDouble(reader.GetString()),
                _ => throw new JsonException($"Unexpected token {reader.TokenType} when parsing float.")
            };
        }

        /// <summary>Writes a float</summary>
        public override void Write(Utf8JsonWriter writer, float value, JsonSerializerOptions options)
        {
            if (!float.IsFinite(value))
            {
                writer.WriteStringValue(value.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            Span<char> buffer = stackalloc char[64];
            value.TryFormat(buffer, out var written, "R", CultureInfo.InvariantCulture);
            writer.WriteRawValue(NumberFormatting.EnsureDecimalPlace(buffer, written), skipInputValidation: true);
        }

        /// <summary>Reads a dictionary key</summary>
        public override float ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return (float)NumberFormatting.ParseDouble(reader.GetString());
        }

        /// <summary>Writes a dictionary key</summary>
        public override void WriteAsPropertyName(Utf8JsonWriter writer, float value, JsonSerializerOptions options)
        {
            writer.WritePropertyName(value.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// Reads strings leniently like Newtonsoft.Json did, accepting numbers and booleans
    /// </summary>
    public sealed class LeanStringConverter : JsonConverter<string>
    {
        /// <summary>Allow handling null tokens so they map to null</summary>
        public override bool HandleNull => false;

        /// <summary>Reads a string</summary>
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                case JsonTokenType.PropertyName:
                    return reader.GetString();
                case JsonTokenType.Number:
                    // like newtonsoft's JsonReader.ReadAsString, the value is kept as written
                    return NumberFormatting.GetRawText(ref reader);
                case JsonTokenType.True:
                    return "true";
                case JsonTokenType.False:
                    return "false";
                default:
                    throw new JsonException($"Unexpected token {reader.TokenType} when parsing string.");
            }
        }

        /// <summary>Writes a string</summary>
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value);
        }

        /// <summary>Reads a dictionary key</summary>
        public override string ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.GetString();
        }

        /// <summary>Writes a dictionary key</summary>
        public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            writer.WritePropertyName(value);
        }
    }

    /// <summary>
    /// Reads booleans leniently like Newtonsoft.Json did, accepting strings and integers
    /// </summary>
    public sealed class LeanBooleanConverter : JsonConverter<bool>
    {
        /// <summary>Reads a boolean</summary>
        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.TokenType switch
            {
                JsonTokenType.True => true,
                JsonTokenType.False => false,
                JsonTokenType.String => bool.Parse(reader.GetString().Trim()),
                JsonTokenType.Number => reader.GetDouble() != 0,
                _ => throw new JsonException($"Unexpected token {reader.TokenType} when parsing boolean.")
            };
        }

        /// <summary>Writes a boolean</summary>
        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
        {
            writer.WriteBooleanValue(value);
        }

        /// <summary>Reads a dictionary key</summary>
        public override bool ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return bool.Parse(reader.GetString());
        }

        /// <summary>Writes a dictionary key</summary>
        public override void WriteAsPropertyName(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
        {
            writer.WritePropertyName(value ? bool.TrueString : bool.FalseString);
        }
    }

    /// <summary>
    /// Serializes <see cref="Regex"/> as an object with its pattern and options, like Newtonsoft.Json's RegexConverter
    /// </summary>
    public sealed class LeanRegexConverter : JsonConverter<Regex>
    {
        /// <summary>Reads a regex</summary>
        public override Regex Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                return new Regex(reader.GetString());
            }
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException($"Unexpected token {reader.TokenType} when reading Regex.");
            }
            string pattern = null;
            var regexOptions = RegexOptions.None;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var name = reader.GetString();
                reader.Read();
                if (string.Equals(name, "Pattern", StringComparison.OrdinalIgnoreCase))
                {
                    pattern = reader.GetString();
                }
                else if (string.Equals(name, "Options", StringComparison.OrdinalIgnoreCase))
                {
                    regexOptions = JsonSerializer.Deserialize<RegexOptions>(ref reader, options);
                }
                else
                {
                    reader.Skip();
                }
            }
            return pattern == null ? null : new Regex(pattern, regexOptions);
        }

        /// <summary>Writes a regex</summary>
        public override void Write(Utf8JsonWriter writer, Regex value, JsonSerializerOptions options)
        {
            var policy = options.PropertyNamingPolicy;
            writer.WriteStartObject();
            writer.WriteString(policy?.ConvertName("Pattern") ?? "Pattern", value.ToString());
            writer.WriteNumber(policy?.ConvertName("Options") ?? "Options", (int)value.Options);
            writer.WriteEndObject();
        }
    }

    /// <summary>
    /// Writes dates in the ISO format Newtonsoft.Json used and reads them leniently
    /// </summary>
    public sealed class LeanDateTimeConverter : JsonConverter<DateTime>
    {
        /// <summary>
        /// The format Newtonsoft.Json used to write dates
        /// </summary>
        public const string Format = "yyyy'-'MM'-'dd'T'HH':'mm':'ss.FFFFFFFK";

        /// <summary>Reads a date</summary>
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException($"Unexpected token {reader.TokenType} when parsing date.");
            }
            return Parse(reader.GetString());
        }

        /// <summary>Writes a date</summary>
        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        {
            Span<char> buffer = stackalloc char[40];
            value.TryFormat(buffer, out var written, Format, CultureInfo.InvariantCulture);
            writer.WriteStringValue(buffer.Slice(0, written));
        }

        /// <summary>Reads a dictionary key</summary>
        public override DateTime ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return Parse(reader.GetString());
        }

        /// <summary>Writes a dictionary key</summary>
        public override void WriteAsPropertyName(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        {
            writer.WritePropertyName(value.ToString(Format, CultureInfo.InvariantCulture));
        }

        private static DateTime Parse(string value)
        {
            return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }
    }

    /// <summary>
    /// Writes date time offsets in the ISO format Newtonsoft.Json used and reads them leniently
    /// </summary>
    public sealed class LeanDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        private const string Format = "yyyy'-'MM'-'dd'T'HH':'mm':'ss.FFFFFFFzzz";

        /// <summary>Reads a date</summary>
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException($"Unexpected token {reader.TokenType} when parsing date.");
            }
            return reader.TryGetDateTimeOffset(out var value)
                ? value
                : DateTimeOffset.Parse(reader.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }

        /// <summary>Writes a date</summary>
        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        {
            Span<char> buffer = stackalloc char[40];
            value.TryFormat(buffer, out var written, Format, CultureInfo.InvariantCulture);
            writer.WriteStringValue(buffer.Slice(0, written));
        }
    }

    /// <summary>
    /// Reads time spans the way Newtonsoft.Json did, using <see cref="TimeSpan.Parse(string, IFormatProvider)"/>
    /// </summary>
    public sealed class LeanTimeSpanConverter : JsonConverter<TimeSpan>
    {
        /// <summary>
        /// Reads the time span
        /// </summary>
        public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException($"Unexpected token {reader.TokenType} when parsing a time span.");
            }
            return TimeSpan.Parse(reader.GetString(), CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Writes the time span using the constant format
        /// </summary>
        public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString("c", CultureInfo.InvariantCulture));
        }
    }

    internal static class NumberFormatting
    {
        public static ReadOnlySpan<char> EnsureDecimalPlace(Span<char> buffer, int written)
        {
            var text = buffer.Slice(0, written);
            if (text.IndexOfAny('.', 'E', 'e') >= 0)
            {
                return text;
            }
            buffer[written] = '.';
            buffer[written + 1] = '0';
            return buffer.Slice(0, written + 2);
        }

        public static double ParseDouble(string value)
        {
            return double.Parse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
        }

        public static string GetRawText(ref Utf8JsonReader reader)
        {
            return System.Text.Encoding.UTF8.GetString(reader.HasValueSequence ? System.Buffers.BuffersExtensions.ToArray(reader.ValueSequence) : reader.ValueSpan);
        }
    }
}
