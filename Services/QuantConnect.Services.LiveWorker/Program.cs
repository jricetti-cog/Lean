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
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using QuantConnect.Services.BacktestWorker;
using QuantConnect.Services.Defaults;
using QuantConnect.Services.Messaging;
using QuantConnect.Services.Messaging.RabbitMq;

namespace QuantConnect.Services.LiveWorker
{
    /// <summary>live-worker host: one container per live deployment (job-orchestrator starts it with <c>LiveWorker__Dispatch</c>).</summary>
    public static class LiveWorkerProgram
    {
        public static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.AddLeanServiceDefaults();
            builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection(WorkerOptions.Section));
            builder.Services.Configure<LiveWorkerOptions>(builder.Configuration.GetSection(LiveWorkerOptions.Section));
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddLeanRabbitMq(builder.Configuration);
            builder.Services.AddSingleton<ITransientSubscriber>(sp => sp.GetRequiredService<RabbitMqMessageBus>());

            var s3 = builder.Configuration.GetSection(S3Options.Section).Get<S3Options>() ?? new S3Options();
            builder.Services.AddSingleton<IResultStore>(new S3ResultStore(s3));
            builder.Services.AddSingleton(sp => new LiveEngineEnvironment(EngineVariables(sp.GetRequiredService<RabbitMqOptions>(), s3)));
            builder.Services.AddHttpClient(LiveServicesClient.Orchestrator, (sp, http) =>
                http.BaseAddress = sp.GetRequiredService<IOptions<LiveWorkerOptions>>().Value.OrchestratorUri);
            builder.Services.AddHttpClient(LiveServicesClient.Results, (sp, http) =>
                http.BaseAddress = sp.GetRequiredService<IOptions<LiveWorkerOptions>>().Value.ResultsUri);
            builder.Services.AddSingleton<ILiveServices, LiveServicesClient>();
            builder.Services.AddSingleton<IEngineLauncher, ProcessEngineLauncher>();
            builder.Services.AddSingleton<LiveJobRunner>();
            builder.Services.AddHostedService<LiveWorkerService>();

            var app = builder.Build();
            app.MapGet("/internal/v1/worker", (LiveJobRunner runner) => runner.Status);
            await app.RunAsync();
        }

        /// <summary>The bus engine handlers read <c>bus-*</c> settings from <c>LEAN_BUS_*</c> variables.</summary>
        public static IReadOnlyDictionary<string, string> EngineVariables(RabbitMqOptions rabbitMq, S3Options s3) => new Dictionary<string, string>
        {
            ["LEAN_BUS_RABBITMQ_URI"] = rabbitMq.Uri.ToString(),
            ["LEAN_BUS_ARTIFACT_STORE"] = "s3",
            ["LEAN_BUS_S3_ENDPOINT"] = s3.ServiceUrl,
            ["LEAN_BUS_S3_ACCESS_KEY"] = s3.AccessKey,
            ["LEAN_BUS_S3_SECRET_KEY"] = s3.SecretKey,
            ["LEAN_BUS_S3_REGION"] = s3.Region,
        };
    }
}
