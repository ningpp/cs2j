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

    /// <summary>忽略空白的包含断言（合成表达式会被格式化器折行）。</summary>
    private static void AssertContainsNormalized(string haystack, string needle)
        => Assert.True(
            new string(haystack.Where(c => !char.IsWhiteSpace(c)).ToArray())
                .Contains(new string(needle.Where(c => !char.IsWhiteSpace(c)).ToArray()),
                    StringComparison.Ordinal),
            $"Expected to contain: {needle}");

    private static void AssertDoesNotContainNormalized(string haystack, string needle)
        => Assert.False(
            new string(haystack.Where(c => !char.IsWhiteSpace(c)).ToArray())
                .Contains(new string(needle.Where(c => !char.IsWhiteSpace(c)).ToArray()),
                    StringComparison.Ordinal),
            $"Expected NOT to contain: {needle}");

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

    /// <summary>
    /// 画像暴露（2026-10-09，csharp-sdk json TrimmingTests）：CLI 树准备按目录枚举收集
    /// .cs，不读 msbuild Compile 项。EnableDefaultCompileItems=false 的项目里，不参与
    /// 真实编译的同源镜像文件（每个文件本应独立成程序）被合入同一个 Roslyn 编译；
    /// 属性去糖后镜像类型两侧的 get_X 撞 CS0082，锚点落在已回退 plan 保留的属性侧，
    /// 严格归属失败 → 旧行为 CS2J5100 全树回滚，连真实参与编译文件的有效改写也被吞掉。
    /// 新行为：新错误仅能锚定已排除 plan 时接受本轮；真实 breakage 仍由 msbuild 验证兜底。
    /// </summary>
    [Fact]
    public async Task Phantom_Mirror_Types_Outside_CompileItems_Do_Not_Trigger_WholeTree_Rollback()
    {
        var srcDir = Path.Combine(Path.GetTempPath(),
            "cs2j-md-mirror-" + Guid.NewGuid().ToString("N"));
        var projectDir = Path.Combine(srcDir, "MirrorProj");
        Directory.CreateDirectory(projectDir);
        var outputDir = Path.Combine(srcDir, "out");
        try
        {
            File.WriteAllText(Path.Combine(projectDir, "MirrorProj.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Real.cs" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(projectDir, "Real.cs"),
                """
                namespace MirrorProj;

                public class Real
                {
                    public int Value { get; set; }
                }
                """);
            // 两个镜像文件：不被 csproj 编译，但会被 CLI 目录枚举合入同一 Roslyn 编译。
            var mirrorA =
                """
                namespace MirrorHarness;

                internal class Program
                {
                    static int Main() => 0;
                }

                internal class ClassWithDay
                {
                    public int Day { get; set; }
                }
                """;
            File.WriteAllText(Path.Combine(projectDir, "Phantom1.cs"), mirrorA);
            File.WriteAllText(Path.Combine(projectDir, "Phantom2.cs"), mirrorA);

            var adapter = new MemberDesugarPassAdapter(
                new MemberDesugarOptions(), verbose: false,
                filterUnsupportedProjects: false, compatNamespacePrefixes: null);
            var run = await ProjectPassRunner.RunAsync(adapter, new ProjectPassRunRequest
            {
                SourcePath = Path.Combine(projectDir, "MirrorProj.csproj"),
                DestinationRoot = outputDir,
                Force = true,
            });

            Assert.True(run.Success, string.Join("\n",
                adapter.Diagnostics.Where(d => d.Severity == MemberDesugarSeverity.Error)
                    .Select(d => d.ToMsBuildString())));
            Assert.DoesNotContain(adapter.Diagnostics,
                d => d.Code == MemberDesugarDiagnosticCode.WholeTreeRollback);

            // 真实参与编译文件上的有效属性改写不得被镜像碰撞连带回滚。
            var realOutput = File.ReadAllText(Path.Combine(run.IntermediateRoot, "Real.cs"));
            AssertContainsNormalized(realOutput, "int get_Value()");
            AssertDoesNotContainNormalized(realOutput, "public int Value { get; set; }");
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
        }
    }

    /// <summary>摘要行所有段必须完成插值（2026-10-09 修复 operators/residual 段漏 $ 前缀）。</summary>
    [Fact]
    public void Chain_Summary_Line_Has_No_Uninterpolated_Placeholders()
    {
        var adapter = new MemberDesugarPassAdapter(
            new MemberDesugarOptions(), verbose: false,
            filterUnsupportedProjects: false, compatNamespacePrefixes: null);
        var line = adapter.BuildChainSummaryLine();
        Assert.DoesNotContain("{", line);
        Assert.DoesNotContain("}", line);
    }

}
