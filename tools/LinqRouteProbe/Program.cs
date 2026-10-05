// C26 M0 baseline scanner (design §8 gate 4, §9 M0).
//
// Runs the prototype LinqRouteClassifier over:
//   sdk  --root D:\csharp-sdk            (sample source trees, TPA references)
//   ef   --project path\to\X.csproj       (msbuild-evaluated compile items,
//                                          built-output + NuGet asset refs)
//
// Numbers are MEASURED, never estimated. Unbound sites and compile errors are
// reported honestly: LC0010-shaped means a member-access invocation whose
// syntax name is a LINQ operator name but whose symbol did not bind — this is
// reporting only; routing never uses syntax names (D1).

using System.Diagnostics;
using System.Text.Json;
using CSharpToJava.Tests.LinqChannel.Probe;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class Program
{
    private const string Tfm = "net10.0";

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 2;
            }

            List<ScanResult> results;
            switch (args[0])
            {
                case "sdk":
                    results = ScanSdk(
                        GetOption(args, "--root") ?? @"D:\csharp-sdk",
                        GetOption(args, "--samples")?.Split(',', StringSplitOptions.RemoveEmptyEntries));
                    break;
                case "ef":
                {
                    var project = GetOption(args, "--project");
                    if (string.IsNullOrEmpty(project))
                        throw new InvalidOperationException("ef requires --project <csproj>");
                    var configuration = GetOption(args, "--configuration") ?? "Debug";
                    var rebuild = HasFlag(args, "--build");
                    results = new List<ScanResult> { ScanEfProject(project, configuration, rebuild) };
                    break;
                }
                default:
                    PrintUsage();
                    return 2;
            }

            Report(results);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("probe failed: " + ex);
            return 1;
        }
    }

    // ── SDK sample scan (mirrors the C19 M0 probe reference set) ───────────────

    private static readonly string[] DefaultSdkSamples =
        { "json", "regular-expressions", "uri", "xml-tiny" };

    private static List<ScanResult> ScanSdk(string root, IReadOnlyList<string>? samplesOverride)
    {
        var results = new List<ScanResult>();
        var references = TpaReferences();
        var samples = samplesOverride is { Count: > 0 } ? samplesOverride : DefaultSdkSamples;

        foreach (var sample in samples)
        {
            var sampleRoot = Path.Combine(root, sample);
            if (!Directory.Exists(sampleRoot))
            {
                Console.Error.WriteLine($"[sdk] missing sample dir: {sampleRoot}");
                continue;
            }

            var files = Directory.EnumerateFiles(sampleRoot, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Replace('\\', '/').Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                    && !f.Replace('\\', '/').Contains("/obj/", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var trees = files
                .Select(f => ParseFile(f, new[] { "NETCOREAPP" }))
                .Where(t => t is not null)
                .Select(t => t!)
                .ToList();

            var compilation = CSharpCompilation.Create(
                "c26-probe-sdk-" + Sanitize(sample),
                trees,
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

            results.Add(ScanCompilation($"sdk/{sample}", compilation, trees));
        }

        return results;
    }

    // ── EF Core project scan ───────────────────────────────────────────────────

    private static ScanResult ScanEfProject(string projectPath, string configuration, bool runBuild)
    {
        projectPath = Path.GetFullPath(projectPath);
        if (runBuild)
        {
            RunDotNet($"build \"{projectPath}\" -c {configuration} --nologo");
        }

        var projectDir = Path.GetDirectoryName(projectPath)!;
        var projectName = Path.GetFileNameWithoutExtension(projectPath);

        var properties = EvaluateProperties(projectPath, "DefineConstants", "TargetFramework", "NoWarn", "AssemblyName");
        var assemblyName = properties.GetValueOrDefault("AssemblyName");
        if (string.IsNullOrWhiteSpace(assemblyName))
            assemblyName = projectName;
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

        var trees = new List<SyntaxTree>();
        var missingFiles = 0;
        foreach (var identity in compileItems)
        {
            var fullPath = Path.GetFullPath(Path.Combine(projectDir, identity));
            if (!File.Exists(fullPath))
            {
                missingFiles++;
                Console.Error.WriteLine($"[ef] compile item not found: {fullPath}");
                continue;
            }

            var tree = ParseFile(fullPath, defines);
            if (tree is not null)
                trees.Add(tree);
        }

        // SDK-generated global usings (ImplicitUsings + project <Using> items).
        var objRoot = FindObjDir(projectDir, projectName, configuration);
        var globalUsings = Directory.EnumerateFiles(objRoot, "*.GlobalUsings.g.cs", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (globalUsings is not null)
        {
            var tree = ParseFile(globalUsings, defines);
            if (tree is not null)
                trees.Add(tree);
        }
        else
        {
            Console.WriteLine("[ef] note: no generated GlobalUsings file (expected when ImplicitUsings is disabled)");
        }

        var references = BuildEfReferences(projectPath, projectName, assemblyName!, configuration);

        var compilation = CSharpCompilation.Create(
            "c26-probe-ef-" + Sanitize(projectName),
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable,
                specificDiagnosticOptions: suppressedDiagnostics));

        var result = ScanCompilation($"ef/{projectName}", compilation, trees);
        result.MissingCompileItems = missingFiles;
        return result;
    }

    private static void RunDotNet(string arguments)
    {
        Console.WriteLine($"[ef] dotnet {arguments}");
        var psi = new ProcessStartInfo("dotnet", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"dotnet {arguments} exited {process.ExitCode}\n{process.StandardError.ReadToEnd()}");
    }

    private static Dictionary<string, string> EvaluateProperties(string projectPath, params string[] propertyNames)
    {
        var argumentList = string.Join(" ", propertyNames.Select(n => $"-getProperty:{n}"));
        var psi = new ProcessStartInfo("dotnet",
            $"msbuild \"{projectPath}\" {argumentList} --nologo")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        var json = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("dotnet msbuild -getProperty failed");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("Properties").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
    }

    private static List<string> EvaluateCompileItems(string projectPath)
    {
        var psi = new ProcessStartInfo("dotnet",
            $"msbuild \"{projectPath}\" -getItem:Compile --nologo")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        var json = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("dotnet msbuild -getItem:Compile failed");
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.GetProperty("Items").GetProperty("Compile");
        return items.EnumerateArray()
            .Select(e => e.GetProperty("Identity").GetString()!)
            .ToList();
    }

    private static string FindObjDir(string projectDir, string projectName, string configuration)
    {
        // Arcade layout: <repo-root>\artifacts\obj\<ProjectName>
        var current = new DirectoryInfo(projectDir);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "artifacts", "obj", projectName);
            if (Directory.Exists(candidate))
                return candidate;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException($"artifacts/obj/{projectName} not found above {projectDir}");
    }

    private static List<MetadataReference> BuildEfReferences(
        string projectPath, string projectName, string selfAssemblyName, string configuration)
    {
        var bySimpleName = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in TpaReferences())
            bySimpleName[SimpleName(reference.Display ?? "")] = reference;

        var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        var objDir = FindObjDir(projectDir, projectName, configuration);
        var assetsPath = Path.Combine(objDir, "project.assets.json");

        // Project outputs that are REAL ProjectReferences of the scanned
        // project (Arcade projects P2P via bin/placeholder compile entries).
        // Used to whitelist artifacts/bin: sibling projects that merely happen
        // to be built in the same checkout must not be referenced (their
        // metadata references the scanned project itself → CS0012 cascade).
        var allowedProjectAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? packagesPath = null;

        if (File.Exists(assetsPath))
        {
            using var assets = JsonDocument.Parse(File.ReadAllText(assetsPath));
            packagesPath = assets.RootElement.GetProperty("project").GetProperty("restore")
                .GetProperty("packagesPath").GetString();

            // NuGet compile-time assemblies.
            var targets = assets.RootElement.GetProperty("targets");
            foreach (var target in targets.EnumerateObject())
            {
                foreach (var library in target.Value.EnumerateObject())
                {
                    if (string.Equals(library.Value.TryGetProperty("type", out var typeElement)
                            ? typeElement.GetString()
                            : null, "project", StringComparison.OrdinalIgnoreCase))
                    {
                        var assemblyName = library.Name[..library.Name.IndexOf('/')];
                        if (!assemblyName.EndsWith(".Analyzers", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(assemblyName, selfAssemblyName, StringComparison.OrdinalIgnoreCase))
                        {
                            allowedProjectAssemblies.Add(assemblyName);
                        }
                    }

                    if (!library.Value.TryGetProperty("compile", out var compile))
                        continue;
                    foreach (var item in compile.EnumerateObject())
                    {
                        if (item.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                            && packagesPath is not null)
                        {
                            var full = Path.Combine(packagesPath, library.Name.ToLowerInvariant(),
                                item.Name.Replace('/', Path.DirectorySeparatorChar));
                            if (File.Exists(full))
                                bySimpleName.TryAdd(Path.GetFileNameWithoutExtension(full),
                                    MetadataReference.CreateFromFile(full));
                        }
                    }
                }
            }
        }
        else
        {
            Console.Error.WriteLine("[ef] project.assets.json not found at " + assetsPath);
        }

        // Resolve the whitelisted ProjectReference outputs from artifacts/bin.
        var current = new DirectoryInfo(projectDir);
        string? artifactsRoot = null;
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "artifacts", "bin")))
            {
                artifactsRoot = Path.Combine(current.FullName, "artifacts", "bin");
                break;
            }
            current = current.Parent;
        }

        if (artifactsRoot is not null)
        {
            foreach (var dll in Directory.EnumerateFiles(artifactsRoot, "*.dll", SearchOption.AllDirectories)
                .Where(p => p.Replace('\\', '/').Contains($"/{configuration}/", StringComparison.OrdinalIgnoreCase))
                .Where(p => allowedProjectAssemblies.Contains(Path.GetFileNameWithoutExtension(p))))
            {
                bySimpleName.TryAdd(Path.GetFileNameWithoutExtension(dll),
                    MetadataReference.CreateFromFile(dll));
            }
        }

        return bySimpleName.Values.ToList();
    }

    // ── Shared scan logic ──────────────────────────────────────────────────────

    private static readonly HashSet<string> LinqOperatorNames = new(StringComparer.Ordinal)
    {
        "Where", "Select", "SelectMany", "Join", "GroupJoin",
        "OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending",
        "GroupBy", "Skip", "Take", "SkipWhile", "TakeWhile", "SkipLast", "TakeLast",
        "Any", "All", "Count", "LongCount",
        "First", "FirstOrDefault", "Last", "LastOrDefault", "Single", "SingleOrDefault",
        "Distinct", "DistinctBy", "Concat", "Union", "UnionBy", "Intersect", "IntersectBy",
        "Except", "ExceptBy", "Reverse", "AsEnumerable", "AsQueryable",
        "ToList", "ToArray", "ToDictionary", "ToHashSet", "ToLookup",
        "Aggregate", "Sum", "Average", "Min", "Max", "MaxBy", "MinBy",
        "Contains", "SequenceEqual", "ElementAt", "ElementAtOrDefault",
        "DefaultIfEmpty", "Append", "Prepend", "Chunk", "Cast", "OfType", "Zip",
        "Order", "OrderDescending",
    };

    private static ScanResult ScanCompilation(string unit, CSharpCompilation compilation, List<SyntaxTree> trees)
    {
        var result = new ScanResult { Unit = unit, FilesParsed = trees.Count };

        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetCompilationUnitRoot();
            var classifier = new LinqRouteClassifier(model);

            var treeDiagnostics = model.GetDiagnostics();
            var treeErrors = treeDiagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
            if (treeErrors > 0)
            {
                result.FilesWithErrors++;
                result.CompileErrors += treeErrors;
                foreach (var diagnostic in treeDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                {
                    var code = diagnostic.Id;
                    result.ErrorCodes[code] = result.ErrorCodes.GetValueOrDefault(code) + 1;
                    if (result.ErrorExamples.Count(e => e.StartsWith(code + " ", StringComparison.Ordinal)) < 2)
                    {
                        var lineSpan = diagnostic.Location.GetLineSpan();
                        var message = diagnostic.GetMessage()
                            .Replace('\r', ' ').Replace('\n', ' ');
                        if (message.Length > 480)
                            message = message[..480] + "…";
                        result.ErrorExamples.Add(
                            $"{code} {Path.GetFileName(tree.FilePath)}:{lineSpan.StartLinePosition.Line + 1}: {message}");
                    }
                }
            }

            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                result.TotalInvocations++;

                // nameof(...) is an InvocationExpression syntactically but a
                // compiler pseudo-function (GetSymbolInfo returns null); it is
                // never a call site, so bucket it separately.
                if (invocation.Expression is IdentifierNameSyntax { Identifier.Text: "nameof" }
                    || (invocation.Expression is MemberAccessExpressionSyntax nameofAccess
                        && nameofAccess.Name.Identifier.Text == "nameof"))
                {
                    result.NameofInvocations++;
                    continue;
                }

                var decision = classifier.ClassifyInvocation(invocation);
                switch (decision.Kind)
                {
                    case LinqRouteKind.Enumerable: result.Enumerable++; break;
                    case LinqRouteKind.Queryable: result.Queryable++; break;
                    case LinqRouteKind.NonLinq: result.NonLinq++; break;
                    case LinqRouteKind.Ambiguous:
                        if (decision.Ambiguity == LinqRouteAmbiguity.Candidate)
                            result.AmbiguousCandidate++;
                        else
                        {
                            result.AmbiguousUnbound++;
                            if (result.UnboundExamples.Count < 8)
                            {
                                var lineSpan = invocation.GetLocation().GetLineSpan();
                                var text = invocation.Expression.ToString();
                                if (text.Length > 60)
                                    text = text[..60] + "…";
                                result.UnboundExamples.Add(
                                    $"{Path.GetFileName(tree.FilePath)}:{lineSpan.StartLinePosition.Line + 1} {text}");
                            }
                        }

                        if (invocation.Expression is MemberAccessExpressionSyntax ma
                            && LinqOperatorNames.Contains(ma.Name.Identifier.Text))
                        {
                            result.LinqShapedUnbound++;
                            if (result.LinqShapedUnboundExamples.Count < 12)
                            {
                                var lineSpan = invocation.GetLocation().GetLineSpan();
                                result.LinqShapedUnboundExamples.Add(
                                    $"{Path.GetFileName(tree.FilePath)}:{lineSpan.StartLinePosition.Line + 1} " +
                                    $"{ma.Name.Identifier.Text} [{decision.Ambiguity}/{decision.CandidateReason}]");
                            }
                        }
                        break;
                }

                if (decision.ExpressionArgumentCount > 0)
                    result.ExpressionArgumentSites++;
            }

            foreach (var chain in LinqRouteClassifier.FindChains(root))
            {
                var chainDecision = classifier.ClassifyChain(chain);
                result.ChainsTotal++;
                switch (chainDecision.Route)
                {
                    case LinqChainRoute.Expandable: result.ChainsExpandable++; break;
                    case LinqChainRoute.PreserveQueryable: result.ChainsPreserveQueryable++; break;
                    case LinqChainRoute.PreserveMixed: result.ChainsPreserveMixed++; break;
                    case LinqChainRoute.PreserveAmbiguous: result.ChainsPreserveAmbiguous++; break;
                }
            }
        }

        return result;
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

        // net5.0+ modern TFMs (SDK emits NET, NETCOREAPP, NETx, NETx_y).
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
            Console.Error.WriteLine($"parse failed {path}: {ex.Message}");
            return null;
        }
    }

    private static List<MetadataReference> TpaReferences()
    {
        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(File.Exists)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
        return tpa;
    }

    // ── Reporting ──────────────────────────────────────────────────────────────

    private static void Report(List<ScanResult> results)
    {
        Console.WriteLine();
        Console.WriteLine("| unit | files | files w/ errors | CS errors | total invocations | nameof sites | LINQ total | Enumerable | Queryable | Expression arg sites | NonLinq | Ambiguous unbound | Ambiguous candidate | LC0010-shaped unbound | chains | expandable | preserve Queryable | preserve Mixed | preserve Ambiguous |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var r in results)
        {
            Console.WriteLine(
                $"| {r.Unit} | {r.FilesParsed} | {r.FilesWithErrors} | {r.CompileErrors} | {r.TotalInvocations} " +
                $"| {r.NameofInvocations} " +
                $"| {r.Enumerable + r.Queryable} | {r.Enumerable} | {r.Queryable} | {r.ExpressionArgumentSites} " +
                $"| {r.NonLinq} | {r.AmbiguousUnbound} | {r.AmbiguousCandidate} | {r.LinqShapedUnbound} " +
                $"| {r.ChainsTotal} | {r.ChainsExpandable} | {r.ChainsPreserveQueryable} " +
                $"| {r.ChainsPreserveMixed} | {r.ChainsPreserveAmbiguous} |");
        }

        var total = Aggregate(results);
        Console.WriteLine(
            $"| **TOTAL** | {total.FilesParsed} | {total.FilesWithErrors} | {total.CompileErrors} | {total.TotalInvocations} " +
            $"| {total.NameofInvocations} " +
            $"| {total.Enumerable + total.Queryable} | {total.Enumerable} | {total.Queryable} | {total.ExpressionArgumentSites} " +
            $"| {total.NonLinq} | {total.AmbiguousUnbound} | {total.AmbiguousCandidate} | {total.LinqShapedUnbound} " +
            $"| {total.ChainsTotal} | {total.ChainsExpandable} | {total.ChainsPreserveQueryable} " +
            $"| {total.ChainsPreserveMixed} | {total.ChainsPreserveAmbiguous} |");

        Console.WriteLine();
        Console.WriteLine("Binding health (diagnostic codes, top 8 per unit):");
        foreach (var r in results)
        {
            if (r.ErrorCodes.Count == 0)
                continue;
            var codes = string.Join(", ",
                r.ErrorCodes.OrderByDescending(kv => kv.Value).Take(8)
                    .Select(kv => $"{kv.Key}×{kv.Value}"));
            var missing = r.MissingCompileItems > 0 ? $" (missing compile items: {r.MissingCompileItems})" : "";
            Console.WriteLine($"  {r.Unit}: {codes}{missing}");
            foreach (var example in r.ErrorExamples.Take(6))
                Console.WriteLine($"    - {example}");
        }

        var withExamples = results.Where(r => r.LinqShapedUnboundExamples.Count > 0).ToList();
        if (withExamples.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("LC0010-shaped unbound site examples (syntax name, reason):");
            foreach (var r in withExamples)
            foreach (var example in r.LinqShapedUnboundExamples)
                Console.WriteLine($"  [{r.Unit}] {example}");
        }

        Console.WriteLine();
        Console.WriteLine("Other unbound invocation examples (null symbol, no LINQ operator name):");
        foreach (var r in results)
        foreach (var example in r.UnboundExamples.Take(5))
            Console.WriteLine($"  [{r.Unit}] {example}");

        Console.WriteLine();
        Console.WriteLine("JSON:");
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            generatedAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            units = results,
            total,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static ScanResult Aggregate(List<ScanResult> results)
    {
        var total = new ScanResult { Unit = "TOTAL" };
        foreach (var r in results)
        {
            total.FilesParsed += r.FilesParsed;
            total.FilesWithErrors += r.FilesWithErrors;
            total.CompileErrors += r.CompileErrors;
            total.TotalInvocations += r.TotalInvocations;
            total.NameofInvocations += r.NameofInvocations;
            total.Enumerable += r.Enumerable;
            total.Queryable += r.Queryable;
            total.ExpressionArgumentSites += r.ExpressionArgumentSites;
            total.NonLinq += r.NonLinq;
            total.AmbiguousUnbound += r.AmbiguousUnbound;
            total.AmbiguousCandidate += r.AmbiguousCandidate;
            total.LinqShapedUnbound += r.LinqShapedUnbound;
            total.ChainsTotal += r.ChainsTotal;
            total.ChainsExpandable += r.ChainsExpandable;
            total.ChainsPreserveQueryable += r.ChainsPreserveQueryable;
            total.ChainsPreserveMixed += r.ChainsPreserveMixed;
            total.ChainsPreserveAmbiguous += r.ChainsPreserveAmbiguous;
            total.MissingCompileItems += r.MissingCompileItems;
            foreach (var (code, count) in r.ErrorCodes)
                total.ErrorCodes[code] = total.ErrorCodes.GetValueOrDefault(code) + count;
        }
        return total;
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name)
                return args[i + 1];
        return null;
    }

    private static bool HasFlag(string[] args, string name) => Array.Exists(args, a => a == name);

    private static string SimpleName(string path) => Path.GetFileNameWithoutExtension(path);

    private static string Sanitize(string s) =>
        new(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    private static void PrintUsage()
    {
        Console.WriteLine("""
            C26 M0 LinqRouteProbe

              sdk [--root D:\csharp-sdk] [--samples a,b,...]
              ef  --project path\to\Project.csproj [--configuration Debug] [--build]
            """);
    }

    private sealed class ScanResult
    {
        public string Unit { get; set; } = "";
        public int FilesParsed { get; set; }
        public int FilesWithErrors { get; set; }
        public int CompileErrors { get; set; }
        public int MissingCompileItems { get; set; }
        public long TotalInvocations { get; set; }
        public int NameofInvocations { get; set; }
        public int Enumerable { get; set; }
        public int Queryable { get; set; }
        public int ExpressionArgumentSites { get; set; }
        public int NonLinq { get; set; }
        public int AmbiguousUnbound { get; set; }
        public int AmbiguousCandidate { get; set; }
        public int LinqShapedUnbound { get; set; }
        public int ChainsTotal { get; set; }
        public int ChainsExpandable { get; set; }
        public int ChainsPreserveQueryable { get; set; }
        public int ChainsPreserveMixed { get; set; }
        public int ChainsPreserveAmbiguous { get; set; }
        public List<string> LinqShapedUnboundExamples { get; } = new();
        public List<string> UnboundExamples { get; } = new();
        public Dictionary<string, int> ErrorCodes { get; } = new(StringComparer.Ordinal);
        public List<string> ErrorExamples { get; } = new();
    }
}
