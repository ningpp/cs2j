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
/// C31 Java 半区还债门：默认预处理链（index-range 默认 on）改写
/// SampleIndexRangeSolution（9 个 slice 重载域 + hat/range 边界 + string
/// Substring），转换为 Java 后用 JDK 28 javac 真实编译 compat target/classes
/// 并启动 App 的 main —— 以 JVM 运行时值为闸门（ALL OK），取代旧的
/// 「生成代码包含 ArrayHelper.slice(」文本断言。快速门默认 Skip，慢门
/// <c>CS2J_RUN_SLOW_E2E=1</c> 执行。
/// </summary>
[Collection(ConsoleChainTestCollection.Name)]
public class IndexRangeJavaE2EProjectTests
{
    private const string FixtureName = "SampleIndexRangeSolution";

    private static string DeployedFixture =>
        Path.Combine(AppContext.BaseDirectory, FixtureName);

    private static ConversionOptions Options => new()
    {
        TypeMappingConfigPath = Path.Combine(
            AppContext.BaseDirectory, "config", "TypeMappings.json"),
    };

    [SlowE2EFact]
    public async Task Fixture_Converts_Compiles_RunsAllSliceAssertions()
    {
        var workRoot = Path.Combine(
            Path.GetTempPath(), "cs2j-c31-e2e-" + Guid.NewGuid().ToString("N"));
        var outputRoot = Path.Combine(workRoot, "out");
        var fixtureRoot = StagePrivateFixture(workRoot + "-fixture");

        try
        {
            var chain = await RunDefaultChainAsync(
                Path.Combine(fixtureRoot, FixtureName + ".sln"), outputRoot);
            Assert.True(chain.Success);
            Assert.Contains(".cs2j-index-range-src",
                chain.ExecutedIntermediateDirectoryNames);

            Assert.True(Directory.Exists(JavaE2EHarness.CompatClasses),
                "compat target/classes missing; build csharptojava-compat first: "
                + JavaE2EHarness.CompatClasses);

            SolutionLoader.EnsureMSBuildRegistered();
            using var loader = new SolutionLoader();
            var workspaceProjects = await loader.TryOpenAsync(chain.FinalSourcePath);
            Assert.NotNull(workspaceProjects);
            Assert.NotEmpty(workspaceProjects);

            // 支撑工程只作符号源；Tests 工程不进 javac 门（xunit 断言面与
            // 运行时门无关，App 已覆盖全部库行为）。
            var convertProjects = workspaceProjects
                .Where(p => !Cs2jArrayProject.IsCs2jArrayProject(p.FilePath))
                .Where(p => !Cs2jPreludeProject.IsCs2jPreludeProject(p.FilePath))
                .Where(p => !Cs2jRefOutInProject.IsCs2jRefOutInProject(p.FilePath))
                .Where(p => !Cs2jUnsignedMathProject.IsCs2jUnsignedMathProject(p.FilePath))
                .Where(p => !p.IsTestProject)
                .ToList();
            Assert.Equal(2, convertProjects.Count);

            var pipeline = new ConversionPipeline();
            var converted = new List<ConversionResult>();
            foreach (var wp in convertProjects)
            {
                var extraPaths = wp.ProjectReferences
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(Path.GetDirectoryName)
                    .Where(d => !string.IsNullOrWhiteSpace(d))
                    .ToList()!;
                converted.AddRange(await pipeline.ConvertProjectWithPartialMergeAsync(
                    wp.Directory, Options,
                    additionalSemanticProjectPaths: extraPaths,
                    projectName: wp.Name,
                    projectFilePath: wp.FilePath,
                    projectReferences: wp.ProjectReferences,
                    isTestProject: wp.IsTestProject));
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

            // 文本契约保留（快速定位回归），真正的门是下面的编译+运行。
            Assert.Contains(entries, e => e.Item2.Contains("ArrayHelper.slice("));
            Assert.Contains(entries, e => e.Item2.Contains(".substring("));

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
            Assert.Contains("ALL OK", run.StdOut);
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
    /// 会与它们的清理竞争（照搬 C40 的做法）。
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
