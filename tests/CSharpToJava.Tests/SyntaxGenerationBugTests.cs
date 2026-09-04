using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for common Java syntax generation bugs found in cs-data-common-java.
/// These are NOT generics errors — they are structural/syntax generation issues.
/// </summary>
public class SyntaxGenerationBugTests
{
    // ── Bug 1: global:: alias qualifier not stripped ──

    [Fact]
    public void GlobalAlias_ParameterType_QualifierStripped()
    {
        var result = Convert(@"
using System.Xml;
public class XmlHelper
{
    public void Read(XmlReader reader) { }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.DoesNotContain("global::", code, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalAlias_NewExpression_QualifierStripped()
    {
        var result = Convert(@"
using System.IO;
public class StreamHelper
{
    public void Create()
    {
        var writer = new StreamWriter();
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.DoesNotContain("new global::", code, StringComparison.Ordinal);
    }

    // ── Bug 2: ternary expression wrapped in extra parens ──

    [Fact]
    public void TernaryStatement_NotWrappedInExtraParens()
    {
        var result = Convert(@"
public class EventSource
{
    private System.Action _handler;
    public void Fire()
    {
        (_handler != null ? _handler() : null);
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        // The ternary should NOT have outer parens when used as statement
        Assert.DoesNotContain("( _handler != null", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TernaryStatement_AssignmentFormWorks()
    {
        var result = Convert(@"
public class EventSource
{
    private System.Action _handler;
    private object _lastResult;
    public void FireAndSet()
    {
        _lastResult = (_handler != null ? _handler() : null);
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        // Assignment form with parens is fine
        Assert.Contains("_lastResult =", code);
    }

    // ── Bug 3: method name ToXArray not mangled ──

    [Fact]
    public void MethodName_ByteArray_NotMangled()
    {
        var result = Convert(@"
public class ByteHelper
{
    public static byte[] ConvertToBytes(System.ReadOnlySpan<byte> data) { return data.ToArray(); }
    public byte[] GetBytes() { return new byte[0]; }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        // "Bytes" or "ByteArray" should not become "Byte[]"
        Assert.DoesNotContain("convertToByte[]", code, StringComparison.Ordinal);
        Assert.DoesNotContain("getByte[]", code, StringComparison.Ordinal);
    }

    // ── Bug 4: Java floating-point literal format ──

    [Fact]
    public void FloatLiteral_ScientificNotation_ValidJava()
    {
        var result = Convert(@"
public class SciMath
{
    public double BigValue() { return 1E11; }
    public double DecimalExp() { return 5.0E-3; }
    public double OnePointZeroEpow() { return 1E201; }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        // Scientific notation should use Java-compatible format (no ".0" suffix)
        Assert.DoesNotContain("1E11.0", code, StringComparison.Ordinal);
        Assert.DoesNotContain("1E201.0", code, StringComparison.Ordinal);
    }

    // ── Bug 5: conditional expression inside if (no syntax error from parens) ──

    [Fact]
    public void TernaryInCondition_CompilesWithParens()
    {
        // The transformer wraps ternary in parens, and C# source may add its own.
        // This produces redundant but syntactically valid Java.
        var result = Convert(@"
public class ParenTest
{
    public bool Check(object[] a, object[] b)
    {
        if (((a[0] != null ? a[0].Equals(b[0]) : b[0] != null))) return false;
        return true;
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        // The ternary should be wrapped (redundant parens are OK in Java)
        Assert.Contains("a[0] != null", code);
        Assert.Contains("b[0] != null", code);
        // Verify both fields are referenced
        Assert.Contains("a[0]", code);
        Assert.Contains("b[0]", code);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
