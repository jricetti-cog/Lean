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
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using QuantConnect.Util.Json;

namespace QuantConnect.Api
{
    /// <summary>
    /// Custom JsonConverter for LiveResults data for live algorithms
    /// </summary>
    public class LiveAlgorithmResultsJsonConverter : JsonConverter<LiveAlgorithmResults>
    {
        /// <summary>
        /// Determines whether this instance can convert the specified object type.
        /// </summary>
        public override bool CanConvert(Type objectType)
        {
            return typeof(LiveAlgorithmResults).IsAssignableFrom(objectType);
        }

        /// <summary>
        /// Writes the results using their default serialization
        /// </summary>
        public override void Write(Utf8JsonWriter writer, LiveAlgorithmResults value, JsonSerializerOptions options)
        {
            writer.WriteDefault(value, options);
        }

        /// <summary>
        /// Reads the JSON representation of the object.
        /// </summary>
        public override LiveAlgorithmResults Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var jObject = JsonNode.Parse(ref reader).AsObject();

            // `files` and `charts` need their own deserialization, and every field is optional since an
            // errored or not yet started deployment only reports a subset of them
            var liveAlgoResults = new LiveAlgorithmResults
            {
                Message = jObject["message"].Value<string>(),
                Status = jObject["status"].Value<string>(),
                DeployId = jObject["deployId"].Value<string>(),
                CloneId = jObject["cloneId"].Value<int>(),
                Launched = jObject["launched"].Value<DateTime>(),
                Stopped = jObject["stopped"].Value<DateTime?>(),
                Brokerage = jObject["brokerage"].Value<string>(),
                SecurityTypes = jObject["securityTypes"].Value<string>(),
                ProjectName = jObject["projectName"].Value<string>(),
                Description = jObject["description"].Value<string>(),
                Datacenter = jObject["datacenter"].Value<string>(),
                Public = jObject["public"].Value<bool>(),
                Success = jObject["success"].Value<bool>(),
                Errors = jObject["errors"]?.Value<List<string>>() ?? new List<string>()
            };

            if (!liveAlgoResults.Success)
            {
                // Either there was an error in the running algorithm or the algorithm hasn't started
                return liveAlgoResults;
            }

            // Deserialize charting data
            var chartDictionary = new Dictionary<string, Chart>();
            var charts = jObject["charts"] ?? jObject["Charts"];
            if (charts != null)
            {
                chartDictionary = charts.Value<Dictionary<string, Chart>>();
            }

            // Deserialize files data
            var projectFiles = new List<ProjectFile>();
            var files = jObject["files"] ?? jObject["Files"];
            if (files != null)
            {
                projectFiles = files.Value<List<ProjectFile>>();
            }

            liveAlgoResults.Charts = chartDictionary;
            liveAlgoResults.Files = projectFiles;
            liveAlgoResults.RuntimeStatistics = DeserializeDictionary(jObject, "runtimeStatistics", "RuntimeStatistics");
            liveAlgoResults.ServerStatistics = DeserializeDictionary(jObject, "serverStatistics", "ServerStatistics");
            liveAlgoResults.DeploymentDetails = DeserializeDictionary(jObject, "deploymentDetails", "DeploymentDetails");

            return liveAlgoResults;
        }

        private static IDictionary<string, string> DeserializeDictionary(JsonObject jObject, string name, string alternativeName)
        {
            var value = jObject[name] ?? jObject[alternativeName];
            if (value is not JsonObject)
            {
                return null;
            }
            return value.Value<Dictionary<string, string>>();
        }
    }
}
