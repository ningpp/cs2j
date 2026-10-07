using CSharpToJava.CLI;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.EnumDeclaration;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;
using CSharpToJava.Core.Workspace;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// P2-4 数值预言机 Java 半区慢门：默认预处理链（numeric-boundary/decimal-ops、
/// enum/delegate/nullable 各 family 全默认）改写 SampleNumericBoundarySolution
/// （App + Lib），转换为 Java 后用 JDK 28 javac 真实编译 compat target/classes
/// 并启动 App 的 main —— 以 JVM 运行时值为闸门（"numeric-boundary sample OK"），
/// 覆盖 C34 高精度除法/tie/scale、C22 可空算术与枚举提升、C17 增量副作用、
/// C15 无符号回绕边界。快速门默认 Skip，慢门 CS2J_RUN_SLOW_E2E=1 执行。
/// </summary>
[Collection(ConsoleChainTestCollection.Name)]
public class NumericBoundaryJavaE2EProjectTests
{
    private const string FixtureName = "SampleNumericBoundarySolution";

    private static string DeployedFixture =>
        Path.Combine(AppContext.BaseDirectory, FixtureName);

    private static ConversionOptions Options => new()
    {
        TypeMappingConfigPath = Path.Combine(
            AppContext.BaseDirectory, "config", "TypeMappings.json"),
    };

    [SlowE2EFact(144)]
    public async Task Fixture_Converts_Compiles_RunsNumericOracle()
    {
        var workRoot = Path.Combine(
            Path.GetTempPath(), "cs2j-p2-4-e2e-" + Guid.NewGuid().ToString("N"));
        var outputRoot = Path.Combine(workRoot, "out");
        var fixtureRoot = StagePrivateFixture(workRoot + "-fixture");

        try
        {
            var chain = await RunDefaultChainAsync(
                Path.Combine(fixtureRoot, FixtureName + ".sln"), outputRoot);
            Assert.True(chain.Success);

            Assert.True(Directory.Exists(JavaE2EHarness.CompatClasses),
                "compat target/classes missing; build csharptojava-compat first: "
                + JavaE2EHarness.CompatClasses);

            SolutionLoader.EnsureMSBuildRegistered();
            using var loader = new SolutionLoader();
            var workspaceProjects = await loader.TryOpenAsync(chain.FinalSourcePath);
            Assert.NotNull(workspaceProjects);
            Assert.NotEmpty(workspaceProjects);

            // 支撑工程只作符号源；本 fixture 只有 App + Lib 两个产品工程。
            var convertProjects = workspaceProjects
                .Where(p => !Cs2jArrayProject.IsCs2jArrayProject(p.FilePath))
                .Where(p => !Cs2jPreludeProject.IsCs2jPreludeProject(p.FilePath))
                .Where(p => !Cs2jRefOutInProject.IsCs2jRefOutInProject(p.FilePath))
                .Where(p => !Cs2jUnsignedMathProject.IsCs2jUnsignedMathProject(p.FilePath))
                .Where(p => !p.IsTestProject)
                .ToList();
            Assert.Equal(2, convertProjects.Count);

            // 与 CLI convert-project 的 workspace 路径一致：直接用 MSBuild
            // 编译单元，并以 Documents（含 <Compile Include="..\_Cs2j*.cs" Link>
            // 挂到中间树根的 tuple/closed-type 生成文件）为发射集。目录枚举式
            // 的 ConvertProjectWithPartialMergeAsync 会漏掉这些链接文件，javac
            // 即报「程序包 Cs2jTuples 不存在」。
            var converted = new List<ConversionResult>();
            foreach (var wp in convertProjects)
            {
                var emitFilePaths = new HashSet<string>(
                    wp.Documents
                        .Where(d => d.FilePath != null)
                        .Select(d => Path.GetFullPath(d.FilePath!)),
                    StringComparer.OrdinalIgnoreCase);
                var projectPipeline = new ProjectConversionPipeline(Options);
                converted.AddRange(await projectPipeline.ConvertProjectAsync(
                    wp.Compilation,
                    emitFilePaths,
                    wp.Name,
                    wp.FilePath,
                    wp.ProjectReferences,
                    wp.IsTestProject));
            }

            var failed = converted.Where(c => !c.Success).ToList();
            Assert.True(failed.Count == 0,
                "Java conversion produced failures:\n"
                + string.Join("\n", failed.SelectMany(f =>
                    f.Diagnostics.Select(d => d.ToString()))));
            Assert.NotEmpty(converted);

            var entries = converted
                .Where(c => !string.IsNullOrEmpty(c.FileName))
                .Select(c => (c.FileName!, c.GeneratedCode))
                .ToList();

            // 文本契约（快速定位回归），真正的门是下面的编译+运行：
            // decimal[] 默认 0m 经 compat 工厂分配；Nullable<T>.Value/拆箱
            // 经 IllegalStateException 守卫。
            Assert.Contains(entries, e => e.Item2.Contains("Decimal.array("));
            Assert.Contains(entries, e => e.Item2.Contains("Nullable.value("));

            var classesDir = JavaE2EHarness.Compile(workRoot, entries);

            string? mainClass = null;
            foreach (var (_, code) in entries)
            {
                mainClass = JavaE2EHarness.FindMainClass(code);
                if (mainClass != null) break;
            }
            Assert.NotNull(mainClass);

            var run = JavaE2EHarness.RunMain(classesDir, mainClass!);
            Assert.True(run.ExitCode == 0,
                "exit=" + run.ExitCode + "\nSTDOUT:\n" + run.StdOut + "\nSTDERR:\n" + run.StdErr);
            Assert.Contains("numeric-boundary sample OK", run.StdOut);
            Assert.DoesNotContain("Exception", run.StdErr);
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("CS2J_KEEP_E2E") != "1")
                JavaE2EHarness.TryDelete(workRoot);
        }
    }

    private static Task<CliChainResult> RunDefaultChainAsync(string slnPath, string outputRoot)
        => CliPreprocessChain.RunAsync(new CliChainRequest
        {
            SourcePath = slnPath,
            DestinationRoot = outputRoot,
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
            VerifyBuild = true,
            VerifyTests = true,
        });

    /// <summary>
    /// 私有 fixture 副本：其它测试类并发地在部署目录内写中间树，直接跑 sln
    /// 会与它们的清理竞争（照搬 IndexRange/C40 的做法）。
    /// </summary>
    private static string StagePrivateFixture(string staging)
    {
        CopyTree(DeployedFixture, staging);
        return staging;
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith(".test-output", StringComparison.Ordinal)
                || name is "bin" or "obj" or ".vs")
            {
                continue;
            }

            CopyTree(directory, Path.Combine(destination, name));
        }

        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
    }
}
