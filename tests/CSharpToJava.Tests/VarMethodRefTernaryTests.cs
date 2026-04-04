using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;
using System;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that var declarations with method references in ternary expressions
/// use explicit types instead of Java var (which can't infer from method refs).
/// </summary>
public class VarMethodRefTernaryTests
{
    [Fact]
    public void VarWithDelegateCastInTernary_UsesExplicitType()
    {
        // C#: var fn = cond ? (Func<double, double>)Math.Abs : Math.Sin;
        // Java var can't infer functional interface type from method references in ternary.
        // Must use explicit type.
        var result = Convert(@"
using System;
class Sample {
    static double Negate(double x) => -x;
    static double Identity(double x) => x;
    void M(bool flag) {
        var fn = flag ? (Func<double, double>)Negate : Identity;
        Console.WriteLine(fn(3.14));
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // Should NOT use 'var' for the declaration
        Assert.DoesNotContain("var fn =", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void VarWithMemberAccessInTernary_UsesExplicitType()
    {
        // When ternary branches contain member access expressions (potential method groups),
        // use explicit type.
        var result = Convert(@"
using System;
class Sample {
    delegate double Transform(double x);
    static double MinusY(double x) => -x;
    static double PlusY(double x) => x;
    void M(bool flag) {
        var proj = flag ? (Transform)MinusY : PlusY;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.DoesNotContain("var proj =", result.GeneratedCode, StringComparison.Ordinal);
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
