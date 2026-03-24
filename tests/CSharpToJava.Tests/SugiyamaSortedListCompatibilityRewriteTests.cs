using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class SugiyamaSortedListCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_IncrementalSugiyamaTests_RewritesSortedListValuesAccess()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests;

            public class IncrementalSugiyamaTests {
                public void nodeShapeChange() {
                    String filePath = java.nio.file.Paths.get(this.getTestContext().TestRunDirectory, "Out\\Dots").toString();
                }

                private static void verifyLayersAreEqual(TreeMap<Double, TreeMap<Double, Node>> layers1, TreeMap<Double, TreeMap<Double, Node>> layers2) {
                    List<Node> nodes1 = layers1.getValues().get(i).getValues();
                    List<Node> nodes2 = layers2.getValues().get(i).getValues();
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("IncrementalSugiyamaTests.java", generated);

        Assert.Contains("String filePath = resolveTestDataPath(\"Resources\\\\DotFiles\\\\LevFiles\\\\chat.dot\");", output);
        Assert.Contains("new ArrayList<>(new ArrayList<>(layers1.values()).get(i).values())", output);
        Assert.Contains("new ArrayList<>(new ArrayList<>(layers2.values()).get(i).values())", output);
        Assert.DoesNotContain("getValues().get(i).getValues()", output);
    }

    [Fact]
    public void ApplyCompatibilityRewritesForTesting_SugiyamaValidation_RewritesSortedListHelpers()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests;

            public class SugiyamaValidation {
                public static void validateNeighborConstraint(TreeMap<Double, TreeMap<Double, Node>> layers, TreeMap<Double, Node> newLayer, TreeMap<Double, Node> layer, boolean isVertical, Node node, Node node2) {
                    var orderedKeys = layers.getKeys();
                    layers.add(node.getCenter().Y, newLayer);
                    newLayer.add(node.getCenter().X, node);
                    layers.get(nearestKey).add(node2.getCenter().X, node2);
                    if (layer.getValues().contains(node) && layer.getValues().contains(node2)) {
                    }
                    var left = layer.indexOfKey(node.getCenter().X);
                    var right = layer.indexOfKey(node2.getCenter().Y);
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("SugiyamaValidation.java", generated);

        Assert.Contains("new ArrayList<>(layers.keySet())", output);
        Assert.Contains("layers.put(node.getCenter().Y, newLayer)", output);
        Assert.Contains("newLayer.put(node.getCenter().X, node)", output);
        Assert.Contains("layers.get(nearestKey).put(node2.getCenter().X, node2)", output);
        Assert.Contains("layer.values().contains(node)", output);
        Assert.Contains("layer.values().contains(node2)", output);
        Assert.Contains("new ArrayList<>(layer.keySet()).indexOf(node.getCenter().X)", output);
        Assert.Contains("new ArrayList<>(layer.keySet()).indexOf(node2.getCenter().Y)", output);
        Assert.DoesNotContain("getValues()", output);
        Assert.DoesNotContain("indexOfKey", output);
        Assert.DoesNotContain("getKeys()", output);
        Assert.DoesNotContain(".add(", output);
    }
}