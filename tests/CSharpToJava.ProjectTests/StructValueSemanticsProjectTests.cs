using System.Text;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.StructValueSemantics;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C07 struct-value-semantics 项目级测试（设计 §7.3/§7.5）：
/// 直接构造 StructValueSemanticsPassAdapter + ProjectPassRunner。
/// run.Success 即证明中间树通过 dotnet build + dotnet test（fixture 的
/// ValueSemanticsTests 在插了 clone 的 C# 上验证值语义恒等）。
/// </summary>
public class StructValueSemanticsProjectTests
{
    private static string FixtureDir
        => Path.Combine(AppContext.BaseDirectory, "SampleStructValueSemanticsSolution");

    private static StructValueSemanticsPassAdapter CreateAdapter(
        StructValueSemanticsOptions? options = null)
        => new(
            options ?? new StructValueSemanticsOptions(),
            verbose: false,
            filterUnsupportedProjects: true,
            compatNamespacePrefixes: null);

    private static string CreateTempProject(
        string root,
        string projectName,
        string csharpSource,
        string? extraPropertyGroup = null)
    {
        var projectDir = Path.Combine(root, projectName);
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, projectName + ".csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <IsPackable>false</IsPackable>
            """ + extraPropertyGroup + """
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(projectDir, "File.cs"), csharpSource);
        return Path.Combine(projectDir, projectName + ".csproj");
    }

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

    // ───────────────────────── §7.3 fixture sln 全链路 ─────────────────────────

    [Fact]
    public async Task Fixture_RewritesBuildsAndTests_WithExpectedStatsAndText()
    {
        var fixture = FixtureDir;
        Assert.True(Directory.Exists(fixture),
            "SampleStructValueSemanticsSolution must be copied to the test output directory");
        var solutionPath = Path.Combine(fixture, "SampleStructValueSemanticsSolution.sln");
        var workDir = Path.Combine(Path.GetTempPath(),
            "cs2j-svs-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var destination = Path.Combine(workDir, "out");

        try
        {
            var adapter = CreateAdapter();
            var run = await ProjectPassRunner.RunAsync(adapter, new ProjectPassRunRequest
            {
                SourcePath = solutionPath,
                DestinationRoot = destination,
                Force = true,
            });

            Assert.True(run.Success, string.Join("\n",
                adapter.Diagnostics
                    .Where(d => d.Severity == StructValueSemanticsSeverity.Error)
                    .Select(d => d.ToMsBuildString())));

            // 5 个 plan：Point/Line/Polygon/BuildSettings 生成 shim，Reusable 复用。
            Assert.Equal(5, adapter.Statistics.StructsPlanned);
            Assert.Equal(4, adapter.Statistics.ShimsGenerated);
            Assert.Equal(1, adapter.Statistics.StructsReusedClone);
            Assert.Equal(1, adapter.Statistics.PlansSkipped); // Counter（SV051）
            // is-pattern 声明局部是普通可写局部（拆箱已产生独立副本），不进只读
            // 槽、不提升防御副本——较旧基线少 1 站点。
            Assert.True(adapter.Statistics.SitesRewritten >= 29,
                $"expected >=29 rewritten sites, got {adapter.Statistics.SitesRewritten}");

            // SV051（Warning）与 SV011/SV012（Info）残余类型均有显式诊断。
            Assert.Contains(adapter.Diagnostics, d =>
                d.Code == StructValueSemanticsDiagnosticCode.SV051
                && d.Severity == StructValueSemanticsSeverity.Warning);
            Assert.Contains(adapter.Diagnostics, d =>
                d.Code == StructValueSemanticsDiagnosticCode.SV011);
            Assert.Contains(adapter.Diagnostics, d =>
                d.Code == StructValueSemanticsDiagnosticCode.SV012);
            Assert.DoesNotContain(adapter.Diagnostics, d =>
                d.Severity == StructValueSemanticsSeverity.Error);

            var root = run.IntermediateRoot;

            var program = await File.ReadAllTextAsync(
                Path.Combine(root, "SampleStructValueSemanticsApp", "Program.cs"));
            Assert.Contains("a.Clone()", program);
            Assert.Contains("__cs2jVc0", program); // holder.Anchor rvalue 接收者
            Assert.Contains("__cs2jSc0", program); // 链式赋值提升
            Assert.DoesNotContain("Geometry.Origin.Clone().Translate",
                program.Replace(" ", "").Replace("\n", "").Replace("\r", ""));
            // 可写局部的字段直接变异，不提升。
            Assert.Contains("line.Start.Translate(1, 0);", program);
            // 查询方法不提升。
            Assert.Contains("Geometry.Origin.Peek()", program);

            var shapes = await File.ReadAllTextAsync(
                Path.Combine(root, "SampleStructValueSemanticsLib", "Shapes.cs"));
            AssertContainsNormalized(shapes, "public Point Clone() => new Point(this);");
            AssertContainsNormalized(shapes, "private Point(Point other)");
            // 嵌套可变 struct 字段递归 Clone（构造 + 拷贝构造）。
            Assert.Contains("Start = start.Clone();", shapes);
            Assert.Contains("other.Start.Clone()", shapes);
            // in 参/装箱/拆箱站点。
            Assert.Contains("var __cs2jVc0 = p.Clone();", shapes);
            Assert.Contains("object boxed = p.Clone();", shapes);
            Assert.Contains("return ((Point)boxed).Clone();", shapes);
            // readonly 字段接收者提升。
            Assert.Contains("var __cs2jVc1 = _anchor.Clone();", shapes);

            // Reusable 复用既有 Clone，不生成拷贝构造。
            AssertDoesNotContainNormalized(shapes, "private Reusable(Reusable other)");
            AssertContainsNormalized(shapes,
                "public Reusable Clone() => new Reusable { V = V };");

            // Counter（SV051）不生成 shim；Lib/Residuals.cs（SV012/SV051）与原文
            // 逐字节一致。SV011 的 ref struct StackBuf 按 UF050 残余契约放在测试
            // 工程（测试工程是 .NET 行为预言机、不产出 Java）。
            var residualsOut = Path.Combine(root, "SampleStructValueSemanticsLib", "Residuals.cs");
            var residualsSrc = Path.Combine(fixture, "SampleStructValueSemanticsLib", "Residuals.cs");
            Assert.True(
                File.ReadAllBytes(residualsOut).SequenceEqual(File.ReadAllBytes(residualsSrc)),
                "Residuals.cs (SV011/SV012/SV051) must be byte-identical");
            var residualsText = await File.ReadAllTextAsync(residualsOut);
            Assert.DoesNotContain("Clone()", residualsText);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    // ───────────────────────── 干净工程字节复制 ─────────────────────────

    [Fact]
    public async Task Clean_Tree_Is_Byte_Copied()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "cs2j-svs-clean-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var outputDir = Path.Combine(root, "out");
        try
        {
            var projectPath = CreateTempProject(root, "CleanProj", """
                namespace CleanProj;

                public sealed class Node
                {
                    public int Value { get; set; }
                }
                """);

            var adapter = CreateAdapter();
            var run = await ProjectPassRunner.RunAsync(adapter, new ProjectPassRunRequest
            {
                SourcePath = projectPath,
                DestinationRoot = outputDir,
                Force = true,
            });

            Assert.True(run.Success, string.Join("\n",
                adapter.Diagnostics.Select(d => d.ToMsBuildString())));
            Assert.Equal(0, adapter.Statistics.StructsPlanned);
            Assert.Equal(0, adapter.Statistics.SitesRewritten);

            foreach (var file in Directory.GetFiles(run.IntermediateRoot, "*.*",
                         SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(run.IntermediateRoot, file);
                if (Path.GetFileName(relative) is "Directory.Build.targets" or "Directory.Build.props")
                    continue; // 树准备阶段注入的构建文件
                var parts = relative.Split(Path.DirectorySeparatorChar);
                if (parts.Contains("obj") || parts.Contains("bin"))
                    continue;
                // csproj 输入时布局的 SourceRoot 即项目目录本身，必须以 run.SourceRoot 为基。
                var source = Path.Combine(run.SourceRoot, relative);
                Assert.True(File.Exists(source), $"unexpected intermediate file: {relative}");
                Assert.True(
                    File.ReadAllBytes(file).SequenceEqual(File.ReadAllBytes(source)),
                    $"file was modified on a clean tree: {relative}");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ───────────────────────── M5-a：回退演练 ─────────────────────────

    /// <summary>
    /// SV031：struct 自带签名恰为 <c>Point(Point)</c> 的拷贝构造，但标注了
    /// <c>[Obsolete(…, error: true)]</c>。基线代码无人调用它所以可编译；shim 决策
    /// 复用该签名只合成 <c>Clone() =&gt; new Point(this)</c>，整树重编译时 Clone 体内
    /// 触发 CS0619 新错误且唯一归属该 plan → 计划级回退，重放后零错误交付
    /// （SV031 Warning，文件全部还原）。
    /// </summary>
    [Fact]
    public async Task ObsoleteCopyCtor_NewError_TriggersSV031_PlanRollback()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "cs2j-svs-sv031-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var outputDir = Path.Combine(root, "out");
        try
        {
            var projectPath = CreateTempProject(root, "RollbackProj", """
                namespace RollbackProj;

                public struct Point
                {
                    public int X;

                    // 无人调用时基线可编译；合成的克隆工厂复用该签名 → CS0619。
                    [System.Obsolete("do not copy", true)]
                    public Point(Point other) { X = other.X; }

                    public Point(int x) { X = x; }
                }

                public static class Runner
                {
                    public static void Go()
                    {
                        var a = new Point(1);
                        var b = a;
                        System.Console.WriteLine(b.X);
                    }
                }
                """);

            var adapter = CreateAdapter();
            var run = await ProjectPassRunner.RunAsync(adapter, new ProjectPassRunRequest
            {
                SourcePath = projectPath,
                DestinationRoot = outputDir,
                Force = true,
            });

            Assert.True(run.Success, string.Join("\n",
                adapter.Diagnostics.Select(d => d.ToMsBuildString())));

            var sv031 = Assert.Single(adapter.Diagnostics, d =>
                d.Code == StructValueSemanticsDiagnosticCode.SV031
                && d.Severity == StructValueSemanticsSeverity.Warning);
            Assert.Equal("RollbackProj.Point", sv031.StructName);
            Assert.Equal(0, adapter.Statistics.SitesRewritten);

            // 回退文件与原文逐字节一致，且无 shim 残留。
            var outFile = Path.Combine(run.IntermediateRoot, "File.cs");
            var srcFile = Path.Combine(root, "RollbackProj", "File.cs");
            Assert.True(File.ReadAllBytes(outFile).SequenceEqual(File.ReadAllBytes(srcFile)),
                "rolled-back file must be byte-identical to source");
            Assert.DoesNotContain("Clone()", await File.ReadAllTextAsync(outFile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>SV040（this 经 ref 实参逃逸）：plan 期 Error → pass 失败 + 中间目录全量回退。</summary>
    [Fact]
    public async Task ThisEscape_SV040_Fails_AndRemovesIntermediateTree()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "cs2j-svs-sv040-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var outputDir = Path.Combine(root, "out");
        try
        {
            var projectPath = CreateTempProject(root, "EscapeProj", """
                namespace EscapeProj;

                public struct Box
                {
                    public int X;
                    public void Hand(ref Box other) { other.X++; }
                    public void Escape() { Hand(ref this); }
                }
                """);

            var adapter = CreateAdapter();
            var run = await ProjectPassRunner.RunAsync(adapter, new ProjectPassRunRequest
            {
                SourcePath = projectPath,
                DestinationRoot = outputDir,
                Force = true,
            });

            Assert.False(run.Success);
            Assert.Contains(adapter.Diagnostics, d =>
                d.Code == StructValueSemanticsDiagnosticCode.SV040
                && d.Severity == StructValueSemanticsSeverity.Error);
            Assert.False(Directory.Exists(run.IntermediateRoot),
                "SV040 failure must leave no intermediate tree residue (design §7.5)");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>SV043：语句无法提升的表达式位置防御接收者 → Error + 中间目录全量回退。</summary>
    [Fact]
    public async Task DefensiveReceiver_ExpressionPosition_SV043_Fails_AndRemovesIntermediateTree()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "cs2j-svs-sv043-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var outputDir = Path.Combine(root, "out");
        try
        {
            var projectPath = CreateTempProject(root, "HoistProj", """
                namespace HoistProj;

                public struct Point
                {
                    public int X;
                    public int Bump() { X++; return X; }
                    private static readonly Point Origin = new();
                    public static int Use() { var y = Origin.Bump(); return y; }
                }
                """);

            var adapter = CreateAdapter();
            var run = await ProjectPassRunner.RunAsync(adapter, new ProjectPassRunRequest
            {
                SourcePath = projectPath,
                DestinationRoot = outputDir,
                Force = true,
            });

            Assert.False(run.Success);
            Assert.Contains(adapter.Diagnostics, d =>
                d.Code == StructValueSemanticsDiagnosticCode.SV043
                && d.Severity == StructValueSemanticsSeverity.Error);
            Assert.False(Directory.Exists(run.IntermediateRoot),
                "SV043 failure must leave no intermediate tree residue (design §7.5)");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 幂等：对已改写中间树再跑一次，零站点改写、产物与输入逐字节一致
    /// （既有 Clone()/拷贝构造被识别为 SV010 复用）。
    /// </summary>
    [SlowE2EFact(11.88)]
    public async Task RewrittenTree_Is_Idempotent_OnSecondRun()
    {
        var fixture = FixtureDir;
        var solutionPath = Path.Combine(fixture, "SampleStructValueSemanticsSolution.sln");
        var workDir = Path.Combine(Path.GetTempPath(),
            "cs2j-svs-idem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var firstDest = Path.Combine(workDir, "out1");
        var secondDest = Path.Combine(workDir, "out2");

        try
        {
            var first = CreateAdapter();
            var run1 = await ProjectPassRunner.RunAsync(first, new ProjectPassRunRequest
            {
                SourcePath = solutionPath,
                DestinationRoot = firstDest,
                Force = true,
            });
            Assert.True(run1.Success, string.Join("\n",
                first.Diagnostics.Select(d => d.ToMsBuildString())));
            Assert.True(first.Statistics.SitesRewritten > 0);

            var second = CreateAdapter();
            var run2 = await ProjectPassRunner.RunAsync(second, new ProjectPassRunRequest
            {
                SourcePath = run1.PreprocessedSourcePath,
                DestinationRoot = secondDest,
                Force = true,
            });
            Assert.True(run2.Success, string.Join("\n",
                second.Diagnostics.Select(d => d.ToMsBuildString())));
            Assert.Equal(0, second.Statistics.SitesRewritten);
            Assert.Equal(5, second.Statistics.StructsPlanned);
            Assert.Equal(5, second.Statistics.StructsReusedClone);

            // 第二次输出与第一次中间树（.cs 源文件）逐字节一致。
            foreach (var file in Directory.GetFiles(run2.IntermediateRoot, "*.cs",
                         SearchOption.AllDirectories))
            {
                var parts = file.Split(Path.DirectorySeparatorChar);
                if (parts.Contains("obj") || parts.Contains("bin")) continue;
                var relative = Path.GetRelativePath(run2.IntermediateRoot, file);
                var firstFile = Path.Combine(run1.IntermediateRoot, relative);
                Assert.True(File.Exists(firstFile), $"missing first-tree file: {relative}");
                Assert.True(
                    File.ReadAllBytes(file).SequenceEqual(File.ReadAllBytes(firstFile)),
                    $"second run modified a rewritten file: {relative}");
            }
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }
}
