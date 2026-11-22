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

using System.Linq;
using System.Text.Json.Nodes;
using NUnit.Framework;
using QuantConnect.Services.Results;

namespace QuantConnect.Tests.Services.Results
{
    [TestFixture, Category("Services")]
    public class ResultsJsonTests
    {
        private static JsonArray Line(int count, System.Func<int, double> y) =>
            new(Enumerable.Range(0, count).Select(i => (JsonNode)new JsonArray(1_000_000 + i * 60, y(i))).ToArray());

        [Test]
        public void DownsampleKeepsFirstLastAndPeaks()
        {
            var values = Line(10_000, i => i == 4321 ? 1_000 : i % 7);
            var sampled = Downsampler.Downsample(values, 200);

            Assert.AreEqual(200, sampled.Count);
            Assert.AreEqual(Downsampler.X(values[0]), Downsampler.X(sampled[0]));
            Assert.AreEqual(Downsampler.X(values[^1]), Downsampler.X(sampled[^1]));
            Assert.IsTrue(sampled.Any(p => Downsampler.Y(p) == 1_000), "the spike survives LTTB");
            var xs = sampled.Select(Downsampler.X).ToList();
            CollectionAssert.IsOrdered(xs);
        }

        [Test]
        public void DownsampleLeavesShortSeriesAndStridesCandles()
        {
            var shortSeries = Line(50, i => i);
            Assert.AreEqual(50, Downsampler.Downsample(shortSeries, 100).Count);

            var candles = new JsonArray(Enumerable.Range(0, 1000).Select(i => (JsonNode)new JsonArray(i, 1, 2, 0.5, 1.5)).ToArray());
            var sampled = Downsampler.Downsample(candles, 10);
            Assert.AreEqual(10, sampled.Count);
            Assert.AreEqual(0d, Downsampler.X(sampled[0]));
            Assert.AreEqual(999d, Downsampler.X(sampled[^1]));
            Assert.AreEqual(5, ((JsonArray)sampled[3]).Count);
        }

        [Test]
        public void MergeAppendsOnlyNewerPoints()
        {
            var stored = LeanJson.NormalizeChart("Strategy Equity", Chart(Line(3, i => i)));
            var update = LeanJson.NormalizeChart("Strategy Equity", Chart(new JsonArray(
                new JsonArray(1_000_060, 1), new JsonArray(1_000_120, 2), new JsonArray(1_000_180, 3))));

            var merged = Downsampler.Merge(stored, update);
            var values = (JsonArray)merged["series"]!["Equity"]!["values"]!;
            CollectionAssert.AreEqual(new[] { 1_000_000d, 1_000_060d, 1_000_120d, 1_000_180d }, values.Select(Downsampler.X).ToArray());
        }

        [Test]
        public void NormalizesInlinePascalCaseLikeNewtonsoft()
        {
            Assert.AreEqual("orderId", LeanJson.CamelCase("OrderId"));
            Assert.AreEqual("id", LeanJson.CamelCase("ID"));
            Assert.AreEqual("utcTime", LeanJson.CamelCase("UtcTime"));
            Assert.AreEqual("alreadyCamel", LeanJson.CamelCase("alreadyCamel"));

            var chart = LeanJson.NormalizeChart("Benchmark", new JsonObject
            {
                ["Name"] = "Benchmark",
                ["ChartType"] = 0,
                ["Series"] = new JsonObject { ["SPY Price"] = new JsonObject { ["name"] = "SPY Price", ["values"] = new JsonArray() } },
            });
            Assert.AreEqual("Benchmark", (string)chart["name"]!);
            Assert.AreEqual(0, (int)chart["chartType"]!);
            Assert.IsNotNull(chart["series"]!["SPY Price"], "series names are dictionary keys and keep their case");
        }

        [Test]
        public void CursorsRoundTrip()
        {
            var cursor = ResultsQueries.EncodeCursor("638322500000000000|12|3");
            StringAssert.DoesNotContain("=", cursor);
            Assert.AreEqual("638322500000000000|12|3", ResultsQueries.DecodeCursor(cursor));
        }

        internal static JsonObject Chart(JsonArray values) => new()
        {
            ["Name"] = "Strategy Equity",
            ["ChartType"] = 0,
            ["Series"] = new JsonObject
            {
                ["Equity"] = new JsonObject { ["name"] = "Equity", ["unit"] = "$", ["index"] = 0, ["seriesType"] = 2, ["values"] = values },
            },
        };

        [Test]
        public void LiveStateKeepsInvestedHoldingsWithTheirSymbolAndTheCashBookInLeanRestoreShapes()
        {
            // Holdings and Cash as LiveTradingResultHandler sends them (keyed by SecurityIdentifier, symbol not in the value).
            var results = JsonNode.Parse(@"{""Holdings"":{""BTCUSD 2XR"":{""a"":105.59,""p"":100.36,""q"":495.9480457,""v"":49773},""SPY R735QTJ8XC9X"":{""a"":0,""q"":0}},
                ""Cash"":{""USD"":{""Symbol"":""USD"",""Amount"":47631.25},""BTC"":{""Symbol"":""BTC"",""Amount"":495.9480457}}}")!.AsObject();

            var state = ResultsIngestor.LiveStateOf(results)!;

            Assert.AreEqual(1, state["holdings"]!.AsArray().Count);
            var holdings = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.List<Holding>>(state["holdings"]!.ToJsonString());
            Assert.AreEqual(Symbol.Create("BTCUSD", SecurityType.Crypto, Market.Coinbase), holdings![0].Symbol);
            Assert.AreEqual(495.9480457m, holdings[0].Quantity);
            CollectionAssert.AreEquivalent(new[] { "USD", "BTC" }, state["cash"]!.AsArray().Select(c => (string)c!["currency"]));
            Assert.IsNull(ResultsIngestor.LiveStateOf(JsonNode.Parse(@"{""Orders"":{}}")!.AsObject()), "partial packets without the portfolio are not live state");
        }
    }
}
