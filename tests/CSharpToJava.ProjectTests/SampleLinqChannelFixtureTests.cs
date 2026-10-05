using System.Diagnostics;
using System.Text.Json;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.EnumDeclaration;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.LinqChannel;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C26 fixture gate for SampleLinqChannelSolution (SampleLinqLib +
/// SampleLinqApp + SampleLinqTests).
/// <list type="bullet">
/// <item><see cref="Baseline_OriginalFixture_Builds_AndAllTruthTestsPass"/>:
/// the unprocessed solution builds and its eight xUnit truth tests pass
/// (.NET half of the equivalence contract; four Enumerable/Queryable M1-M2
/// pins plus four M3 pins: recording provider quoted-call sequence, mixed D7
/// chain, two-source query syntax, hand-built expression tree);</item>
/// <item><see cref="ChannelRun_IntermediateTree_Builds_PassesTruthTests_AndEmitsV2Report"/>:
/// the real linq-channel preprocess chain over the solution (M3 permanent
/// gate + §7.10 residual scan) succeeds, the materialized
/// <c>.cs2j-linq-channel-src</c> tree builds in Release and passes the SAME
/// eight truth tests, and the schemaVersion 2 report carries the exact
/// routing counters with zero residual findings; and</item>
/// <item><see cref="ChannelRun_QueryableFile_IsByteIdenticalCopy"/>.</item>
/// </list>
/// The §5.3 double-run suppression (channel run ⇒ no embedded
/// cs2j-linq-report.json; plain run ⇒ report present) is pinned end to end by
/// <see cref="LinqChannelGateM2ProjectTests.ConvertProject_ChannelDoubleRun_SuppressesEmbeddedReport_AndWithoutChannelWritesIt"/>.
/// It is deliberately not duplicated here: a full convert-project Java run
/// over this fixture's IQueryable repository hits the pre-existing
/// struct-value-semantics boundary limitation (M3 owns Java-side Queryable
/// handling), while the intermediate-tree contract covered here is the
/// channel's own surface.
/// </summary>
public sealed class SampleLinqChannelFixtureTests
{
    private const string PassId = "linq-channel";
    private const string IntermediateDirName = ".cs2j-linq-channel-src";
    private const string ChannelReportFileName = "cs2j-linq-channel-report.json";

    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleLinqChannelSolution");

    private static string FixtureSlnPath =>
        Path.Combine(FixtureDir, "SampleLinqChannelSolution.sln");

    [Fact]
    public async Task Baseline_OriginalFixture_Builds_AndAllTruthTestsPass()
    {
        var resultsDirectory = Path.Combine(FixtureDir, ".test-output-baseline-trx");
        try
        {
            // Retry once for transient parallel-suite child failures.
            var exitCode = -1;
            var succeededAttempt = -1;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                exitCode = await RunDotNetAsync(
                    "test", FixtureSlnPath, resultsDirectory, $"baseline-{attempt}.trx");
                if (exitCode == 0)
                {
                    succeededAttempt = attempt;
                    break;
                }
            }

            Assert.Equal(0, exitCode);

            var trxPath = Path.Combine(resultsDirectory, $"baseline-{succeededAttempt}.trx");
            Assert.True(File.Exists(trxPath), "missing baseline trx from the successful run");
            var content = await File.ReadAllTextAsync(trxPath);
            Assert.Contains("passed=\"8\"", content);
            Assert.Contains("failed=\"0\"", content);
        }
        finally
        {
            DeleteQuiet(resultsDirectory);
        }
    }

    [Fact]
    public async Task ChannelRun_IntermediateTree_Builds_PassesTruthTests_AndEmitsV2Report()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(),
            "cs2j-c26-m2-fixture-" + Guid.NewGuid().ToString("N"));
        var sourceDir = Path.Combine(tempRoot, "src");
        var outputDir = Path.Combine(tempRoot, "out");
        var resultsDirectory = Path.Combine(tempRoot, "trx");
        try
        {
            CopyFixtureSources(sourceDir);

            var errorCapture = new StringWriter();
            var originalError = Console.Error;
            CliChainResult result;
            try
            {
                Console.SetError(errorCapture);
                result = await RunChainAsync(
                    Path.Combine(sourceDir, "SampleLinqChannelSolution.sln"),
                    outputDir,
                    () => new LinqChannelOptions());
            }
            finally
            {
                Console.SetError(originalError);
            }

            Assert.True(
                result.Success,
                "linq-channel chain (with Release build verification) must succeed.\nstderr:\n"
                + errorCapture);
            Assert.Contains(IntermediateDirName, result.ExecutedIntermediateDirectoryNames);

            var intermediateRoot = Path.Combine(outputDir, IntermediateDirName);
            var intermediateSln = Path.Combine(
                intermediateRoot, "SampleLinqChannelSolution.sln");
            Assert.True(File.Exists(intermediateSln),
                "intermediate tree must contain the copied solution");
            Assert.True(Directory.EnumerateFiles(
                intermediateRoot, "*.cs", SearchOption.AllDirectories).Any(),
                "intermediate tree must contain the processed C# sources");

            // The adapter already verifies the Release build, but the fixture
            // contract pins both commands explicitly against the tree on disk.
            var buildExit = RunDotNetBuild(intermediateSln);
            Assert.Equal(0, buildExit);

            var testExit = await RunDotNetAsync(
                "test", intermediateSln, resultsDirectory, "channel-fixture.trx");
            Assert.Equal(0, testExit);
            var trxPath = Path.Combine(resultsDirectory, "channel-fixture.trx");
            Assert.True(File.Exists(trxPath));
            var trx = await File.ReadAllTextAsync(trxPath);
            Assert.Contains("passed=\"8\"", trx);
            Assert.Contains("failed=\"0\"", trx);

            // ---- schemaVersion 2 report ----
            var reportPath = Path.Combine(intermediateRoot, ChannelReportFileName);
            Assert.True(File.Exists(reportPath), "channel report must be written");
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            var root = doc.RootElement;
            Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());

            // Top-level counters/sections.
            _ = root.GetProperty("desugaredQueryCount");
            _ = root.GetProperty("rewrittenChainCount");
            _ = root.GetProperty("rewrittenMethodCount");
            _ = root.GetProperty("residualFindings");
            _ = root.GetProperty("skippedChains");
            _ = root.GetProperty("preservedChains");
            _ = root.GetProperty("uncoveredOperators");
            Assert.Equal(JsonValueKind.Array, root.GetProperty("preservedChains").ValueKind);
            Assert.Equal(JsonValueKind.Array, root.GetProperty("uncoveredOperators").ValueKind);

            // Exact M3 fixture routing counts (measured 2026-10-05): one
            // two-source query desugars; LinqTools/QueryableScenarios expand
            // 12 Enumerable chains in 8 methods; the Queryable repository, the
            // recording-provider scenario and the query-test calls freeze as
            // 8 Queryable chains (10 Expression<> argument sites) and 2 D7
            // mixed chains; the §7.10 residual scan reports zero findings.
            Assert.Equal(1, root.GetProperty("desugaredQueryCount").GetInt32());
            Assert.Equal(8, root.GetProperty("rewrittenChainCount").GetInt32());
            Assert.Equal(8, root.GetProperty("rewrittenMethodCount").GetInt32());
            Assert.Equal(0, root.GetProperty("residualFindings").GetInt32());

            // All eight routing counters are present and exact.
            var routing = root.GetProperty("routing");
            Assert.Equal(12, routing.GetProperty("enumerableChainsExpanded").GetInt32());
            Assert.Equal(8, routing.GetProperty("queryableChainsPreserved").GetInt32());
            Assert.Equal(10, routing.GetProperty("expressionArgumentSites").GetInt32());
            Assert.Equal(2, routing.GetProperty("mixedChainsPreserved").GetInt32());
            Assert.Equal(0, routing.GetProperty("ambiguousChains").GetInt32());
            Assert.Equal(0, routing.GetProperty("providerCustomCalls").GetInt32());
            Assert.Equal(0, routing.GetProperty("expressionQuotedChainsPreserved").GetInt32());
            Assert.Equal(0, routing.GetProperty("expressionTreeUnprovableSites").GetInt32());
        }
        finally
        {
            DeleteQuiet(tempRoot);
        }
    }

    [Fact]
    public async Task ChannelRun_QueryableFile_IsByteIdenticalCopy()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(),
            "cs2j-c26-m2-fixture-touch-" + Guid.NewGuid().ToString("N"));
        var sourceDir = Path.Combine(tempRoot, "src");
        var outputDir = Path.Combine(tempRoot, "out");
        try
        {
            CopyFixtureSources(sourceDir);

            var result = await RunChainAsync(
                Path.Combine(sourceDir, "SampleLinqChannelSolution.sln"),
                outputDir,
                () => new LinqChannelOptions());
            Assert.True(result.Success);

            // The Queryable-only file is frozen node-for-node: intercept mode
            // returns the original subtree, no stage-1 query syntax exists in
            // it, so the intermediate copy must be byte-identical.
            var original = await File.ReadAllBytesAsync(
                Path.Combine(sourceDir, "SampleLinqLib", "IThingRepository.cs"));
            var produced = await File.ReadAllBytesAsync(
                Path.Combine(outputDir, IntermediateDirName, "SampleLinqLib", "IThingRepository.cs"));
            Assert.Equal(original, produced);
        }
        finally
        {
            DeleteQuiet(tempRoot);
        }
    }

    // ---- plumbing ----

    private static void CopyFixtureSources(string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        foreach (var directory in Directory.EnumerateDirectories(
                     FixtureDir, "*", SearchOption.AllDirectories)
                     .Where(d => !d.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                         && !d.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                         && !d.Contains(".test-output", StringComparison.Ordinal)))
        {
            var relative = Path.GetRelativePath(FixtureDir, directory);
            Directory.CreateDirectory(Path.Combine(destinationRoot, relative));
        }

        foreach (var file in Directory.EnumerateFiles(
                     FixtureDir, "*", SearchOption.AllDirectories)
                     .Where(f =>
                     {
                         var relative = Path.GetRelativePath(FixtureDir, f);
                         return !relative.Split(Path.DirectorySeparatorChar)
                                    .Any(part => part is "bin" or "obj")
                                && !relative.Contains(".test-output", StringComparison.Ordinal);
                     }))
        {
            var relative = Path.GetRelativePath(FixtureDir, file);
            var dest = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private static Task<CliChainResult> RunChainAsync(
        string sourcePath,
        string outputDir,
        Func<LinqChannelOptions> channelOptionsFactory)
    {
        var registry = PassCatalog.BuildRegistry();
        return CliPreprocessChain.RunAsync(new CliChainRequest
        {
            SourcePath = sourcePath,
            DestinationRoot = outputDir,
            MakerOptionsFactory = () => new ReadOnlyStructMakerOptions(),
            RecordReadOnlyOptionsFactory = () => new RecordReadOnlyOptions(),
            MemberDesugarOptionsFactory = () => new MemberDesugarOptions(),
            EnumDeclarationOptionsFactory = () => new EnumDeclarationOptions(),
            LexicalNormalizeOptionsFactory = () =>
                new CSharpToJava.Core.LexicalNormalize.LexicalNormalizeOptions(),
            EqualityConcatOptionsFactory = () =>
                new CSharpToJava.Core.EqualityConcat.EqualityConcatOptions(),
            UnsafeFfmOptionsFactory = () =>
                new CSharpToJava.Core.UnsafeFfm.UnsafeFfmOptions(),
            ExpressionDesugarOptionsFactory = () =>
                new CSharpToJava.Core.ExpressionDesugar.ExpressionDesugarOptions(),
            StructValueSemanticsOptionsFactory = () =>
                new CSharpToJava.Core.StructValueSemantics.StructValueSemanticsOptions(),
            JavaKeywordOptionsFactory = () => new JavaKeywordOptions(),
            LinqChannelOptionsFactory = channelOptionsFactory,
            LegacyToggles = PassChainFactory.BuildAllDisabledToggles(registry),
            EnablePassIds = new[] { PassId },
            VerifyBuild = true,
            VerifyTests = false,
        });
    }

    private static int RunDotNetBuild(string targetPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{targetPath}\" --nologo -c Release"
                + " -p:GenerateResourceUsePreserializedResources=true",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        using var process = Process.Start(startInfo)!;
        _ = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }

    private static async Task<int> RunDotNetAsync(
        string verb, string targetPath, string resultsDirectory, string trxFileName)
    {
        Directory.CreateDirectory(resultsDirectory);
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"{verb} \"{targetPath}\" --nologo -c Release"
                + " -p:GenerateResourceUsePreserializedResources=true "
                + $"--logger \"trx;LogFileName={trxFileName}\" "
                + $"--results-directory \"{resultsDirectory}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";

        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(15));
        _ = await stdoutTask + await stderrTask;

        return process.ExitCode;
    }

    private static void DeleteQuiet(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Temp cleanup is best effort under parallel test execution.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
