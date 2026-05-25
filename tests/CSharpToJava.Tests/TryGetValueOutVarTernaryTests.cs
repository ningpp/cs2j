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

    /// <summary>
    /// When the same out variable appears in both branches of a ternary, the converter
    /// must use a single ObjectHolder shared by both branches. Before the fix, two
    /// separate holders were created and both were read back unconditionally, causing
    /// the unexecuted branch's null holder to overwrite the correct value.
    /// </summary>
    [Fact]
    public void OutParam_Ternary_TwoMethods_SharedOutVar_UsesSingleHolder()
    {
        var result = Convert(@"
class CdtSite { }
class CdtSweeper
{
    CdtSite MiddleCase(CdtSite pi, object node, out CdtSite rightSite) {
        rightSite = new CdtSite();
        return rightSite;
    }

    CdtSite LeftCase(CdtSite pi, object node, out CdtSite rightSite) {
        rightSite = new CdtSite();
        return rightSite;
    }

    void PointEvent(CdtSite pi) {
        CdtSite rightSite;
        CdtSite leftSite = pi != null
            ? MiddleCase(pi, null, out rightSite)
            : LeftCase(pi, null, out rightSite);
        InsertSiteIntoFront(leftSite, pi, rightSite);
    }

    void InsertSiteIntoFront(CdtSite leftSite, CdtSite pi, CdtSite rightSite) { }
}");

        Assert.True(result.Success,
            "Conversion failed: " + string.Join("; ", result.Diagnostics.Select(d => $"[{d.Severity}] {d.Message}")));

        var code = result.GeneratedCode ?? "";

        // Should create exactly ONE holder for rightSite (shared by both branches)
        Assert.Contains("ObjectHolder<CdtSite>", code);
        var holderDeclCount = CountOccurrences(code, "ObjectHolder<CdtSite> _rightSiteHolder");
        Assert.True(holderDeclCount == 1,
            $"Expected exactly 1 holder declaration for rightSite, found {holderDeclCount}");

        // Should have exactly ONE read-back: rightSite = _rightSiteHolder1.value
        var readBackCount = CountOccurrences(code, "rightSite = _rightSiteHolder");
        Assert.True(readBackCount == 1,
            $"Expected exactly 1 read-back for rightSite, found {readBackCount}");

        // Both branches should use the same holder
        Assert.Contains("middleCase(pi, null, _rightSiteHolder1)", code);
        Assert.Contains("leftCase(pi, null, _rightSiteHolder1)", code);
    }

    private static int CountOccurrences(string text, string substring)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(substring, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += substring.Length;
        }
        return count;
    }
}
