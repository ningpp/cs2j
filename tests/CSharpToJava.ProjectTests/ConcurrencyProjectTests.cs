using System.Diagnostics;
using System.Text.RegularExpressions;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.ConcurrencyNormalize;
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
/// Project-level gate for the C29 concurrency-normalize pass (design §7.2).
/// Runs the pass over the dedicated SampleConcurrencySolution fixture (Lib +
/// xUnit Tests with real-multithreaded behavior tests). With the pass enabled
/// the intermediate tree must show every hand-written Enter/Exit pair
/// normalized into `lock`, every TryEnter in the canonical 3-arg int-ms +
/// lockTaken form, the lifted `__cs2j_mt`/`__cs2j_to` temps, reconciled
/// statistics and a zero-unknown residual gate — and `dotnet build` +
/// `dotnet test` must pass on the intermediate tree, which proves the
/// normalization did not change observable concurrency behavior (the fixture's
/// 16 behavior tests run on the preprocessed tree, plus a baseline run on the
/// original source).
/// </summary>
[Collection("SampleConcurrencySolution")]
public class ConcurrencyProjectTests
{
    private const string PassId = "concurrency-normalize";
    private const string IntermediateDir = ".cs2j-concurrency-normalize-src";

    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleConcurrencySolution");

    private static string FixtureSlnPath => Path.Combine(FixtureDir, "SampleConcurrencySolution.sln");

    // ---- chain plumbing (SampleParamsCollectionsSolution precedent) ----

    private static async Task<CliChainResult> RunChainAsync(
        string sourcePath, string outputRoot, bool enableConcurrency, bool verify)
    {
        var registry = PassCatalog.BuildRegistry();
        var toggles = PassChainFactory.BuildAllDisabledToggles(registry);
        return await CliPreprocessChain.RunAsync(new CliChainRequest
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
            EnablePassIds = enableConcurrency ? new[] { PassId } : null,
            // The pass runs opt-in and the fixture intentionally contains
            // TryEnter sites: CS2J2911 stays a Warning on this .NET-side gate
            // run (the CLI escape hatch is --no-local-function-strict).
            Strict = false,
            VerifyBuild = verify,
            VerifyTests = verify,
        });
    }

    /// <summary>
    /// Runs the chain, retrying once on failure: the full ProjectTests suite
    /// runs many chain tests in parallel and a dotnet child can die
    /// transiently (exit code -1) under that load. Two consecutive failures
    /// still fail the test.
    /// </summary>
    private static async Task<CliChainResult> RunChainWithRetryAsync(
        string sourcePath, string outputRoot, bool enableConcurrency, bool verify)
    {
        var result = await RunChainAsync(sourcePath, outputRoot, enableConcurrency, verify);
        if (!result.Success)
        {
            result = await RunChainAsync(sourcePath, outputRoot, enableConcurrency, verify);
        }

        return result;
    }

    // ---- text helpers ----

    private static string Flat(string s) => Regex.Replace(s, @"\s+", string.Empty);

    private static void AssertContainsFlat(string expected, string actual)
        => Assert.Contains(Flat(expected), Flat(actual));

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

    private static Task<string> ReadIntermediateAsync(string intermediateRoot, params string[] segments)
        => File.ReadAllTextAsync(Path.Combine(new[] { intermediateRoot }.Concat(segments).ToArray()));

    /// <summary>
    /// Every .cs file of the intermediate tree except bin/obj, with comment
    /// lines stripped so doc text cannot fake a residual.
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

    private static int CountAllIntermediate(string intermediateRoot, string needle)
        => EnumerateCodeText(intermediateRoot).Sum(text => CountFlat(text, needle));

    private static string DiagnosticsDump(IReadOnlyList<ConcurrencyDiagnostic> diagnostics)
        => string.Join("\n", diagnostics.Select(d => d.ToMsBuildString()));

    // ------------------------------------------------------------------
    // Pass enabled (opt-in): the CLI preprocess-verb path with verify on.
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConcurrencyOn_NormalizesMonitorCalls_IntermediateTreeBuildsTestsPassAndCanonical()
    {
        var outputRoot = Path.Combine(FixtureDir, ".test-output-concurrency-on");
        try
        {
            var result = await RunChainWithRetryAsync(
                FixtureSlnPath, outputRoot, enableConcurrency: true, verify: true);

            Assert.True(result.Success,
                "concurrency-normalize chain failed (see chain diagnostics)");
            Assert.Contains(IntermediateDir, result.ExecutedIntermediateDirectoryNames);

            var intermediate = Path.Combine(outputRoot, IntermediateDir);

            // Lib CN-2 target: every hand-written Enter/Exit pair (form A and
            // form B) is gone — rewritten into `lock` on the same gate object
            // (4 rewrites + the 2 source lock statements = 6).
            var handwritten = await ReadIntermediateAsync(
                intermediate, "SampleConcurrencyLib", "HandwrittenMonitorLock.cs");
            AssertDoesNotContainFlat("Monitor.Enter(", handwritten);
            AssertDoesNotContainFlat("Monitor.Exit(", handwritten);
            AssertDoesNotContainFlat("bool taken = false", handwritten);
            Assert.Equal(6, CountFlat(handwritten, "lock (_gate)"));

            // Lib CN-3 target: every TryEnter site is the canonical 3-arg
            // int-milliseconds + lockTaken form. Shapes 1-3 were rewritten
            // with the __cs2j_mt/__cs2j_to temps (0ms filled in, TimeSpan
            // lifted); shape 4 was already canonical and is structure-checked.
            var timedGate = await ReadIntermediateAsync(
                intermediate, "SampleConcurrencyLib", "TimedGate.cs");
            AssertContainsFlat("bool __cs2j_mt = false; Monitor.TryEnter(_gate, 0, ref __cs2j_mt);", timedGate);
            AssertContainsFlat(
                "bool __cs2j_mt = false; Monitor.TryEnter(_gate, timeoutMilliseconds, ref __cs2j_mt);", timedGate);
            AssertContainsFlat(
                "int __cs2j_to = (int)timeout.TotalMilliseconds; bool __cs2j_mt = false; " +
                "Monitor.TryEnter(_gate, __cs2j_to, ref __cs2j_mt);", timedGate);
            AssertContainsFlat("Monitor.TryEnter(_gate, 30, ref taken)", timedGate);
            Assert.Equal(4, CountFlat(timedGate, "Monitor.TryEnter("));
            Assert.Equal(4, CountFlat(timedGate, "Monitor.Exit("));
            Assert.Equal(6, CountFlat(timedGate, "if (__cs2j_mt)"));

            // Lib CN-4 target: the Wait(TimeSpan) site was lifted to the
            // int-milliseconds temp; the int overload and the bool branch stay.
            var boundedBuffer = await ReadIntermediateAsync(
                intermediate, "SampleConcurrencyLib", "BoundedBuffer.cs");
            AssertContainsFlat(
                "int __cs2j_to = (int)timeout.TotalMilliseconds; if (!Monitor.Wait(_sync, __cs2j_to))",
                boundedBuffer);
            Assert.Equal(4, CountFlat(boundedBuffer, "Monitor.Wait("));
            Assert.Equal(3, CountFlat(boundedBuffer, "Monitor.Pulse("));
            Assert.Equal(1, CountFlat(boundedBuffer, "Monitor.PulseAll("));

            // Whole-tree residual shape: zero hand-written Enter anywhere;
            // the only surviving Exits are the canonical TryEnter guards.
            Assert.Equal(0, CountAllIntermediate(intermediate, "Monitor.Enter("));
            Assert.Equal(4, CountAllIntermediate(intermediate, "Monitor.TryEnter("));
            Assert.Equal(4, CountAllIntermediate(intermediate, "Monitor.Exit("));
            Assert.Equal(4, CountAllIntermediate(intermediate, "Monitor.Wait("));
            Assert.Equal(3, CountAllIntermediate(intermediate, "Monitor.Pulse("));
            Assert.Equal(1, CountAllIntermediate(intermediate, "Monitor.PulseAll("));

            // The out-of-scope contrast section is byte-identical semantics:
            // untouched SemaphoreSlim/EventWaitHandle sites survive.
            var outOfScope = await ReadIntermediateAsync(
                intermediate, "SampleConcurrencyLib", "OutOfScopePrimitives.cs");
            AssertContainsFlat("new SemaphoreSlim(1, 1)", outOfScope);
            AssertContainsFlat("new EventWaitHandle(false, EventResetMode.AutoReset)", outOfScope);
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
    // Statistics reconciliation + residual gate (design §7.4).
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConcurrencyStatistics_ReconcileRewriteCounts_ResidualGateHasZeroUnknownSites()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "cs2j-c29-stats-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var adapter = new ConcurrencyNormalizePassAdapter(
                new ConcurrencyOptions(), verbose: false, filterUnsupportedProjects: true,
                compatNamespacePrefixes: null, verifyBuild: false, verifyTests: false, strict: false);
            var run = await ProjectPassRunner.RunAsync(adapter, new ProjectPassRunRequest
            {
                SourcePath = FixtureSlnPath,
                DestinationRoot = Path.Combine(workDir, "out"),
                Force = true,
            });

            Assert.True(run.Success,
                "adapter run failed:\n" + DiagnosticsDump(adapter.Diagnostics));
            Assert.False(adapter.HasErrors);

            // Gate codes must be absent: the fixture is fully canonical, all
            // Wait/Pulse/PulseAll sites are provably owned, the intermediate
            // tree compiles, and the residual gate has zero unknown sites.
            foreach (var forbidden in new[]
                     {
                         ConcurrencyDiagnosticCodes.WholeTreeRollback,
                         ConcurrencyDiagnosticCodes.PlanRollback,
                         ConcurrencyDiagnosticCodes.RestoreFailed,
                         ConcurrencyDiagnosticCodes.BuildFailed,
                         ConcurrencyDiagnosticCodes.TestFailed,
                         ConcurrencyDiagnosticCodes.NonCanonicalStructure,
                         ConcurrencyDiagnosticCodes.LockOwnershipUnproven,
                         ConcurrencyDiagnosticCodes.WaitExitContext,
                         ConcurrencyDiagnosticCodes.AwaitInLockMirror,
                         ConcurrencyDiagnosticCodes.ResidualMonitorGate,
                     })
            {
                Assert.DoesNotContain(adapter.Diagnostics, d => d.Code == forbidden);
            }

            // Exactly the three rewritten TryEnter sites carry CS2J2911, and
            // they stay Warnings on this non-strict .NET-side run.
            var tryEnterGates = adapter.Diagnostics
                .Where(d => d.Code == ConcurrencyDiagnosticCodes.TryEnterGate).ToList();
            Assert.Equal(3, tryEnterGates.Count);
            Assert.All(tryEnterGates, d => Assert.Equal(ConcurrencySeverity.Warning, d.Severity));
            Assert.Contains(adapter.Diagnostics, d => d.Code == ConcurrencyDiagnosticCodes.OutOfScopePrimitive);
            Assert.DoesNotContain(adapter.Diagnostics, d => d.Code == ConcurrencyDiagnosticCodes.VolatileStaticApi);

            // ---- statistics reconciliation (fixture ground truth) ----
            var stats = adapter.Statistics;
            Assert.Equal(7, stats.FilesScanned);
            Assert.Equal(3, stats.FilesTransformed);
            Assert.Equal(4, stats.FilesSkippedClean);

            Assert.Equal(14, stats.LockStatementsSeen);
            Assert.Equal(1, stats.LockNestedRegions);

            // CN-2: all four hand-written pairs detected and rewritten.
            Assert.Equal(4, stats.EnterExitPairsDetected);
            Assert.Equal(4, stats.EnterExitPairsRewritten);
            Assert.Equal(0, stats.EnterExitPairsRejected);

            // CN-3: 4 sites — 3 bare-bool canonicalized (1 zero-timeout,
            // 2 timed + the TimeSpan one) + 1 already-canonical lockTaken kept.
            Assert.Equal(4, stats.TryEnterSitesDetected);
            Assert.Equal(3, stats.TryEnterSitesCanonicalized);
            Assert.Equal(3, stats.TryEnterTimed);
            Assert.Equal(1, stats.TryEnterZeroTimeout);
            Assert.Equal(3, stats.TryEnterBareBool);
            Assert.Equal(1, stats.TryEnterLockTaken);

            // CN-4: 4 Wait sites (2 un-timed, 1 int, 1 TimeSpan lifted).
            Assert.Equal(4, stats.WaitSites);
            Assert.Equal(2, stats.WaitSitesNoTimeout);
            Assert.Equal(1, stats.WaitSitesIntMs);
            Assert.Equal(1, stats.WaitSitesTimeSpan);
            Assert.Equal(1, stats.WaitSitesCanonicalized);
            Assert.Equal(0, stats.WaitSitesExitContext);

            // CN-5 + ownership: 3 Pulse + 1 PulseAll, all provably held.
            Assert.Equal(3, stats.PulseSites);
            Assert.Equal(1, stats.PulseAllSites);
            Assert.Equal(0, stats.OwnershipWarnings);

            // CN-7: one primitive volatile field, no static Volatile API.
            Assert.Equal(1, stats.VolatileFieldsPrimitive);
            Assert.Equal(0, stats.VolatileFieldsNonPrimitive);
            Assert.Equal(0, stats.VolatileStaticApiSites);

            // Nothing skipped, no identity audits, no infeasible sites.
            Assert.Equal(0, stats.InfeasibleSites);
            Assert.Empty(stats.SkippedByReason);
            Assert.Empty(stats.IdentityAuditsByKind);

            // CN-11: the out-of-scope contrast sections are counted by type.
            Assert.True(stats.OutOfScopeSitesByType.TryGetValue("SemaphoreSlim", out var semaphore)
                && semaphore >= 1, "SemaphoreSlim out-of-scope bucket missing");
            Assert.True(stats.OutOfScopeSitesByType.TryGetValue("EventWaitHandle", out var eventHandle)
                && eventHandle >= 1, "EventWaitHandle out-of-scope bucket missing");
            Assert.True(stats.OutOfScopeSitesByType.TryGetValue("ManualResetEventSlim", out var mres)
                && mres >= 1, "ManualResetEventSlim out-of-scope bucket missing");

            // Residual gate reconciliation: the bound Monitor sites counted by
            // the scanner equal the Monitor calls visible in the output tree.
            Assert.Equal(16, stats.ResidualMonitorCalls);
            Assert.Equal(stats.ResidualMonitorCalls,
                CountAllIntermediate(run.IntermediateRoot, "Monitor.TryEnter(")
                + CountAllIntermediate(run.IntermediateRoot, "Monitor.Enter(")
                + CountAllIntermediate(run.IntermediateRoot, "Monitor.Exit(")
                + CountAllIntermediate(run.IntermediateRoot, "Monitor.Wait(")
                + CountAllIntermediate(run.IntermediateRoot, "Monitor.Pulse(")
                + CountAllIntermediate(run.IntermediateRoot, "Monitor.PulseAll("));
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
    public async Task ConcurrencyOff_ByDefault_ChainProducesNoIntermediateTree()
    {
        var outputRoot = Path.Combine(FixtureDir, ".test-output-concurrency-off");
        try
        {
            var result = await RunChainAsync(
                FixtureSlnPath, outputRoot, enableConcurrency: false, verify: false);

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
    // Baseline: the fixture's behavior tests pass on the ORIGINAL source too.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Baseline_OriginalFixture_BehaviorTestsPassOnUnprocessedSource()
    {
        // The .NET-side half of the equivalence contract: the fixture's 16
        // real-multithreaded tests pass on the ORIGINAL source (they run on
        // the preprocessed tree inside ConcurrencyOn_... via VerifyTests).
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"test \"{FixtureSlnPath}\" --nologo",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // Retry once for transient parallel-suite child failures (exit code -1).
        var output = string.Empty;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var process = Process.Start(psi)!;
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(10));
            output = await stdoutTask + await stderrTask;
            if (process.ExitCode == 0)
            {
                return;
            }
        }

        Assert.Fail($"baseline dotnet test failed after two attempts:\n{output}");
    }
}
