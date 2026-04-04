using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.Transformers.Expression;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for Phase C: Expression Transformer IR foundation.
/// Covers IIRExpressionTransformer interface, TransformToIR facade method,
/// LiteralExpressionTransformer IR output, and TransformStatementsToIR bridge.
/// </summary>
public class PhaseCExpressionIRTests
{
    private static string ConvertCode(string code)
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = code,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        return result.GeneratedCode;
    }

    // ── IIRExpressionTransformer interface ──────────────────────────

    [Fact]
    public void LiteralTransformer_ImplementsIIRExpressionTransformer()
    {
        Assert.IsAssignableFrom<IIRExpressionTransformer>(LiteralExpressionTransformer.Instance);
    }

    [Fact]
    public void LiteralTransformer_ImplementsIExpressionTransformer()
    {
        Assert.IsAssignableFrom<IExpressionTransformer>(LiteralExpressionTransformer.Instance);
    }

    // ── TransformToIR end-to-end ───────────────────────────────────

    [Fact]
    public void TransformToIR_NumericLiteral()
    {
        var result = ConvertCode("class T { void M() { int x = 42; } }");
        Assert.Contains("42", result);
    }

    [Fact]
    public void TransformToIR_StringLiteral()
    {
        var result = ConvertCode("class T { void M() { string s = \"hello\"; } }");
        Assert.Contains("\"hello\"", result);
    }

    [Fact]
    public void Facade_TransformToIR_MultipleDeclarations()
    {
        var result = ConvertCode(@"
class TestClass {
    void TestMethod() {
        bool flag = true;
        int count = 100;
        string name = ""test"";
    }
}");
        Assert.Contains("true", result);
        Assert.Contains("100", result);
        Assert.Contains("\"test\"", result);
    }

    // ── TransformStatementsToIR bridge ─────────────────────────────

    [Fact]
    public void TransformBlockToStructuredBody_ProducesValidJava()
    {
        var result = ConvertCode(@"
class TestClass {
    void Method() {
        int x = 1;
        string s = ""hello"";
        bool b = false;
    }
}");
        Assert.Contains("int x = 1", result);
        Assert.Contains("String s = \"hello\"", result);
        Assert.Contains("boolean b = false", result);
    }

    [Fact]
    public void TransformBlockToStructuredBody_PreservesStatementOrder()
    {
        var result = ConvertCode(@"
class TestClass {
    void Method() {
        int a = 1;
        int b = 2;
        int c = a + b;
    }
}");
        var posA = result.IndexOf("int a = 1");
        var posB = result.IndexOf("int b = 2");
        var posC = result.IndexOf("int c = a + b");
        Assert.True(posA < posB, "a should come before b");
        Assert.True(posB < posC, "b should come before c");
    }

    // ── Structured IR node tests ────────────────────────────────────

    [Fact]
    public void JavaLiteralExpression_ToInlineString_ReturnsValue()
    {
        var lit = new JavaLiteralExpression("42");
        Assert.Equal("42", lit.ToInlineString());
    }

    [Fact]
    public void JavaLiteralExpression_NullLiteral()
    {
        var lit = new JavaLiteralExpression("null");
        Assert.Equal("null", lit.ToInlineString());
    }

    [Fact]
    public void JavaRawExpression_WithResolvedType_PreservesType()
    {
        var expr = new JavaRawExpression("getValue()", "int");
        Assert.Equal("int", expr.ResolvedType);
        Assert.Equal("getValue()", expr.ToInlineString());
    }

    // ── Phase C integration: complex expressions still work ────────

    [Fact]
    public void ComplexExpression_StillWorksAsRawFallback()
    {
        var result = ConvertCode(@"
using System.Collections.Generic;
class TestClass {
    void Method() {
        var list = new List<int>();
        list.Add(42);
        int count = list.Count;
    }
}");
        Assert.Contains("new ArrayList<", result);
        Assert.Contains(".add(42)", result);
    }

    [Fact]
    public void MethodWithReturnValue_StillProducesValidOutput()
    {
        var result = ConvertCode(@"
class TestClass {
    int GetValue() {
        return 42;
    }
}");
        Assert.Contains("return 42", result);
    }
}
