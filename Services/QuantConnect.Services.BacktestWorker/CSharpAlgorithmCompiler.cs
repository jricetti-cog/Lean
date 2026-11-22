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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using QuantConnect.Services.Contracts;

namespace QuantConnect.Services.BacktestWorker;

/// <summary>Outcome of compiling an uploaded C# algorithm: the assembly and algorithm type to load, or the compiler diagnostics.</summary>
public sealed record CompileResult(string? AssemblyPath, string? TypeName, IReadOnlyList<string> Diagnostics)
{
    public bool Success => AssemblyPath != null && TypeName != null;
}

/// <summary>
/// Compiles uploaded C# (<c>.cs</c> source or a project <c>.zip</c> of sources) with Roslyn against the LEAN assemblies the
/// child Launcher runs with (<see cref="WorkerOptions.LauncherDirectory"/>) plus the shared framework, in process and
/// before the engine starts, so a compile error fails the job with <c>compile-error</c> without spawning an engine.
/// </summary>
public static class CSharpAlgorithmCompiler
{
    private const int MaxDiagnostics = 50;
    private const string AlgorithmInterface = "QuantConnect.Interfaces.IAlgorithm";
    private static readonly ConcurrentDictionary<string, IReadOnlyList<MetadataReference>> References = new(StringComparer.Ordinal);
    private static readonly string[] IgnoredFolders = ["bin", "obj"];

    /// <summary>True for uploads the worker compiles (C# source or zip); a C# <c>.dll</c> runs as uploaded.</summary>
    public static bool Handles(UploadedAlgorithmRef upload) =>
        upload.Language == AlgorithmLanguage.CSharp && Path.GetExtension(upload.Object.Key).ToLowerInvariant() is ".cs" or ".zip";

    /// <summary>The <c>.cs</c> files of an upload: the file itself, or every <c>.cs</c> entry of a zip outside <c>bin/</c> and <c>obj/</c>.</summary>
    public static IReadOnlyList<(string Path, string Text)> ReadSources(string fileName, byte[] content)
    {
        if (!string.Equals(Path.GetExtension(fileName), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            return [(Path.GetFileName(fileName), Decode(content))];
        }
        using var archive = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
        var sources = new List<(string, string)>();
        foreach (var entry in archive.Entries.OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            var name = entry.FullName.Replace('\\', '/');
            if (!name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || name.Split('/').Any(part => IgnoredFolders.Contains(part, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            sources.Add((name, Decode(buffer.ToArray())));
        }
        return sources;
    }

    /// <summary>
    /// Compiles <paramref name="sources"/> into <paramref name="outputDirectory"/>/<paramref name="assemblyName"/>.dll and picks the
    /// algorithm class: <paramref name="typeName"/> when given (full or simple name), otherwise the only concrete public
    /// <c>IAlgorithm</c> with a parameterless constructor (or the one named like <paramref name="fileName"/>).
    /// </summary>
    public static CompileResult Compile(IReadOnlyList<(string Path, string Text)> sources, string referenceDirectory, string outputDirectory,
        string assemblyName, string? typeName = null, string? fileName = null)
    {
        if (sources.Count == 0)
        {
            return Failure("error: the upload contains no .cs source file");
        }
        var parse = new CSharpParseOptions(LanguageVersion.Latest);
        var trees = sources.Select(s => CSharpSyntaxTree.ParseText(s.Text, parse, s.Path, Encoding.UTF8)).ToList();
        var compilation = CSharpCompilation.Create(assemblyName, trees, ReferencesFor(referenceDirectory),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release,
                nullableContextOptions: NullableContextOptions.Disable, concurrentBuild: true));

        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            return new CompileResult(null, null, Format(errors));
        }

        var algorithm = compilation.GetTypeByMetadataName(AlgorithmInterface);
        if (algorithm == null)
        {
            return Failure($"error: {AlgorithmInterface} is not available in {referenceDirectory}");
        }
        var candidates = trees.SelectMany(tree =>
            {
                var model = compilation.GetSemanticModel(tree);
                return tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Select(c => model.GetDeclaredSymbol(c));
            })
            .OfType<INamedTypeSymbol>()
            .Where(t => !t.IsAbstract && !t.IsGenericType && t.DeclaredAccessibility == Accessibility.Public
                && t.AllInterfaces.Contains(algorithm, SymbolEqualityComparer.Default)
                && t.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public))
            .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
            .Select(t => t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty, StringComparison.Ordinal))
            .ToList();

        var selected = Select(candidates, typeName, fileName, out var problem);
        if (selected == null)
        {
            return Failure(problem!);
        }

        Directory.CreateDirectory(outputDirectory);
        var assemblyPath = Path.Combine(outputDirectory, assemblyName + ".dll");
        using (var dll = File.Create(assemblyPath))
        using (var pdb = File.Create(Path.ChangeExtension(assemblyPath, ".pdb")))
        {
            var emitted = compilation.Emit(dll, pdb);
            if (!emitted.Success)
            {
                return new CompileResult(null, null, Format(emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList()));
            }
        }
        return new CompileResult(assemblyPath, selected, []);
    }

    private static string? Select(List<string> candidates, string? typeName, string? fileName, out string? problem)
    {
        problem = null;
        if (!string.IsNullOrWhiteSpace(typeName))
        {
            var suffix = typeName.Contains('.') ? typeName : "." + typeName;
            var match = candidates.Where(c => c == typeName || c.EndsWith(suffix, StringComparison.Ordinal)).ToList();
            if (match.Count == 1) return match[0];
            problem = $"error: algorithm class '{typeName}' not found among the public IAlgorithm classes with a parameterless constructor" +
                (candidates.Count > 0 ? $" ({string.Join(", ", candidates)})" : string.Empty);
            return null;
        }
        if (candidates.Count == 1) return candidates[0];
        if (candidates.Count == 0)
        {
            problem = "error: no public non-abstract class deriving from QCAlgorithm (IAlgorithm) with a parameterless constructor was found";
            return null;
        }
        var named = fileName == null ? null : candidates.Where(c => c.EndsWith("." + Path.GetFileNameWithoutExtension(fileName), StringComparison.Ordinal)).ToList();
        if (named is { Count: 1 }) return named[0];
        problem = $"error: several algorithm classes found ({string.Join(", ", candidates)}); set typeName on the upload";
        return null;
    }

    /// <summary>Managed assemblies of the Launcher folder plus the running shared framework (framework copies win on name clashes).</summary>
    private static IReadOnlyList<MetadataReference> ReferencesFor(string directory) => References.GetOrAdd(Path.GetFullPath(directory), dir =>
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(dir))
        {
            foreach (var path in Directory.EnumerateFiles(dir, "*.dll"))
            {
                paths[Path.GetFileNameWithoutExtension(path)] = path;
            }
        }
        foreach (var path in ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.StartsWith("System.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
                || name is "mscorlib" or "netstandard" || !paths.ContainsKey(name))
            {
                paths[name] = path;
            }
        }
        return paths.Values.Where(IsManaged).Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
    });

    private static bool IsManaged(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            return reader.HasMetadata;
        }
        catch (Exception e) when (e is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static List<string> Format(List<Diagnostic> errors)
    {
        var lines = errors.Take(MaxDiagnostics).Select(d => d.ToString()).ToList();
        if (errors.Count > MaxDiagnostics)
        {
            lines.Add($"... and {errors.Count - MaxDiagnostics} more errors");
        }
        return lines;
    }

    private static CompileResult Failure(string message) => new(null, null, [message]);

    private static string Decode(byte[] content) => new UTF8Encoding(false).GetString(content).TrimStart('\uFEFF');
}
