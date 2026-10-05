using System.Diagnostics;
using System.Text.RegularExpressions;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.EnumDeclaration;
using CSharpToJava.Core.EqualityConcat;
using CSharpToJava.Core.ExpressionDesugar;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.LexicalNormalize;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;
using CSharpToJava.Core.StructValueSemantics;
using CSharpToJava.Core.ThreadStaticRewrite;
using CSharpToJava.Core.UnsafeFfm;
using Xunit;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// Project-level gate for the C09 thread-static pass (design §7.2/§7.4):
/// the SampleThreadStaticSolution fixture (Lib + xUnit App with real
/// multithreaded behavior tests) is copied into a temp directory and run
/// through the public preprocess chain with thread-static explicitly enabled.
/// The runner verifies dotnet build + dotnet test on the intermediate tree
/// (the App's concurrency tests are the .NET-side behavior gate), while these
/// facts additionally pin the holder shape, the synthesized FirstThreadFaithful
/// static constructor, TS-5 struct lifts, byte/BOM preservation, statistics,
/// the two skip controls (CS2J1201/CS2J1204), and the zero-CS2J1220 residual
/// gate. The pass stays default-off.
/// </summary>
[Collection("ThreadStaticSerial")]
public sealed class ThreadStaticProjectTests
{
    private const string PassId = "thread-static";
    private const string IntermediateDir = ".cs2j-thread-static-src";

    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleThreadStaticSolution");

    private static string CopyFixtureToTemp()
    {
        Assert.True(Directory.Exists(FixtureDir),
            "SampleThreadStaticSolution must be copied to the test output directory");
        var workDir = Path.Combine(Path.GetTempPath(),
            "cs2j-c09-e2e-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(FixtureDir, workDir);
        return workDir;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }

        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
    }

    // ---- chain plumbing (ConcurrencyProjectTests precedent) ----

    private static Task<CliChainResult> RunChainAsync(
        string sourcePath, string outputRoot, bool enableThreadStatic, bool verify)
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
            EnablePassIds = enableThreadStatic ? new[] { PassId } : null,
            // The two skip controls (CS2J1201 instance / CS2J1204 generic
            // container) are intentional Warnings on this .NET-side gate.
            Strict = false,
            VerifyBuild = verify,
            VerifyTests = verify,
        });
    }

    /// <summary>Retry once: parallel ProjectTests load can kill a dotnet child transiently.</summary>
    private static async Task<CliChainResult> RunChainWithRetryAsync(
        string sourcePath, string outputRoot, bool enableThreadStatic, bool verify)
    {
        var result = await RunChainAsync(sourcePath, outputRoot, enableThreadStatic, verify);
        if (!result.Success)
        {
            result = await RunChainAsync(sourcePath, outputRoot, enableThreadStatic, verify);
        }

        return result;
    }

    // ---- text helpers ----

    private static string Flat(string s) => Regex.Replace(s, @"\s+", string.Empty);

    private static void AssertContainsFlat(string expected, string actual)
        => Assert.Contains(Flat(expected), Flat(actual));

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

    /// <summary>Every .cs file of the intermediate tree except bin/obj, comment lines stripped.</summary>
    private static IEnumerable<(string RelativePath, string Text)> EnumerateCode(string root)
        => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file =>
            {
                var parts = Path.GetRelativePath(root, file)
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return !parts.Contains("bin", StringComparer.OrdinalIgnoreCase)
                    && !parts.Contains("obj", StringComparer.OrdinalIgnoreCase);
            })
            .Select(file =>
            {
                var text = string.Join(Environment.NewLine, File.ReadLines(file)
                    .Select(line => line.TrimStart())
                    .Where(line => !line.StartsWith("//", StringComparison.Ordinal)));
                return (Path.GetRelativePath(root, file), text);
            });

    private static string FindFile(string root, string fileName)
        => Directory.GetFiles(root, fileName, SearchOption.AllDirectories)
            .Where(file =>
            {
                var parts = Path.GetRelativePath(root, file)
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return !parts.Contains("bin", StringComparer.OrdinalIgnoreCase)
                    && !parts.Contains("obj", StringComparer.OrdinalIgnoreCase)
                    && !parts.Contains(IntermediateDir, StringComparer.OrdinalIgnoreCase);
            })
            .Single();

    private static string DiagnosticsDump(IReadOnlyList<ThreadStaticDiagnostic> diagnostics)
        => string.Join("\n", diagnostics.Select(d => d.ToString()));

    // ------------------------------------------------------------------
    // Pass enabled (opt-in): full chain with verify build+tests.
    // ------------------------------------------------------------------

    [Fact]
    public async Task ThreadStaticOn_RewritesFixture_IntermediateTreeBuildsAndBehaviorTestsPass()
    {
        var workDir = CopyFixtureToTemp();
        var sourceSln = Path.Combine(workDir, "SampleThreadStaticSolution.sln");
        var outputRoot = Path.Combine(workDir, "out");

        try
        {
            var result = await RunChainWithRetryAsync(
                sourceSln, outputRoot, enableThreadStatic: true, verify: true);

            Assert.True(result.Success, "thread-static chain failed (verify build+tests gate)");
            Assert.Contains(IntermediateDir, result.ExecutedIntermediateDirectoryNames);

            var intermediate = Path.Combine(outputRoot, IntermediateDir);
            Assert.True(Directory.Exists(intermediate));

            // ---- holder shape: exactly five parameterless ThreadLocal holders ----
            Assert.Equal(5, EnumerateCode(intermediate)
                .Sum(file => CountFlat(file.Text, "new global::System.Threading.ThreadLocal<")));

            // ---- no residual attribute outside the single skip-control file ----
            foreach (var (relativePath, text) in EnumerateCode(intermediate))
            {
                if (relativePath.EndsWith("SkipControls.cs", StringComparison.OrdinalIgnoreCase))
                {
                    // Both skip controls retain their attribute (CS2J1201 + CS2J1204).
                    // Drop inline comment tails (the pragma note mentions the attribute
                    // by name) so only real attribute applications are counted.
                    var codeOnly = Regex.Replace(text, @"//[^\r\n]*", string.Empty);
                    Assert.Equal(2, CountFlat(codeOnly, "[ThreadStatic]"));
                    continue;
                }

                Assert.DoesNotContain("[ThreadStatic]", text);
                Assert.DoesNotContain("ThreadStaticAttribute", text);
            }

            // ---- case 1: uninitialized counter ----
            var plain = await File.ReadAllTextAsync(FindFile(intermediate, "PlainCounter.cs"));
            AssertContainsFlat(
                "internal static readonly global::System.Threading.ThreadLocal<int> Hits "
                + "= new global::System.Threading.ThreadLocal<int>();",
                plain);
            Assert.Contains("Hits.Value", plain);
            Assert.DoesNotContain("(() =>", plain);

            // ---- case 2: FirstThreadFaithful — parameterless holder + synthesized cctor ----
            var initialized = await File.ReadAllTextAsync(FindFile(intermediate, "InitializedCounter.cs"));
            AssertContainsFlat(
                "private static readonly global::System.Threading.ThreadLocal<int> _first "
                + "= new global::System.Threading.ThreadLocal<int>();",
                initialized);
            AssertContainsFlat("static InitializedCounter() { _first.Value = 42; }", initialized);
            Assert.DoesNotContain("(() =>", initialized);

            // ---- case 3: TS-5 struct temp lifts with write-back ----
            var structState = await File.ReadAllTextAsync(FindFile(intermediate, "StructState.cs"));
            AssertContainsFlat("global::System.Threading.ThreadLocal<CounterVec>", structState);
            AssertContainsFlat(
                "var __cs2j_ts__vec = _vec.Value; __cs2j_ts__vec.Bump(); "
                + "_vec.Value = __cs2j_ts__vec;",
                structState);
            AssertContainsFlat(
                "var __cs2j_ts__vec = _vec.Value; __cs2j_ts__vec.X += 10; "
                + "_vec.Value = __cs2j_ts__vec;",
                structState);

            // ---- case 4: cross-file sites rewritten in the other file too ----
            var crossFile = await File.ReadAllTextAsync(FindFile(intermediate, "CrossFileUser.cs"));
            AssertContainsFlat("PlainCounter.Hits.Value += delta", crossFile);
            AssertContainsFlat("LambdaShared.Tally.Value = delta", crossFile);
            AssertContainsFlat("var snapshot = PlainCounter.Hits.Value", crossFile);

            // ---- case 5: lambda site rewritten like an ordinary site ----
            var lambdaShared = await File.ReadAllTextAsync(FindFile(intermediate, "LambdaShared.cs"));
            AssertContainsFlat("n => Tally.Value + n", lambdaShared);
            AssertContainsFlat("Tally.Value += add", lambdaShared);

            // ---- clean file reproduced byte-for-byte ----
            var sourceClean = await File.ReadAllBytesAsync(FindFile(workDir, "CleanMarker.cs"));
            var intermediateClean = await File.ReadAllBytesAsync(FindFile(intermediate, "CleanMarker.cs"));
            Assert.Equal(sourceClean, intermediateClean);

            // ---- BOM hit file keeps its BOM after the rewrite ----
            var bomPath = FindFile(intermediate, "BomCounter.cs");
            var bomBytes = await File.ReadAllBytesAsync(bomPath);
            Assert.True(bomBytes.Length >= 3 && bomBytes[0] == 0xEF && bomBytes[1] == 0xBB
                && bomBytes[2] == 0xBF, "rewritten BOM hit file must keep its UTF-8 BOM");
            AssertContainsFlat("new global::System.Threading.ThreadLocal<int>",
                await File.ReadAllTextAsync(bomPath));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // Statistics reconciliation + residual gate (design §7.4).
    // ------------------------------------------------------------------

    [Fact]
    public async Task Statistics_ReconcileGroundTruth_ResidualGateHasOnlyOwnedSkips_Zero1220()
    {
        var workDir = CopyFixtureToTemp();
        var sourceSln = Path.Combine(workDir, "SampleThreadStaticSolution.sln");

        try
        {
            var adapter = new ThreadStaticPassAdapter(
                new ThreadStaticOptions(),
                verbose: false,
                filterUnsupportedProjects: true,
                compatNamespacePrefixes: null,
                verifyBuild: false,
                verifyTests: false);

            var run = await ProjectPassRunner.RunAsync(adapter, new ProjectPassRunRequest
            {
                SourcePath = sourceSln,
                DestinationRoot = Path.Combine(workDir, "out"),
                Force = true,
            });

            Assert.True(run.Success, "adapter run failed:\n" + DiagnosticsDump(adapter.Diagnostics));
            Assert.False(adapter.HasErrors);

            var stats = adapter.Statistics;

            // Ground truth: 15 .cs files (8 Lib + 7 App); six Lib files change,
            // the rest (2 untouched Lib files + 7 App files) are clean copies.
            Assert.Equal(15, stats.FilesScanned);
            Assert.Equal(6, stats.FilesTransformed);
            Assert.Equal(9, stats.FilesSkippedClean);

            // Five fields rewritten; two controls skipped (one each).
            Assert.Equal(7, stats.FieldsDetected);
            Assert.Equal(5, stats.FieldsRewritten);
            Assert.Equal(2, stats.FieldsSkipped);
            Assert.Equal(1, stats.SkippedByReason[ThreadStaticCodes.GenericContainerUnsupported]);
            Assert.Equal(1, stats.SkippedByReason[ThreadStaticCodes.UnsupportedAttributePosition]);

            // Site ground truth counted across the fixture:
            //  reads    10 (Hits x3, _first x1, _vec x1, Tally x4, _bomHits x1)
            //  writes    4 (Reset x2, Overwrite x1, cross-file Tally x1)
            //  compound  3 (Add + cross-file Hits, Tally += add)
            //  increment 3 (PlainCounter/Initialized/Bom)
            Assert.Equal(10, stats.ReadsRewritten);
            Assert.Equal(4, stats.WritesRewritten);
            Assert.Equal(3, stats.CompoundSitesRewritten);
            Assert.Equal(3, stats.IncrementSitesRewritten);

            // TS-5: one method mutation + one field mutation in StructState.
            Assert.Equal(2, stats.StructTempLifts);

            // Exactly one inline initializer (FirstThreadFaithful cctor move).
            Assert.Equal(1, stats.InitializersNormalized);
            Assert.Equal(0, stats.InfeasibleSites);

            // Residual gate: precisely the two registered skip controls remain,
            // both owned by inactive plans — zero unowned, zero CS2J1220.
            Assert.Equal(2, stats.ResidualThreadStaticAttributes);
            Assert.Equal(2, stats.OwnedResidualAttributes);
            Assert.Equal(0, stats.UnownedResidualAttributes);
            Assert.DoesNotContain(adapter.Diagnostics, d => d.Code == ThreadStaticCodes.ResidualFound);
            Assert.DoesNotContain(adapter.Diagnostics, d => d.Code == ThreadStaticCodes.WholeTreeRollback);
            Assert.Equal(0, stats.PlansRolledBack);
            Assert.Equal(0, stats.WholeRunRolledBack);
            Assert.Equal(1, stats.RollbackRounds);

            // Both skip controls are announced with their codes (Warnings here).
            var byCode = adapter.Diagnostics.GroupBy(d => d.Code).ToDictionary(g => g.Key, g => g.Count());
            Assert.Equal(1, byCode[ThreadStaticCodes.GenericContainerUnsupported]);
            Assert.Equal(1, byCode[ThreadStaticCodes.UnsupportedAttributePosition]);

            // No rollback/infra error codes.
            Assert.DoesNotContain(adapter.Diagnostics,
                d => d.Code is ThreadStaticCodes.PlanRolledBack
                    or ThreadStaticCodes.RestoreOrBuildFailed
                    or ThreadStaticCodes.TestsFailed);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // Default off: without the explicit enable id the pass never runs.
    // ------------------------------------------------------------------

    [Fact]
    public async Task ThreadStaticOff_ByDefault_ChainProducesNoIntermediateTree()
    {
        var workDir = CopyFixtureToTemp();
        var sourceSln = Path.Combine(workDir, "SampleThreadStaticSolution.sln");
        var outputRoot = Path.Combine(workDir, "out");

        try
        {
            var result = await RunChainAsync(
                sourceSln, outputRoot, enableThreadStatic: false, verify: false);

            Assert.True(result.Success, "all-disabled chain must succeed");
            Assert.DoesNotContain(IntermediateDir, result.ExecutedIntermediateDirectoryNames);
            Assert.False(Directory.Exists(Path.Combine(outputRoot, IntermediateDir)),
                "the pass is default-off: no intermediate tree may be produced");
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // Baseline: the fixture behavior tests pass on the ORIGINAL source.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Baseline_OriginalFixture_BehaviorTestsPassOnUnprocessedSource()
    {
        var workDir = CopyFixtureToTemp();
        var solutionPath = Path.Combine(workDir, "SampleThreadStaticSolution.sln");

        try
        {
            string output = string.Empty;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"test \"{solutionPath}\" --nologo -c Release",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
                psi.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";

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
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }
}
