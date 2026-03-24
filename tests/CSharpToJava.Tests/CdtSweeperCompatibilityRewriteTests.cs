using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class CdtSweeperCompatibilityRewriteTests
{
    [Fact]
    public void ApplyCompatibilityRewritesForTesting_CdtSweeper_RewritesConditionalOutHolderSelection()
    {
        const string generated = """
            package Microsoft.Msagl.Routing.ConstrainedDelaunayTriangulation;

            public class CdtSweeper {
                void pointEvent(CdtSite pi) {
                    CdtSite rightSite;
                    ObjectHolder<CdtSite> _rightSiteHolder1 = new ObjectHolder<>();
                    ObjectHolder<CdtSite> _rightSiteHolder2 = new ObjectHolder<>();
                    CdtSite leftSite = (hittedFrontElementNode.Item.getX() + ApproximateComparer.DistanceEpsilon < pi.Point.X ? middleCase(pi, hittedFrontElementNode, _rightSiteHolder1) : leftCase(pi, hittedFrontElementNode, _rightSiteHolder2));
                    rightSite = _rightSiteHolder1.value;
                    rightSite = _rightSiteHolder2.value;
                }
            }
            """;

        var output = ProjectConversionPipeline.ApplyCompatibilityRewritesForTesting("CdtSweeper.java", generated).Replace("\r\n", "\n");

        Assert.Contains("CdtSite leftSite;", output);
        Assert.Contains("leftSite = middleCase(pi, hittedFrontElementNode, _rightSiteHolder1);", output);
        Assert.Contains("rightSite = _rightSiteHolder1.value;", output);
        Assert.Contains("leftSite = leftCase(pi, hittedFrontElementNode, _rightSiteHolder2);", output);
        Assert.Contains("rightSite = _rightSiteHolder2.value;", output);
        Assert.DoesNotContain("CdtSite leftSite = (hittedFrontElementNode.Item.getX() + ApproximateComparer.DistanceEpsilon < pi.Point.X ? middleCase(pi, hittedFrontElementNode, _rightSiteHolder1) : leftCase(pi, hittedFrontElementNode, _rightSiteHolder2));", output);
    }
}