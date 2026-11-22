using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using QuantConnect.Services.Contracts;

namespace QuantConnect.Services.Report;

/// <summary>Input files of one render, already downloaded into <see cref="WorkDirectory"/>.</summary>
public sealed record ReportRenderRequest
{
    public required Guid JobId { get; init; }
    public required int Attempt { get; init; }
    public required ReportSpec Spec { get; init; }
    public required string WorkDirectory { get; init; }
    public required string BacktestResultFile { get; init; }
    public string? LiveResultFile { get; init; }
    public bool Pdf { get; init; }
}

public sealed record ReportRenderOutput
{
    public required byte[] Html { get; init; }
    public byte[]? Pdf { get; init; }
    public required string Log { get; init; }
    public int? ExitCode { get; init; }
}

public interface IReportRenderer
{
    Task<ReportRenderOutput> RenderAsync(ReportRenderRequest request, CancellationToken cancellationToken);
}

/// <summary>A render that failed; <see cref="Retryable"/> failures leave the retry decision to the orchestrator.</summary>
public sealed class ReportFailedException(ErrorCode code, string message, bool retryable, Exception? inner = null, int? exitCode = null)
    : Exception(message, inner)
{
    public ErrorCode Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public int? ExitCode { get; } = exitCode;
}

/// <summary>
/// Runs LEAN's unchanged report pipeline (<c>QuantConnect.Report.dll</c>: pythonnet, <c>Report.Compile</c>,
/// <c>xvfb-run wkhtmltopdf</c> for PDF) as a child process per render, so a pythonnet crash cannot take the host
/// down. The child's <c>config.json</c> is the shipped <c>config.example.json</c> with the job's inputs and an
/// absolute <c>data-folder</c>.
/// </summary>
public sealed class LeanReportRenderer(ReportSettings settings, ILogger<LeanReportRenderer> logger) : IReportRenderer
{
    public const string ReportAssembly = "QuantConnect.Report.dll";
    private const int MaxLogChars = 512 * 1024;
    private static readonly JsonDocumentOptions LeanConfigJson = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public async Task<ReportRenderOutput> RenderAsync(ReportRenderRequest request, CancellationToken cancellationToken)
    {
        var reportDirectory = Path.GetFullPath(settings.ReportDirectory);
        var assembly = Path.Combine(reportDirectory, ReportAssembly);
        if (!File.Exists(assembly))
        {
            throw new ReportFailedException(ErrorCode.Internal, $"{assembly} not found (Report:Directory)", retryable: true);
        }
        // Report.cs reads css/report.css relative to the working directory.
        CopyDirectory(Path.Combine(reportDirectory, "css"), Path.Combine(request.WorkDirectory, "css"));
        var destination = Path.Combine(request.WorkDirectory, "report.html");
        var configFile = Path.Combine(request.WorkDirectory, "config.json");
        await File.WriteAllTextAsync(configFile, BuildConfig(reportDirectory, request, destination).ToJsonString(), cancellationToken).ConfigureAwait(false);

        var start = new ProcessStartInfo(settings.DotnetPath)
        {
            WorkingDirectory = request.WorkDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(assembly);
        // ReportCharts.py lives next to the assembly; the host's PYTHONPATH points at the Launcher.
        var pythonPath = Environment.GetEnvironmentVariable("PYTHONPATH");
        start.Environment["PYTHONPATH"] = string.IsNullOrEmpty(pythonPath) ? reportDirectory : $"{reportDirectory}{Path.PathSeparator}{pythonPath}";

        var log = new StringBuilder();
        void Append(string? line)
        {
            if (line == null) return;
            lock (log)
            {
                if (log.Length < MaxLogChars) log.AppendLine(line);
            }
        }

        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        logger.LogInformation("Rendering report {JobId} attempt {Attempt} in {WorkDirectory}", request.JobId, request.Attempt, request.WorkDirectory);
        if (!process.Start())
        {
            throw new ReportFailedException(ErrorCode.Internal, $"Could not start {settings.DotnetPath} {assembly}", retryable: true);
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.RenderTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new ReportFailedException(ErrorCode.Timeout, $"Report render exceeded {settings.RenderTimeout}", retryable: true);
        }
        process.WaitForExit();

        string text;
        lock (log)
        {
            text = log.ToString();
        }
        // Success is the rendered file, not the exit code (ADR 0005: pythonnet hosts can exit -6 after finishing).
        if (!File.Exists(destination) || new FileInfo(destination).Length == 0)
        {
            throw new ReportFailedException(ErrorCode.RuntimeError,
                $"Report exited with code {process.ExitCode} without writing report.html: {Tail(text, 2000)}", retryable: true, exitCode: process.ExitCode);
        }
        byte[]? pdf = null;
        var pdfFile = Path.ChangeExtension(destination, ".pdf");
        if (request.Pdf)
        {
            if (File.Exists(pdfFile) && new FileInfo(pdfFile).Length > 0)
            {
                pdf = await File.ReadAllBytesAsync(pdfFile, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                logger.LogWarning("Report {JobId} attempt {Attempt}: PDF requested but the wkhtmltopdf pipeline produced none", request.JobId, request.Attempt);
            }
        }
        return new ReportRenderOutput
        {
            Html = await File.ReadAllBytesAsync(destination, cancellationToken).ConfigureAwait(false),
            Pdf = pdf,
            Log = text,
            ExitCode = process.ExitCode,
        };
    }

    /// <summary>The child's <c>config.json</c>: <c>config.example.json</c> (handlers) plus this job's inputs.</summary>
    public JsonObject BuildConfig(string reportDirectory, ReportRenderRequest request, string destination)
    {
        var example = Path.Combine(reportDirectory, "config.example.json");
        var config = File.Exists(example) && JsonNode.Parse(File.ReadAllText(example), documentOptions: LeanConfigJson) is JsonObject parsed
            ? parsed
            : new JsonObject { ["environment"] = "report" };
        var spec = request.Spec;
        config["data-folder"] = settings.DataFolder;
        config["strategy-name"] = spec.StrategyName ?? $"Backtest {spec.SourceJobId:D}";
        config["strategy-description"] = spec.StrategyDescription ?? string.Empty;
        config["strategy-version"] = spec.StrategyVersion ?? string.Empty;
        config["backtest-data-source-file"] = request.BacktestResultFile;
        config["live-data-source-file"] = request.LiveResultFile ?? string.Empty;
        config["report-destination"] = destination;
        config["report-format"] = request.Pdf ? "pdf" : string.Empty;
        config["report-css-override-file"] = Path.Combine(reportDirectory, "css", "report_override.css");
        config["report-html-custom-file"] = Path.Combine(reportDirectory, "template.html");
        config["close-automatically"] = true;
        config["composer-dll-directory"] = reportDirectory;
        return config;
    }

    private static void CopyDirectory(string source, string target)
    {
        if (!Directory.Exists(source))
        {
            throw new ReportFailedException(ErrorCode.Internal, $"{source} not found (Report:Directory)", retryable: true);
        }
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
    }

    private static string Tail(string text, int chars) => text.Length <= chars ? text.Trim() : text[^chars..].Trim();
}
