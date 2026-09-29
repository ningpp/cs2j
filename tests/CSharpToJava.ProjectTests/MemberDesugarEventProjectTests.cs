using System.Text;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.MemberDesugar;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C13 project-level coverage (design §7.2)：直接构造 MemberDesugarPassAdapter +
/// ProjectPassRunner。VerifyBuild/VerifyTests 默认开，run.Success 即证明中间树通过
/// dotnet build + dotnet test——同时实证事件改写后适配器的 Cs2jPrelude 释放与
/// ProjectReference 注入（事件访问器体引用 CsPrelude.Combine/Remove）。
/// </summary>
/// <remarks>
/// 与 <see cref="MemberDesugarProjectTests"/> 同集合串行：两个类的输出目录都嵌在
/// 共享 fixture 目录（bin\...\SampleMemberDesugarSolution\.test-output-*）下，并行运行时
/// 一方的 fixture 拷贝会扫入另一方的活动输出树，随后被其 finally 删除打断
/// （DirectoryNotFoundException）。同集合保证互斥。
/// </remarks>
[Collection("SampleMemberDesugarSolution")]
public class MemberDesugarEventProjectTests
{
    private static string FixtureDir
        => Path.Combine(AppContext.BaseDirectory, "SampleMemberDesugarSolution");

    [Fact]
    public async Task Solution_EventsDesugared_AndIntermediateBuildsAndTests()
    {
        var solutionPath = Path.Combine(FixtureDir, "SampleMemberDesugarSolution.sln");
        Assert.True(File.Exists(solutionPath), $"Fixture not found: {solutionPath}");
        var outputDir = Path.Combine(FixtureDir, ".test-output-md-event-sln");
        try
        {
            var adapter = new MemberDesugarPassAdapter(
                new MemberDesugarOptions(), verbose: false,
                filterUnsupportedProjects: true, compatNamespacePrefixes: null);
            var run = await ProjectPassRunner.RunAsync(adapter, new ProjectPassRunRequest
            {
                SourcePath = solutionPath,
                DestinationRoot = outputDir,
                Force = true,
            });

            Assert.True(run.Success, string.Join("\n",
                adapter.Diagnostics.Where(d => d.Severity == MemberDesugarSeverity.Error)
                    .Select(d => d.ToMsBuildString())));
            Assert.True(adapter.Statistics.EventsPlanned > 0,
                "expected at least one planned event");
            Assert.True(adapter.Statistics.EventBackingFieldsGenerated > 0);
            Assert.True(adapter.Statistics.EventSubscribeSitesRewritten > 0,
                "expected cross-project subscribe sites");

            var samples = await File.ReadAllTextAsync(
                Path.Combine(run.IntermediateRoot, "SampleMemberDesugarLib", "EventSamples.cs"));
            Assert.Contains("add_Basic(", samples);
            Assert.Contains("remove_Generic(", samples);
            Assert.Contains("Cs2jPrelude.CsPrelude.Combine", samples);
            Assert.DoesNotContain("event EventHandler? Basic;", samples);
            Assert.Contains("public event Action? value;", samples); // 保留名 skip 对照：原样保留

            var program = await File.ReadAllTextAsync(
                Path.Combine(run.IntermediateRoot, "SampleMemberDesugarApp", "Program.cs"));
            Assert.Contains("add_Basic(onBasic);", program);
            Assert.Contains("remove_Basic(onBasic);", program);

            var behavior = await File.ReadAllTextAsync(
                Path.Combine(run.IntermediateRoot, "SampleMemberDesugarTests", "BehaviorTests.cs"));
            Assert.Contains("add_Changed(h1);", behavior);

            // 保留名事件应有 CS2J5157 跳过诊断，其余无 residual
            Assert.Contains(adapter.Diagnostics,
                d => d.Code == MemberDesugarDiagnosticCode.EventNameCollision);
            var residual = adapter.Statistics.SkippedByReason.TryGetValue(
                MemberDesugarDiagnosticCode.ResidualEvent, out var r) ? r : 0;
            Assert.Equal(0, residual);
        }
        finally
        {
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Skipped_ReservedNameEvent_KeepsDeclaration_AndSitesWork()
    {
        var solutionPath = Path.Combine(FixtureDir, "SampleMemberDesugarSolution.sln");
        var outputDir = Path.Combine(FixtureDir, ".test-output-md-event-skip");
        try
        {
            var adapter = new MemberDesugarPassAdapter(
                new MemberDesugarOptions(), verbose: false,
                filterUnsupportedProjects: true, compatNamespacePrefixes: null);
            var run = await ProjectPassRunner.RunAsync(adapter, new ProjectPassRunRequest
            {
                SourcePath = solutionPath,
                DestinationRoot = outputDir,
                Force = true,
            });

            Assert.True(run.Success, string.Join("\n",
                adapter.Diagnostics.Where(d => d.Severity == MemberDesugarSeverity.Error)
                    .Select(d => d.ToMsBuildString())));

            var samples = await File.ReadAllTextAsync(
                Path.Combine(run.IntermediateRoot, "SampleMemberDesugarLib", "EventSamples.cs"));
            // skip 对照事件声明原样保留，且其类内触发点（同名字段绑定语义）未受影响
            Assert.Contains("public event Action? value;", samples);
        }
        finally
        {
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }
}
