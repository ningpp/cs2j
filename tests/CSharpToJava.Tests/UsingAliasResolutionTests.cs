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

    /// <summary>
    /// A using alias used as a static-member receiver inside a switch case label
    /// (`case FT.FuncBoolean:`) must resolve to the mapped target type, exactly
    /// like the same alias in field/method-signature type positions. Keeping the
    /// alias spelling makes javac parse `FT.` as a package
    /// (real chain: MS.Internal.Xml.XPath BooleanFunctions/NodeFunctions/
    /// NumberFunctions/QueryBuilder over `using FT = ...FunctionType`).
    /// </summary>
    [Fact]
    public void UsingAlias_EnumMemberReceiverInSwitchCaseLabel_Resolved()
    {
        var result = Convert(@"
using FT = Helpers.FuncType;

namespace Helpers
{
    public enum FuncType
    {
        FuncA,
        FuncB,
    }
}

class Test
{
    int Eval(FT t)
    {
        switch (t)
        {
            case FT.FuncA:
                return 1;
            default:
                return 0;
        }
    }
}
");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain("FT.", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("case FuncType.FuncA:", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = CreateOptions(),
        });
    }

    private static ConversionOptions CreateOptions()
        => new()
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
        };
}
