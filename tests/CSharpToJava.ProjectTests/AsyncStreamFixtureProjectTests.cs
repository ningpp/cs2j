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
/// shape with the plain try/finally + <c>__cs2jAsE_</c> enumerator temps
/// (default <see cref="AsyncNormalizeOptions.FaithfulAsyncDisposeGuard"/>=false:
/// no <c>__cs2jAsOk_</c> flag temp).
///
/// <para><b>.NET 10 TRUTH MATCH (measured net10.0 / SDK 10.0.300, fixture
/// T4).</b> When MoveNextAsync throws "body-in-flight" and DisposeAsync throws
/// "dispose", await foreach surfaces <i>the DisposeAsync exception</i> — its
/// lowering is an ordinary try/finally (finally masks the try fault); no C# 8
/// "swallow-the-dispose-exception-while-in-flight" special case exists. The
/// pass therefore defaults to the plain try/finally expansion, so all T1–T9
/// (10/10) pass on BOTH the original tree and the intermediate tree — T4
/// receives the ObjectDisposedException("dispose") either way. The earlier
/// 9-pass/1-fail "guard divergence" split existed only when the pass emitted
/// the faithful guard; that shape is now an explicit, non-.NET opt-in
/// (<see cref="AsyncNormalizeOptions.FaithfulAsyncDisposeGuard"/>=true).
/// <see cref="AsyncStreamOn_ExpandsAsyncStreams_IntermediateTreeBuildsAndAllTruthTestsMatchNetTruth"/>
/// pins the 10-pass / 0-fail intermediate-tree result.
/// </para>
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
    // chain; behavior tests run explicitly below and must match the .NET
    // truth 10/10 (plain try/finally default expansion).
    // ------------------------------------------------------------------

    [Fact]
    public async Task AsyncStreamOn_ExpandsAsyncStreams_IntermediateTreeBuildsAndAllTruthTestsMatchNetTruth()
    {
        var outputRoot = Path.Combine(FixtureDir, ".test-output-async-on");
        var resultsDirectory = Path.Combine(outputRoot, "trx");
        try
        {
            // VerifyBuild=true proves the expansion compiles; VerifyTests is
            // off because the explicit trx run below is what pins the 10/10
            // .NET-truth result (T4 included: ODE("dispose") masks the
            // in-flight fault under the plain try/finally expansion).
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

            // ...with the enumerator temps (plain try/finally default
            // expansion) and NO faithful-guard ok-flag temps anywhere.
            Assert.True(IntermediateContainsAnyFlat(intermediate, "__cs2jAsE_"),
                "enumerator temp missing from intermediate tree");
            Assert.False(IntermediateContainsAnyFlat(intermediate, "__cs2jAsOk_"),
                "faithful-guard ok-flag temp must not appear under the plain-finally default");

            // M4: the three YieldProbe async iterators are fully lowered (no
            // residual yield anywhere), with iterator/enumerable state-machine
            // classes present in the intermediate tree.
            Assert.Equal(0, CountAllIntermediate(intermediate, "yield return"));
            Assert.Equal(0, CountAllIntermediate(intermediate, "yield break"));
            Assert.True(IntermediateContainsAnyFlat(intermediate, "Cs2jAsyncIterator_"),
                "M4 iterator state machine missing from intermediate tree");
            Assert.True(IntermediateContainsAnyFlat(intermediate, "Cs2jAsyncEnumerable_"),
                "M4 enumerable wrapper missing from intermediate tree");

            // Explicit behavior evidence on the intermediate tree: T1–T14
            // (14 tests) pass, matching the measured net10 truth — T4 receives
            // ObjectDisposedException("dispose") via ordinary finally masking,
            // and T11–T14 run on the M4-lowered async-iterator state machines.
            var intermediateSln = Path.Combine(intermediate, "SampleAsyncSolution.sln");
            var (exitCode, trxPath) = await RunFixtureTestsAsync(
                intermediateSln, resultsDirectory, "intermediate.trx");

            Assert.Equal(0, exitCode);
            Assert.NotNull(trxPath);
            var trx = ReadTrx(trxPath!);
            Assert.Equal(14, trx.Counters.Total);
            Assert.Equal(14, trx.Counters.Passed);
            Assert.Equal(0, trx.Counters.Failed);
            Assert.Empty(trx.FailedNames);
            Assert.DoesNotContain(T4TestName, trx.FailedNames);
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

            // Fixture ground truth (7 Lib source files + 1 test file; one of
            // the 7 Lib files is YieldProbe.cs):
            //  • await foreach sites: ChannelProbe x2, Handwritten x2,
            //    CancellationProbe x1, DoubleFaultProbe x2, YieldProbe x1
            //    (joint site inside BoundedWithFinally) = 8
            //  • await using sites: AsyncDisposalProbe x3 (statements),
            //    LifoScope x3 (2 declarations + 1 statement),
            //    DoubleFaultProbe x1 (statement) = 7
            //  • WithCancellation: Handwritten + CancellationProbe
            //    + YieldProbe.BoundedWithFinally = 3
            //  • async-iterator producers (M4): YieldProbe x3, each lowered to
            //    an iterator + enumerable class = 3 lowered / 6 classes
            var stats = adapter.Statistics;
            Assert.Equal(8, stats.FilesScanned);
            Assert.Equal(7, stats.FilesTransformed);

            Assert.Equal(8, stats.AwaitForeachExpanded);
            Assert.Equal(8, stats.AsyncEnumeratorsEmitted);
            // The 8th await foreach lives inside an iterator producer: its
            // release is absorbed into the M4 DisposeAsync region, so only 7
            // standalone dispose guards are emitted.
            Assert.Equal(7, stats.AsyncDisposeGuardsEmitted);
            Assert.Equal(3, stats.WithCancellationResolved);

            Assert.Equal(5, stats.AwaitUsingStatementsExpanded);
            Assert.Equal(2, stats.AwaitUsingDeclarationsExpanded);
            Assert.True(
                stats.AwaitUsingStatementsExpanded + stats.AwaitUsingDeclarationsExpanded >= 3,
                "fewer than 3 await-using expansions");

            // M4 lowers every YieldProbe producer; nothing is rejected.
            Assert.Equal(0, stats.AsyncIteratorsRejected);
            Assert.Equal(3, stats.AsyncIteratorsLowered);
            Assert.Equal(6, stats.AsyncIteratorClassesEmitted);
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
            Assert.Equal(14, trx.Counters.Total);
            Assert.Equal(14, trx.Counters.Passed);
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
