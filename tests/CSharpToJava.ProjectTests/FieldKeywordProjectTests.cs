using System.Text.RegularExpressions;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.MemberDesugar.Properties;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C50 项目级覆盖（设计 §7.4）：SampleMemberDesugarSolution 全 sln 跑 member-desugar。
/// A 档（fieldKeyword 默认关）：12 个 field-keyword 属性全部原样保留 + CS2J5122 跳过，
/// 中间树 build + xunit（含 FieldKeywordBehaviorTests）绿；
/// B 档（fieldKeyword.enabled=true）：12 个属性归一为显式后备字段 + get_X/set_X，
/// 使用点跨 sln 改写，中间树 build + 同一批行为测试仍绿（A/B 行为等价）。
/// </summary>
[Collection("SampleMemberDesugarSolution")]
public class FieldKeywordProjectTests
{
    private static string FixtureDir
        => Path.Combine(AppContext.BaseDirectory, "SampleMemberDesugarSolution");

    // 夹具内 field-keyword 属性总数（FieldKeywordSamples.cs）。
    private const int FixtureFieldKeywordProperties = 12;

    // 访问器体内 field 引用总条数：INPC 3 / lazy 1 / 校验 2 / get-only+set-only 2 /
    // 静态 2 / virtual 2 / override 2 / readonly struct 1 / DTO 4 / 初始化器副作用 2。
    private const int FixtureFieldReferences = 21;

    // 属性初始化器移字段：Inpc.Name / Dto.Secret / InitializerSideEffect.Weight。
    private const int FixtureMovedInitializers = 3;

    private static readonly Regex ResidualFieldUse = new(
        @"=>\s*field\b|\bfield\s*(\?\?=|==|=[^=]|\*)|\[field:",
        RegexOptions.Compiled);

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

    /// <summary>夹具含 FinalizerCritical（C39 CS2J5205 恒定 Error，与 C50 无关）：
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

    // ---- A 档：默认关闭，零改树，12×CS2J5122 声明级跳过 ----

    [Fact]
    public async Task TierA_DefaultDisabled_KeepsAllFieldKeywordProperties_With5122Skip()
    {
        var solutionPath = Path.Combine(FixtureDir, "SampleMemberDesugarSolution.sln");
        var workDir = Path.Combine(Path.GetTempPath(),
            "cs2j-fk-a-" + Guid.NewGuid().ToString("N"));
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

            Assert.Equal(0, adapter.Statistics.FieldKeywordProperties);
            Assert.Equal(0, adapter.Statistics.FieldKeywordResidualTokens);
            Assert.Equal(FixtureFieldKeywordProperties,
                adapter.Diagnostics.Count(d =>
                    d.Code == MemberDesugarDiagnosticCode.FieldKeywordAccessor));

            var intermediateFile = Path.Combine(
                run.IntermediateRoot, "SampleMemberDesugarLib", "FieldKeywordSamples.cs");
            var text = await File.ReadAllTextAsync(intermediateFile);
            Assert.Contains("[field: NonSerialized]", text, StringComparison.Ordinal);
            Assert.Contains("=> field", text, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }

    // ---- B 档：启用归一，显式字段 + 方法，行为测试跨 sln 全绿 ----

    [Fact]
    public async Task TierB_Enabled_NormalizesAllProperties_AndBehaviorTestsStayGreen()
    {
        var solutionPath = Path.Combine(FixtureDir, "SampleMemberDesugarSolution.sln");
        var workDir = Path.Combine(Path.GetTempPath(),
            "cs2j-fk-b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var destination = Path.Combine(workDir, "out");

        try
        {
            var options = new MemberDesugarOptions
            {
                FieldKeyword = new FieldKeywordOptions { Enabled = true },
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
            Assert.Equal(FixtureFieldKeywordProperties, stats.FieldKeywordProperties);
            Assert.Equal(FixtureFieldReferences, stats.FieldReferencesRewritten);
            Assert.Equal(FixtureMovedInitializers, stats.FieldInitializersMoved);
            Assert.Equal(1, stats.FieldTargetAttributesMigrated);
            Assert.Equal(0, stats.FieldKeywordResidualTokens);

            // ---- 无任何跳过/安全网/容器/特性诊断 ----
            foreach (var code in new[]
                     {
                         MemberDesugarDiagnosticCode.FieldKeywordAccessor,
                         MemberDesugarDiagnosticCode.FieldKeywordSafetyNet,
                         MemberDesugarDiagnosticCode.FieldKeywordUnsupportedContainer,
                         MemberDesugarDiagnosticCode.FieldKeywordAttributeFailed,
                     })
            {
                Assert.DoesNotContain(adapter.Diagnostics, d => d.Code == code);
            }

            // ---- 归一留痕：每个属性一条 CS2J5125 Info（ReportInfo 默认开）----
            Assert.Equal(FixtureFieldKeywordProperties,
                adapter.Diagnostics.Count(d =>
                    d.Code == MemberDesugarDiagnosticCode.FieldKeywordNormalized));

            // ---- 中间树文本断言：field 关键字零残留，特性落字段 ----
            var intermediateFile = Path.Combine(
                run.IntermediateRoot, "SampleMemberDesugarLib", "FieldKeywordSamples.cs");
            var text = await File.ReadAllTextAsync(intermediateFile);
            // 注释里保留了 [field:] / field 等说明文字，剥掉纯注释行后做残留断言。
            var codeLines = text.Split('\n')
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal));
            var codeOnly = string.Join('\n', codeLines);
            Assert.DoesNotMatch(ResidualFieldUse, codeOnly);
            Assert.DoesNotContain("[field:", codeOnly, StringComparison.Ordinal);
            Assert.Contains("[NonSerialized]", codeOnly, StringComparison.Ordinal);
            Assert.Contains("public string secret = \"shh\";", text, StringComparison.Ordinal);
            Assert.Contains("public int weight = 10;", text, StringComparison.Ordinal);
            Assert.Contains("public string name = \"init\";", text, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        }
    }
}
