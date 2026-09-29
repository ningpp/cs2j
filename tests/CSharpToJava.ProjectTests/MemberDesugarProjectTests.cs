using System.Text;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.MemberDesugar;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C11 project-level coverage (design §7.2)：直接构造 MemberDesugarPassAdapter +
/// ProjectPassRunner（无独立 verb 门面，设计 §5.2）。VerifyBuild/VerifyTests 默认开，
/// run.Success 即证明中间树通过 dotnet build + dotnet test（BehaviorTests 为
/// 去糖前/后行为等价闸门）。
/// </summary>
[Collection("SampleMemberDesugarSolution")]
public class MemberDesugarProjectTests
{
    private static string FixtureDir
        => Path.Combine(AppContext.BaseDirectory, "SampleMemberDesugarSolution");

    [Fact]
    public async Task Solution_PropertiesDesugared_AndIntermediateBuildsAndTests()
    {
        var solutionPath = Path.Combine(FixtureDir, "SampleMemberDesugarSolution.sln");
        Assert.True(File.Exists(solutionPath), $"Fixture not found: {solutionPath}");
        var outputDir = Path.Combine(FixtureDir, ".test-output-md-sln");
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
            Assert.True(adapter.Statistics.PropertiesPlanned > 0,
                "expected at least one planned property");
            Assert.True(adapter.Statistics.BackingFieldsGenerated > 0,
                "expected at least one generated backing field");
            Assert.True(adapter.Statistics.ReadSitesRewritten > 0);
            Assert.True(adapter.Statistics.WriteSitesRewritten > 0);
            Assert.True(adapter.Statistics.InitializersRewritten > 0);

            var dtos = await File.ReadAllTextAsync(
                Path.Combine(run.IntermediateRoot, "SampleMemberDesugarLib", "DtoSamples.cs"));
            Assert.Contains("get_Total()", dtos);
            Assert.Contains("set_Quantity(", dtos);
            Assert.DoesNotContain("public decimal Total =>", dtos);
            Assert.Contains("get_Counter()", dtos);

            var interfaces = await File.ReadAllTextAsync(
                Path.Combine(run.IntermediateRoot, "SampleMemberDesugarLib", "InterfaceSamples.cs"));
            Assert.Contains("get_FullName()", interfaces);
            Assert.Contains("IPerson.get_FirstName()", interfaces);

            var behavior = await File.ReadAllTextAsync(
                Path.Combine(run.IntermediateRoot, "SampleMemberDesugarTests", "BehaviorTests.cs"));
            Assert.Contains("get_Total()", behavior);
            Assert.Contains("\"Total\"", behavior); // nameof 字面量化

            // VerifyBuild/VerifyTests 默认开 → run.Success 已证明中间树构建 + 测试通过。
        }
        finally
        {
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Skipped_Properties_KeepDeclarations_AndRunStillSucceeds()
    {
        var solutionPath = Path.Combine(FixtureDir, "SampleMemberDesugarSolution.sln");
        Assert.True(File.Exists(solutionPath), $"Fixture not found: {solutionPath}");
        var outputDir = Path.Combine(FixtureDir, ".test-output-md-skip");
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

            // 表达式树（CS2J5117）与不可提升静态初始化器（CS2J5119）都必须保守跳过。
            Assert.Contains(adapter.Diagnostics,
                d => d.Code == MemberDesugarDiagnosticCode.ExpressionTreeSite);
            Assert.Contains(adapter.Diagnostics,
                d => d.Code == MemberDesugarDiagnosticCode.NonLiftableInitializer);

            var skipSamples = await File.ReadAllTextAsync(
                Path.Combine(run.IntermediateRoot, "SampleMemberDesugarLib", "SkipSamples.cs"));
            Assert.Contains("public int Erp { get; set; }", skipSamples);
            Assert.Contains("public string Label { get; set; }", skipSamples);
        }
        finally
        {
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Clean_Tree_Is_Byte_Copied_AndBomPreserved()
    {
        var srcDir = Path.Combine(Path.GetTempPath(),
            "cs2j-md-clean-" + Guid.NewGuid().ToString("N"));
        var projectDir = Path.Combine(srcDir, "CleanProj");
        Directory.CreateDirectory(projectDir);
        var outputDir = Path.Combine(srcDir, "out");
        try
        {
            File.WriteAllText(Path.Combine(projectDir, "CleanProj.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(projectDir, "NoProps.cs"),
                """
                namespace CleanProj;

                public class NoProps
                {
                    private int _v;

                    public int V() => _v;

                    public void Set(int value) => _v = value;
                }
                """);
            // 带 BOM 的干净文件：初始 File.Copy 必须逐字节保留。
            File.WriteAllText(Path.Combine(projectDir, "WithBom.cs"),
                """
                namespace CleanProj;

                public static class WithBom
                {
                    public static int Id() => 1;
                }
                """, new UTF8Encoding(true));

            var adapter = new MemberDesugarPassAdapter(
                new MemberDesugarOptions(), verbose: false,
                filterUnsupportedProjects: true, compatNamespacePrefixes: null);
            var run = await ProjectPassRunner.RunAsync(adapter, new ProjectPassRunRequest
            {
                SourcePath = Path.Combine(projectDir, "CleanProj.csproj"),
                DestinationRoot = outputDir,
                Force = true,
            });

            Assert.True(run.Success, string.Join("\n",
                adapter.Diagnostics.Where(d => d.Severity == MemberDesugarSeverity.Error)
                    .Select(d => d.ToMsBuildString())));
            Assert.Equal(0, adapter.Statistics.PropertiesPlanned);
            Assert.True(adapter.Statistics.FilesSkippedClean >= 2);

            foreach (var file in Directory.GetFiles(run.IntermediateRoot, "*.*",
                         SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(run.IntermediateRoot, file);
                if (Path.GetFileName(relative) is "Directory.Build.targets" or "Directory.Build.props")
                    continue; // 树准备阶段注入的构建文件，不属于字节拷贝比对
                var parts = relative.Split(Path.DirectorySeparatorChar);
                if (parts.Contains("obj") || parts.Contains("bin"))
                    continue; // restore/verify 产生的构建产物
                var source = Path.Combine(projectDir, relative);
                Assert.True(File.Exists(source), $"unexpected intermediate file: {relative}");
                Assert.True(
                    File.ReadAllBytes(file).SequenceEqual(File.ReadAllBytes(source)),
                    $"file was modified on a clean tree: {relative}");
            }

            var bomFile = Path.Combine(run.IntermediateRoot, "WithBom.cs");
            Assert.True(File.Exists(bomFile));
            using var stream = File.OpenRead(bomFile);
            Span<byte> prefix = stackalloc byte[3];
            var read = stream.Read(prefix);
            Assert.True(read >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF,
                "BOM must survive the byte copy");
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
        }
    }
}
