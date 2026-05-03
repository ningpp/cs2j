using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that when a property/field name collides with a type name (e.g., property "Edge"
/// of type "Edge"), member access on that property generates instance getter calls
/// (getEdge().getLabel()) rather than static type references
/// (Microsoft.Msagl.Core.Layout.Edge.getLabel()).
///
/// Regression: Strategy 8 in IdentifierExpressionTransformer resolved identifiers
/// starting with uppercase as types without checking if they match an instance member
/// of the enclosing class.
/// </summary>
public class PropertyTypeNameCollisionTests
{
    [Fact]
    public void PropertyNamedAfterItsType_GeneratesInstanceGetter_NotStaticTypeAccess()
    {
        var result = Convert(@"
public class Edge
{
    public LabelData Label { get; set; }
    public double Separation { get; set; }
    public double Weight { get; set; }
}

public class LabelData
{
    public double Width { get; set; }
    public double Height { get; set; }
}

public class PolyIntEdge
{
    public Edge Edge { get; set; }

    public double LabelWidth
    {
        get { return Edge.Label.Width; }
    }

    public double LabelHeight
    {
        get { return Edge.Label.Height; }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode!;

        // Should use instance getter: getEdge().getLabel().getWidth()
        // NOT static type reference: Edge.getLabel().getWidth()
        Assert.DoesNotContain("Edge.getLabel()", code);
        Assert.Contains("getEdge().getLabel().getWidth()", code);
        Assert.Contains("getEdge().getLabel().getHeight()", code);
    }

    [Fact]
    public void PropertyNamedAfterItsType_FromOtherNamespace_DifferentiatesCorrectly()
    {
        var result = Convert(@"
namespace Geometry {
    public class Point
    {
        public double X { get; set; }
        public double Y { get; set; }
    }
}

namespace Routing {
    using Geometry;

    public class LinkedPoint
    {
        public Point Point { get; set; }

        public double X
        {
            get { return Point.X; }
        }

        public double Y
        {
            get { return Point.Y; }
        }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode!;

        // Should use instance getter: getPoint().getX()
        // NOT static field access: Point.X
        Assert.DoesNotContain("return Point.X", code);
        Assert.DoesNotContain("return Point.Y", code);
        Assert.Contains("getPoint().getX()", code);
        Assert.Contains("getPoint().getY()", code);
    }

    [Fact]
    public void FieldNamedAfterItsType_GeneratesInstanceAccess_NotStaticTypeAccess()
    {
        var result = Convert(@"
public class Edge
{
    public LabelData Label { get; set; }
}

public class LabelData
{
    public double Width { get; set; }
}

public class GeometryGraphReader
{
    readonly Edge edge;

    public double GetLabelWidth()
    {
        return edge.Label.Width;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode!;

        // Should use instance field access: edge.getLabel().getWidth()
        // NOT static type reference: Edge.getLabel().getWidth()
        Assert.DoesNotContain("Edge.getLabel()", code);
        Assert.Contains("edge.getLabel().getWidth()", code);
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
