using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class EdgeLabelPlacementTestCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_EdgeLabelPlacementTest_RewritesIntArrayCollectionAssert()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests;

            public class EdgeLabelPlacementTest {
                public void expandingSearchTest_IncreasingOnly() {
                    int[] expected = new int[] { 0, 1, 2, 3, 4 };
                    var r = new ArrayList<>();
                    CollectionAssert.areEqual(expected, r);
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("EdgeLabelPlacementTest.java", generated);

        Assert.Contains("CollectionAssert.areEqual(Arrays.stream(expected).boxed().collect(Collectors.toList()), r);", output);
        Assert.DoesNotContain("CollectionAssert.areEqual(expected, r);", output);
    }

    [Fact]
    public void ApplyCompatibilityRewritesForTesting_EdgeLabelPlacementTest_RewritesBindingFlagsReflection()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests;

            public class EdgeLabelPlacementTest {
                private static Iterable<Double> getPossibleSides(Label.PlacementSide side, Point derivative) {
                    Method methodInfo = EdgeLabelPlacement.class.getMethod("GetPossibleSides", BindingFlags.Static | BindingFlags.NonPublic);
                    return (Iterable<Double>)(methodInfo.invoke(null, new Object[] { side, derivative }));
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("EdgeLabelPlacementTest.java", generated);

        Assert.Contains("java.lang.reflect.Method methodInfo = EdgeLabelPlacement.class.getDeclaredMethod(\"getPossibleSides\", Label.PlacementSide.class, Point.class);", output);
        Assert.Contains("methodInfo.setAccessible(true);", output);
        Assert.Contains("return Arrays.stream((double[])(methodInfo.invoke(null, side, derivative))).boxed().collect(Collectors.toList());", output);
        Assert.Contains("catch (ReflectiveOperationException e)", output);
        Assert.DoesNotContain("BindingFlags.Static | BindingFlags.NonPublic", output);
    }
}