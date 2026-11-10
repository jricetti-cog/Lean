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

using System.Collections.Generic;
using NUnit.Framework;
using QuantConnect.Configuration;

namespace QuantConnect.Tests.Configuration
{
    [TestFixture]
    public class LeanArgumentParserCoverageTests
    {
        [Test]
        public void ParsesSingleNoAndMultipleValueOptions()
        {
            var result = LeanArgumentParser.ParseArguments(new[]
            {
                "--config", "my-config.json",
                "--close-automatically", "true",
                "--algorithm-type-name", "BasicTemplateAlgorithm",
                "--force-exchange-always-open",
                "--parameters", "ema-fast:10,ema-slow:20,flag"
            });

            Assert.AreEqual("my-config.json", result["config"]);
            Assert.AreEqual("true", result["close-automatically"]);
            Assert.AreEqual("BasicTemplateAlgorithm", result["algorithm-type-name"]);
            Assert.AreEqual(true, result["force-exchange-always-open"]);
            var parameters = (Dictionary<string, string>)result["parameters"];
            Assert.AreEqual("10", parameters["ema-fast"]);
            Assert.AreEqual("20", parameters["ema-slow"]);
            Assert.AreEqual("", parameters["flag"]);
            Assert.IsFalse(result.ContainsKey("environment"));
        }

        [Test]
        public void ParsesEnvironmentsDictionary()
        {
            var result = LeanArgumentParser.ParseArguments(new[] { "--environments", "live:true", "--environment", "backtesting" });
            Assert.AreEqual("backtesting", result["environment"]);
            Assert.AreEqual("true", ((Dictionary<string, string>)result["environments"])["live"]);
        }

        [Test]
        public void NoArgumentsReturnsEmptyResult()
        {
            CollectionAssert.IsEmpty(LeanArgumentParser.ParseArguments(new string[0]));
        }

        [Test]
        public void GetParameterOrDefaultHelpers()
        {
            var result = LeanArgumentParser.ParseArguments(new[] { "--backtest-name", "test" });
            Assert.AreEqual("test", ApplicationParser.GetParameterOrDefault(result, "backtest-name", "x"));
            Assert.AreEqual("x", ApplicationParser.GetParameterOrDefault(result, "algorithm-id", "x"));
            Assert.AreEqual("test", ApplicationParser.GetParameterOrExit(result, "backtest-name"));
        }
    }
}
