using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.AsyncNormalize;
using CSharpToJava.Core.EnumDeclaration;
using CSharpToJava.Core.EqualityConcat;
using CSharpToJava.Core.ExpressionDesugar;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.LexicalNormalize;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;
using CSharpToJava.Core.StructValueSemantics;
using CSharpToJava.Core.UnsafeFfm;
using Xunit;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// Project-level gate for the C38 async-stream slices of the C18
/// async-normalize pass (design §7.2). Drives the SampleAsyncSolution .NET
/// truth fixture (SampleAsyncLib + xUnit T1–T9: channel ordering,
/// cancellation visibility, in-flight/normal-end double-fault disposal,
/// await-using null/LIFO/exactly-once) through <see cref="CliPreprocessChain"/>
/// with <c>async-normalize</c> explicitly enabled. The intermediate tree must
/// contain no bare <c>await foreach</c>/<c>await using</c> and show the
/// explicit GetAsyncEnumerator/MoveNextAsync/Current/DisposeAsync driving
/// shape with the <c>__cs2jAsE_</c>/<c>__cs2jAsOk_</c> temps.
///
/// <para><b>KNOWN DIVERGENCE (measured .NET 10 truth vs design §4.6 / D6).</b>
/// The fixture's T4 pins the MEASURED net10.0 / SDK 10.0.300 behavior: when
/// MoveNextAsync throws "body-in-flight" and DisposeAsync throws "dispose",
/// await foreach surfaces <i>the DisposeAsync exception</i> — its lowering is
/// an ordinary try/finally (finally masks the try fault); no C# 8
/// "swallow-the-dispose-exception-while-in-flight" special case exists. The
/// pass's default <see cref="AsyncNormalizeOptions.FaithfulAsyncDisposeGuard"/>
/// expansion instead lets the in-flight exception win, so T4 fails ON THE
/// INTERMEDIATE TREE while T1–T3/T5–T9 pass.
/// <see cref="AsyncStreamOn_ExpandsAsyncStreams_IntermediateTreeBuildsAndTruthTestsPinGuardDivergence"/>
/// pins that 9-pass / 1-fail split explicitly (the chain run verifies the
/// build, not the tests); flipping the guard default to plain-finally must
/// flip this assertion. The same T4 passes on the original tree
/// (see <see cref="Baseline_OriginalFixture_AllTruthTestsPassOnUnprocessedSource"/>).</para>
/// </summary>
[Collection("SampleAsyncSolution")]
public class AsyncStreamFixtureProjectTests
{
    private const string PassId = "async-normalize";
    private const string IntermediateDir = ".cs2j-async-normalize-src";

    private const string T4TestName =
        "SampleAsyncTests.AsyncStreamTruthTests.T4_InFlightDoubleFault_DisposeExceptionMasksBody_AndDisposeRan";

    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleAsyncSolution");

    private static string FixtureSlnPath => Path.Combine(FixtureDir, "SampleAsyncSolution.sln");

    // ---- chain plumbing (ConcurrencyProjectTests precedent) ----

    private static Task<CliChainResult> RunChainAsync(
        string sourcePath, string outputRoot, bool enableAsyncNormalize,
        bool verifyBuild, bool verifyTests)
    {
        var registry = PassCatalog.BuildRegistry();
        var toggles = PassChainFactory.BuildAllDisabledToggles(registry);
        return CliPreprocessChain.RunAsync(new CliChainRequest
        {
            SourcePath = sourcePath,
            DestinationRoot = outputRoot,
            MakerOptionsFactory = () => new ReadOnlyStructMakerOptions(),
            RecordReadOnlyOptionsFactory = () => new RecordReadOnlyOptions(),
            MemberDesugarOptionsFactory = () => new MemberDesugarOptions(),
            EnumDeclarationOptionsFactory = () => new EnumDeclarationOptions(),
            LexicalNormalizeOptionsFactory = () => new LexicalNormalizeOptions(),
            StructValueSemanticsOptionsFactory = () => new StructValueSemanticsOptions(),
            JavaKeywordOptionsFactory = () => new JavaKeywordOptions(),
            EqualityConcatOptionsFactory = () => new EqualityConcatOptions(),
            UnsafeFfmOptionsFactory = () => new UnsafeFfmOptions(),
            ExpressionDesugarOptionsFactory = () => new ExpressionDesugarOptions(),
            LegacyToggles = toggles,
            EnablePassIds = enableAsyncNormalize ? new[] { PassId } : null,
            // The fixture is fully in the supported C38 subset; non-strict
            // mirrors the concurrency fixture driver's .NET-side gate run.
            Strict = false,
            VerifyBuild = verifyBuild,
            VerifyTests = verifyTests,
        });
    }

    /// <summary>
    /// Retry once on a transient chain failure (the full ProjectTests suite
    /// runs many chain tests in parallel and a dotnet child can die
    /// transiently under that load).
    /// </summary>
    private static async Task<CliChainResult> RunChainWithRetryAsync(
        string sourcePath, string outputRoot, bool enableAsyncNormalize,
        bool verifyBuild, bool verifyTests)
    {
        var result = await RunChainAsync(
            sourcePath, outputRoot, enableAsyncNormalize, verifyBuild, verifyTests);
        if (!result.Success)
        {
            result = await RunChainAsync(
                sourcePath, outputRoot, enableAsyncNormalize, verifyBuild, verifyTests);
        }

        return result;
    }

    // ---- text helpers ----

    private static string Flat(string s) => Regex.Replace(s, @"\s+", string.Empty);

    private static void AssertDoesNotContainFlat(string unexpected, string actual)
        => Assert.DoesNotContain(Flat(unexpected), Flat(actual));

    private static int CountFlat(string text, string needle)
    {
        var flat = Flat(text);
        var flatNeedle = Flat(needle);
        var count = 0;
        var index = 0;
        while ((index = flat.IndexOf(flatNeedle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += flatNeedle.Length;
        }

        return count;
    }

    private static int CountAllIntermediate(string intermediateRoot, string needle)
        => EnumerateCodeText(intermediateRoot).Sum(text => CountFlat(text, needle));

    private static bool IntermediateContainsAnyFlat(string intermediateRoot, string needle)
        => EnumerateCodeText(intermediateRoot)
            .Any(text => Flat(text).Contains(Flat(needle), StringComparison.Ordinal));

    /// <summary>
    /// Every .cs file of the intermediate tree except bin/obj, with full-line
    /// comments stripped (// and ///) so doc text cannot fake or hide a
    /// residual.
    /// </summary>
    private static IEnumerable<string> EnumerateCodeText(string root)
        => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file =>
            {
                var parts = Path.GetRelativePath(root, file)
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return !parts.Contains("bin", StringComparer.OrdinalIgnoreCase)
                    && !parts.Contains("obj", StringComparer.OrdinalIgnoreCase);
            })
            .Select(file => string.Join(Environment.NewLine, File.ReadLines(file)
                .Select(line => line.TrimStart())
                .Where(line => !line.StartsWith("//", StringComparison.Ordinal))));

    private static string DiagnosticsDump(IReadOnlyList<AsyncNormalizeDiagnostic> diagnostics)
        => string.Join("\n", diagnostics.Select(d => d.ToMsBuildString()));

    /// <summary>
    /// Runs `dotnet test` on an intermediate/root fixture tree, publishing a
    /// trx result file; returns (exitCode, trxPath or null when missing).
    /// </summary>
    private static async Task<(int ExitCode, string? TrxPath)> RunFixtureTestsAsync(
        string slnPath, string resultsDirectory, string trxFileName)
    {
        Directory.CreateDirectory(resultsDirectory);
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"test \"{slnPath}\" --nologo "
                + $"--logger \"trx;LogFileName={trxFileName}\" "
                + $"--results-directory \"{resultsDirectory}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(10));
        _ = await stdoutTask + await stderrTask;

        var trxPath = Path.Combine(resultsDirectory, trxFileName);
        return (process.ExitCode, File.Exists(trxPath) ? trxPath : null);
    }

    private sealed record TrxCounters(int Total, int Passed, int Failed);

    private sealed record TrxResult(
        TrxCounters Counters,
        IReadOnlyList<string> FailedNames,
        string FirstFailureMessage);

    private static TrxResult ReadTrx(string trxPath)
    {
        var doc = XDocument.Load(trxPath);
        var ns = doc.Root!.Name.Namespace;

        var countersElement = doc.Descendants(ns + "Counters").FirstOrDefault();
        Assert.NotNull(countersElement);
        var counters = new TrxCounters(
            Total: (int?)countersElement!.Attribute("total") ?? -1,
            Passed: (int?)countersElement.Attribute("passed") ?? -1,
            Failed: (int?)countersElement.Attribute("failed") ?? -1);

        var failedResults = doc.Descendants(ns + "UnitTestResult")
            .Where(r => (string?)r.Attribute("outcome") == "Failed")
            .ToList();

        var failedNames = failedResults
            .Select(r => (string?)r.Attribute("testName") ?? string.Empty)
            .ToList();

        // ErrorInfo message of the first failing test (exception identity).
        var firstFailureMessage = failedResults
            .SelectMany(r => r.Descendants(ns + "Message"))
            .FirstOrDefault()?.Value ?? string.Empty;

        return new TrxResult(counters, failedNames, firstFailureMessage);
    }

    // ------------------------------------------------------------------
    // Pass enabled: the CLI preprocess-chain path. Build verified by the
    // chain; behavior tests run explicitly below (T4 divergence pinned).
    // ------------------------------------------------------------------

    [Fact]
    public async Task AsyncStreamOn_ExpandsAsyncStreams_IntermediateTreeBuildsAndTruthTestsPinGuardDivergence()
    {
        var outputRoot = Path.Combine(FixtureDir, ".test-output-async-on");
        var resultsDirectory = Path.Combine(outputRoot, "trx");
        try
        {
            // VerifyBuild=true proves the expansion compiles; VerifyTests is
            // deliberately off: under the default faithful guard T4 cannot
            // pass on the intermediate tree (see class doc / T4 comments),
            // and that divergence is asserted explicitly further below.
            var result = await RunChainWithRetryAsync(
                FixtureSlnPath, outputRoot, enableAsyncNormalize: true,
                verifyBuild: true, verifyTests: false);

            Assert.True(result.Success,
                "async-normalize chain (build-verified) failed unexpectedly");
            Assert.Contains(IntermediateDir, result.ExecutedIntermediateDirectoryNames);

            var intermediate = Path.Combine(outputRoot, IntermediateDir);
            Assert.True(Directory.Exists(intermediate), "intermediate tree missing");

            // Whole-tree residual gate: no bare await foreach / await using
            // survive anywhere in the expanded tree (comment lines stripped).
            Assert.Equal(0, CountAllIntermediate(intermediate, "await foreach"));
            Assert.Equal(0, CountAllIntermediate(intermediate, "await using"));

            // The explicit async-enumerator driving shape is present...
            Assert.True(IntermediateContainsAnyFlat(intermediate, "GetAsyncEnumerator("));
            Assert.True(IntermediateContainsAnyFlat(intermediate, "MoveNextAsync("));
            Assert.True(IntermediateContainsAnyFlat(intermediate, ".Current"));
            Assert.True(IntermediateContainsAnyFlat(intermediate, "DisposeAsync()"));

            // ...with the guard flag and enumerator temps (default
            // FaithfulAsyncDisposeGuard=true expansion), plus await-using's
            // try/finally DisposeAsync shape and resource temps.
            Assert.True(IntermediateContainsAnyFlat(intermediate, "__cs2jAsOk_"),
                "faithful-guard ok-flag temp missing from intermediate tree");
            Assert.True(IntermediateContainsAnyFlat(intermediate, "__cs2jAsE_"),
                "enumerator temp missing from intermediate tree");

            // Explicit behavior evidence on the intermediate tree: T1–T3 and
            // T5–T9 (9 tests) still pass under the guard expansion; T4 is the
            // single pinned divergence (body-in-flight wins under the guard,
            // dispose masks under real .NET).
            var intermediateSln = Path.Combine(intermediate, "SampleAsyncSolution.sln");
            var (exitCode, trxPath) = await RunFixtureTestsAsync(
                intermediateSln, resultsDirectory, "intermediate.trx");

            Assert.NotEqual(0, exitCode);
            Assert.NotNull(trxPath);
            var trx = ReadTrx(trxPath!);
            Assert.Equal(10, trx.Counters.Total);
            Assert.Equal(9, trx.Counters.Passed);
            Assert.Equal(1, trx.Counters.Failed);

            Assert.Single(trx.FailedNames);
            Assert.Contains(T4TestName, trx.FailedNames[0]);

            // xUnit's Throws failure records both the expected and the actual
            // exception type — pins the root cause rather than a random failure.
            Assert.Contains("ObjectDisposedException", trx.FirstFailureMessage);
            Assert.Contains("InvalidOperationException", trx.FirstFailureMessage);
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }
        }
    }

    // ------------------------------------------------------------------
    // Statistics + diagnostic reconciliation (design §5.1/§6).
    // ------------------------------------------------------------------

    [Fact]
    public async Task AsyncStreamStatistics_ReconcileExpandCounts_Zero1840To1849_ZeroResidual()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "cs2j-c38-stats-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var adapter = new AsyncNormalizePassAdapter(
                new AsyncNormalizeOptions(),
                verbose: false,
                filterUnsupportedProjects: true,
                compatNamespacePrefixes: null,
                verifyBuild: false,
                verifyTests: false,
                strict: false);

            var run = await ProjectPassRunner.RunAsync(adapter, new ProjectPassRunRequest
            {
                SourcePath = FixtureSlnPath,
                DestinationRoot = Path.Combine(workDir, "out"),
                Force = true,
            });

            Assert.True(run.Success,
                "adapter run failed:\n" + DiagnosticsDump(adapter.Diagnostics));
            Assert.False(adapter.HasErrors,
                "unexpected error diagnostics:\n" + DiagnosticsDump(adapter.Diagnostics));

            // Fixture ground truth (7 Lib source files + 1 test file):
            //  • await foreach sites: ChannelProbe x2, Handwritten x2,
            //    CancellationProbe x1, DoubleFaultProbe x2 = 7
            //  • await using sites: AsyncDisposalProbe x3 (statements),
            //    LifoScope x3 (2 declarations + 1 statement),
            //    DoubleFaultProbe x1 (statement) = 7
            //  • WithCancellation: Handwritten + CancellationProbe = 2
            var stats = adapter.Statistics;
            Assert.Equal(7, stats.FilesScanned);
            Assert.Equal(6, stats.FilesTransformed);

            Assert.Equal(7, stats.AwaitForeachExpanded);
            Assert.Equal(7, stats.AsyncEnumeratorsEmitted);
            Assert.Equal(7, stats.AsyncDisposeGuardsEmitted);
            Assert.Equal(2, stats.WithCancellationResolved);

            Assert.Equal(5, stats.AwaitUsingStatementsExpanded);
            Assert.Equal(2, stats.AwaitUsingDeclarationsExpanded);
            Assert.True(
                stats.AwaitUsingStatementsExpanded + stats.AwaitUsingDeclarationsExpanded >= 3,
                "fewer than 3 await-using expansions");

            // No repo-owned async-iterator producer (CS2J1844 / M4 surface):
            // producers are Channel + hand-written enumerators by design.
            Assert.Equal(0, stats.AsyncIteratorsRejected);
            // Every await foreach / await using site was normalized.
            Assert.Equal(0, stats.AsyncStreamResidualCount);

            // The fixture is the supported subset: zero C38 rejection/gate
            // codes CS2J1840–CS2J1849 and zero build/test hooks.
            var streamGateDiagnostics = adapter.Diagnostics
                .Where(d => d.Code.ToString().StartsWith("CS2J184", StringComparison.Ordinal))
                .ToList();
            Assert.Empty(streamGateDiagnostics);
            Assert.DoesNotContain(adapter.Diagnostics, d => d.Code == AsyncNormalizeDiagnosticCode.CS2J1820);
        }
        finally
        {
            if (Directory.Exists(workDir))
            {
                Directory.Delete(workDir, recursive: true);
            }
        }
    }

    // ------------------------------------------------------------------
    // Default off: without the explicit enable id the pass never runs.
    // ------------------------------------------------------------------

    [Fact]
    public async Task AsyncStreamOff_ByDefault_ChainProducesNoIntermediateTree()
    {
        var outputRoot = Path.Combine(FixtureDir, ".test-output-async-off");
        try
        {
            var result = await RunChainAsync(
                FixtureSlnPath, outputRoot, enableAsyncNormalize: false,
                verifyBuild: false, verifyTests: false);

            Assert.True(result.Success, "default chain must succeed without the opt-in pass");
            Assert.DoesNotContain(IntermediateDir, result.ExecutedIntermediateDirectoryNames);
            Assert.False(Directory.Exists(Path.Combine(outputRoot, IntermediateDir)),
                "the pass is default-off: no intermediate tree may be produced");
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }
        }
    }

    // ------------------------------------------------------------------
    // Baseline: the T1–T9 truth tests all pass on the ORIGINAL source
    // (the .NET half of the equivalence contract), T4 included.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Baseline_OriginalFixture_AllTruthTestsPassOnUnprocessedSource()
    {
        var resultsDirectory = Path.Combine(FixtureDir, ".test-output-baseline-trx");
        try
        {
            // Retry once for transient parallel-suite child failures.
            (int ExitCode, string? TrxPath)? outcome = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                outcome = await RunFixtureTestsAsync(
                    FixtureSlnPath, resultsDirectory, $"baseline-{attempt}.trx");
                if (outcome.Value.ExitCode == 0)
                {
                    break;
                }
            }

            Assert.True(outcome!.Value.ExitCode == 0 && outcome.Value.TrxPath != null,
                "baseline dotnet test failed after two attempts");

            var trx = ReadTrx(outcome.Value.TrxPath!);
            Assert.Equal(10, trx.Counters.Total);
            Assert.Equal(10, trx.Counters.Passed);
            Assert.Equal(0, trx.Counters.Failed);
        }
        finally
        {
            if (Directory.Exists(resultsDirectory))
            {
                Directory.Delete(resultsDirectory, recursive: true);
            }
        }
    }
}
