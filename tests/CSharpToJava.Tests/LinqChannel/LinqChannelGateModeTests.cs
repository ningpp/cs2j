using System.Text.RegularExpressions;
using CSharpToJava.Core.LinqChannel;
using CSharpToJava.Core.LinqRewrite;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace CSharpToJava.Tests.LinqChannel;

/// <summary>
/// M3 gate seam tests (design §4.2/§4.3/§4.4/§4.6): inside the channel the
/// gate is permanently intercepting (the M1/M2 observe-only
/// <c>EnableQueryableGate</c> off-switch was removed).
/// <list type="bullet">
/// <item>Queryable/ExpressionArgument/Mixed chains freeze whole; Ambiguous
/// chains freeze + LC0010 (strict escalates); Enumerable/NonLinq chains
/// outside quoted regions behave exactly as E16;</item>
/// <item>freezing includes argument lambdas AND every invocation inside a
/// quoted Expression body (an inner Enumerable chain is counted as a
/// quoted-preserved chain, never proceduralized);</item>
/// <item>the embedded E16 construction is the NULL-gate
/// <see cref="LinqRewriter"/>: its name-guess fallback stays reachable and
/// ambiguous chains still land in the E16 skip outcome (M0 finding, hard
/// boundary — E16 behavior is unchanged).</item>
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

        var result = RunChannel(source);

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

        var result = RunChannel(source);

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

        var result = RunChannel(source);

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

        var lenient = RunChannel(source, strict: false);
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

        var strict = RunChannel(source, strict: true);
        Assert.All(
            strict.Result.Diagnostics.Where(d => d.Code == LinqChannelDiagnosticCodes.AmbiguousPreserved),
            d => Assert.Equal(LinqChannelDiagnosticSeverity.Error, d.Severity));
    }

    [Fact]
    public void NullGate_AmbiguousChain_StillReachesStockFallback_SkipOutcome()
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

        // M3 removed the channel's gate off-switch; the embedded E16
        // construction is instead the rewriter built with NO gate (null).
        // M0 pinned outcome (b) must remain reachable on exactly that path:
        // the stock name-guess fallback engages and rule expansion lands in
        // the three-type skip catch instead of preserving up front. This is
        // the E16 zero-change hard boundary made explicit.
        var (model, postDesugarRoot) = BuildModel(source);
        var rewriter = new LinqRewriter(model);
        rewriter.Visit(postDesugarRoot);

        Assert.True(rewriter.Statistics.SkippedChains.Count >= 1,
            "null-gate (embedded E16) must keep the name-guess fallback reachable (M0 outcome b: skip)");
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

        var result = RunChannel(source);

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

        var result = RunChannel(source);

        Assert.False(result.Result.RewriteChanged,
            "freezing the outer Queryable node must freeze its quoted lambda subtree");
        Assert.Equal(1, result.Statistics.QueryableChainsPreserved);
        // M3: the static plan now flags the inner Numbers chain as a quoted
        // region — it is counted as a quoted-preserved chain (M2 leftover: in
        // M2 the plan still called it Expandable and only subtree freezing
        // saved it).
        Assert.Equal(1, result.Statistics.ExpressionQuotedChainsPreserved);
        Assert.Equal(0, result.Statistics.EnumerableChainsExpanded);
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

        var result = RunChannel(source);

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

        var result = RunChannel(source);

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

    private static ChannelRun RunChannel(string source, bool strict = false)
    {
        var (model, postDesugarRoot) = BuildModel(source);

        // M3: no off-switch — the channel gate is always intercepting.
        var channel = new LinqChannelRewriter(new LinqChannelOptions
        {
            Strict = strict,
        });
        var result = channel.ProcessFile(model, postDesugarRoot, filePath: null);
        return new ChannelRun(result, result.Statistics, result.RewrittenRoot,
            result.RewrittenRoot.ToFullString());
    }

    private static (SemanticModel Model, SyntaxNode PostDesugarRoot) BuildModel(string source)
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
        return (compilation.GetSemanticModel(desugaredTree), desugaredTree.GetRoot());
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
