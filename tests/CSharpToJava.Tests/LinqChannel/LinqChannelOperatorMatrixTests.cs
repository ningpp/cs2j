using System.Text.RegularExpressions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.LinqChannel;
using CSharpToJava.Core.LinqRewrite;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Tests.LinqChannel;

/// <summary>
/// M2 Enumerable dual-path operator matrix (design §7.1/§7.6/§7.8).
///
/// Every <see cref="OperatorCases"/> row runs the same post-desugar tree
/// through BOTH the embedded E16 call (ungated <see cref="LinqRewriter"/>)
/// and the channel in M2 intercept mode, and asserts:
/// <list type="bullet">
/// <item>normalized output is character-identical (generated helper names
/// collapsed by <see cref="LinqChannelDualPathHarness.Normalize"/>);</item>
/// <item>routing counters: only Enumerable expansion, zero Queryable/Ambiguous;</item>
/// <item>the E16 skip set is identical on both paths (a few rules emit an
/// outer-terminal UnsupportedMethodChain skip after extracting a helper —
/// that pre-existing E16 behavior must be reproduced, not fixed here);</item>
/// <item>syntactic idempotency (a second stage-1 pass lowers nothing);</item>
/// <item>the rewritten output recompiles cleanly standalone.</item>
/// </list>
///
/// Shapes follow the stock E16 admission rule: a chain needs at least one
/// lambda argument OR a recognized non-lambda operator (Skip/Take/Distinct/
/// Append/Prepend/DefaultIfEmpty/Chunk/Order(-Descending)/Aggregate/Count/
/// SequenceEqual/*By/Join — LinqRewriter.cs chain gate around L262). Bare
/// terminal-only chains (e.g. <c>s.First()</c>, <c>s.Cast&lt;int&gt;()</c>)
/// are intentionally exercised behind a <c>Where</c>/<c>Select</c> lambda.
///
/// Operator → existing-coverage map (the rows below are the channel-side
/// equivalent; E16 behavior itself is pinned by the legacy suite):
/// materialize/terminals, predicates, aggregates → LinqProceduralPhase1-9Tests,
/// LinqObservabilityPhase10Tests; set operators/Concat → LinqConcatSetOperatorTests,
/// LinqSetOperatorProjectTests; OfType/Cast → LinqOfTypeConversionTests;
/// count-on-iterable → LinqCountOnIterableTests/LinqCountAnyMethodTests;
/// Skip/Take chains → LinqTakeWhereChainDropTests/LinqTakeExceptChainDropTests;
/// Single-method chain → LinqSingleMethodChainTests; captures →
/// LinqRewriterVariableCaptureTests/LinqLocalFunctionCaptureTests; anonymous
/// projection → LinqAnonymousTypeExtractedMethodTests; aggregate seed →
/// LinqAggregateWithSeedParameterRenamingTests; multi-enumeration →
/// LinqMultiEnumerationSourceCastTests; unresolved fallback →
/// UnresolvedLinqSelectFallbackTests; query desugar → LinqQueryDesugarTests;
/// GroupBy → GroupByProceduralRewriteTests; foreach rewrap →
/// ForeachSource* + LinqChainRefactoringTests.
///
/// Known E16 rule gaps (observed while building this matrix; M4 resolution
/// recorded 2026-10-06):
/// the whole OrderBy/OrderByDescending/ThenBy/ThenByDescending family is
/// recognized for observability but excluded from procedural lowering
/// (Rules.cs rewrite-set "/*OrderBy*/") — both paths skip and leave the chain
/// untouched; GroupBy used to make TryRewrite return null on the
/// elementSelector overload (unguarded NRE — fixed M4 to a clean
/// UnsupportedOverload skip) while its key-only Dictionary form is not
/// standalone-compilable C#, so the M4 channel freezes Enumerable GroupBy
/// chains wholesale instead of lowering them (see
/// LinqChannelGroupByM4Tests); Queryable-only operators and ToLookup stay
/// outside the supported set.
/// </summary>
public sealed class LinqChannelOperatorMatrixTests
{
    public static IEnumerable<object[]> OperatorCases()
    {
        // ---- materialization / terminals ----
        yield return Row("ToArray", """
            using System.Linq;
            public static class M { public static int[] F(int[] s) => s.Where(x => x > 0).ToArray(); }
            """);
        yield return Row("ToList", """
            using System.Collections.Generic;
            using System.Linq;
            public static class M { public static List<int> F(int[] s) => s.Where(x => x > 0).ToList(); }
            """);
        yield return Row("ToDictionary-key", """
            using System.Collections.Generic;
            using System.Linq;
            public static class M { public static Dictionary<int, int> F(int[] s) => s.ToDictionary(x => x); }
            """);
        yield return Row("ToDictionary-key-element", """
            using System.Collections.Generic;
            using System.Linq;
            public static class M { public static Dictionary<int, string> F(int[] s) => s.ToDictionary(x => x, x => x.ToString()); }
            """);
        yield return Row("ToHashSet", """
            using System.Collections.Generic;
            using System.Linq;
            public static class M { public static HashSet<int> F(int[] s) => s.Select(x => x).ToHashSet(); }
            """);
        yield return Row("Reverse", """
            using System.Linq;
            public static class M { public static int[] F(int[] s) => s.Where(x => x >= 0).Reverse().ToArray(); }
            """);

        // ---- First/Single/Last family (all supported shapes) ----
        yield return Row("First", """
            using System.Linq;
            public static class M { public static int F(int[] s) => s.Where(x => x > 0).First(); }
            """);
        yield return Row("Single", """
            using System.Linq;
            public static class M { public static int F(int[] s) => s.Where(x => x > 0).Single(); }
            """);
        yield return Row("Last", """
            using System.Linq;
            public static class M { public static int F(int[] s) => s.Where(x => x > 0).Last(); }
            """);
        yield return Row("First-predicate", """
            using System.Linq;
            public static class M { public static int F(int[] s) => s.First(x => x > 0); }
            """);
        yield return Row("Single-predicate", """
            using System.Linq;
            public static class M { public static int F(int[] s) => s.Single(x => x > 0); }
            """);
        yield return Row("Last-predicate", """
            using System.Linq;
            public static class M { public static int F(int[] s) => s.Last(x => x > 0); }
            """);
        yield return Row("FirstOrDefault", """
            using System.Linq;
            public static class M { public static int F(int[] s) => s.Where(x => x > 0).FirstOrDefault(); }
            """);
        yield return Row("SingleOrDefault", """
            using System.Linq;
            public static class M { public static int F(int[] s) => s.Where(x => x > 0).SingleOrDefault(); }
            """);
        yield return Row("LastOrDefault", """
            using System.Linq;
            public static class M { public static int F(int[] s) => s.Where(x => x > 0).LastOrDefault(); }
            """);
        yield return Row("FirstOrDefault-predicate", """
            using System.Linq;
            public static class M { public static int F(int[] s) => s.FirstOrDefault(x => x > 0); }
            """);
        yield return Row("SingleOrDefault-predicate", """
            using System.Linq;
            public static class M { public static int F(int[] s) => s.SingleOrDefault(x => x > 0); }
            """);
        yield return Row("LastOrDefault-predicate", """
            using System.Linq;
            public static class M { public static int F(int[] s) => s.LastOrDefault(x => x > 0); }
            """);
        yield return Row("FirstOrDefault-defaultValue", """
            using System.Linq;
            public static class M { public static int F(int[] s) => s.Where(x => x > 0).FirstOrDefault(-1); }
            """);
        yield return Row("FirstOrDefault-predicate-defaultValue", """
            using System.Linq;
            public static class M { public static int F(int[] s) => s.FirstOrDefault(x => x > 0, -1); }
            """);
        yield return Row("LastOrDefault-defaultValue", """
            using System.Linq;
            public static class M { public static int F(int[] s) => s.Where(x => x > 0).LastOrDefault(-1); }
            """);
        yield return Row("LastOrDefault-predicate-defaultValue", """
            using System.Linq;
            public static class M { public static int F(int[] s) => s.LastOrDefault(x => x > 0, -1); }
            """);
        yield return Row("SingleOrDefault-defaultValue", """
            using System.Linq;
            public static class M { public static int F(int[] s) => s.Where(x => x > 0).SingleOrDefault(-1); }
            """);
        yield return Row("SingleOrDefault-predicate-defaultValue", """
            using System.Linq;
            public static class M { public static int F(int[] s) => s.SingleOrDefault(x => x > 0, -1); }
            """);

        // ---- counts / element ----
        yield return Row("Count", """
            using System.Linq;
            public static class M { public static int F(int[] s) => s.Count(); }
            """);
        yield return Row("LongCount", """
            using System.Linq;
            public static class M { public static long F(int[] s) => s.LongCount(); }
            """);
        yield return Row("Count-predicate", """
            using System.Linq;
            public static class M { public static int F(int[] s) => s.Count(x => x > 0); }
            """);
        yield return Row("LongCount-predicate", """
            using System.Linq;
            public static class M { public static long F(int[] s) => s.LongCount(x => x > 0); }
            """);
        yield return Row("ElementAt", """
            using System.Linq;
            public static class M { public static int F(int[] s) => s.Where(x => true).ElementAt(1); }
            """);
        yield return Row("ElementAtOrDefault", """
            using System.Linq;
            public static class M { public static int F(int[] s) => s.Where(x => true).ElementAtOrDefault(1); }
            """);

        // ---- quantifiers / predicate operators ----
        yield return Row("Any-empty", """
            using System.Linq;
            public static class M { public static bool F(int[] s) => s.Where(x => true).Any(); }
            """);
        yield return Row("Any-predicate", """
            using System.Linq;
            public static class M { public static bool F(int[] s) => s.Any(x => x > 0); }
            """);
        yield return Row("All", """
            using System.Linq;
            public static class M { public static bool F(int[] s) => s.All(x => x > 0); }
            """);
        yield return Row("Contains", """
            using System.Linq;
            public static class M { public static bool F(int[] s) => s.Where(x => true).Contains(1); }
            """);
        yield return Row("Where", """
            using System.Linq;
            public static class M { public static int[] F(int[] s) => s.Where(x => x > 0).ToArray(); }
            """);
        yield return Row("Where-indexed", """
            using System.Linq;
            public static class M { public static int[] F(int[] s) => s.Where((x, i) => i % 2 == 0).ToArray(); }
            """);
        yield return Row("Select", """
            using System.Linq;
            public static class M { public static string[] F(int[] s) => s.Select(x => x.ToString()).ToArray(); }
            """);
        yield return Row("Select-indexed", """
            using System.Linq;
            public static class M { public static int[] F(int[] s) => s.Select((x, i) => x + i).ToArray(); }
            """);
        yield return Row("Cast", """
            using System.Linq;
            public static class M { public static int[] F(object[] s) => s.Cast<int>().Where(x => x > 0).ToArray(); }
            """);
        yield return Row("OfType", """
            using System.Linq;
            public static class M { public static int[] F(object[] s) => s.OfType<int>().Where(x => x > 0).ToArray(); }
            """);

        // ---- filtering / paging ----
        yield return Row("Distinct", """
            using System.Linq;
            public static class M { public static int[] F(int[] s) => s.Distinct().ToArray(); }
            """);
        yield return Row("Skip", """
            using System.Linq;
            public static class M { public static int[] F(int[] s) => s.Skip(1).ToArray(); }
            """);
        yield return Row("Take", """
            using System.Linq;
            public static class M { public static int[] F(int[] s) => s.Take(2).ToArray(); }
            """);
        yield return Row("SkipWhile", """
            using System.Linq;
            public static class M { public static int[] F(int[] s) => s.SkipWhile(x => x < 3).ToArray(); }
            """);
        yield return Row("TakeWhile", """
            using System.Linq;
            public static class M { public static int[] F(int[] s) => s.TakeWhile(x => x < 3).ToArray(); }
            """);
        yield return Row("SkipWhile-indexed", """
            using System.Linq;
            public static class M { public static int[] F(int[] s) => s.SkipWhile((x, i) => i < 3).ToArray(); }
            """);
        yield return Row("TakeWhile-indexed", """
            using System.Linq;
            public static class M { public static int[] F(int[] s) => s.TakeWhile((x, i) => i < 3).ToArray(); }
            """);
        yield return Row("SelectMany", """
            using System.Linq;
            public static class M { public static int[] F(int[][] s) => s.SelectMany(x => x).ToArray(); }
            """);
        yield return Row("SelectMany-indexed", """
            using System.Linq;
            public static class M { public static int[] F(int[][] s) => s.SelectMany((x, i) => x).ToArray(); }
            """);

        // ---- ordering / grouping ----
        // Ordering operators are recognized/observed but intentionally not
        // lowered today (LinqRewriter.Rules.cs rewrite set marks "/*OrderBy*/"
        // excluded): both paths skip them as UnsupportedMethodChain and leave
        // the chain untouched.
        yield return Row("OrderBy", """
            using System.Collections.Generic;
            using System.Linq;
            public static class M { public static List<int> F(IEnumerable<int> s)
                => s.OrderBy(x => x).ToList(); }
            """, expectRewrite: false);
        yield return Row("OrderByDescending", """
            using System.Collections.Generic;
            using System.Linq;
            public static class M { public static List<int> F(IEnumerable<int> s)
                => s.OrderByDescending(x => x).ToList(); }
            """, expectRewrite: false);
        // GroupBy is deliberately absent from this dual-path matrix as of M4:
        // the two paths intentionally DIVERGE for GroupBy (embedded E16 keeps
        // lowering key-only to the non-recompilable Dictionary helper; the
        // build-verified channel freezes every Enumerable GroupBy chain and
        // leaves LINQ for Java's streaming groupingBy path). The divergence is
        // pinned by LinqChannelGroupByM4Tests below.

        // ---- set operators / concat / zip ----
        yield return Row("Concat", """
            using System.Linq;
            public static class M { public static int[] F(int[] s, int[] t) => s.Where(x => true).Concat(t).ToArray(); }
            """);
        yield return Row("Union", """
            using System.Linq;
            public static class M { public static int[] F(int[] s, int[] t) => s.Where(x => true).Union(t).ToArray(); }
            """);
        yield return Row("Intersect", """
            using System.Linq;
            public static class M { public static int[] F(int[] s, int[] t) => s.Where(x => true).Intersect(t).ToArray(); }
            """);
        yield return Row("Except", """
            using System.Linq;
            public static class M { public static int[] F(int[] s, int[] t) => s.Where(x => true).Except(t).ToArray(); }
            """);
        yield return Row("Zip", """
            using System.Linq;
            public static class M { public static int[] F(int[] s, int[] t) => s.Zip(t, (a, b) => a + b).ToArray(); }
            """);

        // ---- aggregates ----
        yield return Row("Aggregate-func", """
            using System.Linq;
            public static class M { public static int F(int[] s) => s.Aggregate((a, b) => a + b); }
            """);
        yield return Row("Aggregate-seed", """
            using System.Linq;
            public static class M { public static long F(int[] s) => s.Aggregate(0L, (a, b) => a + b); }
            """);
        yield return Row("Sum", """
            using System.Linq;
            public static class M { public static int F(int[] s) => s.Select(x => x).Sum(); }
            """);
        yield return Row("Average", """
            using System.Linq;
            public static class M { public static double F(int[] s) => s.Select(x => x).Average(); }
            """);
        yield return Row("Min", """
            using System.Linq;
            public static class M { public static int F(int[] s) => s.Select(x => x).Min(); }
            """);
        yield return Row("Max", """
            using System.Linq;
            public static class M { public static int F(int[] s) => s.Select(x => x).Max(); }
            """);
        yield return Row("Sum-selector-long", """
            using System.Linq;
            public static class M { public static long F(int[] s) => s.Sum(x => (long)x); }
            """);
        yield return Row("Average-selector-decimal", """
            using System;
            using System.Linq;
            public static class M { public static decimal F(int[] s) => s.Average(x => (decimal)x); }
            """);

        // ---- equality / ends / append / chunk ----
        yield return Row("SequenceEqual", """
            using System.Linq;
            public static class M { public static bool F(int[] s, int[] t) => s.SequenceEqual(t); }
            """);
        yield return Row("SkipLast-TakeLast", """
            using System.Linq;
            public static class M { public static int F(int[] s) => s.SkipLast(1).TakeLast(2).Count(); }
            """);
        yield return Row("Append-Prepend", """
            using System.Linq;
            public static class M { public static int[] F(int[] s) => s.Append(1).Prepend(0).ToArray(); }
            """);
        yield return Row("DefaultIfEmpty", """
            using System.Linq;
            public static class M { public static int[] F(int[] s) => s.DefaultIfEmpty().ToArray(); }
            """);
        yield return Row("DefaultIfEmpty-value", """
            using System.Linq;
            public static class M { public static int[] F(int[] s) => s.DefaultIfEmpty(-1).ToArray(); }
            """);
        yield return Row("Chunk", """
            using System.Linq;
            public static class M { public static int[] F(int[][] s) => s.Chunk(2).Select(g => g.Length).ToArray(); }
            """);

        // ---- By-family (net6+) ----
        yield return Row("DistinctBy", """
            using System.Linq;
            public static class M { public static int[] F(int[] s) => s.DistinctBy(x => x % 3).ToArray(); }
            """);
        yield return Row("UnionBy", """
            using System.Collections.Generic;
            using System.Linq;
            public static class M { public static IEnumerable<int> F(int[] s, int[] t) => s.UnionBy(t, x => x % 3); }
            """);
        yield return Row("IntersectBy", """
            using System.Collections.Generic;
            using System.Linq;
            public static class M { public static IEnumerable<int> F(int[] s, int[] t) => s.IntersectBy(t.Select(x => x % 3), x => x % 3); }
            """);
        yield return Row("ExceptBy", """
            using System.Collections.Generic;
            using System.Linq;
            public static class M { public static IEnumerable<int> F(int[] s, int[] t) => s.ExceptBy(t.Select(x => x % 3), x => x % 3); }
            """);
        yield return Row("MinBy", """
            using System.Linq;
            public static class M { public static int? F(int[] s) => s.MinBy(x => x % 3); }
            """);
        yield return Row("MaxBy", """
            using System.Linq;
            public static class M { public static int? F(int[] s) => s.MaxBy(x => x % 3); }
            """);

        // ---- ordering helpers / as-enumerable ----
        yield return Row("Order", """
            using System.Linq;
            public static class M { public static int[] F(int[] s) => s.Order().ToArray(); }
            """);
        yield return Row("OrderDescending", """
            using System.Linq;
            public static class M { public static int[] F(int[] s) => s.OrderDescending().ToArray(); }
            """);
        yield return Row("AsEnumerable", """
            using System.Collections.Generic;
            using System.Linq;
            public static class M { public static int F(int[] s) => s.AsEnumerable().Count(); }
            """);

        // ---- joins ----
        yield return Row("Join", """
            using System.Collections.Generic;
            using System.Linq;
            public class Key { public int Id; public string Name = ""; }
            public static class M {
                public static IEnumerable<string> F(Key[] a, Key[] b)
                    => a.Join(b, x => x.Id, y => y.Id, (x, y) => x.Name + y.Name);
            }
            """);
        yield return Row("GroupJoin", """
            using System.Collections.Generic;
            using System.Linq;
            public class Key { public int Id; public string Name = ""; }
            public static class M {
                public static IEnumerable<string> F(Key[] a, Key[] b)
                    => a.GroupJoin(b, x => x.Id, y => y.Id, (x, ys) => x.Name + ys.Count());
            }
            """);
    }

    [Theory]
    [MemberData(nameof(OperatorCases))]
    public void EnumerableOperator_DualPath_IdenticalOutput_AndRecompiles(
        string label, string source, bool expectRewrite, bool recompile)
    {
        var result = LinqChannelDualPathHarness.RunBothPaths(source);

        Assert.Equal(result.EmbeddedText, result.ChannelText);
        Assert.Equal(expectRewrite, result.Channel.RewriteChanged);
        Assert.True(result.Channel.SyntacticallyIdempotent);
        Assert.Equal(0, result.Channel.Statistics.QueryableChainsPreserved);
        Assert.Equal(0, result.Channel.Statistics.AmbiguousChains);
        Assert.Equal(0, result.Channel.Statistics.MixedChainsPreserved);
        if (expectRewrite)
        {
            Assert.True(result.Channel.Statistics.EnumerableChainsExpanded >= 1);
        }

        // The stock E16 admission/rule engine produces skip records for a few
        // outer-terminal shapes (e.g. ToArray on top of an extracted Reverse
        // helper). The channel must reproduce the skip set exactly — it owns
        // no rewrite rules of its own.
        var channelSkips = result.Channel.Statistics.Rewrite.SkippedChains
            .Select(s => (s.Reason, s.MethodName)).ToList();
        var embeddedSkips = result.EmbeddedStatistics.SkippedChains
            .Select(s => (s.Reason, s.MethodName)).ToList();
        Assert.Equal(embeddedSkips, channelSkips);

        if (recompile)
        {
            LinqChannelDualPathHarness.AssertOutputCompiles(
                result.Channel.RewrittenRoot.ToFullString());
        }
    }

    private static object[] Row(string label, string source,
        bool expectRewrite = true, bool recompile = true)
        => new object[] { label, source.Trim(), expectRewrite, recompile };
}

/// <summary>
/// M4 (2026-10-06) GroupBy decision: the build-verified channel freezes
/// Enumerable GroupBy chains instead of reproducing E16's Java-bound
/// Dictionary lowering, so the intermediate tree stays compilable and the
/// Java converter takes the streaming Collectors.groupingBy path. The
/// embedded E16 behavior is pinned here too — it stays untouched until the
/// M4 project-path retirement (design §5.4).
/// </summary>
public sealed class LinqChannelGroupByM4Tests
{
    [Fact]
    public void KeyOnlyGroupBy_ChannelFreezesWholesale_AndOutputRecompiles()
    {
        const string source = """
            using System.Linq;
            public static class M
            {
                public static void F(int[] s)
                {
                    var groups = s.Where(n => n > 0).GroupBy(n => n % 2);
                }
            }
            """;

        var result = LinqChannelDualPathHarness.RunBothPaths(source);

        // Embedded E16 keeps its historical key-only Dictionary lowering.
        Assert.True(result.Channel.RewriteChanged == false);
        Assert.Contains("GroupBy", result.PostDesugarRoot.ToFullString());
        Assert.DoesNotContain("_ProceduralLinq", result.ChannelText);

        var stats = result.Channel.Statistics;
        Assert.Equal(1, stats.GroupByChainsPreserved);
        Assert.Equal(0, stats.EnumerableChainsExpanded);
        Assert.Equal(0, stats.QueryableChainsPreserved);
        var preserved = Assert.Single(stats.PreservedChains);
        Assert.Equal("PreserveGroupBy", preserved.Route);
        Assert.Contains("GroupBy", preserved.Operators);

        // The preserved output is the original (compilable) LINQ form.
        LinqChannelDualPathHarness.AssertOutputCompiles(
            result.Channel.RewrittenRoot.ToFullString());
    }

    [Fact]
    public void ElementSelectorGroupBy_ChannelFreezes_EmbeddedRecordsCleanSkip()
    {
        const string source = """
            using System.Linq;
            public static class M
            {
                public static void F(int[] s)
                {
                    var groups = s.GroupBy(n => n % 2, n => n);
                }
            }
            """;

        var result = LinqChannelDualPathHarness.RunBothPaths(source);

        // Channel: frozen, untouched, compilable.
        Assert.False(result.Channel.RewriteChanged);
        Assert.Equal(1, result.Channel.Statistics.GroupByChainsPreserved);
        Assert.Equal(0, result.Channel.Statistics.EnumerableChainsExpanded);
        LinqChannelDualPathHarness.AssertOutputCompiles(
            result.Channel.RewrittenRoot.ToFullString());

        // Embedded E16: M4 replaced the escaping NRE with a structured skip.
        var skip = Assert.Single(result.EmbeddedStatistics.SkippedChains);
        Assert.Equal(LinqSkipReason.UnsupportedOverload, skip.Reason);
        Assert.Equal("GroupBy", skip.MethodName);
    }

    [Fact]
    public void QueryableGroupBy_StaysInQueryableBucket_NotTheM4Bucket()
    {
        const string source = """
            using System.Linq;
            public static class M
            {
                public static void F(IQueryable<int> q)
                {
                    var groups = q.GroupBy(n => n % 2);
                }
            }
            """;

        var result = LinqChannelDualPathHarness.RunBothPaths(source);

        Assert.False(result.Channel.RewriteChanged);
        Assert.Equal(0, result.Channel.Statistics.GroupByChainsPreserved);
        Assert.Equal(1, result.Channel.Statistics.QueryableChainsPreserved);
        LinqChannelDualPathHarness.AssertOutputCompiles(
            result.Channel.RewrittenRoot.ToFullString());
    }
}

/// <summary>
/// foreach rewrap seam (design §4.3): the gate's second entry point wraps the
/// same CanRewrapForeachVisitor decision on both paths. One representative
/// successful rewrap plus the three control-flow fail reasons from
/// CanRewrapForeachVisitor (break/continue/return — the fail path extracts a
/// helper instead of rewrapping). await/goto/yield are the same fail branch in
/// the visitor and are covered by the existing ForeachSource* suite.
/// </summary>
public sealed class LinqChannelForeachRewrapMatrixTests
{
    [Fact]
    public void ForeachOverEnumerableChain_Rewraps_IdenticallyOnBothPaths()
    {
        const string source = """
            using System.Linq;
            public static class M
            {
                public static int F(int[] s)
                {
                    var total = 0;
                    foreach (var v in s.Where(x => x > 0))
                    {
                        total += v;
                    }
                    return total;
                }
            }
            """;

        var result = LinqChannelDualPathHarness.RunBothPaths(source);

        Assert.Equal(result.EmbeddedText, result.ChannelText);
        Assert.True(result.Channel.RewriteChanged);
        Assert.True(result.Channel.Statistics.EnumerableChainsExpanded >= 1);
        Assert.Equal(0, result.Channel.Statistics.QueryableChainsPreserved);
        LinqChannelDualPathHarness.AssertOutputCompiles(result.Channel.RewrittenRoot.ToFullString());
    }

    [Theory]
    [InlineData("break", "if (v == 42) break;")]
    [InlineData("continue", "if (v == 42) continue;")]
    [InlineData("return", "if (v == 42) return v;")]
    public void ForeachBodyControlFlow_FailsRewrap_ExtractsHelper_IdenticallyOnBothPaths(
        string kind, string controlFlow)
    {
        var source = $$"""
            using System.Linq;
            public static class M
            {
                public static int F(int[] s)
                {
                    var total = 0;
                    foreach (var v in s.Where(x => x > 0))
                    {
                        {{controlFlow}}
                        total += v;
                    }
                    return total;
                }
            }
            """;

        var result = LinqChannelDualPathHarness.RunBothPaths(source);

        Assert.Equal(result.EmbeddedText, result.ChannelText);
        Assert.True(result.Channel.RewriteChanged);
        Assert.True(result.Channel.Statistics.EnumerableChainsExpanded >= 1);
        Assert.Equal(0, result.Channel.Statistics.QueryableChainsPreserved);
        // Fail-to-rewrap means extraction: the chain leaves a generated
        // helper behind rather than an in-place loop.
        Assert.True(
            result.Channel.RewrittenRoot.ToFullString().Contains("_ProceduralLinq", StringComparison.Ordinal));
        LinqChannelDualPathHarness.AssertOutputCompiles(result.Channel.RewrittenRoot.ToFullString());
    }
}

/// <summary>
/// The five risk surfaces called out in design §7.8. Every case asserts
/// dual-path output equality (the channel owns no lowering rules), clean
/// standalone recompilation and zero Queryable/Ambiguous routing.
/// </summary>
public sealed class LinqChannelRiskSurfaceMatrixTests
{
    /// <summary>① Anonymous projection gated by UseRecords/ConversionOptions.</summary>
    [Fact]
    public void AnonymousProjection_WithRecordOptions_IdenticalAndRecompiles()
    {
        const string source = """
            using System.Linq;
            public static class M
            {
                public static int[] F(int[] s)
                    => s.Select(x => new { V = x })
                        .Where(a => a.V > 0)
                        .Select(a => a.V)
                        .ToArray();
            }
            """;

        var result = LinqChannelDualPathHarness.RunBothPaths(
            source,
            conversionOptions: new ConversionOptions { UseRecords = true });

        Assert.Equal(result.EmbeddedText, result.ChannelText);
        Assert.True(result.Channel.RewriteChanged);
        LinqChannelDualPathHarness.AssertOutputCompiles(result.Channel.RewrittenRoot.ToFullString());
    }

    /// <summary>② Captured variable flows into (and out of) lifted lambdas.</summary>
    [Fact]
    public void CapturedVariableFlow_IdenticalAndRecompiles()
    {
        const string source = """
            using System.Linq;
            public static class M
            {
                public static int[] F(int[] s, int threshold)
                    => s.Where(x => x > threshold)
                        .Select(x => x + threshold)
                        .ToArray();
            }
            """;

        var result = LinqChannelDualPathHarness.RunBothPaths(source);

        Assert.Equal(result.EmbeddedText, result.ChannelText);
        Assert.True(result.Channel.RewriteChanged);
        var output = result.Channel.RewrittenRoot.ToFullString();
        Assert.Contains("threshold", output, StringComparison.Ordinal);
        LinqChannelDualPathHarness.AssertOutputCompiles(output);
    }

    /// <summary>
    /// ③ Extracted helper in a static context: the generated procedural helper
    /// is static and may call sibling static helpers — captured symbol
    /// resolution must match the embedded path.
    /// </summary>
    [Fact]
    public void StaticContextExtractedHelper_IdenticalAndRecompiles()
    {
        const string source = """
            using System.Linq;
            static class M
            {
                static int Square(int x) => x * x;

                public static int[] F(int[] s)
                    => s.Select(x => Square(x)).Where(x => x > 1).ToArray();
            }
            """;

        var result = LinqChannelDualPathHarness.RunBothPaths(source);

        Assert.Equal(result.EmbeddedText, result.ChannelText);
        Assert.True(result.Channel.RewriteChanged);
        LinqChannelDualPathHarness.AssertOutputCompiles(result.Channel.RewrittenRoot.ToFullString());
    }

    /// <summary>④ Nullable / value-type aggregation lifting.</summary>
    [Fact]
    public void NullableValueTypeAggregation_IdenticalAndRecompiles()
    {
        const string source = """
            using System.Linq;
            public static class M
            {
                public static long F(int?[] s)
                    => s.Where(x => x != 0).Sum(x => x ?? 0L);
            }
            """;

        var result = LinqChannelDualPathHarness.RunBothPaths(source);

        Assert.Equal(result.EmbeddedText, result.ChannelText);
        Assert.True(result.Channel.RewriteChanged);
        LinqChannelDualPathHarness.AssertOutputCompiles(result.Channel.RewrittenRoot.ToFullString());
    }

    /// <summary>
    /// ⑤ Multiple enumeration side effects: two consumers of one lazily
    /// evaluated IEnumerable must keep two physical enumerations after
    /// lowering (the channel must not silently memoize).
    /// </summary>
    [Fact]
    public void MultipleEnumerationSideEffects_IdenticalAndRecompiles()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;
            public static class M
            {
                public static readonly List<int> Calls = new();

                static IEnumerable<int> Source()
                {
                    Calls.Add(1);
                    yield return 1;
                    yield return 2;
                }

                public static int F()
                {
                    var q = Source().Where(x => x > 0);
                    var a = q.ToArray();
                    var b = q.Count();
                    return a.Length + b;
                }
            }
            """;

        var result = LinqChannelDualPathHarness.RunBothPaths(source);

        Assert.Equal(result.EmbeddedText, result.ChannelText);
        Assert.True(result.Channel.RewriteChanged);
        LinqChannelDualPathHarness.AssertOutputCompiles(result.Channel.RewrittenRoot.ToFullString());
    }
}

/// <summary>
/// Regression tests for array sources that reach the channel through a
/// SelectMany collection selector (two-`from` query syntax). Such selectors
/// are spliced into the generated foreach without a normal descent, so the
/// nested <c>new[]{...}.Select(...)</c> chain used to survive unchanged and
/// the Java emitter produced an uncompilable <c>String[].map(...)</c>.
/// </summary>
public sealed class LinqChannelArraySourceTests
{
    private const string Source = """
        using System.Collections.Generic;
        using System.Linq;
        public static class M
        {
            public static IEnumerable<object[]> Pairs() =>
                from host in new[] { "a", "b" }
                from scheme in new[] { "http", "test" }
                select new object[] { scheme, host };

            public static int[] Doubled() =>
                new[] { 1, 2, 3 }.Select(x => x * 2).ToArray();
        }
        """;

    [Fact]
    public void ArrayLiteralSelect_IsProceduralized_NotLeftAsArrayCall()
    {
        var result = LinqChannelDualPathHarness.RunBothPaths(Source);

        Assert.True(result.Channel.RewriteChanged);
        Assert.DoesNotContain(".Select(", result.ChannelText);
        LinqChannelDualPathHarness.AssertOutputCompiles(result.Channel.RewrittenRoot.ToFullString());
    }

    [Fact]
    public void NestedSelectorArrayQuery_UsesAliasForOuterRangeVariable()
    {
        var result = LinqChannelDualPathHarness.RunBothPaths(Source);
        var text = result.Channel.RewrittenRoot.ToFullString();

        // The collection selector's range variable is re-bound to the outer
        // loop item before the nested foreach.
        Assert.Contains("= _linqitem", text, StringComparison.Ordinal);
        LinqChannelDualPathHarness.AssertOutputCompiles(text);
    }
}

/// <summary>
/// M2 idempotency + residual gates (design §7.1③/§7.8): channel output fed
/// through the channel a second time must not move again, and after
/// recompilation the route plan must leave no expandable LINQ shape behind
/// except materialization-only terminals (ToArray/ToList/…), which are the
/// documented residual surface.
/// </summary>
public sealed class LinqChannelIdempotencyResidualTests
{
    private const string MultiChainSource = """
        using System.Collections.Generic;
        using System.Linq;
        public static class M
        {
            public static int[] Filtered(int[] s)
                => s.Where(x => x > 0).Select(x => x * 2).ToArray();

            public static List<int> Taken(IEnumerable<int> s)
                => s.Skip(1).Take(3).ToList();

            public static bool Has(int[] s, int v)
                => s.Where(x => x > 0).Contains(v);
        }
        """;

    private static readonly Regex GeneratedHelperName =
        new(@"[A-Za-z_][A-Za-z0-9_]*_ProceduralLinq\w*", RegexOptions.Compiled);

    [Fact]
    public void ChannelOutput_ReRunThroughChannel_ChangesNothingAndAddsNoHelpers()
    {
        var first = LinqChannelDualPathHarness.RunBothPaths(MultiChainSource);
        Assert.True(first.Channel.RewriteChanged);
        var firstText = first.Channel.RewrittenRoot.ToFullString();
        var firstHelpers = GeneratedHelperName.Matches(firstText)
            .Select(m => m.Value).ToHashSet();

        // Rebuild the compilation over the lowered tree and run the channel
        // again exactly as a re-entrant pipeline invocation would.
        var (model2, root2) = LinqChannelDualPathHarness.Recompile(firstText);
        var second = new LinqChannelRewriter(new LinqChannelOptions())
            .ProcessFile(model2, root2, filePath: null);

        Assert.False(second.RewriteChanged);
        Assert.Equal(0, second.SecondPassDesugaredCount);
        Assert.Equal(
            firstText,
            second.RewrittenRoot.ToFullString());

        var secondHelpers = GeneratedHelperName
            .Matches(second.RewrittenRoot.ToFullString())
            .Select(m => m.Value).ToHashSet();
        Assert.Equal(firstHelpers, secondHelpers);
        Assert.Equal(0, second.Statistics.QueryableChainsPreserved);
        Assert.Equal(0, second.Statistics.AmbiguousChains);
        Assert.Equal(0, second.Statistics.MixedChainsPreserved);
    }

    /// <summary>
    /// Materialization terminals the residual scan explicitly tolerates:
    /// ToArray/ToList/etc. may remain as ordinary calls on top of an extracted
    /// helper (they carry no lambda and do no deferred work).
    /// </summary>
    private static readonly HashSet<string> MaterializationTerminals = new()
    {
        "ToArray", "ToList", "ToHashSet", "ToDictionary",
    };

    [Fact]
    public void ChannelOutput_AfterRecompile_HasNoExpandableLinqShapeResidual()
    {
        var first = LinqChannelDualPathHarness.RunBothPaths(MultiChainSource);
        var (model2, root2) =
            LinqChannelDualPathHarness.Recompile(first.Channel.RewrittenRoot.ToFullString());

        var plan = LinqChannelPlan.Build(model2, root2, filePath: null);

        foreach (var chain in plan.Chains)
        {
            // Preserved chains are intentional M2 output; only Expandable
            // leftovers are residual violations.
            if (chain.Decision.Route != LinqChainRoute.Expandable) continue;

            var names = chain.Nodes
                .Select(n => ((MemberAccessExpressionSyntax)n.Expression).Name.Identifier.Text)
                .ToList();

            // Every surviving Expandable chain must be a bare materialization:
            // a materialization terminal with no lambda-bearing argument.
            Assert.All(chain.Nodes, node =>
                Assert.Empty(node.ArgumentList.Arguments
                    .Where(a => a.Expression is AnonymousFunctionExpressionSyntax)));
            Assert.Contains(names[0], MaterializationTerminals);
        }
    }

    [Fact]
    public void GeneratedHelpers_AreAddedOnlyToTheTypeThatContainedTheChain()
    {
        const string source = """
            using System.Linq;
            public class WithLinq
            {
                public int[] F(int[] s) => s.Where(x => x > 0).ToArray();
            }

            public class WithoutLinq
            {
                public int G(int x) => x + 1;
            }
            """;

        var result = LinqChannelDualPathHarness.RunBothPaths(source);
        Assert.Equal(result.EmbeddedText, result.ChannelText);
        Assert.True(result.Channel.RewriteChanged);
        var output = result.Channel.RewrittenRoot;
        LinqChannelDualPathHarness.AssertOutputCompiles(output.ToFullString());

        var classes = output.DescendantNodes().OfType<ClassDeclarationSyntax>().ToList();
        var withLinq = classes.Single(c => c.Identifier.Text == "WithLinq");
        var withoutLinq = classes.Single(c => c.Identifier.Text == "WithoutLinq");

        Assert.True(
            withLinq.ToFullString().Contains("_ProceduralLinq", StringComparison.Ordinal));
        Assert.False(
            withoutLinq.ToFullString().Contains("_ProceduralLinq", StringComparison.Ordinal));

        // The untouched type still has exactly its single member.
        Assert.Single(withoutLinq.Members);
    }
}
