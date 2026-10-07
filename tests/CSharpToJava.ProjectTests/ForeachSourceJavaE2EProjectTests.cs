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
/// C40 Java 半区还债门：默认预处理链（foreach-source 默认 on）改写
/// SampleForeachSourceSolution 的 duck-typed foreach 家族（普通 pattern /
/// extension GetEnumerator / 泛型与非泛型 IEnumerator 装箱 / IDisposable
/// try-finally / 值类型 enumerator 不装箱 / 显式接口 Dispose）与
/// string foreach（ToCharArray 包裹、UTF-16 code units、装箱、单次求值），
/// Java 半区只转换纯引用面 Lib（ref struct / Span 夹具只存在于 Tests
/// 工程，UF050 不进此门），用 JDK 28 javac 真实编译并以测试内置 JVM
/// harness 跑运行时值断言（ALL OK）。快速门默认 Skip，慢门
/// <c>CS2J_RUN_SLOW_E2E=1</c> 执行。
/// </summary>
[Collection(ConsoleChainTestCollection.Name)]
public class ForeachSourceJavaE2EProjectTests
{
    private const string FixtureName = "SampleForeachSourceSolution";
    private const string LibProjectName = "SampleForeachSourceLib";

    private static string DeployedFixture =>
        Path.Combine(AppContext.BaseDirectory, FixtureName);

    private static IReadOnlyList<string> GateRuntimeFiles =>
        Directory.Exists(Path.Combine(AppContext.BaseDirectory, "JavaGateRuntime"))
            ? Directory.GetFiles(
                Path.Combine(AppContext.BaseDirectory, "JavaGateRuntime"),
                "*.java", SearchOption.AllDirectories)
            : Array.Empty<string>();

    private static ConversionOptions Options => new()
    {
        TypeMappingConfigPath = Path.Combine(
            AppContext.BaseDirectory, "config", "TypeMappings.json"),
    };

    [SlowE2EFact]
    public async Task Fixture_Converts_Compiles_RunsDuckAndStringAssertions()
    {
        var workRoot = Path.Combine(
            Path.GetTempPath(), "cs2j-c40-e2e-" + Guid.NewGuid().ToString("N"));
        var outputRoot = Path.Combine(workRoot, "out");
        var fixtureRoot = StagePrivateFixture(workRoot + "-fixture");

        try
        {
            var chain = await RunDefaultChainAsync(
                Path.Combine(fixtureRoot, FixtureName + ".sln"), outputRoot);
            Assert.True(chain.Success);
            Assert.Contains(".cs2j-foreach-source-src",
                chain.ExecutedIntermediateDirectoryNames);

            Assert.True(Directory.Exists(JavaE2EHarness.CompatClasses),
                "compat target/classes missing; build csharptojava-compat first: "
                + JavaE2EHarness.CompatClasses);

            SolutionLoader.EnsureMSBuildRegistered();
            using var loader = new SolutionLoader();
            var workspaceProjects = await loader.TryOpenAsync(chain.FinalSourcePath);
            Assert.NotNull(workspaceProjects);
            Assert.NotEmpty(workspaceProjects);

            // 支撑工程只作符号源；Tests 工程（ref struct/Span 夹具）与 App
            // （top-level statements）不进 javac 门——运行时面由内置 JVM
            // harness 直接调用 Lib API 覆盖。
            var convertProjects = workspaceProjects
                .Where(p => !Cs2jArrayProject.IsCs2jArrayProject(p.FilePath))
                .Where(p => !Cs2jPreludeProject.IsCs2jPreludeProject(p.FilePath))
                .Where(p => !Cs2jRefOutInProject.IsCs2jRefOutInProject(p.FilePath))
                .Where(p => !Cs2jUnsignedMathProject.IsCs2jUnsignedMathProject(p.FilePath))
                .Where(p => !p.IsTestProject)
                .Where(p => p.Name == LibProjectName)
                .ToList();
            Assert.Single(convertProjects);

            var wp = convertProjects[0];
            var extraPaths = wp.ProjectReferences
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(Path.GetDirectoryName)
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .ToList()!;
            var results = await new ConversionPipeline().ConvertProjectWithPartialMergeAsync(
                wp.Directory, Options,
                additionalSemanticProjectPaths: extraPaths,
                projectName: wp.Name,
                projectFilePath: wp.FilePath,
                projectReferences: wp.ProjectReferences,
                isTestProject: wp.IsTestProject);

            var failed = results.Where(c => !c.Success).ToList();
            Assert.True(failed.Count == 0,
                "Java conversion produced failures:\n"
                + string.Join("\n", failed.SelectMany(f =>
                    f.Diagnostics.Select(d => d.ToString()))));

            var entries = results
                .Where(c => !string.IsNullOrEmpty(c.FileName))
                .Select(c => (c.FileName!, c.GeneratedCode))
                .ToList();
            Assert.NotEmpty(entries);
            Assert.Contains(entries, e => e.Item2.Contains("EnumeratorDuckSamples"));
            Assert.Contains(entries, e => e.Item2.Contains("ForeachStringSamples"));

            // 迭代辅助：保留现场供检查生成 Java。
            if (Environment.GetEnvironmentVariable("CS2J_KEEP_E2E") == "1")
            {
                var dump = Path.Combine(workRoot, "generated-java");
                Directory.CreateDirectory(dump);
                foreach (var (fileName, code) in entries)
                {
                    File.WriteAllText(
                        Path.Combine(dump, Path.GetFileName(fileName)), code);
                }
            }

            var harnessFile = Path.Combine(workRoot, "C40Harness.java");
            File.WriteAllText(harnessFile, HarnessSource);

            var classesDir = JavaE2EHarness.Compile(workRoot, entries,
                GateRuntimeFiles.Append(harnessFile).ToList());

            var run = JavaE2EHarness.RunMain(classesDir, "SampleForeachSourceLib.C40Harness");
            Assert.True(run.ExitCode == 0,
                "harness exit " + run.ExitCode + "\n" + run.StdOut + "\n" + run.StdErr);
            Assert.Contains("ALL OK", run.StdOut);
            Assert.DoesNotContain("Exception", run.StdErr);
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("CS2J_KEEP_E2E") != "1")
            {
                JavaE2EHarness.TryDelete(workRoot);
            }
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
    /// 会与它们的清理竞争。
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

    // JVM harness written against the actual generated-Java spelling. Every
    // assertion mirrors the .NET oracle (EnumeratorDuckTests / direct fixture
    // semantics); a mismatch throws and the process exits non-zero.
    private const string HarnessSource = """
        package SampleForeachSourceLib;

        import dotnet.System.Collections.Generic.List;

        public final class C40Harness {
            public static void main(String[] args) {
                duckFamilies();
                disposeTiming();
                structEnumerators();
                stringSites();
                System.out.println("ALL OK");
            }

            private static void duckFamilies() {
                expectInts(EnumeratorDuckSamples.collectPlain(new PlainDuck()), new int[] {1, 2, 3}, "collectPlain");

                PlainDuckFactory factory = new PlainDuckFactory();
                expectInts(EnumeratorDuckSamples.collectFromFactory(factory), new int[] {1, 2, 3}, "collectFromFactory");
                check("factory source evaluated once", factory.SourceCallCount == 1);

                expectInts(EnumeratorDuckSamples.collectExtension(new ExtensionEnumerable(4)),
                    new int[] {1, 2, 3, 4}, "collectExtension");
                expectInts(EnumeratorDuckSamples.collectGenericBoxed(new GenericBoxedEnumerable(1, 2, 3)),
                    new int[] {1, 2, 3}, "collectGenericBoxed");
                expectInts(EnumeratorDuckSamples.collectNonGenericBoxed(
                        new NonGenericBoxedEnumerable(Integer.valueOf(4), Integer.valueOf(5))),
                    new int[] {4, 5}, "collectNonGenericBoxed");
            }

            private static void disposeTiming() {
                FlaggedDuck duck = new FlaggedDuck(3);
                check("normal completion disposes once",
                    EnumeratorDuckSamples.normalCompletionDisposeCount(duck) == 1);

                FlaggedDuck breakDuck = new FlaggedDuck(3);
                List<Integer> seen = new List<Integer>();
                check("break disposes once", EnumeratorDuckSamples.breakAfterFirst(breakDuck, seen) == 1);
                expectInts(seen, new int[] {1}, "break sees first only");

                FlaggedDuck throwDuck = new FlaggedDuck(3);
                boolean threw = false;
                try {
                    EnumeratorDuckSamples.throwAtSecond(throwDuck);
                } catch (IllegalStateException ex) {
                    threw = "boom".equals(ex.getMessage());
                }
                check("body exception bubbles (boom)", threw);
                check("dispose runs before body exception", throwDuck.Enumerator().DisposeCount == 1);

                FlaggedDuck continueDuck = new FlaggedDuck(3);
                List<Integer> midCounts = new List<Integer>();
                check("continue final dispose once",
                    EnumeratorDuckSamples.continueObservations(continueDuck, midCounts) == 1);
                expectInts(midCounts, new int[] {0, 0, 0}, "no mid-loop dispose on continue");
            }

            private static void structEnumerators() {
                expectInts(EnumeratorDuckSamples.collectStatefulStruct(new StatefulStructDuck(4)),
                    new int[] {1, 2, 3, 4}, "collectStatefulStruct");
                expectInts(EnumeratorDuckSamples.breakStatefulStruct(new StatefulStructDuck(4), 3),
                    new int[] {1, 2}, "breakStatefulStruct");

                ExplicitDisposeStructDuck explicitDuck = new ExplicitDisposeStructDuck(3);
                expectInts(EnumeratorDuckSamples.collectExplicitDisposeStruct(explicitDuck),
                    new int[] {1, 2, 3}, "collectExplicitDisposeStruct");
                check("explicit struct dispose once on completion", explicitDuck.Probe().Count == 1);

                ExplicitDisposeStructDuck explicitBreakDuck = new ExplicitDisposeStructDuck(3);
                expectInts(EnumeratorDuckSamples.breakExplicitDisposeStruct(explicitBreakDuck),
                    new int[] {1, 2}, "breakExplicitDisposeStruct");
                check("explicit struct dispose once on break", explicitBreakDuck.Probe().Count == 1);
            }

            private static void stringSites() {
                expectChars(ForeachStringSamples.collect("ab"),
                    new char[] {'a', 'b'}, "collect");
                ForeachStringSamples samples = new ForeachStringSamples();
                expectChars(samples.collectAllKinds("z"),
                    new char[] {'z', 'x', 'y', 'p', 'q', 'l', 'i', 't', 'a', 'z'}, "collectAllKinds");
                expectChars(ForeachStringSamples.explicitChar("a"), new char[] {'a'}, "explicitChar");
                expectInts(ForeachStringSamples.codeUnits("A\u20AC"), new int[] {65, 8364}, "codeUnits");

                List<Object> boxed = ForeachStringSamples.boxedChars("ab");
                check("boxedChars size", boxed.size() == 2);
                check("boxedChars[0]", Character.valueOf('a').equals(boxed.get(0)));
                check("boxedChars[1]", Character.valueOf('b').equals(boxed.get(1)));

                expectChars(ForeachStringSamples.nested(new String[] {"ab", "c"}),
                    new char[] {'a', 'b', 'c'}, "nested");

                List<Character> charList = new List<Character>();
                charList.add('x');
                charList.add('y');
                expectChars(ForeachStringSamples.nonStringSites(charList, new char[] {'p', 'q'}),
                    new char[] {'x', 'y', 'p', 'q'}, "nonStringSites");

                check("empty string zero iterations", ForeachStringSamples.emptyIterations("") == 0);
                check("three iterations", ForeachStringSamples.emptyIterations("abc") == 3);

                expectNpe(() -> ForeachStringSamples.throwOnNullParameter(null), "throwOnNullParameter");
                expectNpe(ForeachStringSamples::throwOnNullReturn, "throwOnNullReturn");

                ForeachStringSamples once = new ForeachStringSamples();
                expectChars(once.singleEvaluation(), new char[] {'o', 'n', 'c', 'e'}, "singleEvaluation");
                check("source evaluated exactly once", once.GetTextCallCount() == 1);
            }

            private interface ThrowingRunnable { void run(); }

            private static void expectNpe(ThrowingRunnable r, String name) {
                try {
                    r.run();
                } catch (NullPointerException ex) {
                    return;
                }
                throw new AssertionError(name + ": expected NullPointerException");
            }

            private static void expectInts(List<Integer> actual, int[] expected, String name) {
                check(name + " size " + actual.size() + " vs " + expected.length,
                    actual.size() == expected.length);
                for (int i = 0; i < expected.length; i++) {
                    check(name + " [" + i + "]=" + actual.get(i) + " expected " + expected[i],
                        actual.get(i).intValue() == expected[i]);
                }
            }

            private static void expectChars(List<Character> actual, char[] expected, String name) {
                check(name + " size " + actual.size() + " vs " + expected.length,
                    actual.size() == expected.length);
                for (int i = 0; i < expected.length; i++) {
                    check(name + " [" + i + "]=" + actual.get(i) + " expected " + expected[i],
                        actual.get(i).charValue() == expected[i]);
                }
            }

            private static void check(String what, boolean ok) {
                if (!ok) {
                    throw new AssertionError("C40 harness failed: " + what);
                }
            }
        }
        """;
}
