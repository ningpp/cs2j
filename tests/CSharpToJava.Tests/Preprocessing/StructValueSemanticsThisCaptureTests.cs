using System.Reflection;
using CSharpToJava.Core.StructValueSemantics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// SV040-c/d 根因修复单测（设计 §6/§11）：
/// ①同步 struct 实例方法内直接捕获 this 的 lambda/匿名方法/局部函数（CS1673 形态，
/// 含 ref-out-in 风格 ByRef 访问器闭包）改写为方法级 this 持有者 copy-in/copy-out；
/// ②async/iterator 实例方法由编译器状态机按值快照 this，不再判 SV040；
/// ③真无解形态（表达式树捕获、async/iterator 内捕获闭包、base 访问+捕获闭包、
/// 非方法成员内的捕获、ref/out this、ref return、指针字段）保持显式失败；
/// ④this 整体赋值的方法持有者/字段式展开见 StructValueSemanticsThisAssignmentTests；
/// ⑤显式其它接收者（builder.AsSpan()）不误判；⑥改写幂等。
/// </summary>
public class StructValueSemanticsThisCaptureTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

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

    private static CSharpCompilation BuildCompilation(params string[] sources)
    {
        var trees = sources.Select(s => (SyntaxTree)CSharpSyntaxTree.ParseText(s, ParseOptions)).ToArray();
        return CSharpCompilation.Create(
            "svs-this-capture-test",
            trees,
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
    }

    private sealed class PlanRun
    {
        public required StructValueSemanticsPreprocessor Preprocessor { get; init; }
        public required CSharpCompilation Compilation { get; init; }
        public required StructValueSemanticsStatistics Statistics { get; init; }
        public required List<StructValueSemanticsDiagnostic> Diagnostics { get; init; }
        public required string Output { get; init; }
        public required bool Changed { get; init; }
    }

    private static PlanRun Analyze(string source, StructValueSemanticsOptions? options = null)
    {
        var compilation = BuildCompilation(source);
        var pp = new StructValueSemanticsPreprocessor(options ?? new StructValueSemanticsOptions());
        pp.BindCompilation(compilation);

        var stats = new StructValueSemanticsStatistics();
        var diags = new List<StructValueSemanticsDiagnostic>();
        var tree = compilation.SyntaxTrees.Single();
        var result = pp.RewriteFile(
            tree.GetText().ToString(), compilation.GetSemanticModel(tree), stats, diags);

        return new PlanRun
        {
            Preprocessor = pp,
            Compilation = compilation,
            Statistics = stats,
            Diagnostics = diags,
            Output = result.Changed ? result.OutputCode : tree.GetText().ToString(),
            Changed = result.Changed,
        };
    }

    private static void AssertOutputCompiles(string output)
    {
        var errors = BuildCompilation(output).GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "Rewritten output failed to compile:\n" + string.Join("\n", errors.Select(e => e.ToString()))
            + "\n--- output ---\n" + output);
    }

    private static string Normalized(string code) =>
        CSharpSyntaxTree.ParseText(code, ParseOptions).GetRoot().NormalizeWhitespace().ToFullString();

    private static ClonePlan Plan(PlanRun run, string qualifiedName = "Lib.Box") =>
        Assert.Single(run.Preprocessor.Plans.Where(p => p.StructQualifiedName == qualifiedName));

    private static void AssertInfeasible(
        PlanRun run, StructValueSemanticsDiagnosticCode code, string qualifiedName = "Lib.Box")
    {
        var plan = Plan(run, qualifiedName);
        Assert.Equal(ClonePlanOutcome.Infeasible, plan.Outcome);
        Assert.False(plan.IsActive);
        Assert.Equal(code, plan.FailCode);
        Assert.Contains(run.Diagnostics, d =>
            d.Code == code && d.Severity == StructValueSemanticsSeverity.Error);
        Assert.Null(plan.GeneratedCloneMethod);
        Assert.Null(plan.GeneratedCopyConstructor);
        Assert.DoesNotContain(StructValueSemanticsSyntaxFacts.ThisCopyTempPrefix, run.Output);
    }

    // ---------- 子形态 (a)：lambda/匿名函数/局部函数捕获 this → 持有者改写 ----------

    [Fact]
    public void WritingEscapingLambda_BecomesHolderCopyInCopyOut()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public void Arm()
                {
                    System.Action a = () => X++;
                    a();
                }
            }
            """);

        var plan = Plan(run);
        Assert.True(plan.IsActive);
        Assert.Single(plan.ThisCaptureRewrites);
        Assert.True(plan.ThisCaptureRewrites[0].HasWrites);
        Assert.Equal(1, run.Statistics.ThisCaptureMethodsRewritten);
        Assert.Equal(0, run.Statistics.ThisCaptureReadOnlyMethods);

        var output = Normalized(run.Output);
        Assert.Contains("var __cs2jTc0 = this;", output);
        Assert.Contains("() => __cs2jTc0.X++", output);
        Assert.Contains("try", output);
        Assert.Contains("finally", output);
        Assert.Contains("this = __cs2jTc0;", output);
        AssertOutputCompiles(run.Output);
    }

    [Fact]
    public void ReadOnlyCapturingLambda_HolderSnapshotWithoutCopyOut()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public int Read()
                {
                    System.Func<int> f = () => X + 1;
                    return f();
                }
            }
            """);

        Assert.True(Plan(run).IsActive);
        Assert.Equal(1, run.Statistics.ThisCaptureMethodsRewritten);
        Assert.Equal(1, run.Statistics.ThisCaptureReadOnlyMethods);

        var output = Normalized(run.Output);
        Assert.Contains("var __cs2jTc0 = this;", output);
        Assert.Contains("() => __cs2jTc0.X + 1", output);
        Assert.DoesNotContain("finally", output);
        Assert.DoesNotContain("this = __cs2jTc0", output);
        AssertOutputCompiles(run.Output);
    }

    [Fact]
    public void ByRefStyleAccessorClosure_RoutedToHolder_AndCommitsAtExit()
    {
        var run = Analyze("""
            namespace Lib;
            public sealed class ByRef<T>
            {
                public System.Func<T>? Get;
                public System.Action<T>? Set;
                public static ByRef<T> Create(System.Func<T> g, System.Action<T> s) =>
                    new ByRef<T> { Get = g, Set = s };
            }
            public struct Box
            {
                public int X;
                public int Parse()
                {
                    var r = ByRef<int>.Create(() => X, v => X = v);
                    r.Set!(43);
                    return r.Get!();
                }
            }
            """);

        var plan = Plan(run);
        Assert.True(plan.IsActive);
        Assert.True(plan.ThisCaptureRewrites[0].HasWrites);

        var output = Normalized(run.Output);
        Assert.Contains("() => __cs2jTc0.X", output);
        Assert.Contains("v => __cs2jTc0.X = v", output);
        Assert.Contains("this = __cs2jTc0;", output);
        AssertOutputCompiles(run.Output);

        // 运行时：setter 链经持有者写入，finally 出口回写对调用者可见。
        var assembly = EmitAssembly(run.Output);
        var boxType = assembly.GetType("Lib.Box")!;
        var instance = Activator.CreateInstance(boxType);
        var result = boxType.GetMethod("Parse")!.Invoke(instance, null);
        Assert.Equal(43, result);
        Assert.Equal(43, boxType.GetField("X")!.GetValue(instance));
    }

    [Fact]
    public void DirectlyCalledLocalFunctionCapturingThis_IsRouted()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public int Use()
                {
                    int Local() => X + 1;
                    return Local();
                }
            }
            """);

        Assert.True(Plan(run).IsActive);
        Assert.Equal(1, run.Statistics.ThisCaptureMethodsRewritten);
        Assert.Equal(1, run.Statistics.ThisCaptureReadOnlyMethods);
        var output = Normalized(run.Output);
        Assert.Contains("int Local() => __cs2jTc0.X + 1;", output);
        Assert.DoesNotContain("finally", output);
        AssertOutputCompiles(run.Output);
    }

    [Fact]
    public void NestedNonEscapingLambdaCapturingThis_IsRouted()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public int Use()
                {
                    System.Func<int> outer = () =>
                    {
                        System.Func<int> inner = () => X;
                        return inner();
                    };
                    return outer();
                }
            }
            """);

        Assert.True(Plan(run).IsActive);
        var output = Normalized(run.Output);
        Assert.Contains("__cs2jTc0.X", output);
        AssertOutputCompiles(run.Output);
    }

    [Fact]
    public void ExpressionBodiedMethodWithCapturingLambda_BecomesBlockBodiedReturn()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public int Use() => Invoke(() => X);
                private static int Invoke(System.Func<int> f) => f();
            }
            """);

        Assert.True(Plan(run).IsActive);
        var output = Normalized(run.Output);
        Assert.Contains("var __cs2jTc0 = this;", output);
        Assert.Contains("return Invoke(() => __cs2jTc0.X);", output);
        Assert.DoesNotContain("public int Use() =>", output);
        AssertOutputCompiles(run.Output);
    }

    [Fact]
    public void AnonymousMethodCapturingThis_IsRouted()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public void Arm()
                {
                    System.Action a = delegate { X++; };
                    a();
                }
            }
            """);

        Assert.True(Plan(run).IsActive);
        var output = Normalized(run.Output);
        Assert.Contains("var __cs2jTc0 = this;", output);
        Assert.Contains("delegate", output);
        Assert.Contains("__cs2jTc0.X++", output);
        Assert.Contains("this = __cs2jTc0;", output);
        AssertOutputCompiles(run.Output);
    }

    [Fact]
    public void CapturedHolder_RuntimeCopyOutAndEscapedClosureSnapshotIsolation()
    {
        var source = """
            namespace Lib;
            public struct Box
            {
                public int X;
                public System.Action Arm()
                {
                    System.Action a = () => X++;
                    a();
                    return a;
                }
            }
            """;
        var run = Analyze(source);
        AssertOutputCompiles(run.Output);

        var assembly = EmitAssembly(run.Output);
        var boxType = assembly.GetType("Lib.Box")!;
        var instance = Activator.CreateInstance(boxType);
        var returned = (System.Action)boxType.GetMethod("Arm")!.Invoke(instance, null)!;

        // 方法内 a() 经持有者写入，出口回写对调用者可见。
        Assert.Equal(1, boxType.GetField("X")!.GetValue(instance));

        // 方法返回后再调用逃逸闭包：只改它自己捕获的持有者快照，调用者存储不变。
        returned();
        returned();
        Assert.Equal(1, boxType.GetField("X")!.GetValue(instance));
    }

    // ---------- 子形态 (b)：async/iterator 实例方法按值快照，不再 SV040 ----------

    [Fact]
    public void AsyncReadOnlyInstanceMethod_IsNotRewritten_PlanStaysActive()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public async System.Threading.Tasks.Task<int> GetAsync()
                {
                    await System.Threading.Tasks.Task.CompletedTask;
                    return X + 1;
                }
            }
            """);

        Assert.True(Plan(run).IsActive);
        Assert.Equal(0, run.Statistics.ThisCaptureMethodsRewritten);
        Assert.DoesNotContain(StructValueSemanticsSyntaxFacts.ThisCopyTempPrefix, run.Output);
        AssertOutputCompiles(run.Output);
    }

    [Fact]
    public void IteratorInstanceMethod_IsNotRewritten_PlanStaysActive()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public System.Collections.Generic.IEnumerable<int> Values()
                {
                    yield return X;
                    yield return X + 1;
                }
            }
            """);

        Assert.True(Plan(run).IsActive);
        Assert.Equal(0, run.Statistics.ThisCaptureMethodsRewritten);
        Assert.DoesNotContain(StructValueSemanticsSyntaxFacts.ThisCopyTempPrefix, run.Output);
        AssertOutputCompiles(run.Output);
    }

    [Fact]
    public void AsyncMethodContainingThisCapturingClosure_StaysSV040()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public async System.Threading.Tasks.Task UseAsync()
                {
                    System.Action a = () => X++;
                    await System.Threading.Tasks.Task.CompletedTask;
                    a();
                }
            }
            """);
        AssertInfeasible(run, StructValueSemanticsDiagnosticCode.SV040);
    }

    [Fact]
    public void IteratorMethodContainingThisCapturingClosure_StaysSV040()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public System.Collections.Generic.IEnumerable<int> Values(bool flag)
                {
                    System.Action a = () => X++;
                    a();
                    yield return X;
                }
            }
            """);
        AssertInfeasible(run, StructValueSemanticsDiagnosticCode.SV040);
    }

    // ---------- 站点 1 根因：显式其它接收者不误判 ----------

    [Fact]
    public void ExplicitReceiverLambda_DoesNotCaptureThis_NoHolderRewrite()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public string Use(char[] builder)
                {
                    System.Func<char[], string> make = b => b.Length.ToString();
                    return make(builder);
                }
            }
            """);

        Assert.True(Plan(run).IsActive);
        Assert.Empty(Plan(run).ThisCaptureRewrites);
        Assert.Equal(0, run.Statistics.ThisCaptureMethodsRewritten);
        Assert.DoesNotContain(StructValueSemanticsSyntaxFacts.ThisCopyTempPrefix, run.Output);
        AssertOutputCompiles(run.Output);
    }

    [Fact]
    public void LambdaCapturingLocalOnly_IsNotRewritten()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public int Use()
                {
                    var q = 1;
                    System.Func<int> f = () => q + 1;
                    X = f();
                    return X;
                }
            }
            """);

        Assert.True(Plan(run).IsActive);
        Assert.Equal(0, run.Statistics.ThisCaptureMethodsRewritten);
        Assert.DoesNotContain(StructValueSemanticsSyntaxFacts.ThisCopyTempPrefix, run.Output);
        AssertOutputCompiles(run.Output);
    }

    [Fact]
    public void PlainMethodWithoutAnonymousFunction_IsNotRewritten()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public void Bump() { X++; }
            }
            """);

        Assert.True(Plan(run).IsActive);
        Assert.Equal(0, run.Statistics.ThisCaptureMethodsRewritten);
        Assert.DoesNotContain(StructValueSemanticsSyntaxFacts.ThisCopyTempPrefix, run.Output);
        AssertOutputCompiles(run.Output);
    }

    // ---------- 真无解形态保持显式失败 ----------

    [Fact]
    public void ExpressionTreeLambdaCapturingThis_StaysSV040()
    {
        var run = Analyze("""
            namespace Lib;
            using System.Linq.Expressions;
            public struct Box
            {
                public int X;
                public void Use()
                {
                    Expression<System.Func<int>> e = () => X;
                }
            }
            """);
        AssertInfeasible(run, StructValueSemanticsDiagnosticCode.SV040);
    }

    [Fact]
    public void BaseAccessPlusCapturingClosure_StaysSV040()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public int Use()
                {
                    System.Func<int> f = () => X;
                    return f() + (int)base.GetHashCode();
                }
            }
            """);
        AssertInfeasible(run, StructValueSemanticsDiagnosticCode.SV040);
    }

    [Fact]
    public void CapturingClosureInPropertyAccessor_StaysSV040()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public int P
                {
                    get
                    {
                        System.Func<int> f = () => X;
                        return f();
                    }
                }
            }
            """);
        AssertInfeasible(run, StructValueSemanticsDiagnosticCode.SV040);
    }

    [Fact]
    public void RefThisArgument_StaysSV040()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public void Hand(ref Box other) { other.X++; }
                public void Escape() { Hand(ref this); }
            }
            """);
        AssertInfeasible(run, StructValueSemanticsDiagnosticCode.SV040);
    }

    [Fact]
    public void RefReturnMutableField_StaysSV040()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Inner { public int X; }
            public struct Box
            {
                private Inner _f;
                public ref Inner Foo() => ref _f;
            }
            """);
        AssertInfeasible(run, StructValueSemanticsDiagnosticCode.SV040);
    }

    [Fact]
    public void ThisAssignment_InMethod_BecomesHolderCopyInCopyOut()
    {
        // SV041 根因修复：普通实例方法整体赋值 this 走方法级持有者（与 SV040-d 同机制）。
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public void Reset() { this = default; }
            }
            """);

        var plan = Plan(run);
        Assert.True(plan.IsActive);
        Assert.Single(plan.ThisCaptureRewrites);
        Assert.True(plan.ThisCaptureRewrites[0].HasWrites);
        Assert.Equal(1, run.Statistics.ThisCaptureMethodsRewritten);
        var output = Normalized(run.Output);
        Assert.Contains("var __cs2jTc0 = this;", output);
        Assert.Contains("__cs2jTc0 = default;", output);
        Assert.Contains("this = __cs2jTc0;", output);
        AssertOutputCompiles(run.Output);
        Assert.DoesNotContain(run.Diagnostics, d =>
            d.Severity == StructValueSemanticsSeverity.Error
            && d.Code == StructValueSemanticsDiagnosticCode.SV041);
    }

    [Fact]
    public void PointerField_StaysSV042()
    {
        var run = Analyze("""
            namespace Lib;
            public unsafe struct Box
            {
                public int* Ptr;
            }
            """);
        AssertInfeasible(run, StructValueSemanticsDiagnosticCode.SV042);
    }

    // ---------- 幂等：二次运行零 diff ----------

    [Fact]
    public void RewriteIsIdempotent_SecondRunProducesNoChange()
    {
        var first = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public void Arm()
                {
                    System.Action a = () => X++;
                    a();
                }
            }
            """);
        AssertOutputCompiles(first.Output);

        var secondCompilation = BuildCompilation(first.Output);
        var pp = new StructValueSemanticsPreprocessor();
        pp.BindCompilation(secondCompilation);
        var stats = new StructValueSemanticsStatistics();
        var diags = new List<StructValueSemanticsDiagnostic>();
        var tree = secondCompilation.SyntaxTrees.Single();
        var result = pp.RewriteFile(
            tree.GetText().ToString(), secondCompilation.GetSemanticModel(tree), stats, diags);

        Assert.False(result.Changed,
            "second pass must be a no-op, but it produced changes:\n" +
            (result.Changed ? result.OutputCode : string.Empty));
        Assert.Equal(0, stats.ThisCaptureMethodsRewritten);
        AssertOutputCompiles(first.Output);
    }

    // ---------- Emit 辅助 ----------

    private static Assembly EmitAssembly(string output)
    {
        var compilation = BuildCompilation(output);
        using var pe = new MemoryStream();
        var emit = compilation.Emit(pe);
        Assert.True(emit.Success,
            "emit failed:\n" + string.Join("\n", emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()))
            + "\n--- output ---\n" + output);
        pe.Seek(0, SeekOrigin.Begin);
        return Assembly.Load(pe.ToArray());
    }
}
