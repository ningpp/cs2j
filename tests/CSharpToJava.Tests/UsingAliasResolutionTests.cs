using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# using aliases are properly resolved to their target types in Java output.
/// </summary>
public class UsingAliasResolutionTests
{
    [Fact]
    public void UsingAlias_GenericType_ResolvedInFieldDeclaration()
    {
        var result = Convert(@"
using System.Collections.Generic;
using MyList = System.Collections.Generic.List<int>;
class Test
{
    MyList _items = new MyList();
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // The alias should be resolved; the Java output should NOT contain "MyList"
        Assert.DoesNotContain("MyList", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UsingAlias_InMethodParameter_ResolvedToTargetType()
    {
        var result = Convert(@"
using System.Collections.Generic;
using SymmetricSegment = System.Collections.Generic.List<int>;
class Test
{
    void M(SymmetricSegment seg)
    {
        int n = seg.Count;
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain("SymmetricSegment", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UsingAlias_AsGenericTypeArgument_Resolved()
    {
        var result = Convert(@"
using System.Collections.Generic;
using MyPoint = System.Collections.Generic.KeyValuePair<int,string>;
class Test
{
    List<MyPoint> _points = new List<MyPoint>();
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // MyPoint alias should be resolved, not appear in output
        Assert.DoesNotContain("MyPoint", result.GeneratedCode, StringComparison.Ordinal);
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
