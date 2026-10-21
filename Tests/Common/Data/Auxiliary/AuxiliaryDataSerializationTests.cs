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
 *
*/

using System;
using System.Text.Json;
using NUnit.Framework;
using QuantConnect.Data.Market;
using QuantConnect.Util.Json;

namespace QuantConnect.Tests.Common.Data.Auxiliary
{
    [TestFixture]
    public class AuxiliaryDataSerializationTests
    {
        private readonly JsonSerializerOptions _settings = JsonSerialization.Create(converters: new TypeNameHandlingJsonConverter(typeof(QuantConnect.Data.BaseData)));

        [Test]
        public void DeserializesSplitWarning()
        {
            var splitWarning = new Split(Symbols.AAPL, new DateTime(2014, 6, 9), 645.57m, 0.142857m, SplitType.Warning);

            var json = JsonSerialization.Serialize(splitWarning, _settings);
            var deserialized = JsonSerialization.Deserialize<Split>(json, _settings);

            Assert.AreEqual(splitWarning.Symbol, deserialized.Symbol);
            Assert.AreEqual(splitWarning.Time, deserialized.Time);
            Assert.AreEqual(splitWarning.Type, deserialized.Type);
            Assert.AreEqual(splitWarning.ReferencePrice, deserialized.ReferencePrice);
            Assert.AreEqual(splitWarning.SplitFactor, deserialized.SplitFactor);
        }

        [Test]
        public void DeserializesSplit()
        {
            var split = new Split(Symbols.AAPL, new DateTime(2014, 6, 9), 645.57m, 0.142857m, SplitType.SplitOccurred);

            var json = JsonSerialization.Serialize(split, _settings);
            var deserialized = JsonSerialization.Deserialize<Split>(json, _settings);

            Assert.AreEqual(split.Symbol, deserialized.Symbol);
            Assert.AreEqual(split.Time, deserialized.Time);
            Assert.AreEqual(split.Type, deserialized.Type);
            Assert.AreEqual(split.ReferencePrice, deserialized.ReferencePrice);
            Assert.AreEqual(split.SplitFactor, deserialized.SplitFactor);
        }

        [Test]
        public void DeserializesDividend()
        {
            var dividend = new Dividend(Symbols.AAPL, new DateTime(2014, 11, 6), 0.47m, 108.60m);

            var json = JsonSerialization.Serialize(dividend, _settings);
            var deserialized = JsonSerialization.Deserialize<Dividend>(json, _settings);

            Assert.AreEqual(dividend.Symbol, deserialized.Symbol);
            Assert.AreEqual(dividend.Time, deserialized.Time);
            Assert.AreEqual(dividend.Distribution, deserialized.Distribution);
        }

        [Test]
        public void DeserializesDelistingWarning()
        {
            var delistingWarning = new Delisting(Symbols.AAPL, new DateTime(2999, 12, 31), 100m, DelistingType.Warning);

            var json = JsonSerialization.Serialize(delistingWarning, _settings);
            var deserialized = JsonSerialization.Deserialize<Delisting>(json, _settings);

            Assert.AreEqual(delistingWarning.Symbol, deserialized.Symbol);
            Assert.AreEqual(delistingWarning.Time, deserialized.Time);
            Assert.AreEqual(delistingWarning.Type, deserialized.Type);
        }

        [Test]
        public void DeserializesDelisting()
        {
            var delisting = new Delisting(Symbols.AAPL, new DateTime(2999, 12, 31), 100m, DelistingType.Delisted);

            var json = JsonSerialization.Serialize(delisting, _settings);
            var deserialized = JsonSerialization.Deserialize<Delisting>(json, _settings);

            Assert.AreEqual(delisting.Symbol, deserialized.Symbol);
            Assert.AreEqual(delisting.Time, deserialized.Time);
            Assert.AreEqual(delisting.Type, deserialized.Type);
        }
    }
}
