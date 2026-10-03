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
    public void Gate5224_NestedWith_OuterAndInnerUntouched()
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
        var (output, stats, diags) = Rewrite(src, Enabled(strict: false));
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.UnsupportedNestedInitializer);
        Assert.Equal(2, diags.Count(d => d.Code == MemberDesugarDiagnosticCode.UnsupportedNestedInitializer));
        Assert.Contains("with", output);
        AssertNotContains(output, "Cs2jWithClone");
        Assert.Equal(0, stats.CloneFactoriesSynthesized);
        Assert.Equal(0, stats.ResidualWithCount);
    }

    [Fact]
    public void Gate5220_RecordStructAndPositionalRecord_Untouched()
    {
        var src = """
            public record struct S
            {
                public int X { get; set; }
            }
            public record Point(int X, int Y);
            public static class P
            {
                public static S RunS(S s)
                {
                    var t = s with { X = 1 };
                    return t;
                }
                public static Point RunP(Point p)
                {
                    var q = p with { X = 2 };
                    return q;
                }
            }
            """;
        var (output, stats, diags) = Rewrite(src, Enabled(strict: false));
        Assert.Contains(diags, d => d.Code == MemberDesugarDiagnosticCode.PositionalReshapeGate);
        Assert.Equal(2, stats.SkippedByReason[MemberDesugarDiagnosticCode.PositionalReshapeGate]);
        Assert.Contains("with", output);
        AssertNotContains(output, "Cs2jWithClone");
        Assert.Equal(0, stats.CloneFactoriesSynthesized);
        Assert.Equal(0, stats.ResidualWithCount);
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
