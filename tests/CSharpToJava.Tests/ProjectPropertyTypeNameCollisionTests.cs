using System;
using System.IO;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Project-pipeline test: When a property name collides with a type name from
/// another file, member access should use the instance property getter (getEdge())
/// not a static type reference (Microsoft.Msagl.Core.Layout.Edge).
///
/// Regression: in the project pipeline, Strategy 8 resolved identifiers starting
/// with uppercase as types without checking against enclosing-type instance members.
/// </summary>
public class ProjectPropertyTypeNameCollisionTests
{
    private static ConversionOptions CreateOptions()
    {
        return new ConversionOptions
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
            PreferStreamApi = false,
        };
    }

    [Fact]
    public async Task CrossFile_PropertyNamedAfterType_UsesInstanceGetter()
    {
        var edgeFile = @"
namespace Test.Core.Layout {
    public class Edge {
        public LabelData Label { get; set; }
        public double Separation { get; set; }
        public double Weight { get; set; }
        public Curve Curve { get; set; }
    }

    public class LabelData {
        public double Width { get; set; }
        public double Height { get; set; }
    }

    public interface Curve { }
}";

        var polyIntEdgeFile = @"
using Test.Core.Layout;

namespace Test.Layout.Layered {
    public class PolyIntEdge {
        public Edge Edge { get; set; }

        public double LabelWidth {
            get { return Edge.Label.Width; }
        }

        public double LabelHeight {
            get { return Edge.Label.Height; }
        }

        public bool HasLabel {
            get { return Edge.Label != null; }
        }

        public Curve GetCurve() {
            return Edge.Curve;
        }
    }
}";

        var options = CreateOptions();
        var pipeline = new ProjectConversionPipeline(options);
        var results = await pipeline.ConvertProjectAsync(new[]
        {
            new SourceFile { FilePath = "Core/Layout/Edge.cs", Content = edgeFile },
            new SourceFile { FilePath = "Layout/Layered/PolyIntEdge.cs", Content = polyIntEdgeFile },
        });

        Assert.True(results.Count >= 2, $"Expected at least 2 results but got {results.Count}");
        var polyResult = Assert.Single(results, r => r.FileName == "PolyIntEdge.java");
        Assert.True(polyResult.Success, string.Join("\n", polyResult.Diagnostics));

        var code = polyResult.GeneratedCode!;

        // Should use instance getter: getEdge().getLabel().getWidth()
        // NOT static type reference: Edge.getLabel().getWidth()
        Assert.DoesNotContain("Test.Core.Layout.Edge.getLabel()", code, StringComparison.Ordinal);
        Assert.Contains("getEdge().getLabel().getWidth()", code, StringComparison.Ordinal);
        Assert.Contains("getEdge().getLabel().getHeight()", code, StringComparison.Ordinal);
        Assert.Contains("getEdge().getLabel() != null", code, StringComparison.Ordinal);
        Assert.Contains("getEdge().getCurve()", code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrossFile_PropertyNamedAfterType_FromDifferentNamespace_UsesInstanceGetter()
    {
        var pointFile = @"
namespace Test.Core.Geometry {
    public class Point {
        public double X { get; set; }
        public double Y { get; set; }
        public Point Clone() { return new Point { X = this.X, Y = this.Y }; }
    }
}";

        var linkedPointFile = @"
using Test.Core.Geometry;

namespace Test.Routing.Nudging {
    public class LinkedPoint {
        public Point Point { get; set; }

        public double X {
            get { return Point.X; }
        }

        public double Y {
            get { return Point.Y; }
        }

        public override string ToString() {
            return Point.ToString();
        }
    }
}";

        var options = CreateOptions();
        var pipeline = new ProjectConversionPipeline(options);
        var results = await pipeline.ConvertProjectAsync(new[]
        {
            new SourceFile { FilePath = "Core/Geometry/Point.cs", Content = pointFile },
            new SourceFile { FilePath = "Routing/Nudging/LinkedPoint.cs", Content = linkedPointFile },
        });

        Assert.Equal(2, results.Count);
        var linkedResult = Assert.Single(results, r => r.FileName == "LinkedPoint.java");
        Assert.True(linkedResult.Success, string.Join("\n", linkedResult.Diagnostics));

        var code = linkedResult.GeneratedCode!;

        // Should use instance getter: getPoint().getX()
        // NOT static field access: Point.X
        Assert.DoesNotContain("return Point.X", code, StringComparison.Ordinal);
        Assert.DoesNotContain("return Point.Y", code, StringComparison.Ordinal);
        Assert.Contains("getPoint().getX()", code, StringComparison.Ordinal);
        Assert.Contains("getPoint().getY()", code, StringComparison.Ordinal);
        // ToString: should be getPoint().toString() not Point.toString()
        Assert.Contains("getPoint().toString()", code, StringComparison.Ordinal);
    }
}
