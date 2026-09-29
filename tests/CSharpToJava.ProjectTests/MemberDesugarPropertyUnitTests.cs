using System.IO;
using CSharpToJava.Core.MemberDesugar;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C11 property 去糖 Core 单测矩阵（设计 §7.1）：内联源码 → CSharpCompilation →
/// RewriteFile → 忽略空白断言 + AssertOutputCompiles。
/// </summary>
public class MemberDesugarPropertyUnitTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static SemanticModel ParseWithSemantics(string src, params string[] extra)
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var extraTrees = extra.Select(s => CSharpSyntaxTree.ParseText(s, ParseOptions)).ToArray();
        var compilation = CSharpCompilation.Create(
            "md-test",
            new[] { tree }.Concat(extraTrees),
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return compilation.GetSemanticModel(tree);
    }

    /// <summary>残差闸门类测试用的语义解析入口（ExcludePlans 流程需要自持 model）。</summary>
    internal static SemanticModel ParseWithSemanticsPublic(string src)
        => ParseWithSemantics(src);

    internal static (string OutputCode, MemberDesugarStatistics Stats,
        List<MemberDesugarDiagnostic> Diagnostics) Rewrite(
        string src, MemberDesugarOptions? options = null, params string[] extra)
    {
        var model = ParseWithSemantics(src, extra);
        var pp = new MemberDesugarPreprocessor(options ?? new MemberDesugarOptions());
        var stats = new MemberDesugarStatistics();
        var diags = new List<MemberDesugarDiagnostic>();
        var result = pp.RewriteFile(src, model, stats, diags);
        return (result.OutputCode, stats, diags);
    }

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly)) yield return MetadataReference.CreateFromFile(assembly);
            }
            yield break;
        }
        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }

    internal static void AssertOutputCompiles(string output, params string[] extra)
    {
        var tree = CSharpSyntaxTree.ParseText(output, ParseOptions);
        var extraTrees = extra.Select(s => CSharpSyntaxTree.ParseText(s, ParseOptions)).ToArray();
        var compilation = CSharpCompilation.Create("md-verify",
            new[] { tree }.Concat(extraTrees),
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "Rewritten code failed to compile:\n" + string.Join("\n", errors) +
            "\n--- output ---\n" + output);
    }

    private static string Normalize(string code)
        => new string(code.Where(ch => !char.IsWhiteSpace(ch)).ToArray());

    private static void AssertContains(string output, string expected)
        => Assert.True(Normalize(output).Contains(Normalize(expected).Trim(),
            StringComparison.Ordinal),
            $"Expected output to contain:\n{expected}\n--- actual ---\n{output}");

    private static void AssertNotContains(string output, string unexpected)
        => Assert.False(Normalize(output).Contains(Normalize(unexpected).Trim(),
            StringComparison.Ordinal),
            $"Expected output NOT to contain:\n{unexpected}\n--- actual ---\n{output}");

    // ---- §7.1-1 自动属性 ----

    [Fact]
    public void AutoProperty_BecomesFieldAndTwoMethods()
    {
        var (output, stats, _) = Rewrite("""
            class C
            {
                public int Total { get; set; }
                public int Use() { Total = Total + 1; return Total; }
            }
            """);
        AssertContains(output, "private int total;");
        AssertContains(output, "public int get_Total() { return total; }");
        AssertContains(output, "public void set_Total(int value) { total = value; }");
        AssertContains(output, "set_Total(get_Total() + 1);");
        AssertContains(output, "return get_Total();");
        AssertNotContains(output, "public int Total {");
        Assert.Equal(1, stats.PropertiesPlanned);
        Assert.Equal(1, stats.BackingFieldsGenerated);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void GetOnlyAutoProperty_ReadOnlyFieldAndGetterOnly()
    {
        var (output, stats, _) = Rewrite("""
            class C
            {
                public int Answer { get; } = 42;
                public C() { }
                public int Use() => Answer;
            }
            """);
        AssertContains(output, "private readonly int answer = 42;");
        AssertContains(output, "public int get_Answer() { return answer; }");
        AssertNotContains(output, "set_Answer");
        Assert.Equal(1, stats.BackingFieldsGenerated);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void PrivateSet_AccessibilityTransfersToSetter()
    {
        var (output, _, _) = Rewrite("""
            public class C
            {
                public string Name { get; private set; }
                public void Init() { Name = "x"; }
            }
            """);
        AssertContains(output, "public string get_Name() { return name; }");
        AssertContains(output, "private void set_Name(string value) { name = value; }");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void StaticAutoProperty_StaticFieldAndMethods()
    {
        var (output, _, _) = Rewrite("""
            class C
            {
                public static int Count { get; set; }
                public static void Bump() { Count++; }
            }
            """);
        AssertContains(output, "private static int count;");
        AssertContains(output, "public static int get_Count() { return count; }");
        AssertContains(output, "set_Count(get_Count() + 1);");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void BackingFieldCollision_AgeAgeAndExplicitField()
    {
        var (output, _, _) = Rewrite("""
            class C
            {
                private int age;
                public int Age { get; set; }
                public int age_ { get; set; }
            }
            """);
        // 显式字段 age 占用裸名 → Age 的后备字段加 Value 后缀（对齐 JavaNaming）。
        AssertContains(output, "private int ageValue;");
        AssertOutputCompiles(output);
    }

    // ---- §7.1-2 full/计算属性 ----

    [Fact]
    public void FullProperty_BlockBodiesTransfer()
    {
        var (output, _, _) = Rewrite("""
            class C
            {
                private int _v;
                public int V
                {
                    get { return _v * 2; }
                    set { _v = value + 1; }
                }
            }
            """);
        AssertContains(output, "public int get_V() { return _v * 2; }");
        AssertContains(output, "public void set_V(int value) { _v = value + 1; }");
        AssertNotContains(output, "private int v;");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void ExpressionBodiedAccessors_Transfer()
    {
        var (output, _, _) = Rewrite("""
            class C
            {
                private int _v;
                public int V
                {
                    get => _v;
                    set => _v = value;
                }
            }
            """);
        AssertContains(output, "public int get_V() => _v;");
        AssertContains(output, "public void set_V(int value) => _v = value;");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void ComputedGetterOnly_NoBackingField()
    {
        var (output, stats, _) = Rewrite("""
            class C
            {
                public int Twice => System.Math.Min(1, 2) * 2;
            }
            """);
        AssertContains(output, "public int get_Twice() => System.Math.Min(1, 2) * 2;");
        Assert.Equal(0, stats.BackingFieldsGenerated);
        AssertOutputCompiles(output);
    }

    // ---- §7.1-3 使用点 ----

    [Fact]
    public void UseSites_CompoundAndIncrement()
    {
        var (output, _, _) = Rewrite("""
            class C
            {
                public int X { get; set; }
                public int? Y { get; set; }
                public void M(C other)
                {
                    X += 5;
                    X -= 1;
                    X *= 2;
                    Y ??= 3;
                    X++;
                    --X;
                    other.X = X;
                    other.X += X;
                }
            }
            """);
        AssertContains(output, "set_X(get_X() + 5);");
        AssertContains(output, "set_X(get_X() - 1);");
        AssertContains(output, "set_X(get_X() * 2);");
        AssertContains(output, "set_Y(get_Y() ?? 3);");
        AssertContains(output, "set_X(get_X() + 1);");
        AssertContains(output, "set_X(get_X() - 1);");
        AssertContains(output, "other.set_X(get_X());");
        AssertContains(output, "other.set_X(other.get_X() + get_X());");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void UseSites_SideEffectReceiverEvaluatedOnce()
    {
        var (output, _, _) = Rewrite("""
            class C
            {
                public int X { get; set; }
                public void M()
                {
                    GetC().X += 1;
                }
                C GetC() => this;
            }
            """);
        AssertContains(output, "var __cs2j_recv0 = GetC();");
        AssertContains(output, "__cs2j_recv0.set_X(__cs2j_recv0.get_X() + 1);");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void UseSites_MethodGroupConversion()
    {
        var (output, _, _) = Rewrite("""
            class C
            {
                public int X { get; set; }
                public void M()
                {
                    System.Func<int> f = X;
                    var y = f();
                }
            }
            """);
        AssertContains(output, "System.Func<int> f = get_X;");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void UseSites_BaseAccess()
    {
        var (output, _, _) = Rewrite("""
            class B { public virtual int X { get; set; } }
            class D : B
            {
                public override int X { get; set; }
                public void M()
                {
                    base.X = 5;
                    var y = base.X;
                }
            }
            """);
        AssertContains(output, "base.set_X(5);");
        AssertContains(output, "base.get_X()");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void UseSites_ConditionalAccess()
    {
        var (output, _, _) = Rewrite("""
            class C
            {
                public int X { get; set; }
                public void M(C? c)
                {
                    var y = c?.X;
                }
            }
            """);
        AssertContains(output, "c?.get_X()");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void UseSites_CrossFile()
    {
        var libSrc = """
            class Lib
            {
                public int N { get; set; }
            }
            """;
        var appSrc = """
            class App
            {
                public int Use(Lib l)
                {
                    l.N = l.N + 1;
                    return l.N;
                }
            }
            """;
        var libTree = CSharpSyntaxTree.ParseText(libSrc, ParseOptions);
        var appTree = CSharpSyntaxTree.ParseText(appSrc, ParseOptions);
        var compilation = CSharpCompilation.Create("md-test",
            new[] { libTree, appTree }, GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var pp = new MemberDesugarPreprocessor(new MemberDesugarOptions());
        var stats = new MemberDesugarStatistics();
        var diags = new List<MemberDesugarDiagnostic>();
        var libOut = pp.RewriteFile(libSrc, compilation.GetSemanticModel(libTree), stats, diags).OutputCode;
        var appOut = pp.RewriteFile(appSrc, compilation.GetSemanticModel(appTree), stats, diags).OutputCode;
        AssertContains(libOut, "public int get_N() { return n; }");
        AssertContains(appOut, "l.set_N(l.get_N() + 1);");
        AssertContains(appOut, "return l.get_N();");
        AssertOutputCompiles(libOut, appOut);
    }

    // ---- §7.1-4 构造器只读写入 + init ----

    [Fact]
    public void CtorWriteToGetOnly_WritesBackingField()
    {
        var (output, _, _) = Rewrite("""
            class C
            {
                public int Id { get; }
                public C(int id) { Id = id; }
            }
            """);
        AssertContains(output, "id = id;");
        AssertContains(output, "public int get_Id() { return id; }");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void InitProperty_WidensToSet()
    {
        var (output, stats, _) = Rewrite("""
            class C
            {
                public int A { get; init; }
                public void M(C c) { var d = new C { A = 1 }; }
            }
            """);
        AssertContains(output, "public void set_A(int value) { a = value; }");
        AssertContains(output, "var __cs2j_init0 = new C();");
        AssertContains(output, "__cs2j_init0.set_A(1);");
        Assert.Equal(1, stats.InitAccessorsWidened);
        AssertOutputCompiles(output);
    }

    // ---- §7.1-7 对象初始化器提升 ----

    [Fact]
    public void ObjectInitializer_LiftsToStatements()
    {
        var (output, stats, _) = Rewrite("""
            class C
            {
                public int A { get; set; }
                public int B { get; set; }
                public int F;
                public void M()
                {
                    var c = new C { A = 1, B = 2, F = 3 };
                    var d = new C { A = 4 };
                    return;
                }
            }
            """);
        AssertContains(output, "var __cs2j_init0 = new C { F = 3 };");
        AssertContains(output, "__cs2j_init0.set_A(1);");
        AssertContains(output, "__cs2j_init0.set_B(2);");
        AssertContains(output, "var c = __cs2j_init0;");
        AssertContains(output, "var d = __cs2j_init1;");
        Assert.True(stats.InitializersRewritten >= 2);
        AssertOutputCompiles(output);
    }

    [Fact]
    public void ObjectInitializer_NestedLifts()
    {
        var (output, _, _) = Rewrite("""
            class Sub { public int X { get; set; } }
            class C
            {
                public Sub S { get; set; }
                public void M()
                {
                    var c = new C { S = new Sub { X = 1 } };
                }
            }
            """);
        AssertContains(output, "var __cs2j_init0 = new C();");
        AssertContains(output, "var __cs2j_init1 = new Sub();");
        AssertContains(output, "__cs2j_init1.set_X(1);");
        AssertContains(output, "__cs2j_init0.set_S(__cs2j_init1);");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void ObjectInitializer_ReturnStatementLifts()
    {
        var (output, _, _) = Rewrite("""
            class C
            {
                public int A { get; set; }
                public C Make() { return new C { A = 7 }; }
            }
            """);
        AssertContains(output, "var __cs2j_init0 = new C();");
        AssertContains(output, "__cs2j_init0.set_A(7);");
        AssertContains(output, "return __cs2j_init0;");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void ObjectInitializer_NonLiftableContext_SkipsWith5119()
    {
        var (output, stats, diags) = Rewrite("""
            class C
            {
                public int A { get; set; }
                private static C d = new C { A = 1 };
            }
            """);
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.NonLiftableInitializer);
        Assert.True(stats.PlansSkipped >= 1);
        // 原子不动：声明保留
        AssertContains(output, "public int A { get; set; }");
    }

    // ---- §7.1-8 nameof ----

    [Fact]
    public void Nameof_Literalized()
    {
        var (output, stats, _) = Rewrite("""
            class C
            {
                public int X { get; set; }
                public string M() => nameof(X) + nameof(C.X);
            }
            """);
        AssertContains(output, "\"X\" + \"X\"");
        Assert.True(stats.NameofLiteralized >= 1);
        AssertOutputCompiles(output);
    }

    // ---- §7.1-5 修饰符/层次 ----

    [Fact]
    public void Modifiers_VirtualAbstractOverrideSealedNew()
    {
        var (output, _, _) = Rewrite("""
            abstract class A
            {
                public abstract int X { get; set; }
                public virtual int Y { get; set; }
            }
            class B : A
            {
                public sealed override int X { get; set; }
                public new int Y { get; set; }
            }
            """);
        AssertContains(output, "public abstract int get_X();");
        AssertContains(output, "public abstract void set_X(int value);");
        AssertContains(output, "public virtual int get_Y() { return y; }");
        AssertContains(output, "public sealed override int get_X() { return x; }");
        AssertContains(output, "public new int get_Y() { return y; }");
        AssertOutputCompiles(output);
    }

    // ---- §7.1-6 接口 ----

    [Fact]
    public void InterfaceProperty_BecomesInterfaceMethods()
    {
        var (output, _, _) = Rewrite("""
            public interface IFoo
            {
                int X { get; set; }
                int Y { get; }
            }
            """);
        AssertContains(output, "int get_X();");
        AssertContains(output, "void set_X(int value);");
        AssertContains(output, "int get_Y();");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void InterfaceDim_KeepsBody()
    {
        var (output, _, _) = Rewrite("""
            public interface IFoo
            {
                int X { get => 42; }
            }
            """);
        AssertContains(output, "int get_X() => 42;");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void ExplicitInterfaceImplementation_BecomesQualifiedMethod()
    {
        var (output, _, _) = Rewrite("""
            public interface IFoo { int X { get; set; } }
            public class C : IFoo
            {
                private int _x;
                int IFoo.X { get { return _x; } set { _x = value; } }
            }
            """);
        AssertContains(output, "int IFoo.get_X() { return _x; }");
        AssertContains(output, "void IFoo.set_X(int value) { _x = value; }");
        AssertOutputCompiles(output);
    }

    [Fact]
    public void ExplicitInterface_CoexistsWithSameNameProperty()
    {
        var (output, _, _) = Rewrite("""
            public interface IFoo { int X { get; } }
            public class C : IFoo
            {
                private int _x;
                public int X { get { return 1; } }
                int IFoo.X { get { return _x; } }
            }
            """);
        AssertContains(output, "public int get_X() { return 1; }");
        AssertContains(output, "int IFoo.get_X() { return _x; }");
        AssertOutputCompiles(output);
    }

    // ---- §7.1-9 特性 ----

    [Fact]
    public void Attribute_FieldTargetRoutesToField()
    {
        var (output, _, _) = Rewrite("""
            using System;
            class C
            {
                [field: NonSerialized]
                public int X { get; set; }
            }
            """);
        AssertContains(output, "[NonSerialized]");
        AssertOutputCompiles(output);
    }

    // ---- §7.1-10 跳过族 ----

    [Fact]
    public void Skip_ExpressionTree_5117()
    {
        var src = """
            class C
            {
                public int X { get; set; }
            }
            class App
            {
                System.Linq.Expressions.Expression<System.Func<int>> F(C c)
                    => () => c.X;
            }
            """;
        var model = ParseWithSemantics(src);
        var pp = new MemberDesugarPreprocessor(new MemberDesugarOptions());
        var stats = new MemberDesugarStatistics();
        var diags = new List<MemberDesugarDiagnostic>();
        var result = pp.RewriteFile(src, model, stats, diags);
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.ExpressionTreeSite);
        AssertContains(result.OutputCode, "public int X { get; set; }");
    }

    [Fact]
    public void Skip_ExpressionTree_ForceLifts()
    {
        var src = """
            class C
            {
                public int X { get; set; }
            }
            class App
            {
                System.Linq.Expressions.Expression<System.Func<int>> F(C c)
                    => () => c.X;
            }
            """;
        var model = ParseWithSemantics(src);
        var options = new MemberDesugarOptions
        {
            ForceQualifiedNames = new HashSet<string> { "C", "C::X" },
        };
        var pp = new MemberDesugarPreprocessor(options);
        var stats = new MemberDesugarStatistics();
        var diags = new List<MemberDesugarDiagnostic>();
        pp.RewriteFile(src, model, stats, diags);
        Assert.DoesNotContain(diags, d => d.Code == MemberDesugarDiagnosticCode.ExpressionTreeSite);
    }

    [Fact]
    public void Skip_RefReturning_5115()
    {
        var (output, _, diags) = Rewrite("""
            class C
            {
                private int _x;
                public ref int X => ref _x;
            }
            """);
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.RefReturningProperty);
        AssertContains(output, "public ref int X => ref _x;");
    }

    [Fact]
    public void Skip_Partial_5114()
    {
        var (output, _, diags) = Rewrite("""
            partial class C
            {
                public partial int X { get; set; }
            }
            """);
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.PartialProperty);
        AssertContains(output, "public partial int X { get; set; }");
    }

    [Fact]
    public void Skip_StaticAbstractInterface_5116()
    {
        var (output, _, diags) = Rewrite("""
            interface I
            {
                static abstract int X { get; }
            }
            """);
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.StaticAbstractInterfaceProperty);
        AssertContains(output, "static abstract int X { get; }");
    }

    [Fact]
    public void Skip_FieldKeyword_5122()
    {
        var (output, _, diags) = Rewrite("""
            class C
            {
                public int X { get => field; set { } }
            }
            """);
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.FieldKeywordAccessor);
        AssertContains(output, "public int X { get => field; set { } }");
    }

    [Fact]
    public void Skip_PositionalRecord_5123()
    {
        var (output, _, diags) = Rewrite("""
            record R(int X);
            """);
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.PositionalRecordProperty);
        AssertContains(output, "record R(int X)");
    }

    [Fact]
    public void Skip_WithSite_5124()
    {
        var (output, _, diags) = Rewrite("""
            record R { public int X { get; set; } }
            class App
            {
                public R M(R r) => r with { X = 1 };
            }
            """);
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.WithSite);
        AssertContains(output, "public int X { get; set; }");
    }

    // ---- §7.1-11 边界零误伤 ----
    // C13 落地后事件有独立规则：事件按 add_E/remove_E 去糖，索引器/运算符仍零误伤。

    [Fact]
    public void Boundary_IndexerEventOperatorUntouched()
    {
        var (output, _, _) = Rewrite("""
            class C
            {
                public int this[int i] { get { return i; } set { } }
                public event System.EventHandler? E;
                public static C operator +(C a, C b) => a;
                public int P { get; set; }
                public void M(C c)
                {
                    c[0] = c.P;
                    c.E += null;
                    var d = c + c;
                }
            }
            """);
        AssertContains(output, "public int get_Item(int i)");
        AssertContains(output, "c.set_Item(0, c.get_P());");
        AssertNotContains(output, "public event System.EventHandler? E;");
        AssertContains(output, "private System.EventHandler? E;");
        AssertContains(output, "public void add_E(System.EventHandler? value)");
        AssertContains(output, "c.E = Cs2jPrelude.CsPrelude.Combine(c.E, null);");
        AssertContains(output, "public static C operator +(C a, C b) => a;");
        AssertOutputCompiles(output);
    }

    // ---- §7.1-12 工程性质 ----

    [Fact]
    public void Idempotent_SecondRunNoChange()
    {
        var src = """
            class C
            {
                public int X { get; set; }
                public void M() { X += 1; var y = new C { X = 2 }; }
            }
            """;
        var (first, _, _) = Rewrite(src);
        var model = ParseWithSemantics(first);
        var pp = new MemberDesugarPreprocessor();
        var stats = new MemberDesugarStatistics();
        var diags = new List<MemberDesugarDiagnostic>();
        var second = pp.RewriteFile(first, model, stats, diags);
        Assert.False(second.Changed);
        Assert.Equal(first, second.OutputCode);
    }

    [Fact]
    public void CleanFile_Unchanged()
    {
        var src = "class C { public int F; public void M() { F = 1; } }";
        var (output, stats, _) = Rewrite(src);
        Assert.False(Normalize(output) != Normalize(src) && output != src);
        Assert.Equal(0, stats.PropertiesPlanned);
    }
}
