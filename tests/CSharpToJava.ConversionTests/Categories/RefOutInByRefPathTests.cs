using CSharpToJava.Core.ClosureLift;
using CSharpToJava.Core.ForeachSource;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// ByRefPath (M3): C# input is first rewritten by the ref/out/in preprocessor,
/// then converted. Asserts the Java lands on the compat ByRef/RefStructByRef
/// family (J1-J4) — never the legacy IntHolder/ObjectHolder path (J6).
/// </summary>
public class RefOutInByRefPathTests : ConversionTestBase
{
    private static readonly string ByRefSource =
        File.ReadAllText(RepoFile("src/Cs2jRefOutIn/ByRef.cs"));

    private static readonly string ByRefNamespaceBody =
        ByRefSource.Substring(ByRefSource.IndexOf("namespace", StringComparison.Ordinal));

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;
        if (dir == null)
            throw new InvalidOperationException($"repo root with '{relative}' not found");
        return Path.Combine(dir.FullName, relative);
    }

    private static ConversionResult ConvertRewritten(string userSource, bool closureLift = false)
    {
        var tree = CSharpSyntaxTree.ParseText(userSource);
        var compilation = CSharpCompilation.Create("refout-pre",
            new[] { tree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);

        var result = new RefOutInRewriter().Rewrite(userSource, model);
        Assert.True(result.Changed, "expected the preprocessor to rewrite the input");

        // Support source is appended (namespace body only, usings stripped) so
        // Cs2jByRef.* resolves in the converter's semantic model exactly like the
        // released Cs2jRefOutIn project does, while all using directives stay
        // legal at the top of the combined file.
        var combined = "using System;\n" + result.OutputCode + "\n" + ByRefNamespaceBody;

        // Production convert-project order (C45 M5): ref-out-in emits ByRef
        // accessor proxies whose setter lambda mutably captures the aliased
        // local; closure-lift, anchored right after ref-out-in, must lift that
        // capture into a display class before the direct Java converter runs.
        if (closureLift)
        {
            var lift = new ClosureLiftPreprocessor().Preprocess(combined);
            Assert.True(lift.Changed,
                "expected closure-lift to lift the ByRef accessor captures\n" +
                string.Join("\n", lift.Diagnostics.Select(d => d.ToString())));
            combined = lift.OutputCode;
        }

        var converted = Convert(combined);
        if (!converted.Success)
        {
            Assert.True(converted.Success,
                "Conversion failed.\n--- Rewritten C# ---\n" + result.OutputCode +
                "\n--- Diagnostics ---\n" + string.Join("\n", converted.Diagnostics));
        }
        return converted;
    }

    private static readonly MetadataReference[] ChainMetadataReferences =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Memory<>).Assembly.Location),
    };

    /// <summary>
    /// C40 M3 chain: foreach-source lowers a ref foreach, then C01 binds the
    /// ref local to a ByRef-family proxy; the rewritten C# (plus the support
    /// namespace body) is converted to Java.
    /// </summary>
    private static ConversionResult ConvertForeachRewritten(string userSource)
    {
        var fsTree = CSharpSyntaxTree.ParseText(userSource);
        var fsCompilation = CSharpCompilation.Create("foreach-m3-pre",
            new[] { fsTree },
            ChainMetadataReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var fsResult = new ForeachSourcePreprocessor().Preprocess(
            userSource, fsCompilation.GetSemanticModel(fsTree), new ForeachSourceOptions());
        Assert.True(fsResult.Changed,
            "expected foreach-source to rewrite the input\n" +
            string.Join("\n", fsResult.Diagnostics));

        var c01Tree = CSharpSyntaxTree.ParseText(fsResult.OutputCode);
        var c01Compilation = CSharpCompilation.Create("foreach-m3-c01",
            new[] { c01Tree },
            ChainMetadataReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var c01Result = new RefOutInRewriter().Rewrite(
            fsResult.OutputCode, c01Compilation.GetSemanticModel(c01Tree));

        var combined = "using System;\n" + c01Result.OutputCode + "\n" + ByRefNamespaceBody;
        var converted = Convert(combined);
        if (!converted.Success)
        {
            Assert.True(converted.Success,
                "Conversion failed.\n--- Foreach-source output ---\n" + fsResult.OutputCode +
                "\n--- C01 output ---\n" + c01Result.OutputCode +
                "\n--- Diagnostics ---\n" + string.Join("\n", converted.Diagnostics));
        }
        return converted;
    }

    [SlowE2EFact(7.9)]
    public void M3_RefForeach_SpanChain_MapsToByRefSpanFactory()
    {
        var result = ConvertForeachRewritten(@"
using System;
public class Calc
{
    public static void Double(Span<int> values)
    {
        foreach (ref int x in values) { x *= 2; }
    }
}");
        AssertSuccess(result);
        // ScopedByRef<T>.Create(span, i) maps to the existing compat
        // ByRef.create(Span, int) factory (get/set closure lives in the
        // Java compat library); Span.Length -> getLength().
        AssertJavaContains(result, "ByRef.create(__cs2jFrSrc, __cs2jFrI)", "span/index factory");
        AssertJavaContains(result, ".getLength()", "Span length accessor");
        AssertJavaContains(result, "x.setValue(x.getValue() * 2)", "write through holder");
        AssertJavaDoesNotContain(result, "ScopedByRef");
        AssertJavaDoesNotContain(result, "IntHolder");
    }

    [Fact]
    public void M3_RefReadonlyForeach_ReadOnlySpanChain_MapsToReadOnlyByRef()
    {
        var result = ConvertForeachRewritten(@"
using System;
public class Calc
{
    public static int Sum(ReadOnlySpan<int> values)
    {
        int sum = 0;
        foreach (ref readonly int x in values) { sum += x; }
        return sum;
    }
}");
        AssertSuccess(result);
        AssertJavaContains(result,
            "ReadOnlyByRef.create(__cs2jFrSrc, __cs2jFrI)", "readonly span/index factory");
        AssertJavaContains(result, "sum += x.getValue()", "read-only holder read");
        AssertJavaDoesNotContain(result, "ReadOnlyScopedByRef");
    }

    [Fact]
    public void M3_RefForeach_ArrayChain_MapsToByRefArrayFactory()
    {
        // C# source cannot legally spell `foreach (ref int x in array)`
        // (CS1510), but foreach-source lowers it defensively and C01 binds
        // the array element alias to ByRef.Create(array, index).
        var result = ConvertForeachRewritten(@"
public class Calc
{
    public static void Double(int[] values)
    {
        foreach (ref int x in values) { x *= 2; }
    }
}");
        AssertSuccess(result);
        AssertJavaContains(result, "ByRef.create(__cs2jFrSrc, __cs2jFrI)", "array/index factory");
        AssertJavaContains(result, "doubleValue(int[] values)", "array parameter kept");
    }

    [Fact]
    public void M3_Foreach_DuckEnumeratorWithDispose_MapsToMoveNextGetCurrentTryFinally()
    {
        var result = ConvertForeachRewritten(@"
using System;
public class Calc
{
    public struct En : IDisposable
    {
        private int[] _a;
        private int _i;
        public En(int[] a) { _a = a; _i = -1; }
        public bool MoveNext() { ++_i; return _i < _a.Length; }
        public int Current { get { return _a[_i]; } }
        public void Dispose() { }
    }
    public sealed class Col
    {
        private readonly int[] _a;
        public Col(int[] a) { _a = a; }
        public En GetEnumerator() { return new En(_a); }
    }
    public static int Sum(Col c)
    {
        int sum = 0;
        foreach (int x in c) { sum += x; }
        return sum;
    }
}");
        AssertSuccess(result);
        AssertJavaContains(result, ".moveNext()", "duck MoveNext");
        AssertJavaContains(result, ".getCurrent()", "duck Current");
        AssertJavaContains(result, "finally", "Dispose try/finally");
        AssertJavaContains(result, ".close()", "duck Dispose normalized to AutoCloseable.close in finally");
    }

    [Fact]
    public void UnmappedCs2jByRefForm_FailsFast_CS2J3320()
    {
        var result = Convert(@"
namespace Cs2jByRef { public class Raw { public int x; } }
public class Calc
{
    public void Direct(Cs2jByRef.Raw r) { var y = r.x; }
}");
        Assert.False(result.Success, "hand-written Cs2jByRef types must not convert");
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("CS2J3320"));
    }

    [Fact]
    public void InternalRefCall_ProducesJavaByRefLambda()
    {
        // C45 M5: the setter lambda emitted by ref-out-in (v => x = v) mutably
        // captures the local, so closure-lift runs next (production order) and
        // moves x onto a display class; the converter binds the accessor pair
        // to the display class methods as ByRef.create(getter, setter).
        var result = ConvertRewritten(@"
public class Calc
{
    public void Bump(ref int v) { v = v + 1; }
    public void Use()
    {
        int x = 1;
        Bump(ref x);
    }
}", closureLift: true);
        AssertSuccess(result);
        AssertJavaContains(result, "ByRef.create(", "factory call");
        AssertJavaContains(result, "::lambda_0", "getter accessor bound to the display class");
        AssertJavaContains(result, "::lambda_1", "setter accessor bound to the display class");
        AssertJavaContains(result, "_cs2jCap_x = v", "setter writes through the lifted capture");
        AssertJavaContains(result, "v.getValue()", "body slot read");
        AssertJavaDoesNotContain(result, "IntHolder");
        AssertJavaDoesNotContain(result, "ObjectHolder");
    }

    [Fact]
    public void OutSlot_ReadBackUsesGetValue()
    {
        var result = ConvertRewritten(@"
public class Calc
{
    public bool TryGet(out int r) { r = 42; return true; }
    public int Use()
    {
        if (TryGet(out var v)) { return v; }
        return 0;
    }
}");
        AssertSuccess(result);
        AssertJavaContains(result, "ByRef.create()", "no-seed slot");
        // 值类型 out 拷回走 readbackInt：泛型被调方失败路径在擦除下只能写 null，
        // helper 把 null 映射回 default(int)，等价 C# out 合约（C# 侧直接透传）。
        AssertJavaContains(result, "readbackInt(", "null→default(int) slot read-back");
    }

    [Fact]
    public void OutSlot_GenericCalleeFailurePath_ReadBackToleratesErasureNull()
    {
        // regex-sdk: `caps.TryGetValue(i, out i)` — TryGetValue<T> 的失败路径
        // `value = default` 在 Java 泛型擦除下只能 setValue(null)，旧的
        // `i = __out.getValue()` 无条件解箱直接 NPE（35× GetGroupNamesTests 簇）。
        // 值类型拷回必须走 readbackX，null → default(T)。
        var result = ConvertRewritten(@"
public class Calc
{
    public bool TryGetValue(int key, out int value) { value = default; return false; }
    public string Use(int i)
    {
        return !TryGetValue(i, out i) ? string.Empty : i.ToString();
    }
}");
        AssertSuccess(result);
        AssertJavaContains(result, "ByRef.readbackInt(", "value-type out readback maps erasure-null to default(int)");
        AssertJavaDoesNotContain(result, "= __out0.getValue()");
    }

    [Fact]
    public void OutSlot_ReferenceTypeReadBack_KeepsPlainGetValue()
    {
        // 引用类型 out 拷回保持原样：null 是合法的 C# 值，不得被 helper 吞掉。
        var result = ConvertRewritten(@"
public class Calc
{
    public bool TryGet(out string r) { r = null; return false; }
    public string Use()
    {
        if (TryGet(out var s)) { return s; }
        return string.Empty;
    }
}");
        AssertSuccess(result);
        AssertJavaContains(result, "= __out0.getValue()", "plain reference-type read-back");
        AssertJavaDoesNotContain(result, "readback");
    }

    [Fact]
    public void OutSlot_CharTypeReadBack_UsesReadbackChar()
    {
        var result = ConvertRewritten(@"
public class Calc
{
    public bool TryGet(out char c) { c = default; return false; }
    public bool Use()
    {
        if (TryGet(out var ch)) { return ch != '\0'; }
        return false;
    }
}");
        AssertSuccess(result);
        AssertJavaContains(result, "readbackChar(", "char out readback maps erasure-null to '\\0'");
    }

    [Fact]
    public void ByRefValue_WriteUsesSetValue()
    {
        var result = Convert(@"
namespace Cs2jByRef { public abstract class ByRef<T> { public abstract T Value { get; set; } } }
public class Calc
{
    public void Direct(Cs2jByRef.ByRef<int> v) { v.Value = 3; }
    public int Read(Cs2jByRef.ByRef<int> v) { return v.Value; }
}");
        AssertSuccess(result);
        AssertJavaContains(result, "setValue(3)", "property write convention");
        AssertJavaContains(result, "getValue()", "property read convention");
    }
}
