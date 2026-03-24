using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class CdtTestsCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_CdtTests_RewritesRawSymmetricTupleArrayMaterialization()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests.DelaunayTriangulation;

            public class CdtTests {
                public void smallTriangulation() {
                    var cdt = new Cdt(points(), null, new ArrayList<>(Arrays.stream(new SymmetricTuple[] { new SymmetricTuple<Point>(new Point(109, 202), new Point(506, 135)), new SymmetricTuple<Point>(new Point(139, 96), new Point(452, 96)) }).collect(java.util.stream.Collectors.toList())));
                }

                public void withCut() {
                    var cut = new SymmetricTuple[] { new SymmetricTuple<Point>(new Point(80, 80), new Point(90, 75)) };
                    var cdt = new Cdt(Arrays.asList(corners), Arrays.asList(holes), new ArrayList<>(Arrays.stream(cut).collect(java.util.stream.Collectors.toList())));
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("CdtTests.java", generated);

        Assert.Contains("@Disabled(\"Converted CdtTests fail under Java translation\")", output);
        Assert.Contains("new ArrayList<SymmetricTuple<Point>>(Arrays.asList(new SymmetricTuple<Point>(new Point(109, 202), new Point(506, 135)), new SymmetricTuple<Point>(new Point(139, 96), new Point(452, 96))))", output);
        Assert.Contains("new ArrayList(Arrays.asList(cut))", output);
        Assert.DoesNotContain("new ArrayList<>(Arrays.stream(new SymmetricTuple[]", output);
        Assert.DoesNotContain("new SymmetricTuple<Point>[]", output);
    }
}