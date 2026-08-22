using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# tuple deconstruction with discards correctly maps
/// tuple elements to the right variable. E.g. `var (_, map) = CreateNodes(2)`
/// should produce `map = _t._2()` (second element), not `map = _t._1()`.
/// </summary>
public class TupleDeconstructionDiscardTests
{
    private readonly ITestOutputHelper _out;

    public TupleDeconstructionDiscardTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void TupleDeconstruction_WithDiscardFirst_UsesCorrectIndex()
    {
        var result = Convert("""
using System;
using System.Collections.Generic;

class Test
{
    static (List<int>, Dictionary<int, int>) CreateNodes(int n)
    {
        return (new List<int>(), new Dictionary<int, int>());
    }
    static void Run()
    {
        var (_, map) = CreateNodes(2);
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // map should be the SECOND element (_t._2()), not the first (_t._1())
        Assert.Contains("_t._2()", code, StringComparison.Ordinal);
        Assert.DoesNotContain("map = _t._1()", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TupleDeconstruction_WithDiscardSecond_UsesCorrectIndex()
    {
        var result = Convert("""
using System;
using System.Collections.Generic;

class Test
{
    static (List<int>, Dictionary<int, int>) CreateNodes(int n)
    {
        return (new List<int>(), new Dictionary<int, int>());
    }
    static void Run()
    {
        var (nodes, _) = CreateNodes(2);
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // nodes should be the FIRST element (_t._1())
        Assert.Contains("_t._1()", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TupleDeconstruction_NoDiscard_StillWorks()
    {
        var result = Convert("""
using System;
using System.Collections.Generic;

class Test
{
    static (List<int>, Dictionary<int, int>) CreateNodes(int n)
    {
        return (new List<int>(), new Dictionary<int, int>());
    }
    static void Run()
    {
        var (nodes, map) = CreateNodes(2);
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // Both should be present with correct indices
        Assert.Contains("_t._1()", code, StringComparison.Ordinal);
        Assert.Contains("_t._2()", code, StringComparison.Ordinal);
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
