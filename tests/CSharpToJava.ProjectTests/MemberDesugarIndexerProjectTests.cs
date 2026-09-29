using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.MemberDesugar;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C12 indexer project-level coverage（设计 §7.2）：SampleMemberDesugarSolution
/// 跑 member-desugar 适配器，断言索引器分项统计与中间树文本；VerifyBuild/VerifyTests
/// 默认开 → run.Success 已含中间树 dotnet build + dotnet test（含跨工程 App→Lib 用点）。
/// </summary>
public class MemberDesugarIndexerProjectTests
{
    private static string FixtureDir
        => Path.Combine(AppContext.BaseDirectory, "SampleMemberDesugarSolution");

    [Fact]
    public async Task Solution_IndexersDesugared_AndIntermediateBuildsAndTests()
    {
        var solutionPath = Path.Combine(FixtureDir, "SampleMemberDesugarSolution.sln");
        Assert.True(File.Exists(solutionPath), $"Fixture not found: {solutionPath}");
        var outputDir = Path.Combine(FixtureDir, ".test-output-md-idx");
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
            Assert.True(adapter.Statistics.IndexersPlanned >= 6,
                "expected indexer plans from IndexerSamples");
            Assert.True(adapter.Statistics.IndexerReadSitesRewritten > 0);
            Assert.True(adapter.Statistics.IndexerWriteSitesRewritten > 0);
            Assert.True(adapter.Statistics.IndexerCompoundSitesExpanded > 0);
            Assert.True(adapter.Statistics.ExplicitInterfaceIndexers >= 2);

            var samples = await File.ReadAllTextAsync(
                Path.Combine(run.IntermediateRoot, "SampleMemberDesugarLib", "IndexerSamples.cs"));
            Assert.DoesNotContain("this[", samples);
            Assert.Contains("get_Item(", samples);
            Assert.Contains("set_Item(", samples);
            Assert.Contains("IRow.get_Item(", samples);
            Assert.Contains("get_Slot(", samples);
            Assert.Contains("ref int get_Item(", samples);

            var program = await File.ReadAllTextAsync(
                Path.Combine(run.IntermediateRoot, "SampleMemberDesugarApp", "Program.cs"));
            Assert.Contains("grid.set_Item(1, 2, 9)", program);
            Assert.Contains("grid.get_Item(1, 2)", program);
        }
        finally
        {
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }
}
