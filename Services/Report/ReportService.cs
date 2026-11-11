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
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;
using QuantConnect.Configuration;
using QuantConnect.Lean.Engine;
using QuantConnect.Python;
using QuantConnect.Util;

namespace QuantConnect.Services.Report
{
    /// <summary>
    /// Report generation service: POST a backtest result json, get the LEAN HTML report back
    /// </summary>
    public static class ReportService
    {
        /// <summary>
        /// Name reported by /health
        /// </summary>
        public const string Name = "lean-report";

        /// <summary>
        /// Builds the web application
        /// </summary>
        public static WebApplication Build(string[] args)
        {
            var builder = LeanServiceHost.CreateBuilder(Name, args);

            // same initialization the Report console app performs
            LeanEngineAlgorithmHandlers.FromConfiguration(Composer.Instance);
            PythonInitializer.ActivatePythonVirtualEnvironment(Config.Get("python-venv"));
            PythonInitializer.Initialize();

            var generator = new ReportGenerator(Config.Get("report-storage-folder", Path.Combine(Path.GetTempPath(), "lean-reports")));
            var app = builder.Build();
            app.UseLeanServiceDefaults(Name);

            app.MapPost("/v1/reports", async (HttpContext context, string name, string description, string version, string id, string format) =>
            {
                var body = await context.Request.ReadBodyAsync().ConfigureAwait(false);
                ReportGenerator.GeneratedReport report;
                try
                {
                    report = generator.Generate(body, name, description, version, id);
                }
                catch (Exception err) when (err is ArgumentException or Newtonsoft.Json.JsonException)
                {
                    return LeanServiceHost.Json(new { error = err.Message }, StatusCodes.Status400BadRequest);
                }

                if (string.Equals(format, "html", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Content(report.Html, "text/html");
                }
                return LeanServiceHost.Json(new
                {
                    id = report.Id,
                    name = report.Name,
                    url = $"/v1/reports/{report.Id}",
                    statistics = string.IsNullOrEmpty(report.Statistics) ? null : JToken.Parse(report.Statistics)
                }, StatusCodes.Status201Created);
            });

            app.MapGet("/v1/reports", () => LeanServiceHost.Json(generator.List()));
            app.MapGet("/v1/reports/{id}", (string id) =>
            {
                var html = generator.GetHtml(id);
                return html == null ? Results.NotFound() : Results.Content(html, "text/html");
            });
            app.MapGet("/v1/reports/{id}/statistics", (string id) =>
            {
                var statistics = generator.GetStatistics(id);
                return statistics == null ? Results.NotFound() : Results.Content(statistics, "application/json");
            });
            return app;
        }
    }

    /// <summary>
    /// Entry point
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// Runs the report service
        /// </summary>
        public static void Main(string[] args)
        {
            ReportService.Build(args).Run();
        }
    }
}
