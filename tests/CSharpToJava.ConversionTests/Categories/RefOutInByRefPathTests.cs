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

    private static ConversionResult ConvertRewritten(string userSource)
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

    [Fact]
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
        var result = ConvertRewritten(@"
public class Calc
{
    public void Bump(ref int v) { v = v + 1; }
    public void Use()
    {
        int x = 1;
        Bump(ref x);
    }
}");
        AssertSuccess(result);
        AssertJavaContains(result, "ByRef.create(", "factory call");
        AssertJavaContains(result, "() -> x", "getter lambda");
        AssertJavaContains(result, "v -> x = v", "setter lambda");
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
        AssertJavaContains(result, ".getValue()", "slot read-back");
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
