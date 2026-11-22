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
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.EngineHandlers;
using QuantConnect.Services.Messaging;
using BusExchanges = QuantConnect.Services.Messaging.Exchanges;
using BusQueues = QuantConnect.Services.Messaging.Queues;
using QuantConnect.Services.Messaging.InMemory;
using QuantConnect.Services.Orchestrator;

namespace QuantConnect.Tests.Services.LiveWorker
{
    [TestFixture]
    public class LiveDeployerTests
    {
        private FakeRuntime _runtime;
        private LiveDeployer _deployer;

        [SetUp]
        public void SetUp()
        {
            _runtime = new FakeRuntime();
            var options = new LiveDeploymentOptions { Mode = "docker", Network = "lean_lean", DataVolume = "lean_lean-data", ContainerPrefix = "lean-live-", MaxRestarts = 3 };
            var env = new Dictionary<string, string>
            {
                ["RabbitMQ__Uri"] = "amqp://u:p@rabbitmq:5672/lean", ["S3__ServiceUrl"] = "http://minio:9000", ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://otel-collector:4317",
            };
            _deployer = new LiveDeployer(new InMemoryMessageBus(), _runtime, null, options, NullLogger<LiveDeployer>.Instance, k => env.GetValueOrDefault(k));
        }

        private static JobSubmitted Job(int attempt = 1) => new()
        {
            JobId = Guid.Parse("8d6f6d36-1d5a-4a51-9d1e-2c1f0b8a7e10"), Kind = JobKind.Live, Attempt = attempt, SubmittedAt = DateTimeOffset.UtcNow, SubmittedBy = "tests",
            Live = new LiveSpec { Algorithm = new BuiltinAlgorithmRef { TypeName = "LiveWorkerDemoAlgorithm" }, Brokerage = "PaperBrokerage" },
        };

        private static ReceivedMessage Delivery(JobSubmitted job) => new()
        {
            Queue = BusQueues.Live, Exchange = BusExchanges.Jobs, RoutingKey = RoutingKeys.Dispatch(JobKind.Live),
            Envelope = MessageEnvelope.Create(MessageTypes.LiveRequested, job.JobId, job.Attempt, AppIds.Orchestrator, job.Kind),
            Body = ContractJson.SerializeToUtf8Bytes(job),
        };

        [Test]
        public async Task EachDeploymentGetsItsOwnContainer()
        {
            var job = Job();
            Assert.AreEqual(MessageDisposition.Ack, await _deployer.HandleAsync(Delivery(job), CancellationToken.None));

            var (name, spec) = _runtime.Created.Single();
            Assert.AreEqual($"lean-live-{job.JobId:D}", name);
            CollectionAssert.AreEqual(new[] { name }, _runtime.Started);
            var env = spec["Env"]!.AsArray().Select(e => (string)e).ToList();
            CollectionAssert.Contains(env, "RabbitMQ__Uri=amqp://u:p@rabbitmq:5672/lean");
            CollectionAssert.Contains(env, $"LiveWorker__WorkerId={name}");
            CollectionAssert.Contains(env, "OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317");
            Assert.AreEqual(job.JobId, LiveWorker_Dispatch(env).JobId);
            Assert.AreEqual("lean_lean", (string)spec["HostConfig"]!["NetworkMode"]);
            Assert.AreEqual("lean_lean-data:/Lean/Data:ro", (string)spec["HostConfig"]!["Binds"]![0]);
            Assert.AreEqual("on-failure", (string)spec["HostConfig"]!["RestartPolicy"]!["Name"]);
            Assert.AreEqual(3, (int)spec["HostConfig"]!["RestartPolicy"]!["MaximumRetryCount"]);
            Assert.AreEqual("1", (string)spec["Labels"]![LiveDeployer.AttemptLabel]);
        }

        private static JobSubmitted LiveWorker_Dispatch(List<string> env) =>
            QuantConnect.Services.LiveWorker.LiveJobRunner.ParseDispatch(env.Single(e => e.StartsWith("LiveWorker__Dispatch=")).Substring("LiveWorker__Dispatch=".Length));

        [Test]
        public async Task RedeliveryOfTheSameAttemptReusesTheContainerAndANewAttemptReplacesIt()
        {
            await _deployer.HandleAsync(Delivery(Job()), CancellationToken.None);
            _runtime.Containers.Values.Single().Running = false;
            await _deployer.HandleAsync(Delivery(Job()), CancellationToken.None);
            Assert.AreEqual(1, _runtime.Created.Count);
            Assert.AreEqual(2, _runtime.Started.Count);

            await _deployer.HandleAsync(Delivery(Job(attempt: 2)), CancellationToken.None);
            Assert.AreEqual(2, _runtime.Created.Count);
            Assert.AreEqual(1, _runtime.Removed.Count);
            Assert.AreEqual("2", _runtime.Containers.Values.Single().Labels[LiveDeployer.AttemptLabel]);
        }

        [Test]
        public async Task SweepRemovesOnlyCleanlyExitedWorkers()
        {
            _runtime.Containers["a"] = new FakeContainer("a", false, 0);
            _runtime.Containers["b"] = new FakeContainer("b", false, 1);
            _runtime.Containers["c"] = new FakeContainer("c", true, 0);
            Assert.AreEqual(1, await _deployer.SweepAsync(CancellationToken.None));
            CollectionAssert.AreEquivalent(new[] { "b", "c" }, _runtime.Containers.Keys);
        }

        [Test]
        public async Task NonLiveDeliveriesAreDeadLettered()
        {
            var job = Job() with { Kind = JobKind.Backtest };
            var delivery = Delivery(job);
            Assert.AreEqual(MessageDisposition.DeadLetter, await _deployer.HandleAsync(delivery with { Envelope = delivery.Envelope with { Type = MessageTypes.BacktestRequested } }, CancellationToken.None));
            CollectionAssert.IsEmpty(_runtime.Created);
        }

        [Test]
        public async Task BusDataQueueHandlerFeedsTheAggregatorFromTheLiveFanout()
        {
            var bus = new InMemoryMessageBus();
            var aggregator = new FakeAggregator();
            using var handler = new BusDataQueueHandler(aggregator, () => bus);
            var symbol = Symbol.Create("BTCUSD", SecurityType.Crypto, Market.Coinbase);
            var config = new SubscriptionDataConfig(typeof(TradeBar), symbol, Resolution.Second, TimeZones.Utc, TimeZones.Utc, false, false, false);
            Assert.IsNotNull(handler.Subscribe(config, (_, _) => { }));

            var now = DateTimeOffset.UtcNow;
            var batch = new LiveDataBatch
            {
                FeedId = Guid.NewGuid(), Sequence = 1,
                Symbol = new LiveDataSymbol { SecurityType = ContractSecurityType.Crypto, Market = Market.Coinbase, Ticker = "BTCUSD", Id = symbol.ID.ToString() },
                Points = new List<LiveDataPoint>
                {
                    new() { DataType = LiveDataType.Tick, TickType = ContractTickType.Trade, Time = now, Value = 50000m, Volume = 2m },
                    new() { DataType = LiveDataType.Tick, TickType = ContractTickType.Quote, Time = now, Value = 50001m, BidPrice = 50000m, AskPrice = 50002m, BidSize = 1m, AskSize = 3m },
                },
            };
            await bus.PublishAsync(OutgoingMessage.Create(BusExchanges.MarketDataLive, RoutingKeys.MarketData("Crypto", Market.Coinbase, "BTCUSD"),
                MessageEnvelope.Create(MessageTypes.LiveData, Guid.Empty, 0, AppIds.MarketData, JobKind.Live), batch));
            Assert.AreEqual(1, await bus.DeliverAllAsync());

            for (var i = 0; i < 500 && aggregator.Updates.Count < 2; i++) await Task.Delay(10);
            Assert.AreEqual(2, handler.Received);
            var trade = (Tick)aggregator.Updates[0];
            Assert.AreEqual(TickType.Trade, trade.TickType);
            Assert.AreEqual(50000m, trade.Value);
            Assert.AreEqual(2m, trade.Quantity);
            var quote = (Tick)aggregator.Updates[1];
            Assert.AreEqual(TickType.Quote, quote.TickType);
            Assert.AreEqual(50000m, quote.BidPrice);
            Assert.AreEqual(50002m, quote.AskPrice);
            Assert.AreEqual(3m, quote.AskSize);

            handler.Unsubscribe(config);
            Assert.AreEqual(1, aggregator.Removed);
            Assert.IsNull(handler.Subscribe(new SubscriptionDataConfig(config, symbol: Symbol.CreateCanonicalOption(Symbols.SPY)), (_, _) => { }));
        }

        private sealed class FakeAggregator : IDataAggregator
        {
            public List<BaseData> Updates { get; } = new();
            public int Removed { get; private set; }
            public void Initialize(DataAggregatorInitializeParameters parameters) { }
            public IEnumerator<BaseData> Add(SubscriptionDataConfig dataConfig, EventHandler newDataAvailableHandler) => Enumerable.Empty<BaseData>().GetEnumerator();
            public bool Remove(SubscriptionDataConfig dataConfig) { Removed++; return true; }
            public void Update(BaseData input) { lock (Updates) Updates.Add(input); }
            public void Dispose() { }
        }

        private sealed class FakeContainer(string name, bool running, int exitCode)
        {
            public string Name { get; } = name;
            public bool Running { get; set; } = running;
            public int ExitCode { get; } = exitCode;
            public Dictionary<string, string> Labels { get; init; } = new() { [LiveDeployer.RoleLabel] = LiveDeployer.Role };
        }

        private sealed class FakeRuntime : ILiveContainerRuntime
        {
            public Dictionary<string, FakeContainer> Containers { get; } = new();
            public List<(string, JsonObject)> Created { get; } = new();
            public List<string> Started { get; } = new();
            public List<string> Removed { get; } = new();

            private static LiveContainer View(FakeContainer c) => new(c.Name, c.Name, c.Running, c.ExitCode, c.Labels);

            public Task<LiveContainer> InspectAsync(string name, CancellationToken ct) => Task.FromResult(Containers.TryGetValue(name, out var c) ? View(c) : null);

            public Task<string> CreateAsync(string name, JsonObject spec, CancellationToken ct)
            {
                Created.Add((name, spec));
                Containers[name] = new FakeContainer(name, false, 0) { Labels = spec["Labels"]!.AsObject().ToDictionary(p => p.Key, p => (string)p.Value) };
                return Task.FromResult(name);
            }

            public Task StartAsync(string id, CancellationToken ct)
            {
                Started.Add(id);
                Containers[id].Running = true;
                return Task.CompletedTask;
            }

            public Task RemoveAsync(string id, CancellationToken ct)
            {
                Removed.Add(id);
                Containers.Remove(id);
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<LiveContainer>> ListAsync(string label, CancellationToken ct) =>
                Task.FromResult<IReadOnlyList<LiveContainer>>(Containers.Values.Select(View).ToList());
        }
    }
}
