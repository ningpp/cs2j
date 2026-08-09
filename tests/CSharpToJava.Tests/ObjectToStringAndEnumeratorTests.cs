using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Iteration 23 regressions from the RegularExpressions test suite:
/// 1. C# object.ToString() on a type without a ToString override returns the type's
///    FullName (no "@hash"); the converter must route it through ObjectHelper.objectToString.
///    Regression: RegexReductionTests.GetRegexCodes -> code.ToString() where RegexCode
///    has no ToString override in release builds (its override is inside #if DEBUG).
/// 2. IEnumerator.Reset() must emit .reset() for compat enumerators (C# tests reset
///    between iteration passes). Regression: CaptureCollectionTests.GetEnumerator.
/// 3. "obj as T[]" from an object-typed source must use CSharpArray.tryAsArray so an
///    Object[] widened by erasure materializes a typed T[].
///    Regression: CaptureCollectionTests.DebuggerAttributeTests -> proxyItems as Capture[].
/// </summary>
public class ObjectToStringAndEnumeratorTests
{
    [Fact]
    public void ObjectToString_NoOverride_EmitsObjectHelper()
    {
        var result = Convert("""
class Foo {
    object o;
    string M() => o.ToString();
}
""");

        _ = result.GeneratedCode ?? "";
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("ObjectHelper.objectToString(o)", result.GeneratedCode);
        Assert.Contains("import io.github.ningpp.compat.ObjectHelper;", result.GeneratedCode);
    }

    [Fact]
    public void ToString_WithOverride_KeepsVirtualDispatch()
    {
        var result = Convert("""
class Bar {
    public override string ToString() => "bar";
    string M(Bar b) => b.ToString();
}
""");

        _ = result.GeneratedCode ?? "";
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("b.toString()", result.GeneratedCode);
        Assert.DoesNotContain("ObjectHelper", result.GeneratedCode);
    }

    [Fact]
    public void StringToString_KeepsToString()
    {
        var result = Convert("""
class Baz {
    string M(string s) => s.ToString();
}
""");

        _ = result.GeneratedCode ?? "";
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("s.toString()", result.GeneratedCode);
        Assert.DoesNotContain("ObjectHelper", result.GeneratedCode);
    }

    [Fact]
    public void IEnumeratorReset_EmitsResetCall()
    {
        var result = Convert("""
using System.Collections;
class C {
    void M(IEnumerator e) {
        while (e.MoveNext()) { }
        e.Reset();
    }
}
""");

        _ = result.GeneratedCode ?? "";
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain("reset unsupported", result.GeneratedCode);
        Assert.Contains("e.reset()", result.GeneratedCode);
    }

    [Fact]
    public void ObjectAsArray_EmitsTryAsArray()
    {
        var result = Convert("""
class D {
    Capture[] M(object o) => o as Capture[];
}
class Capture { }
""");

        _ = result.GeneratedCode ?? "";
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("CSharpArray.tryAsArray(o, Capture.class)", result.GeneratedCode);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
