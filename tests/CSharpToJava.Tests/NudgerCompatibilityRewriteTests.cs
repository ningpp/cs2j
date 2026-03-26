using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class NudgerCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_Nudger_CachesEnumeratorCurrentWithinLoop()
    {
        const string generated = """
            package Microsoft.Msagl.Routing.Rectilinear.Nudging;

            public class Nudger {
                public java.lang.Iterable<Point> removeSwitchbacksAndMiddlePoints(java.util.Iterator<Point> en) {
                    en.hasNext();
                    var a = en.next().clone();
                    _yieldResult.add(a);
                    en.hasNext();
                    var b = en.next().clone();
                    var prevDir = (Point.subtract(b, a)).getCompassDirection();
                    while (en.hasNext()) {
                    var dir = (Point.subtract(en.next(), b)).getCompassDirection();
                    if (!(dir == prevDir || CompassVector.oppositeDir(dir) == prevDir || dir == Direction.None)) {
                    if (!ApproximateComparer.close(a, b)) {
                    _yieldResult.add(a = rectilinearise(a, b.clone()));
                    }
                    prevDir = dir;
                    }
                    b = en.next().clone();
                    }
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("Nudger.java", generated).Replace("\r\n", "\n");

        Assert.Contains("var _current = en.next();", output);
        Assert.Contains("var dir = (Point.subtract(_current, b)).getCompassDirection();", output);
        Assert.Contains("b = _current.clone();", output);
        Assert.DoesNotContain("Point.subtract(en.next(), b)", output);
    }
}