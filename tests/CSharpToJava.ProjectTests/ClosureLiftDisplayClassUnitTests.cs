using CSharpToJava.Core.ClosureLift;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// The in-process ClosureLift test classes share process-global planner
/// test hooks (<see cref="DisplayClassPlanner.ForcedUnaccountedForTests"/>);
/// xUnit parallelizes test classes by default, so every class that runs the
/// preprocessor in-process belongs to this non-parallel collection.
/// CLI tests spawn subprocesses and stay outside it.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public static class ClosureLiftTestCollection
{
    public const string Name = "closure-lift-inproc";
}

/// <summary>
/// C45 M1 unit tests (design §7.1 cases 1, 2, 11 and 17): the zero-capture
/// static path (D10) lifts statement lambdas to host-level `private static`
/// named methods (D9/D12) and rewrites every planned site to an explicit
/// delegate creation (D8) — no display class, no instance, byte-stable
/// across runs and idempotent on the intermediate text. Mirrors the inline
/// source → CSharpCompilation → Preprocess → text-assertion style of
/// ClosureLiftUnitTests.
/// </summary>
[Collection(ClosureLiftTestCollection.Name)]
public sealed class ClosureLiftDisplayClassUnitTests
{
    // ── §7.1-1: M1 zero-capture static lift ──

    [Fact]
    public void M1_ExpressionBody_Action_ZeroParam_LiftsToHostStaticMethod()
    {
        var src = @"
class C {
    void M() {
        System.Action a = () => System.Console.WriteLine(1);
        a();
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Contains("System.Action a = new global::System.Action(Cs2jLambda_M_0);", r.OutputCode);
        Assert.Contains("private static void Cs2jLambda_M_0() => System.Console.WriteLine(1);", r.OutputCode);
        Assert.Equal(1, r.Statistics.StaticLambdaMethods);
        Assert.Equal(1, r.Statistics.LambdaMethodsLifted);
        Assert.Equal(1, r.Statistics.DelegateSitesRewritten);
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void M1_BlockBody_Func_MultiParam_KeepsBodyVerbatim()
    {
        var src = @"
class C {
    int M() {
        System.Func<int, int, int> f = (x, y) => {
            var t = x + y;
            return t * 2;
        };
        return f(2, 3);
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Contains("System.Func<int, int, int> f = new global::System.Func<int, int, int>(Cs2jLambda_M_0);", r.OutputCode);
        Assert.Contains("private static int Cs2jLambda_M_0(int x, int y)", r.OutputCode);
        Assert.Contains("var t = x + y;", r.OutputCode);
        Assert.Contains("return t * 2;", r.OutputCode);
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void M1_NoDisplayClass_NoInstance_NoCaptureStats()
    {
        var src = @"
class C {
    void M() {
        System.Func<int> f = () => 42;
        System.Console.WriteLine(f());
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Empty(HolderClassNames(r.OutputCode));
        Assert.DoesNotContain("__cs2jClosure", r.OutputCode);
        Assert.Equal(0, r.Statistics.CapturesLifted);
        Assert.Equal(0, r.Statistics.HolderClassesGenerated);
    }

    [Fact]
    public void M1_TwoLambdasSameMember_KSequenceBySpanStart()
    {
        var src = @"
class C {
    void M() {
        System.Func<int> a = () => 1;
        System.Func<int> b = () => 2;
        System.Console.WriteLine(a() + b());
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Contains("new global::System.Func<int>(Cs2jLambda_M_0)", r.OutputCode);
        Assert.Contains("new global::System.Func<int>(Cs2jLambda_M_1)", r.OutputCode);
        Assert.Contains("private static int Cs2jLambda_M_0() => 1;", r.OutputCode);
        Assert.Contains("private static int Cs2jLambda_M_1() => 2;", r.OutputCode);
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void M1_Overloads_ShareMemberNameSequence()
    {
        var src = @"
class C {
    void A() {
        System.Func<int> f = () => 1;
        System.Console.WriteLine(f());
    }

    void A(int n) {
        System.Func<int> g = () => 2;
        System.Console.WriteLine(g() + n);
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Contains("Cs2jLambda_A_0", r.OutputCode);
        Assert.Contains("Cs2jLambda_A_1", r.OutputCode);
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void M1_ByteIdenticalAcrossRuns()
    {
        var src = @"
class C {
    void M() {
        System.Func<int> f = () => 42;
        System.Console.WriteLine(f());
    }
}";
        var r1 = Transform(src);
        var r2 = Transform(src);

        Assert.True(r1.Changed);
        Assert.Equal(r1.OutputCode, r2.OutputCode);
    }

    [Fact]
    public void M1_NestedLambda_InnerSiteRewrittenInsideMethodBody()
    {
        var src = @"
class C {
    void M() {
        System.Action a = () => {
            System.Action b = () => System.Console.WriteLine(1);
            b();
        };
        a();
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Contains("System.Action a = new global::System.Action(Cs2jLambda_M_0);", r.OutputCode);
        Assert.Contains("System.Action b = new global::System.Action(Cs2jLambda_M_1);", r.OutputCode);
        Assert.Contains("private static void Cs2jLambda_M_1() => System.Console.WriteLine(1);", r.OutputCode);
        // The outer method body hosts the inner site; no lambda node survives.
        Assert.Empty(LambdaNodes(r.OutputCode));
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void M1_MixedFile_HolderLiftAndStaticLiftCoexist()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Action a = () => { x++; };
        System.Func<int> b = () => 42;
        a();
        System.Console.WriteLine(x + b());
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        // C32/M2 part: mutable capture is the evolved display class; the
        // capturing lambda becomes its Lambda_0 instance method (D2/D9).
        Assert.Contains("private sealed class Cs2jDisplayClass_M_0", r.OutputCode);
        Assert.Contains("new global::System.Action(__cs2jClosure0.Lambda_0)", r.OutputCode);
        Assert.Contains("internal void Lambda_0()", r.OutputCode);
        Assert.Contains("_cs2jCap_x++;", r.OutputCode);
        // M1 part: the zero-capture lambda in the SAME file lifts to a host
        // static method and its site is rewritten even though C32 inserted
        // statements before it (dual-span anchor; §4.1 plan bag sharing).
        Assert.Contains("new global::System.Func<int>(Cs2jLambda_M_0)", r.OutputCode);
        Assert.Contains("private static int Cs2jLambda_M_0()", r.OutputCode);
        Assert.Equal(1, r.Statistics.StaticLambdaMethods);
        Assert.Equal(1, r.Statistics.LambdaMethodsLifted - r.Statistics.StaticLambdaMethods);
        Assert.Empty(LambdaNodes(r.OutputCode));
        Assert.Equal(0, r.Statistics.FilesSkippedClean);
        Assert.Equal(1, r.Statistics.FilesTransformed);
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void M1_AsyncLambda_AsyncModifierAndTaskReturnPreserved()
    {
        var src = @"
class C {
    async System.Threading.Tasks.Task M() {
        System.Func<System.Threading.Tasks.Task> f = async () => {
            await System.Threading.Tasks.Task.Yield();
        };
        await f();
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Contains("private static async global::System.Threading.Tasks.Task Cs2jLambda_M_0()", r.OutputCode);
        Assert.Contains("await System.Threading.Tasks.Task.Yield();", r.OutputCode);
        Assert.Contains("new global::System.Func<global::System.Threading.Tasks.Task>(Cs2jLambda_M_0)", r.OutputCode);
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void M2_ThisCapture_LiftsToThisBackedDisplayClass()
    {
        var src = @"
class C {
    private int _n;
    void M() {
        System.Func<int> f = () => _n + 1;
        System.Func<int> g = () => this._n + 1;
        System.Console.WriteLine(f() + g());
    }
}";
        var r = Transform(src);

        // D7 (§7.1-3): this-only capture now lifts — one fresh display class
        // carrying `_cs2jThis`; both lambdas become Lambda_0/Lambda_1 on the
        // same instance whether the body uses implicit or explicit `this`.
        Assert.True(r.Changed);
        Assert.Single(HolderClassNames(r.OutputCode));
        Assert.Contains("var __cs2jClosure0 = new Cs2jDisplayClass_M_0(this);", r.OutputCode);
        Assert.Contains("private readonly C _cs2jThis;", r.OutputCode);
        Assert.Contains("public Cs2jDisplayClass_M_0(C @this)", r.OutputCode);
        Assert.Contains("new global::System.Func<int>(__cs2jClosure0.Lambda_0)", r.OutputCode);
        Assert.Contains("new global::System.Func<int>(__cs2jClosure0.Lambda_1)", r.OutputCode);
        Assert.Contains("internal int Lambda_0() => _cs2jThis._n + 1;", r.OutputCode);
        Assert.Contains("internal int Lambda_1() => _cs2jThis._n + 1;", r.OutputCode);
        Assert.Equal(2, r.Statistics.ThisOnlyLambdas);
        Assert.Empty(LambdaNodes(r.OutputCode));
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void M2_StaticMemberLambda_NoThisField_StaysStaticPath()
    {
        var src = @"
class C {
    static int S() => 1;
    static void M() {
        System.Func<int> f = () => S() + 1;
        System.Console.WriteLine(f());
    }
}";
        // Note: a zero-capture static-context lambda is an M1 case; a lambda
        // referencing a STATIC member captures neither this nor state, so it
        // must NOT gain a _cs2jThis field (D7 tail).
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.DoesNotContain("_cs2jThis", r.OutputCode);
        Assert.Empty(HolderClassNames(r.OutputCode));
        Assert.Contains("new global::System.Func<int>(Cs2jLambda_M_0)", r.OutputCode);
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void M1_LocalFunctionReference_Skipped()
    {
        var src = @"
class C {
    void M() {
        int Local() => 1;
        System.Func<int> f = () => Local();
        System.Console.WriteLine(f());
    }
}";
        var r = Transform(src);

        Assert.False(r.Changed);
        Assert.Equal(src, r.OutputCode);
    }

    [Fact]
    public void M1_MethodTypeParameterReference_Skipped()
    {
        var src = @"
class C {
    T Pick<T>() {
        System.Func<T> f = () => default(T);
        return f();
    }
}";
        var r = Transform(src);

        Assert.False(r.Changed);
        Assert.Equal(src, r.OutputCode);
    }

    [Fact]
    public void M1_GenericMethodHost_UnusedTypeParameter_Lifts()
    {
        var src = @"
class C {
    T Echo<T>(T value) {
        System.Func<int> f = () => 42;
        System.Console.WriteLine(f());
        return value;
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Contains("private static int Cs2jLambda_Echo_0() => 42;", r.OutputCode);
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void M1_ExpressionBodiedAndAccessorHosts_LiftWithMemberAnchor()
    {
        var expressionBodied = @"
class C {
    int M() => Run(() => 42) + Run(() => 7);

    static int Run(System.Func<int> f) => f();
}";
        var r = Transform(expressionBodied);
        Assert.True(r.Changed);
        Assert.Contains("Cs2jLambda_M_0", r.OutputCode);
        Assert.Contains("Cs2jLambda_M_1", r.OutputCode);
        AssertOutputCompiles(r.OutputCode);

        var accessor = @"
class C {
    private int P {
        get => Run(() => 42);
    }

    private static int Run(System.Func<int> f) => f();
}";
        var ra = Transform(accessor);
        Assert.True(ra.Changed);
        Assert.Contains("Cs2jLambda_P_get_0", ra.OutputCode);
        AssertOutputCompiles(ra.OutputCode);
    }

    [Fact]
    public void M1_FieldInitializerStaticLambda_CS2J4504_WholeFileVerbatim()
    {
        var src = @"
class C {
    private static readonly System.Func<int, int> Sq = static x => x * x;

    static int M(int v) {
        System.Func<int, int> add = static y => y + 1;
        return add(Sq(v));
    }
}";
        var r = Transform(src);

        // D18③ (M3): the initializer-hosted static lambda now trips CS2J4504;
        // strict Error rolls the WHOLE file back — the method-body lambda that
        // previously lifted alongside it stays in place too (no half lift).
        Assert.False(r.Changed);
        Assert.Equal(src, r.OutputCode);
        Assert.Contains(r.Diagnostics, d =>
            d.Code == "CS2J4504" && d.Severity == ClosureLiftSeverity.Error);
        Assert.DoesNotContain("Cs2jLambda_M_0", r.OutputCode);
    }

    [Fact]
    public void M1_AuditOnly_Unchanged_NoRows()
    {
        var src = @"
class C {
    void M() {
        System.Func<int> f = () => 42;
        System.Console.WriteLine(f());
    }
}";
        var r = Transform(src, new ClosureLiftOptions(AuditOnly: true));

        Assert.False(r.Changed);
        Assert.Equal(src, r.OutputCode);
        Assert.DoesNotContain(r.Diagnostics, d => d.Severity == ClosureLiftSeverity.Error);
        Assert.Equal(0, r.Statistics.StaticLambdaMethods);
    }

    // ── §7.1-2: static keyword + expression trees ──

    [Fact]
    public void M1_StaticKeyword_SameProductAsZeroCapture()
    {
        var src = @"
class C {
    void M() {
        System.Func<int, int> f = static x => x * x;
        System.Console.WriteLine(f(3));
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Contains("System.Func<int, int> f = new global::System.Func<int, int>(Cs2jLambda_M_0);", r.OutputCode);
        Assert.Contains("private static int Cs2jLambda_M_0(int x) => x * x;", r.OutputCode);
        // The static modifier lives on the synthesized method; the site is the
        // same explicit delegate creation as the analyzed zero-capture path.
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void M1_ExpressionTreeLambda_Unchanged_StatementLambdaLifted()
    {
        var src = @"
using System.Linq.Expressions;
class C {
    void M() {
        Expression<System.Func<int, int>> e = x => x + 1;
        System.Func<int, int> f = x => x + 2;
        System.Console.WriteLine(f(1) + e.Compile()(1));
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        // D18: the quoted subtree stays byte-identical, including its lambda.
        Assert.Contains("Expression<System.Func<int, int>> e = x => x + 1;", r.OutputCode);
        Assert.Contains("new global::System.Func<int, int>(Cs2jLambda_M_0)", r.OutputCode);
        Assert.Equal(1, LambdaNodes(r.OutputCode).Count);
        AssertOutputCompiles(r.OutputCode);
    }

    // ── §7.1-11: delegate type rendering ──

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void M1_ActionRendering_Arity0To4(int arity)
    {
        var typeArgs = string.Concat(Enumerable.Range(0, arity).Select(_ => ", int"));
        var paramNames = string.Join(", ", Enumerable.Range(0, arity).Select(i => $"p{i}"));
        var sum = arity == 0 ? "1" : string.Join(" + ", Enumerable.Range(0, arity).Select(i => $"p{i}"));
        var src = $@"
class C {{
    void M() {{
        System.Action{(arity == 0 ? "" : $"<{typeArgs.TrimStart(',', ' ')}>")} a = ({paramNames}) => System.Console.WriteLine({sum});
        a({string.Join(", ", Enumerable.Range(0, arity).Select(i => i.ToString()))});
    }}
}}";
        var r = Transform(src);

        Assert.True(r.Changed);
        var delegateType = arity == 0
            ? "global::System.Action"
            : $"global::System.Action<{typeArgs.TrimStart(',', ' ')}>";
        Assert.Contains($"new {delegateType}(Cs2jLambda_M_0)", r.OutputCode);
        var parameterList = arity == 0 ? "" : string.Join(", ", Enumerable.Range(0, arity).Select(i => $"int p{i}"));
        Assert.Contains($"private static void Cs2jLambda_M_0({parameterList})", r.OutputCode);
        AssertOutputCompiles(r.OutputCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void M1_FuncRendering_Arity0To4(int arity)
    {
        var typeArgs = arity == 0
            ? "string"
            : string.Join(", ", Enumerable.Range(0, arity).Select(_ => "int").Append("string"));
        var paramNames = string.Join(", ", Enumerable.Range(0, arity).Select(i => $"p{i}"));
        var body = arity == 0 ? "\"x\"" : "\"v\" + " + string.Join(" + ", Enumerable.Range(0, arity).Select(i => $"p{i}"));
        var src = $@"
class C {{
    void M() {{
        System.Func<{typeArgs}> f = ({paramNames}) => {body};
        System.Console.WriteLine(f({string.Join(", ", Enumerable.Range(0, arity).Select(i => i.ToString()))}));
    }}
}}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Contains($"new global::System.Func<{typeArgs}>(Cs2jLambda_M_0)", r.OutputCode);
        var parameterList = arity == 0 ? "" : string.Join(", ", Enumerable.Range(0, arity).Select(i => $"int p{i}"));
        Assert.Contains($"private static string Cs2jLambda_M_0({parameterList})", r.OutputCode);
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void M1_CustomDelegate_UsesSourceName_GenericClosedArguments()
    {
        var src = @"
namespace Fixtures {
    delegate int MyDel(int value);
    delegate R GenericDel<T, R>(T input);

    class C {
        int M() {
            MyDel d = v => v * 3;
            GenericDel<int, string> g = i => ""v"" + i;
            return d(2) + g(1).Length;
        }
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Contains("new global::Fixtures.MyDel(Cs2jLambda_M_0)", r.OutputCode);
        Assert.Contains("new global::Fixtures.GenericDel<int, string>(Cs2jLambda_M_1)", r.OutputCode);
        Assert.Contains("private static int Cs2jLambda_M_0(int v) => v * 3;", r.OutputCode);
        Assert.Contains("private static string Cs2jLambda_M_1(int i) => \"v\" + i;", r.OutputCode);
        AssertOutputCompiles(r.OutputCode);
    }

    // ── §7.1-17: fixpoint / idempotence / diagnostics ──

    [Fact]
    public void M1_Idempotent_SecondRunUnchangedByteIdentical()
    {
        var src = @"
class C {
    void M() {
        System.Func<int> f = () => 42;
        System.Console.WriteLine(f());
    }
}";
        var r1 = Transform(src);
        Assert.True(r1.Changed);

        // Fresh preprocessor + freshly parsed semantic model: reentry relies
        // on the reserved name prefixes, exactly like the project pipeline.
        var r2 = new ClosureLiftPreprocessor().Preprocess(
            r1.OutputCode, ParseWithSemantics(r1.OutputCode), new ClosureLiftOptions());

        Assert.False(r2.Changed);
        Assert.Equal(r1.OutputCode, r2.OutputCode);
        Assert.Equal(0, r2.Statistics.StaticLambdaMethods);
        Assert.Equal(0, r2.Statistics.DelegateSitesRewritten);
    }

    [Fact]
    public void M1_SamePreprocessorRepeatedRuns_DeterministicNames()
    {
        var src = @"
class C {
    void M() {
        System.Func<int> f = () => 42;
        System.Console.WriteLine(f());
    }
}";
        var pp = new ClosureLiftPreprocessor();
        var r1 = pp.Preprocess(src, ParseWithSemantics(src), new ClosureLiftOptions());
        var r2 = pp.Preprocess(src, ParseWithSemantics(src), new ClosureLiftOptions());

        Assert.True(r1.Changed);
        Assert.True(r2.Changed);
        Assert.Equal(r1.OutputCode, r2.OutputCode);
    }

    [Fact]
    public void M1_UnresolvableConvertedType_CS2J4509_FileVerbatim()
    {
        var src = @"
class C {
    void M() {
        UndefinedDel d = x => x;
    }
}";
        var r = Transform(src);

        Assert.False(r.Changed);
        Assert.Equal(src, r.OutputCode);
        var row = Assert.Single(r.Diagnostics.Where(d => d.Code == "CS2J4509"));
        Assert.Equal(ClosureLiftSeverity.Error, row.Severity);
        Assert.Equal("M", row.MemberName);
    }

    [Fact]
    public void M1_RefLambdaParameter_CS2J4501_FileVerbatim()
    {
        var src = @"
class C {
    void M() {
        RefDel d = (ref int x) => { x++; };
    }
}

delegate void RefDel(ref int x);";
        var r = Transform(src);

        Assert.False(r.Changed);
        Assert.Equal(src, r.OutputCode);
        var row = Assert.Single(r.Diagnostics.Where(d => d.Code == "CS2J4501"));
        Assert.Equal(ClosureLiftSeverity.Error, row.Severity);
    }

    [Fact]
    public void ResidualLambdas_QuotedExcluded_AnonymousCounted()
    {
        var src = @"
using System.Linq.Expressions;
class C {
    void M() {
        System.Func<int> a = () => 1;
        System.Comparison<int> c = delegate(int x, int y) { return x - y; };
        Expression<System.Func<int>> q = () => 2;
    }
}";
        var (statementLambdas, anonymousMethods) =
            new ClosureLiftPreprocessor().CountResidualLambdas(src);

        Assert.Equal(1, statementLambdas); // quoted expression-tree lambda excluded
        Assert.Equal(1, anonymousMethods);
    }

    [Fact]
    public void ResidualLambdas_ParseBrokenFile_CountsZero()
    {
        var (statementLambdas, anonymousMethods) =
            new ClosureLiftPreprocessor().CountResidualLambdas("class C { void M() { ");

        Assert.Equal(0, statementLambdas);
        Assert.Equal(0, anonymousMethods);
    }

    // ── §7.1 C45 M2: this / read-only / mixed paths (design §7.1 3/4/6/12/…) ──

    [Fact]
    public void M2_ReadOnlyParameter_ExternalReadKept_SnapshotField()
    {
        var src = @"
class C {
    int M(int extra) {
        System.Func<int> f = () => extra + 1;
        int kept = extra;
        return f() + kept;
    }
}";
        var r = Transform(src);

        // D5 on a parameter: field snapshot at the site boundary; the external
        // read (`int kept = extra;`) keeps the parameter name.
        Assert.True(r.Changed);
        Assert.Single(HolderClassNames(r.OutputCode));
        Assert.Contains("int kept = extra;", r.OutputCode);
        Assert.Contains("__cs2jClosure0._cs2jCap_extra = extra;", r.OutputCode);
        Assert.Contains("public int _cs2jCap_extra;", r.OutputCode);
        Assert.Contains("new global::System.Func<int>(__cs2jClosure0.Lambda_0)", r.OutputCode);
        Assert.Contains("internal int Lambda_0() => _cs2jCap_extra + 1;", r.OutputCode);
        Assert.Equal(0, r.Statistics.CapturesLifted);
        Assert.Empty(LambdaNodes(r.OutputCode));
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void M2_EventSubscription_CapturingLambdaMigrates()
    {
        var src = @"
class C {
    event System.Action E;
    void M() {
        int x = 1;
        E += () => { x++; };
        E?.Invoke();
        System.Console.WriteLine(x);
    }
}";
        var r = Transform(src);

        // D16 (§7.1-12): += event subscription is an ordinary delegate site;
        // the lambda migrates, the external read stays C32 field-backed.
        Assert.True(r.Changed);
        Assert.Contains("E += new global::System.Action(__cs2jClosure0.Lambda_0);", r.OutputCode);
        Assert.Contains("internal void Lambda_0()", r.OutputCode);
        Assert.Contains("_cs2jCap_x++;", r.OutputCode);
        Assert.Contains("System.Console.WriteLine(__cs2jClosure0._cs2jCap_x);", r.OutputCode);
        Assert.Empty(LambdaNodes(r.OutputCode));
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void M2_Multicast_TwoCapturingLambdas_SameInstanceSequence()
    {
        var src = @"
class C {
    void M() {
        int x = 1;
        System.Action d = null;
        d += () => { x++; };
        d += () => System.Console.WriteLine(x);
        d();
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Single(HolderClassNames(r.OutputCode));
        Assert.Contains("d += new global::System.Action(__cs2jClosure0.Lambda_0);", r.OutputCode);
        Assert.Contains("d += new global::System.Action(__cs2jClosure0.Lambda_1);", r.OutputCode);
        Assert.Contains("internal void Lambda_1() => System.Console.WriteLine(_cs2jCap_x);", r.OutputCode);
        Assert.Equal(2, r.Statistics.LambdaMethodsLifted - r.Statistics.StaticLambdaMethods);
        Assert.Empty(LambdaNodes(r.OutputCode));
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void M2_CustomDelegate_CapturingLambda_KeepsParamsAndRendersSourceName()
    {
        var src = @"
class C {
    delegate int MyD(int p);
    void M() {
        int x = 10;
        MyD d = (p) => x + p;
        System.Console.WriteLine(d(2));
    }
}";
        var r = Transform(src);

        // D8/D12 on the instance path: fully-qualified source delegate name;
        // the synthesized method keeps the lambda's parameter list.
        Assert.True(r.Changed);
        Assert.Contains("MyD d = new global::C.MyD(__cs2jClosure0.Lambda_0);", r.OutputCode);
        Assert.Contains("internal int Lambda_0(int p) => _cs2jCap_x + p;", r.OutputCode);
        Assert.Empty(LambdaNodes(r.OutputCode));
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void M2_QuotedLambda_ReadOnlyMix_QuotedStaysInstanceMigrates()
    {
        var src = @"
using System;
using System.Linq.Expressions;
class C {
    void M() {
        int r = 7;
        Expression<Func<int>> e = () => r + 1;
        Func<int> f = () => r + 2;
        System.Console.WriteLine(f() + e.Compile()());
    }
}";
        var r = Transform(src);

        // D18②: a read-only symbol shared with a quoted lambda is allowed
        // (only MUTABLE mixed capture is the CS2J3206 freeze). The quoted node
        // stays verbatim; the statement lambda takes the M2 path.
        Assert.True(r.Changed);
        Assert.Contains("Expression<Func<int>> e = () => r + 1;", r.OutputCode);
        Assert.Contains("Func<int> f = new global::System.Func<int>(__cs2jClosure0.Lambda_0);", r.OutputCode);
        Assert.Contains("internal int Lambda_0() => _cs2jCap_r + 2;", r.OutputCode);
        Assert.Equal(1, r.Statistics.ExpressionTreeLambdasSkipped);
        Assert.Single(LambdaNodes(r.OutputCode)); // the quoted node alone
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void M2_NestedZeroCaptureInsideCapturing_InnerStaticSiteRewrittenInMethod()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Action a = () => {
            x++;
            System.Func<int> b = () => 42;
            System.Console.WriteLine(b());
        };
        a();
    }
}";
        var r = Transform(src);

        // The outer capturing lambda becomes Lambda_0; the inner zero-capture
        // lambda still takes the M1 static path and its site is rewritten
        // INSIDE the migrated body (received-tree span anchor after C32/M2
        // statement insertions).
        Assert.True(r.Changed);
        Assert.Contains("System.Action a = new global::System.Action(__cs2jClosure0.Lambda_0);", r.OutputCode);
        Assert.Contains("System.Func<int> b = new global::System.Func<int>(Cs2jLambda_M_0);", r.OutputCode);
        Assert.Contains("private static int Cs2jLambda_M_0() => 42;", r.OutputCode);
        Assert.Equal(1, r.Statistics.StaticLambdaMethods);
        Assert.Equal(1, r.Statistics.LambdaMethodsLifted - r.Statistics.StaticLambdaMethods);
        Assert.Empty(LambdaNodes(r.OutputCode));
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void M2_MixedStaticAndInstance_Idempotent_SecondRunClean()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Action a = () => { x++; };
        System.Func<int> b = () => 42;
        a();
        System.Console.WriteLine(x + b());
    }
}";
        var r1 = Transform(src);
        Assert.True(r1.Changed);
        Assert.Contains("new global::System.Action(__cs2jClosure0.Lambda_0)", r1.OutputCode);
        Assert.Contains("new global::System.Func<int>(Cs2jLambda_M_0)", r1.OutputCode);
        AssertOutputCompiles(r1.OutputCode);

        // Regression for the dual-span anchor bug: re-entering the mixed file
        // must not re-pair the already-rewritten static site (CS0111).
        var r2 = new ClosureLiftPreprocessor().Preprocess(
            r1.OutputCode, ParseWithSemantics(r1.OutputCode), new ClosureLiftOptions());

        Assert.False(r2.Changed);
        Assert.Equal(r1.OutputCode, r2.OutputCode);
        Assert.Equal(0, r2.Statistics.StaticLambdaMethods);
        Assert.Empty(r2.Diagnostics.Where(d => d.Severity == ClosureLiftSeverity.Error));
    }

    [Fact]
    public void M2_RefArgumentInsideMigratedBody_FieldBackedThroughThis()
    {
        var src = @"
class C {
    void Mutate(ref int v) => v += 10;
    void M() {
        int x = 1;
        System.Action a = () => Mutate(ref x);
        a();
        System.Console.WriteLine(x);
    }
}";
        var r = Transform(src);

        // D21 through the instance path: the ref site references the holder
        // field (`ref _cs2jCap_x`) and the host method via `_cs2jThis`; the
        // external read stays C32 field-backed.
        Assert.True(r.Changed);
        Assert.Contains("var __cs2jClosure0 = new Cs2jDisplayClass_M_0(this);", r.OutputCode);
        Assert.Contains("internal void Lambda_0() => _cs2jThis.Mutate(ref _cs2jCap_x);", r.OutputCode);
        Assert.Contains("System.Console.WriteLine(__cs2jClosure0._cs2jCap_x);", r.OutputCode);
        Assert.Empty(LambdaNodes(r.OutputCode));
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void M3_NestedCapturingChain_LoweredToParentLinkedClasses()
    {
        var src = @"
class C {
    void M() {
        int outer = 0;
        System.Action a = () => {
            System.Action b = () => { outer++; };
            b();
        };
        a();
    }
}";
        var r = Transform(src);

        // M3 (D4): the nested pair is a real lowering now — no residual
        // lambdas, two Lambda_k methods, a nested child class holding
        // `_cs2jParent`, and the child instance constructed inside the
        // migrated parent method (replaces the M2 accounted-residual shape).
        Assert.True(r.Changed);
        Assert.Equal(2, HolderClassNames(r.OutputCode).Count);
        Assert.Equal(1, r.Statistics.DisplayClassesGenerated);
        Assert.Equal(1, r.Statistics.NestedDisplayClasses);
        Assert.Equal(0, r.Statistics.AccountedResidualLambdas);
        Assert.Contains("__cs2jClosure0._cs2jCap_outer = 0;", r.OutputCode);
        Assert.Contains("new global::System.Action(__cs2jClosure0.Lambda_0)", r.OutputCode);
        Assert.Contains(
            "var __cs2jClosure1 = new Cs2jDisplayClass_M_0.Cs2jDisplayClass_M_1(this);",
            r.OutputCode);
        Assert.Contains("new global::System.Action(__cs2jClosure1.Lambda_0)", r.OutputCode);
        Assert.Contains(
            "private sealed class Cs2jDisplayClass_M_1", r.OutputCode);
        Assert.Contains(
            "private readonly Cs2jDisplayClass_M_0 _cs2jParent;", r.OutputCode);
        Assert.Contains(
            "public Cs2jDisplayClass_M_1(Cs2jDisplayClass_M_0 parent)", r.OutputCode);
        Assert.Contains("_cs2jParent._cs2jCap_outer++;", r.OutputCode);
        Assert.Empty(LambdaNodes(r.OutputCode));
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "CS2J4510");
        var (statementLambdas, _) = new ClosureLiftPreprocessor().CountResidualLambdas(r.OutputCode);
        Assert.Equal(0, statementLambdas);
        AssertOutputCompiles(r.OutputCode);

        // Idempotent: re-entering the lowered tree is a no-op.
        var r2 = new ClosureLiftPreprocessor().Preprocess(
            r.OutputCode, ParseWithSemantics(r.OutputCode), new ClosureLiftOptions());
        Assert.False(r2.Changed);
        Assert.Equal(r.OutputCode, r2.OutputCode);
    }

    [Fact]
    public void M2_TransformedTree_ResidualCensusZero()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Action a = () => { x++; };
        System.Func<int> b = () => 42;
        a();
        System.Console.WriteLine(x + b());
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Equal(0, r.Statistics.ResidualMutableCaptures);
        var (statementLambdas, anonymousMethods) =
            new ClosureLiftPreprocessor().CountResidualLambdas(r.OutputCode);
        Assert.Equal(0, statementLambdas);
        Assert.Equal(0, anonymousMethods);
    }

    [Fact]
    public void M2_AuditOnly_ThisAndReadOnlyCapture_StaysVerbatim()
    {
        var src = @"
class C {
    private int _n;
    void M(int extra) {
        System.Func<int> f = () => _n + extra;
        System.Console.WriteLine(f());
    }
}";
        var r = Transform(src, new ClosureLiftOptions(AuditOnly: true));

        Assert.False(r.Changed);
        Assert.Equal(src, r.OutputCode);
        Assert.DoesNotContain(r.Diagnostics, d => d.Severity == ClosureLiftSeverity.Error);
        Assert.Equal(0, r.Statistics.DisplayClassesGenerated);
        Assert.Equal(0, r.Statistics.LambdaMethodsLifted);
    }

    [Fact]
    public void M2_ResidualGateFaultInjection_CS2J4510_FileVerbatim()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Action a = () => { x++; };
        a();
    }
}";
        DisplayClassPlanner.ForcedUnaccountedForTests = 1;
        try
        {
            var r = Transform(src);

            // §7.3: an unaccounted residual is a strict Error; the facade
            // rolls the whole file back even though earlier stages edited it.
            Assert.False(r.Changed);
            Assert.Equal(src, r.OutputCode);
            var row = Assert.Single(r.Diagnostics.Where(d => d.Code == "CS2J4510"));
            Assert.Equal(ClosureLiftSeverity.Error, row.Severity);
            Assert.Equal(0, r.Statistics.FilesTransformed);
        }
        finally
        {
            DisplayClassPlanner.ForcedUnaccountedForTests = null;
        }
    }

    [Fact]
    public void M3_ThreeLevelNestedChain_ParentLinksReachTopFields()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Action a = () => {
            System.Action b = () => {
                System.Action c = () => { x++; };
                c();
            };
            b();
        };
        a();
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Equal(3, HolderClassNames(r.OutputCode).Count);
        Assert.Equal(2, r.Statistics.NestedDisplayClasses);
        Assert.Contains(
            "var __cs2jClosure1 = new Cs2jDisplayClass_M_0.Cs2jDisplayClass_M_1(this);",
            r.OutputCode);
        Assert.Contains(
            "var __cs2jClosure2 = new Cs2jDisplayClass_M_0.Cs2jDisplayClass_M_1.Cs2jDisplayClass_M_2(this);",
            r.OutputCode);
        Assert.Contains(
            "private readonly Cs2jDisplayClass_M_0.Cs2jDisplayClass_M_1 _cs2jParent;",
            r.OutputCode);
        // The leaf reaches the chain-top field through two parent hops.
        Assert.Contains("_cs2jParent._cs2jParent._cs2jCap_x++;", r.OutputCode);
        Assert.Empty(LambdaNodes(r.OutputCode));
        AssertOutputCompiles(r.OutputCode);
        AssertIdempotent(r.OutputCode);
    }

    [Fact]
    public void M3_NestedLambdas_DifferentBlocks_GetSeparateChildClasses()
    {
        var src = @"
class C {
    void M(bool cond) {
        int x = 0;
        System.Func<int> a;
        if (cond) {
            a = () => { System.Func<int> b = () => x++; return b(); };
        } else {
            a = () => { System.Func<int> c = () => x + 1; return c(); };
        }
        System.Console.WriteLine(a());
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        // Same parent, different anchor blocks → two child classes, each with
        // its own per-block instance.
        Assert.Contains("private sealed class Cs2jDisplayClass_M_1", r.OutputCode);
        Assert.Contains("private sealed class Cs2jDisplayClass_M_2", r.OutputCode);
        Assert.Contains(
            "var __cs2jClosure1 = new Cs2jDisplayClass_M_0.Cs2jDisplayClass_M_1(this);",
            r.OutputCode);
        Assert.Contains(
            "var __cs2jClosure2 = new Cs2jDisplayClass_M_0.Cs2jDisplayClass_M_2(this);",
            r.OutputCode);
        Assert.Equal(2, r.Statistics.NestedDisplayClasses);
        Assert.Empty(LambdaNodes(r.OutputCode));
        AssertOutputCompiles(r.OutputCode);
        AssertIdempotent(r.OutputCode);
    }

    [Fact]
    public void M3_NestedLambdas_SameBlock_ShareChildClassAndInstance()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Action a = () => {
            System.Action b = () => x++;
            System.Action c = () => x += 2;
            b(); c();
        };
        a();
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        // D3 sharing at D4 depth: both sites share one child class + one
        // instance, methods numbered in SpanStart order.
        Assert.Contains(
            "var __cs2jClosure1 = new Cs2jDisplayClass_M_0.Cs2jDisplayClass_M_1(this);",
            r.OutputCode);
        Assert.Contains("new global::System.Action(__cs2jClosure1.Lambda_0)", r.OutputCode);
        Assert.Contains("new global::System.Action(__cs2jClosure1.Lambda_1)", r.OutputCode);
        Assert.Contains("internal void Lambda_0() => _cs2jParent._cs2jCap_x++;", r.OutputCode);
        Assert.Contains("internal void Lambda_1() => _cs2jParent._cs2jCap_x += 2;", r.OutputCode);
        Assert.Single(HolderClassNames(r.OutputCode).Where(n => n == "Cs2jDisplayClass_M_1"));
        Assert.Empty(LambdaNodes(r.OutputCode));
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void M3_OuterLambdaParameter_ReadOnlyCapturedByNested_EntryAssignedToField()
    {
        var src = @"
class C {
    void M() {
        System.Action<int> a = (p) => {
            System.Action b = () => System.Console.WriteLine(p + 1);
            b();
        };
        a(1);
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Contains("public int _cs2jCap_p;", r.OutputCode);
        Assert.Contains("internal void Lambda_0(int p)", r.OutputCode);
        Assert.Contains("_cs2jCap_p = p;", r.OutputCode);
        Assert.Contains(
            "internal void Lambda_0() => System.Console.WriteLine(_cs2jParent._cs2jCap_p + 1);",
            r.OutputCode);
        Assert.Empty(LambdaNodes(r.OutputCode));
        AssertOutputCompiles(r.OutputCode);
        AssertIdempotent(r.OutputCode);
    }

    [Fact]
    public void M3_OuterLambdaParameter_MutatedAndCaptured_AllSitesFieldBacked()
    {
        var src = @"
class C {
    void M() {
        System.Action<int> a = (p) => {
            p++;
            System.Action b = () => System.Console.WriteLine(p);
            b();
        };
        a(1);
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Contains("_cs2jCap_p = p;", r.OutputCode);
        Assert.Contains("_cs2jCap_p++;", r.OutputCode);
        Assert.Contains(
            "internal void Lambda_0() => System.Console.WriteLine(_cs2jParent._cs2jCap_p);",
            r.OutputCode);
        // The signature parameter is an identifier TOKEN (not an
        // IdentifierName), so the only bare textual `p` left is the
        // entry-assignment RHS.
        Assert.Single(BareIdentifiers(r.OutputCode, "p"));
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void M3_OuterLambdaLocal_CapturedByNested_DeclarationBecomesFieldAssignment()
    {
        var src = @"
class C {
    void M() {
        System.Action a = () => {
            int y = 1;
            System.Action b = () => y++;
            b();
            System.Console.WriteLine(y);
        };
        a();
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Contains("public int _cs2jCap_y;", r.OutputCode);
        // No local declaration of y survives; the initializer becomes a field
        // assignment and every use site (including the outer body) is field-backed.
        Assert.DoesNotContain("int y", r.OutputCode);
        Assert.Contains("_cs2jCap_y = 1;", r.OutputCode);
        Assert.Contains("_cs2jParent._cs2jCap_y++;", r.OutputCode);
        Assert.Contains("System.Console.WriteLine(_cs2jCap_y);", r.OutputCode);
        Assert.Empty(BareIdentifiers(r.OutputCode, "y"));
        Assert.Empty(LambdaNodes(r.OutputCode));
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void M3_ExpressionBodiedOuterLambda_BlockWrappedForChildInstance()
    {
        var src = @"
class C {
    void M() {
        int x = 0;
        System.Func<System.Func<int>> a = () => () => x + 1;
        System.Console.WriteLine(a()());
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        // The expression-bodied outer method wraps into a block that declares
        // the child instance and returns the delegate creation.
        Assert.Contains("internal global::System.Func<int> Lambda_0()", r.OutputCode);
        Assert.Contains(
            "var __cs2jClosure1 = new Cs2jDisplayClass_M_0.Cs2jDisplayClass_M_1(this);",
            r.OutputCode);
        Assert.Contains("return new global::System.Func<int>(__cs2jClosure1.Lambda_0);", r.OutputCode);
        Assert.Contains("internal int Lambda_0() => _cs2jParent._cs2jCap_x + 1;", r.OutputCode);
        Assert.Empty(LambdaNodes(r.OutputCode));
        AssertOutputCompiles(r.OutputCode);
        AssertIdempotent(r.OutputCode);
    }

    [Fact]
    public void M3_NestedChain_ChildReachesHostThisViaParent()
    {
        var src = @"
class C {
    private int _n;
    void Use(int z) { }
    void M() {
        System.Action a = () => {
            System.Action b = () => Use(_n + 1);
            b();
        };
        a();
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        // `_cs2jThis` is carried only by the chain top; the child reaches it
        // through `_cs2jParent` and declares no `_cs2jThis` of its own.
        Assert.Contains("var __cs2jClosure0 = new Cs2jDisplayClass_M_0(this);", r.OutputCode);
        var childIndex = r.OutputCode.IndexOf(
            "private sealed class Cs2jDisplayClass_M_1", StringComparison.Ordinal);
        Assert.DoesNotContain("_cs2jThis;", r.OutputCode[childIndex..]);
        Assert.Contains(
            "internal void Lambda_0() => _cs2jParent._cs2jThis.Use(_cs2jParent._cs2jThis._n + 1);",
            r.OutputCode);
        AssertOutputCompiles(r.OutputCode);
    }

    [Fact]
    public void M3_ForeachBody_NestedChildInstanceRebuiltForEveryIteration()
    {
        var src = @"
using System.Collections.Generic;
class C {
    List<System.Action> M(IEnumerable<int> src) {
        var acts = new List<System.Action>();
        foreach (var item in src) {
            acts.Add(() => {
                System.Action later = () => System.Console.WriteLine(item);
                later();
            });
        }
        return acts;
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        // The routine per-iteration holder stays in the foreach body; the
        // nested child instance is constructed inside the migrated method, so
        // every invocation gets a fresh child (delegate-identity fixture
        // locks the observable behavior).
        Assert.Contains("foreach", r.OutputCode);
        Assert.Contains(
            "var __cs2jClosure1 = new Cs2jDisplayClass_M_0.Cs2jDisplayClass_M_1(this);",
            r.OutputCode);
        Assert.Contains(
            "internal void Lambda_0() => System.Console.WriteLine(_cs2jParent._cs2jCap_item);",
            r.OutputCode);
        Assert.Equal(1, r.Statistics.NestedDisplayClasses);
        Assert.Empty(LambdaNodes(r.OutputCode));
        AssertOutputCompiles(r.OutputCode);
        AssertIdempotent(r.OutputCode);
    }

    [Fact]
    public void M3_PromotionBlocked_LoopScopedOuterLocal_StaysAccountedResidual()
    {
        var src = @"
class C {
    void M() {
        System.Action a = () => {
            for (int i = 0; i < 3; i++) {
                System.Action b = () => System.Console.WriteLine(i);
                b();
            }
        };
        a();
    }
}";
        var r = Transform(src);

        // A single promoted field cannot preserve per-iteration identity of a
        // loop-scoped local: the owner lambda and its nested site stay in the
        // documented accounted bucket and the file is kept verbatim.
        Assert.False(r.Changed);
        Assert.Equal(src, r.OutputCode);
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "CS2J4510");
        Assert.Equal(2, r.Statistics.AccountedResidualLambdas);
    }

    [Fact]
    public void M3_SiblingTopLevelTypes_EarlierTypeDocumented_AnchorsStableAcrossSpineAppends()
    {
        // Regression: appending holder members to the earlier type along the
        // type spine shifts the ABSOLUTE spans of every later sibling in the
        // detached tree. A span-based anchor lookup then missed the later
        // type's statement boundary (doc comments merely changed the offset),
        // the stage bailed for that type mid-run and the file ended up
        // "holder-only" — fields present, sites still physical lambdas — which
        // broke idempotency (duplicate class on the second pass). Anchors must
        // pair lambdas by descendant order, not by absolute span.
        var src = @"
/// <summary>
/// Documented earlier sibling type; the documentation text shifts spans.
/// </summary>
public sealed class First
{
    public int Run()
    {
        var count = 0;
        System.Action inc = () => count++;
        inc();
        inc();
        return count;
    }
}

public sealed class Second
{
    public int Run()
    {
        var count = 0;
        System.Action inc = () => count += 5;
        inc();
        return count;
    }
}";
        var r = Transform(src);

        Assert.True(r.Changed);
        Assert.Equal(
            2,
            System.Text.RegularExpressions.Regex.Matches(r.OutputCode, "internal void Lambda_0\\(\\)").Count);
        Assert.Equal(2, r.Statistics.LambdaMethodsLifted);
        Assert.Empty(LambdaNodes(r.OutputCode));
        AssertOutputCompiles(r.OutputCode);
        AssertIdempotent(r.OutputCode);
    }

    private static void AssertIdempotent(string output)
    {
        var r2 = new ClosureLiftPreprocessor().Preprocess(
            output, ParseWithSemantics(output), new ClosureLiftOptions());
        Assert.False(r2.Changed);
        Assert.Equal(output, r2.OutputCode);
    }

    // ── Helpers (mirror ClosureLiftUnitTests) ──

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private static SemanticModel ParseWithSemantics(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "closure-m1-test",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return compilation.GetSemanticModel(tree);
    }

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly))
                    yield return MetadataReference.CreateFromFile(assembly);
            }

            yield break;
        }

        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }

    private static ClosureLiftResult Transform(string src, ClosureLiftOptions? options = null)
        => new ClosureLiftPreprocessor().Preprocess(
            src, ParseWithSemantics(src), options ?? new ClosureLiftOptions());

    /// <summary>Design §7.1: every transformed tree must compile (LanguageVersion.Latest, unsafe allowed).</summary>
    private static void AssertOutputCompiles(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "closure-m1-transformed",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(
            errors.Count == 0,
            "Transformed output failed to compile:\n" + string.Join("\n", errors.Select(e => e.GetMessage())));
    }

    private static IReadOnlyList<LambdaExpressionSyntax> LambdaNodes(string code)
        => CSharpSyntaxTree.ParseText(code, ParseOptions).GetRoot()
            .DescendantNodes().OfType<LambdaExpressionSyntax>()
            .ToList();

    private static IReadOnlyList<string> HolderClassNames(string code)
    {
        var root = (SyntaxNode)CSharpSyntaxTree.ParseText(code, ParseOptions).GetRoot();
        return root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Where(c => c.Identifier.Text.StartsWith("Cs2jDisplayClass_", StringComparison.Ordinal))
            .Select(c => c.Identifier.Text)
            .ToList();
    }

    /// <summary>All IdentifierName nodes spelling <paramref name="name"/> (parameters/declarators excluded).</summary>
    private static IReadOnlyList<IdentifierNameSyntax> BareIdentifiers(string code, string name)
        => ((SyntaxNode)CSharpSyntaxTree.ParseText(code, ParseOptions).GetRoot())
            .DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(id => id.Identifier.Text == name)
            .ToList();
}
