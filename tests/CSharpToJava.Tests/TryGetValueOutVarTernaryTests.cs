using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for TryGetValue with out var in ternary return expressions.
/// </summary>
public class TryGetValueOutVarTernaryTests
{
    private ConversionResult Convert(string src)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = src,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }

    /// <summary>
    /// C#: return dict.TryGetValue(key, out T v) ? v : null;
    /// Should produce clean Java: T v = dict.get(key); return v;
    /// not the convoluted holder-based pattern.
    /// </summary>
    [Fact]
    public void TryGetValueOutVar_TernaryReturn_ShouldUseGet()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Point {
    public double X = 0, Y = 0;
}

class VisibilityVertex {
    public Point Point;
}

class VisibilityGraph
{
    Dictionary<Point, VisibilityVertex> PointToVertexMap = new Dictionary<Point, VisibilityVertex>();

    VisibilityVertex FindVertex(Point point) {
        return PointToVertexMap.TryGetValue(point, out VisibilityVertex v) ? v : null;
    }
}");

        Assert.True(result.Success, "Conversion should succeed");
        var code = result.GeneratedCode ?? "";

        // Should use clean get() pattern, not holder-based pattern
        Assert.Contains(".get(", code);
        Assert.DoesNotContain("ObjectHolder", code);
        Assert.DoesNotContain("_vHolder", code);
        Assert.Contains("VisibilityVertex v = ", code);
        Assert.DoesNotContain("var _ret =", code);
    }

    /// <summary>
    /// When the false branch is not a raw null literal (e.g. (int?)null), the special case
    /// should not apply — the generic path handles it.
    /// </summary>
    [Fact]
    public void TryGetValueOutVar_TernaryReturn_WithNullableCast_UsesGenericPath()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Sample
{
    Dictionary<string, int> Counts = new Dictionary<string, int>();

    int? GetCount(string key) {
        return Counts.TryGetValue(key, out int v) ? v : (int?)null;
    }
}");

        Assert.True(result.Success, "Conversion should succeed");
        var code = result.GeneratedCode ?? "";

        // (int?)null is a CastExpression, not a raw NullLiteralExpression,
        // so the special case does not apply — the generic holder path is used
        Assert.Contains("IntHolder", code);
    }

    /// <summary>
    /// C#: return dict.TryGetValue(key, out T v) ? v : someDefault;
    /// When the false branch is not null, the generic path still applies.
    /// </summary>
    [Fact]
    public void TryGetValueOutVar_TernaryReturn_NonNullDefault_UsesGenericPath()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Point {
    public double X, Y;
}

class VisibilityVertex {
    public Point Point;
}

class VisibilityGraph
{
    Dictionary<Point, VisibilityVertex> PointToVertexMap = new Dictionary<Point, VisibilityVertex>();

    VisibilityVertex FindVertex(Point point) {
        return PointToVertexMap.TryGetValue(point, out VisibilityVertex v) ? v : new VisibilityVertex();
    }
}");

        Assert.True(result.Success, "Conversion should succeed");
        var code = result.GeneratedCode ?? "";

        // When false branch is not null, the generic holder path is used
        Assert.Contains("ObjectHolder", code);
    }
}
