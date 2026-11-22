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
using QuantConnect.Data;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// Demo for the live-worker (PaperBrokerage on the market-data service's simulated feed): buys half the portfolio of
    /// BTCUSD once when it starts flat. A restarted deployment comes back with its restored holdings and does not buy again;
    /// after a liquidation it stays flat until it is redeployed.
    /// </summary>
    public class LiveWorkerDemoAlgorithm : QCAlgorithm
    {
        private Symbol _symbol;
        private bool _traded;

        public override void Initialize()
        {
            SetCash(100000);
            _symbol = AddCrypto(GetParameter("ticker", "BTCUSD"), Resolution.Second, Market.Coinbase).Symbol;
        }

        public override void OnWarmupFinished()
        {
            var holdings = Portfolio[_symbol].Quantity;
            _traded = holdings != 0;
            Log($"LiveWorkerDemo: started with {holdings} {_symbol.Value}, cash {Portfolio.CashBook[AccountCurrency].Amount}");
        }

        public override void OnData(Slice slice)
        {
            if (_traded || !slice.ContainsKey(_symbol))
            {
                return;
            }
            _traded = true;
            SetHoldings(_symbol, 0.5m);
            Log($"LiveWorkerDemo: bought {_symbol.Value} at {Securities[_symbol].Price}");
        }
    }
}
