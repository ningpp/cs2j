using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class JavaReservedWordTests
{
    [Fact]
    public void IsJavaKeyword_True_IsKeyword()
    {
        Assert.True(JavaNaming.IsJavaKeyword("true"));
    }

    [Fact]
    public void IsJavaKeyword_False_IsKeyword()
    {
        Assert.True(JavaNaming.IsJavaKeyword("false"));
    }

    [Fact]
    public void IsJavaKeyword_Null_IsKeyword()
    {
        Assert.True(JavaNaming.IsJavaKeyword("null"));
    }

    [Fact]
    public void EscapeJavaKeyword_True_EscapesToTrueValue()
    {
        Assert.Equal("trueValue", JavaNaming.EscapeJavaKeyword("true"));
    }

    [Fact]
    public void EscapeJavaKeyword_False_EscapesToFalseValue()
    {
        Assert.Equal("falseValue", JavaNaming.EscapeJavaKeyword("false"));
    }

    [Fact]
    public void EscapeJavaKeyword_Null_EscapesToNullValue()
    {
        Assert.Equal("nullValue", JavaNaming.EscapeJavaKeyword("null"));
    }

    [Fact]
    public void EscapeJavaKeyword_NonKeyword_Unchanged()
    {
        Assert.Equal("foo", JavaNaming.EscapeJavaKeyword("foo"));
    }

    [Fact]
    public void EscapeJavaKeyword_ExistingKeyword_Escapes()
    {
        Assert.Equal("assertValue", JavaNaming.EscapeJavaKeyword("assert"));
    }

    [Fact]
    public void OperatorTrue_ConvertedToIsTrue()
    {
        var result = Convert(@"
public class Foo
{
    public static bool operator true(Foo f) => f.Value > 0;
    public static bool operator false(Foo f) => f.Value <= 0;
    public int Value { get; }
}");
        Assert.True(result.Success);
        Assert.Contains("isTrue", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("isFalse", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("op_True", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("op_False", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void OperatorTrue_UsedInIfCondition_ResolvesToIsTrue()
    {
        var result = Convert(@"
public class Foo
{
    public int Value { get; }
    public static bool operator true(Foo f) => f.Value > 0;
    public static bool operator false(Foo f) => f.Value <= 0;
    public static void Test(Foo f)
    {
        if (f) { System.Console.WriteLine(""positive""); }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("isTrue", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("op_True", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpressionTransformerHelpers_IsJavaKeyword_True_IsKeyword()
    {
        Assert.True(CSharpToJava.Core.Transformers.Expression.Utilities.ExpressionTransformerHelpers.IsJavaKeyword("true"));
    }

    [Fact]
    public void ExpressionTransformerHelpers_IsJavaKeyword_False_IsKeyword()
    {
        Assert.True(CSharpToJava.Core.Transformers.Expression.Utilities.ExpressionTransformerHelpers.IsJavaKeyword("false"));
    }

    [Fact]
    public void ExpressionTransformerHelpers_IsJavaKeyword_Null_IsKeyword()
    {
        Assert.True(CSharpToJava.Core.Transformers.Expression.Utilities.ExpressionTransformerHelpers.IsJavaKeyword("null"));
    }

    [Fact]
    public void MethodNamedTrue_ConvertedToTrueValue()
    {
        var result = Convert(@"
public class Sample
{
    public bool True() => true;
}");
        Assert.True(result.Success);
        Assert.Contains("trueValue()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("public boolean true()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void MethodNamedFalse_ConvertedToFalseValue()
    {
        var result = Convert(@"
public class Sample
{
    public bool False() => false;
}");
        Assert.True(result.Success);
        Assert.Contains("falseValue()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("public boolean false()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void MethodNamedNull_ConvertedToNullValue()
    {
        var result = Convert(@"
public class Sample
{
    public object Null() => null;
}");
        Assert.True(result.Success);
        Assert.Contains("nullValue()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("public Object null()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void MethodNamedTrueAndFalse_InSameClass_BothEscaped()
    {
        var result = Convert(@"
public class BoolHelper
{
    public bool True() => true;
    public bool False() => false;
}");
        Assert.True(result.Success);
        Assert.Contains("trueValue()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("falseValue()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("boolean true()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("boolean false()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void InvocationOfMethodNamedTrue_ConvertedToTrueValue()
    {
        var result = Convert(@"
public class Sample
{
    public void Test()
    {
        var x = True();
    }
    public bool True() => true;
}");
        Assert.True(result.Success);
        Assert.Contains("trueValue()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Regression_KeywordAssert_StillEscaped()
    {
        var result = Convert(@"
public class Sample
{
    public void Assert() { }
}");
        Assert.True(result.Success);
        Assert.Contains("assertValue()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("void assert()", result.GeneratedCode, StringComparison.Ordinal);
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
