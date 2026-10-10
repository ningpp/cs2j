using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.MemberDesugar;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// 项目加载语法闸门：PrepareTreeAsync 在复制/编译/改写之前预解析所有 .cs，
/// 任一文件存在 Error 级解析诊断必须直接抛出 ProjectSyntaxException，
/// 语法错误不允许进入后续流水线。
/// </summary>
public class ProjectSyntaxGateTests
{
    private const string MinimalCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    private static MemberDesugarPassAdapter CreateAdapter()
        => new(
            new MemberDesugarOptions(),
            verbose: false,
            filterUnsupportedProjects: true,
            compatNamespacePrefixes: null);

    [Fact]
    public async Task InvalidSyntax_Throws_And_Does_Not_Copy_Tree()
    {
        var srcDir = Path.Combine(Path.GetTempPath(),
            "cs2j-syntax-gate-" + Guid.NewGuid().ToString("N"));
        var projectDir = Path.Combine(srcDir, "BrokenProj");
        Directory.CreateDirectory(projectDir);
        var outputDir = Path.Combine(srcDir, "out");
        try
        {
            File.WriteAllText(Path.Combine(projectDir, "BrokenProj.csproj"), MinimalCsproj);
            var brokenFile = Path.Combine(projectDir, "Broken.cs");
            File.WriteAllText(brokenFile,
                """
                namespace BrokenProj;

                public class Broken {
                """);

            var adapter = CreateAdapter();
            var ex = await Assert.ThrowsAsync<ProjectSyntaxException>(() =>
                ProjectPassRunner.PrepareTreeAsync(adapter, new ProjectPassRunRequest
                {
                    SourcePath = Path.Combine(projectDir, "BrokenProj.csproj"),
                    DestinationRoot = outputDir,
                    Force = true,
                }));

            Assert.Contains("Broken.cs", ex.Message);
            Assert.Contains("error CS", ex.Message);
            Assert.Contains("(3,", ex.Message);

            // 闸门在复制中间树之前抛出：输出目录不得出现任何已复制的源文件。
            Assert.False(
                Directory.Exists(outputDir)
                && Directory.GetFiles(outputDir, "*.cs", SearchOption.AllDirectories).Length > 0,
                "no .cs files must be copied when the syntax gate rejects the load");
        }
        finally
        {
            if (Directory.Exists(srcDir))
            {
                Directory.Delete(srcDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ErrorInside_InactiveDirectiveBranch_Is_Accepted()
    {
        var srcDir = Path.Combine(Path.GetTempPath(),
            "cs2j-syntax-gate-" + Guid.NewGuid().ToString("N"));
        var projectDir = Path.Combine(srcDir, "DirectiveProj");
        Directory.CreateDirectory(projectDir);
        var outputDir = Path.Combine(srcDir, "out");
        try
        {
            File.WriteAllText(Path.Combine(projectDir, "DirectiveProj.csproj"), MinimalCsproj);
            File.WriteAllText(Path.Combine(projectDir, "Branched.cs"),
                """
                namespace DirectiveProj;

                #if NEVER
                public class Oops {
                #endif

                public class Fine
                {
                    public int Id() => 1;
                }
                """);

            var adapter = CreateAdapter();
            var tree = await ProjectPassRunner.PrepareTreeAsync(adapter, new ProjectPassRunRequest
            {
                SourcePath = Path.Combine(projectDir, "DirectiveProj.csproj"),
                DestinationRoot = outputDir,
                Force = true,
            });

            Assert.Contains(tree.CsFiles,
                file => file.EndsWith("Branched.cs", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(srcDir))
            {
                Directory.Delete(srcDir, recursive: true);
            }
        }
    }
}
