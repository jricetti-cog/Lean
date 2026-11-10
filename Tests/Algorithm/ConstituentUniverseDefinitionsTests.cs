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
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using QuantConnect.Algorithm;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Securities;

namespace QuantConnect.Tests.Algorithm
{
    [TestFixture]
    public class ConstituentUniverseDefinitionsTests
    {
        private static readonly MethodInfo[] UniverseMethods = typeof(ConstituentUniverseDefinitions)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.ReturnType == typeof(Universe))
            .OrderBy(method => method.Name)
            .ToArray();

        [Test]
        public void ExposesAllMorningstarDefinitions()
        {
            Assert.AreEqual(72, UniverseMethods.Length);
            foreach (var method in UniverseMethods)
            {
                var parameters = method.GetParameters();
                Assert.AreEqual(1, parameters.Length, method.Name);
                Assert.AreEqual(typeof(UniverseSettings), parameters[0].ParameterType, method.Name);
                Assert.IsTrue(parameters[0].IsOptional, method.Name);
            }
        }

        [TestCaseSource(nameof(MethodNames))]
        public void CreatesConstituentsUniverseUsingAlgorithmUniverseSettings(string methodName)
        {
            var algorithm = new QCAlgorithm();
            var definitions = new ConstituentUniverseDefinitions(algorithm);

            var universe = Invoke(definitions, methodName, null);

            AssertConstituentUniverse(universe, methodName);
            Assert.AreSame(algorithm.UniverseSettings, universe.UniverseSettings);
        }

        [TestCaseSource(nameof(MethodNames))]
        public void CreatesConstituentsUniverseUsingProvidedUniverseSettings(string methodName)
        {
            var algorithm = new QCAlgorithm();
            var definitions = new ConstituentUniverseDefinitions(algorithm);
            var settings = new UniverseSettings(Resolution.Hour, 2m, false, true, TimeSpan.FromDays(3));

            var universe = Invoke(definitions, methodName, settings);

            AssertConstituentUniverse(universe, methodName);
            Assert.AreSame(settings, universe.UniverseSettings);
            Assert.AreEqual(Resolution.Hour, universe.UniverseSettings.Resolution);
            Assert.AreEqual(2m, universe.UniverseSettings.Leverage);
        }

        [Test]
        public void EveryDefinitionHasAUniqueSymbol()
        {
            var definitions = new ConstituentUniverseDefinitions(new QCAlgorithm());
            var symbols = UniverseMethods
                .Select(method => Invoke(definitions, method.Name, null).Configuration.Symbol)
                .ToList();

            Assert.AreEqual(symbols.Count, symbols.Distinct().Count());
        }

        [Test]
        public void DirectCallsMatchExpectedSymbols()
        {
            var definitions = new ConstituentUniverseDefinitions(new QCAlgorithm());

            Assert.AreEqual("CONSTITUENTS-UNIVERSE-AGGRESSIVEGROWTH", definitions.AggressiveGrowth().Configuration.Symbol.Value);
            Assert.AreEqual("CONSTITUENTS-UNIVERSE-BANKS", definitions.Banks().Configuration.Symbol.Value);
            Assert.AreEqual("CONSTITUENTS-UNIVERSE-REITS", definitions.REITs().Configuration.Symbol.Value);
            Assert.AreEqual("CONSTITUENTS-UNIVERSE-SEMICONDUCTORS", definitions.Semiconductors().Configuration.Symbol.Value);
            Assert.AreEqual(definitions.Software().Configuration.Symbol, definitions.Software().Configuration.Symbol);
            Assert.AreNotEqual(definitions.Software().Configuration.Symbol, definitions.Hardware().Configuration.Symbol);
        }

        [Test]
        public void UniverseCanBeAddedToAlgorithm()
        {
            var algorithm = new QCAlgorithm();
            var definitions = new ConstituentUniverseDefinitions(algorithm);

            var universe = algorithm.AddUniverse(definitions.Biotechnology());

            Assert.IsInstanceOf<ConstituentsUniverse>(universe);
            Assert.AreEqual("CONSTITUENTS-UNIVERSE-BIOTECHNOLOGY", universe.Configuration.Symbol.Value);
        }

        private static void AssertConstituentUniverse(Universe universe, string methodName)
        {
            Assert.IsInstanceOf<ConstituentsUniverse>(universe);

            var symbol = universe.Configuration.Symbol;
            var expectedTicker = $"constituents-universe-{methodName}";
            // symbol values are always upper cased
            Assert.AreEqual(expectedTicker.ToUpperInvariant(), symbol.Value);
            Assert.AreEqual(SecurityType.Equity, symbol.SecurityType);
            Assert.AreEqual(Market.USA, symbol.ID.Market);
            StringAssert.AreEqualIgnoringCase(expectedTicker, symbol.ID.Symbol);
            Assert.AreEqual(typeof(ConstituentsUniverseData), universe.Configuration.Type);
        }

        private static Universe Invoke(ConstituentUniverseDefinitions definitions, string methodName, UniverseSettings settings)
        {
            var method = UniverseMethods.Single(m => m.Name == methodName);
            return (Universe)method.Invoke(definitions, new object[] { settings });
        }

        private static IEnumerable<string> MethodNames()
        {
            return UniverseMethods.Select(method => method.Name);
        }
    }
}
