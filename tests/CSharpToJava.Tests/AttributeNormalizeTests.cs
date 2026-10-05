using System.Text.RegularExpressions;
using CSharpToJava.CLI;
using CSharpToJava.Core.AttributeNormalize;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// C10 attribute-normalize M1 Core 测试（design §7.1）：
/// 内联源码 → CSharpCompilation(Latest + TPA refs + allowUnsafe) →
/// Bind/Analyze/RewriteFile，忽略空白断言，输出可再编译。
/// </summary>
public class AttributeNormalizeTests
{
    private sealed record TestFile(string Name, string Source);

    private sealed class Round
    {
        public required CSharpCompilation Compilation { get; init; }
        public required List<CSharpSyntaxTree> Trees { get; init; }
        public required List<AttributeNormalizeFileResult> Results { get; init; }
        public required AttributeNormalizeStatistics Statistics { get; init; }
        public required List<AttributeNormalizeDiagnostic> Diagnostics { get; init; }
        public required IReadOnlyList<CallerInfoPlan> Plans { get; init; }
        public AttributeNormalizeFileResult Result => Results[0];
    }

    private static (CSharpCompilation Compilation, List<CSharpSyntaxTree> Trees) BuildCompilation(
        IReadOnlyList<TestFile> files,
        IEnumerable<MetadataReference>? extraRefs = null)
    {
        var trees = files
            .Select(f => (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
                f.Source,
                new CSharpParseOptions(LanguageVersion.Latest),
                path: f.Name))
            .ToList();

        var references = ProjectUnsignedTypePreprocessor.GetBasicReferences()
            .Where(r => r.Display == null
                || r.Display.IndexOf("Microsoft.WindowsDesktop.App", StringComparison.OrdinalIgnoreCase) < 0)
            .ToList();
        if (extraRefs != null) references = references.Concat(extraRefs).ToList();

        var compilation = CSharpCompilation.Create(
            "an-tests",
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        return (compilation, trees);
    }

    private static Round RunRound(
        IReadOnlyList<TestFile> files,
        AttributeNormalizeOptions? options = null,
        IEnumerable<MetadataReference>? extraRefs = null)
    {
        var (compilation, trees) = BuildCompilation(files, extraRefs);

        var preprocessor = new AttributeNormalizePreprocessor(options);
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze();

        var statistics = new AttributeNormalizeStatistics();
        var diagnostics = new List<AttributeNormalizeDiagnostic>();
        var results = new List<AttributeNormalizeFileResult>();

        foreach (var tree in trees)
        {
            results.Add(preprocessor.RewriteFile(
                tree.ToString(),
                compilation.GetSemanticModel(tree),
                statistics,
                diagnostics));
        }

        return new Round
        {
            Compilation = compilation,
            Trees = trees,
            Results = results,
            Statistics = statistics,
            Diagnostics = diagnostics,
            Plans = preprocessor.Plans,
        };
    }

    private static Round RunSingle(
        string source,
        AttributeNormalizeOptions? options = null,
        string fileName = "test0.cs")
        => RunRound(new[] { new TestFile(fileName, source) }, options);

    private static void AssertRecompiles(Round round)
    {
        var newTrees = new List<SyntaxTree>();
        for (var i = 0; i < round.Trees.Count; i++)
        {
            newTrees.Add(round.Results[i].Changed
                ? CSharpSyntaxTree.ParseText(
                    round.Results[i].OutputCode,
                    new CSharpParseOptions(LanguageVersion.Latest),
                    path: round.Trees[i].FilePath)
                : round.Trees[i]);
        }

        var rewritten = round.Compilation;
        for (var i = 0; i < round.Trees.Count; i++)
        {
            if (!round.Results[i].Changed) continue;
            rewritten = rewritten.ReplaceSyntaxTree(round.Trees[i], newTrees[i]);
        }

        var errors = rewritten.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        Assert.True(
            errors.Count == 0,
            "Rewritten output has compile errors: " + string.Join("; ", errors));
    }

    private static void AssertIgnoreWhitespace(string expected, string actual)
        => Assert.Equal(
            Regex.Replace(expected, @"\s+", string.Empty),
            Regex.Replace(actual, @"\s+", string.Empty));

    private static void AssertContainsIgnoreWhitespace(string expectedFragment, string actual)
        => Assert.Contains(
            Regex.Replace(expectedFragment, @"\s+", string.Empty),
            Regex.Replace(actual, @"\s+", string.Empty),
            StringComparison.Ordinal);

    // =====================================================================
    // 站点填参基本形态（design §7.1）
    // =====================================================================

    [Fact]
    public void Trailing_Caller_Argument_Is_Filled_Positionally()
    {
        var source = """
            using System.Runtime.CompilerServices;
            class C {
                void Log(int x, [CallerMemberName] string m = null) {}
                void Work() { Log(1); }
            }
            """;

        var round = RunSingle(source);
        Assert.True(round.Result.Changed);
        AssertContainsIgnoreWhitespace(
            """void Work() { Log(1, "Work"); }""",
            round.Result.OutputCode);
        AssertRecompiles(round);
    }

    [Fact]
    public void Middle_Caller_Argument_Omitted_Is_Inserted_In_Place()
    {
        var source = """
            using System.Runtime.CompilerServices;
            class C {
                void Log(int a = 0, [CallerMemberName] string m = null, int b = 0) {}
                void Work() { Log(1, b: 2); }
            }
            """;

        var round = RunSingle(source);
        Assert.True(round.Result.Changed);
        AssertContainsIgnoreWhitespace(
            """void Work() { Log(1, m: "Work", b: 2); }""",
            round.Result.OutputCode);
        AssertRecompiles(round);
    }

    [Fact]
    public void Explicit_Argument_Is_Never_Overwritten()
    {
        var source = """
            using System.Runtime.CompilerServices;
            class C {
                void Log(int a = 0, [CallerMemberName] string m = null, int b = 0) {}
                void Work() { Log(1, "Explicit", 2); }
            }
            """;

        var round = RunSingle(source);
        Assert.False(round.Result.Changed);
        Assert.Contains("Explicit", round.Result.OutputCode);
        Assert.DoesNotContain("\"Work\"", round.Result.OutputCode);
        Assert.Equal(0, round.Statistics.CallerSitesFilled.Values.Sum());
    }

    [Fact]
    public void Named_Arguments_Insert_Named_Form()
    {
        var source = """
            using System.Runtime.CompilerServices;
            class C {
                void Log(int a = 0, [CallerMemberName] string m = null, int b = 0) {}
                void Work() { Log(b: 2, a: 1); }
            }
            """;

        var round = RunSingle(source);
        Assert.True(round.Result.Changed);
        AssertContainsIgnoreWhitespace(
            """void Work() { Log(b: 2, m: "Work", a: 1); }""",
            round.Result.OutputCode);
        AssertRecompiles(round);
    }

    [Fact]
    public void Caller_Argument_Is_Inserted_Before_Params_Expansion()
    {
        var source = """
            using System.Runtime.CompilerServices;
            class C {
                void Log(int a = 0, [CallerMemberName] string m = null, params int[] rest) {}
                void Work() { Log(1, rest: new[] { 2, 3 }); }
            }
            """;

        var round = RunSingle(source);
        Assert.True(round.Result.Changed);
        AssertContainsIgnoreWhitespace(
            """void Work() { Log(1, m: "Work", rest: new[] { 2, 3 }); }""",
            round.Result.OutputCode);
        AssertRecompiles(round);
    }

    // =====================================================================
    // CallerMemberName 取值表（design §2）
    // =====================================================================

    [Fact]
    public void CallerMemberName_Property_Accessor_Uses_Property_Name()
    {
        var source = """
            using System.Runtime.CompilerServices;
            class C {
                void Log([CallerMemberName] string m = null) {}
                public int P { get { Log(); return 1; } }
            }
            """;

        var round = RunSingle(source);
        AssertContainsIgnoreWhitespace("""get { Log("P"); return 1; }""", round.Result.OutputCode);
        AssertRecompiles(round);
    }

    [Fact]
    public void CallerMemberName_Event_Add_Remove_Uses_Event_Name()
    {
        var source = """
            using System.Runtime.CompilerServices;
            class C {
                void Log([CallerMemberName] string m = null) {}
                event System.Action E {
                    add { Log(); }
                    remove { Log(); }
                }
            }
            """;

        var round = RunSingle(source);
        AssertContainsIgnoreWhitespace(
            """
            add { Log("E"); }
            remove { Log("E"); }
            """,
            round.Result.OutputCode);
        AssertRecompiles(round);
    }

    [Fact]
    public void CallerMemberName_Indexer_Uses_Item()
    {
        var source = """
            using System.Runtime.CompilerServices;
            class C {
                void Log([CallerMemberName] string m = null) {}
                public int this[int i] { get { Log(); return i; } }
            }
            """;

        var round = RunSingle(source);
        AssertContainsIgnoreWhitespace("""get { Log("Item"); return i; }""", round.Result.OutputCode);
        AssertRecompiles(round);
    }

    [Fact]
    public void CallerMemberName_Instance_Ctor_Uses_DotCtor()
    {
        var source = """
            using System.Runtime.CompilerServices;
            class C {
                void Log([CallerMemberName] string m = null) {}
                public C() { Log(); }
            }
            """;

        var round = RunSingle(source);
        AssertContainsIgnoreWhitespace("""public C() { Log(".ctor"); }""", round.Result.OutputCode);
        AssertRecompiles(round);
    }

    [Fact]
    public void CallerMemberName_Static_Ctor_Uses_DotCctor()
    {
        var source = """
            using System.Runtime.CompilerServices;
            class C {
                static void Log([CallerMemberName] string m = null) {}
                static C() { Log(); }
            }
            """;

        var round = RunSingle(source);
        AssertContainsIgnoreWhitespace("""static C() { Log(".cctor"); }""", round.Result.OutputCode);
        AssertRecompiles(round);
    }

    [Fact]
    public void CallerMemberName_Finalizer_Uses_Finalize()
    {
        var source = """
            using System.Runtime.CompilerServices;
            class C {
            #pragma warning disable CS0067
                event System.Action Unused;
            #pragma warning restore CS0067
                void Log([CallerMemberName] string m = null) {}
                ~C() { Log(); }
            }
            """;

        var round = RunSingle(source);
        AssertContainsIgnoreWhitespace("""~C() { Log("Finalize"); }""", round.Result.OutputCode);
        AssertRecompiles(round);
    }

    [Fact]
    public void CallerMemberName_Local_Function_Uses_Function_Name()
    {
        var source = """
            using System.Runtime.CompilerServices;
            class C {
                void Log([CallerMemberName] string m = null) {}
                void Work() {
                    void Local() { Log(); }
                    Local();
                }
            }
            """;

        var round = RunSingle(source);
        AssertContainsIgnoreWhitespace("""void Local() { Log("Local"); }""", round.Result.OutputCode);
        AssertRecompiles(round);
    }

    [Fact]
    public void CallerMemberName_Lambda_Uses_Outer_Member_Name()
    {
        var source = """
            using System.Runtime.CompilerServices;
            class C {
                void Log([CallerMemberName] string m = null) {}
                void Work() {
                    System.Action a = () => Log();
                    a();
                }
            }
            """;

        var round = RunSingle(source);
        AssertContainsIgnoreWhitespace("""System.Action a = () => Log("Work");""", round.Result.OutputCode);
        AssertRecompiles(round);
    }

    [Fact]
    public void CallerMemberName_Field_Initializers_Use_Ctor_Names()
    {
        var source = """
            using System.Runtime.CompilerServices;
            class C {
                static int Log([CallerMemberName] string m = null) { return 0; }
                static int s = Log();
                int i = Log();
            }
            """;

        var round = RunSingle(source);
        AssertContainsIgnoreWhitespace(
            """
            static int s = Log(".cctor");
            int i = Log(".ctor");
            """,
            round.Result.OutputCode);
        AssertRecompiles(round);
    }

    // =====================================================================
    // 其余三族取值
    // =====================================================================

    [Fact]
    public void CallerFilePath_Uses_Tree_FilePath()
    {
        var source = """
            using System.Runtime.CompilerServices;
            class C {
                void Trace([CallerFilePath] string p = null) {}
                void Work() { Trace(); }
            }
            """;

        var round = RunSingle(source, fileName: @"D:\proj\sample.cs");
        AssertContainsIgnoreWhitespace(
            """void Work() { Trace("D:\\proj\\sample.cs"); }""",
            round.Result.OutputCode);
        AssertRecompiles(round);
    }

    [Fact]
    public void CallerFilePath_Empty_Policy_Inserts_Empty()
    {
        var source = """
            using System.Runtime.CompilerServices;
            class C {
                void Trace([CallerFilePath] string p = null) {}
                void Work() { Trace(); }
            }
            """;

        var round = RunSingle(
            source,
            new AttributeNormalizeOptions { CallerFilePathPolicy = CallerFilePathPolicy.Empty });
        AssertContainsIgnoreWhitespace("""void Work() { Trace(""); }""", round.Result.OutputCode);
        AssertRecompiles(round);
    }

    [Fact]
    public void CallerLineNumber_Inserts_One_Based_Line()
    {
        var source = """
            using System.Runtime.CompilerServices;
            class C {
                void Trace([CallerLineNumber] int n = 0) {}
                void Work() {
                    Trace();
                }
            }
            """;

        var round = RunSingle(source);
        Assert.Matches(
            new Regex(@"Trace\(\s*\d+\s*\)"),
            round.Result.OutputCode);
        AssertRecompiles(round);
    }

    [Fact]
    public void CallerArgumentExpression_Extracts_Argument_Text()
    {
        var source = """
            using System.Runtime.CompilerServices;
            class C {
                void Verify(int x, [CallerArgumentExpression("x")] string e = null) {}
                void Work() { Verify(1 + 2); }
            }
            """;

        var round = RunSingle(source);
        AssertContainsIgnoreWhitespace(
            """void Work() { Verify(1 + 2, "1+2"); }""",
            round.Result.OutputCode);
        AssertRecompiles(round);
    }

    // =====================================================================
    // 分派/重载/跨文件/外部目标
    // =====================================================================

    [Fact]
    public void Overloads_Are_Tracked_As_Separate_Plans()
    {
        var source = """
            using System.Runtime.CompilerServices;
            class C {
                void M(int a, [CallerMemberName] string n = null) {}
                void M(string a, [CallerMemberName] string n = null) {}
                void Work() { M(1); M("s"); }
            }
            """;

        var round = RunSingle(source);
        Assert.Equal(2, round.Statistics.CallerPlansApplied);
        AssertContainsIgnoreWhitespace(
            """void Work() { M(1, "Work"); M("s", "Work"); }""",
            round.Result.OutputCode);
        AssertRecompiles(round);
    }

    [Fact]
    public void Interface_And_Base_Dispatch_Sites_Are_Filled()
    {
        var source = """
            using System.Runtime.CompilerServices;
            interface I { void Run([CallerMemberName] string n = null); }
            class Base { public virtual void Run([CallerMemberName] string n = null) {} }
            class Derived : Base, I {
                public override void Run(string n = null) {}
                void I.Run(string n = null) {}
                void Work() {
                    I i = this; i.Run();
                    base.Run();
                }
            }
            """;

        var round = RunSingle(source);
        Assert.Equal(2, round.Statistics.CallerSitesFilled[CallerInfoKind.CallerMemberName]);
        AssertContainsIgnoreWhitespace(
            """
            I i = this; i.Run("Work");
            base.Run("Work");
            """,
            round.Result.OutputCode);
        AssertRecompiles(round);
    }

    [Fact]
    public void Cross_File_Declaration_And_Site_Are_Both_Rewritten_And_Residual_Is_Clean()
    {
        var fileA = new TestFile("a.cs", """
            using System.Runtime.CompilerServices;
            class Helper {
                public static void Log([CallerMemberName] string n = null) {}
            }
            """);
        var fileB = new TestFile("b.cs", """
            class C {
                void Work() { Helper.Log(); }
            }
            """);

        var round = RunRound(new[] { fileA, fileB });
        Assert.True(round.Results[0].Changed);
        Assert.True(round.Results[1].Changed);
        Assert.DoesNotContain("CallerMemberName", round.Results[0].OutputCode);
        AssertContainsIgnoreWhitespace(
            """void Work() { Helper.Log("Work"); }""",
            round.Results[1].OutputCode);
        AssertRecompiles(round);

        var newTrees = round.Trees
            .Select((t, i) => round.Results[i].Changed
                ? CSharpSyntaxTree.ParseText(round.Results[i].OutputCode, path: t.FilePath)
                : (SyntaxTree)t)
            .ToArray();
        var rewritten = round.Compilation;
        for (var i = 0; i < round.Trees.Count; i++)
            rewritten = rewritten.ReplaceSyntaxTree(round.Trees[i], newTrees[i]);

        var residual = new AttributeNormalizePreprocessor().ResidualScan(
            rewritten, round.Statistics);
        Assert.Empty(residual);
    }

    [Fact]
    public void ExternalTarget_Site_Is_Filled_Without_Declaration_Rewrite()
    {
        var librarySource = """
            using System.Runtime.CompilerServices;
            public class ExtApi {
                public static void Log([CallerMemberName] string n = null) {}
            }
            """;

        var libTree = CSharpSyntaxTree.ParseText(
            librarySource, new CSharpParseOptions(LanguageVersion.Latest));
        var library = CSharpCompilation.Create(
            "extlib",
            new[] { libTree },
            ProjectUnsignedTypePreprocessor.GetBasicReferences()
                .Where(r => r.Display == null
                    || r.Display.IndexOf("Microsoft.WindowsDesktop.App", StringComparison.OrdinalIgnoreCase) < 0),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var stream = new MemoryStream();
        var emit = library.Emit(stream);
        Assert.True(emit.Success, string.Join("; ", emit.Diagnostics));
        var extReference = MetadataReference.CreateFromImage(stream.ToArray());

        var consumer = """
            class C {
                void Work() { ExtApi.Log(); }
            }
            """;

        var round = RunRound(
            new[] { new TestFile("consumer.cs", consumer) },
            extraRefs: new[] { extReference });

        Assert.Equal(CallerPlanKind.ExternalTarget, round.Plans[0].Kind);
        AssertContainsIgnoreWhitespace(
            """void Work() { ExtApi.Log("Work"); }""",
            round.Result.OutputCode);
        AssertRecompiles(round);
    }

    [Fact]
    public void This_And_Base_Constructor_Initializers_Are_Filled()
    {
        var source = """
            using System.Runtime.CompilerServices;
            class Node {
                public Node([CallerMemberName] string n = null) {}
                public Node(int x) : this() {}
            }
            class Derived : Node {
                public Derived() : base() {}
            }
            """;

        var round = RunSingle(source);
        AssertContainsIgnoreWhitespace(
            """public Node(int x) : this(".ctor") {}""",
            round.Result.OutputCode);
        AssertContainsIgnoreWhitespace(
            """public Derived() : base(".ctor") {}""",
            round.Result.OutputCode);
        AssertRecompiles(round);
    }

    [Fact]
    public void Delegate_Invocation_Is_Not_Treated_As_Caller_Target()
    {
        var source = """
            class C {
                void Work() {
                    System.Action d = () => { };
                    d();
                }
            }
            """;

        var round = RunSingle(source);
        Assert.False(round.Result.Changed);
        Assert.Equal(0, round.Statistics.CallerPlansDetected);
    }

    // =====================================================================
    // 声明端保留 / 幂等 / 原子跳过
    // =====================================================================

    [Fact]
    public void Declaration_Rewrite_Preserves_Other_Attributes_And_Default_Value()
    {
        var source = """
            using System.Runtime.CompilerServices;
            class KeepAttribute : System.Attribute {}
            class C {
                void Log([Keep] [CallerMemberName] string m = "def") {}
                void Work() { Log(); }
            }
            """;

        var round = RunSingle(source);
        AssertContainsIgnoreWhitespace(
            """
            void Log([Keep] string m = "def") {}
            void Work() { Log("Work"); }
            """,
            round.Result.OutputCode);
        Assert.Contains("Keep", round.Result.OutputCode);
        Assert.Contains("= \"def\"", round.Result.OutputCode);
        Assert.DoesNotContain("CallerMemberName", round.Result.OutputCode);
        AssertRecompiles(round);
    }

    [Fact]
    public void Rewrite_Is_Idempotent_On_Second_Pass()
    {
        var source = """
            using System.Runtime.CompilerServices;
            class C {
                void Log(int a = 0, [CallerMemberName] string m = null) {}
                void Work() { Log(); }
            }
            """;

        var first = RunSingle(source);
        Assert.True(first.Result.Changed);

        var secondSource = first.Result.OutputCode;
        var second = RunSingle(secondSource);
        Assert.False(second.Result.Changed);
        AssertIgnoreWhitespace(secondSource, second.Result.OutputCode);
    }

    [Fact]
    public void ExpressionTree_Site_Causes_Atomic_CS2J1303_Skip()
    {
        var source = """
            using System.Runtime.CompilerServices;
            class C {
                void Log([CallerMemberName] string m = null) {}
                void Work() {
                    System.Linq.Expressions.Expression<System.Action> e = () => Log();
                    Log();
                }
            }
            """;

        var round = RunSingle(source);
        Assert.False(round.Result.Changed);
        Assert.Equal(source, round.Result.OutputCode);
        var diagnostic = Assert.Single(round.Diagnostics);
        Assert.Equal(AttributeNormalizeDiagnosticCode.CS2J1303, diagnostic.Code);
        Assert.Equal(CallerPlanOutcome.Skipped, round.Plans[0].Outcome);
    }

    [Fact]
    public void Dynamic_Site_Causes_Atomic_CS2J1302_Skip()
    {
        var source = """
            using System.Runtime.CompilerServices;
            class C {
                void Log([CallerMemberName] string m = null) {}
                void Work() {
                    dynamic d = 1;
                    Log(d);
                }
            }
            """;

        var round = RunSingle(source);
        Assert.False(round.Result.Changed);
        Assert.Equal(source, round.Result.OutputCode);
        var diagnostic = Assert.Single(round.Diagnostics);
        Assert.Equal(AttributeNormalizeDiagnosticCode.CS2J1302, diagnostic.Code);
    }

    [Fact]
    public void FilePath_Error_Policy_Causes_CS2J1304_Skip()
    {
        var source = """
            using System.Runtime.CompilerServices;
            class C {
                void Trace([CallerFilePath] string p = null) {}
                void Work() { Trace(); }
            }
            """;

        var round = RunSingle(
            source,
            new AttributeNormalizeOptions { CallerFilePathPolicy = CallerFilePathPolicy.Error });
        Assert.False(round.Result.Changed);
        var diagnostic = Assert.Single(round.Diagnostics);
        Assert.Equal(AttributeNormalizeDiagnosticCode.CS2J1304, diagnostic.Code);
    }

    [Fact]
    public void Clean_File_Is_Byte_Level_Preserved()
    {
        var source = """
            class C {
                void M() {
                    int a = 1;
                    System.Console.WriteLine(a);
                }
            }
            """;

        var round = RunSingle(source);
        Assert.False(round.Result.Changed);
        Assert.Equal(source, round.Result.OutputCode);
        Assert.Equal(1, round.Statistics.FilesScanned);
        Assert.Equal(1, round.Statistics.FilesSkippedClean);
    }

    [Fact]
    public void Clean_File_With_Bom_Is_Preserved()
    {
        var source = "\uFEFFclass C { void M() { } }";
        var round = RunSingle(source);
        Assert.False(round.Result.Changed);
        Assert.Equal(source, round.Result.OutputCode);
    }
}
