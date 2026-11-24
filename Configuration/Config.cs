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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using QuantConnect.Logging;
using static System.FormattableString;

namespace QuantConnect.Configuration
{
    /// <summary>
    /// Configuration class loads the required external setup variables to launch the Lean engine.
    /// </summary>
    public static class Config
    {
        //Location of the configuration file.
        private static string ConfigurationFileName = "config.json";

        /// <summary>
        /// Set configuration file on-fly
        /// </summary>
        /// <param name="fileName"></param>
        public static void SetConfigurationFile(string fileName)
        {
            if (File.Exists(fileName))
            {
                Log.Trace(Invariant($"Using {fileName} as configuration file"));
                ConfigurationFileName = fileName;
            }
            else
            {
                Log.Error(Invariant($"Configuration file {fileName} does not exist, using {ConfigurationFileName}"));
            }
        }

        /// <summary>
        /// Merge CLI arguments with configuration file + load custom config file via CLI arg
        /// </summary>
        /// <param name="cliArguments"></param>
        public static void MergeCommandLineArgumentsWithConfiguration(Dictionary<string, object> cliArguments)
        {
            if (cliArguments.ContainsKey("config"))
            {
                SetConfigurationFile(cliArguments["config"] as string);
                Reset();
            }

            var jsonArguments = JsonSerializer.SerializeToNode(cliArguments, WriteOptions).AsObject();

            Merge(Settings.Value, jsonArguments);
        }

        /// <summary>
        /// Resets the config settings to their default values.
        /// Called in regression tests where multiple algorithms are run sequentially,
        /// and we need to guarantee that every test starts with the same configuration.
        /// </summary>
        public static void Reset()
        {
            Settings = new Lazy<JsonObject>(ConfigFactory);
        }

        private static Lazy<JsonObject> Settings = new Lazy<JsonObject>(ConfigFactory);

        private static JsonObject ConfigFactory()
        {
            // initialize settings inside a lazy for free thread-safe, one-time initialization
            if (!File.Exists(ConfigurationFileName))
            {
                return new JsonObject
                {
                    {"algorithm-type-name", "BasicTemplateAlgorithm"},
                    {"live-mode", false},
                    {"data-folder", "../../../Data/"},
                    {"messaging-handler", "QuantConnect.Messaging.Messaging"},
                    {"job-queue-handler", "QuantConnect.Queues.JobQueue"},
                    {"api-handler", "QuantConnect.Api.Api"},
                    {"setup-handler", "QuantConnect.Lean.Engine.Setup.ConsoleSetupHandler"},
                    {"result-handler", "QuantConnect.Lean.Engine.Results.BacktestingResultHandler"},
                    {"data-feed-handler", "QuantConnect.Lean.Engine.DataFeeds.FileSystemDataFeed"},
                    {"real-time-handler", "QuantConnect.Lean.Engine.RealTime.BacktestingRealTimeHandler"},
                    {"transaction-handler", "QuantConnect.Lean.Engine.TransactionHandlers.BacktestingTransactionHandler"}
                };
            }

            return JsonNode.Parse(File.ReadAllText(ConfigurationFileName), documentOptions: DocumentOptions).AsObject();
        }

        /// <summary>
        /// Gets the currently selected environment. If sub-environments are defined,
        /// they'll be returned as {env1}.{env2}
        /// </summary>
        /// <returns>The fully qualified currently selected environment</returns>
        public static string GetEnvironment()
        {
            var environments = new List<string>();
            JsonNode currentEnvironment = Settings.Value;
            var env = currentEnvironment["environment"];
            while (currentEnvironment != null && env != null)
            {
                var currentEnv = AsText(env);
                environments.Add(currentEnv);
                var moreEnvironments = currentEnvironment["environments"];
                if (moreEnvironments == null)
                {
                    break;
                }

                currentEnvironment = moreEnvironments[currentEnv];
                env = currentEnvironment["environment"];
            }
            return string.Join(".", environments);
        }

        /// <summary>
        /// Get the matching config setting from the file searching for this key.
        /// </summary>
        /// <param name="key">String key value we're seaching for in the config file.</param>
        /// <param name="defaultValue"></param>
        /// <returns>String value of the configuration setting or empty string if nothing found.</returns>
        public static string Get(string key, string defaultValue = "")
        {
            // special case environment requests
            if (key == "environment") return GetEnvironment();

            var token = GetToken(Settings.Value, key);
            if (token == null)
            {
                if (Log.DebuggingEnabled)
                {
                    Log.Debug(Invariant($"Config.Get(): Configuration key not found. Key: {key} - Using default value: {defaultValue}"));
                }
                return defaultValue;
            }
            return AsText(token);
        }

        /// <summary>
        /// Gets the underlying json node for the specified key
        /// </summary>
        public static JsonNode GetToken(string key)
        {
            return GetToken(Settings.Value, key);
        }

        /// <summary>
        /// Sets a configuration value. This is really only used to help testing. The key heye can be
        /// specified as {environment}.key to set a value on a specific environment
        /// </summary>
        /// <param name="key">The key to be set</param>
        /// <param name="value">The new value</param>
        public static void Set(string key, dynamic value)
        {
            JsonNode environment = Settings.Value;
            while (key.Contains('.', StringComparison.InvariantCulture))
            {
                var envName = key.Substring(0, key.IndexOf(".", StringComparison.InvariantCulture));
                key = key.Substring(key.IndexOf(".", StringComparison.InvariantCulture) + 1);
                var environments = environment["environments"];
                if (environments == null)
                {
                    environment["environments"] = environments = new JsonObject();
                }
                environment = environments[envName];
            }
            environment[key] = ToNode((object)value);
        }

        /// <summary>
        /// Get a boolean value configuration setting by a configuration key.
        /// </summary>
        /// <param name="key">String value of the configuration key.</param>
        /// <param name="defaultValue">The default value to use if not found in configuration</param>
        /// <returns>Boolean value of the config setting.</returns>
        public static bool GetBool(string key, bool defaultValue = false)
        {
            return GetValue(key, defaultValue);
        }

        /// <summary>
        /// Get the int value of a config string.
        /// </summary>
        /// <param name="key">Search key from the config file</param>
        /// <param name="defaultValue">The default value to use if not found in configuration</param>
        /// <returns>Int value of the config setting.</returns>
        public static int GetInt(string key, int defaultValue = 0)
        {
            return GetValue(key, defaultValue);
        }

        /// <summary>
        /// Get the double value of a config string.
        /// </summary>
        /// <param name="key">Search key from the config file</param>
        /// <param name="defaultValue">The default value to use if not found in configuration</param>
        /// <returns>Double value of the config setting.</returns>
        public static double GetDouble(string key, double defaultValue = 0.0)
        {
            return GetValue(key, defaultValue);
        }

        /// <summary>
        /// Gets a value from configuration and converts it to the requested type, assigning a default if
        /// the configuration is null or empty
        /// </summary>
        /// <typeparam name="T">The requested type</typeparam>
        /// <param name="key">Search key from the config file</param>
        /// <param name="defaultValue">The default value to use if not found in configuration</param>
        /// <returns>Converted value of the config setting.</returns>
        public static T GetValue<T>(string key, T defaultValue = default(T))
        {
            // special case environment requests
            if (key == "environment" && typeof(T) == typeof(string)) return (T)(object)GetEnvironment();

            var token = GetToken(Settings.Value, key);
            if (token == null)
            {
                var defaultValueString = defaultValue is IConvertible
                    ? ((IConvertible)defaultValue).ToString(CultureInfo.InvariantCulture)
                    : defaultValue is IFormattable
                        ? ((IFormattable)defaultValue).ToString(null, CultureInfo.InvariantCulture)
                        : Invariant($"{defaultValue}");

                if (Log.DebuggingEnabled)
                {
                    Log.Debug(Invariant($"Config.GetValue(): {key} - Using default value: {defaultValueString}"));
                }
                return defaultValue;
            }

            var type = typeof(T);
            var value = AsText(token);

            if (type.IsEnum)
            {
                return (T)Enum.Parse(type, value, true);
            }

            if (typeof(IConvertible).IsAssignableFrom(type))
            {
                return (T)Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
            }

            // try and find a static parse method
            try
            {
                var parse = type.GetMethod("Parse", new[] { typeof(string) });
                if (parse != null)
                {
                    var result = parse.Invoke(null, new object[] { value });
                    return (T)result;
                }
            }
            catch (Exception err)
            {
                Log.Trace(Invariant($"Config.GetValue<{typeof(T).Name}>({key},{defaultValue}): Failed to parse: {value}. Using default value."));
                Log.Error(err);
                return defaultValue;
            }

            try
            {
                return JsonSerializer.Deserialize<T>(value, SerializerOptions);
            }
            catch (Exception err)
            {
                Log.Trace(Invariant($"Config.GetValue<{typeof(T).Name}>({key},{defaultValue}): Failed to JSON deserialize: {value}. Using default value."));
                Log.Error(err);
                return defaultValue;
            }
        }

        /// <summary>
        /// Tries to find the specified key and parse it as a T, using
        /// default(T) if unable to locate the key or unable to parse it
        /// </summary>
        /// <typeparam name="T">The desired output type</typeparam>
        /// <param name="key">The configuration key</param>
        /// <param name="value">The output value. If the key is found and parsed successfully, it will be the parsed value, else default(T).</param>
        /// <returns>True on successful parse or if they key is not found. False only when key is found but fails to parse.</returns>
        public static bool TryGetValue<T>(string key, out T value)
        {
            return TryGetValue(key, default(T), out value);
        }

        /// <summary>
        /// Tries to find the specified key and parse it as a T, using
        /// defaultValue if unable to locate the key or unable to parse it
        /// </summary>
        /// <typeparam name="T">The desired output type</typeparam>
        /// <param name="key">The configuration key</param>
        /// <param name="defaultValue">The default value to use on key not found or unsuccessful parse</param>
        /// <param name="value">The output value. If the key is found and parsed successfully, it will be the parsed value, else defaultValue.</param>
        /// <returns>True on successful parse or if they key is not found and using defaultValue. False only when key is found but fails to parse.</returns>
        public static bool TryGetValue<T>(string key, T defaultValue, out T value)
        {
            try
            {
                value = GetValue(key, defaultValue);
                return true;
            }
            catch
            {
                value = defaultValue;
                return false;
            }
        }

        /// <summary>
        /// Write the contents of the serialized configuration back to the disk.
        /// </summary>
        public static void Write(string targetPath = null)
        {
            if (!Settings.IsValueCreated) return;
            var serialized = Settings.Value.ToJsonString(WriteOptions);

            var taget = ConfigurationFileName;
            if (!string.IsNullOrEmpty(targetPath))
            {
                taget = Path.Combine(targetPath, ConfigurationFileName);
            }
            File.WriteAllText(taget, serialized);
        }

        /// <summary>
        /// Flattens the jobject with respect to the selected environment and then
        /// removes the 'environments' node
        /// </summary>
        /// <param name="overrideEnvironment">The environment to use</param>
        /// <returns>The flattened json object</returns>
        public static JsonObject Flatten(string overrideEnvironment)
        {
            return Flatten(Settings.Value, overrideEnvironment);
        }

        /// <summary>
        /// Flattens the jobject with respect to the selected environment and then
        /// removes the 'environments' node
        /// </summary>
        /// <param name="config">The configuration represented as a json object</param>
        /// <param name="overrideEnvironment">The environment to use</param>
        /// <returns>The flattened json object</returns>
        public static JsonObject Flatten(JsonObject config, string overrideEnvironment)
        {
            var clone = config.DeepClone().AsObject();

            // remove the environment declaration
            clone.Remove("environment");

            if (!string.IsNullOrEmpty(overrideEnvironment))
            {
                var environmentSections = overrideEnvironment.Split('.');

                for (int i = 0; i < environmentSections.Length; i++)
                {
                    var env = string.Join(".environments.", environmentSections.Where((x, j) => j <= i));

                    if (config["environments"] is not JsonObject environments) continue;

                    if (SelectToken(environments, env) is not JsonObject settings) continue;

                    // copy values for the selected environment to the root
                    foreach (var kvp in settings)
                    {
                        // remove if already exists on clone
                        clone.Remove(kvp.Key);
                        clone.Add(kvp.Key, AsText(kvp.Value));
                    }
                }
            }

            // remove all environments
            clone.Remove("environments");

            return clone;
        }

        private static JsonNode GetToken(JsonNode settings, string key)
        {
            return GetToken(settings, key, SelectToken(settings, key));
        }

        private static JsonNode GetToken(JsonNode settings, string key, JsonNode current)
        {
            var environmentSetting = SelectToken(settings, "environment");
            if (environmentSetting != null)
            {
                var environmentSettingValue = AsText(environmentSetting);
                if (!string.IsNullOrWhiteSpace(environmentSettingValue))
                {
                    var environment = SelectToken(settings, "environments." + environmentSettingValue);
                    if (environment != null)
                    {
                        var setting = SelectToken(environment, key);
                        if (setting != null)
                        {
                            current = setting;
                        }
                        // allows nesting of environments, live.tradier, live.interactive, ect...
                        return GetToken(environment, key, current);
                    }
                }
            }
            if (current == null)
            {
                return SelectToken(settings, key);
            }
            return current;
        }

        private static readonly JsonDocumentOptions DocumentOptions = new()
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            IncludeFields = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private static readonly JsonSerializerOptions WriteOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>
        /// Navigates a dotted path like newtonsoft's SelectToken
        /// </summary>
        private static JsonNode SelectToken(JsonNode node, string path)
        {
            foreach (var segment in path.Split('.'))
            {
                if (node is not JsonObject jObject || !jObject.TryGetPropertyValue(segment, out node))
                {
                    return null;
                }
            }
            return node;
        }

        /// <summary>
        /// The text of a setting, matching newtonsoft's JToken.ToString(): strings unquoted, 'True'/'False', objects indented
        /// </summary>
        private static string AsText(JsonNode token)
        {
            if (token is JsonValue value)
            {
                switch (value.GetValueKind())
                {
                    case JsonValueKind.String:
                        return value.TryGetValue<string>(out var text) ? text : JsonSerializer.Deserialize<string>(value.ToJsonString());
                    case JsonValueKind.True:
                        return bool.TrueString;
                    case JsonValueKind.False:
                        return bool.FalseString;
                    case JsonValueKind.Null:
                        return string.Empty;
                    case JsonValueKind.Number:
                        var raw = value.ToJsonString();
                        if (raw.AsSpan().IndexOfAny('.', 'e', 'E') >= 0 && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                        {
                            return number.ToString(CultureInfo.InvariantCulture);
                        }
                        return raw;
                }
            }
            return token?.ToJsonString(WriteOptions) ?? string.Empty;
        }

        private static JsonNode ToNode(object value)
        {
            return value switch
            {
                null => null,
                JsonNode node => node.Parent == null ? node : node.DeepClone(),
                _ => JsonSerializer.SerializeToNode(value, value.GetType(), WriteOptions)
            };
        }

        /// <summary>
        /// Newtonsoft's JObject.Merge with MergeArrayHandling.Union and null values ignored
        /// </summary>
        private static void Merge(JsonObject target, JsonObject content)
        {
            foreach (var kvp in content.ToList())
            {
                if (!target.TryGetPropertyValue(kvp.Key, out var existing))
                {
                    target[kvp.Key] = kvp.Value?.DeepClone();
                }
                else if (kvp.Value != null)
                {
                    if (existing is JsonObject existingObject && kvp.Value is JsonObject contentObject)
                    {
                        Merge(existingObject, contentObject);
                    }
                    else if (existing is JsonArray existingArray && kvp.Value is JsonArray contentArray)
                    {
                        foreach (var item in contentArray)
                        {
                            if (!existingArray.Any(x => JsonNode.DeepEquals(x, item)))
                            {
                                existingArray.Add(item?.DeepClone());
                            }
                        }
                    }
                    else if (kvp.Value.GetValueKind() != JsonValueKind.Null)
                    {
                        target[kvp.Key] = kvp.Value.DeepClone();
                    }
                }
            }
        }
    }
}
