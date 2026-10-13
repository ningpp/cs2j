using System.Reflection;
using CSharpToJava.Core.StructValueSemantics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// SV042-a 根因修复单测：含指针类型实例字段的普通可变 struct 不再被一刀切判死。
/// 语义依据：指针字段持有的是一个地址「值」，C# 每次 struct 值拷贝逐位复制该地址，
/// 防御性 clone 的逐字段拷贝构造（this.p = other.p）与之恒等，与引用类型/数组字段
/// 同属浅拷贝族（设计 §4.4：副本共享数组本体与引用目标本就是 C# 值复制语义）。
/// XmlCharType 实证：byte* 指向只读清单资源映射表，全工程仅经指针读取，值拷贝
/// （局部/类实例字段/静态字段）全部别名同一不可变目标，浅拷贝完全保持语义。
/// 覆盖：byte*/char*/void*/多字段/泛型 unmanaged/嵌套结构、Emit 运行时值语义、
/// 与 SV040-d 持有者（005fb40a）/SV041 持有者（86964b36）组合、真别名逃逸
/// （成员体 fixed/stackalloc/取址本字段或本类型局部）仍报 SV042、幂等。
/// </summary>
public class StructValueSemanticsPointerFieldTests
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
            "svs-pointer-field-test",
            trees,
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
    }

    private sealed class PlanRun
    {
        public required StructValueSemanticsPreprocessor Preprocessor { get; init; }
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
    }

    private static void AssertNoSvErrors(PlanRun run) =>
        Assert.DoesNotContain(run.Diagnostics, d =>
            d.Severity == StructValueSemanticsSeverity.Error
            && d.Code.ToString().StartsWith("SV", StringComparison.Ordinal));

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

    // ---------- 指针字段：shim 浅拷贝，plan 激活，编译通过 ----------

    [Fact]
    public void BytePointerField_ReadHandle_ShallowCopyCtor()
    {
        // XmlCharType 同族：byte* 只读句柄 + 经指针索引读取。
        var run = Analyze("""
            namespace Lib;
            internal unsafe struct CharTable
            {
                internal byte* P;
                public CharTable(byte* p) { P = p; }
                public byte At(char c) => P[c];
            }
            """);

        var plan = Plan(run, "Lib.CharTable");
        Assert.True(plan.IsActive);
        Assert.NotNull(plan.GeneratedCopyConstructor);
        Assert.NotNull(plan.GeneratedCloneMethod);
        Assert.Contains("P = other.P;", run.Output);
        AssertOutputCompiles(run.Output);
        AssertNoSvErrors(run);
    }

    [Fact]
    public void CharPointerField_ReadIndex_ShallowCopyCtor()
    {
        var run = Analyze("""
            namespace Lib;
            public unsafe struct Chars
            {
                public char* C;
                public char First() => C[0];
            }
            """);

        Assert.True(Plan(run, "Lib.Chars").IsActive);
        Assert.Contains("C = other.C;", run.Output);
        AssertOutputCompiles(run.Output);
        AssertNoSvErrors(run);
    }

    [Fact]
    public void VoidPointerField_ShallowCopyCtor()
    {
        // void* 不可索引，但作为地址值字段同样逐位复制。
        var run = Analyze("""
            namespace Lib;
            public unsafe struct Handle
            {
                public void* P;
                public bool Has => P != null;
            }
            """);

        Assert.True(Plan(run, "Lib.Handle").IsActive);
        Assert.Contains("P = other.P;", run.Output);
        AssertOutputCompiles(run.Output);
        AssertNoSvErrors(run);
    }

    [Fact]
    public void MultiplePointerFields_AllCopiedShallow()
    {
        var run = Analyze("""
            namespace Lib;
            public unsafe struct Multi
            {
                public byte* B;
                public char* C;
                public int* I;
                public void* V;
            }
            """);

        var output = run.Output;
        Assert.True(Plan(run, "Lib.Multi").IsActive);
        Assert.Contains("B = other.B;", output);
        Assert.Contains("C = other.C;", output);
        Assert.Contains("I = other.I;", output);
        Assert.Contains("V = other.V;", output);
        AssertOutputCompiles(output);
        AssertNoSvErrors(run);
    }

    [Fact]
    public void GenericUnmanagedStruct_WithPointerField_ShimCompiles()
    {
        var run = Analyze("""
            namespace Lib;
            public unsafe struct Box<T> where T : unmanaged
            {
                public T* P;
                public Box(T* p) { P = p; }
                public T At(int i) => P[i];
            }
            """);

        var plan = Assert.Single(run.Preprocessor.Plans
            .Where(p => p.StructShortName == "Box"));
        Assert.True(plan.IsActive);
        Assert.Contains("P = other.P;", run.Output);
        AssertOutputCompiles(run.Output);
        AssertNoSvErrors(run);
    }

    [Fact]
    public void NestedStructWithPointerField_OuterRecursivelyClonesInner()
    {
        var run = Analyze("""
            namespace Lib;
            public unsafe struct Inner
            {
                public byte* P;
                public byte At(int i) => P[i];
            }
            public struct Outer
            {
                public Inner In;
                public int X;
                public byte Get(int i) => In.At(i);
            }
            """);

        var inner = Plan(run, "Lib.Inner");
        var outer = Plan(run, "Lib.Outer");
        Assert.True(inner.IsActive);
        Assert.True(outer.IsActive);
        var output = Normalized(run.Output);
        Assert.Contains("P = other.P;", output);
        // 外层对内层（激活的残余可变 struct）递归 Clone，不浅套副本。
        Assert.Contains("In = other.In.Clone();", output);
        AssertOutputCompiles(run.Output);
        AssertNoSvErrors(run);
    }

    // ---------- Emit 运行时：clone 后指针别名共享只读目标、非指针字段隔离 ----------

    [Fact]
    public void Runtime_CloneSharesPointerTarget_AndIsolatesScalarField()
    {
        var run = Analyze("""
            namespace Lib;

            public unsafe struct Table
            {
                public int X;
                public byte* P;
                public byte At(int i) => P[i];
            }

            public unsafe static class Demo
            {
                public static byte ReadThroughClone(byte[] buf)
                {
                    Table t = default;
                    fixed (byte* p = buf)
                    {
                        t.P = p;
                        Table copy = t;
                        return copy.At(2);
                    }
                }

                public static int ScalarFieldIsolationAfterClone(byte[] buf)
                {
                    Table t = default;
                    fixed (byte* p = buf)
                    {
                        t.P = p;
                        t.X = 5;
                        Table copy = t;
                        copy.X = 9;
                        return t.X;
                    }
                }
            }
            """);

        AssertOutputCompiles(run.Output);
        Assert.Contains(".Clone()", Normalized(run.Output));
        AssertNoSvErrors(run);

        var assembly = EmitAssembly(run.Output);
        var demo = assembly.GetType("Lib.Demo")!;
        var buf = new byte[] { 1, 2, 7, 4 };

        var shared = demo.GetMethod("ReadThroughClone")!.Invoke(null, new object[] { buf });
        Assert.Equal((byte)7, shared);

        var originalX = demo.GetMethod("ScalarFieldIsolationAfterClone")!.Invoke(null, new object[] { buf });
        Assert.Equal(5, originalX);
    }

    // ---------- 与 005fb40a this 持有者机制组合 ----------

    [Fact]
    public void PointerField_ThisCapturingLambda_RoutedThroughHolderAndCompiles()
    {
        var run = Analyze("""
            namespace Lib;
            public unsafe struct Box
            {
                public byte* P;
                public int X;
                public byte Run()
                {
                    System.Func<byte> f = () => P[X];
                    X = 0;
                    return f();
                }
            }
            """);

        var plan = Plan(run);
        Assert.True(plan.IsActive);
        Assert.Single(plan.ThisCaptureRewrites);
        AssertOutputCompiles(run.Output);
        AssertNoSvErrors(run);
    }

    // ---------- 与 86964b36 整体 this 赋值持有者组合（Emit） ----------

    [Fact]
    public void Runtime_PointerField_WholeThisResetZeroesOnlyReceiverCopy()
    {
        var run = Analyze("""
            namespace Lib;

            public unsafe struct Box
            {
                public byte* P;
                public void Reset() { this = default; }
                public byte At(int i) => P[i];
            }

            public unsafe static class Demo
            {
                public static byte AssignThenReset(byte[] buf)
                {
                    Box a = default;
                    fixed (byte* p = buf)
                    {
                        a.P = p;
                        Box b = a;
                        b.Reset();
                        return a.At(2);
                    }
                }
            }
            """);

        AssertOutputCompiles(run.Output);
        AssertNoSvErrors(run);

        var assembly = EmitAssembly(run.Output);
        var demo = assembly.GetType("Lib.Demo")!;
        var result = demo.GetMethod("AssignThenReset")!
            .Invoke(null, new object[] { new byte[] { 1, 2, 7, 4 } });
        Assert.Equal((byte)7, result);
    }

    // ---------- 真别名逃逸：成员体跨 clone 存储身份边界，仍报 SV042 ----------

    [Fact]
    public void FixedStatementInMember_StaysSV042()
    {
        var run = Analyze("""
            namespace Lib;
            public unsafe struct Box
            {
                public byte* P;
                public byte Run()
                {
                    byte[] data = new byte[4];
                    fixed (byte* q = data)
                    {
                        q[0] = P[0];
                        return q[0];
                    }
                }
            }
            """);
        AssertInfeasible(run, StructValueSemanticsDiagnosticCode.SV042);
    }

    [Fact]
    public void StackAllocInMember_StaysSV042()
    {
        var run = Analyze("""
            namespace Lib;
            public unsafe struct Box
            {
                public byte* P;
                public byte Run()
                {
                    byte* local = stackalloc byte[4];
                    local[0] = 1;
                    return (byte)(local[0] + P[0]);
                }
            }
            """);
        AssertInfeasible(run, StructValueSemanticsDiagnosticCode.SV042);
    }

    [Fact]
    public void AddressOfPointerFieldSlot_StaysSV042()
    {
        // 取实例字段槽自身地址（byte**）：原生指针命名 struct 存储，跨过 clone 边界。
        var run = Analyze("""
            namespace Lib;
            public unsafe struct Box
            {
                public byte* P;
                public byte** Q;
                public void Capture() { Q = &P; }
            }
            """);
        AssertInfeasible(run, StructValueSemanticsDiagnosticCode.SV042);
    }

    [Fact]
    public void AddressOfSelfLocal_StaysSV042()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public unsafe void Run()
                {
                    Box local = default;
                    Box* p = &local;
                    p->X++;
                }
            }
            """);
        AssertInfeasible(run, StructValueSemanticsDiagnosticCode.SV042);
    }

    [Fact]
    public void SpanOfSelfParameter_StaysSV042()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public void Fill(System.Span<Box> xs) { xs[0] = this; }
            }
            """);
        AssertInfeasible(run, StructValueSemanticsDiagnosticCode.SV042);
    }

    // ---------- 幂等：二次运行零 diff ----------

    [Fact]
    public void PointerFieldRewrite_IsIdempotent()
    {
        var first = Analyze("""
            namespace Lib;
            public unsafe struct Box
            {
                public byte* P;
                public Box(byte* p) { P = p; }
                public byte At(int i) => P[i];
            }
            """);
        AssertOutputCompiles(first.Output);
        Assert.True(first.Changed);

        var secondCompilation = BuildCompilation(first.Output);
        var pp = new StructValueSemanticsPreprocessor();
        pp.BindCompilation(secondCompilation);
        var stats = new StructValueSemanticsStatistics();
        var diags = new List<StructValueSemanticsDiagnostic>();
        var tree = secondCompilation.SyntaxTrees.Single();
        var second = pp.RewriteFile(
            tree.GetText().ToString(), secondCompilation.GetSemanticModel(tree), stats, diags);

        Assert.False(second.Changed);
        AssertOutputCompiles(first.Output);
        AssertNoSvErrors(new PlanRun
        {
            Preprocessor = pp,
            Statistics = stats,
            Diagnostics = diags,
            Output = first.Output,
            Changed = false,
        });
    }
}
