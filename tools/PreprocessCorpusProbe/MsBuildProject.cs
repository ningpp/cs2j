// MsBuild-evaluated project compilation builder.
//
// Same evaluation pipeline as tools/LinqRouteProbe/Program.cs (proven on the
// D:\csharp-sdk training set): per-csproj `dotnet msbuild` evaluation of
// DefineConstants/TargetFramework/NoWarn/Nullable, -getItem:Compile compile
// items, ResolveAssemblyReferences reference set, plus SDK-generated global
// usings under obj (standard layout) or artifacts/obj (Arcade layout used by
// efcore/runtime). TPA is deliberately NOT merged for SDK-style projects.
//
// All numbers stay MEASURED: projects that fail evaluation or miss their
// reference set are reported honestly instead of being silently dropped.

using System.Diagnostics;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

internal sealed record ProjectBuild(
    string Unit,
    string ProjectPath,
    List<SyntaxTree> Trees,
    List<MetadataReference> References,
    NullableContextOptions Nullable,
    Dictionary<string, ReportDiagnostic> Suppressed,
    int MissingCompileItems)
{
    public string? UnavailableReason { get; init; }
    public Dictionary<string, int> EvalErrorCodes { get; } = new(StringComparer.Ordinal);
}

internal static class MsBuildProject
{
    public static List<ProjectBuild> Enumerate(
        string root,
        IReadOnlyList<string>? samples,
        IReadOnlyList<string>? projectFilters,
        int? limit)
    {
        var builds = new List<ProjectBuild>();
        var searchRoots = new List<string>();

        if (samples is { Count: > 0 })
        {
            foreach (var sample in samples)
                searchRoots.Add(Path.GetFullPath(Path.Combine(root, sample)));
        }
        else
        {
            searchRoots.Add(Path.GetFullPath(root));
        }

        var projectPaths = new List<string>();
        foreach (var searchRoot in searchRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(searchRoot))
            {
                Console.Error.WriteLine($"[probe] missing root: {searchRoot}");
                continue;
            }

            projectPaths.AddRange(Directory.EnumerateFiles(searchRoot, "*.csproj", SearchOption.AllDirectories)
                .Where(f =>
                {
                    var n = f.Replace('\\', '/');
                    return !n.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                        && !n.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                        && !n.Contains("/artifacts/", StringComparison.OrdinalIgnoreCase);
                }));
        }

        projectPaths = projectPaths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (projectFilters is { Count: > 0 })
        {
            projectPaths = projectPaths
                .Where(p => projectFilters.Any(f =>
                    p.Contains(f, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        if (limit is > 0)
            projectPaths = projectPaths.Take(limit.Value).ToList();

        foreach (var projectPath in projectPaths)
        {
            var unit = Path.GetRelativePath(root, projectPath).Replace('\\', '/');
            try
            {
                builds.Add(BuildProject(root, projectPath, unit));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[probe] project unavailable: {unit}: {ex.Message}");
                builds.Add(new ProjectBuild(
                    unit, projectPath, new List<SyntaxTree>(), new List<MetadataReference>(),
                    NullableContextOptions.Disable, new Dictionary<string, ReportDiagnostic>(), 0)
                {
                    UnavailableReason = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message,
                });
            }
        }

        return builds;
    }

    private static ProjectBuild BuildProject(string root, string projectPath, string unit)
    {
        projectPath = Path.GetFullPath(projectPath);
        var projectDir = Path.GetDirectoryName(projectPath)!;
        var projectName = Path.GetFileNameWithoutExtension(projectPath);

        var properties = EvaluateProperties(projectPath,
            "DefineConstants", "TargetFramework", "NoWarn", "Nullable");
        var compileItems = EvaluateCompileItems(projectPath);

        var defines = (properties.GetValueOrDefault("DefineConstants") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Concat(TfmDefines(properties.GetValueOrDefault("TargetFramework") ?? ""))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var suppressedDiagnostics = (properties.GetValueOrDefault("NoWarn") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(c => c, _ => ReportDiagnostic.Suppress, StringComparer.OrdinalIgnoreCase);
        var nullable = (properties.GetValueOrDefault("Nullable") ?? "").Trim().ToLowerInvariant() switch
        {
            "enable" => NullableContextOptions.Enable,
            "annotations" => NullableContextOptions.Annotations,
            "warnings" => NullableContextOptions.Warnings,
            _ => NullableContextOptions.Disable,
        };

        var trees = new List<SyntaxTree>();
        var missing = 0;
        foreach (var identity in compileItems)
        {
            var fullPath = Path.GetFullPath(Path.Combine(projectDir, identity));
            if (!File.Exists(fullPath))
            {
                missing++;
                continue;
            }

            var tree = ParseFile(fullPath, defines);
            if (tree is not null)
                trees.Add(tree);
        }

        foreach (var globalUsing in FindGlobalUsings(projectDir, projectName))
        {
            var tree = ParseFile(globalUsing, defines);
            if (tree is not null)
                trees.Add(tree);
        }

        var references = BuildReferences(projectPath);

        return new ProjectBuild(
            unit, projectPath, trees, references, nullable, suppressedDiagnostics, missing);
    }

    private static IEnumerable<string> FindGlobalUsings(string projectDir, string projectName)
    {
        // Standard SDK layout: <projectDir>/obj/**/*.GlobalUsings.g.cs
        var localObj = Path.Combine(projectDir, "obj");
        if (Directory.Exists(localObj))
        {
            foreach (var f in Directory.EnumerateFiles(
                localObj, "*.GlobalUsings.g.cs", SearchOption.AllDirectories))
                yield return f;
        }

        // Arcade layout (efcore/runtime): walk up to the repo artifacts/obj/<name>.
        var current = new DirectoryInfo(projectDir);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "artifacts", "obj", projectName);
            if (Directory.Exists(candidate))
            {
                foreach (var f in Directory.EnumerateFiles(
                    candidate, "*.GlobalUsings.g.cs", SearchOption.AllDirectories))
                    yield return f;
            }

            current = current.Parent;
        }
    }

    private static List<MetadataReference> BuildReferences(string projectPath)
    {
        return EvaluateReferencePaths(projectPath)
            .Where(File.Exists)
            .GroupBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase)
            .Select(g => (MetadataReference)MetadataReference.CreateFromFile(g.First()))
            .ToList();
    }

    private static List<string> EvaluateReferencePaths(string projectPath)
    {
        var (json, stderr, exit) = RunMsBuild(
            projectPath, "-target:ResolveAssemblyReferences -getItem:ReferencePath");
        if (exit != 0)
            throw new InvalidOperationException("ResolveAssemblyReferences failed: " + stderr.Take(300).Flatten());
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("Items", out var items)
            || !items.TryGetProperty("ReferencePath", out var references))
            return new List<string>();

        return references.EnumerateArray()
            .Select(e => (e.TryGetProperty("FullPath", out var full) ? full.GetString() : null)
                ?? e.GetProperty("Identity").GetString()!)
            .ToList();
    }

    private static Dictionary<string, string> EvaluateProperties(string projectPath, params string[] names)
    {
        var argumentList = string.Join(" ", names.Select(n => $"-getProperty:{n}"));
        var (json, _, exit) = RunMsBuild(projectPath, argumentList);
        if (exit != 0)
            throw new InvalidOperationException("msbuild -getProperty failed");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("Properties").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
    }

    private static List<string> EvaluateCompileItems(string projectPath)
    {
        var (json, _, exit) = RunMsBuild(projectPath, "-getItem:Compile");
        if (exit != 0)
            throw new InvalidOperationException("msbuild -getItem:Compile failed");
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("Items", out var items)
            || !items.TryGetProperty("Compile", out var compile))
            return new List<string>();
        return compile.EnumerateArray()
            .Select(e => e.GetProperty("Identity").GetString()!)
            .ToList();
    }

    private static (string StdOut, string StdErr, int Exit) RunMsBuild(string projectPath, string arguments)
    {
        var psi = new ProcessStartInfo("dotnet",
            $"msbuild \"{projectPath}\" {arguments} --nologo")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit(120_000);
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("msbuild evaluation timed out (120s)");
        }

        return (stdoutTask.Result, stderrTask.Result, process.ExitCode);
    }

    private static IEnumerable<string> TfmDefines(string tfm)
    {
        if (tfm.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase))
        {
            var ver = tfm["netstandard".Length..].Split('.');
            yield return "NETSTANDARD";
            yield return $"NETSTANDARD{ver[0]}_{ver[1]}";
            yield return $"NETSTANDARD{ver[0]}_{ver[1]}_OR_GREATER";
            yield break;
        }

        if (tfm.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase))
        {
            var ver = tfm["netcoreapp".Length..].Split('.');
            yield return "NETCOREAPP";
            yield return $"NETCOREAPP{ver[0]}_{ver[1]}";
            yield return $"NETCOREAPP{ver[0]}_{ver[1]}_OR_GREATER";
            yield break;
        }

        if (tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase)
            && tfm.Length > 3 && char.IsDigit(tfm[3]))
        {
            var ver = tfm["net".Length..].Split('-')[0].Split('.');
            yield return "NET";
            yield return "NETCOREAPP";
            yield return $"NET{ver[0]}";
            yield return $"NET{ver[0]}_{ver[1]}";
            yield return $"NET{ver[0]}_{ver[1]}_OR_GREATER";
        }
    }

    private static SyntaxTree? ParseFile(string path, string[] defines)
    {
        try
        {
            return CSharpSyntaxTree.ParseText(
                File.ReadAllText(path),
                new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: defines),
                path: path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[probe] parse failed {path}: {ex.Message}");
            return null;
        }
    }
}

internal static class StringEnumerableExtensions
{
    public static string Flatten(this IEnumerable<char> chars) => new(chars.ToArray());
}
