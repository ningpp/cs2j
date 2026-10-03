using System.IO;
using System.Linq;
using System.Reflection;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.MemberDesugar.Records;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C27 record `with` 预处理 M1 单测矩阵：RC-1/RC-2 克隆工厂注入（abstract/virtual/
/// override/sealed + 泛型 + 幂等去重）、WR-1/2/3/6 使用点改写（set_X 属性、字段直写、
/// 源接收者单次求值、this with、多态虚分派）、跳过族原子性（5220/5223/5224/5225/
/// 5227/5228）、WR-8 5230 residual、默认关零行为与二次执行零 diff。
/// </summary>
public class MemberDesugarRecordWithUnitTests
{
    private static MemberDesugarOptions Enabled(bool strict = true, bool info = false)
        => new()
        {
            Strict = strict,
            ReportInfo = info,
            Records = new RecordOptions { Enabled = true },
        };

    private static (string Output, MemberDesugarStatistics Stats,
        List<MemberDesugarDiagnostic> Diags) Rewrite(
            string src, MemberDesugarOptions? options = null, params string[] extra)
        => MemberDesugarPropertyUnitTests.Rewrite(src, options ?? new MemberDesugarOptions(), extra);

    private static void AssertCompiles(string output, params string[] extra)
        => MemberDesugarPropertyUnitTests.AssertOutputCompiles(output, extra);

    private static string Normalize(string code)
        => new string(code.Where(ch => !char.IsWhiteSpace(ch)).ToArray());

    private static void AssertContains(string output, string expected)
        => Assert.True(Normalize(output).Contains(Normalize(expected).Trim(), StringComparison.Ordinal),
            $"Expected output to contain:\n{expected}\n--- actual ---\n{output}");

    private static void AssertNotContains(string output, string unexpected)
        => Assert.False(Normalize(output).Contains(Normalize(unexpected).Trim(), StringComparison.Ordinal),
            $"Expected output NOT to contain:\n{unexpected}\n--- actual ---\n{output}");

    private static void AssertNoErrors(List<MemberDesugarDiagnostic> diags)
        => Assert.DoesNotContain(diags, d => d.Severity == MemberDesugarSeverity.Error);

    // ---- WR-1：单/多成员 + 属性 set_X / 字段直写 ----

    [Fact]
    public void Enabled_SingleProperty_LocalDecl_VirtualFactoryAndSetter()
    {
        var src = """
            public record R
            {
                public int X { get; set; }
            }
            public static class P
            {
                public static R Run(R r)
                {
                    var q = r with { X = 1 };
                    return q;
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertNoErrors(diags);
        AssertContains(output, "public virtual R Cs2jWithClone() => new R(this);");
        AssertContains(output, "__cs2j_wclone");
        AssertContains(output, "__cs2j_wclone0.set_X(1)");
        AssertNotContains(output, " with ");
        Assert.Equal(1, stats.CloneFactoriesSynthesized);
        Assert.Equal(1, stats.WithSitesTotal);
        Assert.Equal(1, stats.PropertyAssignments);
        Assert.Equal(0, stats.FieldAssignments);
        Assert.Equal(0, stats.ResidualWithCount);
    }

    [Fact]
    public void Enabled_MultiMembers_FieldDirectWrite_AndPropertySetter()
    {
        var src = """
            public record R
            {
                public int X { get; set; }
                public int F;
                public string? Name { get; set; }
            }
            public static class P
            {
                public static R Run(R r)
                {
                    var q = r with { X = 1, F = 2, Name = "n" };
                    return q;
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertNoErrors(diags);
        AssertContains(output, "__cs2j_wclone0.set_X(1)");
        AssertContains(output, "__cs2j_wclone0.F = 2");
        AssertContains(output, "__cs2j_wclone0.set_Name(\"n\")");
        Assert.Equal(2, stats.PropertyAssignments);
        Assert.Equal(1, stats.FieldAssignments);
    }

    // ---- WR-3：空初始化器内联 ----

    [Fact]
    public void Enabled_EmptyInitializer_InlinesFactoryCall_NoTemps()
    {
        var src = """
            public record R
            {
                public int X { get; set; }
            }
            public static class P
            {
                public static R Run(R r)
                {
                    var q = r with { };
                    return q;
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertNoErrors(diags);
        AssertContains(output, "var q = r.Cs2jWithClone();");
        AssertNotContains(output, "__cs2j_wsrc");
        AssertNotContains(output, "__cs2j_wclone");
        Assert.Equal(1, stats.WithSitesEmpty);
        Assert.Equal(0, stats.TemporariesPromoted);
    }

    [Fact]
    public void Enabled_EmptyInitializer_ComplexReceiver_ParenthesizedAndValidInFieldInit()
    {
        var src = """
            public record R
            {
                public int X { get; set; }
                public static R Make() => new R();
                public R Peer = new R() with { };
                public static R Run() => Make() with { };
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertNoErrors(diags);
        // 对象创建接收者加括号；字段初始化器位（不可提升）空 with 内联合法。
        AssertContains(output, "(new R()).Cs2jWithClone()");
        AssertContains(output, "Make().Cs2jWithClone()");
        Assert.Equal(2, stats.WithSitesEmpty);
    }

    // ---- WR-1/决策 10：源接收者只求值一次 ----

    [Fact]
    public void Enabled_MethodReturnReceiver_EvaluatedExactlyOnce_Reflection()
    {
        var src = """
            public record R
            {
                public int X { get; set; }
                public static int Calls;
                public static R Get() { Calls++; return new R(); }
            }
            public static class P
            {
                public static R Run()
                {
                    var q = R.Get() with { X = 5 };
                    return q;
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertNoErrors(diags);
        AssertContains(output, "var __cs2j_wsrc0 = R.Get();");
        AssertContains(output, "var __cs2j_wclone1 = __cs2j_wsrc0.Cs2jWithClone();");
        Assert.Equal(2, stats.TemporariesPromoted);

        var asm = CompileToAssembly(output);
        var rType = asm.GetType("R")!;
        var result = asm.GetType("P")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null)!;
        Assert.Equal(1, rType.GetField("Calls", BindingFlags.Public | BindingFlags.Static)!.GetValue(null));
        Assert.Equal(5, rType.GetMethod("get_X", BindingFlags.Public | BindingFlags.Instance)!
            .Invoke(result, null));
    }

    [Fact]
    public void Enabled_ArrayElementReceiver_IndexEvaluatedExactlyOnce_Reflection()
    {
        var src = """
            public record R
            {
                public int X { get; set; }
            }
            public static class P
            {
                // 不能用字段初始化器里的对象初始化器：那会让 X 的 property plan 判 5123
                // 不可提升而整体不展开（与本站点无关的既有 C11 规则）；用静态构造函数。
                public static R[] Arr = null!;
                public static int IdxCalls;
                static P()
                {
                    Arr = new R[1];
                    Arr[0] = new R();
                    Arr[0].X = 9;
                }
                public static int NextIndex() { IdxCalls++; return 0; }
                public static R Run()
                {
                    var q = Arr[NextIndex()] with { X = 2 };
                    return q;
                }
            }
            """;
        var (output, _, diags) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertNoErrors(diags);
        var asm = CompileToAssembly(output);
        var result = asm.GetType("P")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null)!;
        Assert.Equal(1, asm.GetType("P")!.GetField("IdxCalls", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null));
        Assert.Equal(2, asm.GetType("R")!.GetMethod("get_X", BindingFlags.Public | BindingFlags.Instance)!
            .Invoke(result, null));
    }

    // ---- WR-6：this with + return 位 + switch 节提升 ----

    [Fact]
    public void Enabled_ThisWith_ReturnStatement_LiftsClone()
    {
        var src = """
            public record R
            {
                public int X { get; set; }
                public R Bump()
                {
                    return this with { X = X + 1 };
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertNoErrors(diags);
        // this 是简单接收者，不发 wsrc；克隆后 RHS 的属性读留给 property 阶段 get_X。
        AssertContains(output, "var __cs2j_wclone0 = this.Cs2jWithClone();");
        AssertNotContains(output, "__cs2j_wsrc");
        AssertContains(output, "__cs2j_wclone0.set_X(");
    }

    [Fact]
    public void Enabled_WithInsideSwitchSection_ReturnSplicedAndCompiles()
    {
        var src = """
            public record R
            {
                public int X { get; set; }
            }
            public static class P
            {
                public static R Run(R r, int n)
                {
                    switch (n)
                    {
                        case 1:
                            return r with { X = 11 };
                        default:
                            return r;
                    }
                }
            }
            """;
        var (output, _, diags) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertNoErrors(diags);
        AssertContains(output, "__cs2j_wclone0.set_X(11)");
        AssertNotContains(output, " with ");
    }

    [Fact]
    public void Enabled_TwoSitesSameBlock_TempsAreUnique()
    {
        var src = """
            public record R
            {
                public int X { get; set; }
            }
            public static class P
            {
                public static void Run(R r)
                {
                    var a = r with { X = 1 };
                    var b = r with { X = 2 };
                    System.Console.WriteLine(a);
                    System.Console.WriteLine(b);
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertNoErrors(diags);
        AssertContains(output, "__cs2j_wclone0");
        AssertContains(output, "__cs2j_wclone1");
        Assert.Equal(2, stats.WithSitesTotal);
    }

    // ---- WR-2：多态虚分派链 ----

    [Fact]
    public void Enabled_AbstractBase_DerivedOverride_VirtualDispatchKeepsRuntimeType()
    {
        var src = """
            public abstract record Base
            {
                public int X { get; set; }
            }
            public sealed record Derived : Base
            {
            }
            public static class P
            {
                public static Base Run(Base b)
                {
                    var q = b with { X = 3 };
                    return q;
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertNoErrors(diags);
        AssertContains(output, "public abstract Base Cs2jWithClone();");
        AssertContains(output, "public override Derived Cs2jWithClone() => new Derived(this);");
        AssertNotContains(output, " with ");

        var asm = CompileToAssembly(output);
        var derived = System.Activator.CreateInstance(asm.GetType("Derived")!)!;
        var result = asm.GetType("P")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new[] { derived })!;
        Assert.Equal("Derived", result.GetType().Name);
        Assert.Equal(3, asm.GetType("Base")!.GetMethod("get_X", BindingFlags.Public | BindingFlags.Instance)!
            .Invoke(result, null));
    }

    [Fact]
    public void Enabled_SealedRoot_EmitsNonVirtualFactory()
    {
        var src = """
            public sealed record R
            {
                public int X { get; set; }
            }
            public static class P
            {
                public static R Run(R r)
                {
                    var q = r with { X = 7 };
                    return q;
                }
            }
            """;
        var (output, _, diags) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertNoErrors(diags);
        AssertContains(output, "public R Cs2jWithClone() => new R(this);");
        AssertNotContains(output, "virtual R Cs2jWithClone");
    }

    [Fact]
    public void Enabled_ThreeLevelHierarchy_RuntimeTypePreserved()
    {
        var src = """
            public record A { public int X { get; set; } }
            public record B : A { }
            public record C : B { }
            public static class P
            {
                public static A Run(A a)
                {
                    var q = a with { X = 42 };
                    return q;
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertNoErrors(diags);
        AssertContains(output, "public virtual A Cs2jWithClone() => new A(this);");
        AssertContains(output, "public override B Cs2jWithClone() => new B(this);");
        AssertContains(output, "public override C Cs2jWithClone() => new C(this);");
        // 3 个工厂（A/B/C 全在虚分派闭包内，即使站点只作用于 A 静态类型）。
        Assert.Equal(3, stats.CloneFactoriesSynthesized);

        var asm = CompileToAssembly(output);
        var c = System.Activator.CreateInstance(asm.GetType("C")!)!;
        var result = asm.GetType("P")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new[] { c })!;
        Assert.Equal("C", result.GetType().Name);
    }

    [Fact]
    public void Enabled_GenericRecord_FactoryCarriesTypeParameters()
    {
        var src = """
            public record Box<T>
            {
                public T Value { get; set; }
            }
            public static class P
            {
                public static Box<int> Run(Box<int> b)
                {
                    var q = b with { Value = 9 };
                    return q;
                }
            }
            """;
        var (output, _, diags) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertNoErrors(diags);
        AssertContains(output, "public virtual Box<T> Cs2jWithClone() => new Box<T>(this);");
        AssertContains(output, "__cs2j_wclone0.set_Value(9)");

        var asm = CompileToAssembly(output);
        var box = System.Activator.CreateInstance(
            asm.GetType("Box`1")!.MakeGenericType(typeof(int)))!;
        var result = asm.GetType("P")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new[] { box })!;
        Assert.Equal(9, result.GetType().GetMethod("get_Value")!.Invoke(result, null));
    }

    [Fact]
    public void Enabled_UserCopyConstructor_IsInvocatedByFactory_Reflection()
    {
        var src = """
            public record R
            {
                public int X { get; set; }
                public static int CopyCalls;
                public R() { }
                protected R(R other)
                {
                    CopyCalls++;
                    X = other.X;
                }
            }
            public static class P
            {
                public static R Run(R r)
                {
                    var q = r with { X = 8 };
                    return q;
                }
            }
            """;
        var (output, _, diags) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertNoErrors(diags);
        var asm = CompileToAssembly(output);
        var rType = asm.GetType("R")!;
        var result = asm.GetType("P")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new[] { System.Activator.CreateInstance(rType)! })!;
        Assert.Equal(1, rType.GetField("CopyCalls", BindingFlags.Public | BindingFlags.Static)!.GetValue(null));
        Assert.Equal(8, rType.GetMethod("get_X", BindingFlags.Public | BindingFlags.Instance)!
            .Invoke(result, null));
    }

    [Fact]
    public void Enabled_FactoryAlreadyExists_NotReSynthesized_AndSiteCallsIt()
    {
        var src = """
            public record R
            {
                public int X { get; set; }
                public int Marker;
                public R Cs2jWithClone()
                {
                    var c = new R();
                    c.Marker = 1;
                    return c;
                }
            }
            public static class P
            {
                public static R Run(R r)
                {
                    var q = r with { X = 1 };
                    return q;
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertNoErrors(diags);
        // 用户工厂同签名（零参返回本类型）→ 幂等不重发；with 站点仍调该名。
        Assert.Equal(0, stats.CloneFactoriesSynthesized);
        // 一次用户方法定义 + 一处站点调用。
        Assert.Equal(2, Normalize(output).Split("Cs2jWithClone").Length - 1);
        AssertContains(output, "__cs2j_wclone0.set_X(1)");

        var asm = CompileToAssembly(output);
        var result = asm.GetType("P")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new[] { System.Activator.CreateInstance(asm.GetType("R")!)! })!;
        Assert.Equal(1, asm.GetType("R")!.GetField("Marker", BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(result));
    }

    // ---- 跳过族：plan 原子（声明与站点都不动）----

    [Fact]
    public void Gate5223_NonLiftableFieldInitializer_AtomicSkip_NoFactory()
    {
        var src = """
            public record R
            {
                public int X { get; set; }
                public R Peer = new R() with { X = 1 };
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.NonLiftableWithContext);
        Assert.Contains("with", output);
        AssertNotContains(output, "Cs2jWithClone");
        Assert.Equal(0, stats.CloneFactoriesSynthesized);
        Assert.Equal(1, stats.SkippedByReason[MemberDesugarDiagnosticCode.NonLiftableWithContext]);
    }

    [Fact]
    public void WR5_NestedWith_RhsChain_PostOrderLift()
    {
        var src = """
            public record R
            {
                public int X { get; set; }
                public R? Next { get; set; }
            }
            public static class P
            {
                public static R Run(R r)
                {
                    var q = r with { X = 1, Next = r with { X = 2 } };
                    return q;
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertNoErrors(diags);
        AssertCompiles(output);
        // 外层克隆先建；构造 set_Next 的 RHS 时先提升内层，内层赋值先于外层 set_Next，
        // RHS 最终替换为内层克隆标识符。
        AssertContains(output, "var __cs2j_wclone0 = r.Cs2jWithClone();");
        AssertContains(output, "__cs2j_wclone0.set_X(1);");
        AssertContains(output, "var __cs2j_wclone1 = r.Cs2jWithClone();");
        AssertContains(output, "__cs2j_wclone1.set_X(2);");
        AssertContains(output, "__cs2j_wclone0.set_Next(__cs2j_wclone1);");
        Assert.Equal(0, stats.ResidualWithCount);
        Assert.Equal(2, stats.WithSitesTotal);

        var asm = CompileToAssembly(output);
        var rType = asm.GetType("R")!;
        var r1 = Activator.CreateInstance(rType)!;
        var q = asm.GetType("P")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new[] { r1 })!;
        Assert.Equal(1, rType.GetMethod("get_X")!.Invoke(q, null));
        var next = rType.GetMethod("get_Next")!.Invoke(q, null)!;
        Assert.Equal(2, rType.GetMethod("get_X")!.Invoke(next, null));
        Assert.NotSame(r1, q);
        Assert.NotSame(r1, next);
    }

    [Fact]
    public void Gate5224_NestedWith_NotDirectPosition_Untouched()
    {
        // 内层 with 埋在 RHS 成员访问的接收者位（非外层接收者/非整个右值）→ 5224 原子跳过。
        var src = """
            public record R
            {
                public int X { get; set; }
                public R? Next { get; set; }
            }
            public static class P
            {
                public static int Run(R r)
                {
                    var q = r with { X = (r with { X = 2 }).X };
                    return q.X;
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled(strict: false));
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.UnsupportedNestedInitializer);
        Assert.Contains("with", output);
        Assert.Equal(0, stats.ResidualWithCount);
        AssertCompiles(output);
    }

    [Fact]
    public void WR4_PlainRecordStruct_ValueCopy_DirectPropertyWrite()
    {
        var src = """
            public record struct S
            {
                public int X { get; set; }
            }
            public static class P
            {
                public static S RunS(S s)
                {
                    var t = s with { X = 1 };
                    return t;
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertNoErrors(diags);
        AssertCompiles(output);
        // 无工厂：克隆局部直接按值复制源；Records 发属性直写，C11 归一为 set_X，
        // 但绝不出现克隆工厂。
        AssertContains(output, "var __cs2j_wclone0 = s;");
        AssertContains(output, "__cs2j_wclone0.set_X(1);");
        AssertNotContains(output, "Cs2jWithClone");
        Assert.Equal(0, stats.CloneFactoriesSynthesized);
        Assert.Equal(1, stats.WithSitesStruct);
        Assert.Equal(0, stats.WithSitesClass);
        Assert.Equal(0, stats.ResidualWithCount);

        var asm = CompileToAssembly(output);
        var sType = asm.GetType("S")!;
        var s1 = Activator.CreateInstance(sType)!;
        var s2 = asm.GetType("P")!.GetMethod("RunS", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new[] { s1 })!;
        Assert.Equal(1, sType.GetMethod("get_X")!.Invoke(s2, null));
        Assert.Equal(0, sType.GetMethod("get_X")!.Invoke(s1, null));
    }

    [Fact]
    public void WR4_Tuple_ItemNDirectWrite()
    {
        var src = """
            public static class P
            {
                public static (int A, string B) Run((int A, string B) t)
                {
                    return t with { Item1 = 2 };
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertNoErrors(diags);
        AssertCompiles(output);
        // 元组接收者在树外（orphan plan）：双局部值拷贝，ItemN 直写，无工厂/无 set_X。
        AssertContains(output, "var __cs2j_wclone0 = t;");
        AssertContains(output, "__cs2j_wclone0.Item1 = 2;");
        AssertNotContains(output, "Cs2jWithClone");
        AssertNotContains(output, "set_Item1");
        Assert.Equal(1, stats.WithSitesStruct);
        Assert.Equal(0, stats.ResidualWithCount);

        var asm = CompileToAssembly(output);
        var result = asm.GetType("P")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object[] { (1, "x") })!;
        var tuple = ((int A, string B))result;
        Assert.Equal((2, "x"), tuple);
    }

    [Fact]
    public void WR4_EmptyStruct_AlwaysLifts_AndSourceEvaluatedOnce()
    {
        // 空 with：struct 也保守发双局部；非安全接收者先提升 wsrc，保证只求值一次。
        var src = """
            public record struct S
            {
                public int X { get; set; }
                public static int Calls;
                public static S Make() { Calls++; return new S(); }
            }
            public static class P
            {
                public static (S, S, int) Run(S s)
                {
                    var a = s with { };
                    var b = S.Make() with { };
                    return (a, b, S.Calls);
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertNoErrors(diags);
        AssertCompiles(output);
        AssertContains(output, "var __cs2j_wclone0 = s;");
        AssertContains(output, "var __cs2j_wsrc1 = S.Make();");
        AssertContains(output, "var __cs2j_wclone2 = __cs2j_wsrc1;");
        AssertNotContains(output, "Cs2jWithClone");
        Assert.Equal(2, stats.WithSitesStruct);
        Assert.Equal(2, stats.WithSitesEmpty);

        var asm = CompileToAssembly(output);
        var sType = asm.GetType("S")!;
        var s0 = Activator.CreateInstance(sType)!;
        var tuple = (System.Runtime.CompilerServices.ITuple)asm.GetType("P")!
            .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new[] { s0 })!;
        Assert.Equal(1, tuple[2]);
        Assert.NotSame(tuple[0], s0);
        Assert.NotSame(tuple[1], s0);
    }

    [Fact]
    public void WR5_NestedWith_ReceiverChain_PostOrder()
    {
        var src = """
            public record R
            {
                public int X { get; set; }
            }
            public static class P
            {
                public static R Run(R r)
                {
                    var q = (r with { X = 1 }) with { X = 3 };
                    return q;
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertNoErrors(diags);
        AssertCompiles(output);
        AssertContains(output, "var __cs2j_wclone0 = r.Cs2jWithClone();");
        AssertContains(output, "__cs2j_wclone0.set_X(1);");
        // 外层源直接取内层克隆，不再读 r，也不发 wsrc。
        AssertContains(output, "var __cs2j_wclone1 = __cs2j_wclone0.Cs2jWithClone();");
        AssertContains(output, "__cs2j_wclone1.set_X(3);");
        AssertNotContains(output, "__cs2j_wsrc");
        Assert.Equal(2, stats.WithSitesTotal);

        var asm = CompileToAssembly(output);
        var rType = asm.GetType("R")!;
        var r1 = Activator.CreateInstance(rType)!;
        var q = asm.GetType("P")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new[] { r1 })!;
        Assert.Equal(3, rType.GetMethod("get_X")!.Invoke(q, null));
        Assert.Equal(0, rType.GetMethod("get_X")!.Invoke(r1, null));
    }

    [Fact]
    public void WR5_NestedWith_OuterClass_InnerStruct_Mixed()
    {
        var src = """
            public record struct Box(int N);
            public record class Holder
            {
                public Box B { get; set; }
            }
            public static class P
            {
                public static Holder Run(Holder h, Box b)
                {
                    return h with { B = b with { N = 7 } };
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertNoErrors(diags);
        AssertCompiles(output);
        // 外层克隆局部先声明（wclone0），构造 RHS 时内层 struct 值拷贝再提升（wclone1），
        // 二者独立，次序不影响语义；外层 set_B 的 RHS 最终为内层克隆标识符。
        AssertContains(output, "var __cs2j_wclone0 = h.Cs2jWithClone();");
        AssertContains(output, "var __cs2j_wclone1 = b;");
        AssertContains(output, "__cs2j_wclone1.set_N(7);");
        AssertContains(output, "__cs2j_wclone0.set_B(__cs2j_wclone1);");
        Assert.Equal(1, stats.WithSitesStruct);
        Assert.Equal(1, stats.WithSitesClass);

        var asm = CompileToAssembly(output);
        var holderType = asm.GetType("Holder")!;
        var boxType = asm.GetType("Box")!;
        var h0 = Activator.CreateInstance(holderType)!;
        var b0 = Activator.CreateInstance(boxType, 0)!;
        var h1 = asm.GetType("P")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new[] { h0, b0 })!;
        var b1 = holderType.GetMethod("get_B")!.Invoke(h1, null)!;
        Assert.Equal(7, boxType.GetMethod("get_N")!.Invoke(b1, null));
        Assert.Equal(0, boxType.GetMethod("get_N")!.Invoke(b0, null));
    }

    [Fact]
    public void WR5_NestedWith_ThreeLevels_PostOrder()
    {
        var src = """
            public record R
            {
                public int X { get; set; }
                public R? Next { get; set; }
            }
            public static class P
            {
                public static R Run(R r)
                {
                    var q = r with { Next = r with { Next = r with { X = 9 } } };
                    return q;
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertNoErrors(diags);
        AssertCompiles(output);
        // 最内层最先，逐层向外，每层 RHS 都是内层克隆标识符。
        AssertContains(output, "__cs2j_wclone2.set_X(9);");
        AssertContains(output, "__cs2j_wclone1.set_Next(__cs2j_wclone2);");
        AssertContains(output, "__cs2j_wclone0.set_Next(__cs2j_wclone1);");
        var normalized = Normalize(output);
        Assert.True(normalized.IndexOf("__cs2j_wclone2.set_X(9)", StringComparison.Ordinal)
            < normalized.IndexOf("__cs2j_wclone1.set_Next(__cs2j_wclone2)", StringComparison.Ordinal));
        Assert.True(normalized.IndexOf("__cs2j_wclone1.set_Next(__cs2j_wclone2)", StringComparison.Ordinal)
            < normalized.IndexOf("__cs2j_wclone0.set_Next(__cs2j_wclone1)", StringComparison.Ordinal));
        Assert.Equal(3, stats.WithSitesTotal);
        Assert.Equal(0, stats.ResidualWithCount);

        var asm = CompileToAssembly(output);
        var rType = asm.GetType("R")!;
        var r1 = Activator.CreateInstance(rType)!;
        var q = asm.GetType("P")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new[] { r1 })!;
        var lvl1 = rType.GetMethod("get_Next")!.Invoke(q, null)!;
        var lvl2 = rType.GetMethod("get_Next")!.Invoke(lvl1, null)!;
        Assert.Equal(9, rType.GetMethod("get_X")!.Invoke(lvl2, null));
    }

    [Fact]
    public void Gate5228_NonRecordClassReceiver_Untouched()
    {
        // 输入本身带 CS8858（非 record 不支持 with）；预处理器仍逐站点发 5228，
        // 站点原子保留，不合成任何工厂。
        var src = """
            public class C
            {
                public int X { get; set; }
            }
            public static class P
            {
                public static C Run(C c)
                {
                    var d = c with { X = 1 };
                    return d;
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled(strict: false));
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.NonRecordClassWith);
        Assert.Contains("with", output);
        AssertNotContains(output, "Cs2jWithClone");
        Assert.Equal(0, stats.ResidualWithCount);
    }

    [Fact]
    public void GateAtomicity_MixedFeasibleAnd5223Site_AllSitesUntouched_StrictError()
    {
        // plan 原子铁律：同一 record 一个不可提升站点（字段初始化器 5223）即整 plan 闸门——
        // 另一处合法局部声明站点也必须原样保留、零重写；工厂仍按 FactoryRequired 独立发
        // （分析层既定语义：GateCode 只管站点不动，不抑制工厂）；strict 下诊断升级 Error。
        var src = """
            public record R
            {
                public int X { get; set; }
                public R Peer = new R() with { X = 0 };
            }
            public static class P
            {
                public static R Run(R r)
                {
                    var q = r with { X = 1 };
                    return q;
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled(strict: true));
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.NonLiftableWithContext
            && d.Severity == MemberDesugarSeverity.Error);
        Assert.Equal(2, Normalize(output).Split("with").Length - 1);
        AssertContains(output, "Cs2jWithClone");
        Assert.Equal(1, stats.CloneFactoriesSynthesized);
        Assert.Equal(0, stats.WithSitesTotal);
        Assert.Equal(0, stats.TemporariesPromoted);
        Assert.Equal(0, stats.ResidualWithCount);
    }

    [Fact]
    public void Enabled_FieldOnlyRecord_FactoryAndDirectFieldWrite_NoPropertyMethods()
    {
        var src = """
            public record R
            {
                public int F;
            }
            public static class P
            {
                public static R Run(R r)
                {
                    var q = r with { F = 3 };
                    return q;
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertNoErrors(diags);
        AssertContains(output, "public virtual R Cs2jWithClone() => new R(this);");
        AssertContains(output, "__cs2j_wclone0.F = 3");
        AssertNotContains(output, "set_F(");
        Assert.Equal(1, stats.FieldAssignments);
        Assert.Equal(0, stats.PropertyAssignments);
    }

    [Fact]
    public void Enabled_PropertyReadSitesCoexist_DesugaredToGetter_AndCompiles()
    {
        var src = """
            public record R
            {
                public int X { get; set; }
                public int Bump()
                {
                    return X + 1;
                }
                public R Self()
                {
                    var q = this with { X = Bump() };
                    return q;
                }
            }
            """;
        var (output, _, diags) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertNoErrors(diags);
        // with 先于 property 阶段消除：RHS 原节点（Bump()）与类型内属性读（X）交给后续
        // property 阶段正常降形为 get_X()，两族天然共存。
        AssertContains(output, "get_X() + 1");
        AssertContains(output, "__cs2j_wclone0.set_X(Bump())");
    }

    [Fact]
    public void Gate5225_AnonymousTypeReceiver_Untouched()
    {
        var src = """
            public static class P
            {
                public static void Run()
                {
                    var a = new { X = 1 };
                    var b = a with { X = 2 };
                    System.Console.WriteLine(b);
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled(strict: false));
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.AnonymousTypeWith);
        Assert.Contains("with", output);
        Assert.Equal(0, stats.ResidualWithCount);
    }

    [Fact]
    public void Gate5227_DynamicReceiver_Untouched()
    {
        var src = """
            public static class P
            {
                public static void Run(dynamic d)
                {
                    var x = d with { X = 1 };
                    System.Console.WriteLine(x);
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled(strict: false));
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.DynamicWith);
        Assert.Contains("with", output);
        Assert.Equal(0, stats.ResidualWithCount);
    }

    // ---- 默认关 / 幂等 / 干净文件 ----

    [Fact]
    public void Disabled_RecordWith_ZeroPlanZeroDiff()
    {
        var src = """
            public record R
            {
                public int X { get; set; }
            }
            public static class P
            {
                public static R Run(R r)
                {
                    var q = r with { X = 1 };
                    return q;
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, new MemberDesugarOptions());
        Assert.Equal(Normalize(src), Normalize(output));
        Assert.Equal(0, stats.CloneFactoriesSynthesized);
        Assert.Equal(0, stats.WithSitesTotal);
        Assert.Equal(0, stats.RecordPlans);
        Assert.Equal(0, stats.ResidualWithCount);
        Assert.All(diags, d => Assert.NotEqual(MemberDesugarDiagnosticCode.ResidualWith, d.Code));
    }

    [Fact]
    public void Enabled_SecondRun_IsZeroDiff()
    {
        var src = """
            public record R
            {
                public int X { get; set; }
                public int F;
            }
            public static class P
            {
                public static R Run(R r)
                {
                    var q = r with { X = 1, F = 2 };
                    return q;
                }
            }
            """;
        var (first, _, _) = Rewrite(src, Enabled());
        AssertCompiles(first);
        var (second, stats2, diags2) = Rewrite(first, Enabled());
        AssertCompiles(second);
        Assert.Equal(Normalize(first), Normalize(second));
        Assert.Equal(0, stats2.CloneFactoriesSynthesized);
        Assert.Equal(0, stats2.WithSitesTotal);
        AssertNoErrors(diags2);
    }

    [Fact]
    public void CleanFileWithoutRecords_IsByteIdentical_PreservingBom()
    {
        var src = "﻿using System;\npublic class C\n{\n    public void M() { Console.WriteLine(1); }\n}\n";
        var (output, stats, _) = Rewrite(src, Enabled());
        Assert.Equal(src, output);
        Assert.Equal(1, stats.FilesSkippedClean);
    }

    [Fact]
    public void Enabled_InfoDiagnostics_Report5212And5213()
    {
        var src = """
            public record R
            {
                public int X { get; set; }
            }
            public static class P
            {
                public static R Run(R r)
                {
                    var q = r with { X = 1 };
                    return q;
                }
            }
            """;
        var (output, _, diags) = Rewrite(src, Enabled(info: true));
        AssertCompiles(output);
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.WithSiteRewritten);
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.CloneFactorySynthesized);
    }

    // ---- C27 M2 RC-3：位置 record class reshape ----

    [Fact]
    public void M2_PositionalClass_Reshape_FinalShape_AndWith()
    {
        var src = """
            public record Point(int X, int Y);
            public static class P
            {
                public static Point Run(Point p)
                {
                    var q = p with { X = 2 };
                    return q;
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled(info: true));
        AssertCompiles(output);
        AssertNoErrors(diags);
        // 参数表移除；字段 + get/set + 主 ctor + 复制 ctor + Deconstruct 全量发射。
        AssertNotContains(output, "record Point(int X, int Y)");
        AssertContains(output, "private int x;");
        AssertContains(output, "private int y;");
        AssertContains(output, "public int get_X()");
        AssertContains(output, "public void set_X(int value)");
        AssertContains(output, "public Point(int X, int Y)");
        AssertContains(output, "protected Point(Point other)");
        AssertContains(output, "public Point()");
        AssertContains(output, "public void Deconstruct(out int X, out int Y)");
        AssertContains(output, "public virtual Point Cs2jWithClone() => new Point(this);");
        AssertContains(output, "__cs2j_wclone0.set_X(2)");
        Assert.NotEqual(0, stats.PositionalRecordsReshaped);
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.PositionalReshaped);
    }

    [Fact]
    public void M2_PositionalClass_BareUseSitesInMethod_FallbackToAccessors()
    {
        // 方法体内残余的裸 X / p.X 在重解析模型上已无属性符号，靠回退绑定降形。
        var src = """
            using System;
            public record Point(int X, int Y)
            {
                public int Sum() { return X + Y; }
                public static int ReadOther(Point p) { return p.X + p.Y; }
                public void Print() { Console.Write(X); }
                public static string Name() { return nameof(X); }
            }
            public static class P
            {
                public static Point Run(Point p) { return p with { X = 2 }; }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertNoErrors(diags);
        AssertContains(output, "return get_X() + get_Y();");
        AssertContains(output, "return p.get_X() + p.get_Y();");
        AssertContains(output, "Console.Write(get_X());");
        AssertContains(output, "return \"X\";");
        Assert.NotEqual(0, stats.PositionalRecordsReshaped);
    }

    [Fact]
    public void M2_PositionalClass_Derived_PrimaryBaseArgs_Preserved()
    {
        var src = """
            public record Point(int X, int Y);
            public record ColoredPoint(int X, int Y, int Color) : Point(X, Y);
            public static class P
            {
                public static ColoredPoint Run(ColoredPoint p) { return p with { Color = 3 }; }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertNoErrors(diags);
        // 基部件降级为无参普通基类（继承保留），主 ctor 承载 : base(X, Y)。
        AssertContains(output, "public record ColoredPoint : Point");
        AssertContains(output, "public ColoredPoint(int X, int Y, int Color) : base(X, Y)");
        AssertContains(output, "this.color = Color;");
        AssertContains(output, "protected ColoredPoint(ColoredPoint other) : base(other)");
        // 转发参数不发字段/访问器；派生 Deconstruct 含基类全部 out 参数并 new 隐藏。
        AssertContains(output,
            "public new void Deconstruct(out int X, out int Y, out int Color)");
        AssertContains(output, "X = get_X();");
        AssertContains(output, "Color = get_Color();");
        AssertContains(output, "public override ColoredPoint Cs2jWithClone() => new ColoredPoint(this);");
        AssertContains(output, "__cs2j_wclone0.set_Color(3)");
        Assert.Equal(2, stats.PositionalRecordsReshaped);
    }

    [Fact]
    public void M2_PositionalClass_Derived_RuntimeSemantics_ChainCopyAndDeconstruct()
    {
        var src = """
            public record Point(int X, int Y);
            public record ColoredPoint(int X, int Y, int Color) : Point(X, Y);
            public static class P
            {
                public static ColoredPoint Run(ColoredPoint p) { return p with { Color = 3 }; }
            }
            """;
        var (output, _, _) = Rewrite(src, Enabled());
        AssertCompiles(output);
        var asm = CompileToAssembly(output);
        var cpType = asm.GetType("ColoredPoint")!;
        var c1 = Activator.CreateInstance(cpType, 1, 2, 9)!;
        Assert.Equal(1, cpType.GetMethod("get_X")!.Invoke(c1, null));
        Assert.Equal(9, cpType.GetMethod("get_Color")!.Invoke(c1, null));
        var dec = new object?[3];
        cpType.GetMethods().First(m => m.Name == "Deconstruct"
            && m.GetParameters().Length == 3).Invoke(c1, dec);
        Assert.Equal(new object?[] { 1, 2, 9 }, dec);
        var programType = asm.GetType("P")!;
        var c2 = programType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new[] { c1 })!;
        Assert.NotSame(c1, c2);
        Assert.Equal(3, cpType.GetMethod("get_Color")!.Invoke(c2, null));
        Assert.Equal(1, cpType.GetMethod("get_X")!.Invoke(c2, null));
        Assert.Equal(9, cpType.GetMethod("get_Color")!.Invoke(c1, null));
    }

    [Fact]
    public void M2_PositionalClass_Generic_ReshapeAndWith()
    {
        var src = """
            public record Box<T>(T Value, int Count);
            public static class P
            {
                public static Box<T> Run<T>(Box<T> b) { return b with { Count = 9 }; }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertNoErrors(diags);
        // Value 的 camelCase 与 set_X 形参名 value 撞，BackingFieldNaming 确定性消歧。
        AssertContains(output, "private T valueValue;");
        AssertContains(output, "public T get_Value()");
        AssertContains(output, "protected Box(Box<T> other)");
        AssertContains(output, "public virtual Box<T> Cs2jWithClone() => new Box<T>(this);");
        Assert.NotEqual(0, stats.PositionalRecordsReshaped);
    }

    [Fact]
    public void M2_PositionalClass_RuntimeSemantics_CtorAccessorsDeconstruct_CloneIndependence()
    {
        var src = """
            public record Point(int X, int Y);
            public static class P
            {
                public static Point Bump(Point p) { return p with { X = p.X + 10 }; }
            }
            """;
        var (output, _, _) = Rewrite(src, Enabled());
        AssertCompiles(output);
        var asm = CompileToAssembly(output);
        var pointType = asm.GetType("Point")!;
        var p1 = Activator.CreateInstance(pointType, 3, 4)!;
        Assert.Equal(3, pointType.GetMethod("get_X")!.Invoke(p1, null));
        Assert.Equal(4, pointType.GetMethod("get_Y")!.Invoke(p1, null));
        var deconstructed = new object?[2];
        pointType.GetMethod("Deconstruct")!.Invoke(p1, deconstructed);
        Assert.Equal(3, deconstructed[0]);
        Assert.Equal(4, deconstructed[1]);
        var programType = asm.GetType("P")!;
        var p2 = programType
            .GetMethod("Bump", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new[] { p1 })!;
        Assert.NotSame(p1, p2);
        Assert.Equal(13, pointType.GetMethod("get_X")!.Invoke(p2, null));
        Assert.Equal(3, pointType.GetMethod("get_X")!.Invoke(p1, null));
    }

    [Fact]
    public void M2_PositionalClass_SecondRun_BytesStable()
    {
        var src = """
            public record Point(int X, int Y);
            public static class P
            {
                public static Point Run(Point p) { return p with { X = 2 }; }
            }
            """;
        var (first, _, _) = Rewrite(src, Enabled());
        var (second, stats2, diags2) = Rewrite(first, Enabled());
        AssertCompiles(second);
        AssertNoErrors(diags2);
        Assert.Equal(first, second);
        Assert.Equal(0, stats2.WithSitesTotal);
        Assert.Equal(0, stats2.PositionalRecordsReshaped);
    }

    [Fact]
    public void M2_PositionalClass_Gate_FieldInitializerCapturesParameter_5220()
    {
        // 闸门①：字段初始化器捕获位置参数；reshape 后参数消失会 CS0103，整类型不碰。
        var src = """
            public record P(int X)
            {
                private int _z = X;
            }
            public static class Q
            {
                public static P Run(P p) { return p with { X = 2 }; }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled(strict: false));
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.PositionalReshapeGate);
        AssertNotContains(output, "Cs2jWithClone");
        Assert.Equal(0, stats.PositionalRecordsReshaped);
    }

    [Fact]
    public void M2_PositionalClass_Gate_UserDeconstruct_5220()
    {
        var src = """
            public record Point(int X, int Y)
            {
                public void Deconstruct(out int x, out int y) { x = X; y = Y; }
            }
            public static class P
            {
                public static Point Run(Point p) { return p with { X = 2 }; }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled(strict: false));
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.PositionalReshapeGate);
        AssertNotContains(output, "Cs2jWithClone");
        Assert.Equal(0, stats.PositionalRecordsReshaped);
    }

    // ---- RC-4：位置 record struct reshape（WR-4 站点值拷贝下一批落地）----

    [Fact]
    public void M2_PositionalStruct_ReshapeAndWith_ValueCopySetX()
    {
        var src = """
            public record struct Point(int X, int Y);
            public static class P
            {
                public static Point Run(Point p) { return p with { X = 2 }; }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertNoErrors(diags);

        // 终形：去参数表；字段/get/set；public 主 ctor + 必发 public 无参 ctor；Deconstruct。
        AssertContains(output, "public record struct Point");
        AssertNotContains(output, "record struct Point(");
        AssertContains(output, "private int x;");
        AssertContains(output, "public int get_X()");
        AssertContains(output, "public void set_X(int value)");
        AssertContains(output, "public Point(int X, int Y)");
        AssertContains(output, "this.x = X;");
        AssertContains(output, "public Point()");
        AssertContains(output, "public void Deconstruct(out int X, out int Y)");
        // struct 不发复制 ctor、不发克隆工厂、Deconstruct 不 new 隐藏。
        AssertNotContains(output, "protected Point(Point other)");
        AssertNotContains(output, "Cs2jWithClone");
        AssertNotContains(output, "public new void Deconstruct");

        // WR-4 站点：克隆局部按值复制，位置属性走终形 set_X。
        AssertContains(output, "var __cs2j_wclone0 = p;");
        AssertContains(output, "__cs2j_wclone0.set_X(2);");
        AssertNotContains(output, " with ");
        Assert.Equal(1, stats.PositionalRecordsReshaped);
        Assert.Equal(1, stats.WithSitesStruct);
        Assert.Equal(0, stats.CloneFactoriesSynthesized);
        Assert.Equal(0, stats.ResidualWithCount);
        AssertCompiles(output);

        var asm = CompileToAssembly(output);
        var pType = asm.GetType("Point")!;
        var p1 = Activator.CreateInstance(pType, 1, 9)!;
        var p2 = asm.GetType("P")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new[] { p1 })!;
        Assert.Equal(2, pType.GetMethod("get_X")!.Invoke(p2, null));
        Assert.Equal(9, pType.GetMethod("get_Y")!.Invoke(p2, null));
        Assert.Equal(1, pType.GetMethod("get_X")!.Invoke(p1, null));
    }

    [Fact]
    public void WR4_PositionalStruct_ParameterlessCtorAndDeconstruct_Run()
    {
        var src = """
            public record struct Point(int X, int Y);
            public static class P
            {
                public static (int, int, int, int) Run()
                {
                    var a = new Point();
                    var b = new Point(3, 4) with { X = 5 };
                    b.Deconstruct(out var bx, out var by);
                    return (a.X, a.Y, bx, by);
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertNoErrors(diags);
        AssertCompiles(output);
        // 干净的位置 struct 场景不得误记任何跳过原因（5220 reshape 闸门、5223 等）。
        Assert.Empty(stats.SkippedByReason);

        var asm = CompileToAssembly(output);
        var tuple = (System.Runtime.CompilerServices.ITuple)asm.GetType("P")!
            .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null)!;
        Assert.Equal(0, tuple[0]);
        Assert.Equal(0, tuple[1]);
        Assert.Equal(5, tuple[2]);
        Assert.Equal(4, tuple[3]);
    }

    [Fact]
    public void WR4_CleanClassSite_NoSkipReason()
    {
        var src = """
            public record class R
            {
                public int X { get; set; }
            }
            public static class P
            {
                public static R Run(R r) => r with { X = 1 };
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertNoErrors(diags);
        AssertCompiles(output);
        Assert.Equal(1, stats.WithSitesClass);
        Assert.Equal(1, stats.CloneFactoriesSynthesized);
        Assert.Equal(0, stats.ResidualWithCount);
        Assert.Empty(stats.SkippedByReason);
    }

    // ---- WR-4：with 位于表达式体成员/表达式 lambda（箭头体 → 块体提升）----

    [Fact]
    public void WR4_ExpressionBodiedMethod_BlockedShape_AndRuns()
    {
        var src = """
            public record class R
            {
                public int X { get; set; }
                public static R Run(R r) => r with { X = 1 };
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertNoErrors(diags);
        AssertCompiles(output);
        // 箭头体方法块化：保留签名与 static，体为克隆序列 + 显式 return。
        AssertContains(output, "public static R Run(R r)");
        AssertContains(output, """
            {
                var __cs2j_wclone0 = r.Cs2jWithClone();
                __cs2j_wclone0.set_X(1);
                return __cs2j_wclone0;
            }
            """);
        AssertNotContains(output, " with ");
        var asm = CompileToAssembly(output);
        var t = asm.GetType("R")!;
        var r = System.Activator.CreateInstance(t)!;
        t.GetMethod("set_X")!.Invoke(r, new object[] { 4 });
        var q = t.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new[] { r })!;
        Assert.Equal(1, t.GetMethod("get_X")!.Invoke(q, null));
        Assert.Equal(4, t.GetMethod("get_X")!.Invoke(r, null));
        Assert.Equal(1, stats.WithSitesClass);
        Assert.Equal(1, stats.CloneFactoriesSynthesized);
        Assert.Equal(0, stats.ResidualWithCount);
    }

    [Fact]
    public void WR4_ExpressionBodiedOperator_Blocked_ReturnClone_Runs()
    {
        var src = """
            public record class R
            {
                public int X { get; set; }
                public static R operator +(R r, int x) => r with { X = x };
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertNoErrors(diags);
        AssertCompiles(output);
        // 运算符阶段把 operator+ 落名为 Add；Records 阶段先把箭头体块化并保留 return clone。
        AssertContains(output, "public static R Add(R r, int x)");
        AssertContains(output, "var __cs2j_wclone0 = r.Cs2jWithClone();");
        AssertContains(output, "__cs2j_wclone0.set_X(x);");
        AssertContains(output, "return __cs2j_wclone0;");
        AssertNotContains(output, " with ");
        var asm = CompileToAssembly(output);
        var t = asm.GetType("R")!;
        var r = System.Activator.CreateInstance(t)!;
        t.GetMethod("set_X")!.Invoke(r, new object[] { 1 });
        var q = t.GetMethod("Add", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new[] { r, (object)7 })!;
        Assert.Equal(7, t.GetMethod("get_X")!.Invoke(q, null));
        Assert.Equal(1, t.GetMethod("get_X")!.Invoke(r, null));
        Assert.Equal(1, stats.WithSitesClass);
        Assert.Equal(0, stats.ResidualWithCount);
        Assert.Empty(stats.SkippedByReason);
    }

    [Fact]
    public void WR4_ExpressionBodiedConversionOperator_Blocked_EvenWhenOperatorStageSkips()
    {
        // 转换运算符箭头体由 Records 阶段块化；转换目标不在运算符命名镜像表时运算符阶段
        // 单独跳计划（既有 ConversionMirrorMiss 闸门，非本修复引入），块体仍合法保留。
        var src = """
            public record class R
            {
                public int X { get; set; }
                public static explicit operator R(int x) => new R() with { X = x };
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled(strict: false));
        AssertCompiles(output);
        Assert.DoesNotContain(diags, d => d.Code == MemberDesugarDiagnosticCode.NonLiftableWithContext);
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.ConversionMirrorMiss);
        AssertContains(output, """
            public static explicit operator R(int x)
            {
            """);
        AssertContains(output, "var __cs2j_wclone1 = __cs2j_wsrc0.Cs2jWithClone();");
        AssertContains(output, "__cs2j_wclone1.set_X(x);");
        AssertContains(output, "return __cs2j_wclone1;");
        AssertNotContains(output, " with ");
        Assert.Equal(0, stats.ResidualWithCount);
    }

    [Fact]
    public void WR4_ExpressionBodiedLocalFunction_Blocked_EmptyWithInlined_Runs()
    {
        var src = """
            using System;
            public record class R
            {
                public int X { get; set; }
                public static R ViaLocal(R r)
                {
                    R Bump(R q) => q with { X = q.X + 5 };
                    R Clone(R q) => q with { };
                    return Clone(Bump(r));
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertNoErrors(diags);
        AssertCompiles(output);
        // 非空 with：本地函数箭头体 → 块体（含 q.X 读点降形为 get_X()）。
        AssertContains(output, "R Bump(R q) {");
        AssertContains(output, "var __cs2j_wclone0 = q.Cs2jWithClone();");
        AssertContains(output, "__cs2j_wclone0.set_X(q.get_X() + 5);");
        AssertContains(output, "return __cs2j_wclone0;");
        // 空 with（class）不进提升闸门：箭头体保留，仅内联工厂调用。
        AssertContains(output, "R Clone(R q) => q.Cs2jWithClone();");
        AssertNotContains(output, " with ");
        var asm = CompileToAssembly(output);
        var t = asm.GetType("R")!;
        var r = System.Activator.CreateInstance(t)!;
        t.GetMethod("set_X")!.Invoke(r, new object[] { 1 });
        var q = t.GetMethod("ViaLocal", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new[] { r })!;
        Assert.Equal(6, t.GetMethod("get_X")!.Invoke(q, null));
        Assert.Equal(1, t.GetMethod("get_X")!.Invoke(r, null));
        Assert.Equal(2, stats.WithSitesClass);
        Assert.Equal(1, stats.WithSitesEmpty);
        Assert.Equal(0, stats.ResidualWithCount);
    }

    [Fact]
    public void WR4_ExpressionLambdas_BecomeStatementLambdas_Runs()
    {
        var src = """
            using System;
            public record class R
            {
                public int X { get; set; }
                public static R Apply(R r, Func<R, R> f) => f(r);
                public static R Simple(R r) => Apply(r, q => q with { X = 9 });
                public static R Paren(R r)
                {
                    Func<R, R> f = (R q) => q with { X = 4 };
                    return f(r);
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertNoErrors(diags);
        AssertCompiles(output);
        // 表达式 lambda → 语句 lambda：=> 保留，体换块；外层成员不被拆语句。
        AssertContains(output, "q => {");
        AssertContains(output, "(R q) => {");
        AssertContains(output, "__cs2j_wclone0.set_X(9);");
        AssertContains(output, "__cs2j_wclone1.set_X(4);");
        AssertNotContains(output, " with ");
        var asm = CompileToAssembly(output);
        var t = asm.GetType("R")!;
        var r = System.Activator.CreateInstance(t)!;
        t.GetMethod("set_X")!.Invoke(r, new object[] { 1 });
        var rs = t.GetMethod("Simple", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new[] { r })!;
        var rp = t.GetMethod("Paren", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new[] { r })!;
        Assert.Equal(9, t.GetMethod("get_X")!.Invoke(rs, null));
        Assert.Equal(4, t.GetMethod("get_X")!.Invoke(rp, null));
        Assert.Equal(1, t.GetMethod("get_X")!.Invoke(r, null));
        Assert.Equal(2, stats.WithSitesClass);
        Assert.Equal(0, stats.ResidualWithCount);
    }

    [Fact]
    public void WR4_PolymorphicWith_InsideExpressionBody_KeepsRuntimeType()
    {
        var src = """
            public record class Base
            {
                public int X { get; set; }
            }
            public record class Derived : Base { }
            public static class P
            {
                public static Base Run(Base b) => b with { X = 5 };
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertNoErrors(diags);
        AssertCompiles(output);
        AssertContains(output, "public virtual Base Cs2jWithClone() => new Base(this);");
        AssertContains(output, "public override Derived Cs2jWithClone() => new Derived(this);");
        AssertNotContains(output, " with ");
        var asm = CompileToAssembly(output);
        var derived = System.Activator.CreateInstance(asm.GetType("Derived")!)!;
        var result = asm.GetType("P")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new[] { derived })!;
        Assert.Equal("Derived", result.GetType().Name);
        Assert.Equal(5, asm.GetType("Base")!.GetMethod("get_X")!.Invoke(result, null));
        Assert.Equal(1, stats.WithSitesClass);
        Assert.Equal(2, stats.CloneFactoriesSynthesized);
        Assert.Equal(0, stats.ResidualWithCount);
    }

    [Fact]
    public void WR4_ExpressionBodiedProperty_AndGetAccessor_ExpandWithBlock_Runs()
    {
        var src = """
            public record class R
            {
                public int X { get; set; }
                public R Self => this with { X = 6 };
                public R Also { get => this with { X = 7 }; }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertNoErrors(diags);
        AssertCompiles(output);
        // 声明级箭头属性与 get 箭头访问器先块化为访问器块，C11 再展开成带块 get_X()。
        AssertContains(output, """
            public R get_Self()
            {
                var __cs2j_wclone0 = this.Cs2jWithClone();
                __cs2j_wclone0.set_X(6);
                return __cs2j_wclone0;
            }
            """);
        AssertContains(output, """
            public R get_Also()
            {
                var __cs2j_wclone1 = this.Cs2jWithClone();
                __cs2j_wclone1.set_X(7);
                return __cs2j_wclone1;
            }
            """);
        AssertNotContains(output, " with ");
        var asm = CompileToAssembly(output);
        var t = asm.GetType("R")!;
        var r = System.Activator.CreateInstance(t)!;
        Assert.Equal(6, t.GetMethod("get_X")!.Invoke(t.GetMethod("get_Self")!.Invoke(r, null), null));
        Assert.Equal(7, t.GetMethod("get_X")!.Invoke(t.GetMethod("get_Also")!.Invoke(r, null), null));
        Assert.Equal(0, t.GetMethod("get_X")!.Invoke(r, null));
        Assert.Equal(2, stats.WithSitesClass);
        Assert.Equal(0, stats.ResidualWithCount);
    }

    [Fact]
    public void WR4_ExpressionBodiedIndexer_GetAccessorBlocked_SetBlockCarried_Runs()
    {
        var src = """
            public record class R
            {
                public int X { get; set; }
                public R[] Items = new R[3];
                public R this[int i]
                {
                    get => Items[i] with { X = 8 };
                    set { Items[i] = value; }
                }
                public void Seed()
                {
                    Items[0] = new R();
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertNoErrors(diags);
        AssertCompiles(output);
        // get 箭头访问器块化（数组元素接收者走 wsrc 单求值）；set 块由 C12 原样携带。
        AssertContains(output, """
            public R get_Item(int i)
            {
                var __cs2j_wsrc0 = Items[i];
                var __cs2j_wclone1 = __cs2j_wsrc0.Cs2jWithClone();
                __cs2j_wclone1.set_X(8);
                return __cs2j_wclone1;
            }
            """);
        AssertContains(output, "public void set_Item(int i, R value)");
        AssertContains(output, "Items[i] = value;");
        AssertNotContains(output, " with ");
        var asm = CompileToAssembly(output);
        var t = asm.GetType("R")!;
        var r = System.Activator.CreateInstance(t)!;
        t.GetMethod("Seed")!.Invoke(r, null);
        var item = t.GetMethod("get_Item")!.Invoke(r, new object[] { 0 })!;
        Assert.Equal(8, t.GetMethod("get_X")!.Invoke(item, null));
        // 克隆是独立对象：后备数组元素保持 X=0，第二次取仍生成新克隆。
        var items = (object[])t.GetField("Items")!.GetValue(r)!;
        Assert.False(ReferenceEquals(item, items[0]));
        Assert.Equal(0, t.GetMethod("get_X")!.Invoke(items[0], null));
        Assert.Equal(1, stats.WithSitesClass);
        Assert.Equal(0, stats.ResidualWithCount);
    }

    [Fact]
    public void WR4_GetArrowAndSetBlockWithSites_BothVisited_Runs()
    {
        // 红节点纪律：同一属性的 get 箭头命中块化后，set 块内的局部声明站点
        // 仍必须被游历到，不能随红属性节点整体漏改。
        var src = """
            public record class R
            {
                public int X { get; set; }
                private R _other;
                public void Seed(R other)
                {
                    _other = other;
                }
                public R Pair
                {
                    get => _other with { X = 8 };
                    set
                    {
                        var q = value with { X = 2 };
                        _other = q;
                    }
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertNoErrors(diags);
        AssertCompiles(output);
        AssertContains(output, "var __cs2j_wclone0 = _other.Cs2jWithClone();");
        AssertContains(output, "__cs2j_wclone0.set_X(8);");
        AssertContains(output, "var __cs2j_wclone1 = value.Cs2jWithClone();");
        AssertContains(output, "__cs2j_wclone1.set_X(2);");
        AssertNotContains(output, " with ");
        var asm = CompileToAssembly(output);
        var t = asm.GetType("R")!;
        var r = System.Activator.CreateInstance(t)!;
        var seed = System.Activator.CreateInstance(t)!;
        var arg = System.Activator.CreateInstance(t)!;
        t.GetMethod("set_X")!.Invoke(arg, new object[] { 1 });
        t.GetMethod("Seed")!.Invoke(r, new[] { seed });
        t.GetMethod("set_Pair")!.Invoke(r, new[] { arg });
        var got = t.GetMethod("get_Pair")!.Invoke(r, null)!;
        Assert.Equal(8, t.GetMethod("get_X")!.Invoke(got, null));
        // get 每次克隆独立对象；改一份不影响另一份（且 _other 本体保持 X=2 不被写 8）。
        var got2 = t.GetMethod("get_Pair")!.Invoke(r, null)!;
        Assert.False(ReferenceEquals(got, got2));
        t.GetMethod("set_X")!.Invoke(got, new object[] { 99 });
        Assert.Equal(8, t.GetMethod("get_X")!.Invoke(
            t.GetMethod("get_Pair")!.Invoke(r, null), null));
        Assert.Equal(2, stats.WithSitesClass);
        Assert.Equal(0, stats.ResidualWithCount);
    }

    [Fact]
    public void WR4_PositionalRecord_ArrowMethod_BlockedAndReshaped_Runs()
    {
        // 箭头体方法提升必须与位置 record reshape 的 ParseReplacement 重解析次序兼容。
        var src = """
            public record Point(int X, int Y)
            {
                public Point Bump() => this with { X = 1 };
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertNoErrors(diags);
        AssertCompiles(output);
        AssertContains(output, """
            public Point Bump()
            {
                var __cs2j_wclone0 = this.Cs2jWithClone();
                __cs2j_wclone0.set_X(1);
                return __cs2j_wclone0;
            }
            """);
        AssertNotContains(output, "record Point(int X, int Y)");
        AssertNotContains(output, " with ");
        var asm = CompileToAssembly(output);
        var t = asm.GetType("Point")!;
        var p = t.GetConstructor(new[] { typeof(int), typeof(int) })!.Invoke(new object[] { 0, 2 });
        var q = t.GetMethod("Bump")!.Invoke(p, null)!;
        Assert.Equal(1, t.GetMethod("get_X")!.Invoke(q, null));
        Assert.Equal(2, t.GetMethod("get_Y")!.Invoke(q, null));
        Assert.Equal(0, t.GetMethod("get_X")!.Invoke(p, null));
        Assert.Equal(1, stats.WithSitesClass);
        Assert.Equal(0, stats.ResidualWithCount);
    }

    [Fact]
    public void WR4_Gate5223_FieldAndPropertyInitializer_AtomicPlanSkip_Untouched()
    {
        var src = """
            public record class R
            {
                public int X { get; set; }
                public static R Template = new R();
                public int Z = (Template with { X = 1 }).X;
                public R Prototype { get; } = new R() with { X = 1 };
                public static R Run(R r) => r with { X = 2 };
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled(strict: false));
        AssertCompiles(output);
        // 整 plan 闸门：三个站点（字段初始化器/属性初始化器/本可提升的箭头方法）各报一条
        // 5223 且全部原样不动；工厂按 FactoryRequired 独立仍合成（与 GateAtomicity 既定语义一致）。
        Assert.Equal(3, diags.Count(d => d.Code == MemberDesugarDiagnosticCode.NonLiftableWithContext));
        AssertContains(output, "public static R Run(R r) => r with { X = 2 };");
        AssertContains(output, "public int Z = (Template with { X = 1 }).X;");
        AssertContains(output, "= new R() with { X = 1 };");
        AssertContains(output, "Cs2jWithClone");
        Assert.Equal(3, stats.SkippedByReason[MemberDesugarDiagnosticCode.NonLiftableWithContext]);
        Assert.Equal(1, stats.CloneFactoriesSynthesized);
        Assert.Equal(0, stats.WithSitesTotal);
    }

    [Fact]
    public void WR4_Gate5223_QueryClause_Untouched()
    {
        var src = """
            using System.Linq;
            public record class R
            {
                public int X { get; set; }
            }
            public static class P
            {
                public static R[] Run(R[] rs)
                {
                    return (from r in rs select r with { X = 1 }).ToArray();
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled(strict: false));
        AssertCompiles(output);
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.NonLiftableWithContext);
        AssertContains(output, "select r with { X = 1 }");
        AssertNotContains(output, "Cs2jWithClone");
        Assert.Equal(0, stats.CloneFactoriesSynthesized);
    }

    [Fact]
    public void WR4_Gate5223_AttributeArgument_Untouched()
    {
        // 特性实参本身要求常量表达式（CS0182，输入即非法）；本用例只验证预处理在该位
        // 给 5223 且不改动声明/站点，不做产物编译断言。
        var src = """
            using System;
            public record R
            {
                public int X { get; set; }
            }
            [AttributeUsage(AttributeTargets.All)]
            public class AAttribute : Attribute
            {
                public AAttribute(R r) { }
            }
            [A((R)null! with { X = 1 })]
            public class C { }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled(strict: false));
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.NonLiftableWithContext);
        AssertContains(output, "[A((R)null! with { X = 1 })]");
        AssertNotContains(output, "Cs2jWithClone");
        Assert.Equal(0, stats.CloneFactoriesSynthesized);
        Assert.Equal(0, stats.ResidualWithCount);
    }

    [Fact]
    public void WR4_ArrowLift_SecondRun_IsZeroDiff()
    {
        var src = """
            using System;
            public record class R
            {
                public int X { get; set; }
                public static R Run(R r) => r with { X = 1 };
                public static R Via(R r) => Apply(r, q => q with { X = 2 });
                public static R Apply(R r, Func<R, R> f) => f(r);
            }
            """;
        var (first, _, diags1) = Rewrite(src, Enabled());
        AssertCompiles(first);
        AssertNoErrors(diags1);
        var (second, stats2, diags2) = Rewrite(first, Enabled());
        AssertCompiles(second);
        Assert.Equal(Normalize(first), Normalize(second));
        Assert.Equal(0, stats2.CloneFactoriesSynthesized);
        Assert.Equal(0, stats2.WithSitesTotal);
        AssertNoErrors(diags2);
    }

    [Fact]
    public void WR4_Disabled_ArrowSite_ZeroPlanZeroDiff()
    {
        var src = """
            public record class R
            {
                public int X { get; set; }
                public static R Run(R r) => r with { X = 1 };
            }
            """;
        var (output, stats, diags) = Rewrite(src, new MemberDesugarOptions());
        Assert.Equal(Normalize(src), Normalize(output));
        Assert.Equal(0, stats.CloneFactoriesSynthesized);
        Assert.Equal(0, stats.WithSitesTotal);
        Assert.Equal(0, stats.RecordPlans);
        Assert.Equal(0, stats.ResidualWithCount);
        Assert.All(diags, d => Assert.NotEqual(MemberDesugarDiagnosticCode.ResidualWith, d.Code));
    }

    [Fact]
    public void M2_PositionalStruct_Reshape_FinalShapeAloneCompilesAndRuns()
    {
        // 不带 with 的纯验证：手工取终形片段编译，确认 struct 终形成员自身合法
        // （主 ctor 赋值、无参 ctor、get/set、Deconstruct）。
        var src = """
            public record struct Point(int X, int Y);
            public static class P
            {
                public static Point Run(Point p) { return p with { X = 2 }; }
            }
            """;
        var (output, _, _) = Rewrite(src, Enabled());
        var finalShape = output[..output.IndexOf("public static class P", StringComparison.Ordinal)];
        var probe = finalShape + """
            public static class Probe
            {
                public static int Go()
                {
                    var p = new Point(1, 2);
                    var d = new Point();
                    d.set_Y(5);
                    p.Deconstruct(out var x, out var y);
                    return p.get_X() + y + d.get_Y();
                }
            }
            """;
        var asm = CompileToAssembly(probe);
        var result = asm.GetType("Probe")!
            .GetMethod("Go", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null);
        Assert.Equal(1 + 2 + 5, result);
    }

    [Fact]
    public void M2_PositionalStruct_Gate_ReadOnly_5220()
    {
        var src = """
            public readonly record struct S(int X);
            public static class P
            {
                public static S Run(S s) { return s with { X = 1 }; }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled(strict: false));
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.PositionalReshapeGate);
        AssertContains(output, "readonly record struct S(int X)");
        AssertNotContains(output, "private int x;");
        Assert.Equal(0, stats.PositionalRecordsReshaped);
        Assert.Equal(1, stats.SkippedByReason[MemberDesugarDiagnosticCode.PositionalReshapeGate]);
        AssertCompiles(output);
    }

    [Fact]
    public void M2_PositionalStruct_Gate_ParameterAttributes_5220()
    {
        var src = """
            public record struct S(int X, [System.Runtime.CompilerServices.CallerMemberName] string Y = null);
            public static class P
            {
                public static S Run(S s) { return s with { X = 1 }; }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled(strict: false));
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.PositionalReshapeGate);
        AssertContains(output, "CallerMemberName] string Y = null");
        Assert.Equal(0, stats.PositionalRecordsReshaped);
        AssertCompiles(output);
    }

    [Fact]
    public void M2_PositionalStruct_Gate_UserDeconstruct_5220()
    {
        var src = """
            public record struct Point(int X)
            {
                public void Deconstruct(out int x) { x = X; }
            }
            public static class P
            {
                public static Point Run(Point p) { return p with { X = 2 }; }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled(strict: false));
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.PositionalReshapeGate);
        Assert.Equal(0, stats.PositionalRecordsReshaped);
        AssertCompiles(output);
    }

    [Fact]
    public void M2_PositionalStruct_Gate_FieldInitializerCapturesParameter_5220()
    {
        var src = """
            public record struct S(int X)
            {
                private int _z = X;
            }
            public static class Q
            {
                public static S Run(S s) { return s with { X = 2 }; }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled(strict: false));
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.PositionalReshapeGate);
        Assert.Equal(0, stats.PositionalRecordsReshaped);
        AssertCompiles(output);
    }

    [Fact]
    public void M2_PositionalStruct_Gate_MultipleParameterListParts_5220()
    {
        // 输入本身非法（多个部件携带参数表）；预处理器仍原子不动并发 5220。
        var src = """
            public partial record struct S(int X);
            public partial record struct S(int Y);
            public static class Q
            {
                public static S Run(S s) { return s with { X = 2 }; }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled(strict: false));
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.PositionalReshapeGate);
        AssertContains(output, "record struct S(int X)");
        Assert.Equal(0, stats.PositionalRecordsReshaped);
    }

    // ---- RC-5：record 面 required / [SetsRequiredMembers] 剥离 ----

    [Fact]
    public void RC5_RequiredField_Stripped_And5215Emitted()
    {
        var src = """
            public record R
            {
                public required int F;
                public int X { get; set; }
            }
            public static class P
            {
                public static R Run(R r) => r with { X = 1 };
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled(info: true));
        AssertCompiles(output);
        AssertNoErrors(diags);
        AssertNotContains(output, "required int F");
        AssertContains(output, "public int F;");
        Assert.Equal(1, stats.RequiredConstraintsDropped);
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.RequiredRecordTouched);
    }

    [Fact]
    public void RC5_SetsRequiredMembers_OnTypeAndCtor_Removed()
    {
        var src = """
            [System.Runtime.CompilerServices.SetsRequiredMembers]
            public record R
            {
                public required int F;
                [System.Runtime.CompilerServices.SetsRequiredMembers]
                public R(int f) { F = f; }
                public int X { get; set; }
            }
            public static class P
            {
                public static R Run(R r) => r with { X = 1 };
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled(info: true));
        AssertCompiles(output);
        AssertNoErrors(diags);
        AssertNotContains(output, "SetsRequiredMembers");
        AssertNotContains(output, "required int F");
        // 1 required field + 2 SetsRequiredMembers (type + ctor) = 3
        Assert.Equal(3, stats.RequiredConstraintsDropped);
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.RequiredRecordTouched);
    }

    [Fact]
    public void RC5_NoRequiredConstraints_NoChange_No5215()
    {
        var src = """
            public record R
            {
                public int F;
                public int X { get; set; }
            }
            public static class P
            {
                public static R Run(R r) => r with { X = 1 };
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled(info: true));
        AssertCompiles(output);
        Assert.Equal(0, stats.RequiredConstraintsDropped);
        Assert.DoesNotContain(diags, d => d.Code == MemberDesugarDiagnosticCode.RequiredRecordTouched);
    }

    [Fact]
    public void RC5_PositionalRecord_RequiredExplicitField_Stripped()
    {
        // 位置 record 的显式 required 字段（非位置参数）应在 reshape 后剥离。
        var src = """
            public record R(int X)
            {
                public required int F;
            }
            public static class P
            {
                public static R Run(R r) => r with { X = 1 };
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled(info: true));
        AssertCompiles(output);
        AssertNoErrors(diags);
        AssertNotContains(output, "required");
        Assert.Equal(1, stats.PositionalRecordsReshaped);
        Assert.Equal(1, stats.RequiredConstraintsDropped);
    }

    // ---- 无体 record 工厂收口 ----

    [Fact]
    public void NoBody_RecordClass_FactoryInjected()
    {
        var src = """
            public record R;
            public static class P
            {
                public static R Run(R r) => r with { };
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertNoErrors(diags);
        AssertContains(output, "public virtual R Cs2jWithClone() => new R(this);");
        AssertContains(output, "r.Cs2jWithClone()");
        AssertNotContains(output, " with ");
        Assert.Equal(1, stats.CloneFactoriesSynthesized);
    }

    [Fact]
    public void NoBody_SealedRecord_SealedFactory()
    {
        var src = """
            public sealed record R;
            public static class P
            {
                public static R Run(R r) => r with { };
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertNoErrors(diags);
        AssertContains(output, "public R Cs2jWithClone() => new R(this);");
        AssertNotContains(output, "virtual");
        AssertNotContains(output, "override");
        Assert.Equal(1, stats.CloneFactoriesSynthesized);
    }

    [Fact]
    public void NoBody_AbstractRecord_AbstractFactory()
    {
        var src = """
            public abstract record R;
            public sealed record D : R;
            public static class P
            {
                public static R Run(R r) => r with { };
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertNoErrors(diags);
        AssertContains(output, "public abstract R Cs2jWithClone();");
        AssertContains(output, "public override D Cs2jWithClone() => new D(this);");
        Assert.Equal(2, stats.CloneFactoriesSynthesized);
    }

    [Fact]
    public void NoBody_DerivedRecord_OverrideFactory()
    {
        var src = """
            public record Base;
            public record Derived : Base;
            public static class P
            {
                public static Base Run(Base b) => b with { };
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertNoErrors(diags);
        AssertContains(output, "public virtual Base Cs2jWithClone() => new Base(this);");
        AssertContains(output, "public override Derived Cs2jWithClone() => new Derived(this);");
        Assert.Equal(2, stats.CloneFactoriesSynthesized);
    }

    [Fact]
    public void NoBody_PolymorphicWith_RuntimeTypePreserved()
    {
        var src = """
            public record Base;
            public record Derived : Base;
            public static class P
            {
                public static Base Run()
                {
                    Base b = new Derived();
                    return b with { };
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled());
        AssertCompiles(output);
        AssertNoErrors(diags);
        AssertContains(output, "Cs2jWithClone()");
        // 虚工厂保证运行时类型为 Derived
        var asm = CompileToAssembly(output);
        var pType = asm.GetType("P")!;
        var result = pType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null);
        Assert.Equal("Derived", result!.GetType().Name);
    }

    // ---- 5290 单文件模式 + 5221 force ----

    [Fact]
    public void Gate5290_SingleFile_NonSealedRecord_WarningEmitted_PlanProceeds()
    {
        // 单文件编译（Rewrite 默认单树）：非密封 record emit 5290 Warning（降级），
        // 但 plan 仍执行——虚工厂对编译内派生正确（同 finalizer 5211 降级模式）。
        var src = """
            public record R
            {
                public int X { get; set; }
            }
            public static class P
            {
                public static R Run(R r) => r with { X = 1 };
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled(strict: false));
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.RecordSingleFileNotSelfContained);
        // plan 仍执行：工厂注入 + with 改写。
        AssertContains(output, "Cs2jWithClone");
        AssertNotContains(output, " with ");
        Assert.Equal(1, stats.CloneFactoriesSynthesized);
        Assert.Equal(1, stats.WithSitesTotal);
    }

    [Fact]
    public void Gate5290_SingleFile_SealedRecord_NoWarning()
    {
        // 密封 record 无派生洞，5290 不触发。
        var src = """
            public sealed record R
            {
                public int X { get; set; }
            }
            public static class P
            {
                public static R Run(R r) => r with { X = 1 };
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled(strict: false));
        Assert.DoesNotContain(diags, d => d.Code == MemberDesugarDiagnosticCode.RecordSingleFileNotSelfContained);
        AssertContains(output, "Cs2jWithClone");
        AssertNotContains(output, " with ");
        Assert.Equal(1, stats.CloneFactoriesSynthesized);
    }

    [Fact]
    public void Gate5221_Force_DoesNotBreakNormalRecords()
    {
        // force 开启：普通 record（无元数据派生）正常处理，5221 不触发。
        var src = """
            public record R
            {
                public int X { get; set; }
            }
            public static class P
            {
                public static R Run(R r) => r with { X = 1 };
            }
            """;
        var options = new MemberDesugarOptions
        {
            Strict = false,
            ReportInfo = true,
            Records = new RecordOptions { Enabled = true, AllowMetadataDerivedCloneFallback = true },
        };
        var (output, stats, diags) = Rewrite(src, options);
        AssertCompiles(output);
        AssertNoErrors(diags);
        AssertContains(output, "Cs2jWithClone");
        AssertNotContains(output, " with ");
        Assert.Equal(1, stats.CloneFactoriesSynthesized);
        Assert.Equal(1, stats.WithSitesTotal);
    }

    /// <summary>编译去糖产物并内存加载（TPA 引用），供反射行为断言。</summary>
    private static Assembly CompileToAssembly(string output)
    {
        var tree = CSharpSyntaxTree.ParseText(
            output, new CSharpParseOptions(LanguageVersion.Preview));
        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "md-record-" + System.Guid.NewGuid().ToString("N"),
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success,
            "Rewritten code failed to emit:\n" +
            string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return Assembly.Load(ms.ToArray());
    }
}
