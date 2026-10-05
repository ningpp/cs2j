using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Loader;
using CSharpToJava.Core.LinqChannel;
using CSharpToJava.Core.LinqRewrite;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace CSharpToJava.Tests.LinqChannel;

/// <summary>
/// M3 three-gate matrix (design §7.4/§7.5, milestone item B):
/// <list type="number">
/// <item><b>Queryable gate</b> — every Queryable operator row re-compiles with
/// every invocation bound to <c>System.Linq.Queryable</c>, every lambda
/// argument still converted to <c>Expression&lt;&gt;</c>, and ZERO
/// proceduralization (no helpers, no skips, EnumerableChainsExpanded==0);</item>
/// <item><b>query syntax, two sources</b> — from/let/into/join-into/multi-from
/// desugar to Queryable chains and stay frozen;</item>
/// <item><b>Expression gate</b> — hand-built <c>Expression.Lambda/Call/
/// Property/Constant/Equal</c> trees are untouched (System.* identity proven,
/// no LC0012), while user/3rd-party calls inside quoted lambdas are frozen and
/// reported LC0012 (strict: Error);</item>
/// <item>mixed-host D7 chains freeze wholesale + LC0020; custom
/// <c>Include</c> extensions stay NonLinq; LC0010 negative rows preserve;
/// LC0011 tripwires are verified by direct gate construction;</item>
/// <item><b>runtime double gate</b> — a hand-written IQueryable/IQueryProvider
/// recording the provider-visible expression node sequence produces the
/// IDENTICAL sequence on unprocessed and channel-processed assemblies
/// (loaded in separate collectible contexts and invoked via reflection).</item>
/// </list>
/// No EF Core package is referenced: the query provider is a 60-line test
/// type (milestone item C; M0 prototype in RecordingQueryProviderProbeTests).
/// </summary>
public sealed class LinqChannelQueryableGateMatrixTests
{
    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Queryable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Expression).Assembly.Location),
        MetadataReference.CreateFromFile(Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
        MetadataReference.CreateFromFile(Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Collections.dll")),
    };

    // ------------------------------------------------------------------
    // Gate 1: Queryable operator matrix
    // ------------------------------------------------------------------

    public static IEnumerable<object[]> QueryableRows()
    {
        yield return new object[] { "Where", "q.Where(x => x > 0)" };
        yield return new object[] { "Select", "q.Select(x => x + 1)" };
        yield return new object[] { "SelectMany", "q.SelectMany(x => new[] { x, x })" };
        yield return new object[] { "OrderBy", "q.OrderBy(x => x)" };
        yield return new object[] { "OrderByDescending", "q.OrderByDescending(x => x)" };
        yield return new object[] { "ThenBy", "q.OrderBy(x => x).ThenBy(x => -x)" };
        yield return new object[] { "ThenByDescending", "q.OrderBy(x => x).ThenByDescending(x => -x)" };
        yield return new object[] { "Skip", "q.Skip(1)" };
        yield return new object[] { "Take", "q.Take(1)" };
        yield return new object[] { "Distinct", "q.Distinct()" };
        yield return new object[] { "AnyPredicate", "q.Any(x => x > 0)" };
        yield return new object[] { "Any", "q.Any()" };
        yield return new object[] { "CountPredicate", "q.Count(x => x > 0)" };
        yield return new object[] { "Count", "q.Count()" };
        yield return new object[] { "FirstPredicate", "q.First(x => x > 0)" };
        yield return new object[] { "First", "q.First()" };
        yield return new object[] { "FirstOrDefaultPredicate", "q.FirstOrDefault(x => x > 0)" };
        yield return new object[] { "FirstOrDefault", "q.FirstOrDefault()" };
        yield return new object[] { "Single", "q.Single()" };
        yield return new object[] { "SingleOrDefault", "q.SingleOrDefault()" };
        yield return new object[] { "Last", "q.Last()" };
        yield return new object[] { "LastOrDefault", "q.LastOrDefault()" };
        yield return new object[] { "ElementAt", "q.ElementAt(0)" };
        yield return new object[] { "ElementAtOrDefault", "q.ElementAtOrDefault(0)" };
        yield return new object[] { "DefaultIfEmpty", "q.DefaultIfEmpty()" };
        yield return new object[] { "Concat", "q.Concat(q2)" };
        yield return new object[] { "Union", "q.Union(q2)" };
        yield return new object[] { "SequenceEqual", "q.SequenceEqual(q2)" };
        yield return new object[] { "Reverse", "q.Reverse()" };
        yield return new object[] { "Contains", "q.Contains(1)" };
        yield return new object[] { "GroupBy", "q.GroupBy(x => x)" };
        yield return new object[]
        {
            "Join",
            "q.Join(q2, x => x, y => y, (x, y) => x + y)",
        };
        yield return new object[]
        {
            "GroupJoin",
            "q.GroupJoin(q2, x => x, y => y, (x, g) => x)",
        };
        yield return new object[] { "AsQueryable", "q.AsQueryable()" };
    }

    [Theory]
    [MemberData(nameof(QueryableRows))]
    public void QueryableOperator_Freezes_RecompilesAsQueryable_LambdasStayQuoted_ZeroProceduralization(
        string label, string chain)
    {
        var source = $$"""
            using System.Linq;

            public static class MatrixHost
            {
                public static object Build(System.Linq.IQueryable<int> q, System.Linq.IQueryable<int> q2)
                    => {{chain}};
            }
            """;

        var run = RunChannel(source);

        Assert.True(
            !run.Result.RewriteChanged,
            $"{label}: a pure Queryable operator chain must be preserved node for node");
        Assert.Equal(0, run.Result.Statistics.EnumerableChainsExpanded);
        Assert.Equal(0, run.Result.Statistics.MixedChainsPreserved);
        Assert.Equal(0, run.Result.Statistics.AmbiguousChains);
        Assert.Equal(0, run.Result.Statistics.ExpressionQuotedChainsPreserved);
        Assert.Equal(0, run.Result.Statistics.ExpressionTreeUnprovableSites);
        Assert.Equal(0, run.Result.Statistics.Rewrite.SkippedChains.Count);
        Assert.Empty(run.Result.Diagnostics);
        Assert.DoesNotContain("_ProceduralLinq", run.Output);

        var rebind = Recompile(run.Output);
        var linqInvocations = rebind.Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(LinqRouteClassifier.HasLinqOperatorName)
            .ToList();
        Assert.NotEmpty(linqInvocations);
        foreach (var invocation in linqInvocations)
        {
            var symbol = rebind.Model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            var host = LinqHosts.NormalizeToStaticHost(
                (symbol?.ReducedFrom?.ContainingType ?? symbol?.ContainingType)?.OriginalDefinition
                    .ToDisplayString() ?? "<null>");
            Assert.Equal(LinqHosts.QueryableHost, host);
        }

        // Every lambda argument to a Queryable operator is still a QUOTED
        // Expression<> after the freeze (risk 8 / D2). Bare operator rows
        // (Skip/Take/Count()/...) carry no lambda at all.
        var chainHadLambda = chain.Contains("=>");
        var lambdas = rebind.Root.DescendantNodes().OfType<LambdaExpressionSyntax>().ToList();
        Assert.Equal(chainHadLambda, lambdas.Count > 0);
        Assert.All(lambdas, lambda =>
        {
            var converted = rebind.Model.GetTypeInfo(lambda).ConvertedType?.ToDisplayString();
            Assert.StartsWith("System.Linq.Expressions.Expression<", converted);
        });
    }

    // ------------------------------------------------------------------
    // Gate 2: query syntax, two sources + let/into/join-into/multi-from
    // ------------------------------------------------------------------

    public static IEnumerable<object[]> QuerySyntaxRows()
    {
        yield return new object[]
        {
            "Basic",
            """
            from x in q
            where x > 0
            orderby x descending
            select x + 1
            """,
        };
        yield return new object[]
        {
            "TwoSources",
            """
            from a in q1
            from b in q2
            where a == b
            select a + b
            """,
        };
        yield return new object[]
        {
            "LetInto",
            """
            from x in q
            let y = x + 1
            where y > 0
            select y into z
            where z > 1
            select z
            """,
        };
        yield return new object[]
        {
            "JoinInto",
            // The stage-1 desugarer lowers join-into only when it is the last
            // clause; the result selector projects the grouping itself, so no
            // Enumerable call nests inside the quoted result-selector lambda.
            """
            from a in q1
            join b in q2 on a equals b into g
            select g
            """,
        };
    }

    [Theory]
    [MemberData(nameof(QuerySyntaxRows))]
    public void QuerySyntax_OverQueryable_DesugarsThenFreezes_RecompilesAsQueryable(string label, string query)
    {
        // Indentation is irrelevant to the parser; query rows are embedded as
        // raw newline-delimited text between `return` and `;`.
        var source =
            "using System.Linq;\n\n"
            + "public static class QuerySyntaxHost\n{\n"
            + "    public static object Build(IQueryable<int> q, IQueryable<int> q1, IQueryable<int> q2)\n"
            + "    {\n        return\n" + query + ";\n    }\n}\n";

        var run = RunChannel(source);

        Assert.False(run.Result.RewriteChanged,
            $"{label}: after stage-1 desugaring the Queryable chain is frozen, not rewritten");
        Assert.Equal(0, run.Result.Statistics.EnumerableChainsExpanded);
        Assert.Equal(0, run.Result.Statistics.ExpressionQuotedChainsPreserved);
        Assert.Equal(0, run.Result.Statistics.Rewrite.SkippedChains.Count);
        Assert.DoesNotContain("_ProceduralLinq", run.Output);
        // Stage 1 owns query syntax lowering; stage 2 must see none.
        Assert.DoesNotContain("from ", run.Output);

        var rebind = Recompile(run.Output);
        var linqInvocations = rebind.Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(LinqRouteClassifier.HasLinqOperatorName)
            .ToList();
        Assert.NotEmpty(linqInvocations);
        Assert.All(linqInvocations, invocation =>
        {
            var symbol = rebind.Model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            var host = LinqHosts.NormalizeToStaticHost(
                (symbol?.ReducedFrom?.ContainingType ?? symbol?.ContainingType)?.OriginalDefinition
                    .ToDisplayString() ?? "<null>");
            Assert.Equal(LinqHosts.QueryableHost, host);
        });
    }

    // ------------------------------------------------------------------
    // Gate 3: Expression trees — manual builder API untouched; quoted
    // regions protected even without a Queryable host; LC0012 policy.
    // ------------------------------------------------------------------

    [Fact]
    public void ManualExpressionTreeBuilderApi_IsUntouched_AndReportsNothing()
    {
        const string source = """
            using System;
            using System.Linq.Expressions;

            public sealed class Thing
            {
                public int Id { get; set; }
                public int Mark() => Id;
            }

            public static class TreeHost
            {
                public static Expression<Func<Thing, bool>> Build()
                {
                    var param = Expression.Parameter(typeof(Thing), "t");
                    var body = Expression.Equal(
                        Expression.Property(param, "Id"),
                        Expression.Call(param, typeof(Thing).GetMethod("Mark")!));
                    var lambda = Expression.Lambda<Func<Thing, bool>>(body, param);
                    var visited = Expression.Call(
                        lambda,
                        typeof(Expression).GetMethod(nameof(Expression.ToString), Type.EmptyTypes)!);
                    Console.WriteLine(visited.ToString());
                    return lambda;
                }
            }
            """;

        var run = RunChannel(source);

        Assert.False(run.Result.RewriteChanged,
            "manual Expression.Lambda/Call/Property/Constant construction is ordinary C# the channel never touches");
        Assert.Equal(0, run.Result.Statistics.EnumerableChainsExpanded);
        Assert.Equal(0, run.Result.Statistics.ExpressionTreeUnprovableSites);
        Assert.Empty(run.Result.Diagnostics);
        Assert.Contains("Expression.Lambda<Func<Thing, bool>>", run.Output);
        Assert.Contains("Expression.Property(param, \"Id\")", run.Output);
        Assert.Contains("Expression.Call(param, typeof(Thing).GetMethod(\"Mark\")!)", run.Output);
        Recompile(run.Output);
    }

    [Fact]
    public void QuotedRegion_WithoutQueryableHost_ProtectsInnerEnumerableChain_M2LeftoverFix()
    {
        // The stronger M2 leftover shape: a custom (non-Queryable) tree
        // consumer receives a quoted lambda whose body contains an Enumerable
        // chain. In M2 the stock rewriter proceduralized it (only an outer
        // Queryable freeze saved subtrees); M3 freezes ANY quoted region.
        const string source = """
            using System;
            using System.Linq;
            using System.Linq.Expressions;

            public static class TreeConsumer
            {
                public static void Consume(Expression<Func<int, bool>> predicate) { }
            }

            public static class QuotedHost
            {
                private static readonly int[] Numbers = [1, 2, 3];

                public static void Use()
                    => TreeConsumer.Consume(x => Numbers.Where(n => n > x).Any());
            }
            """;

        var run = RunChannel(source);

        Assert.False(run.Result.RewriteChanged,
            "the Enumerable chain inside the quoted lambda must not be proceduralized");
        Assert.Equal(1, run.Result.Statistics.ExpressionQuotedChainsPreserved);
        Assert.Equal(0, run.Result.Statistics.EnumerableChainsExpanded);
        Assert.Equal(0, run.Result.Statistics.Rewrite.SkippedChains.Count);
        Assert.Contains("Numbers.Where(n => n > x).Any()", run.Output);
        Assert.DoesNotContain("_ProceduralLinq", run.Output);
        // Enumerable calls inside the quote bind the BCL: identity provable.
        Assert.Equal(0, run.Result.Statistics.ExpressionTreeUnprovableSites);
        Recompile(run.Output);
    }

    [Fact]
    public void QuotedRegion_BclInstanceCalls_AreSilent()
    {
        const string source = """
            using System.Linq;

            public static class BclQuoteHost
            {
                public static object Build(IQueryable<int> q)
                    => q.Where(x => x.ToString().Length > 1);
            }
            """;

        var run = RunChannel(source);

        Assert.False(run.Result.RewriteChanged);
        Assert.Equal(0, run.Result.Statistics.ExpressionTreeUnprovableSites);
        Assert.Empty(run.Result.Diagnostics.Where(d => d.Code == LinqChannelDiagnosticCodes.ExpressionTreeUnprovable));
        Recompile(run.Output);
    }

    [Fact]
    public void QuotedRegion_UserMethodCall_ReportsLc0012_WarningThenStrictError_AndStaysFrozen()
    {
        const string source = """
            using System;
            using System.Linq;
            using System.Linq.Expressions;

            namespace M3Probe;

            public static class TreeConsumer
            {
                public static void Consume(Expression<Func<int, bool>> predicate) { }
            }

            public static class UserHelper
            {
                public static int Frob(int x) => x * 2;
            }

            public static class UserQuoteHost
            {
                public static void Use()
                    => TreeConsumer.Consume(x => UserHelper.Frob(x) > 1);
            }
            """;

        var lenient = RunChannel(source, strict: false);
        Assert.False(lenient.Result.RewriteChanged);
        var lc0012 = lenient.Result.Diagnostics
            .Where(d => d.Code == LinqChannelDiagnosticCodes.ExpressionTreeUnprovable)
            .ToList();
        Assert.Single(lc0012);
        Assert.Equal(LinqChannelDiagnosticSeverity.Warning, lc0012[0].Severity);
        Assert.Equal(1, lenient.Statistics.ExpressionTreeUnprovableSites);
        Assert.Contains("UserHelper.Frob(x)", lenient.Output);

        var strict = RunChannel(source, strict: true);
        Assert.All(
            strict.Result.Diagnostics.Where(d => d.Code == LinqChannelDiagnosticCodes.ExpressionTreeUnprovable),
            d => Assert.Equal(LinqChannelDiagnosticSeverity.Error, d.Severity));
    }

    // ------------------------------------------------------------------
    // Mixed D7 + custom provider Include + LC0010 negative
    // ------------------------------------------------------------------

    [Fact]
    public void MixedHostChain_FreezesWholesale_EmitsLc0020_Recompiles()
    {
        const string source = """
            using System.Linq;

            public static class MixedHost
            {
                public static int[] Build(IQueryable<int> q)
                    => q.Where(x => x > 0).AsEnumerable().Select(x => x + 1).ToArray();
            }
            """;

        var run = RunChannel(source);

        Assert.False(run.Result.RewriteChanged);
        Assert.Equal(1, run.Result.Statistics.MixedChainsPreserved);
        Assert.Equal(0, run.Result.Statistics.EnumerableChainsExpanded);
        Assert.Contains(
            run.Result.Diagnostics,
            d => d.Code == LinqChannelDiagnosticCodes.MixedChainPreserved
                && d.Severity == LinqChannelDiagnosticSeverity.Info);
        Assert.DoesNotContain("_ProceduralLinq", run.Output);
        Recompile(run.Output);
    }

    [Fact]
    public void CustomProviderInclude_StaysNonLinq_QueryableNeighborStillBindsQueryable()
    {
        const string source = """
            using System.Linq;

            namespace M3Probe;

            public static class MyProviderExtensions
            {
                public static IQueryable<T> Include<T>(this IQueryable<T> source, string path) => source;
            }

            public static class IncludeHost
            {
                public static IQueryable<int> Build(IQueryable<int> q)
                    => q.Include("Children").Where(x => x > 0);
            }
            """;

        var run = RunChannel(source);

        Assert.False(run.Result.RewriteChanged,
            "the NonLinq Include + Queryable Where chain is preserved wholesale");
        Assert.DoesNotContain("_ProceduralLinq", run.Output);

        var rebind = Recompile(run.Output);
        var include = rebind.Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(i => (i.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.Text == "Include");
        var includeSymbol = rebind.Model.GetSymbolInfo(include).Symbol as IMethodSymbol;
        Assert.Equal("M3Probe.MyProviderExtensions", includeSymbol?.ContainingType.ToDisplayString());

        var where = rebind.Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(i => (i.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.Text == "Where");
        var whereSymbol = rebind.Model.GetSymbolInfo(where).Symbol as IMethodSymbol;
        var whereHost = LinqHosts.NormalizeToStaticHost(
            (whereSymbol?.ReducedFrom?.ContainingType ?? whereSymbol?.ContainingType)?.OriginalDefinition
                .ToDisplayString() ?? "<null>");
        Assert.Equal(LinqHosts.QueryableHost, whereHost);
    }

    [Fact]
    public void AmbiguousChain_Preserves_EmitsTwoLc0010_AndNeverFallsBack()
    {
        const string source = """
            using System;

            public static class BrokenHost
            {
                public static void Broken(global::Missing.Metadata.IDbSet<int> db)
                {
                    var q = db.Where(x => x > 0).Select(x => x + 1);
                    Console.WriteLine(q);
                }
            }
            """;

        var run = RunChannel(source);

        Assert.False(run.Result.RewriteChanged);
        Assert.Equal(1, run.Statistics.AmbiguousChains);
        Assert.Equal(0, run.Statistics.EnumerableChainsExpanded);
        Assert.Equal(2,
            run.Result.Diagnostics.Count(d => d.Code == LinqChannelDiagnosticCodes.AmbiguousPreserved));
        Assert.DoesNotContain("_ProceduralLinq", run.Output);
    }

    // ------------------------------------------------------------------
    // LC0011 tripwires — direct gate construction (correct routing makes
    // these unreachable; they pin the wire-up and exception contract).
    // ------------------------------------------------------------------

    [Fact]
    public void Lc0011_EnterExpansionTripwire_ThrowsHardInvariant_NotSwallowedByWideCatch()
    {
        const string source = """
            using System.Linq;

            public static class GateProbe
            {
                public static object Build(IQueryable<int> q)
                    => q.Where(x => x > 0);
            }
            """;

        var (model, root) = BuildModel(source);
        var rewriter = new LinqRewriter(model, null, new ThrowingGate(enterExpansion: true));

        var ex = Assert.Throws<LinqRouteInvariantException>(() => rewriter.Visit(root));
        var diagnostic = ex.ToDiagnostic();
        Assert.Equal(LinqChannelDiagnosticCodes.RouteInvariantViolation, diagnostic.Code);
        Assert.Equal(LinqChannelDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.NotNull(ex.Line);
    }

    [Fact]
    public void Lc0011_SyntaxNameFallbackTripwire_ThrowsBeforeFabricatedIdentity()
    {
        const string source = """
            using System;

            public static class GateProbe
            {
                public static void Broken(global::Missing.Metadata.IDbSet<int> db)
                {
                    var q = db.Where(x => x > 0);
                    Console.WriteLine(q);
                }
            }
            """;

        var (model, root) = BuildModel(source);
        var rewriter = new LinqRewriter(model, null, new ThrowingGate(enterExpansion: false));

        var ex = Assert.Throws<LinqRouteInvariantException>(() => rewriter.Visit(root));
        Assert.Equal(LinqChannelDiagnosticCodes.RouteInvariantViolation, ex.ToDiagnostic().Code);
        Assert.Contains("Where", ex.Message);
    }

    // ------------------------------------------------------------------
    // Runtime double gate — RecordingQueryProvider expression sequence
    // identical between unprocessed and channel-processed assemblies.
    // ------------------------------------------------------------------

    [Fact]
    public void RecordingProvider_RuntimeExpressionSequence_IdenticalBeforeAndAfterChannel()
    {
        var baselineShapes = (List<string>)InvokeProbe(RecordingProviderSource, "RecordingProbe", "Probe", "Build")!;
        var run = RunChannel(RecordingProviderSource);
        Recompile(run.Output);
        var channelShapes = (List<string>)InvokeProbe(run.Output, "RecordingProbeOut", "Probe", "Build")!;

        Assert.Equal(
            new[]
            {
                "Call:Queryable.Where:2",
                "Call:Queryable.Select:2",
                "Call:Queryable.OrderBy:2",
                "Call:Queryable.Take:2",
            },
            baselineShapes);
        Assert.Equal(baselineShapes, channelShapes);

        // Second-level identity: every recorded node's Quote arguments and
        // MethodInfo declaring type are the BCL Queryable in both runs.
        static void AssertShapesAreQueryable(List<string> shapes) =>
            Assert.All(shapes, shape => Assert.Contains("Queryable.", shape));
        AssertShapesAreQueryable(baselineShapes);
        AssertShapesAreQueryable(channelShapes);
    }

    private const string RecordingProviderSource = """
        using System;
        using System.Collections;
        using System.Collections.Generic;
        using System.Linq;
        using System.Linq.Expressions;

        public sealed class RecordingQueryProvider : IQueryProvider
        {
            public List<Expression> RecordedExpressions { get; } = new();

            public IQueryable CreateQuery(Expression expression)
            {
                var elementType = expression.Type.GetInterfaces()
                    .First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    .GetGenericArguments()[0];
                var queryableType = typeof(RecordingQueryable<>).MakeGenericType(elementType);
                return (IQueryable)Activator.CreateInstance(queryableType, this, expression)!;
            }

            public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
            {
                RecordedExpressions.Add(expression);
                return new RecordingQueryable<TElement>(this, expression);
            }

            public object? Execute(Expression expression) => null;
            public TResult Execute<TResult>(Expression expression) => default!;
        }

        public sealed class RecordingQueryable<T> : IOrderedQueryable<T>
        {
            public RecordingQueryable(IQueryProvider provider)
            {
                Provider = provider;
                Expression = Expression.Constant(this);
            }

            public RecordingQueryable(IQueryProvider provider, Expression expression)
            {
                Provider = provider;
                Expression = expression;
            }

            public Type ElementType => typeof(T);
            public Expression Expression { get; }
            public IQueryProvider Provider { get; }
            public IEnumerator<T> GetEnumerator() => throw new NotSupportedException();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        public static class Probe
        {
            public static List<string> Build()
            {
                var provider = new RecordingQueryProvider();
                IQueryable<int> source = new RecordingQueryable<int>(provider);

                // No enumeration: each operator only calls CreateQuery.
                var query = source
                    .Where(x => x > 1)
                    .Select(x => x * 2)
                    .OrderBy(x => x)
                    .Take(10);

                return RecordedShapes(provider);
            }

            private static List<string> RecordedShapes(RecordingQueryProvider provider)
            {
                var shapes = new List<string>();
                foreach (var expression in provider.RecordedExpressions)
                {
                    if (expression is MethodCallExpression call)
                    {
                        shapes.Add(expression.NodeType + ":" + call.Method.DeclaringType!.Name
                            + "." + call.Method.Name + ":" + call.Arguments.Count);
                        foreach (var argument in call.Arguments)
                        {
                            if (argument is UnaryExpression { NodeType: ExpressionType.Quote } quote)
                            {
                                if (quote.Operand is not LambdaExpression)
                                {
                                    throw new InvalidOperationException("Quote must wrap a LambdaExpression.");
                                }
                            }
                        }
                    }
                }

                return shapes;
            }
        }
        """;

    private static object? InvokeProbe(string source, string assemblyName, string typeName, string methodName)
    {
        var tree = CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { tree },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, "probe snippet must compile: " + string.Join("; ", errors.Take(5)));

        using var pe = new MemoryStream();
        var emit = compilation.Emit(pe);
        Assert.True(emit.Success, "probe snippet must emit: "
            + string.Join("; ", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(5)));
        pe.Seek(0, SeekOrigin.Begin);

        var context = new CollectibleContext();
        try
        {
            var assembly = context.LoadPe(pe);
            var type = assembly.GetType(typeName)!;
            return type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
        }
        finally
        {
            context.Unload();
        }
    }

    private sealed class CollectibleContext : AssemblyLoadContext
    {
        public CollectibleContext() : base(isCollectible: true)
        {
        }

        public Assembly LoadPe(Stream pe) => LoadFromStream(pe);

        protected override Assembly? Load(AssemblyName assemblyName) => null;
    }

    // ------------------------------------------------------------------
    // plumbing
    // ------------------------------------------------------------------

    private sealed record ChannelRun(
        LinqChannelRewriteResult Result,
        LinqChannelStatistics Statistics,
        string Output);

    private static ChannelRun RunChannel(string source, bool strict = false)
    {
        var (model, root) = BuildModel(source);
        var result = new LinqChannelRewriter(new LinqChannelOptions { Strict = strict })
            .ProcessFile(model, root, filePath: null);
        return new ChannelRun(result, result.Statistics, result.RewrittenRoot.ToFullString());
    }

    private static (SemanticModel Model, SyntaxNode Root) BuildModel(string source)
    {
        var originalTree = CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Latest));
        var (desugaredRoot, _) = LinqChannelRewriter.DesugarQueries(originalTree.GetRoot());
        var desugaredTree = CSharpSyntaxTree.ParseText(
            desugaredRoot.ToFullString(),
            new CSharpParseOptions(LanguageVersion.Latest),
            path: originalTree.FilePath);
        var compilation = CSharpCompilation.Create(
            "c26-qgate-" + Guid.NewGuid().ToString("N"),
            new[] { desugaredTree },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        return (compilation.GetSemanticModel(desugaredTree), desugaredTree.GetRoot());
    }

    private static (SemanticModel Model, SyntaxNode Root) Recompile(string output)
    {
        var tree = CSharpSyntaxTree.ParseText(
            output, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            "c26-qgate-re-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "matrix output must recompile: " + string.Join("; ", errors.Take(8)));
        return (compilation.GetSemanticModel(tree), tree.GetRoot());
    }

    /// <summary>
    /// Test gate exercising the LC0011 wire-up directly: preserves nothing
    /// (simulating a lost node) and trips exactly one of the two tripwires.
    /// </summary>
    private sealed class ThrowingGate : ILinqRewriteGate
    {
        private readonly bool _enterExpansion;

        public ThrowingGate(bool enterExpansion) => _enterExpansion = enterExpansion;

        public void ObserveInvocation(InvocationExpressionSyntax invocation) { }

        public void ObserveForeachCollection(InvocationExpressionSyntax collection) { }

        public bool ShouldPreserveInvocation(InvocationExpressionSyntax invocation) => false;

        public bool ShouldPreserveForeachCollection(InvocationExpressionSyntax collection) => false;

        public void GuardEnterExpansion(InvocationExpressionSyntax invocation)
        {
            if (_enterExpansion)
            {
                throw new LinqRouteInvariantException(
                    "synthetic LC0011: preserved node reached expansion (test tripwire)",
                    "SyntheticProbe.cs",
                    7);
            }
        }

        public void GuardSyntaxNameFallback(InvocationExpressionSyntax invocation, string syntaxName)
        {
            if (!_enterExpansion)
            {
                throw new LinqRouteInvariantException(
                    $"synthetic LC0011: syntax-name fallback for '{syntaxName}' (test tripwire)",
                    "SyntheticProbe.cs",
                    9);
            }
        }
    }
}
