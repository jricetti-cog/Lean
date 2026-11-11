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
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QuantConnect.Configuration;
using QuantConnect.Logging;
using QuantConnect.Packets;
using QuantConnect.Report;
using QuantConnect.Util;

namespace QuantConnect.Services.Report
{
    /// <summary>
    /// Generates LEAN reports from backtest (and optional live) result json and keeps them in a directory store
    /// </summary>
    public class ReportGenerator
    {
        private static readonly SemaphoreSlim CompileLock = new(1, 1);
        private readonly string _storageFolder;
        private readonly string _template;
        private readonly string _cssOverride;

        /// <summary>
        /// A generated report
        /// </summary>
        public record GeneratedReport(string Id, string Name, DateTime CreatedUtc, string Html, string Statistics);

        /// <summary>
        /// Creates a generator storing reports under <paramref name="storageFolder"/>
        /// </summary>
        public ReportGenerator(string storageFolder)
        {
            _storageFolder = storageFolder;
            Directory.CreateDirectory(_storageFolder);

            var templateFile = Config.Get("report-html-custom-file", Path.Combine(AppContext.BaseDirectory, "template.html"));
            _template = File.Exists(templateFile) ? File.ReadAllText(templateFile) : null;
            var cssFile = Config.Get("report-css-override-file", Path.Combine(AppContext.BaseDirectory, "css", "report_override.css"));
            _cssOverride = File.Exists(cssFile) ? File.ReadAllText(cssFile) : null;
        }

        /// <summary>
        /// Parses the request body. It is either a backtest result json (the file LEAN writes next to the
        /// launcher, or a BacktestResultPacket's "results"), or an envelope {"backtest": ..., "live": ...}.
        /// </summary>
        public static (BacktestResult Backtest, LiveResult Live) ParseResults(string json)
        {
            var token = JObject.Parse(json);
            JToken backtestToken = token;
            JToken liveToken = null;
            if (token["backtest"] is JObject || token["live"] is JObject)
            {
                backtestToken = token["backtest"];
                liveToken = token["live"];
            }
            else if (token["results"] is JObject packetResults && token["progress"] != null)
            {
                backtestToken = packetResults;
            }

            var backtestSettings = new JsonSerializerSettings
            {
                Converters = new List<JsonConverter> { new NullResultValueTypeJsonConverter<BacktestResult>() },
                FloatParseHandling = FloatParseHandling.Decimal
            };
            var liveSettings = new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                Converters = new List<JsonConverter> { new NullResultValueTypeJsonConverter<LiveResult>() }
            };

            BacktestResult backtest;
            LiveResult live;
            try
            {
                backtest = backtestToken is JObject ? JsonConvert.DeserializeObject<BacktestResult>(backtestToken.ToString(), backtestSettings) : null;
                live = liveToken is JObject ? JsonConvert.DeserializeObject<LiveResult>(liveToken.ToString(), liveSettings) : null;
            }
            catch (Exception err) when (err is NullReferenceException or JsonException or InvalidCastException)
            {
                throw new ArgumentException($"request body is not a LEAN result json: {err.Message}", err);
            }
            if (backtest == null && live == null)
            {
                throw new ArgumentException("request body must contain a backtest and/or live result");
            }
            return (backtest, live);
        }

        /// <summary>
        /// Compiles and stores a report. Compilation is serialized because the report charts run under the Python GIL.
        /// </summary>
        public GeneratedReport Generate(string json, string name, string description, string version, string id = null)
        {
            var (backtest, live) = ParseResults(json);
            id = string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : SanitizeId(id);
            name = string.IsNullOrEmpty(name) ? id : name;

            string html;
            string statistics;
            CompileLock.Wait();
            try
            {
                Log.Trace($"ReportGenerator.Generate(): compiling report {id}");
                var report = new QuantConnect.Report.Report(name, description ?? string.Empty, version ?? string.Empty, backtest, live,
                    pointInTimePortfolioDestination: string.Empty, cssOverride: _cssOverride, htmlCustom: _template);
                report.Compile(out html, out statistics);
            }
            finally
            {
                CompileLock.Release();
            }

            File.WriteAllText(HtmlPath(id), html);
            File.WriteAllText(StatisticsPath(id), statistics ?? "{}");
            File.WriteAllText(MetaPath(id), JsonConvert.SerializeObject(new { id, name, createdUtc = DateTime.UtcNow }));
            return new GeneratedReport(id, name, DateTime.UtcNow, html, statistics);
        }

        /// <summary>
        /// Lists stored reports, newest first
        /// </summary>
        public IEnumerable<JObject> List()
        {
            return Directory.EnumerateFiles(_storageFolder, "*.meta.json")
                .Select(file => JObject.Parse(File.ReadAllText(file)))
                .OrderByDescending(meta => meta.Value<DateTime>("createdUtc"));
        }

        /// <summary>
        /// Gets the stored html of a report, or null
        /// </summary>
        public string GetHtml(string id) => ReadIfExists(HtmlPath(SanitizeId(id)));

        /// <summary>
        /// Gets the stored statistics json of a report, or null
        /// </summary>
        public string GetStatistics(string id) => ReadIfExists(StatisticsPath(SanitizeId(id)));

        private static string ReadIfExists(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
        private string HtmlPath(string id) => Path.Combine(_storageFolder, id + ".html");
        private string StatisticsPath(string id) => Path.Combine(_storageFolder, id + ".statistics.json");
        private string MetaPath(string id) => Path.Combine(_storageFolder, id + ".meta.json");

        /// <summary>
        /// Restricts report ids to file-name safe characters
        /// </summary>
        public static string SanitizeId(string id)
        {
            var clean = new string((id ?? string.Empty).Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
            if (string.IsNullOrEmpty(clean))
            {
                throw new ArgumentException("invalid report id");
            }
            return clean;
        }
    }
}
