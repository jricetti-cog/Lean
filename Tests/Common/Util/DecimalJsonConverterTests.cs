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
using System.Text.Json;
using System.Text.Json.Serialization;
using QuantConnect.Util.Json;
using NUnit.Framework;
using QuantConnect.Util;

namespace QuantConnect.Tests.Common.Util;

public class DecimalJsonConverterTests
{
    private static readonly JsonSerializerOptions settings = JsonSerialization.Create(converters: new DecimalJsonConverter());

    [TestCase("1", 1, true)]
    [TestCase("0", 0, true)]
    [TestCase("-1", -1, true)]
    [TestCase("1.333", 1.333, true)]
    [TestCase("1.333", 1.333, false)]
    [TestCase("9e-8", 0.00000009, true)]
    [TestCase("1.3117285e+06", 1311728.5, true)]
    [TestCase("9e-8", 0.00000009, false)]
    [TestCase("1.3117285e+06", 1311728.5, false)]
    public void DecimalStringConverterTests(string value, decimal expected, bool quote = true)
    {
        var jsonString = TestObject<decimal>.CreateJsonObject(value, quote);
        var obj = JsonSerialization.Deserialize<TestObject<decimal>>(jsonString, settings);

        Assert.AreEqual(expected, obj.Value);
    }

    [TestCase("1", 1, true)]
    [TestCase("0", 0, true)]
    [TestCase("-1", -1, true)]
    [TestCase("1.333", 1.333, true)]
    [TestCase("1.333", 1.333, false)]
    [TestCase("9e-8", 0.00000009, true)]
    [TestCase("1.3117285e+06", 1311728.5, true)]
    [TestCase("9e-8", 0.00000009, false)]
    [TestCase("1.3117285e+06", 1311728.5, false)]
    public void NullableDecimalStringConverterTests(string value, decimal expected, bool quote = true)
    {
        var jsonString = TestObject<decimal?>.CreateJsonObject(value, quote);
        var obj = JsonSerialization.Deserialize<TestObject<decimal?>>(jsonString, settings);

        Assert.AreEqual(expected, obj.Value);
    }

    [Test]
    public void ThrowsConversionError()
    {
        var jsonString = """
                             {"value": "qwerty"}
                         """;

        Assert.Throws<FormatException>(() =>
            JsonSerialization.Deserialize<TestObject<decimal>>(jsonString, settings));
    }

    [Test]
    public void NullableDecimalHandlesNull()
    {
        var jsonString = """
                             {"value": null}
                         """;
        var obj = JsonSerialization.Deserialize<TestObject<decimal?>>(jsonString, settings);

        Assert.IsNull(obj.Value);
    }

    [Test]
    public void NonNullableDecimalThrowsOnNull()
    {
        var jsonString = """
                             {"value": null}
                         """;

        Assert.Throws<JsonException>(() =>
            JsonSerialization.Deserialize<TestObject<decimal>>(jsonString, settings));
    }

    [Test]
    public void SerializesDecimalAsString()
    {
        var obj = new TestObject<decimal> { Value = 1.333m };
        var json = JsonSerialization.Serialize(obj, settings);
        var jsonOrigin = JsonSerialization.Serialize(obj);

        Assert.AreEqual(jsonOrigin, json);
    }

    [Test]
    public void SerializesNullableDecimalAsStringFallbackToOriginBehavior()
    {
        var obj = new TestObject<decimal?> { Value = 1.333m };
        var json = JsonSerialization.Serialize(obj, settings);
        var jsonOrigin = JsonSerialization.Serialize(obj);

        Assert.AreEqual(jsonOrigin, json);
    }

    [Test]
    public void SerializesNullableDecimalNullAsNull()
    {
        var obj = new TestObject<decimal?> { Value = null };
        var json = JsonSerialization.Serialize(obj, settings);
        var jsonOrigin = JsonSerialization.Serialize(obj);

        Assert.AreEqual(jsonOrigin, json);
    }

    private class TestObject<T>
    {
        public T Value { get; set; }

        public static string CreateJsonObject(string value, bool quote = false)
        {
            if (quote)
            {
                value = $"\"{value}\"";
            }

            return $$"""
                         {"value": {{value}}}
                     """;
        }
    }
}
