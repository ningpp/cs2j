using System.Text.RegularExpressions;
using CSharpToJava.Core.LinqChannel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace CSharpToJava.Tests.LinqChannel;

/// <summary>
/// M2 gate seam tests (design §4.2/§4.3): <c>EnableQueryableGate</c> switches
/// the channel between intercept mode (default) and M1 observe-only mode.
/// <list type="bullet">
/// <item>intercept ON: Queryable/ExpressionArgument/Mixed chains freeze whole,
/// Ambiguous chains freeze + LC0010 (strict escalates), Enumerable/NonLinq
/// chains behave exactly as E16;</item>
/// <item>intercept OFF: observe-only — the stock name-guess fallback stays
/// reachable and ambiguous chains land in the E16 skip outcome (M0 finding);</item>
/// <item>freezing includes the argument lambdas so a quoted Expression body's
/// nested Enumerable chain cannot be proceduralized by descent.</item>
/// </list>
/// </summary>
public sealed class LinqChannelGateModeTests
{
    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Queryable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Expressions.Expression).Assembly.Location),
        MetadataReference.CreateFromFile(Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
        MetadataReference.CreateFromFile(Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Collections.dll")),
    };

    private static readonly Regex GeneratedHelperName =
        new(@"[A-Za-z_][A-Za-z0-9_]*_ProceduralLinq\w*", RegexOptions.Compiled);

    [Fact]
    public void Intercept_QueryableChain_FreezesChain_AndRebindsToQueryable()
    {
        const string source = """
            using System.Linq;

            public static class GateHost
            {
                public static IQueryable<int> Query(IQueryable<int> q)
                    => q.Where(x => x > 0).Select(x => x + 1).OrderBy(x => x);
            }
            """;

        var result = RunChannel(source, intercept: true);

        Assert.False(result.Result.RewriteChanged, "a pure Queryable chain must be preserved untouched");
        Assert.Equal(1, result.Statistics.QueryableChainsPreserved);
        Assert.Equal(0, result.Statistics.EnumerableChainsExpanded);
        Assert.Equal(0, result.Statistics.MixedChainsPreserved);
        Assert.Equal(0, result.Statistics.AmbiguousChains);
        Assert.True(result.Statistics.ExpressionArgumentSites >= 3,
            "every Queryable operator lambda is an Expression<> argument");
        Assert.Empty(result.Result.Diagnostics);
        Assert.False(GeneratedHelperName.IsMatch(result.Output),
            "preserved Queryable chains must not gain procedural helpers");

        var rebind = Recompile(result.Output);
        var model = rebind.Model;
        foreach (var invocation in rebind.Root.DescendantNodes()
                     .OfType<InvocationExpressionSyntax>()
                     .Where(i => (i.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.Text
                         is "Where" or "Select" or "OrderBy"))
        {
            var symbol = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            var host = symbol?.ReducedFrom?.ContainingType.ToDisplayString()
                ?? symbol?.ContainingType.ToDisplayString();
            Assert.Equal(LinqHosts.QueryableHost, host);
        }
    }

    [Fact]
    public void Intercept_ExpressionVariableArgument_IsCountedAndPreserved()
    {
        const string source = """
            using System;
            using System.Linq;
            using System.Linq.Expressions;

            public static class GateHost
            {
                public static IQueryable<int> Query(IQueryable<int> q)
                {
                    Expression<Func<int, bool>> pred = x => x > 0;
                    return q.Where(pred);
                }
            }
            """;

        var result = RunChannel(source, intercept: true);

        Assert.False(result.Result.RewriteChanged);
        Assert.Equal(1, result.Statistics.QueryableChainsPreserved);
        Assert.True(result.Statistics.ExpressionArgumentSites >= 1);
        Assert.Contains("q.Where(pred)", result.Output);
    }

    [Fact]
    public void Intercept_MixedHostChain_FreezesWholesale_AndEmitsInfo()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;

            public static class GateHost
            {
                public static IEnumerable<int> Mixed(IQueryable<int> q)
                    => q.Where(x => x > 0).AsEnumerable().Select(x => x);
            }
            """;

        var result = RunChannel(source, intercept: true);

        Assert.False(result.Result.RewriteChanged);
        Assert.Equal(1, result.Statistics.MixedChainsPreserved);
        Assert.Equal(0, result.Statistics.QueryableChainsPreserved);
        Assert.Contains(
            result.Result.Diagnostics,
            d => d.Code == LinqChannelDiagnosticCodes.MixedChainPreserved
                && d.Severity == LinqChannelDiagnosticSeverity.Info);
        Assert.Contains("q.Where(x => x > 0).AsEnumerable().Select(x => x)", result.Output);
    }

    [Fact]
    public void Intercept_AmbiguousChain_Freezes_AndWarnsLc0010_StrictEscalates()
    {
        const string source = """
            using System;

            public static class GateHost
            {
                public static void Broken(global::Missing.Metadata.IDbSet<int> db)
                {
                    var q = db.Where(x => x > 0).Select(x => x + 1);
                    Console.WriteLine(q);
                }
            }
            """;

        var lenient = RunChannel(source, intercept: true, strict: false);
        Assert.False(lenient.Result.RewriteChanged,
            "unbound LINQ-style calls are preserved, never guessed as Enumerable");
        Assert.Equal(1, lenient.Statistics.AmbiguousChains);
        Assert.Empty(lenient.Statistics.Rewrite.SkippedChains);
        Assert.Contains("db.Where(x => x > 0).Select(x => x + 1)", lenient.Output);
        // LC0010 is emitted per ambiguous node (Where and Select both unbound).
        var lc0010 = lenient.Result.Diagnostics
            .Where(d => d.Code == LinqChannelDiagnosticCodes.AmbiguousPreserved)
            .ToList();
        Assert.Equal(2, lc0010.Count);
        Assert.All(lc0010, d => Assert.Equal(LinqChannelDiagnosticSeverity.Warning, d.Severity));

        var strict = RunChannel(source, intercept: true, strict: true);
        Assert.All(
            strict.Result.Diagnostics.Where(d => d.Code == LinqChannelDiagnosticCodes.AmbiguousPreserved),
            d => Assert.Equal(LinqChannelDiagnosticSeverity.Error, d.Severity));
    }

    [Fact]
    public void ObserveOnly_AmbiguousChain_ReachesStockFallback_SkipOutcome()
    {
        const string source = """
            using System;

            public static class GateHost
            {
                public static void Broken(global::Missing.Metadata.IDbSet<int> db)
                {
                    var q = db.Where(x => x > 0).Select(x => x + 1);
                    Console.WriteLine(q);
                }
            }
            """;

        // M0 pinned outcome (b): with the gate observing only, the stock
        // name-guess fallback engages and rule expansion lands in the skip
        // catch instead of preserving the node up front. The plan-level LC0010
        // warning is emitted in both modes — only the rewriter decision moves.
        var observed = RunChannel(source, intercept: false);

        Assert.True(observed.Statistics.Rewrite.SkippedChains.Count >= 1,
            "observe-only mode must leave the E16 fallback reachable (M0 outcome b: skip)");
        Assert.Equal(1, observed.Statistics.AmbiguousChains);
    }

    [Fact]
    public void Intercept_ForeachOverQueryable_FreezesCollection_VisitsBody()
    {
        const string source = """
            using System.Linq;

            public static class GateHost
            {
                public static int Sum(IQueryable<int> q)
                {
                    var total = 0;
                    foreach (var x in q.Where(x => x > 0))
                    {
                        total += x;
                    }

                    return total;
                }
            }
            """;

        var result = RunChannel(source, intercept: true);

        Assert.False(result.Result.RewriteChanged);
        Assert.Equal(1, result.Statistics.QueryableChainsPreserved);
        Assert.Contains("foreach (var x in q.Where(x => x > 0))", result.Output);

        var rebind = Recompile(result.Output);
        var where = rebind.Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(i => (i.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.Text == "Where");
        var symbol = rebind.Model.GetSymbolInfo(where).Symbol as IMethodSymbol;
        var host = symbol?.ReducedFrom?.ContainingType.ToDisplayString()
            ?? symbol?.ContainingType.ToDisplayString();
        Assert.Equal(LinqHosts.QueryableHost, host);
    }

    [Fact]
    public void Intercept_QuotedLambda_InnerEnumerableChain_IsFrozenToo()
    {
        const string source = """
            using System.Linq;

            public static class GateHost
            {
                private static readonly int[] Numbers = [1, 2, 3];

                public static IQueryable<int> Query(IQueryable<int> q)
                    => q.Where(x => Numbers.Where(n => n > x).Any());
            }
            """;

        var result = RunChannel(source, intercept: true);

        Assert.False(result.Result.RewriteChanged,
            "freezing the outer Queryable node must freeze its quoted lambda subtree");
        Assert.Equal(1, result.Statistics.QueryableChainsPreserved);
        // Note: the static plan still classifies the inner Numbers chain as
        // Expandable; the proof of freezing is that no rewrite happened and no
        // helper/skip was produced for it.
        Assert.Equal(0, result.Statistics.Rewrite.SkippedChains.Count);
        Assert.Contains("Numbers.Where(n => n > x).Any()", result.Output);
        Assert.False(GeneratedHelperName.IsMatch(result.Output),
            "no procedural helpers may be extracted for a frozen Queryable subtree");
    }

    [Fact]
    public void Intercept_EnumerableChain_ExpandsExactlyAsE16()
    {
        const string source = """
            using System.Linq;

            public static class GateHost
            {
                public static int[] EvensDoubled(int[] src)
                    => src.Where(x => x % 2 == 0).Select(x => x * 2).ToArray();
            }
            """;

        var result = RunChannel(source, intercept: true);

        Assert.True(result.Result.RewriteChanged);
        Assert.Equal(1, result.Statistics.EnumerableChainsExpanded);
        Assert.Equal(0, result.Statistics.QueryableChainsPreserved);
        Assert.Equal(0, result.Statistics.AmbiguousChains);
        Assert.Equal(0, result.Statistics.Rewrite.SkippedChains.Count);
    }

    [Fact]
    public void Intercept_NonLinqCustomOperatorName_IsCountedButNotFrozenOrRewritten()
    {
        const string source = """
            using System;
            using System.Collections.Generic;

            namespace Custom;

            public static class MyQueryableExtensions
            {
                public static IEnumerable<T> Where<T>(this IEnumerable<T> source, Func<T, bool> predicate)
                    => System.Linq.Enumerable.Where(source, predicate);
            }

            public static class GateHost
            {
                public static IEnumerable<int> Custom(int[] src) => src.Where(x => x > 0);
            }
            """;

        var result = RunChannel(source, intercept: true);

        Assert.False(result.Result.RewriteChanged,
            "a provider extension named Where is NonLinq: counted, never expanded");
        Assert.True(result.Statistics.ProviderCustomCalls >= 1);
        Assert.Equal(0, result.Statistics.QueryableChainsPreserved);
        Assert.Equal(0, result.Statistics.AmbiguousChains);
    }

    private sealed record ChannelRun(
        LinqChannelRewriteResult Result,
        LinqChannelStatistics Statistics,
        SyntaxNode OutputRoot,
        string Output);

    private static ChannelRun RunChannel(string source, bool intercept, bool strict = false)
    {
        var originalTree = CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Latest));
        var (desugaredRoot, _) = LinqChannelRewriter.DesugarQueries(originalTree.GetRoot());

        var desugaredTree = CSharpSyntaxTree.ParseText(
            desugaredRoot.ToFullString(),
            new CSharpParseOptions(LanguageVersion.Latest),
            path: originalTree.FilePath);
        var compilation = CSharpCompilation.Create(
            "c26-gate-" + Guid.NewGuid().ToString("N"),
            new[] { desugaredTree },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        // Ambiguous snippets intentionally fail to compile (missing metadata);
        // the channel routes from symbol info regardless.
        var model = compilation.GetSemanticModel(desugaredTree);
        var postDesugarRoot = desugaredTree.GetRoot();

        var channel = new LinqChannelRewriter(new LinqChannelOptions
        {
            EnableQueryableGate = intercept,
            Strict = strict,
        });
        var result = channel.ProcessFile(model, postDesugarRoot, filePath: null);
        return new ChannelRun(result, result.Statistics, result.RewrittenRoot,
            result.RewrittenRoot.ToFullString());
    }

    private static (SemanticModel Model, SyntaxNode Root) Recompile(string output)
    {
        var tree = CSharpSyntaxTree.ParseText(
            output, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            "c26-gate-rebind-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "preserved output must stay compilable: " + string.Join("; ", errors));
        return (compilation.GetSemanticModel(tree), tree.GetRoot());
    }
}
