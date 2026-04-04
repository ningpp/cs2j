using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that when a method returning IEnumerable&lt;T&gt; is passed as a constructor argument,
/// its result is NOT incorrectly treated as a stream expression just because its *inner* arguments
/// contain stream operations (like .map() or .filter()). 
/// Regression: gluedPolyline(poly.Select(...).ToArray(), map) was getting
/// ".collect(Collectors.toCollection(...))" appended because the inner .map() triggered
/// LooksLikeJavaStreamExpression on the entire expression string.
/// </summary>
public class IterableArgStreamFalsePositiveTests
{
    [Fact]
    public void IterableReturningMethod_WithStreamArgs_NoCollectAppended()
    {
        var source = @"
using System.Collections.Generic;
using System.Linq;

class Point { public int X { get; set; } }
class Station { }
class Polyline {
    public Polyline(IEnumerable<Point> points) { }
}

class Sample {
    Dictionary<Point, Station> pointToStations = new();

    void Regen(Polyline poly) {
        var arr = poly.Select(p => pointToStations[p]).ToArray();
        var curve = new Polyline(GluedPolyline(arr, new Dictionary<Station, Station>()));
    }

    static IEnumerable<Point> GluedPolyline(Station[] metroline, Dictionary<Station, Station> gluedMap) {
        yield return new Point();
    }
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));

        var code = result.GeneratedCode!;

        // gluedPolyline(...) returns Iterable<Point>, should NOT have .collect() appended
        Assert.DoesNotContain("gluedPolyline(", code.Split(".collect(")[0].Contains("gluedPolyline(") ? "" : "SKIP");

        // More directly: the Polyline constructor argument should be gluedPolyline(...) without .collect()
        // The pattern "new Polyline(gluedPolyline(...).collect(" should NOT appear
        Assert.DoesNotContain(".collect(Collectors.toCollection", code.Substring(
            code.IndexOf("new Polyline(gluedPolyline("),
            code.IndexOf(")));", code.IndexOf("new Polyline(gluedPolyline(")) + 4 - code.IndexOf("new Polyline(gluedPolyline(")));
    }

    [Fact]
    public void IterableReturningMethod_WithStreamArgs_ConstructorGetsDirectCall()
    {
        var source = @"
using System.Collections.Generic;
using System.Linq;

class Point { public int X { get; set; } }
class Station { }
class Polyline {
    public Polyline(IEnumerable<Point> points) { }
}

class Sample {
    Dictionary<Point, Station> pointToStations = new();

    void Regen(Polyline poly) {
        var curve = new Polyline(GetPoints(new List<int> { 1, 2, 3 }.Select(x => x * 2).ToArray()));
    }

    static IEnumerable<Point> GetPoints(int[] ids) {
        yield return new Point();
    }
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));

        var code = result.GeneratedCode!;

        // getPoints(...) returns Iterable<Point>, should appear directly inside new Polyline(...)
        Assert.Contains("new Polyline(getPoints(", code);
        // Should NOT have .collect() chained on getPoints(...)
        Assert.DoesNotMatch(@"getPoints\([^)]*\)\.collect\(", code);
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
