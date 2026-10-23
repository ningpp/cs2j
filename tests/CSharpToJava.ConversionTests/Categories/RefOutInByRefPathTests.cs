using System.Reflection;
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

    [Fact]
    public void UnsafePointerRefParameters_KeepNativeRefSyntax_WithoutByRefGeneric()
    {
        // xml-tiny: `internal unsafe void EncodeChar(ref char* pSrc, char* pSrcEnd, ref byte* pDst)`
        // — pointer types cannot be generic type arguments (CS0306), so ref/out
        // pointer parameters must keep the native C# ref form at both the
        // declaration and the by-ref call site.
        var source = @"
public unsafe class Calc
{
    private char* _src;
    private byte* _dst;

    internal unsafe void EncodeChar(ref char* pSrc, char* pSrcEnd, ref byte* pDst)
    {
        int ch = *pSrc;
        pSrc++;
        pDst++;
    }

    internal unsafe void Reset(out char* p)
    {
        p = null;
    }

    internal unsafe void Call()
    {
        char* p = _src;
        byte* q = _dst;
        EncodeChar(ref p, p, ref q);
        Reset(out p);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("refout-ptr",
            new[] { tree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var result = new RefOutInRewriter().Rewrite(source, compilation.GetSemanticModel(tree));

        Assert.True(result.Changed, "expected the preprocessor to rewrite the input");
        Assert.DoesNotContain("ByRef<", result.OutputCode);
        Assert.Contains("ref char*", result.OutputCode);
        Assert.Contains("ref byte*", result.OutputCode);
        Assert.Contains("out char*", result.OutputCode);
        Assert.Contains("EncodeChar(ref p, p, ref q)", result.OutputCode);
        Assert.Contains("Reset(out p)", result.OutputCode);

        // The rewritten source must itself compile: keeping native pointer refs
        // is what removes CS0306 (pointer as generic type argument) from the
        // pass output.
        var rewrittenTree = CSharpSyntaxTree.ParseText(result.OutputCode);
        var rewrittenCompilation = CSharpCompilation.Create("refout-ptr-out",
            new[] { rewrittenTree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = rewrittenCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Rewritten source must compile.\n--- Errors ---\n" +
            string.Join("\n", errors) + "\n--- Rewritten ---\n" + result.OutputCode);

        // Native pointer ref/out params are the intended output, not residual
        // debt the post-verifier should reject.
        var violations = RefOutInPostVerifier.Verify(new[] { rewrittenTree }, rewrittenCompilation);
        Assert.Empty(violations);
    }

    /// <summary>
    /// Minimal ByRef surface needed to compile the rewritten struct snippet.
    /// The runtime half lives in src/Cs2jRefOutIn/ByRef.cs; the test only needs
    /// the factory/read-back signatures the lifter emits.
    /// </summary>
    private const string StructByRefStub = @"
namespace Cs2jByRef
{
    public abstract class ByRef<T>
    {
        public abstract T Value { get; set; }
        public static ByRef<T> Create() => null;
        public static ByRef<T> Create(T initial) => null;
        public static ByRef<T> Create(System.Func<T> getter, System.Action<T> setter) => null;
        public static int ReadbackInt(ByRef<int> slot) => slot.Value;
    }
}";

    private static CSharpCompilation CompileWithByRefStub(string source, string assemblyName)
    {
        var tree = CSharpSyntaxTree.ParseText(source + StructByRefStub);
        return CSharpCompilation.Create(assemblyName,
            new[] { tree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
    }

    [Fact]
    public void StructInstanceFieldRefArguments_UseCopyInOutSlot_WithoutThisCapturingLambda()
    {
        // xml-tiny XsdDateTime.Parser: a struct instance method calling
        // `Parse4Dig(start, ref year)` used to emit
        // `ByRef<int>.Create(() => year, v => year = v)`. A lambda inside a
        // struct cannot access instance members of `this` (CS1673), so a
        // field target must be read into a local ByRef slot before the call
        // and written back after it (same synchronous copy-in/copy-out shape
        // the shadow-frame machinery uses for value-typed holders).
        var source = @"
public struct Acc
{
    private int _total;
    private int _count;

    private bool TryParse(string s, int pos, ref int value)
    {
        value = pos + s.Length;
        return true;
    }

    private bool TryCount(out int n)
    {
        n = 1;
        return true;
    }

    public int Add(string s)
    {
        int pos = 0;
        if (TryParse(s, pos, ref _total))
        {
            TryCount(out _count);
        }
        return _total + _count;
    }
}";
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("refout-struct-field",
            new[] { tree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        var result = new RefOutInRewriter().Rewrite(source, compilation.GetSemanticModel(tree));
        Assert.True(result.Changed, "expected the preprocessor to rewrite the input");

        // No getter/setter closure over a `this` field may survive.
        Assert.DoesNotContain("=> _total", result.OutputCode);
        Assert.DoesNotContain("=> _count", result.OutputCode);
        // Copy-in from the field + write-back after the call.
        Assert.Contains("ByRef<int>.Create(_total)", result.OutputCode);
        Assert.Contains("_total = __ref", result.OutputCode);

        var rewrittenTree = CSharpSyntaxTree.ParseText(result.OutputCode + StructByRefStub);
        var rewrittenCompilation = CSharpCompilation.Create("refout-struct-field-out",
            new[] { rewrittenTree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = rewrittenCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Rewritten source must compile (no CS1673).\n--- Errors ---\n" +
            string.Join("\n", errors) + "\n--- Rewritten ---\n" + result.OutputCode);

        // Copy-in/copy-out slots are the intended output, not residual debt.
        var violations = RefOutInPostVerifier.Verify(new[] { rewrittenTree }, rewrittenCompilation);
        Assert.Empty(violations);
    }

    [Fact]
    public void ClassInstanceFieldRefArgument_KeepsAccessorLambda()
    {
        // The copy-in/copy-out rewrite is scoped to value-type receivers:
        // `this` of a class is safely captureable, so the alias-through-call
        // accessor shape (writes visible to the callee immediately) must stay.
        var source = @"
public class Calc
{
    private int _value;
    private void Bump(ref int v) { v = v + 1; }
    public void Use() { Bump(ref _value); }
}";
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("refout-class-field",
            new[] { tree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var result = new RefOutInRewriter().Rewrite(source, compilation.GetSemanticModel(tree));

        Assert.True(result.Changed, "expected the preprocessor to rewrite the input");
        Assert.Contains("() => _value", result.OutputCode);
        Assert.Contains("v => _value = v", result.OutputCode);

        var rewritten = CompileWithByRefStub(result.OutputCode, "refout-class-field-out");
        var errors = rewritten.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Rewritten source must compile.\n--- Errors ---\n" +
            string.Join("\n", errors) + "\n--- Rewritten ---\n" + result.OutputCode);
    }

    /// <summary>
    /// Fully functional ByRef surface for the execution assertions below: the
    /// emitted assembly must actually run (unlike <see cref="StructByRefStub"/>,
    /// whose factories return null).
    /// </summary>
    private const string ExecutableByRefStub = @"
namespace Cs2jByRef
{
    public class ByRef<T>
    {
        private T _value;
        public ByRef() { }
        public ByRef(T value) { _value = value; }
        public virtual T Value
        {
            get => _value;
            set => _value = value;
        }
        public static ByRef<T> Create() => new ByRef<T>();
        public static ByRef<T> Create(T initial) => new ByRef<T>(initial);
        public static ByRef<T> Create(System.Func<T> getter, System.Action<T> setter)
            => new Accessor<T>(getter, setter);
        public static int ReadbackInt(ByRef<int> slot) => slot.Value;
        private sealed class Accessor<T> : ByRef<T>
        {
            private readonly System.Func<T> _getter;
            private readonly System.Action<T> _setter;
            public Accessor(System.Func<T> getter, System.Action<T> setter)
            {
                _getter = getter;
                _setter = setter;
            }
            public override T Value
            {
                get => _getter();
                set => _setter(value);
            }
        }
    }
}";

    [Fact]
    public void HolderForwardedToExternalNativeOutOrRef_Compiles_AndCopiesBack()
    {
        // xml-tiny XmlConvert.TryToChar: `internal static Exception TryToChar(string s, out char result)`
        // forwards its (rewritten) ByRef<char> holder to the BCL
        // `Char.TryParse(string, out char)`. `out result.Value` is CS0206 — a
        // property is not a variable. The call site must copy the holder into a
        // temp local, pass the temp as the native `out`/`ref` argument, then
        // write it back into the holder after the call (XmlConvert.TryToUri
        // hits the same shape with the reference-typed `out Uri result`, and
        // XdrBuilder.ParseInteger with `ref uint n`).
        var source = @"
public class Calc
{
    internal static bool TryGet(string s, out int value)
    {
        if (!int.TryParse(s, out value))
        {
            return false;
        }
        return true;
    }

    internal static void Grow(ref int[] items)
    {
        System.Array.Resize(ref items, items.Length + 1);
    }

    public static int Use(string s)
    {
        if (TryGet(s, out int v)) { return v; }
        return -1;
    }

    public static int UseGrow()
    {
        int[] items = new int[2];
        Grow(ref items);
        return items.Length;
    }
}";
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("refout-native-ref",
            new[] { tree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        var result = new RefOutInRewriter().Rewrite(source, compilation.GetSemanticModel(tree));
        Assert.True(result.Changed, "expected the preprocessor to rewrite the input");

        // The holder's Value must never be forwarded as a native ref/out target.
        Assert.DoesNotContain("out value.Value", result.OutputCode);
        Assert.DoesNotContain("ref items.Value", result.OutputCode);
        // Temp local passed to the callee, then written back into the holder.
        Assert.Contains("value.Value = __out", result.OutputCode);
        Assert.Contains("items.Value = __ref", result.OutputCode);

        var rewrittenTree = CSharpSyntaxTree.ParseText(result.OutputCode + ExecutableByRefStub);
        var rewrittenCompilation = CSharpCompilation.Create("refout-native-ref-out",
            new[] { rewrittenTree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = rewrittenCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Rewritten source must compile (no CS0206).\n--- Errors ---\n" +
            string.Join("\n", errors) + "\n--- Rewritten ---\n" + result.OutputCode);

        // Native ref/out arguments to external APIs are the intended output.
        var violations = RefOutInPostVerifier.Verify(new[] { rewrittenTree }, rewrittenCompilation);
        Assert.Empty(violations);

        // Semantics: the copy-back must surface the callee's writes to the
        // caller's holder exactly like a direct alias would.
        using var stream = new MemoryStream();
        var emit = rewrittenCompilation.Emit(stream);
        Assert.True(emit.Success,
            "Emit failed.\n--- Diagnostics ---\n" + string.Join("\n", emit.Diagnostics));
        var assembly = Assembly.Load(stream.ToArray());
        var type = assembly.GetType("Calc")!;
        var use = type.GetMethod("Use")!;
        var useGrow = type.GetMethod("UseGrow")!;
        Assert.Equal(123, use.Invoke(null, new object[] { "123" }));
        Assert.Equal(-1, use.Invoke(null, new object[] { "abc" }));
        Assert.Equal(3, useGrow.Invoke(null, null));
    }
}
