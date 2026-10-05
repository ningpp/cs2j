using CSharpToJava.Core.LinqChannel;

namespace CSharpToJava.Tests.LinqChannel;

/// <summary>
/// M1 exit criterion (design §4.1/§7.1): the channel pipeline must be
/// output-equivalent to the embedded E16 path on Enumerable inputs. The
/// mechanics live in <see cref="LinqChannelDualPathHarness"/>; the full M2
/// operator matrix is <see cref="LinqChannelOperatorMatrixTests"/>.
/// </summary>
public sealed class LinqChannelEquivalenceTests
{
    [Fact]
    public void ChannelWithGate_MatchesEmbeddedE16_OnEnumerableMethodChain()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;

            public static class EquivHost
            {
                public static int[] Chain(int[] src)
                    => src.Where(x => x > 0).Select(x => x * 2).ToArray();

                public static List<int> Ordered(IEnumerable<int> src)
                    => src.OrderBy(x => x).ThenByDescending(x => x).ToList();
            }
            """;

        var result = LinqChannelDualPathHarness.RunBothPaths(source);
        var statistics = result.Channel.Statistics;

        Assert.Equal(result.EmbeddedText, result.ChannelText);
        Assert.True(result.Channel.RewriteChanged, "the stock rewriter must lower the Enumerable chains");
        Assert.True(result.Channel.SyntacticallyIdempotent);
        Assert.True(statistics.EnumerableChainsExpanded >= 2);
        Assert.Equal(0, statistics.QueryableChainsPreserved);
        Assert.Equal(0, statistics.AmbiguousChains);
    }

    [Fact]
    public void ChannelWithGate_MatchesEmbeddedE16_OnQueryExpressionAndForeachRewrap()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;

            public static class EquivHost
            {
                public static int QueryAndLoops(List<int> values, int[] numbers)
                {
                    var q = from x in values
                            where x % 2 == 0
                            select x + 1;

                    var total = 0;
                    foreach (var v in q)
                    {
                        total += v;
                    }

                    // Plain List/array foreach: the foreach-rewrap entry point
                    // also carries the gate seam and must stay equivalent.
                    foreach (var v in values)
                    {
                        total += v;
                    }

                    foreach (var n in numbers)
                    {
                        total += n;
                    }

                    return total;
                }
            }
            """;

        var result = LinqChannelDualPathHarness.RunBothPaths(source);
        var statistics = result.Channel.Statistics;

        Assert.Equal(result.EmbeddedText, result.ChannelText);
        Assert.True(result.Channel.RewriteChanged, "desugared query + foreach sites must be rewritten");
        Assert.True(result.Channel.SyntacticallyIdempotent);
        Assert.True(statistics.EnumerableChainsExpanded >= 1);
        Assert.Equal(0, statistics.QueryableChainsPreserved);
        Assert.Equal(0, statistics.AmbiguousChains);
    }
}
