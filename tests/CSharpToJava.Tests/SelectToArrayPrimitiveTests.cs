using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for Select(lambda).ToArray() producing correct typed arrays
/// when the lambda maps a reference type to a primitive type.
/// </summary>
public class SelectToArrayPrimitiveTests
{
    [Fact]
    public void SelectDouble_ToArray_UsesMapToDouble()
    {
        var source = @"
using System;
using System.Linq;
class Sample {
    double ParseDouble(string s) => double.Parse(s);
    void M() {
        var ss = ""1.0 2.0 3.0"".Split(' ');
        var ds = ss.Select(s => ParseDouble(s)).ToArray();
        Console.WriteLine(ds[0] + ds[1]);
    }
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        // Should use mapToDouble somewhere in the chain so the result is double[] not Object[]
        Assert.Contains("mapToDouble(", result.GeneratedCode);
        Assert.Contains(".toArray()", result.GeneratedCode);
    }

    [Fact]
    public void SelectInt_ToArray_UsesMapToInt()
    {
        var source = @"
using System;
using System.Linq;
class Sample {
    void M() {
        var words = new[] { ""hello"", ""world"" };
        var lengths = words.Select(w => w.Length).ToArray();
        Console.WriteLine(lengths[0]);
    }
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("mapToInt(", result.GeneratedCode);
        Assert.Contains(".toArray()", result.GeneratedCode);
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
