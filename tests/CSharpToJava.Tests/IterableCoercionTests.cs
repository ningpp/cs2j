using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// C# values whose static type is ICollection&lt;T&gt;/IEnumerable&lt;T&gt;/T[] but whose
/// Java carrier is a separate hierarchy (CSharpIList, java.util.List, raw arrays)
/// must be adapted into CSharpGenericIterable at every slot that expects it.
/// </summary>
public class IterableCoercionTests
{
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

    [Fact]
    public void ArrayAndListArguments_ToIEnumerableParameter_AreAdapted()
    {
        var result = Convert(@"
using System.Collections.Generic;

public class C
{
    public int Sum(IEnumerable<int> xs)
    {
        int s = 0;
        foreach (int x in xs) { s += x; }
        return s;
    }

    public int Use(int[] arr, List<int> list)
    {
        return Sum(arr) + Sum(list);
    }
}");
        Assert.True(result.Success,
            string.Join("\n", result.Diagnostics) + "\n--- Generated ---\n" + result.GeneratedCode);
        Assert.DoesNotContain("sum(arr)", result.GeneratedCode);
        Assert.DoesNotContain("sum(list)", result.GeneratedCode);
        Assert.Contains("CSharpGenericIterable.<Integer>fromAnyTyped(arr)", result.GeneratedCode);
        Assert.Contains("CSharpGenericIterable.<Integer>fromAnyTyped(list)", result.GeneratedCode);
    }

    [Fact]
    public void ArrayReturned_FromIEnumerableMethod_IsAdapted()
    {
        var result = Convert(@"
using System.Collections.Generic;

public class C
{
    public IEnumerable<int> Repeat(int[] values) { return values; }
}");
        Assert.True(result.Success,
            string.Join("\n", result.Diagnostics) + "\n--- Generated ---\n" + result.GeneratedCode);
        Assert.Contains("fromAnyTyped(values)", result.GeneratedCode);
    }
}
