using System.Diagnostics;
using System.Text.RegularExpressions;
using CSharpToJava.CLI;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.DefaultParameterStripper.ParamsCollections;
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
/// Project-level gate for the C48 params-collections rules (T21, design §7.2).
/// Runs strip-default alone over the dedicated SampleParamsCollectionsSolution
/// fixture (Lib + xUnit Tests). With the rule enabled the intermediate tree
/// must show materialized declarations, explicitized call sites and zero
/// non-array params residuals, and `dotnet build` + `dotnet test` must pass on
/// it — the fixture's own behavior tests double as the .NET-side equivalence
/// check (they also run on the un-preprocessed original via the baseline test).
/// </summary>
public class ParamsCollectionsProjectTests
{
    private const string PassId = "strip-default";
    private const string IntermediateDir = ".cs2j-strip-default-src";

    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleParamsCollectionsSolution");

    private static async Task<CliChainResult> RunStripDefaultAsync(
        string sourcePath, string outputRoot, ParamsCollectionsOptions? paramsOptions, bool verify)
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
            EnablePassIds = new[] { PassId },
            ParamsCollections = paramsOptions,
            VerifyBuild = verify,
            VerifyTests = verify,
        });
    }

    private static string Flat(string s) => Regex.Replace(s, @"\s+", string.Empty);

    private static void AssertContainsFlat(string expected, string actual)
        => Assert.Contains(Flat(expected), Flat(actual));

    private static void AssertDoesNotContainFlat(string unexpected, string actual)
        => Assert.DoesNotContain(Flat(unexpected), Flat(actual));

    private static Task<string> ReadIntermediateAsync(string intermediateRoot, params string[] segments)
        => File.ReadAllTextAsync(Path.Combine(new[] { intermediateRoot }.Concat(segments).ToArray()));

    /// <summary>
    /// Residual gate over the whole intermediate tree: no non-array params
    /// modifier may survive the rule-enabled strip (classic params T[]/T[]
    /// are legal strategy-A residuals and are exempt).
    /// </summary>
    private static void AssertNoNonArrayParamsResidual(string intermediateRoot)
    {
        var forbidden = new[]
        {
            "params ReadOnlySpan<", "params Span<", "params List<", "params HashSet<",
            "params IEnumerable<", "params IReadOnlyCollection<", "params IReadOnlyList<",
            "params ICollection<", "params IList<",
        };

        foreach (var file in Directory.EnumerateFiles(intermediateRoot, "*.cs", SearchOption.AllDirectories))
        {
            // Skip comment lines so doc text cannot fake a residual; the gate
            // is about surviving declaration syntax.
            var codeLines = File.ReadLines(file)
                .Select(line => line.TrimStart())
                .Where(line => !line.StartsWith("//", StringComparison.Ordinal));
            var content = string.Join(Environment.NewLine, codeLines);
            foreach (var marker in forbidden)
            {
                Assert.False(content.Contains(marker, StringComparison.Ordinal),
                    $"non-array params residual in {Path.GetFileName(file)}: '{marker}'");
            }
        }
    }

    /// <summary>
    /// Runs the strip-default chain, retrying once on failure. The full
    /// ProjectTests suite runs many chain tests in parallel, each spawning
    /// dotnet children; a child can die transiently (exit code -1) under that
    /// load. Two consecutive failures still fail the test.
    /// </summary>
    private static async Task<CliChainResult> RunStripDefaultWithRetryAsync(
        string sourcePath, string outputRoot, ParamsCollectionsOptions? paramsOptions, bool verify)
    {
        var result = await RunStripDefaultAsync(sourcePath, outputRoot, paramsOptions, verify);
        if (!result.Success)
        {
            result = await RunStripDefaultAsync(sourcePath, outputRoot, paramsOptions, verify);
        }

        return result;
    }

    [Fact]
    public async Task ParamsOn_MaterializesDeclarations_ExplicitizesCallSites_TreeBuildsAndTestsPass()
    {
        var slnPath = Path.Combine(FixtureDir, "SampleParamsCollections.sln");
        var outputRoot = Path.Combine(FixtureDir, ".test-output-params-on");
        try
        {
            var result = await RunStripDefaultWithRetryAsync(
                slnPath, outputRoot, new ParamsCollectionsOptions(Enabled: true), verify: true);

            Assert.True(result.Success,
                "strip-default with params-collections enabled failed (see chain diagnostics)");
            Assert.Contains(IntermediateDir, result.ExecutedIntermediateDirectoryNames);

            var intermediate = Path.Combine(outputRoot, IntermediateDir);

            // Lib: every materializable params declaration lost its modifier,
            // the parameter type is unchanged (strategy B).
            var lib = await ReadIntermediateAsync(
                intermediate, "SampleParamsCollectionsLib", "ParamsCollections.cs");
            AssertDoesNotContainFlat("params ReadOnlySpan<int> values", lib);
            AssertDoesNotContainFlat("params Span<int> xs", lib);
            AssertDoesNotContainFlat("params IReadOnlyList<int> values", lib);
            AssertDoesNotContainFlat("params List<int> items", lib);
            AssertContainsFlat("public static string FormatInts(ReadOnlySpan<int> values)", lib);
            AssertContainsFlat("public static void ZeroFirst(Span<int> xs)", lib);
            AssertContainsFlat("public static int SumAll(IReadOnlyList<int> values)", lib);
            AssertContainsFlat("public static List<int> Batches(List<int> items)", lib);
            // K1 declarations keep their params modifier (strategy A).
            Assert.Contains("params int[] values", lib, StringComparison.Ordinal);
            Assert.Contains("params T[] items", lib, StringComparison.Ordinal);

            // Hierarchy file: the whole virtual slot family is materialized,
            // including the base ctor chain initializer rewrite.
            var hierarchies = await ReadIntermediateAsync(
                intermediate, "SampleParamsCollectionsLib", "ParamsHierarchies.cs");
            AssertDoesNotContainFlat("params IEnumerable<", hierarchies);
            AssertContainsFlat("protected FormatSinkBase(IEnumerable<int> seeds)", hierarchies);
            AssertContainsFlat("public virtual string Emit(IEnumerable<string> parts)", hierarchies);
            AssertContainsFlat("public abstract string Flush(IEnumerable<char> chars)", hierarchies);
            AssertContainsFlat("public override string Emit(IEnumerable<string> parts)", hierarchies);
            AssertContainsFlat("public override string Flush(IEnumerable<char> chars)", hierarchies);
            AssertContainsFlat("string Merge(IEnumerable<string> parts)", hierarchies);
            AssertContainsFlat("string IBatchSink.Merge(IEnumerable<string> parts)", hierarchies);
            AssertContainsFlat(
                "public ConsoleSink(int seed) : base(new global::System.Collections.Generic.List<int>(){seed, seed, seed})",
                hierarchies);
            // base.Emit(parts) — form B passthrough inside the override.
            AssertContainsFlat("base.Emit(parts)", hierarchies);

            // Tests project: the fixture's own call sites are explicitized.
            var behaviorTests = await ReadIntermediateAsync(
                intermediate, "SampleParamsCollectionsTests", "ParamsBehaviorTests.cs");
            AssertContainsFlat("ParamsLib.FormatInts(new int[]{1, 2, 3})", behaviorTests);
            AssertContainsFlat("ParamsLib.SumAll(new global::System.Collections.Generic.List<int>(){1, 2, 3, 4})", behaviorTests);
            AssertContainsFlat("ParamsLib.Batches(new global::System.Collections.Generic.List<int>(){1, 2, 3})", behaviorTests);
            AssertContainsFlat("ParamsLib.FormatInts(global::System.Array.Empty<int>())", behaviorTests);
            AssertContainsFlat("ParamsLib.SumAll(global::System.Array.Empty<int>())", behaviorTests);
            AssertContainsFlat("ParamsLib.Batches(new global::System.Collections.Generic.List<int>())", behaviorTests);
            AssertContainsFlat("ParamsLib.CopyAndZero(new int[]{5, 7})", behaviorTests);
            // K1 call sites keep the comma form (D8 off, Java varargs shape).
            AssertContainsFlat("ParamsLib.Sum(7, 8, 9)", behaviorTests);
            AssertContainsFlat("ParamsLib.JoinAll(1, 2, 3)", behaviorTests);
            // Form B passthroughs are untouched.
            AssertContainsFlat("ParamsLib.MutateBatch(list)", behaviorTests);
            AssertContainsFlat("ParamsLib.ZeroFirst(buffer)", behaviorTests);

            // Residual gate: zero non-array params modifiers anywhere.
            AssertNoNonArrayParamsResidual(intermediate);
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ParamsOff_ParamsSourcesStayUntouched()
    {
        var slnPath = Path.Combine(FixtureDir, "SampleParamsCollections.sln");
        var outputRoot = Path.Combine(FixtureDir, ".test-output-params-off");
        try
        {
            var result = await RunStripDefaultAsync(slnPath, outputRoot, paramsOptions: null, verify: false);

            Assert.True(result.Success,
                "strip-default with the params rule off must behave exactly as before C48");
            Assert.Contains(IntermediateDir, result.ExecutedIntermediateDirectoryNames);

            var intermediate = Path.Combine(outputRoot, IntermediateDir);

            // With the rule off (CLI default), the pass behavior is exactly its
            // pre-C48 one: the params declarations survive verbatim.
            var lib = await ReadIntermediateAsync(
                intermediate, "SampleParamsCollectionsLib", "ParamsCollections.cs");
            Assert.Contains("params ReadOnlySpan<int> values", lib, StringComparison.Ordinal);
            Assert.Contains("params List<int> items", lib, StringComparison.Ordinal);
            var hierarchies = await ReadIntermediateAsync(
                intermediate, "SampleParamsCollectionsLib", "ParamsHierarchies.cs");
            Assert.Contains("params IEnumerable<int> seeds", hierarchies, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Baseline_OriginalFixture_BehaviorTestsPassOnUnprocessedSource()
    {
        // The .NET-side half of the equivalence contract: the fixture's behavior
        // tests pass on the ORIGINAL source too (they run on the preprocessed
        // tree inside ParamsOn_... via VerifyTests).
        var slnPath = Path.Combine(FixtureDir, "SampleParamsCollections.sln");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"test \"{slnPath}\" --nologo",
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
