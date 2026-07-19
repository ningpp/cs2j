using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that conditional expressions whose target type is an enum adapt
/// integral literals to enum values so Java's ternary type checking succeeds.
/// </summary>
public class EnumConditionalExpressionTests
{
    [Fact]
    public void ConditionalExpression_EnumTarget_IntLiteral_WrapsFromValue()
    {
        var result = Convert("""
            class Sample
            {
                enum Color { Red = 0, Green = 1, Blue = 2 }

                Color Pick(bool flag)
                {
                    return flag ? Color.Red : 0;
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        // The int literal 0 must be wrapped as Color.fromValue(0) so both branches are Color.
        Assert.Contains("Color.fromValue(0)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConditionalExpression_EnumTarget_IntLiteralTrueBranch_WrapsFromValue()
    {
        var result = Convert("""
            class Sample
            {
                enum Color { Red = 0, Green = 1, Blue = 2 }

                Color Pick(bool flag)
                {
                    return flag ? 0 : Color.Blue;
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("Color.fromValue(0)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConditionalExpression_EnumTarget_NonLiteralInt_Succeeds()
    {
        var result = Convert("""
            class Sample
            {
                enum Color { Red = 0, Green = 1, Blue = 2 }

                Color Pick(bool flag, int value)
                {
                    return flag ? Color.Red : (Color)value;
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
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
