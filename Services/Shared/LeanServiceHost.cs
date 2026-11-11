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
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using QuantConnect.Logging;

namespace QuantConnect.Services
{
    /// <summary>
    /// Common hosting for the LEAN service processes: environment configuration, logging,
    /// the /health endpoint, optional bearer-token protection and Newtonsoft JSON responses
    /// (LEAN result types are annotated for Newtonsoft, not System.Text.Json).
    /// </summary>
    public static class LeanServiceHost
    {
        /// <summary>
        /// Environment variable holding the shared token that protects the /v1 routes. Unset disables auth.
        /// </summary>
        public const string ServiceTokenVariable = "LEAN_SERVICE_TOKEN";

        /// <summary>
        /// Serializer settings used for every JSON response
        /// </summary>
        public static JsonSerializerSettings SerializerSettings { get; } = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.None
        };

        /// <summary>
        /// Creates a web application builder with LEAN configuration and logging applied
        /// </summary>
        /// <param name="serviceName">Name reported by /health and in logs</param>
        /// <param name="args">Command line arguments</param>
        public static WebApplicationBuilder CreateBuilder(string serviceName, string[] args)
        {
            if (Log.LogHandler is not ConsoleLogHandler)
            {
                Log.LogHandler = new ConsoleLogHandler();
            }
            LeanServiceConfiguration.ApplyEnvironment();

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args ?? Array.Empty<string>(),
                ContentRootPath = AppContext.BaseDirectory
            });
            builder.Logging.ClearProviders();
            builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
            builder.Logging.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss ";
            });
            return builder;
        }

        /// <summary>
        /// Maps GET /health and, when <see cref="ServiceTokenVariable"/> is set, requires
        /// "Authorization: Bearer &lt;token&gt;" on every /v1 route
        /// </summary>
        public static WebApplication UseLeanServiceDefaults(this WebApplication app, string serviceName, string serviceToken = null)
        {
            serviceToken ??= Environment.GetEnvironmentVariable(ServiceTokenVariable);
            if (!string.IsNullOrEmpty(serviceToken))
            {
                var expected = "Bearer " + serviceToken;
                app.Use(async (context, next) =>
                {
                    if (context.Request.Path.StartsWithSegments("/v1")
                        && !string.Equals(context.Request.Headers.Authorization.ToString(), expected, StringComparison.Ordinal))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        await context.Response.WriteAsync("missing or invalid bearer token").ConfigureAwait(false);
                        return;
                    }
                    await next(context).ConfigureAwait(false);
                });
            }

            app.MapGet("/health", () => Json(new { service = serviceName, status = "ok", version = Globals.Version }));
            return app;
        }

        /// <summary>
        /// Serializes a value with Newtonsoft and returns it as an application/json result
        /// </summary>
        public static IResult Json(object value, int statusCode = StatusCodes.Status200OK)
        {
            return Results.Content(JsonConvert.SerializeObject(value, SerializerSettings), "application/json", statusCode: statusCode);
        }

        /// <summary>
        /// Reads the request body as a string
        /// </summary>
        public static async Task<string> ReadBodyAsync(this HttpRequest request)
        {
            using var reader = new System.IO.StreamReader(request.Body);
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Forwards every request under <paramref name="prefix"/> to <paramref name="baseUrl"/>, keeping
        /// the path, query string, body and content type. Used by the API service to front the internal services.
        /// </summary>
        public static void MapProxy(this IEndpointRouteBuilder app, string prefix, Func<string> baseUrl, HttpClient client)
        {
            app.Map(prefix + "/{**rest}", async (HttpContext context) =>
            {
                var target = baseUrl();
                if (string.IsNullOrEmpty(target))
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await context.Response.WriteAsync($"no upstream configured for {prefix}").ConfigureAwait(false);
                    return;
                }

                var uri = target.TrimEnd('/') + context.Request.Path + context.Request.QueryString;
                using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), uri);
                if (context.Request.ContentLength > 0 || context.Request.Headers.TransferEncoding.Count > 0)
                {
                    request.Content = new StreamContent(context.Request.Body);
                    if (!string.IsNullOrEmpty(context.Request.ContentType))
                    {
                        request.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
                    }
                }
                foreach (var header in context.Request.Headers.Where(h => h.Key is "Accept" or "Authorization"))
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                }

                try
                {
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted).ConfigureAwait(false);
                    context.Response.StatusCode = (int)response.StatusCode;
                    if (response.Content.Headers.ContentType != null)
                    {
                        context.Response.ContentType = response.Content.Headers.ContentType.ToString();
                    }
                    await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
                }
                catch (HttpRequestException err)
                {
                    context.Response.StatusCode = StatusCodes.Status502BadGateway;
                    await context.Response.WriteAsync($"upstream {target} unavailable: {err.Message}").ConfigureAwait(false);
                }
            });
        }
    }
}
