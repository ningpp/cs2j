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

    [Fact]
    public void UsingAlias_MappedFrameworkType_UsesConfiguredImport()
    {
        var result = Convert("""
using Color = System.Drawing.Color;

namespace Dot2Graph
{
    class AttributeValuePair
    {
        Color FromNameOrBlack(string name)
        {
            return name == null ? Color.Black : Color.FromName(name);
        }
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("import java.awt.Color;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.DrawingColor;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Color fromNameOrBlack(String name)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("DrawingColor.getBlack()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("DrawingColor.fromName(name)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("java.lang.Drawing.Color", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("? Color.getBlack()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(": Color.fromName(name)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UsingAlias_MappedFrameworkType_NotRequalifiedWhenProjectHasSameSimpleName()
    {
        var result = Convert("""
using Color = System.Drawing.Color;

namespace Microsoft.Msagl.Drawing
{
    public struct Color
    {
    }
}

namespace Dot2Graph
{
    class AttributeValuePair
    {
        Color Convert(Microsoft.Msagl.Drawing.Color color)
        {
            return Color.FromArgb(1, 2, 3);
        }
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("import java.awt.Color;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("DrawingColor.fromArgb(1, 2, 3)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("java.lang.Drawing.Color", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NumericArguments_ToByteConstructor_AreExplicitlyCast()
    {
        var result = Convert("""
namespace Microsoft.Msagl.Drawing
{
    public struct Color
    {
        public Color(byte a, byte r, byte g, byte b)
        {
        }
    }
}

namespace Dot2Graph
{
    class AttributeValuePair
    {
        Microsoft.Msagl.Drawing.Color Convert(System.Drawing.Color drawingColor)
        {
            return new Microsoft.Msagl.Drawing.Color(
                drawingColor.A,
                drawingColor.R,
                drawingColor.G,
                drawingColor.B);
        }
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("new Color(DrawingColor.getA(drawingColor), DrawingColor.getR(drawingColor), DrawingColor.getG(drawingColor), DrawingColor.getB(drawingColor))", result.GeneratedCode, StringComparison.Ordinal);
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
