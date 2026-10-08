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
/// P2-5 C07 struct-value-semantics Java 半区慢门：默认预处理链（struct-value-semantics、
/// make-readonly 及其余家族全默认）改写 SampleStructValueSemanticsSolution（App + Lib），
/// 生产工程转换为 Java 后用 JDK 28 javac 真实编译 compat target/classes 并启动 App main
/// —— 以 JVM 12 行运行时值与 .NET 真身预言机逐字对拍为闸门，覆盖：
/// VS-1/1a/1b/1c 局部/数组/链式副本、VS-2 值参、VS-3 接收者（in/readonly 字段/
/// get-only 属性/is-pattern/foreach/rvalue）、VS-4 数组槽、VS-5 装箱拆箱/接口，
/// 以及 L3/L4/L5 readonly 改写后的构造器重建与 clone shim。
/// 夹具中的预期不可行残余（ref struct StackBuf，UF050/SV011）按契约只放在测试
/// 工程——测试工程是 .NET 行为预言机、不产出 Java，慢门同样只转换生产工程。
/// 快速门默认 Skip，慢门 CS2J_RUN_SLOW_E2E=1 执行。
/// </summary>
[Collection(ConsoleChainTestCollection.Name)]
public class StructValueSemanticsJavaE2EProjectTests
{
    private const string FixtureName = "SampleStructValueSemanticsSolution";

    /// <summary>.NET 真身预言机 12 行基线（dotnet run App 的标准输出），JVM 必须逐字一致。</summary>
    private const string ExpectedOutput =
        "1,2\n" +
        "6\n" +
        "1,2\n" +
        "1\n" +
        "7\n" +
        "2\n" +
        "1,1\n" +
        "2\n" +
        "4\n" +
        "1,4\n" +
        "11\n" +
        "1";

    private static string DeployedFixture =>
        Path.Combine(AppContext.BaseDirectory, FixtureName);

    private static ConversionOptions Options => new()
    {
        TypeMappingConfigPath = Path.Combine(
            AppContext.BaseDirectory, "config", "TypeMappings.json"),
    };

    [SlowE2EFact(180)]
    public async Task Fixture_Converts_Compiles_RunsValueSemanticsOracle()
    {
        var workRoot = Path.Combine(
            Path.GetTempPath(), "cs2j-p2-5-e2e-" + Guid.NewGuid().ToString("N"));
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

            // 支撑工程只作符号源；测试工程（含预期残余 ref struct StackBuf）是
            // .NET 侧行为预言机，不产出 Java——与 CLI 的生产半区语义一致。
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
            // 发射会漏掉这些链接文件。
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

            // 文本契约（快速定位回归），真正的门是下面的编译 + 运行：
            // C07 为残余可变 struct 生成 clone shim。
            Assert.Contains(entries, e => e.Item2.Contains("clone()"));

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
            Assert.DoesNotContain("Exception", run.StdErr);
            Assert.Equal(ExpectedOutput, run.StdOut.Replace("\r\n", "\n").TrimEnd());
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
                new CSharpToJava.Core.ExpressionDesugar.ExpressionDesugarOptions
                {
                    // 必须与 CLI convert-project 的默认工厂逐字对齐：C24/C35 子节
                    // 为 null 时规则整体惰性（byte-identical），make-readonly 随后
                    // 会把 `new R { V = V }` 形态误迁移成不可编译代码。
                    Interpolations = new()
                    {
                        Enabled = true,
                        Strict = true,
                        HandlerPolicy = CSharpToJava.Core.ExpressionDesugar
                            .Interpolations.HandlerUnsupportedPolicy.Error,
                        KeepFormattableString = true,
                        RewriteInExpressionTrees = false,
                        FoldConstants = true,
                    },
                    Initializers = new() { Strict = true },
                    NullConditional = new() { Strict = true },
                },
            StructValueSemanticsOptionsFactory = () =>
                new CSharpToJava.Core.StructValueSemantics.StructValueSemanticsOptions(),
            JavaKeywordOptionsFactory = () => new JavaKeywordOptions(),
            VerifyBuild = true,
            VerifyTests = true,
        });

    /// <summary>
    /// 私有 fixture 副本：其它测试类并发地在部署目录内写中间树，直接跑 sln
    /// 会与它们的清理竞争（照搬 NumericBoundary 的做法）。
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
