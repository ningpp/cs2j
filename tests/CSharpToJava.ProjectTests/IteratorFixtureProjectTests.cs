using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CSharpToJava.CLI.Preprocessing;
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
/// C04 M2/M3/M4 项目级 .NET 半区闸门（设计 §7.1 第 5 组）：以
/// <c>--enable-pass iterator-lowering</c> 驱动默认预处理链改写
/// SampleIteratorSolution（Lib + App + xUnit T01–T22 真身预言机）。
/// 中间树必须：零 yield 残余、生成 Cs2jIterator_ 状态机类、dotnet build 通过，
/// 且 22 个行为测试在改写后的树上 22/22 通过——覆盖枚举到一半 Dispose 的
/// finally 恰好一次、正常枚举完一次、重复枚举状态隔离、嵌套 finally 三路径、
/// yield break、List&lt;T&gt;/数组 foreach、foreach-in-try、非泛型 IEnumerable，
/// M3 的 using 块/声明/嵌套 LIFO 释放与跨 yield 的 lock 持有/释放，
/// 以及 M4 含 yield 的 switch（多 section/default、循环内、空 section 贯穿）。
/// </summary>
[Collection("SampleIteratorSolution")]
public class IteratorFixtureProjectTests
{
    private const string PassId = "iterator-lowering";
    private const string IntermediateDir = ".cs2j-iterator-lowering-src";
    private const int TruthTestCount = 22;

    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleIteratorSolution");

    private static string FixtureSlnPath =>
        Path.Combine(FixtureDir, "SampleIteratorSolution.sln");

    private static Task<CliChainResult> RunChainAsync(
        string sourcePath, string outputRoot, bool enableIteratorLowering)
        => CliPreprocessChain.RunAsync(new CliChainRequest
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
            EnablePassIds = enableIteratorLowering ? new[] { PassId } : null,
            Strict = false,
            VerifyBuild = true,
            // 链尾 lexical-normalize 闸门会跑一次 fixture 测试；下面的显式 trx
            // 运行再把 22/22 计数钉死。
            VerifyTests = true,
        });

    private static async Task<CliChainResult> RunChainWithRetryAsync(
        string sourcePath, string outputRoot, bool enableIteratorLowering)
    {
        var result = await RunChainAsync(sourcePath, outputRoot, enableIteratorLowering);
        if (!result.Success)
        {
            result = await RunChainAsync(sourcePath, outputRoot, enableIteratorLowering);
        }

        return result;
    }

    private static string Flat(string s) => Regex.Replace(s, @"\s+", string.Empty);

    private static IEnumerable<string> EnumerateCodeText(string root)
        => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file =>
            {
                var parts = Path.GetRelativePath(root, file)
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return !parts.Contains("bin", StringComparer.OrdinalIgnoreCase)
                    && !parts.Contains("obj", StringComparer.OrdinalIgnoreCase);
            })
            .Select(File.ReadAllText);

    private static int CountAllIntermediate(string root, string needle)
        => EnumerateCodeText(root).Sum(text =>
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
        });

    private static bool AnyFileContains(string root, string needle)
        => EnumerateCodeText(root).Any(text =>
            Flat(text).Contains(Flat(needle), StringComparison.Ordinal));

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

    private static (TrxCounters Counters, IReadOnlyList<string> FailedNames) ReadTrx(string trxPath)
    {
        var doc = XDocument.Load(trxPath);
        var ns = doc.Root!.Name.Namespace;

        var countersElement = doc.Descendants(ns + "Counters").FirstOrDefault();
        Assert.NotNull(countersElement);
        var counters = new TrxCounters(
            Total: (int?)countersElement!.Attribute("total") ?? -1,
            Passed: (int?)countersElement.Attribute("passed") ?? -1,
            Failed: (int?)countersElement.Attribute("failed") ?? -1);

        var failedNames = doc.Descendants(ns + "UnitTestResult")
            .Where(r => (string?)r.Attribute("outcome") == "Failed")
            .Select(r => (string?)r.Attribute("testName") ?? string.Empty)
            .ToList();

        return (counters, failedNames);
    }

    // ── 默认关闭：不显式 enable 时不产出中间树 ──

    [Fact]
    public async Task IteratorLowering_OffByDefault_ChainProducesNoIntermediateTree()
    {
        var workDir = Path.Combine(Path.GetTempPath(),
            "cs2j-c04-off-" + Guid.NewGuid().ToString("N"));
        var outputRoot = Path.Combine(workDir, "out");
        try
        {
            var result = await RunChainAsync(FixtureSlnPath, outputRoot, false);

            Assert.True(result.Success, "default chain must succeed without the opt-in pass");
            Assert.DoesNotContain(IntermediateDir, result.ExecutedIntermediateDirectoryNames);
            Assert.False(Directory.Exists(Path.Combine(outputRoot, IntermediateDir)),
                "iterator-lowering is default-off: no intermediate tree may be produced");
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
        }
    }

    // ── 基线：原始树上 T01–T22 全绿 ──

    [Fact]
    public async Task Baseline_OriginalFixture_AllTruthTestsPassOnUnprocessedSource()
    {
        var resultsDirectory = Path.Combine(Path.GetTempPath(),
            "cs2j-c04-baseline-" + Guid.NewGuid().ToString("N"));
        try
        {
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
            var (counters, failed) = ReadTrx(outcome.Value.TrxPath!);
            Assert.Equal(TruthTestCount, counters.Total);
            Assert.Equal(TruthTestCount, counters.Passed);
            Assert.Equal(0, counters.Failed);
            Assert.Empty(failed);
        }
        finally
        {
            if (Directory.Exists(resultsDirectory)) Directory.Delete(resultsDirectory, true);
        }
    }

    // ── 核心：开启 pass 后中间树 build + T01–T22 全绿 ──

    [Fact]
    public async Task IteratorLoweringOn_LoweredTreeBuilds_AndAllTruthTestsMatchNetTruth()
    {
        var workDir = Path.Combine(Path.GetTempPath(),
            "cs2j-c04-on-" + Guid.NewGuid().ToString("N"));
        var outputRoot = Path.Combine(workDir, "out");
        var resultsDirectory = Path.Combine(workDir, "trx");
        try
        {
            var result = await RunChainWithRetryAsync(FixtureSlnPath, outputRoot, true);

            Assert.True(result.Success,
                "iterator-lowering chain (build-verified) failed unexpectedly");
            Assert.Contains(IntermediateDir, result.ExecutedIntermediateDirectoryNames);

            var intermediate = Path.Combine(outputRoot, IntermediateDir);
            Assert.True(Directory.Exists(intermediate), "intermediate tree missing");

            // yield 残余闸门：生产树（Lib/App）中不再有任何 yield。
            Assert.Equal(0, CountAllIntermediate(intermediate, "yield return"));
            Assert.Equal(0, CountAllIntermediate(intermediate, "yield break"));
            // 状态机类与活动区域字段就位。
            Assert.True(AnyFileContains(intermediate, "Cs2jIterator_"),
                "state machine classes missing from intermediate tree");
            Assert.True(AnyFileContains(intermediate, "_cs2jActiveRegion"),
                "protected-region state field missing from intermediate tree");

            var intermediateSln = Path.Combine(intermediate, "SampleIteratorSolution.sln");
            var (exitCode, trxPath) = await RunFixtureTestsAsync(
                intermediateSln, resultsDirectory, "intermediate.trx");

            Assert.Equal(0, exitCode);
            Assert.NotNull(trxPath);
            var (counters, failed) = ReadTrx(trxPath!);
            Assert.Equal(TruthTestCount, counters.Total);
            Assert.Equal(TruthTestCount, counters.Passed);
            Assert.Equal(0, counters.Failed);
            Assert.Empty(failed);
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
        }
    }
}
