using System.Text.RegularExpressions;
using CSharpToJava.Core.LinqChannel;
using CSharpToJava.Core.LinqRewrite;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace CSharpToJava.Tests.LinqChannel;

/// <summary>
/// M1 exit criterion (design §4.1/§7.1): the channel pipeline must be
/// output-equivalent to the embedded E16 path on Enumerable inputs.
///
/// For each snippet the same post-desugar tree is fed through:
/// <list type="bullet">
/// <item>E16: <c>new LinqRewriter(model, options)</c> with NO gate — exactly
/// the embedded call shape;</item>
/// <item>channel: <see cref="LinqChannelRewriter.ProcessFile"/> which builds
/// the route plan and attaches the M1 ObserveOnlyGate to an otherwise
/// identical LinqRewriter.</item>
/// </list>
/// Equality is compared after whitespace normalization and after collapsing
/// the generated <c>*_ProceduralLinqHelper</c> local-function names (which
/// embed the containing method name and therefore differ only by that
/// deterministic suffix policy).
/// </summary>
public sealed class LinqChannelEquivalenceTests
{
    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.Generic.HashSet<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Queryable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Expressions.Expression).Assembly.Location),
        MetadataReference.CreateFromFile(Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
        MetadataReference.CreateFromFile(Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Collections.dll")),
    };

    private static readonly Regex GeneratedHelperName =
        new(@"[A-Za-z_][A-Za-z0-9_]*_ProceduralLinqHelper", RegexOptions.Compiled);

    [Fact]
    public void ChannelWithObserveGate_MatchesEmbeddedE16_OnEnumerableMethodChain()
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

        var (embedded, channel, statistics) = RunBothPaths(source);

        Assert.Equal(Normalize(embedded), Normalize(channel.RewrittenRoot));
        Assert.True(channel.RewriteChanged, "the stock rewriter must lower the Enumerable chains");
        Assert.True(channel.SyntacticallyIdempotent);
        Assert.True(statistics.EnumerableChainsExpanded >= 2);
        Assert.Equal(0, statistics.QueryableChainsPreserved);
        Assert.Equal(0, statistics.AmbiguousChains);
    }

    [Fact]
    public void ChannelWithObserveGate_MatchesEmbeddedE16_OnQueryExpressionAndForeachRewrap()
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
                    // also carries the observe hook and must stay equivalent.
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

        var (embedded, channel, statistics) = RunBothPaths(source);

        Assert.Equal(Normalize(embedded), Normalize(channel.RewrittenRoot));
        Assert.True(channel.RewriteChanged, "desugared query + foreach sites must be rewritten");
        Assert.True(channel.SyntacticallyIdempotent);
        Assert.True(statistics.EnumerableChainsExpanded >= 1);
        Assert.Equal(0, statistics.QueryableChainsPreserved);
        Assert.Equal(0, statistics.AmbiguousChains);
    }

    /// <summary>
    /// Desugars the source once (stage 1, shared by both paths), rebuilds the
    /// semantic model over the post-desugar tree, then runs E16 (no gate) and
    /// the channel (observe-only gate) against the identical input.
    /// </summary>
    private static (SyntaxNode EmbeddedRoot, LinqChannelRewriteResult ChannelResult,
        LinqChannelStatistics Statistics) RunBothPaths(string source)
    {
        var originalTree = CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Latest));
        var originalCompilation = CSharpCompilation.Create(
            "c26-equiv-" + Guid.NewGuid().ToString("N"),
            new[] { originalTree },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        Assert.Empty(originalCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));

        // Stage 1: query desugaring (single shared implementation).
        var (desugaredRoot, _) = LinqChannelRewriter.DesugarQueries(originalTree.GetRoot());
        var desugaredText = desugaredRoot.ToFullString();

        var desugaredTree = CSharpSyntaxTree.ParseText(
            desugaredText, new CSharpParseOptions(LanguageVersion.Latest), path: originalTree.FilePath);
        var compilation = CSharpCompilation.Create(
            "c26-equiv-" + Guid.NewGuid().ToString("N"),
            new[] { desugaredTree },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        var desugaredErrors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(desugaredErrors.Count == 0,
            "desugared snippet must compile: " + string.Join("; ", desugaredErrors));
        var model = compilation.GetSemanticModel(desugaredTree);
        var postDesugarRoot = desugaredTree.GetRoot();

        // E16 embedded call shape — no gate (the gate constructor argument is
        // optional and defaults to null for every existing call site).
        var embeddedRewriter = new LinqRewriter(model);
        var embeddedRoot = embeddedRewriter.Visit(postDesugarRoot) ?? postDesugarRoot;

        // Channel call shape — plan + ObserveOnlyGate attached.
        var channelResult = new LinqChannelRewriter(new LinqChannelOptions())
            .ProcessFile(model, postDesugarRoot, filePath: null);

        return (embeddedRoot, channelResult, channelResult.Statistics);
    }

    private static string Normalize(SyntaxNode root)
    {
        var normalized = root.NormalizeWhitespace().ToFullString();
        return GeneratedHelperName.Replace(normalized, "_ProceduralLinqHelper");
    }
}
