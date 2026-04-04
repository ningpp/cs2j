using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that integer expressions assigned to Dictionary&lt;K, double&gt; indexers
/// receive a (double) cast in Java, since Java can't autobox int → Double.
/// </summary>
public class IntToDoubleBoxingTests
{
    [Fact]
    public void DictionaryIntValueToPutDouble_AddsCast()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Test
{
    void M()
    {
        var p = new Dictionary<string, double>();
        int n = 10;
        p[""key""] = 1 / n;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Must cast int expression to double for Java autoboxing to Double
        Assert.Contains("(double)", result.GeneratedCode);
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
