using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that "System.Array as T[]" uses CSharpArray.tryAs(...) instead of an
/// invalid Java cast, because System.Array maps to the CSharpArray wrapper.
/// </summary>
public class SystemArrayAsArrayTests
{
    [Fact]
    public void SystemArrayAsTypedArray_UsesTryAs()
    {
        var result = Convert(@"
using System;

public class Sample
{
    public string[] TryCastArray(object value)
    {
        Array arr = value as Array;
        string[] strings = arr as string[];
        return strings;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("CSharpArray", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("tryAs", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("String[].class", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("instanceof String[]", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemArrayAsTypedArray_NullSafe()
    {
        var result = Convert(@"
using System;

public class Sample
{
    public int[] TryCastArray(Array arr)
    {
        return arr as int[];
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("tryAs", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("int[].class", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ArrayListToArrayResult_AsTypedArray_UsesStaticTryAs()
    {
        var result = Convert(@"
using System;
using System.Collections;

public class Sample
{
    public string[] CastArrayList(ArrayList list)
    {
        return list.ToArray(typeof(string)) as string[];
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("CSharpArray.tryAs(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("String[].class", result.GeneratedCode, StringComparison.Ordinal);
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
