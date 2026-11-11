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
using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;
using QuantConnect.Api;
using QuantConnect.Configuration;

namespace QuantConnect.Services.Api
{
    /// <summary>
    /// Standalone REST service for the LEAN API layer. It exposes the <see cref="QuantConnect.Api.Api"/>
    /// client (projects, compiles, backtests, live algorithms, data and object store) under /v1/cloud,
    /// and fronts the internal LEAN services (reports, and in later phases results, jobs and data)
    /// so clients and the dashboard only talk to one origin.
    /// </summary>
    public static class ApiService
    {
        /// <summary>
        /// Name reported by /health
        /// </summary>
        public const string Name = "lean-api";

        /// <summary>
        /// Internal services fronted by this service: route prefix and the config key holding the upstream url
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> Upstreams = new Dictionary<string, string>
        {
            { "/v1/reports", "report-service-url" }
        };

        /// <summary>
        /// Builds the web application
        /// </summary>
        /// <param name="args">Command line arguments</param>
        /// <param name="api">Optional pre-initialized api client, used by tests</param>
        public static WebApplication Build(string[] args, QuantConnect.Api.Api api = null)
        {
            var builder = LeanServiceHost.CreateBuilder(Name, args);
            var app = builder.Build();
            app.UseLeanServiceDefaults(Name);
            app.UseDefaultFiles();
            app.UseStaticFiles();

            var cloud = new Lazy<QuantConnect.Api.Api>(() => api ?? CreateApi());
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            foreach (var upstream in Upstreams)
            {
                app.MapProxy(upstream.Key, () => Config.Get(upstream.Value), http);
            }

            app.MapGet("/v1/services", () => LeanServiceHost.Json(Upstreams.ToDictionary(x => x.Key, x => Config.Get(x.Value))));
            MapCloudRoutes(app, cloud);
            return app;
        }

        /// <summary>
        /// Creates the api client from the job-user-id / api-access-token configuration (LEAN_JOB_USER_ID / LEAN_API_ACCESS_TOKEN)
        /// </summary>
        public static QuantConnect.Api.Api CreateApi()
        {
            var api = new QuantConnect.Api.Api();
            api.Initialize(Globals.UserId, Globals.UserToken, Globals.DataFolder);
            return api;
        }

        private static string Organization(HttpContext context)
        {
            var organization = context.Request.Query["organizationId"].ToString();
            return string.IsNullOrEmpty(organization) ? Globals.OrganizationID : organization;
        }

        private static IResult Respond(RestResponse response)
        {
            if (response == null)
            {
                return LeanServiceHost.Json(new { success = false, errors = new[] { "empty response from the QuantConnect API" } }, StatusCodes.Status502BadGateway);
            }
            return LeanServiceHost.Json(response, response.Success ? StatusCodes.Status200OK : StatusCodes.Status502BadGateway);
        }

        private static void MapCloudRoutes(WebApplication app, Lazy<QuantConnect.Api.Api> cloud)
        {
            var group = app.MapGroup("/v1/cloud");

            group.MapGet("/authenticated", () => LeanServiceHost.Json(new { authenticated = cloud.Value.Connected }));
            group.MapGet("/account", (HttpContext c) => Respond(cloud.Value.ReadAccount(Organization(c))));
            group.MapGet("/organization", (HttpContext c) =>
            {
                var organization = cloud.Value.ReadOrganization(Organization(c));
                return organization == null
                    ? LeanServiceHost.Json(new { success = false, errors = new[] { "organization not found" } }, StatusCodes.Status502BadGateway)
                    : LeanServiceHost.Json(organization);
            });
            group.MapGet("/lean-versions", () => Respond(cloud.Value.ReadLeanVersions()));

            group.MapGet("/projects", () => Respond(cloud.Value.ListProjects()));
            group.MapPost("/projects", async (HttpContext c) =>
            {
                var body = JObject.Parse(await c.Request.ReadBodyAsync().ConfigureAwait(false));
                var language = Enum.Parse<Language>(body.Value<string>("language") ?? "Py", ignoreCase: true);
                return Respond(cloud.Value.CreateProject(body.Value<string>("name"), language, body.Value<string>("organizationId") ?? Organization(c)));
            });
            group.MapGet("/projects/{projectId:int}", (int projectId) => Respond(cloud.Value.ReadProject(projectId)));
            group.MapDelete("/projects/{projectId:int}", (int projectId) => Respond(cloud.Value.DeleteProject(projectId)));
            group.MapGet("/projects/{projectId:int}/files", (int projectId) => Respond(cloud.Value.ReadProjectFiles(projectId)));
            group.MapPost("/projects/{projectId:int}/files", async (int projectId, HttpContext c) =>
            {
                var body = JObject.Parse(await c.Request.ReadBodyAsync().ConfigureAwait(false));
                return Respond(cloud.Value.AddProjectFile(projectId, body.Value<string>("name"), body.Value<string>("content")));
            });

            group.MapPost("/projects/{projectId:int}/compiles", (int projectId) => Respond(cloud.Value.CreateCompile(projectId)));
            group.MapGet("/projects/{projectId:int}/compiles/{compileId}", (int projectId, string compileId) => Respond(cloud.Value.ReadCompile(projectId, compileId)));

            group.MapGet("/projects/{projectId:int}/backtests", (int projectId) => Respond(cloud.Value.ListBacktests(projectId)));
            group.MapPost("/projects/{projectId:int}/backtests", async (int projectId, HttpContext c) =>
            {
                var body = JObject.Parse(await c.Request.ReadBodyAsync().ConfigureAwait(false));
                var parameters = body["parameters"]?.ToObject<Dictionary<string, string>>();
                return Respond(cloud.Value.CreateBacktest(projectId, body.Value<string>("compileId"), body.Value<string>("name") ?? "lean-api", parameters));
            });
            group.MapGet("/projects/{projectId:int}/backtests/{backtestId}", (int projectId, string backtestId) => Respond(cloud.Value.ReadBacktest(projectId, backtestId)));
            group.MapDelete("/projects/{projectId:int}/backtests/{backtestId}", (int projectId, string backtestId) => Respond(cloud.Value.DeleteBacktest(projectId, backtestId)));
            group.MapGet("/projects/{projectId:int}/backtests/{backtestId}/orders", (int projectId, string backtestId, int? start, int? end) =>
                Respond(cloud.Value.ReadBacktestOrders(projectId, backtestId, start ?? 0, end ?? 100)));
            group.MapGet("/projects/{projectId:int}/backtests/{backtestId}/report", (int projectId, string backtestId) => Respond(cloud.Value.ReadBacktestReport(projectId, backtestId)));

            group.MapGet("/live", () => Respond(cloud.Value.ListLiveAlgorithms()));
            group.MapGet("/projects/{projectId:int}/live", (int projectId) => Respond(cloud.Value.ReadLiveAlgorithm(projectId)));
            group.MapGet("/projects/{projectId:int}/live/portfolio", (int projectId) => Respond(cloud.Value.ReadLivePortfolio(projectId)));
            group.MapPost("/projects/{projectId:int}/live/stop", (int projectId) => Respond(cloud.Value.StopLiveAlgorithm(projectId)));
            group.MapPost("/projects/{projectId:int}/live/liquidate", (int projectId) => Respond(cloud.Value.LiquidateLiveAlgorithm(projectId)));
            group.MapPost("/projects/{projectId:int}/live/commands", async (int projectId, HttpContext c) =>
                Respond(cloud.Value.CreateLiveCommand(projectId, JObject.Parse(await c.Request.ReadBodyAsync().ConfigureAwait(false)))));

            group.MapGet("/data/prices", (HttpContext c) => Respond(cloud.Value.ReadDataPrices(Organization(c))));
            group.MapGet("/object-store", (HttpContext c, string path) => Respond(cloud.Value.ListObjectStore(Organization(c), path ?? "/")));
        }
    }

    /// <summary>
    /// Entry point
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// Runs the API service
        /// </summary>
        public static void Main(string[] args)
        {
            ApiService.Build(args).Run();
        }
    }
}
