using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# discard parameters (underscore "_") in lambdas are converted
/// to unique Java parameter names. In C#, "(_, _) => true" uses two discards;
/// the converter must not produce duplicate "_Value" names (Java keyword escape
/// for "_" appends "Value"), which causes "variable _Value is already defined"
/// compilation errors.
/// </summary>
public class LambdaDiscardParameterTests
{
    private readonly ITestOutputHelper _out;

    public LambdaDiscardParameterTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Lambda_TwoDiscardParameters_ProducesUniqueNames()
    {
        // Mirrors E:\agl-master\GraphLayout\...\ShortestPartRouterForLgTest.cs:
        //   CreateRouter(s, t, infos, (_, _) => true);
        // The C# discards "_" both become "_Value" via EscapeJavaKeyword,
        // but Java forbids two parameters with the same name.
        var result = Convert("""
using System;
using System.Collections.Generic;

class Test
{
    static bool Predicate(int a, int b, Func<int, int, bool> pred) => pred(a, b);

    static void Run()
    {
        var result = Predicate(1, 2, (_, _) => true);
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // Must not contain two parameters with the same name.
        // The converter should generate unique names for each discard.
        Assert.DoesNotContain("(_Value, _Value)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Lambda_ThreeDiscardParameters_ProducesUniqueNames()
    {
        var result = Convert("""
using System;

class Test
{
    static bool TriPredicate(int a, int b, int c, Func<int, int, int, bool> pred) => pred(a, b, c);

    static void Run()
    {
        var result = TriPredicate(1, 2, 3, (_, _, _) => true);
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // No three identical parameter names.
        Assert.DoesNotContain("(_Value, _Value, _Value)", code, StringComparison.Ordinal);
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
