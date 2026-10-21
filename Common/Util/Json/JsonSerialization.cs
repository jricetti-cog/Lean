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
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using QuantConnect.Util.Json;

namespace QuantConnect
{
    /// <summary>
    /// Shared System.Text.Json serialization settings and helpers. The options replicate the behavior
    /// LEAN had with Newtonsoft.Json so the serialized wire format does not change
    /// </summary>
    public static class JsonSerialization
    {
        private static readonly ConcurrentDictionary<JsonSerializerOptions, JsonSerializerOptions> IndentedOptions = new();

        /// <summary>
        /// Lenient parsing settings for json documents, allows comments and trailing commas
        /// </summary>
        public static JsonDocumentOptions DocumentOptions { get; } = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 128 };

        /// <summary>
        /// The default options, member names are used as is
        /// </summary>
        public static JsonSerializerOptions Default { get; } = Create();

        /// <summary>
        /// Camel case options, names explicitly set with <see cref="JsonPropertyNameAttribute"/> are camel cased too
        /// </summary>
        public static JsonSerializerOptions CamelCase { get; } = Create(camelCase: true);

        /// <summary>
        /// Creates a new options instance with LEAN's serialization behavior
        /// </summary>
        /// <param name="camelCase">True to camel case member names</param>
        /// <param name="indented">True to indent the output</param>
        /// <param name="floatAsDecimal">True to read untyped floating point numbers as decimals</param>
        /// <param name="converters">Additional converters, these take precedence over the default ones</param>
        public static JsonSerializerOptions Create(bool camelCase = false, bool indented = false, bool floatAsDecimal = false, params JsonConverter[] converters)
        {
            var options = new JsonSerializerOptions
            {
                Encoder = LeanJsonEncoder.Instance,
                IncludeFields = true,
                PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                MaxDepth = 128,
                WriteIndented = indented,
                PropertyNamingPolicy = camelCase ? NewtonsoftCamelCaseNamingPolicy.Instance : null,
                TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { LeanJsonContract.Modify } },
            };
            foreach (var converter in converters)
            {
                options.Converters.Add(converter);
            }
            AddDefaultConverters(options, floatAsDecimal);
            return options;
        }

        /// <summary>
        /// Gets the indented version of the given options
        /// </summary>
        public static JsonSerializerOptions Indented(JsonSerializerOptions options = null)
        {
            return IndentedOptions.GetOrAdd(options ?? Default, x => new JsonSerializerOptions(x) { WriteIndented = true });
        }

        /// <summary>
        /// Serializes the value using its runtime type
        /// </summary>
        public static string Serialize(object value, JsonSerializerOptions options = null)
        {
            return JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object), options ?? Default);
        }

        /// <summary>
        /// Serializes the value using its runtime type
        /// </summary>
        public static string Serialize(object value, bool indented)
        {
            return Serialize(value, indented ? Indented() : Default);
        }

        /// <summary>
        /// Serializes the value using its runtime type into the stream
        /// </summary>
        public static void Serialize(Stream stream, object value, JsonSerializerOptions options = null)
        {
            JsonSerializer.Serialize(stream, value, value?.GetType() ?? typeof(object), options ?? Default);
        }

        /// <summary>
        /// Deserializes the json, returns the default value for empty content like Newtonsoft.Json did
        /// </summary>
        public static T Deserialize<T>(string json, JsonSerializerOptions options = null)
        {
            ArgumentNullException.ThrowIfNull(json);
            if (string.IsNullOrWhiteSpace(json))
            {
                return default;
            }
            try
            {
                return JsonSerializer.Deserialize<T>(json, options ?? Default);
            }
            catch (JsonException) when (TryNormalizeQuotes(json, out var normalized))
            {
                return JsonSerializer.Deserialize<T>(normalized, options ?? Default);
            }
        }

        /// <summary>
        /// Deserializes the json, returns null for empty content like Newtonsoft.Json did
        /// </summary>
        public static object Deserialize(string json, Type type, JsonSerializerOptions options = null)
        {
            ArgumentNullException.ThrowIfNull(json);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }
            try
            {
                return JsonSerializer.Deserialize(json, type, options ?? Default);
            }
            catch (JsonException) when (TryNormalizeQuotes(json, out var normalized))
            {
                return JsonSerializer.Deserialize(normalized, type, options ?? Default);
            }
        }

        /// <summary>
        /// Deserializes json without a target type: objects become <see cref="JsonObject"/>, arrays <see cref="JsonArray"/>
        /// and primitives their .NET value
        /// </summary>
        public static object Deserialize(string json)
        {
            return Deserialize(json, typeof(object));
        }

        /// <summary>
        /// Deserializes the stream content
        /// </summary>
        public static T Deserialize<T>(Stream stream, JsonSerializerOptions options = null)
        {
            return JsonSerializer.Deserialize<T>(stream, options ?? Default);
        }

        /// <summary>
        /// Parses the json leniently (comments and trailing commas are allowed)
        /// </summary>
        public static JsonNode Parse(string json)
        {
            try
            {
                return JsonNode.Parse(json, documentOptions: DocumentOptions);
            }
            catch (JsonException) when (TryNormalizeQuotes(json, out var normalized))
            {
                return JsonNode.Parse(normalized, documentOptions: DocumentOptions);
            }
        }

        /// <summary>
        /// Newtonsoft.Json accepted single quoted strings and property names, STJ only accepts double quotes.
        /// Rewrites single quoted strings into double quoted ones
        /// </summary>
        private static bool TryNormalizeQuotes(string json, out string normalized)
        {
            normalized = null;
            if (json.IndexOf('\'') < 0)
            {
                return false;
            }
            var builder = new StringBuilder(json.Length);
            char? quote = null;
            for (var i = 0; i < json.Length; i++)
            {
                var c = json[i];
                if (quote == null)
                {
                    if (c == '\'' || c == '"')
                    {
                        quote = c;
                        builder.Append('"');
                    }
                    else
                    {
                        builder.Append(c);
                    }
                    continue;
                }
                if (c == '\\' && i + 1 < json.Length)
                {
                    var next = json[++i];
                    if (next == '\'' && quote == '\'')
                    {
                        builder.Append('\'');
                    }
                    else
                    {
                        builder.Append(c).Append(next);
                    }
                }
                else if (c == quote)
                {
                    quote = null;
                    builder.Append('"');
                }
                else if (c == '"')
                {
                    builder.Append("\\\"");
                }
                else
                {
                    builder.Append(c);
                }
            }
            normalized = builder.ToString();
            return normalized != json;
        }

        /// <summary>
        /// Parses the json leniently into an object
        /// </summary>
        public static JsonObject ParseObject(string json)
        {
            return Parse(json) as JsonObject ?? throw new JsonException("The json content is not an object.");
        }

        /// <summary>
        /// Converts the node into the given type
        /// </summary>
        public static T ToObject<T>(this JsonNode node, JsonSerializerOptions options = null)
        {
            return node.Deserialize<T>(options ?? Default);
        }

        /// <summary>
        /// Converts the value into a json node
        /// </summary>
        public static JsonNode FromObject(object value, JsonSerializerOptions options = null)
        {
            return JsonSerializer.SerializeToNode(value, value?.GetType() ?? typeof(object), options ?? Default);
        }

        private static void AddDefaultConverters(JsonSerializerOptions options, bool floatAsDecimal)
        {
            options.Converters.Add(new LeanDecimalConverter());
            options.Converters.Add(new LeanDoubleConverter());
            options.Converters.Add(new LeanSingleConverter());
            options.Converters.Add(new LeanStringConverter());
            options.Converters.Add(new LeanBooleanConverter());
            options.Converters.Add(new LeanDateTimeConverter());
            options.Converters.Add(new LeanDateTimeOffsetConverter());
            options.Converters.Add(new LeanTimeSpanConverter());
            options.Converters.Add(new LeanRegexConverter());
            options.Converters.Add(new LeanObjectConverter(floatAsDecimal));
            options.Converters.Add(new LeanEnumConverter());
            options.Converters.Add(new Orders.OrderJsonConverter());
            options.Converters.Add(new InheritedJsonConverterFactory());
            options.Converters.Add(new ConstructorBindingJsonConverterFactory());
        }
    }
}
