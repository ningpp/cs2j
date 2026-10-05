// C26 M0 probe tests (② of the M0 deliverable list):
//
//   * Same-name Enumerable/Queryable operators are classified correctly in BOTH
//     reduced (src.Where(...)) and non-reduced/static (Enumerable.Where(...))
//     shapes — full operator table pinned by design §7.2 and D7.
//   * Queryable reduced display strings have ZERO intersection with
//     LinqRewriter.KnownMethods (all of which are IEnumerable<TSource> shapes),
//     and Enumerable reduced display strings DO hit the table (sanity anchor).
//   * Expression<Func<>> arguments are recognized as the third route both as
//     inline lambdas and as pre-built variables (D2), including a direct check
//     that Enumerable host + Expression arg is reported as unsupported.
//
// Conclusions are back-filled into
// docs/superpowers/specs/2026-09-29-c26-linq-channel-design.md §1.3/§9 M0.

using System.Reflection;
using CSharpToJava.Core.LinqChannel;
using CSharpToJava.Core.LinqRewrite;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit.Abstractions;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace CSharpToJava.Tests.LinqChannel.Probe;

public sealed class LinqRouteProbeTests
{
    private readonly ITestOutputHelper _output;

    public LinqRouteProbeTests(ITestOutputHelper output) => _output = output;

    // TPA-ish reference set: System.Linq.Queryable and System.Linq.Expressions
    // live in separate assemblies on .NET (the production pipeline adds the
    // same references in ConversionPipeline.cs L177-184).
    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
        // List<T>/Dictionary<TKey,TValue> live in System.Collections on .NET 10.
        MetadataReference.CreateFromFile(typeof(System.Collections.Generic.HashSet<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Queryable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Expressions.Expression).Assembly.Location),
        MetadataReference.CreateFromFile(Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
        MetadataReference.CreateFromFile(Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Collections.dll")),
    };

    private const string Wrapper = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Linq.Expressions;

        public static class ProbeHost
        {
            public static void Run()
            {
                // Explicitly IEnumerable/IQueryable locals (not arrays): array
                // receivers get special reduced display names (TSource[].…) for
                // some operators, which LinqRewriter normalizes separately; the
                // route table pins host identity, not array sugar.
                IEnumerable<int> src = new[] { 1, 2, 3 };
                IEnumerable<int> second = new[] { 2, 3, 4 };
                IQueryable<int> q = src.AsQueryable();
                __BODY__
            }
        }
        """;

    private static (CSharpCompilation Compilation, SemanticModel Model) CompileBody(string body)
    {
        var source = Wrapper.Replace("__BODY__", body);
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            "c26-route-probe-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "probe snippet must compile: " + string.Join("; ", errors.Select(e => e.ToString())));

        return (compilation, compilation.GetSemanticModel(tree));
    }

    private static InvocationExpressionSyntax ChainRootNamed(SemanticModel model, string name)
    {
        var root = model.SyntaxTree.GetCompilationUnitRoot();
        var chains = LinqRouteClassifier.FindChains(root);
        var matches = chains
            .Where(c => OperatorName(c[0]) == name)
            .OrderBy(c => c[0].SpanStart)
            .ToList();
        Assert.NotEmpty(matches);
        // The wrapper itself contains one AsQueryable (q initialization);
        // probe statements are appended after it, so the target is the last
        // outermost chain of that name in source order.
        return matches[^1][0];
    }

    private static string OperatorName(InvocationExpressionSyntax invocation) =>
        (invocation.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.Text
        ?? invocation.Expression.ToString();

    /// <summary>
    /// Mirrors LinqRewriter.GetMethodFullName's semantic branch exactly
    /// (OriginalDefinition.ToDisplayString of the reduced symbol).
    /// </summary>
    private static string? RewriterSemanticIdentity(SemanticModel model, InvocationExpressionSyntax invocation)
        => (model.GetSymbolInfo(invocation.Expression).Symbol as IMethodSymbol)
            ?.OriginalDefinition.ToDisplayString();

    // ── Same-name operator matrix (reduced + static, both hosts) ───────────────

    // label, Enumerable reduced snippet, Enumerable static snippet,
    // Queryable reduced snippet, Queryable static snippet.
    public static readonly (string Op, string EnumReduced, string EnumStatic,
        string QueryReduced, string QueryStatic)[] OperatorRows =
    {
        ("Where", "src.Where(x => x > 0)", "Enumerable.Where(src, x => x > 0)",
            "q.Where(x => x > 0)", "Queryable.Where(q, x => x > 0)"),
        ("Select", "src.Select(x => x)", "Enumerable.Select(src, x => x)",
            "q.Select(x => x)", "Queryable.Select(q, x => x)"),
        ("Join",
            "src.Join(second, x => x, y => y, (a, b) => a + b)",
            "Enumerable.Join(src, second, x => x, y => y, (a, b) => a + b)",
            "q.Join(second, x => x, y => y, (a, b) => a + b)",
            "Queryable.Join(q, second, x => x, y => y, (a, b) => a + b)"),
        ("GroupJoin",
            "src.GroupJoin(second, x => x, y => y, (a, bs) => a + bs.Count())",
            "Enumerable.GroupJoin(src, second, x => x, y => y, (a, bs) => a + bs.Count())",
            "q.GroupJoin(second, x => x, y => y, (a, bs) => a + bs.Count())",
            "Queryable.GroupJoin(q, second, x => x, y => y, (a, bs) => a + bs.Count())"),
        ("OrderBy", "src.OrderBy(x => x)", "Enumerable.OrderBy(src, x => x)",
            "q.OrderBy(x => x)", "Queryable.OrderBy(q, x => x)"),
        ("OrderByDescending", "src.OrderByDescending(x => x)", "Enumerable.OrderByDescending(src, x => x)",
            "q.OrderByDescending(x => x)", "Queryable.OrderByDescending(q, x => x)"),
        ("ThenBy",
            "src.OrderBy(x => x).ThenBy(x => -x)",
            "Enumerable.ThenBy(src.OrderBy(x => x), x => -x)",
            "q.OrderBy(x => x).ThenBy(x => -x)",
            "Queryable.ThenBy(q.OrderBy(x => x), x => -x)"),
        ("GroupBy", "src.GroupBy(x => x)", "Enumerable.GroupBy(src, x => x)",
            "q.GroupBy(x => x)", "Queryable.GroupBy(q, x => x)"),
        ("Skip", "src.Skip(1)", "Enumerable.Skip(src, 1)",
            "q.Skip(1)", "Queryable.Skip(q, 1)"),
        ("Take", "src.Take(1)", "Enumerable.Take(src, 1)",
            "q.Take(1)", "Queryable.Take(q, 1)"),
        ("Any", "src.Any(x => x > 0)", "Enumerable.Any(src, x => x > 0)",
            "q.Any(x => x > 0)", "Queryable.Any(q, x => x > 0)"),
        ("Count", "src.Count()", "Enumerable.Count(src)",
            "q.Count()", "Queryable.Count(q)"),
        ("First", "src.First()", "Enumerable.First(src)",
            "q.First()", "Queryable.First(q)"),
        ("Single", "src.Single()", "Enumerable.Single(src)",
            "q.Single()", "Queryable.Single(q)"),
        ("Distinct", "src.Distinct()", "Enumerable.Distinct(src)",
            "q.Distinct()", "Queryable.Distinct(q)"),
        ("Concat", "src.Concat(second)", "Enumerable.Concat(src, second)",
            "q.Concat(second)", "Queryable.Concat(q, second)"),
        ("Union", "src.Union(second)", "Enumerable.Union(src, second)",
            "q.Union(second)", "Queryable.Union(q, second)"),
        ("Reverse", "src.Reverse()", "Enumerable.Reverse(src)",
            "q.Reverse()", "Queryable.Reverse(q)"),
    };

    public static TheoryData<string, string, string, string, string> SameNameOperators
    {
        get
        {
            var data = new TheoryData<string, string, string, string, string>();
            foreach (var row in OperatorRows)
                data.Add(row.Op, row.EnumReduced, row.EnumStatic, row.QueryReduced, row.QueryStatic);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(SameNameOperators))]
    public void SameNameOperator_ReducedAndStatic_ClassifyByHost(
        string op, string enumReduced, string enumStatic, string queryReduced, string queryStatic)
    {
        // ── Enumerable host, reduced sugar ──────────────────────────────────
        var (enumComp, enumModel) = CompileBody(enumReduced + ";");
        var enumNode = ChainRootNamed(enumModel, op);
        var enumSymbol = (IMethodSymbol)enumModel.GetSymbolInfo(enumNode).Symbol!;
        Assert.Equal(MethodKind.ReducedExtension, enumSymbol.MethodKind);

        var enumDecision = new LinqRouteClassifier(enumModel).ClassifyInvocation(enumNode);
        Assert.Equal(LinqRouteKind.Enumerable, enumDecision.Kind);
        Assert.Equal(LinqRouteClassifier.EnumerableHost, enumDecision.HostDisplay);
        Assert.Equal(LinqRouteAmbiguity.None, enumDecision.Ambiguity);
        var enumReducedDisplay = RewriterSemanticIdentity(enumModel, enumNode);
        _output.WriteLine($"[Enumerable/reduced] {op}: {enumReducedDisplay}");

        // ── Enumerable host, explicit static form ───────────────────────────
        var (esComp, esModel) = CompileBody(enumStatic + ";");
        var esNode = ChainRootNamed(esModel, op);
        var esSymbol = (IMethodSymbol)esModel.GetSymbolInfo(esNode).Symbol!;
        Assert.Equal(MethodKind.Ordinary, esSymbol.MethodKind);
        Assert.Null(esSymbol.ReducedFrom);
        var esDecision = new LinqRouteClassifier(esModel).ClassifyInvocation(esNode);
        Assert.Equal(LinqRouteKind.Enumerable, esDecision.Kind);

        // ── Queryable host, reduced sugar ───────────────────────────────────
        var (qComp, qModel) = CompileBody(queryReduced + ";");
        var qNode = ChainRootNamed(qModel, op);
        var qSymbol = (IMethodSymbol)qModel.GetSymbolInfo(qNode).Symbol!;
        Assert.Equal(MethodKind.ReducedExtension, qSymbol.MethodKind);
        Assert.NotNull(qSymbol.ReducedFrom);
        Assert.Equal(LinqRouteClassifier.QueryableHost,
            qSymbol.ReducedFrom!.ContainingType.ToDisplayString());

        var qDecision = new LinqRouteClassifier(qModel).ClassifyInvocation(qNode);
        Assert.Equal(LinqRouteKind.Queryable, qDecision.Kind);
        Assert.Equal(LinqRouteClassifier.QueryableHost, qDecision.HostDisplay);
        var queryReducedDisplay = RewriterSemanticIdentity(qModel, qNode);
        _output.WriteLine($"[Queryable/reduced]  {op}: {queryReducedDisplay}");
        Assert.NotNull(queryReducedDisplay);
        // ThenBy/ThenByDescending reduce onto IOrderedQueryable<TSource>, the
        // rest onto IQueryable<TSource>; the KnownMethods table only knows
        // IEnumerable/IOrderedEnumerable shapes.
        Assert.StartsWith("System.Linq.I", queryReducedDisplay);
        Assert.Contains("Queryable", queryReducedDisplay);

        // The pin: reduced Queryable identity never lives in the
        // Enumerable-shaped KnownMethods table.
        Assert.DoesNotContain(queryReducedDisplay, KnownMethodsValueSet());

        // ── Queryable host, explicit static form ────────────────────────────
        var (qsComp, qsModel) = CompileBody(queryStatic + ";");
        var qsNode = ChainRootNamed(qsModel, op);
        var qsSymbol = (IMethodSymbol)qsModel.GetSymbolInfo(qsNode).Symbol!;
        Assert.Equal(MethodKind.Ordinary, qsSymbol.MethodKind);
        var qsDecision = new LinqRouteClassifier(qsModel).ClassifyInvocation(qsNode);
        Assert.Equal(LinqRouteKind.Queryable, qsDecision.Kind);
        Assert.Equal(LinqRouteClassifier.QueryableHost, qsDecision.HostDisplay);
    }

    // ── D7 boundary operators (hosts are asymmetric on purpose) ────────────────

    public static readonly TheoryData<string, string, LinqRouteKind, string, LinqRouteKind> BoundaryOperators = new()
    {
        // AsEnumerable exists only on Enumerable; q.AsEnumerable() flips the
        // boundary to client evaluation and binds Enumerable.
        { "AsEnumerable", "src.AsEnumerable()", LinqRouteKind.Enumerable,
            "q.AsEnumerable()", LinqRouteKind.Enumerable },
        // AsQueryable exists only on Queryable; src.AsQueryable() flips to
        // server-shaped IQueryable.
        { "AsQueryable", "src.AsQueryable()", LinqRouteKind.Queryable,
            "q.AsQueryable()", LinqRouteKind.Queryable },
        // Materializers are Enumerable-only; an IQueryable receiver reaches
        // them through the IEnumerable base interface, so C# semantics itself
        // forces client enumeration at the call site (D7).
        { "ToList", "src.ToList()", LinqRouteKind.Enumerable,
            "q.ToList()", LinqRouteKind.Enumerable },
        { "ToArray", "src.ToArray()", LinqRouteKind.Enumerable,
            "q.ToArray()", LinqRouteKind.Enumerable },
        { "ToDictionary", "src.ToDictionary(x => x)", LinqRouteKind.Enumerable,
            "q.ToDictionary(x => x)", LinqRouteKind.Enumerable },
    };

    [Theory]
    [MemberData(nameof(BoundaryOperators))]
    public void BoundaryOperator_SymbolHostFollowsCSharpSemantics(
        string op, string enumCall, LinqRouteKind enumKind, string queryCall, LinqRouteKind queryKind)
    {
        var (_, enumModel) = CompileBody(enumCall + ";");
        var enumDecision = new LinqRouteClassifier(enumModel).ClassifyInvocation(ChainRootNamed(enumModel, op));
        Assert.Equal(enumKind, enumDecision.Kind);

        var (_, qModel) = CompileBody(queryCall + ";");
        var qDecision = new LinqRouteClassifier(qModel).ClassifyInvocation(ChainRootNamed(qModel, op));
        Assert.Equal(queryKind, qDecision.Kind);
        _output.WriteLine($"D7 {op}: {enumCall} => {enumDecision.Kind}; {queryCall} => {qDecision.Kind}");
    }

    [Fact]
    public void BoundaryOperator_StaticForms_BindStatedHosts()
    {
        var cases = new (string Op, string Static, LinqRouteKind Kind)[]
        {
            ("AsEnumerable", "Enumerable.AsEnumerable(q)", LinqRouteKind.Enumerable),
            ("AsQueryable", "Queryable.AsQueryable(src)", LinqRouteKind.Queryable),
            ("ToList", "Enumerable.ToList(q)", LinqRouteKind.Enumerable),
            ("ToArray", "Enumerable.ToArray(q)", LinqRouteKind.Enumerable),
            ("ToDictionary", "Enumerable.ToDictionary(q, x => x)", LinqRouteKind.Enumerable),
        };

        foreach (var (op, code, kind) in cases)
        {
            var (_, model) = CompileBody(code + ";");
            var decision = new LinqRouteClassifier(model).ClassifyInvocation(ChainRootNamed(model, op));
            Assert.Equal(kind, decision.Kind);
        }
    }

    // ── KnownMethods zero-intersection, full reduced Queryable table ───────────

    [Fact]
    public void QueryableReducedDisplayNames_HaveZeroIntersectionWithKnownMethods()
    {
        // KnownMethods is built by LinqRewriter's static ctor from all private
        // static string fields whose name ends with "Method" (LinqRewriter.cs
        // L64-67). They are private, so reflect exactly that rule.
        var knownMethods = KnownMethodsValueSet();
        Assert.NotEmpty(knownMethods);
        _output.WriteLine($"KnownMethods entries: {knownMethods.Count}");

        var intersections = new List<string>();
        foreach (var row in OperatorRows)
        {
            var (_, model) = CompileBody(row.QueryReduced + ";");
            var node = ChainRootNamed(model, row.Op);
            var display = RewriterSemanticIdentity(model, node);
            Assert.NotNull(display);
            if (knownMethods.Contains(display!))
                intersections.Add(display!);
        }

        Assert.Empty(intersections);

        // Reverse anchor: every same-name Enumerable reduced identity in this
        // table DOES hit KnownMethods. GroupJoin/Join use TOuter naming and are
        // present as JoinMethod/GroupJoinMethod; if a future BCL/Roslyn change
        // alters the reduced strings, this test forces a conscious re-pin.
        foreach (var row in OperatorRows)
        {
            var (_, model) = CompileBody(row.EnumReduced + ";");
            var node = ChainRootNamed(model, row.Op);
            var display = RewriterSemanticIdentity(model, node);
            Assert.NotNull(display);
            Assert.True(knownMethods.Contains(display!),
                $"Enumerable reduced identity for {row.Op} must stay in KnownMethods: {display}");
        }
    }

    private static HashSet<string>? _knownMethodsCache;

    private static HashSet<string> KnownMethodsValueSet()
    {
        if (_knownMethodsCache is not null)
            return _knownMethodsCache;

        var fields = typeof(LinqRewriter).GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.Name.EndsWith("Method", StringComparison.Ordinal)
                && f.FieldType == typeof(string))
            .Select(f => (string?)f.GetValue(null))
            .Where(v => v is not null)
            .Select(v => v!)
            .ToHashSet();
        Assert.NotEmpty(fields);
        _knownMethodsCache = fields;
        return fields;
    }

    // ── D2 third route: Expression<Func<>> arguments ───────────────────────────

    [Fact]
    public void ExpressionArgument_InlineLambda_RecognizedOnQueryableNotEnumerable()
    {
        var (_, qModel) = CompileBody("q.Where(x => x > 0);");
        var qNode = ChainRootNamed(qModel, "Where");
        var qClassifier = new LinqRouteClassifier(qModel);
        var qDecision = qClassifier.ClassifyInvocation(qNode);
        Assert.Equal(LinqRouteKind.Queryable, qDecision.Kind);
        Assert.Equal(1, qDecision.ExpressionArgumentCount);
        Assert.True(qDecision.IsPreserved);

        var qLambda = qNode.ArgumentList.Arguments[0].Expression;
        Assert.True(qClassifier.IsExpressionQuoted(qLambda));
        var converted = qModel.GetTypeInfo(qLambda).ConvertedType;
        Assert.Equal("System.Linq.Expressions.Expression<System.Func<int, bool>>", converted.ToDisplayString());

        var (_, eModel) = CompileBody("src.Where(x => x > 0);");
        var eNode = ChainRootNamed(eModel, "Where");
        var eClassifier = new LinqRouteClassifier(eModel);
        var eDecision = eClassifier.ClassifyInvocation(eNode);
        Assert.Equal(LinqRouteKind.Enumerable, eDecision.Kind);
        Assert.Equal(0, eDecision.ExpressionArgumentCount);
        var eLambda = eNode.ArgumentList.Arguments[0].Expression;
        Assert.False(eClassifier.IsExpressionQuoted(eLambda));
        Assert.Equal("System.Func<int, bool>", eModel.GetTypeInfo(eLambda).ConvertedType.ToDisplayString());
    }

    [Fact]
    public void ExpressionArgument_PreBuiltVariable_RecognizedViaConvertedType()
    {
        const string body = """
            Expression<Func<int, bool>> pred = x => x > 0;
            q.Where(pred);
            """;
        var (_, model) = CompileBody(body);
        var node = ChainRootNamed(model, "Where");
        var classifier = new LinqRouteClassifier(model);
        var decision = classifier.ClassifyInvocation(node);
        Assert.Equal(LinqRouteKind.Queryable, decision.Kind);
        Assert.Equal(1, decision.ExpressionArgumentCount);

        var arg = node.ArgumentList.Arguments[0].Expression;
        var argDecision = classifier.ClassifyArgument(arg);
        Assert.Equal(LinqRouteKind.ExpressionArgument, argDecision.Kind);
        Assert.True(classifier.IsExpressionQuoted(arg));
    }

    [Fact]
    public void ExpressionHandBuiltTree_ArgumentIsExpressionRoute()
    {
        const string body = """
            var param = Expression.Parameter(typeof(int), "x");
            var body = Expression.GreaterThan(param, Expression.Constant(0));
            var lambda = Expression.Lambda<Func<int, bool>>(body, param);
            q.Where(lambda);
            """;
        var (_, model) = CompileBody(body);
        var node = ChainRootNamed(model, "Where");
        var classifier = new LinqRouteClassifier(model);
        var decision = classifier.ClassifyInvocation(node);
        Assert.Equal(LinqRouteKind.Queryable, decision.Kind);
        Assert.Equal(1, decision.ExpressionArgumentCount);
        Assert.True(classifier.IsExpressionQuoted(node.ArgumentList.Arguments[0].Expression));
    }

    // ── D6 chain classification ────────────────────────────────────────────────

    [Fact]
    public void ClassifyChain_QueryableNodeAnywhere_PreservesWholeChain()
    {
        // Queryable start, AsEnumerable() boundary, then Enumerable operators:
        // C# binds the tail to Enumerable (real client boundary), but D6 v1
        // still preserves the whole chain.
        const string body = "var z = q.Where(x => x > 0).AsEnumerable().Select(x => x).ToList();";
        var (_, model) = CompileBody(body);
        var root = model.SyntaxTree.GetCompilationUnitRoot();
        var chain = Assert.Single(LinqRouteClassifier.FindChains(root)
            .Where(c => OperatorName(c[0]) == "ToList"));
        var decision = new LinqRouteClassifier(model).ClassifyChain(chain);
        Assert.Equal(LinqChainRoute.PreserveMixed, decision.Route);
        Assert.Contains(decision.Nodes, n => n.Kind == LinqRouteKind.Queryable);
        Assert.Contains(decision.Nodes, n => n.Kind == LinqRouteKind.Enumerable);

        // Pure Enumerable chain stays expandable.
        const string body2 = "var z2 = src.Where(x => x > 0).Select(x => x).ToList();";
        var (_, model2) = CompileBody(body2);
        var chain2 = Assert.Single(LinqRouteClassifier.FindChains(model2.SyntaxTree.GetCompilationUnitRoot())
            .Where(c => OperatorName(c[0]) == "ToList"));
        Assert.Equal(LinqChainRoute.Expandable,
            new LinqRouteClassifier(model2).ClassifyChain(chain2).Route);

        // Pure Queryable chain is preserved as Queryable.
        const string body3 = "var z3 = q.Where(x => x > 0).Select(x => x).OrderBy(x => x);";
        var (_, model3) = CompileBody(body3);
        var chain3 = Assert.Single(LinqRouteClassifier.FindChains(model3.SyntaxTree.GetCompilationUnitRoot())
            .Where(c => OperatorName(c[0]) == "OrderBy"));
        Assert.Equal(LinqChainRoute.PreserveQueryable,
            new LinqRouteClassifier(model3).ClassifyChain(chain3).Route);
    }

    [Fact]
    public void NonLinqSameNameMethod_IsNotRoutedByName()
    {
        const string source = """
            namespace Mine
            {
                public static class MyOps
                {
                    public static int[] Where(this int[] a, System.Func<int, bool> f) => a;
                }

                public static class P
                {
                    public static void Run()
                    {
                        var src = new[] { 1, 2, 3 };
                        var z = src.Where(x => x > 0);
                    }
                }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            "c26-route-probe-nonlinq-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var model = compilation.GetSemanticModel(tree);
        var node = model.SyntaxTree.GetCompilationUnitRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(n => OperatorName(n) == "Where");
        var decision = new LinqRouteClassifier(model).ClassifyInvocation(node);
        Assert.Equal(LinqRouteKind.NonLinq, decision.Kind);
        Assert.Equal("Mine.MyOps", decision.HostDisplay);
    }

    // ── M1 promotion findings (design §8 gate-4 M0校注 ③) ──────────────────────

    [Fact]
    public void NameofPseudoCall_IsExcludedBeforeRouting_AndFromChains()
    {
        const string body = "var n = nameof(src); var z = src.Count();";
        var (_, model) = CompileBody(body);
        var root = model.SyntaxTree.GetCompilationUnitRoot();

        var nameofNode = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(n => n.Expression is IdentifierNameSyntax { Identifier.Text: "nameof" });
        var nameofDecision = new LinqRouteClassifier(model).ClassifyInvocation(nameofNode);
        Assert.Equal(LinqRouteKind.NonLinq, nameofDecision.Kind);
        Assert.Equal(LinqRouteAmbiguity.None, nameofDecision.Ambiguity);

        // nameof never forms an Ambiguous singleton chain (M0 measured 3680
        // such fake sites); the real Count() chain is discovered normally.
        // (The probe wrapper itself also contains one AsQueryable() chain.)
        var chains = LinqRouteClassifier.FindChains(root);
        Assert.DoesNotContain(chains.SelectMany(c => c), n => n == nameofNode);
        var countChain = Assert.Single(chains.Where(c => OperatorName(c[0]) == "Count"));
        Assert.Equal("Count", OperatorName(countChain[0]));
    }

    [Fact]
    public void FindChains_OnlyReportsMemberAccessChainsWithALinqOperatorNode()
    {
        const string body = """
            Console.WriteLine("plain");
            var z = src.Where(x => x > 0).Select(x => x).ToList();
            """;
        var (_, model) = CompileBody(body);
        var root = model.SyntaxTree.GetCompilationUnitRoot();

        var chains = LinqRouteClassifier.FindChains(root);
        // WriteLine is a member-access invocation but no LINQ operator node →
        // never appears in a chain; the Where/Select/ToList chain survives.
        Assert.DoesNotContain(chains.SelectMany(c => c),
            n => OperatorName(n) == "WriteLine");
        var target = Assert.Single(chains.Where(c => OperatorName(c[0]) == "ToList"));
        Assert.Equal(new[] { "ToList", "Select", "Where" },
            target.Select(OperatorName).ToArray());
    }

    [Fact]
    public void FindChains_StaticForm_IsReportedAsSingleNodeChain()
    {
        var (_, model) = CompileBody("var z = Enumerable.Select(src, x => x);");
        var chains = LinqRouteClassifier.FindChains(model.SyntaxTree.GetCompilationUnitRoot());
        var target = Assert.Single(chains.Where(c => OperatorName(c[0]) == "Select"));
        Assert.Single(target);
        Assert.Equal("Select", OperatorName(target[0]));
    }

    [Fact]
    public void HostNormalization_CoversOrderedEnumerableAndOrderedQueryableInterfaces()
    {
        Assert.Equal(LinqHosts.EnumerableHost,
            LinqHosts.NormalizeToStaticHost("System.Collections.Generic.IEnumerable<TSource>"));
        Assert.Equal(LinqHosts.EnumerableHost,
            LinqHosts.NormalizeToStaticHost("System.Linq.IOrderedEnumerable<TSource>"));
        Assert.Equal(LinqHosts.QueryableHost,
            LinqHosts.NormalizeToStaticHost("System.Linq.IQueryable<TSource>"));
        Assert.Equal(LinqHosts.QueryableHost,
            LinqHosts.NormalizeToStaticHost("System.Linq.IOrderedQueryable<TSource>"));
        Assert.Equal(LinqHosts.EnumerableHost, LinqHosts.NormalizeToStaticHost(LinqHosts.EnumerableHost));
        Assert.Equal(LinqHosts.QueryableHost, LinqHosts.NormalizeToStaticHost(LinqHosts.QueryableHost));
        Assert.Null(LinqHosts.NormalizeToStaticHost("Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions"));
        Assert.Null(LinqHosts.NormalizeToStaticHost(null));
    }
}
