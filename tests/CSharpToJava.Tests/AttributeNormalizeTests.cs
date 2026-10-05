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
        public required AttributeInventoryReport Inventory { get; init; }
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
            Inventory = preprocessor.Inventory,
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
        var diagnostic = Assert.Single(round.Diagnostics
            .Where(d => d.Severity != AttributeNormalizeSeverity.Info));
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
        var diagnostic = Assert.Single(round.Diagnostics
            .Where(d => d.Severity != AttributeNormalizeSeverity.Info));
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
        var diagnostic = Assert.Single(round.Diagnostics
            .Where(d => d.Severity != AttributeNormalizeSeverity.Info));
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

    // =====================================================================
    // M2 inventory：JSON/序列化注解全量盘点与桥接面核对（design §7.1/§4.4）
    // =====================================================================

    private static AttributeOccurrence? Find(Round round, string metadataName)
        => round.Inventory.Occurrences
            .FirstOrDefault(o => o.AttributeMetadataName == metadataName);

    private const string StjPrefix = "System.Text.Json.Serialization.";

    [Fact]
    public void Inventory_Stj_Seven_Annotations_Are_Bridged_With_Materialized_Arguments()
    {
        var source = """
            using System.Collections.Generic;
            using System.Text.Json.Serialization;
            public sealed class StjDto {
                [JsonPropertyName("id")] public string Id { get; set; }
                [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string Maybe { get; set; }
                [JsonInclude] public string Included;
                [JsonConverter(typeof(string))] public string Conv { get; set; }
                [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public string Number { get; set; }
                [JsonExtensionData] public IDictionary<string, object> Ext { get; set; }
                [JsonConstructor] public StjDto(int x) {}
            }
            """;

        var round = RunSingle(source);

        Assert.Equal(7, round.Inventory.CountByFamily(AttributeFamily.JsonSystemTextJson));
        Assert.Equal(0, round.Inventory.CountByStatus(BridgeStatus.BridgeGap));
        Assert.Equal(0, round.Inventory.CountByStatus(BridgeStatus.Unknown));

        var name = Find(round, StjPrefix + "JsonPropertyNameAttribute");
        Assert.NotNull(name);
        Assert.Equal(BridgeStatus.Bridged, name!.Status);
        Assert.Equal(new[] { "name" }, name.MaterializedArguments);

        var ignore = Find(round, StjPrefix + "JsonIgnoreAttribute");
        Assert.NotNull(ignore);
        Assert.Equal(BridgeStatus.Bridged, ignore!.Status);
        Assert.Equal(new[] { "Condition" }, ignore.MaterializedArguments);

        var include = Find(round, StjPrefix + "JsonIncludeAttribute");
        Assert.NotNull(include);
        Assert.Equal(BridgeStatus.Bridged, include!.Status);
        Assert.Empty(include.MaterializedArguments);
        Assert.Empty(include.UnreadArguments);

        var converter = Find(round, StjPrefix + "JsonConverterAttribute");
        Assert.NotNull(converter);
        Assert.Equal(BridgeStatus.Bridged, converter!.Status);
        Assert.Equal(new[] { "converterType" }, converter.MaterializedArguments);

        var number = Find(round, StjPrefix + "JsonNumberHandlingAttribute");
        Assert.NotNull(number);
        Assert.Equal(BridgeStatus.Bridged, number!.Status);
        Assert.Equal(new[] { "handling" }, number.MaterializedArguments);

        var extension = Find(round, StjPrefix + "JsonExtensionDataAttribute");
        Assert.NotNull(extension);
        Assert.Equal(BridgeStatus.Bridged, extension!.Status);

        var ctor = Find(round, StjPrefix + "JsonConstructorAttribute");
        Assert.NotNull(ctor);
        Assert.Equal(BridgeStatus.Bridged, ctor!.Status);

        // 七个使用点全部物化 → 不刷 CS2J1310/1320。
        Assert.DoesNotContain(round.Diagnostics, d =>
            d.Code == AttributeNormalizeDiagnosticCode.CS2J1310
            || d.Code == AttributeNormalizeDiagnosticCode.CS2J1320);
    }

    [Fact]
    public void Inventory_Null_Positional_Argument_Downgrades_To_BridgeGap_And_Emits_CS2J1310()
    {
        var source = """
            using System.Text.Json.Serialization;
            public sealed class StjDto {
                [JsonPropertyName(null)] public string Id { get; set; }
            }
            """;

        var round = RunSingle(source);

        var occurrence = Find(round, StjPrefix + "JsonPropertyNameAttribute");
        Assert.NotNull(occurrence);
        Assert.Equal(BridgeStatus.BridgeGap, occurrence!.Status);
        Assert.Equal(new[] { "name" }, occurrence.UnreadArguments);

        var diagnostic = Assert.Single(round.Diagnostics
            .Where(d => d.Code == AttributeNormalizeDiagnosticCode.CS2J1310));
        Assert.Equal(AttributeNormalizeSeverity.Warning, diagnostic.Severity);
        Assert.Contains("name", diagnostic.Message);
    }

    [Fact]
    public void Inventory_Unread_Named_Argument_Downgrades_To_BridgeGap_And_Emits_CS2J1310()
    {
        // XmlArray 的 Order 具名属性不在转换器已读集合内（只读位置 elementName）。
        var source = """
            using System.Xml.Serialization;
            public sealed class XmlDto {
                [XmlArray("items", Order = 1)] public string[] Items { get; set; }
            }
            """;

        var round = RunSingle(source);

        var occurrence = Find(round, "System.Xml.Serialization.XmlArrayAttribute");
        Assert.NotNull(occurrence);
        Assert.Equal(BridgeStatus.BridgeGap, occurrence!.Status);
        Assert.Equal(new[] { "elementName" }, occurrence.MaterializedArguments);
        Assert.Equal(new[] { "Order" }, occurrence.UnreadArguments);

        Assert.Contains(round.Diagnostics, d =>
            d.Code == AttributeNormalizeDiagnosticCode.CS2J1310);
    }

    [Fact]
    public void Inventory_Newtonsoft_JsonProperty_Emits_CS2J1310()
    {
        // Newtonsoft.Json 不在框架 TPA 中，源码内声明同元数据名的桩类型，
        // 验证按元数据身份命中 BridgeGap 表。
        var source = """
            namespace Newtonsoft.Json {
                public sealed class JsonPropertyAttribute : System.Attribute {
                    public JsonPropertyAttribute(string name) { }
                }
            }
            public sealed class NsDto {
                [Newtonsoft.Json.JsonProperty("z")] public string Id { get; set; }
            }
            """;

        var round = RunSingle(source);

        var occurrence = Find(round, "Newtonsoft.Json.JsonPropertyAttribute");
        Assert.NotNull(occurrence);
        Assert.Equal(BridgeStatus.BridgeGap, occurrence!.Status);
        Assert.Equal(new[] { "name" }, occurrence.UnreadArguments);

        var diagnostic = Assert.Single(round.Diagnostics
            .Where(d => d.Code == AttributeNormalizeDiagnosticCode.CS2J1310));
        Assert.Contains("Newtonsoft.Json.JsonPropertyAttribute", diagnostic.Message);
        Assert.Contains("name", diagnostic.Message);
    }

    [Fact]
    public void Inventory_DataContract_Annotations_Emit_CS2J1310()
    {
        var source = """
            using System.Runtime.Serialization;
            [DataContract]
            public sealed class DcDto {
                [DataMember(Name = "id")] public string Id { get; set; }
                [IgnoreDataMember] public string Secret { get; set; }
            }
            """;

        var round = RunSingle(source);

        Assert.Equal(3, round.Inventory.CountByFamily(AttributeFamily.DataContract));
        Assert.Equal(3, round.Inventory.CountByStatus(BridgeStatus.BridgeGap));

        var member = Find(round, "System.Runtime.Serialization.DataMemberAttribute");
        Assert.NotNull(member);
        // 具名 Name 未被任何转换器成员级分支读取。
        Assert.Equal(new[] { "Name" }, member!.UnreadArguments);

        var cs1310 = round.Diagnostics
            .Where(d => d.Code == AttributeNormalizeDiagnosticCode.CS2J1310)
            .ToList();
        Assert.Equal(3, cs1310.Count);
    }

    [Fact]
    public void Inventory_Custom_Attribute_Emits_CS2J1320()
    {
        var source = """
            public sealed class MyAnnoAttribute : System.Attribute { }
            public sealed class C {
                [MyAnno] public int P { get; set; }
            }
            """;

        var round = RunSingle(source);

        var occurrence = Find(round, "MyAnnoAttribute");
        Assert.NotNull(occurrence);
        Assert.Equal(BridgeStatus.Unknown, occurrence!.Status);

        var diagnostic = Assert.Single(round.Diagnostics
            .Where(d => d.Code == AttributeNormalizeDiagnosticCode.CS2J1320));
        Assert.Equal(AttributeNormalizeSeverity.Warning, diagnostic.Severity);
        Assert.Contains("MyAnnoAttribute", diagnostic.Message);
        // 三选一处置指引。
        Assert.Contains("ignoreAttributes", diagnostic.Message);
        Assert.Contains("keepAttributes", diagnostic.Message);
    }

    [Fact]
    public void Inventory_Custom_Attribute_Keep_Override_Silences_CS2J1320()
    {
        var source = """
            public sealed class MyAnnoAttribute : System.Attribute { }
            public sealed class C {
                [MyAnno] public int P { get; set; }
            }
            """;

        var options = new AttributeNormalizeOptions
        {
            KeepAttributes = new HashSet<string>(StringComparer.Ordinal)
            {
                "MyAnnoAttribute",
            },
        };

        var round = RunSingle(source, options);
        Assert.DoesNotContain(round.Diagnostics, d =>
            d.Code == AttributeNormalizeDiagnosticCode.CS2J1320);
        var occurrence = Find(round, "MyAnnoAttribute");
        Assert.NotNull(occurrence);
        Assert.Equal(BridgeStatus.InventoriedPassthrough, occurrence!.Status);
    }

    [Fact]
    public void Inventory_Custom_Attribute_Ignore_Override_Removes_Occurrence()
    {
        var source = """
            public sealed class MyAnnoAttribute : System.Attribute { }
            public sealed class C {
                [MyAnno] public int P { get; set; }
            }
            """;

        var options = new AttributeNormalizeOptions
        {
            IgnoreAttributes = new HashSet<string>(StringComparer.Ordinal)
            {
                "MyAnnoAttribute",
            },
        };

        var round = RunSingle(source, options);
        Assert.Null(Find(round, "MyAnnoAttribute"));
        Assert.DoesNotContain(round.Diagnostics, d =>
            d.Code == AttributeNormalizeDiagnosticCode.CS2J1320);
    }

    [Fact]
    public void Inventory_DenyList_Emits_Non_Degradable_CS2J1321()
    {
        var source = """
            namespace MyNs {
                public sealed class AnnoAttribute : System.Attribute { }
            }
            public sealed class C {
                [MyNs.Anno] public int P { get; set; }
            }
            """;

        using (KnownAttributeCatalog.SetTestDenyList(new[] { "MyNs.AnnoAttribute" }))
        {
            var round = RunSingle(source);

            var diagnostic = Assert.Single(round.Diagnostics
                .Where(d => d.Code == AttributeNormalizeDiagnosticCode.CS2J1321));
            Assert.Equal(AttributeNormalizeSeverity.Error, diagnostic.Severity);
            Assert.DoesNotContain(round.Diagnostics, d =>
                d.Code == AttributeNormalizeDiagnosticCode.CS2J1310
                && d.Message.Contains("AnnoAttribute"));
        }

        // scope 结束后恢复，无 CS2J1321。
        var after = RunSingle(source);
        Assert.DoesNotContain(after.Diagnostics, d =>
            d.Code == AttributeNormalizeDiagnosticCode.CS2J1321);
    }

    [Fact]
    public void Inventory_AssemblyInfo_File_Is_Classified_But_Never_Warned()
    {
        var source = """
            [assembly: System.CLSCompliant(true)]
            """;

        var round = RunRound(
            new[] { new TestFile("obj/test.AssemblyInfo.cs", source) });

        Assert.Single(round.Inventory.Occurrences);
        Assert.Equal(BridgeStatus.Unknown, round.Inventory.Occurrences[0].Status);
        Assert.DoesNotContain(round.Diagnostics, d =>
            d.Code == AttributeNormalizeDiagnosticCode.CS2J1320
            || d.Code == AttributeNormalizeDiagnosticCode.CS2J1310);
    }

    [Fact]
    public void Inventory_Test_Framework_Annotations_Do_Not_Warn()
    {
        // 与 xUnit 实程序集解耦：源码内声明同元数据名桩。
        var source = """
            namespace Xunit {
                [System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = false)]
                public sealed class FactAttribute : System.Attribute { }
            }
            public sealed class SomeTests {
                [Xunit.Fact] public void It_Works() { }
            }
            """;

        var round = RunSingle(source);

        var occurrence = Find(round, "Xunit.FactAttribute");
        Assert.NotNull(occurrence);
        Assert.Equal(BridgeStatus.InventoriedPassthrough, occurrence!.Status);
        Assert.DoesNotContain(round.Diagnostics, d =>
            d.Code == AttributeNormalizeDiagnosticCode.CS2J1320
            || d.Code == AttributeNormalizeDiagnosticCode.CS2J1310);
    }

    [Fact]
    public void Inventory_Flags_ThreadStatic_Conditional_Are_GovernedElsewhere()
    {
        // ConditionalAttribute 不在 GetBasicReferences 引用集内，用同元数据名桩。
        var source = """
            using System;
            namespace System.Diagnostics {
                [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
                public sealed class ConditionalAttribute : Attribute {
                    public ConditionalAttribute(string conditionString) { }
                }
            }
            [Flags]
            public enum Mode { A = 1, B = 2 }
            public sealed class C {
                [ThreadStatic] private static int s_slot;
                [System.Diagnostics.Conditional("TRACE")] static void TraceOnly() { }
            }
            """;

        var round = RunSingle(source);

        var flags = Find(round, "System.FlagsAttribute");
        Assert.NotNull(flags);
        Assert.Equal(BridgeStatus.GovernedElsewhere, flags!.Status);
        Assert.Equal("C05", flags.OwnerPass);

        var threadStatic = Find(round, "System.ThreadStaticAttribute");
        Assert.NotNull(threadStatic);
        Assert.Equal(BridgeStatus.GovernedElsewhere, threadStatic!.Status);
        Assert.Equal("C09", threadStatic.OwnerPass);

        var conditional = Find(round, "System.Diagnostics.ConditionalAttribute");
        Assert.NotNull(conditional);
        Assert.Equal(BridgeStatus.GovernedElsewhere, conditional!.Status);

        Assert.DoesNotContain(round.Diagnostics, d =>
            d.Code == AttributeNormalizeDiagnosticCode.CS2J1310
            || d.Code == AttributeNormalizeDiagnosticCode.CS2J1320);
    }

    [Fact]
    public void Inventory_Occurrence_Count_Reconciles_With_Caller_Removal()
    {
        var source = """
            using System.Runtime.CompilerServices;
            public sealed class KeepAttribute : System.Attribute { }
            public sealed class C {
                [System.Obsolete] [Keep] void M() { }
                void Log(int x, [CallerMemberName] string m = null) { }
                void Work() { Log(1); }
            }
            """;

        var round = RunSingle(source);

        // 输入树盘点：Obsolete / Keep / CallerMemberName = 3。
        Assert.Equal(3, round.Inventory.Occurrences.Count);
        Assert.Equal(1, round.Inventory.CountByFamily(AttributeFamily.CallerInfo));

        // 改写输出：Caller 族使用点被移除，其余逐字节保留。
        Assert.DoesNotContain("CallerMemberName", round.Result.OutputCode);
        Assert.Contains("Obsolete", round.Result.OutputCode);
        Assert.Contains("Keep", round.Result.OutputCode);
        Assert.True(round.Statistics.CallerAttributesRemoved >= 1);
    }

    [Fact]
    public void Inventory_SkipTypes_Filters_Every_Occurrence_Inside_The_Type()
    {
        var source = """
            public sealed class MyAnnoAttribute : System.Attribute { }
            [MyAnno]
            public sealed class Skipped {
                [MyAnno] public int P { get; set; }
            }
            public sealed class Kept {
                [MyAnno] public int Q { get; set; }
            }
            """;

        var options = new AttributeNormalizeOptions
        {
            SkipTypes = new HashSet<string>(StringComparer.Ordinal) { "Skipped" },
        };

        var round = RunSingle(source, options);

        Assert.Single(round.Inventory.Occurrences);
        Assert.Equal("Kept", round.Inventory.Occurrences[0].TargetSymbol?.ContainingType.Name);
    }

    [Fact]
    public void BridgeGap_CS2J1310_Message_Is_Stable_Snapshot()
    {
        var source = """
            using System.Xml.Serialization;
            public sealed class XmlDto {
                [XmlArray("items", Order = 1)] public string[] Items { get; set; }
            }
            """;

        var round = RunSingle(source);
        var diagnostic = Assert.Single(round.Diagnostics
            .Where(d => d.Code == AttributeNormalizeDiagnosticCode.CS2J1310));

        var expected = "serialization attribute 'System.Xml.Serialization.XmlArrayAttribute' on " +
            "'XmlDto.Items' has a bridge gap: unread arguments: Order; evidence: " +
            "src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs:L150.";
        Assert.Equal(expected, diagnostic.Message);
    }

    [Fact]
    public void Inventory_AN002_Summary_Lists_Family_Status_And_Gap_Head()
    {
        var source = """
            using System.Runtime.Serialization;
            [DataContract]
            public sealed class DcDto {
                [DataMember] public string Id { get; set; }
            }
            """;

        var round = RunSingle(source);
        var summary = Assert.Single(round.Diagnostics
            .Where(d => d.Code == AttributeNormalizeDiagnosticCode.AN002));
        Assert.Equal(AttributeNormalizeSeverity.Info, summary.Severity);
        Assert.Contains("by family:", summary.Message);
        Assert.Contains("DataContract=2", summary.Message);
        Assert.Contains("by status:", summary.Message);
        Assert.Contains("BridgeGap=2", summary.Message);
        Assert.Contains("gap:", summary.Message);
    }

    [Fact]
    public void Inventory_Strict_Mode_Escalates_Gap_And_Unknown_To_Error()
    {
        var source = """
            using System.Runtime.Serialization;
            public sealed class MyAnnoAttribute : System.Attribute { }
            [DataContract]
            public sealed class DcDto {
                [MyAnno] public string Id { get; set; }
            }
            """;

        var round = RunSingle(source, new AttributeNormalizeOptions { Strict = true });

        var gap = Assert.Single(round.Diagnostics
            .Where(d => d.Code == AttributeNormalizeDiagnosticCode.CS2J1310));
        Assert.Equal(AttributeNormalizeSeverity.Error, gap.Severity);
        var unknown = Assert.Single(round.Diagnostics
            .Where(d => d.Code == AttributeNormalizeDiagnosticCode.CS2J1320));
        Assert.Equal(AttributeNormalizeSeverity.Error, unknown.Severity);
    }
}
