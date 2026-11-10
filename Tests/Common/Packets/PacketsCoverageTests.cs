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
using Newtonsoft.Json;
using NUnit.Framework;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Packets;
using QuantConnect.Securities;

namespace QuantConnect.Tests.Common.Packets
{
    [TestFixture]
    public class PacketsCoverageTests
    {
        [Test]
        public void SimpleMessagePacketsAssignConstructorArguments()
        {
            var name = new AlgorithmNameUpdatePacket("algo", "name");
            Assert.AreEqual(PacketType.AlgorithmNameUpdate, name.Type);
            Assert.AreEqual("algo", name.AlgorithmId);
            Assert.AreEqual("name", name.Name);
            Assert.AreEqual(PacketType.AlgorithmNameUpdate, new AlgorithmNameUpdatePacket().Type);

            var tags = new AlgorithmTagsUpdatePacket("algo", new HashSet<string> { "a", "b" });
            Assert.AreEqual(PacketType.AlgorithmTagsUpdate, tags.Type);
            Assert.AreEqual("algo", tags.AlgorithmId);
            CollectionAssert.AreEquivalent(new[] { "a", "b" }, tags.Tags);
            Assert.IsEmpty(new AlgorithmTagsUpdatePacket().Tags);

            var log = new LogPacket("algo", "message");
            Assert.AreEqual(PacketType.Log, log.Type);
            Assert.AreEqual("algo", log.AlgorithmId);
            Assert.AreEqual("message", log.Message);
            Assert.AreEqual(PacketType.Log, new LogPacket().Type);

            var debug = new DebugPacket(5, "algo", "compile", "msg", true);
            Assert.AreEqual(PacketType.Debug, debug.Type);
            Assert.AreEqual(5, debug.ProjectId);
            Assert.AreEqual("algo", debug.AlgorithmId);
            Assert.AreEqual("compile", debug.CompileId);
            Assert.AreEqual("msg", debug.Message);
            Assert.IsTrue(debug.Toast);

            var systemDebug = new SystemDebugPacket(6, "algo2", "compile2", "msg2");
            Assert.AreEqual(PacketType.SystemDebug, systemDebug.Type);
            Assert.AreEqual(6, systemDebug.ProjectId);
            Assert.AreEqual("msg2", systemDebug.Message);
            Assert.IsFalse(systemDebug.Toast);
            Assert.AreEqual(PacketType.SystemDebug, new SystemDebugPacket().Type);

            var handled = new HandledErrorPacket("algo", "err", "stack");
            Assert.AreEqual(PacketType.HandledError, handled.Type);
            Assert.AreEqual("err", handled.Message);
            Assert.AreEqual("stack", handled.StackTrace);
            Assert.AreEqual(PacketType.HandledError, new HandledErrorPacket().Type);

            var runtime = new RuntimeErrorPacket(7, "algo", "err", "stack");
            Assert.AreEqual(PacketType.RuntimeError, runtime.Type);
            Assert.AreEqual(7, runtime.UserId);
            Assert.AreEqual("algo", runtime.AlgorithmId);
            Assert.AreEqual("err", runtime.Message);
            Assert.AreEqual("stack", runtime.StackTrace);
            Assert.AreEqual(PacketType.RuntimeError, new RuntimeErrorPacket().Type);
        }

        [Test]
        public void OrderEventAndStatusPackets()
        {
            var orderEvent = new OrderEvent(1, Symbols.SPY, new DateTime(2020, 1, 1), OrderStatus.Filled, OrderDirection.Buy, 10m, 1m, OrderFee.Zero);
            var packet = new OrderEventPacket("algo", orderEvent);
            Assert.AreEqual(PacketType.OrderEvent, packet.Type);
            Assert.AreEqual("algo", packet.AlgorithmId);
            Assert.AreSame(orderEvent, packet.Event);
            Assert.AreEqual(PacketType.OrderEvent, new OrderEventPacket().Type);

            var status = new AlgorithmStatusPacket("algo", 3, AlgorithmStatus.Stopped, "done");
            Assert.AreEqual(PacketType.AlgorithmStatus, status.Type);
            Assert.AreEqual("algo", status.AlgorithmId);
            Assert.AreEqual(3, status.ProjectId);
            Assert.AreEqual(AlgorithmStatus.Stopped, status.Status);
            Assert.AreEqual("done", status.Message);
            Assert.AreEqual(PacketType.AlgorithmStatus, new AlgorithmStatusPacket().Type);
        }

        [Test]
        public void HistoryPacketAndResults()
        {
            var packet = new HistoryPacket { QueueName = "queue" };
            Assert.AreEqual(PacketType.History, packet.Type);
            Assert.IsEmpty(packet.Requests);
            packet.Requests.Add(new HistoryRequest { Symbol = Symbols.SPY, Resolution = Resolution.Daily, TickType = TickType.Trade });
            Assert.AreEqual(Symbols.SPY, packet.Requests[0].Symbol);

            var bytes = new byte[] { 1, 2 };
            var file = new FileHistoryResult("path", bytes);
            Assert.AreEqual(HistoryResultType.File, file.Type);
            Assert.AreEqual("path", file.Filepath);
            Assert.AreSame(bytes, file.File);
            Assert.AreEqual(HistoryResultType.Completed, new CompletedHistoryResult().Type);
            var error = new ErrorHistoryResult("bad");
            Assert.AreEqual(HistoryResultType.Error, error.Type);
            Assert.AreEqual("bad", error.Message);
            var status = new StatusHistoryResult(42);
            Assert.AreEqual(HistoryResultType.Status, status.Type);
            Assert.AreEqual(42, status.Progress);
        }

        [TestCase(9.5, 16, 9.5, 16)]
        [TestCase(0, 24, 0, 24)]
        public void MarketHoursHandlesEndOfDay(double start, double end, double expectedStart, double expectedEnd)
        {
            var reference = new DateTime(2020, 1, 2, 13, 0, 0);
            var hours = new MarketHours(reference, start, end);
            Assert.AreEqual(reference.Date.AddHours(expectedStart), hours.Start);
            var expected = reference.Date.AddHours(expectedEnd);
            if (end == 24)
            {
                expected = expected.AddTicks(-1);
                Assert.AreEqual(reference.Date, hours.End.Date);
            }
            Assert.AreEqual(expected, hours.End);

            var today = new MarketToday { Open = hours };
            Assert.AreEqual(string.Empty, today.Status);
            Assert.AreSame(hours, today.Open);
        }

        [Test]
        public void SecurityTypesPacketCsv()
        {
            var packet = new SecurityTypesPacket();
            Assert.AreEqual(PacketType.SecurityTypes, packet.Type);
            Assert.AreEqual(string.Empty, packet.TypesCSV);
            packet.Types.Add(SecurityType.Equity);
            packet.Types.Add(SecurityType.Forex);
            Assert.AreEqual("equity,forex", packet.TypesCSV);
        }

        [Test]
        public void NodePacketTypes()
        {
            var research = new ResearchNodePacket { ResearchId = "rid", ResearchToken = "token" };
            Assert.AreEqual(PacketType.ResearchNode, research.Type);
            Assert.AreEqual("rid", research.ResearchId);
            Assert.AreEqual("token", research.ResearchToken);

            var alpha = new AlphaNodePacket { AlphaId = "alpha" };
            Assert.AreEqual(PacketType.AlphaNode, alpha.Type);
            Assert.AreEqual("alpha", alpha.AlphaId);
        }

        [Test]
        public void LeakyBucketControlParametersConstructors()
        {
            var defaults = new LeakyBucketControlParameters();
            Assert.AreEqual(LeakyBucketControlParameters.DefaultCapacity, defaults.Capacity);
            Assert.AreEqual(LeakyBucketControlParameters.DefaultRefillAmount, defaults.RefillAmount);
            Assert.AreEqual(LeakyBucketControlParameters.DefaultTimeInterval, defaults.TimeIntervalMinutes);

            var custom = new LeakyBucketControlParameters(10, 2, 30);
            Assert.AreEqual(10, custom.Capacity);
            Assert.AreEqual(2, custom.RefillAmount);
            Assert.AreEqual(30, custom.TimeIntervalMinutes);
        }

        [Test]
        public void LiveResultPacketCreateEmptyAndJsonRoundTrip()
        {
            var job = new LiveNodePacket { DeployId = "deploy", UserId = 1, ProjectId = 2, Channel = "channel" };
            var packet = LiveResultPacket.CreateEmpty(job);

            Assert.AreEqual(PacketType.LiveResult, packet.Type);
            Assert.AreEqual("deploy", packet.DeployId);
            Assert.AreEqual(1, packet.UserId);
            Assert.AreEqual(2, packet.ProjectId);
            Assert.AreEqual("channel", packet.Channel);
            Assert.AreEqual(Currencies.USD, packet.Results.AccountCurrency);
            Assert.AreEqual("$", packet.Results.AccountCurrencySymbol);
            Assert.IsTrue(packet.Results.Cash.ContainsKey(Currencies.USD));

            var deserialized = new LiveResultPacket(JsonConvert.SerializeObject(packet));
            Assert.AreEqual("deploy", deserialized.DeployId);
            Assert.AreEqual(1, deserialized.UserId);
            Assert.AreEqual(2, deserialized.ProjectId);

            var invalid = new LiveResultPacket("not json");
            Assert.AreEqual(string.Empty, invalid.DeployId);

            packet.Results.CashBook = null;
            Assert.IsNull(packet.Results.Cash);
            Assert.IsNull(packet.Results.AccountCurrency);
            Assert.IsNull(packet.Results.AccountCurrencySymbol);
        }

        [Test]
        public void BacktestResultPacketCreateEmptyAndJsonRoundTrip()
        {
            var job = new BacktestNodePacket
            {
                BacktestId = "bt",
                ProjectId = 2,
                UserId = 3,
                CompileId = "compile",
                Name = "name",
                Channel = "channel",
                OptimizationId = "opt",
                SessionId = "session"
            };
            var packet = BacktestResultPacket.CreateEmpty(job);

            Assert.AreEqual(PacketType.BacktestResult, packet.Type);
            Assert.AreEqual("bt", packet.BacktestId);
            Assert.AreEqual(2, packet.ProjectId);
            Assert.AreEqual(3, packet.UserId);
            Assert.AreEqual("compile", packet.CompileId);
            Assert.AreEqual("name", packet.Name);
            Assert.AreEqual("opt", packet.OptimizationId);
            Assert.AreEqual("session", packet.SessionId);
            Assert.IsNotNull(packet.Results);

            var deserialized = new BacktestResultPacket(JsonConvert.SerializeObject(packet));
            Assert.AreEqual("bt", deserialized.BacktestId);
            Assert.AreEqual("compile", deserialized.CompileId);
            Assert.AreEqual("opt", deserialized.OptimizationId);
            Assert.AreEqual(packet.Progress, deserialized.Progress);

            var invalid = new BacktestResultPacket("not json");
            Assert.AreEqual(string.Empty, invalid.BacktestId);
        }
    }
}
