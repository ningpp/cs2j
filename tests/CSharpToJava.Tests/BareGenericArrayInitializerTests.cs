using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Java forbids creating arrays of generic types (new Pair&lt;T&gt;[] is a compile
/// error). Bare array initializers assigned to a generic-typed array variable must
/// use the raw-type creation plus an unchecked cast, matching new Pair&lt;T&gt;[0].
/// </summary>
public class BareGenericArrayInitializerTests
{
    [Fact]
    public void EmptyBareInitializer_GenericElementType_EmitsRawArrayWithCast()
    {
        var result = Convert(@"
using System;
class Pair<T> { }
class T {
    void M() {
        Pair<int>[] arr = {};
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("(Pair<Integer>[]) new Pair[] {  }", result.GeneratedCode);
        Assert.DoesNotContain("new Pair<Integer>[]", result.GeneratedCode);
    }

    [Fact]
    public void NonEmptyBareInitializer_GenericElementType_EmitsRawArrayWithCast()
    {
        var result = Convert(@"
using System;
class Pair<T> { }
class T {
    void M() {
        Pair<int> a = null;
        Pair<int>[] arr = { a };
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("(Pair<Integer>[]) new Pair[] { a }", result.GeneratedCode);
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
