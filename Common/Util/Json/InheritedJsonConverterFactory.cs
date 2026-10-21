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
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuantConnect.Util.Json
{
    /// <summary>
    /// Applies a <see cref="JsonConverterAttribute"/> declared on a base class to its derived classes, like Newtonsoft.Json did
    /// </summary>
    public sealed class InheritedJsonConverterFactory : JsonConverterFactory
    {
        private static readonly ConcurrentDictionary<Type, (Type BaseType, JsonConverterAttribute Attribute)> Attributes = new();

        /// <summary>Determines whether a base class of the type declares a converter</summary>
        public override bool CanConvert(Type typeToConvert)
        {
            return GetBaseAttribute(typeToConvert).Attribute != null;
        }

        /// <summary>Creates the converter for the derived type</summary>
        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            var (baseType, attribute) = GetBaseAttribute(typeToConvert);
            var converter = attribute.CreateConverter(baseType) ?? (JsonConverter)Activator.CreateInstance(attribute.ConverterType, true);
            if (converter is JsonConverterFactory factory)
            {
                converter = factory.CreateConverter(typeToConvert, options);
            }
            if (converter.Type == typeToConvert)
            {
                return converter;
            }
            return (JsonConverter)Activator.CreateInstance(typeof(InheritedConverter<,>).MakeGenericType(typeToConvert, converter.Type), converter);
        }

        private static (Type BaseType, JsonConverterAttribute Attribute) GetBaseAttribute(Type type)
        {
            return Attributes.GetOrAdd(type, t =>
            {
                if (!t.IsClass || t.IsDefined(typeof(JsonConverterAttribute), false))
                {
                    return default;
                }
                for (var baseType = t.BaseType; baseType != null && baseType != typeof(object); baseType = baseType.BaseType)
                {
                    var attribute = baseType.GetCustomAttribute<JsonConverterAttribute>(false);
                    if (attribute != null)
                    {
                        return (baseType, attribute);
                    }
                }
                return default;
            });
        }

        private sealed class InheritedConverter<TDerived, TBase> : JsonConverter<TDerived> where TDerived : TBase
        {
            private readonly JsonConverter<TBase> _converter;

            public InheritedConverter(JsonConverter<TBase> converter)
            {
                _converter = converter;
            }

            public override bool HandleNull => true;

            public override TDerived Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.Null && !_converter.HandleNull)
                {
                    return default;
                }
                return (TDerived)_converter.Read(ref reader, typeToConvert, options);
            }

            public override void Write(Utf8JsonWriter writer, TDerived value, JsonSerializerOptions options)
            {
                if (value == null && !_converter.HandleNull)
                {
                    writer.WriteNullValue();
                    return;
                }
                _converter.Write(writer, value, options);
            }
        }
    }

    /// <summary>
    /// <see cref="JsonConverterAttribute"/> that creates the converter passing the given constructor arguments
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Interface)]
    public sealed class JsonConverterWithArgsAttribute : JsonConverterAttribute
    {
        private readonly object[] _arguments;

        /// <summary>
        /// The converter type to instantiate
        /// </summary>
        public Type Converter { get; }

        /// <summary>
        /// Creates a new instance
        /// </summary>
        /// <param name="converterType">The converter type</param>
        /// <param name="arguments">The converter constructor arguments</param>
        public JsonConverterWithArgsAttribute(Type converterType, params object[] arguments)
        {
            Converter = converterType;
            _arguments = arguments;
        }

        /// <summary>
        /// Creates the converter
        /// </summary>
        public override JsonConverter CreateConverter(Type typeToConvert)
        {
            return (JsonConverter)Activator.CreateInstance(Converter, _arguments);
        }
    }
}
