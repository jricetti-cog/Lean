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
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using OpenTelemetry.Trace;
using QuantConnect.Configuration;
using QuantConnect.Services.Contracts.Http;
using QuantConnect.Services.Defaults;

namespace QuantConnect.Tests.Services.Defaults
{
    [TestFixture, Category("Services"), NonParallelizable]
    public class LeanServiceDefaultsTests
    {
        private static async Task<(WebApplication App, HttpClient Client)> StartAsync(Action<WebApplicationBuilder> configure = null, Action<LeanServiceDefaultsOptions> options = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            configure?.Invoke(builder);
            builder.AddLeanServiceDefaults(options);
            var app = builder.Build();
            app.MapGet("/api/v1/ping", () => "pong");
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return (app, new HttpClient { BaseAddress = new Uri(address) });
        }

        [Test]
        public async Task HealthzAndReadyzReturnHealthContract()
        {
            var (app, client) = await StartAsync();
            await using var _ = app;
            using var __ = client;

            foreach (var path in new[] { LeanServiceDefaults.HealthPath, LeanServiceDefaults.ReadyPath })
            {
                var response = await client.GetAsync(path);
                var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, path);
                Assert.AreEqual("application/json", response.Content.Headers.ContentType!.MediaType);
                Assert.AreEqual("ok", body!["status"]!.GetValue<string>(), path);
                ContractSchemas.AssertValid(ContractSchemas.ValidateComponent("common.yaml", "Health", body), path);
            }
        }

        [Test]
        public async Task ReadyzReportsDownWhenAReadinessCheckFailsButHealthzStaysUp()
        {
            var (app, client) = await StartAsync(b => b.Services.AddHealthChecks()
                .AddCheck("postgres", () => HealthCheckResult.Unhealthy(), tags: new[] { HealthTags.Ready }));
            await using var _ = app;
            using var __ = client;

            var ready = await client.GetAsync(LeanServiceDefaults.ReadyPath);
            var body = JsonNode.Parse(await ready.Content.ReadAsStringAsync());
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            Assert.AreEqual("down", body!["status"]!.GetValue<string>());
            Assert.AreEqual("down", body["checks"]!["postgres"]!.GetValue<string>());
            Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync(LeanServiceDefaults.HealthPath)).StatusCode);
        }

        [Test]
        public async Task ReadinessGoesDownWhenShutdownStarts()
        {
            var (app, client) = await StartAsync();
            await using var _ = app;
            using var __ = client;

            var shutdown = app.Services.GetServices<IHostedService>().OfType<IHostedLifecycleService>().Single(s => s.GetType().Name == "GracefulShutdownService");
            await shutdown.StoppingAsync(CancellationToken.None);

            Assert.IsTrue(app.Services.GetRequiredService<ReadinessGate>().IsDraining);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, (await client.GetAsync(LeanServiceDefaults.ReadyPath)).StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync(LeanServiceDefaults.HealthPath)).StatusCode);
        }

        [Test]
        public async Task ShutdownTimeoutAndLoggingComeFromConfiguration()
        {
            var (app, client) = await StartAsync(b => b.Configuration["Service:ShutdownTimeoutSeconds"] = "7");
            await using var _ = app;
            using var __ = client;

            Assert.AreEqual(TimeSpan.FromSeconds(7), app.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout);
            Assert.AreEqual(ConsoleFormatterNames.Json, app.Services.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue.FormatterName);
        }

        [Test]
        public async Task RequestsAreTracedButProbesAreNot()
        {
            var exported = new List<Activity>();
            var (app, client) = await StartAsync(options: o => o.ConfigureTracing = t => t.AddInMemoryExporter(exported));
            await using (app)
            using (client)
            {
                Assert.IsNotNull(app.Services.GetService<TracerProvider>());
                Assert.IsNotNull(app.Services.GetService<OpenTelemetry.Metrics.MeterProvider>());
                await client.GetStringAsync("/api/v1/ping");
                await client.GetStringAsync(LeanServiceDefaults.HealthPath);
                using var source = new ActivitySource(LeanServiceDefaults.TelemetryPrefix + ".Tests");
                source.StartActivity("custom")?.Dispose();
                app.Services.GetRequiredService<TracerProvider>().ForceFlush();
            }

            var names = exported.Select(a => a.DisplayName).ToList();
            CollectionAssert.Contains(names, "GET /api/v1/ping");
            CollectionAssert.Contains(names, "custom");
            Assert.IsFalse(exported.Any(a => a.DisplayName.Contains("healthz", StringComparison.Ordinal)), string.Join(", ", names));
        }

        [Test]
        public void LeanEnvironmentVariablesMapOntoConfig()
        {
            Assert.AreEqual("data-folder", LeanConfigEnvironment.ToConfigKey("LEAN_DATA_FOLDER"));
            Assert.AreEqual("job-queue-handler", LeanConfigEnvironment.ToConfigKey("LEAN_JOB_QUEUE_HANDLER"));
            Assert.IsNull(LeanConfigEnvironment.ToConfigKey("DATA_FOLDER"));
            Assert.IsNull(LeanConfigEnvironment.ToConfigKey("LEAN_"));

            var dataFolder = Config.Get("data-folder");
            try
            {
                var applied = LeanConfigEnvironment.Apply(new Hashtable
                {
                    ["LEAN_DATA_FOLDER"] = "/Lean/Data/",
                    ["LEAN_SERVICES_TEST_FLAG"] = "true",
                    ["LEAN_SERVICES_TEST_LIST"] = "[\"a\",\"b\"]",
                    ["PATH"] = "/usr/bin",
                });

                CollectionAssert.AreEqual(new[] { "data-folder", "services-test-flag", "services-test-list" }, applied);
                Assert.AreEqual("/Lean/Data/", Config.Get("data-folder"));
                Assert.IsTrue(Config.GetBool("services-test-flag"));
                CollectionAssert.AreEqual(new[] { "a", "b" }, Config.GetValue<string[]>("services-test-list"));
            }
            finally
            {
                Config.Set("data-folder", dataFolder);
                Config.Reset();
            }
        }
    }
}
