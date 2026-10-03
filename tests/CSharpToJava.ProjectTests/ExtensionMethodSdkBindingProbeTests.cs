using System.Diagnostics;
using System.Text.RegularExpressions;
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

    private static IEnumerable<string> ProbeSamples()
    {
        var overrideList = Environment.GetEnvironmentVariable("CS2J_PROBE_SAMPLES");
        if (!string.IsNullOrWhiteSpace(overrideList))
        {
            foreach (var s in overrideList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return s;
            }

            yield break;
        }

        foreach (var s in Samples)
        {
            yield return s;
        }
    }

    /// <summary>
    /// M4 Probe6 (design §8 gate ①): materialize the C19 candidate into a
    /// TEMP copy of each sample (D:\csharp-sdk stays untouched), build/test
    /// baseline vs candidate (Release), and require ZERO new compile errors
    /// and zero newly failing tests. Also records residual-gate counts and
    /// mixed-chain skips. Env-gated like Probe5.
    /// </summary>
    [Fact]
    public void Probe6_CandidateMaterializedInTemp_BuildTestNoNewFailures()
    {
        if (Environment.GetEnvironmentVariable("CS2J_RUN_SDK_PROBE") != "1"
            || !Directory.Exists(@"D:\csharp-sdk"))
        {
            _output.WriteLine("Skipped: set CS2J_RUN_SDK_PROBE=1 and ensure D:\\csharp-sdk exists.");
            return;
        }

        var configPath = LocateTypeMappingsConfig();
        var mapper = ExtensionHostMappingResolver.TryCreate(configPath);

        var grandNewErrors = 0;
        var grandNewTestFailures = 0;
        foreach (var sample in ProbeSamples())
        {
            var sampleRoot = Path.Combine(@"D:\csharp-sdk", sample);
            if (!Directory.Exists(sampleRoot))
            {
                _output.WriteLine($"sample missing: {sample}");
                continue;
            }

            var result = MaterializeAndVerifySample(sample, sampleRoot, mapper);
            _output.WriteLine(
                $"[probe6][{sample}] baselineErrors={result.BaselineErrorCount}, "
                + $"candidateErrors={result.CandidateErrorCount}, newErrors={result.NewErrorCount}, "
                + $"baselineFailedTests={result.BaselineFailedTests}, candidateFailedTests={result.CandidateFailedTests}, "
                + $"newFailedTests={result.NewFailedTestCount}, mixedChains={result.MixedChains}, "
                + $"residual[{result.ResidualSummary}]");
            Console.WriteLine(
                $"[C19 probe6][{sample}] newCompileErrors={result.NewErrorCount}, " +
                $"newFailedTests={result.NewFailedTestCount}, mixedChains={result.MixedChains}, " +
                $"residual[{result.ResidualSummary}]");
            foreach (var detail in result.NewErrorDetails)
            {
                _output.WriteLine($"  NEW {detail}");
            }
            foreach (var detail in result.BaselineOnlyDetails.Take(30))
            {
                _output.WriteLine($"  BASELINE-ONLY {detail}");
            }

            grandNewErrors += result.NewErrorCount;
            grandNewTestFailures += result.NewFailedTestCount;
        }

        _output.WriteLine($"[probe6][TOTAL] newCompileErrors={grandNewErrors}, newFailedTests={grandNewTestFailures}");
        Console.WriteLine($"[C19 probe6][TOTAL] newCompileErrors={grandNewErrors}, newFailedTests={grandNewTestFailures}");
        Assert.True(grandNewErrors == 0,
            $"C19 candidate introduced new compile errors in sdk samples: {grandNewErrors}");
        Assert.True(grandNewTestFailures == 0,
            $"C19 candidate introduced new test failures in sdk samples: {grandNewTestFailures}");
    }

    private Probe6Result MaterializeAndVerifySample(
        string sample,
        string sampleRoot,
        Func<INamedTypeSymbol, ExternalHostMappingKind> mapper)
    {
        var projectFiles = Directory.EnumerateFiles(sampleRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(f => !IsBuildArtifact(f))
            .ToList();
        foreach (var projectFile in projectFiles)
        {
            RunRestore(projectFile);
        }

        var sourceFiles = Directory.EnumerateFiles(sampleRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildArtifact(f))
            .ToList();

        var parseOptions = new CSharpParseOptions(
            LanguageVersion.Latest,
            preprocessorSymbols: new[] { "NET", "NETCOREAPP" });

        var trees = sourceFiles
            .Select(f => (SyntaxTree)CSharpSyntaxTree.ParseText(
                File.ReadAllText(f), parseOptions, path: f))
            .ToList();

        var globalUsings = new System.Text.StringBuilder();
        foreach (var projectFile in projectFiles)
        {
            var usings = ImplicitGlobalUsings.BuildForProject(projectFile, sampleRoot);
            if (!string.IsNullOrEmpty(usings))
            {
                globalUsings.AppendLine(usings);
            }
        }

        var globalUsingText = globalUsings.ToString();

        var references = ProjectUnsignedTypePreprocessor.GetBasicReferences();
        references.AddRange(RestorePackageReferenceEnumerator.Enumerate(sampleRoot, out _));

        SyntaxTree? globalUsingTree = null;
        if (globalUsingText.Length > 0)
        {
            globalUsingTree = CSharpSyntaxTree.ParseText(
                globalUsingText, parseOptions, path: $"__cs2j_global_usings_{sample}.g.cs");
            trees.Add(globalUsingTree);
        }

        var compilation = CSharpCompilation.Create(
            "em-probe6-" + sample,
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
            ReportInfo = false,
            ReportSiteDetail = false,
        });

        var workRoot = Path.Combine(Path.GetTempPath(), "cs2j-probe6-" + Guid.NewGuid().ToString("N"), sample);
        var baselineRoot = Path.Combine(workRoot, "baseline");
        var candidateRoot = Path.Combine(workRoot, "candidate");
        try
        {
            var changedFiles = 0;
            var mixedChains = 0;

            // Baseline copy = verbatim sources; candidate copy = baseline +
            // overlaid rewritten files.
            CopySampleTree(sampleRoot, baselineRoot);
            CopySampleTree(sampleRoot, candidateRoot);
            foreach (var tree in trees)
            {
                if (tree.FilePath.StartsWith("__cs2j_global_usings_", StringComparison.Ordinal)
                    || !tree.FilePath.StartsWith(sampleRoot, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var model = compilation.GetSemanticModel(tree);
                var result = preprocessor.RewriteFile(model);
                mixedChains += result.Statistics.MixedLinqChainSites;
                if (!result.Changed)
                {
                    continue;
                }

                changedFiles++;
                var relative = Path.GetRelativePath(sampleRoot, tree.FilePath);
                var candidateFile = Path.Combine(candidateRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(candidateFile)!);
                File.WriteAllText(candidateFile, result.OutputCode);
            }

            var outcome = new Probe6Result { MixedChains = mixedChains };

            foreach (var projectFile in projectFiles)
            {
                var relativeProj = Path.GetRelativePath(sampleRoot, projectFile);
                var baselineProj = Path.Combine(baselineRoot, relativeProj);
                var candidateProj = Path.Combine(candidateRoot, relativeProj);

                var baselineBuild = RunBuild(baselineProj);
                var candidateBuild = RunBuild(candidateProj);
                outcome.BaselineErrorCount += baselineBuild.Errors.Count;
                outcome.CandidateErrorCount += candidateBuild.Errors.Count;
                foreach (var key in candidateBuild.Errors)
                {
                    if (!baselineBuild.Errors.Contains(key))
                    {
                        outcome.NewErrorCount++;
                        outcome.NewErrorDetails.Add($"{relativeProj}: {key}");
                    }
                }
                foreach (var key in baselineBuild.Errors)
                {
                    if (!candidateBuild.Errors.Contains(key))
                    {
                        outcome.BaselineOnlyDetails.Add($"{relativeProj}: {key}");
                    }
                }

                var isTestProject = File.ReadAllText(projectFile)
                    .Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase);
                if (!isTestProject)
                {
                    continue;
                }

                var baselineTests = RunTestSuite(baselineProj);
                var candidateTests = RunTestSuite(candidateProj);
                outcome.BaselineFailedTests += baselineTests;
                outcome.CandidateFailedTests += candidateTests;
                outcome.NewFailedTestCount += Math.Max(0, candidateTests - baselineTests);
            }

            // Residual six-gate scan over the candidate compilation.
            var candidateTrees = Directory
                .EnumerateFiles(candidateRoot, "*.cs", SearchOption.AllDirectories)
                .Where(f => !IsBuildArtifact(f))
                .Select(f => (SyntaxTree)CSharpSyntaxTree.ParseText(
                    File.ReadAllText(f), parseOptions, path: f))
                .ToList();
            if (globalUsingTree is not null)
            {
                candidateTrees.Add(globalUsingTree);
            }

            var candidateCompilation = CSharpCompilation.Create(
                "em-probe6-cand-" + sample,
                candidateTrees,
                references,
                new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    allowUnsafe: true,
                    nullableContextOptions: NullableContextOptions.Enable));
            outcome.ResidualByRule = preprocessor.ResidualScan(candidateCompilation);
            outcome.ResidualSummary = outcome.ResidualByRule.Count == 0
                ? "(none)"
                : string.Join(", ", outcome.ResidualByRule
                    .Where(kv => kv.Value > 0)
                    .Select(kv => $"{kv.Key}={kv.Value}"));
            outcome.ChangedFiles = changedFiles;

            return outcome;
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("CS2J_PROBE_KEEP") == "1")
            {
                _output.WriteLine($"[probe6] kept work tree: {workRoot}");
            }
            else
            {
                try { Directory.Delete(workRoot, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static bool IsBuildArtifact(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase);
    }

    private static void CopySampleTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            if (IsBuildArtifact(dir + Path.DirectorySeparatorChar))
            {
                continue;
            }

            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (IsBuildArtifact(file))
            {
                continue;
            }

            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static readonly Regex BuildErrorRegex = new(
        @": error (CS\d+): (.*)",
        RegexOptions.Compiled);

    private static readonly Regex TrailingProjectBracket = new(
        @"\s*\[[^\[\]]*\.(?:csproj|sln|slnx|props|targets|cs)\]\s*$",
        RegexOptions.Compiled);

    private static readonly Regex TempRootNoise = new(
        @"cs2j-probe6-[0-9a-fA-F]+[\\/](?:baseline|candidate)",
        RegexOptions.Compiled);

    private (int ExitCode, HashSet<string> Errors) RunBuild(string projectFile)
    {
        var (exitCode, output) = RunWithOutput(
            $"build \"{projectFile}\" -c Release --nologo -v q", 15 * 60 * 1000);
        var errors = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in BuildErrorRegex.Matches(output))
        {
            // Relative-file independent: id + normalized message. csc appends
            // the project path in a trailing bracket and temp roots differ
            // between baseline and candidate.
            var message = TrailingProjectBracket.Replace(match.Groups[2].Value.Trim(), string.Empty);
            message = TempRootNoise.Replace(message, "<probe-root>");
            errors.Add(match.Groups[1].Value + ": " + message.Trim());
        }

        return (exitCode, errors);
    }

    private int RunTestSuite(string projectFile)
    {
        var (_, output) = RunWithOutput(
            $"test \"{projectFile}\" -c Release --nologo -v q", 20 * 60 * 1000);
        var failed = 0;
        foreach (Match match in Regex.Matches(output, @"失败[:：]\s*(\d+)"))
        {
            failed = int.Parse(match.Groups[1].Value);
        }

        // English fallback ("Failed: 0").
        if (failed == 0)
        {
            foreach (Match match in Regex.Matches(output, @"Failed[:：]\s*(\d+)"))
            {
                failed = int.Parse(match.Groups[1].Value);
            }
        }

        return failed;
    }

    private (int ExitCode, string Output) RunWithOutput(string arguments, int timeoutMs)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(timeoutMs);
        return (process.HasExited ? process.ExitCode : -1, stdout + "\n" + stderr);
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

    private sealed class Probe6Result
    {
        public int BaselineErrorCount;
        public int CandidateErrorCount;
        public int NewErrorCount;
        public List<string> NewErrorDetails { get; } = new();
        public List<string> BaselineOnlyDetails { get; } = new();
        public int BaselineFailedTests;
        public int CandidateFailedTests;
        public int NewFailedTestCount;
        public int MixedChains;
        public int ChangedFiles;
        public Dictionary<string, int> ResidualByRule { get; set; } = new(StringComparer.Ordinal);
        public string ResidualSummary { get; set; } = "(not run)";
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
        public int MixedLinqChainSites;
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
            MixedLinqChainSites += stats.MixedLinqChainSites;
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
            MixedLinqChainSites += other.MixedLinqChainSites;
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
                + $"singleFileDeferred={SingleFileDeferred}, mixedChains={MixedLinqChainSites}, "
                + $"diagnostics[{codes}]";
        }
    }
}
