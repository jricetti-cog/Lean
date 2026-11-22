using System.IO.Compression;
using System.Reflection;
using System.Text.RegularExpressions;
using QuantConnect.Algorithm.CSharp;
using QuantConnect.Configuration;
using QuantConnect.Data.Auxiliary;
using QuantConnect.Util;
using QuantConnect.Interfaces;

namespace QuantConnect.Tests.Services.Parity;

/// <summary>One regression algorithm in one language; <see cref="SkipReason"/> is set when it cannot run on the repo's data.</summary>
public sealed record ParityCase(string Algorithm, Language Language, IRegressionAlgorithmDefinition Definition, string? SkipReason)
{
    /// <summary>Same name LEAN's <c>RegressionTests.AlgorithmStatisticsRegression</c> gives the case (e.g. <c>CSharp/BasicTemplateAlgorithm</c>).</summary>
    public string Name => $"{Language}/{Algorithm}";

    public override string ToString() => Name;
}

/// <summary>
/// The regression set, discovered exactly like <c>Tests/RegressionTests.cs</c>: every non-abstract
/// <see cref="IRegressionAlgorithmDefinition"/> in <c>QuantConnect.Algorithm.CSharp</c> with a default constructor, once
/// per language in <see cref="IRegressionAlgorithmDefinition.Languages"/>. Definitions LEAN's local suite excludes
/// (<c>CanRunLocally == false</c>) are kept as skipped cases with the reason instead of being dropped.
/// </summary>
public static class ParityCatalog
{
    public const string NeedsCloudData =
        "CanRunLocally = false: needs data that is not in the repo's Data/ folder (LEAN's local regression suite excludes it too)";

    private static readonly Lazy<IReadOnlyList<ParityCase>> _all = new(Discover);

    public static IReadOnlyList<ParityCase> All => _all.Value;

    public static string RepoRoot { get; } = FindRepoRoot();

    public static string PythonAlgorithmsFolder => Path.Combine(RepoRoot, "Algorithm.Python");

    private static IReadOnlyList<ParityCase> Discover()
    {
        // Some definitions read the market-hours/symbol-properties databases in their constructors.
        Config.Set("data-folder", Path.Combine(RepoRoot, "Data") + Path.DirectorySeparatorChar);
        // ...and resolve symbols through the composed providers, set up like Tests/TestGlobals.Initialize.
        var dataProvider = new LocalFileDataProvider();
        var mapFiles = new LocalDiskMapFileProvider();
        var factorFiles = new LocalDiskFactorFileProvider();
        mapFiles.Initialize(dataProvider);
        factorFiles.Initialize(mapFiles, dataProvider);
        Composer.Instance.AddPart<IMapFileProvider>(mapFiles);
        Composer.Instance.AddPart<IFactorFileProvider>(factorFiles);
        var languages = ParitySettings.Languages;
        var only = ParitySettings.Only;
        var cases = new List<ParityCase>();
        foreach (var type in typeof(BasicTemplateAlgorithm).Assembly.GetTypes())
        {
            if (!typeof(IRegressionAlgorithmDefinition).IsAssignableFrom(type) || type.IsAbstract || type.GetConstructor(Type.EmptyTypes) == null)
            {
                continue;
            }
            var definition = (IRegressionAlgorithmDefinition)Activator.CreateInstance(type)!;
            foreach (var language in definition.Languages.Where(languages.Contains))
            {
                var name = type.Name;
                if (only.Count > 0 && !only.Contains(name) && !only.Contains($"{language}/{name}")) continue;
                string? skip = null;
                if (!definition.CanRunLocally)
                {
                    skip = NeedsCloudData;
                }
                else if (language == Language.Python && !File.Exists(Path.Combine(PythonAlgorithmsFolder, name + ".py")))
                {
                    skip = $"Languages includes Python but Algorithm.Python/{name}.py does not exist";
                }
                cases.Add(new ParityCase(name, language, definition, skip));
            }
        }
        return cases.OrderBy(c => c.Language).ThenBy(c => c.Algorithm, StringComparer.Ordinal).ToList();
    }

    private static string FindRepoRoot()
    {
        if (Environment.GetEnvironmentVariable("PARITY_REPO_ROOT") is { Length: > 0 } configured) return Path.GetFullPath(configured);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "QuantConnect.Lean.sln"))) return dir.FullName;
        }
        throw new DirectoryNotFoundException("Cannot find QuantConnect.Lean.sln above " + AppContext.BaseDirectory + "; set PARITY_REPO_ROOT");
    }
}

/// <summary>
/// Packages a Python regression algorithm as a gateway project zip: the algorithm becomes <c>main.py</c> and every
/// <c>Algorithm.Python</c> module it imports (transitively) rides along, as they share one folder in the monolith.
/// </summary>
public static class PythonPackage
{
    private static readonly Regex ImportLine = new(@"^\s*(?:from\s+([A-Za-z_]\w*)[\w.]*\s+import\b|import\s+([A-Za-z_][\w.]*(?:\s*,\s*[A-Za-z_][\w.]*)*))",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public static byte[] Build(string algorithm, out IReadOnlyList<string> modules)
    {
        var folder = ParityCatalog.PythonAlgorithmsFolder;
        var included = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(Imports(File.ReadAllText(Path.Combine(folder, algorithm + ".py"))));
        while (pending.TryPop(out var module))
        {
            if (!File.Exists(Path.Combine(folder, module + ".py")) || !included.Add(module)) continue;
            foreach (var next in Imports(File.ReadAllText(Path.Combine(folder, module + ".py")))) pending.Push(next);
        }
        modules = included.ToList();
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntryFromFile(Path.Combine(folder, algorithm + ".py"), "main.py");
            foreach (var module in included) zip.CreateEntryFromFile(Path.Combine(folder, module + ".py"), module + ".py");
        }
        return buffer.ToArray();
    }

    private static IEnumerable<string> Imports(string source)
    {
        foreach (Match match in ImportLine.Matches(source))
        {
            if (match.Groups[1].Success) yield return match.Groups[1].Value;
            else foreach (var part in match.Groups[2].Value.Split(',')) yield return part.Trim().Split('.')[0];
        }
    }
}

/// <summary>Reads data files straight from disk (the Engine's <c>DefaultDataProvider</c>, without referencing the Engine).</summary>
internal sealed class LocalFileDataProvider : IDataProvider
{
    public event EventHandler<DataProviderNewDataRequestEventArgs>? NewDataRequest;

    public Stream? Fetch(string key)
    {
        var exists = File.Exists(key);
        NewDataRequest?.Invoke(this, new DataProviderNewDataRequestEventArgs(key, exists, exists ? null : "not found"));
        return exists ? File.OpenRead(key) : null;
    }
}
