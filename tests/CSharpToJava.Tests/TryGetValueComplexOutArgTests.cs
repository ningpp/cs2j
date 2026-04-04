using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;
using System;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for TryGetValue with non-simple out arguments (array elements, member access, etc.).
/// Root cause: InvocationExpressionTransformer's TryGetValue handler required IsSimpleIdentifier
/// for the out argument.  When the out arg was e.g. Result[i], it fell through to the generic
/// invocation path which passed the out holder as a second argument to Java Map.get(), producing
/// invalid code like d.get(v, _outArgHolder2).
/// </summary>
public class TryGetValueComplexOutArgTests
{
    [Fact]
    public void TryGetValue_ArrayElement_NegatedIf_UsesContainsKey()
    {
        // C#: if (!d.TryGetValue(v, out Result[i])) Result[i] = double.PositiveInfinity;
        // Java should use containsKey pattern that avoids null auto-unboxing NPE
        var result = Convert(@"
using System.Collections.Generic;
class Node { }
class Sample {
    double[] Result = new double[10];
    void M(Dictionary<Node, double> d) {
        int i = 0;
        foreach (Node v in d.Keys) {
            if (!d.TryGetValue(v, out Result[i]))
                Result[i] = double.PositiveInfinity;
            i++;
        }
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // Must NOT have invalid get(v, holder) with two args
        Assert.DoesNotContain("get(v, _", result.GeneratedCode, StringComparison.Ordinal);
        // Should use containsKey pattern
        Assert.Contains("containsKey(", result.GeneratedCode, StringComparison.Ordinal);
        // Should have the else branch assigning from get
        Assert.Contains(".get(v)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TryGetValue_SimpleVar_StillWorks()
    {
        // Ensure existing behavior for simple out variables is not broken
        var result = Convert(@"
using System.Collections.Generic;
class Sample {
    void M(Dictionary<string, string> d, string key) {
        if (!d.TryGetValue(key, out var value))
            value = ""default"";
        System.Console.WriteLine(value);
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        // Should use the var = get(key) pattern
        Assert.Contains("= d.get(key)", result.GeneratedCode, StringComparison.Ordinal);
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
