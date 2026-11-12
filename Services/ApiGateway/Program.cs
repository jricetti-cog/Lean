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
 *
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using QuantConnect.Configuration;
using QuantConnect.Logging;
using QuantConnect.Messaging.Bus;

namespace QuantConnect.Services.ApiGateway
{
    /// <summary>
    /// API Gateway entry point: REST and WebSocket surface over the message bus
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// Starts the gateway. Settings: <c>gateway-urls</c> (default http://localhost:5000),
        /// <c>gateway-users</c> (json object of user id to access token, defaults to <c>job-user-id</c>/<c>api-access-token</c>),
        /// <c>gateway-timestamp-tolerance-seconds</c>, message bus settings.
        /// </summary>
        public static void Main(string[] args)
        {
            MessageBusFactory.ApplyServiceConfiguration(args);
            Log.LogHandler = new ConsoleLogHandler();
            using var bus = MessageBusFactory.Create("api-gateway-" + Environment.MachineName);
            var app = Build(bus, LoadUsers(), Config.Get("gateway-urls", "http://localhost:5000"));
            app.Run();
        }

        /// <summary>
        /// Reads the configured users
        /// </summary>
        public static Dictionary<string, string> LoadUsers()
        {
            var users = Config.Get("gateway-users");
            if (!string.IsNullOrEmpty(users))
            {
                return ServiceJson.Deserialize<Dictionary<string, string>>(users);
            }
            var token = Config.Get("api-access-token");
            if (string.IsNullOrEmpty(token))
            {
                throw new InvalidOperationException("Configure 'gateway-users' or 'api-access-token' for the gateway");
            }
            return new Dictionary<string, string> { [Config.Get("job-user-id", "0")] = token };
        }

        /// <summary>
        /// Builds the web application
        /// </summary>
        public static WebApplication Build(IMessageBus bus, IReadOnlyDictionary<string, string> users, string urls)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory, WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot") });
            builder.WebHost.UseUrls(urls);
            var app = builder.Build();
            var authenticator = new GatewayAuthenticator(users, TimeSpan.FromSeconds(Config.GetInt("gateway-timestamp-tolerance-seconds", 300)));
            var dispatcher = new JobDispatcher(bus);
            app.Lifetime.ApplicationStopping.Register(dispatcher.Dispose);

            app.UseDefaultFiles();
            app.UseStaticFiles();
            app.UseWebSockets();

            // authentication for everything except the dashboard and health
            app.Use(async (context, next) =>
            {
                var path = context.Request.Path.Value ?? string.Empty;
                if (path.StartsWith("/api/", StringComparison.Ordinal) || path.StartsWith("/ws/", StringComparison.Ordinal))
                {
                    var userId = authenticator.Authenticate(context.Request);
                    if (userId == null)
                    {
                        await WriteJson(context, new { success = false, errors = new[] { "Authentication failed" } }, StatusCodes.Status401Unauthorized).ConfigureAwait(false);
                        return;
                    }
                    context.Items["userId"] = userId;
                }
                await next(context).ConfigureAwait(false);
            });

            app.MapGet("/health", async context =>
            {
                object marketData;
                try
                {
                    marketData = bus.Request<object, ServiceHealth>(Topics.MarketDataHealth, null, ContractVersions.MarketDataV1, TimeSpan.FromSeconds(3));
                }
                catch (MessageBusException exception)
                {
                    marketData = new { status = "unreachable", error = exception.Message };
                }
                await WriteJson(context, new
                {
                    gateway = new ServiceHealth { Service = "api-gateway", InstanceId = bus.InstanceId, Contracts = ContractVersions.Supported.ToList() },
                    marketData,
                    workers = dispatcher.Workers.Where(w => DateTime.UtcNow - w.Value < TimeSpan.FromSeconds(30)).Select(w => new { workerId = w.Key, lastSeenUtc = w.Value })
                }).ConfigureAwait(false);
            });

            // jobs
            app.MapPost("/api/v1/jobs/{type}", async (HttpContext context, string type) =>
            {
                if (!Enum.TryParse<JobType>(type, true, out var jobType))
                {
                    await WriteError(context, $"Unknown job type '{type}', use backtest, live or research").ConfigureAwait(false);
                    return;
                }
                var submission = await ReadJson<JobSubmission>(context).ConfigureAwait(false) ?? new JobSubmission();
                submission.Type = jobType;
                try
                {
                    await WriteJson(context, dispatcher.Submit((string)context.Items["userId"], submission), StatusCodes.Status201Created).ConfigureAwait(false);
                }
                catch (ArgumentException exception)
                {
                    await WriteError(context, exception.Message).ConfigureAwait(false);
                }
            });
            app.MapGet("/api/v1/jobs", context => WriteJson(context, dispatcher.List((string)context.Items["userId"])));
            app.MapGet("/api/v1/jobs/{jobId}", (HttpContext context, string jobId) =>
                Owned(context, dispatcher.Get(jobId), job => WriteJson(context, job)));
            app.MapGet("/api/v1/jobs/{jobId}/events", (HttpContext context, string jobId) =>
                Owned(context, dispatcher.Get(jobId), _ => WriteJson(context, dispatcher.Events(jobId))));
            app.MapPost("/api/v1/jobs/{jobId}/cancel", (HttpContext context, string jobId) =>
                Owned(context, dispatcher.Get(jobId), _ => WriteJson(context, dispatcher.Cancel(jobId))));

            // market data
            app.MapGet("/api/v1/data", (HttpContext context) => Proxy(context, () =>
                bus.Request<DataListRequest, DataListResponse>(Topics.MarketDataList,
                    new DataListRequest { Prefix = context.Request.Query["prefix"], MaxResults = int.TryParse(context.Request.Query["max"], out var max) ? max : 1000 },
                    ContractVersions.MarketDataV1)));
            app.MapGet("/api/v1/data/file", async context =>
            {
                try
                {
                    var reply = bus.Request(Topics.MarketDataFetch, new DataFetchRequest { Key = context.Request.Query["key"] }, ContractVersions.MarketDataV1);
                    if (reply.GetBody<DataFetchResponse>()?.Found != true)
                    {
                        await WriteError(context, "Not found", StatusCodes.Status404NotFound).ConfigureAwait(false);
                        return;
                    }
                    context.Response.ContentType = "application/octet-stream";
                    await context.Response.Body.WriteAsync(reply.Payload ?? Array.Empty<byte>()).ConfigureAwait(false);
                }
                catch (MessageBusException exception)
                {
                    await WriteError(context, exception.Message, StatusCodes.Status502BadGateway).ConfigureAwait(false);
                }
            });
            app.MapPut("/api/v1/data/file", async context =>
            {
                using var buffer = new MemoryStream();
                await context.Request.Body.CopyToAsync(buffer).ConfigureAwait(false);
                await Proxy(context, () => bus.Request(Topics.MarketDataStore,
                    new DataStoreRequest { Key = context.Request.Query["key"], Overwrite = context.Request.Query["overwrite"] != "false" },
                    ContractVersions.MarketDataV1, buffer.ToArray()).GetBody<DataStoreResponse>()).ConfigureAwait(false);
            });
            app.MapPost("/api/v1/data/download", async context =>
            {
                var request = await ReadJson<DataDownloadRequest>(context).ConfigureAwait(false);
                await Proxy(context, () => bus.Request<DataDownloadRequest, DataDownloadResponse>(Topics.MarketDataDownload, request,
                    ContractVersions.MarketDataV1, TimeSpan.FromMinutes(30))).ConfigureAwait(false);
            });

            // websockets
            app.Map("/ws/jobs/{jobId}", async (HttpContext context, string jobId) =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    await WriteError(context, "WebSocket request expected").ConfigureAwait(false);
                    return;
                }
                var job = dispatcher.Get(jobId);
                if (job == null || job.UserId != (string)context.Items["userId"])
                {
                    await WriteError(context, "Job not found", StatusCodes.Status404NotFound).ConfigureAwait(false);
                    return;
                }
                var channel = Channel.CreateUnbounded<object>();
                void OnEvent(JobEvent jobEvent)
                {
                    if (jobEvent.JobId == jobId)
                    {
                        channel.Writer.TryWrite(jobEvent);
                    }
                }
                foreach (var past in dispatcher.Events(jobId))
                {
                    channel.Writer.TryWrite(past);
                }
                dispatcher.JobEventReceived += OnEvent;
                try
                {
                    await Stream(context, channel.Reader).ConfigureAwait(false);
                }
                finally
                {
                    dispatcher.JobEventReceived -= OnEvent;
                }
            });
            app.Map("/ws/marketdata", async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    await WriteError(context, "WebSocket request expected").ConfigureAwait(false);
                    return;
                }
                var query = context.Request.Query;
                var subscriptionId = Guid.NewGuid().ToString("N");
                var channel = Channel.CreateUnbounded<object>();
                using var stream = bus.Subscribe<MarketDataBatch>(Topics.MarketDataStream(subscriptionId), (batch, _) => channel.Writer.TryWrite(batch));
                try
                {
                    var securityType = Enum.TryParse<SecurityType>(query["securityType"], true, out var parsedType) ? parsedType : SecurityType.Equity;
                    var resolution = Enum.TryParse<Resolution>(query["resolution"], true, out var parsedResolution) ? parsedResolution : Resolution.Second;
                    var response = bus.Request<DataSubscriptionRequest, DataSubscriptionResponse>(Topics.MarketDataSubscribe, new DataSubscriptionRequest
                    {
                        SubscriptionId = subscriptionId,
                        Symbol = new SymbolContract { Ticker = query["ticker"].ToString() ?? "SPY", SecurityType = securityType, Market = query["market"].Count > 0 ? query["market"].ToString() : Market.USA },
                        DataType = resolution == Resolution.Tick ? "Tick" : "TradeBar",
                        Resolution = resolution,
                        TickType = TickType.Trade
                    }, ContractVersions.MarketDataV1);
                    channel.Writer.TryWrite(response);
                    await Stream(context, channel.Reader).ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        bus.Request(Topics.MarketDataUnsubscribe, new DataUnsubscribeRequest { SubscriptionId = subscriptionId }, ContractVersions.MarketDataV1, timeout: TimeSpan.FromSeconds(5));
                    }
                    catch (MessageBusException exception)
                    {
                        Log.Error(exception, "Gateway: market data unsubscribe failed");
                    }
                }
            });
            return app;
        }

        private static async Task Stream(HttpContext context, ChannelReader<object> reader)
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            using var closed = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var receive = Task.Run(async () =>
            {
                var buffer = new byte[1024];
                try
                {
                    while (socket.State == WebSocketState.Open)
                    {
                        var result = await socket.ReceiveAsync(buffer, closed.Token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            break;
                        }
                    }
                }
                catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
                {
                }
                closed.Cancel();
            });
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var message = await reader.ReadAsync(closed.Token).ConfigureAwait(false);
                    var bytes = Encoding.UTF8.GetBytes(ServiceJson.Serialize(message));
                    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, closed.Token).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
            {
            }
            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false);
            }
            await receive.ConfigureAwait(false);
        }

        private static Task Owned(HttpContext context, JobRecord job, Func<JobRecord, Task> action)
        {
            if (job == null || job.UserId != (string)context.Items["userId"])
            {
                return WriteError(context, "Job not found", StatusCodes.Status404NotFound);
            }
            return action(job);
        }

        private static async Task Proxy<T>(HttpContext context, Func<T> request)
        {
            try
            {
                await WriteJson(context, request()).ConfigureAwait(false);
            }
            catch (MessageBusException exception)
            {
                await WriteError(context, exception.Message, StatusCodes.Status502BadGateway).ConfigureAwait(false);
            }
        }

        private static async Task<T> ReadJson<T>(HttpContext context)
        {
            using var reader = new StreamReader(context.Request.Body);
            var json = await reader.ReadToEndAsync().ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(json) ? default : ServiceJson.Deserialize<T>(json);
        }

        private static Task WriteError(HttpContext context, string error, int status = StatusCodes.Status400BadRequest)
        {
            return WriteJson(context, new { success = false, errors = new[] { error } }, status);
        }

        private static Task WriteJson(HttpContext context, object value, int status = StatusCodes.Status200OK)
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync(ServiceJson.Serialize(value));
        }
    }
}
