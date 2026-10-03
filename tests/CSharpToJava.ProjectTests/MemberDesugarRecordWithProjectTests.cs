using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.MemberDesugar.Records;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C27 record with 项目级覆盖（设计 §7.4）：SampleMemberDesugarSolution 全 sln 跑 member-desugar。
/// A 档（records 默认关）：record with 零改树，CloneFactoriesSynthesized=0，中间树 build + xunit 绿；
/// B 档（records.enabled=true）：位置 record reshape + 虚克隆工厂注入 + with 改写，
/// 中间树 build + 同一批 RecordWithBehaviorTests 仍绿（A/B 行为等价）。
/// </summary>
[Collection("SampleMemberDesugarSolution")]
public class MemberDesugarRecordWithProjectTests
{
    private static string FixtureDir
        => Path.Combine(AppContext.BaseDirectory, "SampleMemberDesugarSolution");

    // RecordWithSamples.cs 中的 record 类型数（9 个：PositionPoint / RecordExplicitPerson /
    // ShapeBase / Circle / Rectangle / Vec2 / NoBodyToken / RequiredNote / SkipCarrier）。
    private const int FixtureRecordTypes = 9;

    // with 站点总数 10：MovePoint 1 / Birthday 1 / Recolor 1 / Add 1（readonly struct
    // 命中 PositionalReshapeGate 保留原生 with，仍计入站点）/ Refresh 1 / NestedWith 2 /
    // Reclone 空初始化器 1 / Retag 元组 1 / ReNote 1 = 10；SkipCarrier 字段初始化器
    // 站点命中 5223 整 plan 不改，不计入。
    private const int FixtureWithSites = 10;

    // 实际改写站点 9（10 减去 Vec2 5220 闸门 1）；克隆工厂 7（6 个 reshape 的位置
    // class record 中 ShapeBase/Circle/Rectangle 各一 + PositionPoint/NoBodyToken/
    // RequiredNote = 6，加显式成员 RecordExplicitPerson = 7；struct/tuple/跳过载体无工厂）。
    private const int FixtureRewrittenSites = 9;
    private const int FixtureCloneFactories = 7;
    private const int FixturePositionalReshaped = 6;

    private static readonly string[] FixtureRecordTypeNames =
    {
        "PositionPoint", "RecordExplicitPerson", "ShapeBase", "Circle", "Rectangle",
        "Vec2", "NoBodyToken", "RequiredNote", "SkipCarrier",
    };

    private static MemberDesugarPassAdapter CreateAdapter(MemberDesugarOptions options)
        => new(options, verbose: false, filterUnsupportedProjects: true,
            compatNamespacePrefixes: null);

    private static void AssertVerifyGreen(MemberDesugarPassAdapter adapter)
    {
        foreach (var gateCode in new[]
                 {
                     MemberDesugarDiagnosticCode.BuildFailed,
                     MemberDesugarDiagnosticCode.TestsFailed,
                     MemberDesugarDiagnosticCode.RestoreFailed,
                     MemberDesugarDiagnosticCode.WholeTreeRollback,
                 })
        {
            Assert.DoesNotContain(adapter.Diagnostics, d => d.Code == gateCode);
        }
    }

    /// <summary>夹具含 FinalizerCritical（C39 CS2J5205 恒定 Error，与 C27 无关）：
    /// 唯一允许的 Error 即该码，verify（build+test）必须全绿。</summary>
    private static void AssertOnlyCriticalFinalizerError(MemberDesugarPassAdapter adapter)
    {
        var errorCodes = adapter.Diagnostics
            .Where(d => d.Severity == MemberDesugarSeverity.Error)
            .Select(d => d.Code).ToList();
        Assert.All(errorCodes, code =>
            Assert.Equal(MemberDesugarDiagnosticCode.FinalizerCriticalBlocked, code));
        Assert.Single(errorCodes);
        AssertVerifyGreen(adapter);
    }

    private static bool IsSamplesFile(MemberDesugarDiagnostic d)
        => (d.FilePath ?? string.Empty).EndsWith(
            "RecordWithSamples.cs", StringComparison.OrdinalIgnoreCase);

    private static int CountOnSamplesFile(MemberDesugarPassAdapter adapter, MemberDesugarDiagnosticCode code)
        => adapter.Diagnostics.Count(d => d.Code == code && IsSamplesFile(d));

    // ---- A 档：默认关闭，零改树 ----

    [Fact]
    public async Task TierA_DefaultDisabled_ZeroDiff_NoCloneFactories()
    {
        var solutionPath = Path.Combine(FixtureDir, "SampleMemberDesugarSolution.sln");
        var workDir = Path.Combine(Path.GetTempPath(),
            "cs2j-rec-a-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var destination = Path.Combine(workDir, "out");

        try
        {
            var adapter = CreateAdapter(new MemberDesugarOptions());
            var run = await ProjectPassRunner.RunAsync(adapter, new ProjectPassRunRequest
            {
                SourcePath = solutionPath,
                DestinationRoot = destination,
                Force = true,
            });

            Assert.False(run.Success, "夹具含 1 个 CS2J5205 恒定 Error（C39 critical 终结器）");
            AssertOnlyCriticalFinalizerError(adapter);

            // Records 默认关：零工厂、零 with 站点、零 residual。
            Assert.Equal(0, adapter.Statistics.CloneFactoriesSynthesized);
            Assert.Equal(0, adapter.Statistics.WithSitesTotal);
            Assert.Equal(0, adapter.Statistics.ResidualWithCount);
            Assert.Equal(0, adapter.Statistics.PositionalRecordsReshaped);

            // 中间树 with 关键字原样保留，无任何 C27 产物/诊断。
            var intermediateFile = Path.Combine(
                run.IntermediateRoot, "SampleMemberDesugarLib", "RecordWithSamples.cs");
            var text = await File.ReadAllTextAsync(intermediateFile);
            Assert.Contains(" with ", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Cs2jWithClone", text, StringComparison.Ordinal);
            Assert.DoesNotContain("__cs2j_wclone", text, StringComparison.Ordinal);
            Assert.DoesNotContain(adapter.Diagnostics,
                d => d.Code == MemberDesugarDiagnosticCode.NonLiftableWithContext);
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }

    // ---- B 档：启用去糖，虚工厂 + with 改写，行为测试跨 sln 全绿 ----

    [Fact]
    public async Task TierB_Enabled_RewritesAllWithSites_AndBehaviorTestsStayGreen()
    {
        var solutionPath = Path.Combine(FixtureDir, "SampleMemberDesugarSolution.sln");
        var workDir = Path.Combine(Path.GetTempPath(),
            "cs2j-rec-b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var destination = Path.Combine(workDir, "out");

        try
        {
            var options = new MemberDesugarOptions
            {
                Records = new RecordOptions { Enabled = true },
            };
            var adapter = CreateAdapter(options);
            var run = await ProjectPassRunner.RunAsync(adapter, new ProjectPassRunRequest
            {
                SourcePath = solutionPath,
                DestinationRoot = destination,
                Force = true,
            });

            Assert.False(run.Success, "夹具含 1 个 CS2J5205 恒定 Error（C39 critical 终结器）");
            AssertOnlyCriticalFinalizerError(adapter);

            // ---- 统计对账 ----
            var stats = adapter.Statistics;
            Assert.Equal(FixtureWithSites, stats.WithSitesTotal);
            Assert.Equal(FixtureCloneFactories, stats.CloneFactoriesSynthesized);
            Assert.Equal(FixturePositionalReshaped, stats.PositionalRecordsReshaped);
            Assert.Equal(0, stats.ResidualWithCount);

            // ---- 跳过族（均 Warning，中间树相应片段保留原生 with，行为测试双档通过）----
            // Vec2 是 readonly record struct：值拷贝直写不可行 → 5220 整 record 不 reshape。
            Assert.Equal(1, stats.SkippedByReason[MemberDesugarDiagnosticCode.PositionalReshapeGate]);
            // SkipCarrier 的 with 位于静态字段初始化器 → 5223 整 plan 原子保留。
            Assert.Equal(1, CountOnSamplesFile(adapter,
                MemberDesugarDiagnosticCode.NonLiftableWithContext));
            // RequiredNote 的 required 字段剥离留痕（CS2J5215 Info）。
            Assert.Equal(1, CountOnSamplesFile(adapter,
                MemberDesugarDiagnosticCode.RequiredRecordTouched));
            // 无 residual 安全网诊断。
            Assert.DoesNotContain(adapter.Diagnostics,
                d => d.Code == MemberDesugarDiagnosticCode.ResidualWith);

            // ---- 中间树文本断言 ----
            var intermediateFile = Path.Combine(
                run.IntermediateRoot, "SampleMemberDesugarLib", "RecordWithSamples.cs");
            var text = await File.ReadAllTextAsync(intermediateFile);

            // 9 个 record 类型全部在中间树可见。
            Assert.Equal(FixtureRecordTypes, FixtureRecordTypeNames.Length);
            foreach (var name in FixtureRecordTypeNames)
            {
                Assert.Contains(name, text, StringComparison.Ordinal);
            }

            // 克隆工厂注入 + 实际改写站点留痕（9 条 WithSiteRewritten Info）。
            Assert.Contains("Cs2jWithClone", text, StringComparison.Ordinal);
            Assert.Equal(FixtureRewrittenSites, adapter.Diagnostics.Count(
                d => d.Code == MemberDesugarDiagnosticCode.WithSiteRewritten && IsSamplesFile(d)));

            // C27 修复核心：元组克隆局部显式标注站点【闭合】元组类型，
            // 绝不发射开放定义形参名 (T1, T2)。
            Assert.Contains("(int A, string B) __cs2j_wclone", text, StringComparison.Ordinal);
            Assert.DoesNotContain("(T1, T2) __cs2j_wclone", text, StringComparison.Ordinal);

            // 空初始化器：箭头体内联工厂调用（无提升临时量）。
            Assert.Contains("=> p.Cs2jWithClone();", text, StringComparison.Ordinal);

            // 跳过形态：原生 with 片段原样保留。
            Assert.Contains("(Seed with { X = 1 }).X", text, StringComparison.Ordinal);
            Assert.Contains("a with { X = a.X + b.X, Y = a.Y + b.Y }",
                text, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }
}
