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
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using QuantConnect.Configuration;
using QuantConnect.Services;
using QuantConnect.Services.Api;

namespace QuantConnect.Tests.Services
{
    [TestFixture, NonParallelizable]
    public class LeanServicesTests
    {
        [TearDown]
        public void TearDown()
        {
            Config.Reset();
            Globals.Reset();
        }

        [TestCase("LEAN_DATA_FOLDER", "data-folder")]
        [TestCase("LEAN_REPORT_SERVICE_URL", "report-service-url")]
        [TestCase("LEAN_JOB_USER_ID", "job-user-id")]
        [TestCase("LEAN_", null)]
        [TestCase("DATA_FOLDER", null)]
        [TestCase(null, null)]
        public void MapsEnvironmentVariablesToConfigKeys(string variable, string expected)
        {
            Assert.AreEqual(expected, LeanServiceConfiguration.ToConfigKey(variable));
        }

        [Test]
        public void ApplyEnvironmentSetsConfigAndGlobals()
        {
            var applied = LeanServiceConfiguration.ApplyEnvironment(new Hashtable
            {
                { "LEAN_DATA_FOLDER", "/tmp/lean-data/" },
                { "LEAN_REPORT_SERVICE_URL", "http://report:8080" },
                { "PATH", "/usr/bin" }
            });

            CollectionAssert.AreEquivalent(new[] { "data-folder", "report-service-url" }, applied);
            Assert.AreEqual("http://report:8080", Config.Get("report-service-url"));
            Assert.AreEqual("/tmp/lean-data/", Globals.DataFolder);
        }

        [Test]
        public async Task ApiServiceServesHealthAndProxiesReports()
        {
            await using var upstream = await Start(app =>
            {
                app.MapPost("/v1/reports", async (HttpContext c) =>
                {
                    var body = await c.Request.ReadBodyAsync();
                    return Results.Text("echo:" + body, "text/plain", statusCode: 201);
                });
                app.MapGet("/v1/reports/{id}", (string id) => Results.Text("report " + id));
            });
            Config.Set("report-service-url", upstream.Url);

            await using var api = await Start(null, ApiService.Build(Array.Empty<string>()));
            Config.Set("report-service-url", upstream.Url);
            using var client = new HttpClient { BaseAddress = new Uri(api.Url) };

            var health = JObject.Parse(await client.GetStringAsync("/health"));
            Assert.AreEqual("lean-api", health.Value<string>("service"));

            var services = JObject.Parse(await client.GetStringAsync("/v1/services"));
            Assert.AreEqual(upstream.Url, services.Value<string>("/v1/reports"));

            var post = await client.PostAsync("/v1/reports?name=x", new StringContent("{\"a\":1}"));
            var postBody = await post.Content.ReadAsStringAsync();
            Assert.AreEqual("echo:{\"a\":1}", postBody);
            Assert.AreEqual(HttpStatusCode.Created, post.StatusCode);
            Assert.AreEqual("report abc", await client.GetStringAsync("/v1/reports/abc"));
        }

        [Test]
        public async Task ApiServiceReturnsUnavailableWithoutUpstream()
        {
            Config.Set("report-service-url", "");
            await using var api = await Start(null, ApiService.Build(Array.Empty<string>()));
            using var client = new HttpClient { BaseAddress = new Uri(api.Url) };
            var response = await client.GetAsync("/v1/reports");
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        [Test]
        public async Task ServiceTokenProtectsVersionedRoutesOnly()
        {
            await using var service = await Start(app =>
            {
                app.UseLeanServiceDefaults("test", serviceToken: "secret");
                app.MapGet("/v1/ping", () => "pong");
            }, useDefaults: false);
            using var client = new HttpClient { BaseAddress = new Uri(service.Url) };

            Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/ping")).StatusCode);

            using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/ping");
            request.Headers.Add("Authorization", "Bearer secret");
            Assert.AreEqual("pong", await (await client.SendAsync(request)).Content.ReadAsStringAsync());
        }

        private static async Task<RunningService> Start(Action<WebApplication> configure, WebApplication app = null, bool useDefaults = true)
        {
            if (app == null)
            {
                app = LeanServiceHost.CreateBuilder("test", Array.Empty<string>()).Build();
                if (useDefaults)
                {
                    app.UseLeanServiceDefaults("test");
                }
            }
            app.Urls.Clear();
            app.Urls.Add("http://127.0.0.1:0");
            configure?.Invoke(app);
            await app.StartAsync();
            var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses.First();
            return new RunningService(app, url);
        }

        private sealed record RunningService(WebApplication App, string Url) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                await App.StopAsync();
                await App.DisposeAsync();
            }
        }
    }
}
