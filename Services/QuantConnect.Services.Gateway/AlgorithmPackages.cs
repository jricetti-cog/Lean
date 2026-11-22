using System.IO.Compression;
using QuantConnect.Services.Contracts;

namespace QuantConnect.Services.Gateway;

/// <summary>What an uploaded algorithm file is, decided from its name and (for zips) its entries.</summary>
public sealed record AlgorithmPackage(AlgorithmLanguage Language, string ContentType);

/// <summary>
/// Accepts a Python file (<c>.py</c>), C# source (<c>.cs</c>), a compiled C# algorithm (<c>.dll</c>) or a project zip.
/// A zip is Python when it has a <c>main.py</c> (at the root or in one top-level folder), otherwise C# when it has
/// <c>.cs</c> files. Zip entries must stay inside the archive and the expanded size is capped.
/// </summary>
public static class AlgorithmPackages
{
    public const string PythonSource = "text/x-python";
    public const string CSharpSource = "text/x-csharp";
    public const string Assembly = "application/vnd.microsoft.portable-executable";
    public const string ProjectZip = "application/zip";
    public const int MaxZipEntries = 2000;
    public const long MaxZipExpandedBytes = 200L * 1024 * 1024;

    public static AlgorithmPackage Inspect(string fileName, byte[] content, AlgorithmLanguage? declared, out string? error)
    {
        error = null;
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        AlgorithmPackage? package = extension switch
        {
            ".py" => new(AlgorithmLanguage.Python, PythonSource),
            ".cs" => new(AlgorithmLanguage.CSharp, CSharpSource),
            ".dll" => new(AlgorithmLanguage.CSharp, Assembly),
            ".zip" => InspectZip(content, out error),
            _ => null,
        };
        if (package == null)
        {
            error ??= $"unsupported algorithm file '{fileName}': upload a .py or .cs source file, a compiled .dll or a project .zip";
            return new(AlgorithmLanguage.CSharp, ProjectZip);
        }
        if (declared != null && declared != package.Language)
        {
            error = $"language {declared} does not match the uploaded {package.Language} algorithm";
        }
        return package;
    }

    private static AlgorithmPackage? InspectZip(byte[] content, out string? error)
    {
        error = null;
        try
        {
            using var zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxZipEntries) return Fail($"project zips are limited to {MaxZipEntries} entries", out error);
            long expanded = 0;
            var files = new List<string>();
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(part => part == ".."))
                {
                    return Fail($"zip entry '{entry.FullName}' escapes the project folder", out error);
                }
                expanded += entry.Length;
                if (expanded > MaxZipExpandedBytes) return Fail($"project zips are limited to {MaxZipExpandedBytes} expanded bytes", out error);
                if (!name.EndsWith('/')) files.Add(name);
            }
            var roots = files.Select(f => f.Contains('/') ? f[..f.IndexOf('/')] : "").Distinct().ToList();
            var prefix = roots.Count == 1 && roots[0] != "" ? roots[0] + "/" : "";
            if (files.Any(f => f == prefix + "main.py")) return new(AlgorithmLanguage.Python, ProjectZip);
            if (files.Any(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))) return new(AlgorithmLanguage.CSharp, ProjectZip);
            return Fail("project zip has neither a main.py nor any .cs files", out error);
        }
        catch (InvalidDataException e)
        {
            return Fail($"not a valid zip archive: {e.Message}", out error);
        }
    }

    private static AlgorithmPackage? Fail(string message, out string? error)
    {
        error = message;
        return null;
    }
}
