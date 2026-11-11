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
using System.Globalization;
using QuantConnect.Configuration;
using QuantConnect.Logging;

namespace QuantConnect.Services
{
    /// <summary>
    /// Maps container environment variables onto LEAN <see cref="Config"/> keys so every service
    /// (and the engine worker) is configured the same way: <c>LEAN_DATA_FOLDER</c> becomes
    /// <c>data-folder</c>, <c>LEAN_JOB_USER_ID</c> becomes <c>job-user-id</c>, and so on.
    /// <c>LEAN_CONFIG_FILE</c> selects a base config.json before the overrides are applied.
    /// </summary>
    public static class LeanServiceConfiguration
    {
        /// <summary>
        /// Prefix of the environment variables that are copied into <see cref="Config"/>
        /// </summary>
        public const string EnvironmentPrefix = "LEAN_";

        /// <summary>
        /// Converts an environment variable name into its LEAN configuration key, or null when it is not a LEAN variable
        /// </summary>
        public static string ToConfigKey(string environmentVariable)
        {
            if (string.IsNullOrEmpty(environmentVariable)
                || !environmentVariable.StartsWith(EnvironmentPrefix, StringComparison.Ordinal)
                || environmentVariable.Length == EnvironmentPrefix.Length)
            {
                return null;
            }
            return environmentVariable.Substring(EnvironmentPrefix.Length).ToLowerInvariant().Replace('_', '-');
        }

        /// <summary>
        /// Applies the LEAN_* environment variables to <see cref="Config"/> and resets <see cref="Globals"/>
        /// so values such as the data folder pick up the overrides
        /// </summary>
        /// <param name="environment">Variables to apply, defaults to the process environment</param>
        /// <returns>The configuration keys that were set</returns>
        public static IReadOnlyList<string> ApplyEnvironment(IDictionary environment = null)
        {
            environment ??= Environment.GetEnvironmentVariables();

            if (environment["LEAN_CONFIG_FILE"] is string configFile && !string.IsNullOrEmpty(configFile))
            {
                Config.SetConfigurationFile(configFile);
                Config.Reset();
            }

            var applied = new List<string>();
            foreach (DictionaryEntry entry in environment)
            {
                var key = ToConfigKey(entry.Key as string);
                if (key == null || key == "config-file")
                {
                    continue;
                }
                Config.Set(key, Convert.ToString(entry.Value, CultureInfo.InvariantCulture));
                applied.Add(key);
            }

            Globals.Reset();
            if (applied.Count > 0)
            {
                Log.Trace($"LeanServiceConfiguration.ApplyEnvironment(): applied {string.Join(", ", applied)}");
            }
            return applied;
        }
    }
}
