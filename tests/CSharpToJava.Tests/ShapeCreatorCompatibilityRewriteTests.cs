using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ShapeCreatorCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_ShapeCreator_FixesMapPutChainAssignments()
    {
        const string generated = """
            package Microsoft.Msagl.Routing;

            public class ShapeCreator {
                private static void getShapesOnDict(HashMap<Node, Shape> nodesToShapes, Cluster c) {
                    Shape cShape = nodesToShapes.get(c);
                    if (cShape == null) {
                    var _chainVal14 = nodesToShapes.put(c, createShapeWithClusterBoundaryPort(c));
                    cShape = _chainVal14;
                    }
                    Shape nShape = nodesToShapes.get(null);
                    if (nShape == null) {
                    var _chainVal15 = nodesToShapes.put(n, createShapeWithCenterPort(n));
                    nShape = _chainVal15;
                    }
                    if (nShape == null) {
                    var _chainVal16 = nodesToShapes.put(cc, createShapeWithCenterPort(cc));
                    nShape = _chainVal16;
                    }
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("ShapeCreator.java", generated);

        Assert.Contains("cShape = createShapeWithClusterBoundaryPort(c);", output);
        Assert.Contains("nodesToShapes.put(c, cShape);", output);
        Assert.Contains("nShape = createShapeWithCenterPort(n);", output);
        Assert.Contains("nodesToShapes.put(n, nShape);", output);
        Assert.Contains("nShape = createShapeWithCenterPort(cc);", output);
        Assert.Contains("nodesToShapes.put(cc, nShape);", output);
        Assert.DoesNotContain("_chainVal14", output);
        Assert.DoesNotContain("_chainVal15", output);
        Assert.DoesNotContain("_chainVal16", output);
    }
}