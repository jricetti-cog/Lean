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

using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using QuantConnect.Services.Orchestrator;

namespace QuantConnect.Tests.Services.LiveWorker
{
    [TestFixture]
    public class DockerLiveContainerRuntimeTests
    {
        [Test]
        public async Task TcpDockerHostIsUsedInsteadOfTheSocket()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            string path = null;
            var serve = Task.Run(async () =>
            {
                var context = await listener.GetContextAsync();
                path = context.Request.RawUrl;
                context.Response.StatusCode = 404;
                context.Response.Close();
            });

            using var runtime = new DockerLiveContainerRuntime(Options.Create(new LiveDeploymentOptions
            {
                DockerHost = $"tcp://127.0.0.1:{port}",
                DockerSocket = "/nonexistent/docker.sock",
            }));
            var container = await runtime.InspectAsync("lean-live-x", CancellationToken.None);
            await serve;

            Assert.IsNull(container);
            Assert.AreEqual("/containers/lean-live-x/json", path);
        }
    }
}
