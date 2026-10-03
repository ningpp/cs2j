using System.Diagnostics;
using CSharpToJava.CLI;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.ExtensionMethodResolve;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit.Abstractions;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// M3 Probe5 (design appendix ruling 2): rerun the C19 binding-rate
/// measurement against D:\csharp-sdk with the REAL merged-compilation
/// pipeline (all sample sources in one CSharpCompilation, TPA basics plus the
/// restored NuGet compile assemblies enumerated the same way the CLI adapter
/// does, SDK implicit usings mirrored) and the TypeMappings-backed host
/// grader. Records bound/unbound counts, the four receiver classifications,
/// and the 1201/1202/1203/1207/1210 distribution.
///
/// Env-gated like Probe3 (CS2J_RUN_SDK_PROBE=1 + D:\csharp-sdk); a no-op
/// pass otherwise.
/// </summary>
public sealed class ExtensionMethodSdkBindingProbeTests
{
    private readonly ITestOutputHelper _output;

    public ExtensionMethodSdkBindingProbeTests(ITestOutputHelper output) => _output = output;

    private static readonly string[] Samples =
    {
        "json",
        "regular-expressions",
        "uri",
        "xml-tiny",
    };

    [Fact]
    public void Probe5_MergedCompilation_RestoredReferences_BindingRate()
    {
        if (Environment.GetEnvironmentVariable("CS2J_RUN_SDK_PROBE") != "1"
            || !Directory.Exists(@"D:\csharp-sdk"))
        {
            _output.WriteLine("Skipped: set CS2J_RUN_SDK_PROBE=1 and ensure D:\\csharp-sdk exists.");
            return;
        }

        var configPath = LocateTypeMappingsConfig();
        var mapper = ExtensionHostMappingResolver.TryCreate(configPath);
        _output.WriteLine($"Using TypeMappings config: {configPath ?? "<default>"}");

        var grand = new Measurement();
        foreach (var sample in Samples)
        {
            var sampleRoot = Path.Combine(@"D:\csharp-sdk", sample);
            if (!Directory.Exists(sampleRoot))
            {
                _output.WriteLine($"sample missing: {sample}");
                continue;
            }

            var measurement = MeasureSample(sample, sampleRoot, mapper);
            _output.WriteLine($"[{sample}] {measurement}");
            Console.WriteLine($"[C19 probe5][{sample}] {measurement}");
            grand.Add(measurement);
        }

        _output.WriteLine($"[TOTAL] {grand}");
        Console.WriteLine($"[C19 probe5][TOTAL] {grand}");
        _output.WriteLine(
            $"[TOTAL] reduced binding rate: {grand.BindingRatePercent:F2}% " +
            $"(bound={grand.BoundSites}, failed={grand.FailedCandidateSites})");

        Assert.True(grand.FilesParsed > 0, "expected at least one parsable sdk sample");
    }

    private Measurement MeasureSample(
        string sample,
        string sampleRoot,
        Func<INamedTypeSymbol, ExternalHostMappingKind> mapper)
    {
        var projectFiles = Directory.EnumerateFiles(sampleRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(f => !f.Replace('\\', '/').Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                && !f.Replace('\\', '/').Contains("/obj/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var projectFile in projectFiles)
        {
            RunRestore(projectFile);
        }

        var sourceFiles = Directory.EnumerateFiles(sampleRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Replace('\\', '/').Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                && !f.Replace('\\', '/').Contains("/obj/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var parseOptions = new CSharpParseOptions(
            LanguageVersion.Latest,
            preprocessorSymbols: new[] { "NET", "NETCOREAPP" });

        var trees = sourceFiles
            .Select(f => (SyntaxTree)CSharpSyntaxTree.ParseText(
                File.ReadAllText(f), parseOptions, path: f))
            .ToList();

        // Mirror adapter CreateAdditionalSyntaxTrees: synthesized global usings.
        var globalUsings = new System.Text.StringBuilder();
        foreach (var projectFile in projectFiles)
        {
            var usings = ImplicitGlobalUsings.BuildForProject(projectFile, sampleRoot);
            if (!string.IsNullOrEmpty(usings))
            {
                globalUsings.AppendLine(usings);
            }
        }

        if (globalUsings.Length > 0)
        {
            trees.Add(CSharpSyntaxTree.ParseText(
                globalUsings.ToString(), parseOptions, path: $"__cs2j_global_usings_{sample}.g.cs"));
        }

        var references = ProjectUnsignedTypePreprocessor.GetBasicReferences();
        references.AddRange(RestorePackageReferenceEnumerator.Enumerate(sampleRoot, out var restoredCount));
        _output.WriteLine($"[{sample}] projects={projectFiles.Count}, files={sourceFiles.Count}, restored refs added={restoredCount}");

        var compilation = CSharpCompilation.Create(
            "em-probe5-" + sample,
            trees,
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));

        var preprocessor = new ExtensionMethodResolvePreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze(new ExtensionMethodResolveOptions
        {
            ExternalHostMapper = mapper,
            // Probe only: keep the loud Info lines out of the histogram.
            ReportInfo = false,
            ReportSiteDetail = false,
        });

        var measurement = new Measurement { FilesParsed = sourceFiles.Count };
        foreach (var tree in trees)
        {
            if (tree.FilePath.StartsWith("__cs2j_global_usings_", StringComparison.Ordinal))
            {
                continue;
            }

            var result = preprocessor.RewriteFile(compilation.GetSemanticModel(tree));
            measurement.Merge(result.Statistics);
            foreach (var diagnostic in result.Diagnostics)
            {
                measurement.DiagnosticByCode[diagnostic.Code] =
                    measurement.DiagnosticByCode.GetValueOrDefault(diagnostic.Code) + 1;
            }
        }

        return measurement;
    }

    private void RunRestore(string projectFile)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"restore \"{projectFile}\" --nologo -v q",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";

        using var process = Process.Start(startInfo)!;
        process.WaitForExit(10 * 60 * 1000);
        if (process.HasExited && process.ExitCode != 0)
        {
            var stderr = process.StandardError.ReadToEnd();
            throw new InvalidOperationException(
                $"restore failed for {projectFile} (exit {process.ExitCode}): {stderr}");
        }
    }

    private static string? LocateTypeMappingsConfig()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "config", "TypeMappings.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    private sealed class Measurement
    {
        public int FilesParsed;
        public int BoundSites;
        public int RewrittenSites;
        public int InCompilationUser;
        public int InCompilationSupport;
        public int ExternalMapped;
        public int ExternalUnmapped;
        public int LinqOwned;
        public int RefReceivers;
        public int NullConditionalLifted;
        public int GroupsRewritten;
        public int GroupsDeferred;
        public int ExpressionTreeChecked;
        public int ExpressionTreeRollbacks;
        public int SingleFileDeferred;
        public int FailedCandidateSites;
        public Dictionary<string, int> DiagnosticByCode { get; } = new(StringComparer.Ordinal);

        public double BindingRatePercent
            => BoundSites + FailedCandidateSites == 0
                ? 100.0
                : 100.0 * BoundSites / (BoundSites + FailedCandidateSites);

        public void Merge(ExtensionMethodResolveStatistics stats)
        {
            BoundSites += stats.CallsitesBoundReduced;
            RewrittenSites += stats.CallsitesRewritten;
            InCompilationUser += stats.InCompilationUserHostSites;
            InCompilationSupport += stats.InCompilationSupportHostSites;
            ExternalMapped += stats.ExternalHostMappedSites;
            ExternalUnmapped += stats.ExternalHostUnmappedSites;
            LinqOwned += stats.LinqOwnedSkipped;
            RefReceivers += stats.RefReceiverSites;
            NullConditionalLifted += stats.ConditionalChainsLifted;
            GroupsRewritten += stats.GroupSitesRewritten;
            GroupsDeferred += stats.GroupSitesDeferred;
            ExpressionTreeChecked += stats.ExpressionTreeSitesChecked;
            ExpressionTreeRollbacks += stats.ExpressionTreeRollbacks;
            SingleFileDeferred += stats.SingleFileDeferredSites;
            stats.SitesSkippedByReason.TryGetValue(ExtensionMethodCodes.Ambiguous, out var ambiguous);
            stats.SitesSkippedByReason.TryGetValue(ExtensionMethodCodes.Unbound, out var unbound);
            FailedCandidateSites += ambiguous + unbound;
        }

        public void Add(Measurement other)
        {
            FilesParsed += other.FilesParsed;
            BoundSites += other.BoundSites;
            RewrittenSites += other.RewrittenSites;
            InCompilationUser += other.InCompilationUser;
            InCompilationSupport += other.InCompilationSupport;
            ExternalMapped += other.ExternalMapped;
            ExternalUnmapped += other.ExternalUnmapped;
            LinqOwned += other.LinqOwned;
            RefReceivers += other.RefReceivers;
            NullConditionalLifted += other.NullConditionalLifted;
            GroupsRewritten += other.GroupsRewritten;
            GroupsDeferred += other.GroupsDeferred;
            ExpressionTreeChecked += other.ExpressionTreeChecked;
            ExpressionTreeRollbacks += other.ExpressionTreeRollbacks;
            SingleFileDeferred += other.SingleFileDeferred;
            FailedCandidateSites += other.FailedCandidateSites;
            foreach (var (code, count) in other.DiagnosticByCode)
            {
                DiagnosticByCode[code] = DiagnosticByCode.GetValueOrDefault(code) + count;
            }
        }

        public override string ToString()
        {
            var codes = DiagnosticByCode.Count == 0
                ? "(none)"
                : string.Join(", ", DiagnosticByCode.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
            return $"files={FilesParsed}, bound={BoundSites} (rewritten={RewrittenSites}, "
                + $"user={InCompilationUser}, support={InCompilationSupport}, "
                + $"externalMapped={ExternalMapped}, externalUnmapped={ExternalUnmapped}, linqOwned={LinqOwned}), "
                + $"failedCandidates={FailedCandidateSites}, bindingRate={BindingRatePercent:F2}%, "
                + $"ref={RefReceivers}, nullCondLifted={NullConditionalLifted}, "
                + $"groups(rewritten={GroupsRewritten}/deferred={GroupsDeferred}), "
                + $"exprTree={ExpressionTreeChecked}/rollbacks={ExpressionTreeRollbacks}, "
                + $"singleFileDeferred={SingleFileDeferred}, diagnostics[{codes}]";
        }
    }
}
