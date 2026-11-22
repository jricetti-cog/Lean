using Newtonsoft.Json;
using QuantConnect.Configuration;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Packets;
using QuantConnect.Queues;
using QuantConnect.Securities;
using QuantConnect.Services.Contracts;
using ContractLanguage = QuantConnect.Services.Contracts.AlgorithmLanguage;

namespace QuantConnect.Services.EngineHandlers;

/// <summary>
/// <see cref="IJobQueueHandler"/> for a worker's child Launcher (<c>job-queue-handler: BusJobQueueHandler</c>).
/// The job is the dispatched <c>JobSubmitted</c> the worker host received from RabbitMQ and wrote to
/// <c>bus-job-file</c>; uploaded algorithms are fetched from MinIO and checked against their SHA-256.
/// The <see cref="AlgorithmNodePacket"/> itself is built by LEAN's <see cref="JobQueue"/> from the job's values, so
/// it carries exactly the same defaults as a monolith run; the delivery is acked by the worker host, not here.
/// </summary>
public class BusJobQueueHandler : IJobQueueHandler
{
    private IApi? _api;
    private IMessagingHandler? _messagingHandler;

    /// <summary>The job this process runs (after <see cref="NextJob"/>).</summary>
    public JobSubmitted? Job { get; private set; }

    public void Initialize(IApi api, IMessagingHandler messagingHandler)
    {
        _api = api;
        _messagingHandler = messagingHandler;
    }

    public AlgorithmNodePacket NextJob(out string algorithmPath)
    {
        var job = BusJobContext.LoadDispatchedJob()
            ?? throw new InvalidOperationException($"No dispatched job: set '{BusSettings.JobFile}' or '{BusSettings.Job}'");
        Job = job;
        var context = BusJobContext.From(job);
        EngineBus.Job = context;

        var (algorithm, name, parameters) = job.Kind switch
        {
            JobKind.Backtest when job.Backtest is { } spec => (spec.Algorithm, spec.Name, spec.Parameters),
            JobKind.Live when job.Live is { } spec => (spec.Algorithm, spec.Name, spec.Parameters),
            _ => throw new NotSupportedException($"Job {job.JobId:D}: kind {job.Kind} without its spec cannot run in the engine"),
        };

        MaterializeAlgorithm(job, algorithm);
        Config.Set("algorithm-id", job.JobId.ToString("D"));
        Config.Set("parameters", JsonConvert.SerializeObject(parameters ?? new Dictionary<string, string>()));
        if (name != null)
        {
            Config.Set("backtest-name", name);
        }
        if (job.ParentJobId is { } parent)
        {
            Config.Set("optimization-id", parent.ToString("D"));
        }
        if (job.Live is { } live)
        {
            Config.Set("live-mode-brokerage", live.Brokerage);
            if (live.DataQueueHandlers is { Count: > 0 } handlers)
            {
                Config.Set("data-queue-handler", JsonConvert.SerializeObject(handlers
                    .Select(h => string.Equals(h, BusDataQueueHandler.Alias, StringComparison.OrdinalIgnoreCase) ? typeof(BusDataQueueHandler).FullName! : h).ToList()));
            }
        }

        // JobQueue reads its config when constructed: create it after the job's values are in place.
        var inner = new JobQueue();
        inner.Initialize(_api!, _messagingHandler!);
        var packet = inner.NextJob(out algorithmPath);

        if (packet is BacktestNodePacket backtest && job.Backtest is { } backtestSpec)
        {
            if (backtestSpec.MaxRamMb is { } ramMb)
            {
                backtest.Controls.RamAllocation = ramMb;
            }
            if (backtestSpec.StartDate is { } start)
            {
                backtest.PeriodStart = start.ToDateTime(TimeOnly.MinValue);
            }
            if (backtestSpec.EndDate is { } end)
            {
                backtest.PeriodFinish = end.ToDateTime(TimeOnly.MinValue);
            }
            if (backtestSpec.Cash is { } cash)
            {
                backtest.CashAmount = new CashAmount(cash, Currencies.USD);
            }
        }

        Log.Trace($"BusJobQueueHandler.NextJob(): job {job.JobId:D} attempt {job.Attempt} ({job.Kind}) -> {algorithmPath}");
        return packet;
    }

    /// <summary>No-op: the worker host acks the RabbitMQ delivery once the child process exits (ADR 0001).</summary>
    public void AcknowledgeJob(AlgorithmNodePacket job)
    {
        Log.Trace($"BusJobQueueHandler.AcknowledgeJob(): {job.AlgorithmId} done");
    }

    private static void MaterializeAlgorithm(JobSubmitted job, AlgorithmRef algorithm)
    {
        switch (algorithm)
        {
            case BuiltinAlgorithmRef builtin:
                Config.Set("algorithm-type-name", builtin.TypeName);
                Config.Set("algorithm-language", Language.CSharp.ToString());
                Config.Set("algorithm-location", BusSettings.Get(BusSettings.BuiltinAlgorithmLocation) ?? "QuantConnect.Algorithm.CSharp.dll");
                break;

            case UploadedAlgorithmRef upload:
                var folder = Path.Combine(BusSettings.Get(BusSettings.WorkFolder) ?? "algorithm", job.JobId.ToString("D"), job.Attempt.ToString());
                var path = Path.Combine(folder, Path.GetFileName(upload.Object.Key));
                EngineBus.Store.DownloadVerifiedAsync(upload.Object, path).GetAwaiter().GetResult();
                Config.Set("algorithm-location", path);
                Config.Set("algorithm-language", (upload.Language == ContractLanguage.Python ? Language.Python : Language.CSharp).ToString());
                Config.Set("algorithm-type-name", upload.TypeName ?? Path.GetFileNameWithoutExtension(path));
                break;

            default:
                throw new NotSupportedException($"Algorithm source {algorithm.GetType().Name}");
        }
    }
}
