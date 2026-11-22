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
using Json.Schema;
using QuantConnect.Services.Contracts;
using YamlDotNet.RepresentationModel;

namespace QuantConnect.Tests.Services
{
    /// <summary>
    /// The JSON Schemas and OpenAPI documents under docs/architecture/contracts (copied to Services/contracts in the
    /// test output), loaded into one registry so records can be validated against them.
    /// </summary>
    public static class ContractSchemas
    {
        public const string BaseUri = "https://contracts.lean.local/";

        public static string Root { get; } = Path.Combine(AppContext.BaseDirectory, "Services", "contracts");
        public static string MessagesDirectory { get; } = Path.Combine(Root, "messages", "v1");

        private static readonly Lazy<bool> _registry = new(Load);

        public static EvaluationOptions Options => new()
        {
            OutputFormat = OutputFormat.List,
            RequireFormatValidation = true,
        };

        /// <summary>Validates against a message schema file, e.g. <c>job.started.schema.json</c>.</summary>
        public static EvaluationResults ValidateMessage(string schemaFile, JsonNode instance) =>
            Validate($"{BaseUri}messages/v1/{schemaFile}", instance);

        /// <summary>Validates against an OpenAPI component schema, e.g. (<c>common.yaml</c>, <c>Job</c>).</summary>
        public static EvaluationResults ValidateComponent(string openApiFile, string schemaName, JsonNode instance) =>
            Validate($"{BaseUri}openapi/{openApiFile}#/$defs/{schemaName}", instance);

        public static EvaluationResults Validate(string reference, JsonNode instance)
        {
            _ = _registry.Value;
            var schema = new JsonSchemaBuilder().Ref(reference).Build();
            return schema.Evaluate(instance, Options);
        }

        public static string Describe(EvaluationResults results) => string.Join(Environment.NewLine,
            (results.Details ?? new List<EvaluationResults>())
                .Where(d => d.Errors != null && d.Errors.Count > 0)
                .SelectMany(d => d.Errors.Select(e => $"{d.InstanceLocation} [{d.EvaluationPath}] {e.Key}: {e.Value}")));

        public static void AssertValid(EvaluationResults results, string what)
        {
            if (!results.IsValid)
            {
                NUnit.Framework.Assert.Fail($"{what} does not match its schema:{Environment.NewLine}{Describe(results)}");
            }
        }

        /// <summary>Serializes a contract record with <see cref="ContractJson"/>.</summary>
        public static JsonNode ToNode<T>(T value) => JsonNode.Parse(ContractJson.Serialize(value));

        private static bool Load()
        {
            var registry = SchemaRegistry.Global;
            foreach (var file in Directory.GetFiles(MessagesDirectory, "*.schema.json"))
            {
                registry.Register(JsonSchema.FromFile(file));
            }
            foreach (var file in Directory.GetFiles(Path.Combine(Root, "openapi"), "*.yaml"))
            {
                registry.Register(OpenApiComponentsSchema(File.ReadAllText(file), $"{BaseUri}openapi/{Path.GetFileName(file)}"));
            }
            return true;
        }

        /// <summary>
        /// An OpenAPI 3.1 document's <c>components.schemas</c> as a 2020-12 schema whose <c>$defs</c> are those
        /// components (and <c>$ref</c>s rewritten to match), so local and cross-file references resolve.
        /// </summary>
        public static JsonSchema OpenApiComponentsSchema(string yaml, string id)
        {
            var components = YamlToJson(yaml)["components"]?["schemas"]?.DeepClone() ?? new JsonObject();
            RewriteRefs(components);
            var document = new JsonObject
            {
                ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
                ["$id"] = id,
                ["$defs"] = components,
            };
            return JsonSchema.FromText(document.ToJsonString());
        }

        private static void RewriteRefs(JsonNode node)
        {
            switch (node)
            {
                case JsonObject obj:
                    if (obj["$ref"] is JsonValue reference && reference.TryGetValue<string>(out var target))
                    {
                        obj["$ref"] = target.Replace("#/components/schemas/", "#/$defs/", StringComparison.Ordinal);
                    }
                    foreach (var (_, child) in obj.ToList())
                    {
                        RewriteRefs(child);
                    }
                    break;
                case JsonArray array:
                    foreach (var child in array)
                    {
                        RewriteRefs(child);
                    }
                    break;
            }
        }

        /// <summary>YAML → JSON keeping plain scalars typed (numbers, booleans, null) and quoted scalars as strings.</summary>
        public static JsonNode YamlToJson(string yaml)
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            return Convert(stream.Documents[0].RootNode);
        }

        private static JsonNode Convert(YamlNode node)
        {
            switch (node)
            {
                case YamlMappingNode mapping:
                    var obj = new JsonObject();
                    foreach (var (key, value) in mapping.Children)
                    {
                        obj[((YamlScalarNode)key).Value!] = Convert(value);
                    }
                    return obj;
                case YamlSequenceNode sequence:
                    return new JsonArray(sequence.Children.Select(Convert).ToArray());
                case YamlScalarNode scalar:
                    var text = scalar.Value ?? string.Empty;
                    if (scalar.Style != YamlDotNet.Core.ScalarStyle.Plain)
                    {
                        return JsonValue.Create(text);
                    }
                    if (text is "null" or "~" or "")
                    {
                        return null;
                    }
                    if (text is "true" or "false")
                    {
                        return JsonValue.Create(text == "true");
                    }
                    if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                    {
                        return JsonValue.Create(integer);
                    }
                    if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    {
                        return JsonValue.Create(number);
                    }
                    return JsonValue.Create(text);
                default:
                    throw new NotSupportedException(node.GetType().Name);
            }
        }

        /// <summary>The example files (envelope + body + routing) under messages/v1/examples.</summary>
        public static IEnumerable<string> ExampleFiles() =>
            Directory.GetFiles(Path.Combine(MessagesDirectory, "examples"), "*.json").OrderBy(f => f, StringComparer.Ordinal).Select(Path.GetFileName);

        public static JsonElement ReadExample(string fileName) =>
            JsonDocument.Parse(File.ReadAllText(Path.Combine(MessagesDirectory, "examples", fileName))).RootElement.Clone();
    }
}
