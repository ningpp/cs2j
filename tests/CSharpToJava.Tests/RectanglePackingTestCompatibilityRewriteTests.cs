using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class RectanglePackingTestCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_RectanglePackingTest_RewritesOverlapCallsToRectangleProjection()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests;

            import Microsoft.Msagl.Core.Geometry.Rectangle;
            import Microsoft.Msagl.Core.Geometry.RectangleToPack;
            import java.util.stream.StreamSupport;

            public class RectanglePackingTest {
                public void test(ArrayList<RectangleToPack<Integer>> rectangles) {
                    Assertions.assertFalse(isOverlapping(rectangles), "There are overlaps between the packed rectangles");
                }

                private static boolean isOverlapping(Iterable<Rectangle> rectangles) {
                    return false;
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("RectanglePackingTest.java", generated);

        Assert.Contains("Assertions.assertFalse(isOverlapping(StreamSupport.stream(rectangles.spliterator(), false).map(RectangleToPack<Integer>::getRectangle).collect(java.util.stream.Collectors.toList())), \"There are overlaps between the packed rectangles\");", output);
        Assert.Contains("private static boolean isOverlapping(Iterable<Rectangle> rectangles)", output);
        Assert.DoesNotContain("private static boolean isOverlapping(Iterable<RectangleToPack<Integer>> rectangles)", output);
    }

    [Fact]
    public void ApplyCompatibilityRewritesForTesting_RectanglePackingTest_AddsDefaultConstructorArgument()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests;

            import Microsoft.Msagl.Core.Geometry.RectanglePacking;
            import Microsoft.Msagl.Core.Geometry.RectangleToPack;
            import java.util.ArrayList;

            public class RectanglePackingTest {
                public void test(ArrayList<RectangleToPack<Integer>> rectangles, double maxWidth) {
                    RectanglePacking<Integer> rectanglePacking = new RectanglePacking<Integer>(rectangles, 3.0);
                    RectanglePacking<Integer> randomPacking = new RectanglePacking<Integer>(rectangles, maxWidth);
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("RectanglePackingTest.java", generated);

        Assert.Contains("new RectanglePacking<Integer>(rectangles, 3.0, false)", output);
        Assert.Contains("new RectanglePacking<Integer>(rectangles, maxWidth, false)", output);
    }

    [Fact]
    public void ApplyCompatibilityRewritesForTesting_RectanglePackingTest_RewritesGuidShuffleToCollectionsShuffle()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests;

            import Microsoft.Msagl.Core.Geometry.RectangleToPack;
            import java.util.ArrayList;
            import java.util.UUID;

            public class RectanglePackingTest {
                public void test(ArrayList<RectangleToPack<Integer>> rectangles) {
                    rectangles = new ArrayList<>(rectangles.stream().sorted(java.util.Comparator.comparing((RectangleToPack<int> x) -> UUID.newGuid())).collect(java.util.stream.Collectors.toList()));
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("RectanglePackingTest.java", generated);

        Assert.Contains("java.util.Collections.shuffle(rectangles);", output);
        Assert.DoesNotContain("UUID.newGuid()", output);
        Assert.DoesNotContain("RectangleToPack<int>", output);
    }

    [Fact]
    public void ApplyCompatibilityRewritesForTesting_RectanglePackingTest_RemovesUnreachableDebugViewCode()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests;

            import Microsoft.Msagl.Core.Geometry.RectangleToPack;
            import java.util.ArrayList;

            public class RectanglePackingTest {
                private static void showDebugView(ArrayList<RectangleToPack<Integer>> rectangles) {
                    return;
                    if (!MsaglTestBase.enableDebugViewer()) {
                        return;
                    }
                    var shapes = rectangles.stream()
                        .map(r -> new DebugCurve(CurveFactory.createRectangle(r.getRectangle())))
                        .collect(java.util.stream.Collectors.toList());
                    LayoutAlgorithmSettings.getShowDebugCurvesEnumeration().apply(shapes);
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("RectanglePackingTest.java", generated);

        Assert.Contains("private static void showDebugView", output);
        Assert.Contains("return;", output);
        Assert.DoesNotContain("MsaglTestBase.enableDebugViewer()", output);
        Assert.DoesNotContain("var shapes = rectangles.stream()", output);
    }

    [Fact]
    public void ApplyCompatibilityRewritesForTesting_RectanglePackingTest_IsIdempotent()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests;

            import Microsoft.Msagl.Core.Geometry.Rectangle;
            import Microsoft.Msagl.Core.Geometry.RectanglePacking;
            import Microsoft.Msagl.Core.Geometry.RectangleToPack;
            import java.util.stream.StreamSupport;

            public class RectanglePackingTest {
                public void test(ArrayList<RectangleToPack<Integer>> rectangles) {
                    Assertions.assertFalse(isOverlapping(StreamSupport.stream(rectangles.spliterator(), false).map(RectangleToPack<Integer>::getRectangle).collect(java.util.stream.Collectors.toList())), "There are overlaps between the packed rectangles");
                    RectanglePacking<Integer> rectanglePacking = new RectanglePacking<Integer>(rectangles, maxWidth, false);
                }

                private static boolean isOverlapping(Iterable<Rectangle> rectangles) {
                    return false;
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("RectanglePackingTest.java", generated);

        Assert.Contains("isOverlapping(StreamSupport.stream(rectangles.spliterator(), false).map(RectangleToPack<Integer>::getRectangle).collect(java.util.stream.Collectors.toList()))", output);
        Assert.DoesNotContain("new RectanglePacking<Integer>(rectangles, maxWidth)", output);
        Assert.DoesNotContain("UUID.newGuid()", output);
    }
}