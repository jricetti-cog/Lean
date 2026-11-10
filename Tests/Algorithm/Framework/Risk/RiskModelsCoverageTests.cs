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
using System.Linq;
using NUnit.Framework;
using QuantConnect.Algorithm.Framework.Alphas;
using QuantConnect.Algorithm.Framework.Portfolio;
using QuantConnect.Algorithm.Framework.Risk;
using QuantConnect.Data.Market;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Tests.Engine.DataFeeds;

namespace QuantConnect.Tests.Algorithm.Framework.Risk
{
    [TestFixture]
    public class RiskModelsCoverageTests
    {
        [TestCase(110, 0.05, true)]
        [TestCase(104, 0.05, false)]
        [TestCase(110, -0.05, true)]
        [TestCase(110, 0.2, false)]
        public void MaximumUnrealizedProfitLiquidatesWhenThresholdIsExceeded(decimal price, decimal threshold, bool liquidate)
        {
            var algorithm = CreateAlgorithm();
            var spy = algorithm.AddEquity("SPY");
            var aapl = algorithm.AddEquity("AAPL");
            spy.SetMarketPrice(new Tick(algorithm.Time, spy.Symbol, price, price));
            spy.Holdings.SetHoldings(100m, 10);
            aapl.SetMarketPrice(new Tick(algorithm.Time, aapl.Symbol, 500m, 500m));

            var model = new MaximumUnrealizedProfitPercentPerSecurity(threshold);
            var targets = model.ManageRisk(algorithm, Array.Empty<IPortfolioTarget>()).ToList();

            if (liquidate)
            {
                var target = targets.Single();
                Assert.AreEqual(spy.Symbol, target.Symbol);
                Assert.AreEqual(0m, target.Quantity);
            }
            else
            {
                Assert.IsEmpty(targets);
            }
        }

        [TestCase(0)]
        [TestCase(-0.1)]
        public void MaximumSectorExposureRejectsNonPositiveExposure(decimal exposure)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new MaximumSectorExposureRiskManagementModel(exposure));
        }

        [Test]
        public void MaximumSectorExposureRequiresFundamentalData()
        {
            var algorithm = CreateAlgorithm();
            algorithm.AddEquity("SPY");
            var model = new MaximumSectorExposureRiskManagementModel();

            var exception = Assert.Throws<Exception>(() => model.OnSecuritiesChanged(algorithm, SecurityChanges.None));
            StringAssert.Contains("fundamental data", exception.Message);
        }

        [Test]
        public void MaximumSectorExposureIgnoresSecuritiesWithoutFundamentals()
        {
            var algorithm = CreateAlgorithm();
            var model = new MaximumSectorExposureRiskManagementModel(0.1m);

            var targets = model.ManageRisk(algorithm, new IPortfolioTarget[] { new PortfolioTarget(Symbols.SPY, 1000) }).ToList();

            Assert.IsEmpty(targets);
        }

        [Test]
        public void AlphaStreamsPortfolioConstructionModelIsNotImplemented()
        {
            var algorithm = CreateAlgorithm();
            var model = new AlphaStreamsPortfolioConstructionModel();

            Assert.Throws<NotImplementedException>(() => model.GetAlphaWeight("alpha"));
            Assert.Throws<NotImplementedException>(() => model.OnSecuritiesChanged(algorithm, SecurityChanges.None));
            Assert.Throws<NotImplementedException>(() => model.CreateTargets(algorithm, Array.Empty<Insight>()));
        }

        private static AlgorithmStub CreateAlgorithm()
        {
            var algorithm = new AlgorithmStub();
            algorithm.SetDateTime(new DateTime(2021, 1, 4, 15, 0, 0));
            algorithm.SetCash(100000);
            return algorithm;
        }
    }
}
