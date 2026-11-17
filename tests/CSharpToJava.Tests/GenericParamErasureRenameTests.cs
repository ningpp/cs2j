using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Text.RegularExpressions;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Two alias pairs whose setters only differ by a class type parameter
/// (<c>set_graph(__P0)</c> vs <c>set_Graph(__P1)</c>) collapse to the same Java
/// erasure signature <c>Graph(Object)</c>. The declaration-time rename check used
/// to compare "Object" against the emitted "__P0"/"__P1" text, so the second setter
/// kept the name and javac reported 名称冲突 (Cs2jTuples.TupleT0T1).
/// </summary>
public class GenericParamErasureRenameTests
{
    [Fact]
    public void ClassTypeParameterSetters_AreRenamedOnErasureCollision()
    {
        var result = Convert(@"
class Box<__P0, __P1> {
    private __P0 item0;
    private __P1 item1;
    public __P0 get_graph() { return item0; }
    public void set_graph(__P0 value) { item0 = value; }
    public __P1 get_Graph() { return item1; }
    public void set_Graph(__P1 value) { item1 = value; }
}");
        Assert.True(result.Success, result.GeneratedCode);
        AssertSingleSetterAfterRename(result.GeneratedCode, "Graph");
    }

    [Fact]
    public void ClassTypeParameterTargetSetters_AreRenamedOnErasureCollision()
    {
        var result = Convert(@"
class Box<__P0, __P1> {
    private __P0 item0;
    private __P1 item1;
    public __P0 get_target() { return item0; }
    public void set_target(__P0 value) { item0 = value; }
    public __P1 get_Target() { return item1; }
    public void set_Target(__P1 value) { item1 = value; }
}");
        Assert.True(result.Success, result.GeneratedCode);
        AssertSingleSetterAfterRename(result.GeneratedCode, "Target");
    }

    private static void AssertSingleSetterAfterRename(string code, string name)
    {
        var setters = Regex.Matches(code, $@"void\s+{name}\w*\(\s*__P\d+\s+value\s*\)")
            .Select(m => m.Value)
            .ToList();
        Assert.Equal(2, setters.Count);
        // Exactly one keeps the base name; the colliding one gets a _N suffix so the
        // erasure signature Graph(Object) is declared only once.
        Assert.Single(setters, setter => Regex.IsMatch(setter, $@"void\s+{name}\("));
        Assert.Single(setters, setter => Regex.IsMatch(setter, $@"void\s+{name}_\d+\("));
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
