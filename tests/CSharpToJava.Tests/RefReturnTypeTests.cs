using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for C# ref return type conversion. In Java there is no 'ref' return type,
/// so the converter must strip the 'ref' keyword and return the plain value type.
/// </summary>
public class RefReturnTypeTests
{
    [Fact]
    public void RefCharReturnType_StripsRefKeyword()
    {
        var result = Convert(@"
using System;
public class Sample
{
    private char[] _chars;
    public ref char GetPinnableReference()
    {
        return ref _chars[0];
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Java does not support 'ref' return types - should be plain 'char'
        Assert.DoesNotContain("ref char", result.GeneratedCode);
        Assert.Contains("char getPinnableReference()", result.GeneratedCode);
    }

    [Fact]
    public void RefIntReturnType_StripsRefKeyword()
    {
        var result = Convert(@"
public class Sample
{
    private int[] _data = new int[10];
    public ref int GetItem(int index)
    {
        return ref _data[index];
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain("ref int", result.GeneratedCode);
        Assert.Contains("int getItem(int index)", result.GeneratedCode);
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
