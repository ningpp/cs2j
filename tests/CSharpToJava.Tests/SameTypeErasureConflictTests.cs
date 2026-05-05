using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class SameTypeErasureConflictTests
{
    [Fact]
    public void NonGenericOverloads_SameErasedParams_RenamesSecond()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
using System.Collections.Generic;

class Sample
{
    void MkEdgeStmt(IList<string> src, IList<string> dst)
    {
    }

    void MkEdgeStmt(IList<string> src, IList<IList<string>> edges)
    {
    }
}",
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success,
            "Conversion failed: " + string.Join("; ", result.Diagnostics.Select(d => $"[{d.Severity}] {d.Message}")));

        var code = result.GeneratedCode;

        // Both methods must appear
        Assert.Contains("mkEdgeStmt(List<String> src, List<String> dst)", code);
        Assert.Contains("mkEdgeStmt_erasure_2(List<String> src, List<List<String>> edges)", code);

        // Nested generic correctly mapped
        Assert.Contains("List<List<String>>", code);
    }

    [Fact]
    public void NonGenericOverloads_DifferentErasedParameterTypes_NoConflict()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
using System.Collections.Generic;

class Sample
{
    void MkEdgeStmt(IList<string> src, IList<string> dst)
    {
    }

    void MkEdgeStmt(IList<string> src, string name)
    {
    }
}",
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success);

        var code = result.GeneratedCode;
        // Both should have the same name (no conflict — different erased types)
        Assert.Contains("void mkEdgeStmt(List<String> src, List<String> dst)", code);
        Assert.Contains("void mkEdgeStmt(List<String> src, String name)", code);
        Assert.DoesNotContain("_erasure_", code);
    }

    [Fact]
    public void ThreeWayConflict_SameTypeParamCount_RenamesSequentially()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
using System.Collections.Generic;

class Sample
{
    void Process(IList<string> a) { }
    void Process(IList<int> a) { }
    void Process(IList<IList<string>> a) { }
}",
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success,
            "Conversion failed: " + string.Join("; ", result.Diagnostics.Select(d => $"[{d.Severity}] {d.Message}")));

        var code = result.GeneratedCode;

        // First keeps original name, second gets _erasure_2, third gets _erasure_3
        Assert.Contains("void process(List<String> a)", code);
        Assert.Contains("void process_erasure_2(List<Integer> a)", code);
        Assert.Contains("void process_erasure_3(List<List<String>> a)", code);
    }

    [Fact]
    public void DifferentTypeParamCount_UsesExistingNtpSuffix()
    {
        // Existing behavior must stay unchanged
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
class Sample
{
    void M(object x) { }
    void M<T>(T x) { }
}",
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success,
            "Conversion failed: " + string.Join("; ", result.Diagnostics.Select(d => $"[{d.Severity}] {d.Message}")));

        var code = result.GeneratedCode;
        // Method with 0 type params (fewer) gets _0tp
        Assert.Contains("void m_0tp", code);
        // Method with 1 type param (more) keeps original name without suffix.
        // Java syntax puts type parameters before the return type: <T> void m(T x)
        Assert.Contains("<T> void m", code);
    }
}
