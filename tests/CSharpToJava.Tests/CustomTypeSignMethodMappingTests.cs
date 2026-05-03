using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that the Sign → signum mapping only applies to System.Math.Sign,
/// not to Sign methods on custom types like ApproximateComparer.
/// Regression: the universal camelCase switch mapped "Sign" → "signum" for ALL types,
/// causing ApproximateComparer.Sign() to incorrectly generate signum() instead of sign().
/// </summary>
public class CustomTypeSignMethodMappingTests
{
    [Fact]
    public void CustomType_SignMethod_GeneratesSign_NotSignum()
    {
        var result = Convert(@"
public static class ApproximateComparer
{
    public static int Sign(double value)
    {
        return value > 0 ? 1 : value < 0 ? -1 : 0;
    }
}

public class CdtSweeper
{
    public void Foo(double x)
    {
        var s = ApproximateComparer.Sign(x);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var code = result.GeneratedCode!;

        // Should use camelCase "sign()" for custom type, not "signum()"
        Assert.DoesNotContain("signum", code);
        Assert.Contains("ApproximateComparer.sign(", code);
    }

    [Fact]
    public void SystemMath_SignMethod_StillGeneratesCorrectMapping()
    {
        var result = Convert(@"
using System;

public class Demo
{
    public int Foo(int x)
    {
        return Math.Sign(x);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var code = result.GeneratedCode!;

        // System.Math.Sign(int) should map to Integer.signum (handled elsewhere)
        Assert.Contains("Integer.signum", code);
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
