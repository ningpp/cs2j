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
/// C04 M2 Java 半区慢门：默认预处理链 + <c>--enable-pass iterator-lowering</c>
/// 改写 SampleIteratorSolution（App + Lib），yield 迭代器被显式状态机 + goto
/// CFG 取代，随后 eliminate-goto 把 goto 重写为结构化循环；生产工程再转换为
/// Java，用 JDK 28 javac + compat target/classes 真实编译并启动 App main，
/// JVM 输出必须与 .NET 真身预言机（xUnit T01–T14 同一组行为）逐字一致。
/// 覆盖：for/List&lt;T&gt;/数组 foreach、try-finally 保护区域、嵌套 finally、
/// yield break、提前 Dispose 时序、重复枚举状态隔离、非泛型 IEnumerable。
/// 测试工程是 .NET 行为预言机、不产出 Java，慢门同样只转换生产工程。
/// 快速门默认 Skip，慢门 CS2J_RUN_SLOW_E2E=1 执行。
/// </summary>
[Collection(ConsoleChainTestCollection.Name)]
public class IteratorJavaE2EProjectTests
{
    private const string FixtureName = "SampleIteratorSolution";

    /// <summary>.NET 真身预言机基线（dotnet run App 的标准输出），JVM 必须逐字一致。</summary>
    private const string ExpectedOutput =
        "range:10,11,12,13\n" +
        "evens:2,4,6\n" +
        "tens:10,20,30\n" +
        "guarded0:1,2,3\n" +
        "guarded0-finally:1\n" +
        "guarded1:1,2\n" +
        "guarded1-finally:1\n" +
        "nested0:1,2,3,4\n" +
        "nested0-final:1,1\n" +
        "nested1:1,2\n" +
        "nested1-final:1,1\n" +
        "listintry:7,8,9\n" +
        "listintry-finally:1\n" +
        "early:1:0\n" +
        "interleave:2,2:0\n" +
        "repeat-final:2\n" +
        "mixed:1,two";

    private static string DeployedFixture =>
        Path.Combine(AppContext.BaseDirectory, FixtureName);

    private static ConversionOptions Options => new()
    {
        TypeMappingConfigPath = Path.Combine(
            AppContext.BaseDirectory, "config", "TypeMappings.json"),
    };

    [SlowE2EFact(300)]
    public async Task Fixture_Converts_Compiles_RunsIteratorOracle()
    {
        var workRoot = Path.Combine(
            Path.GetTempPath(), "cs2j-c04-e2e-" + Guid.NewGuid().ToString("N"));
        var outputRoot = Path.Combine(workRoot, "out");
        var fixtureRoot = StagePrivateFixture(workRoot + "-fixture");

        try
        {
            var chain = await RunChainAsync(
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

            // 支撑工程只作符号源；测试工程是 .NET 侧行为预言机，不产出 Java。
            var convertProjects = workspaceProjects
                .Where(p => !Cs2jArrayProject.IsCs2jArrayProject(p.FilePath))
                .Where(p => !Cs2jPreludeProject.IsCs2jPreludeProject(p.FilePath))
                .Where(p => !Cs2jRefOutInProject.IsCs2jRefOutInProject(p.FilePath))
                .Where(p => !Cs2jUnsignedMathProject.IsCs2jUnsignedMathProject(p.FilePath))
                .Where(p => !p.IsTestProject)
                .ToList();
            Assert.Equal(2, convertProjects.Count);

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
            {
                JavaE2EHarness.TryDelete(workRoot);
            }
        }
    }

    private static Task<CliChainResult> RunChainAsync(string slnPath, string outputRoot)
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
            EnablePassIds = new[] { "iterator-lowering" },
            Strict = false,
            VerifyBuild = true,
            VerifyTests = true,
        });

    /// <summary>私有 fixture 副本，避免与并发链测试在部署目录内互相竞争。</summary>
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
