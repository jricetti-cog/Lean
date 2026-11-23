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
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using QuantConnect.Services.Contracts;
using QuantConnect.Services.Messaging;
using QuantConnect.Services.Messaging.InMemory;
using QuantConnect.Services.Orchestrator;
using BusExchanges = QuantConnect.Services.Messaging.Exchanges;
using BusQueues = QuantConnect.Services.Messaging.Queues;

namespace QuantConnect.Tests.Services.LiveWorker
{
    [TestFixture]
    public class KubernetesLiveContainerRuntimeTests
    {
        private static readonly LiveDeploymentOptions Options = new()
        {
            Mode = "kubernetes", Image = "ghcr.io/x/live-worker:1.0.0", KubernetesApiUri = "https://k8s.test", DataClaim = "lean-data",
            ServiceAccount = "lean-live-worker", MemoryMb = 2048, Cpus = 1.5, PodLabels = "app.kubernetes.io/instance=lean",
        };

        private static JobSubmitted Job() => new()
        {
            JobId = Guid.Parse("8d6f6d36-1d5a-4a51-9d1e-2c1f0b8a7e10"), Kind = JobKind.Live, Attempt = 2, SubmittedAt = DateTimeOffset.UtcNow, SubmittedBy = "tests",
            Live = new LiveSpec { Algorithm = new BuiltinAlgorithmRef { TypeName = "LiveWorkerDemoAlgorithm" }, Brokerage = "PaperBrokerage" },
        };

        [Test]
        public void DockerSpecBecomesASingleReplicaDeploymentWithTheDataClaimReadOnly()
        {
            var handler = new RecordingHandler();
            var runtime = new KubernetesLiveContainerRuntime(Options, handler, () => "token", "lean");
            var deployer = new LiveDeployer(new InMemoryMessageBus(), runtime, null, Options, NullLogger<LiveDeployer>.Instance,
                k => k == "RabbitMQ__Uri" ? "amqp://u:p@rabbitmq:5672/lean" : null);
            var name = deployer.ContainerName(Job().JobId);
            var manifest = runtime.DeploymentManifest(name, deployer.ContainerSpec(Job()));

            Assert.AreEqual("Deployment", (string)manifest["kind"]);
            Assert.AreEqual("lean", (string)manifest["metadata"]!["namespace"]);
            Assert.AreEqual("2", (string)manifest["metadata"]!["labels"]![LiveDeployer.AttemptLabel]);
            Assert.AreEqual(1, (int)manifest["spec"]!["replicas"]);
            Assert.AreEqual("Recreate", (string)manifest["spec"]!["strategy"]!["type"]);
            var template = manifest["spec"]!["template"]!;
            Assert.AreEqual(name, (string)template["metadata"]!["labels"]!["lean.deployment"]);
            Assert.AreEqual("lean", (string)template["metadata"]!["labels"]!["app.kubernetes.io/instance"]);
            Assert.AreEqual("lean-live-worker", (string)template["spec"]!["serviceAccountName"]);
            Assert.AreEqual("lean-data", (string)template["spec"]!["volumes"]![0]!["persistentVolumeClaim"]!["claimName"]);
            var container = template["spec"]!["containers"]![0]!;
            Assert.AreEqual("ghcr.io/x/live-worker:1.0.0", (string)container["image"]);
            Assert.IsTrue((bool)container["volumeMounts"]![0]!["readOnly"]);
            Assert.AreEqual("/Lean/Data", (string)container["volumeMounts"]![0]!["mountPath"]);
            Assert.AreEqual((2048L * 1024 * 1024).ToString(), (string)container["resources"]!["limits"]!["memory"]);
            Assert.AreEqual("1500m", (string)container["resources"]!["limits"]!["cpu"]);
            var env = container["env"]!.AsArray().ToDictionary(e => (string)e!["name"], e => (string)e!["value"]);
            Assert.AreEqual("amqp://u:p@rabbitmq:5672/lean", env["RabbitMQ__Uri"]);
            Assert.AreEqual(name, env["LiveWorker__WorkerId"]);
            Assert.IsTrue(env.ContainsKey("LiveWorker__Dispatch"));
        }

        [Test]
        public async Task DeployerDrivesTheDeploymentsApi()
        {
            var handler = new RecordingHandler();
            var runtime = new KubernetesLiveContainerRuntime(Options, handler, () => "token", "lean");
            var deployer = new LiveDeployer(new InMemoryMessageBus(), runtime, null, Options, NullLogger<LiveDeployer>.Instance, _ => null);
            var job = Job();
            var delivery = new ReceivedMessage
            {
                Queue = BusQueues.Live, Exchange = BusExchanges.Jobs, RoutingKey = RoutingKeys.Dispatch(JobKind.Live),
                Envelope = MessageEnvelope.Create(MessageTypes.LiveRequested, job.JobId, job.Attempt, AppIds.Orchestrator, job.Kind),
                Body = ContractJson.SerializeToUtf8Bytes(job),
            };
            Assert.AreEqual(MessageDisposition.Ack, await deployer.HandleAsync(delivery, CancellationToken.None));

            var name = deployer.ContainerName(job.JobId);
            CollectionAssert.AreEqual(new[]
            {
                $"GET /apis/apps/v1/namespaces/lean/deployments/{name}",
                "POST /apis/apps/v1/namespaces/lean/deployments",
                $"PATCH /apis/apps/v1/namespaces/lean/deployments/{name}",
            }, handler.Requests.Select(r => r.Line));
            Assert.IsTrue(handler.Requests.All(r => r.Authorization == "Bearer token"));
            Assert.AreEqual("application/merge-patch+json", handler.Requests[2].ContentType);
            Assert.IsTrue(runtime.RestartsFinishedWorkers);
        }

        private sealed record Request(string Line, string Authorization, string ContentType);

        private sealed class RecordingHandler : HttpMessageHandler
        {
            public List<Request> Requests { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(new Request($"{request.Method} {request.RequestUri!.AbsolutePath}", request.Headers.Authorization?.ToString(),
                    request.Content?.Headers.ContentType?.MediaType));
                if (request.Content != null) await request.Content.ReadAsStringAsync(cancellationToken);
                return request.Method == HttpMethod.Get
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            }
        }
    }
}
