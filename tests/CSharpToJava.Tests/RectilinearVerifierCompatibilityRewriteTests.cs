using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class RectilinearVerifierCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_RectilinearVerifier_PreservesWrapperTypesForNullableOverrides()
    {
        // Transformer now produces boxed types directly via MapTypeInternal() for Nullable<T>,
        // so the post-processor no longer needs to patch them. Verify they're preserved.
        const string generated = """
            package Microsoft.Msagl.UnitTests.Rectilinear;

            public class RectilinearVerifier {
                private Double overrideRouterPadding;
                private Boolean overrideWantVerify;

                protected Double getOverrideRouterPadding() {
                    return overrideRouterPadding;
                }

                protected void setOverrideRouterPadding(Double value) {
                    this.overrideRouterPadding = value;
                }

                protected Boolean getOverrideWantVerify() {
                    return overrideWantVerify;
                }

                protected void setOverrideWantVerify(Boolean value) {
                    this.overrideWantVerify = value;
                }

                protected void clearOverrideMembers() {
                    this.setOverrideRouterPadding(null);
                    this.setOverrideWantVerify(null);
                }

                protected void overrideMembers() {
                    var _coalesce1 = this.getOverrideRouterPadding();
                    this.setRouterPadding(_coalesce1 != null ? _coalesce1 : this.getRouterPadding());
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("RectilinearVerifier.java", generated);

        Assert.Contains("private Double overrideRouterPadding;", output);
        Assert.Contains("private Boolean overrideWantVerify;", output);
        Assert.Contains("protected Double getOverrideRouterPadding()", output);
        Assert.Contains("protected void setOverrideRouterPadding(Double value)", output);
        Assert.Contains("protected Boolean getOverrideWantVerify()", output);
        Assert.Contains("protected void setOverrideWantVerify(Boolean value)", output);
    }

    [Fact]
    public void ApplyCompatibilityRewritesForTesting_RectilinearVerifier_RewritesEnumerableReverseOfPoints()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests.Rectilinear;

            import java.util.ArrayList;
            import java.util.Collections;
            import java.util.stream.Collectors;
            import java.util.stream.StreamSupport;

            public class RectilinearVerifier {
                public Shape polylineFromPoints(Point[] points) {
                    return new Shape(new Polyline(StreamSupport.stream(Enumerable.spliterator(), false).collect(Collectors.collectingAndThen(Collectors.toCollection(ArrayList::new), list -> { Collections.reverse(list); return list; }))));
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("RectilinearVerifier.java", generated);

        Assert.Contains("new Microsoft.Msagl.Core.Geometry.Curves.Polyline(new ArrayList<Point>(StreamSupport.stream(java.util.Arrays.asList(points).spliterator(), false)", output);
        Assert.DoesNotContain("Enumerable.spliterator()", output);
    }

    [Fact]
    public void ApplyCompatibilityRewritesForTesting_RectilinearVerifier_QualifiesPolylineConstructors()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests.Rectilinear;

            public class RectilinearVerifier {
                public Shape polylineFromPoints(Point[] points) {
                    var _obj80 = new Polyline(points);
                    var _obj81 = new Polyline(StreamSupport.stream(java.util.Arrays.asList(points).spliterator(), false).collect(Collectors.collectingAndThen(Collectors.toCollection(ArrayList::new), list -> { Collections.reverse(list); return list; })));
                    return null;
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("RectilinearVerifier.java", generated);

        Assert.Contains("new Microsoft.Msagl.Core.Geometry.Curves.Polyline(points)", output);
        Assert.Contains("new Microsoft.Msagl.Core.Geometry.Curves.Polyline(new ArrayList<Point>(StreamSupport.stream(java.util.Arrays.asList(points).spliterator(), false)", output);
    }

    [Fact]
    public void ApplyCompatibilityRewritesForTesting_RectilinearVerifier_RewritesApplicationException()
    {
        const string generated = """
            package Microsoft.Msagl.UnitTests.Rectilinear;

            public class RectilinearVerifier {
                public void test() {
                    throw new ApplicationException("Can't specify default creation of both freePorts and waypoints");
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("RectilinearVerifier.java", generated);

        Assert.Contains("throw new RuntimeException(\"Can't specify default creation of both freePorts and waypoints\")", output);
        Assert.DoesNotContain("ApplicationException", output);
    }
}