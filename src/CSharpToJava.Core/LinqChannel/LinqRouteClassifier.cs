using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LinqChannel;

/// <summary>
/// C26 three-way route gate (design §4.2, D1/D2/D6/D7). Promoted from the M0
/// probe prototype with three M0-measured refinements:
/// <list type="number">
/// <item><see cref="nameof"/> pseudo-calls are excluded before routing
/// (M0 measured 3680 such "unbound" sites on EF Core — they are never calls).</item>
/// <item><see cref="FindChains"/> only reports member-access-connected chains
/// containing at least one known LINQ operator node.</item>
/// <item>Host identity normalizes IOrderedEnumerable/IOrderedQueryable
/// receiver interfaces (reduced ThenBy/ThenByDescending, M0 measurement).</item>
/// </list>
/// Routing uses ONLY the bound method symbol's containing static class; syntax
/// names participate solely in chain discovery (never in host routing).
/// </summary>
public sealed class LinqRouteClassifier
{
    public const string EnumerableHost = LinqHosts.EnumerableHost;
    public const string QueryableHost = LinqHosts.QueryableHost;
    public const string ExpressionNamespace = LinqHosts.ExpressionNamespace;

    /// <summary>
    /// LINQ operator syntax names, used only for chain discovery pre-filtering
    /// and provider-custom-call counting. Never used for host routing (D1).
    /// </summary>
    public static readonly IReadOnlySet<string> KnownOperatorNames = new HashSet<string>(
        new[]
        {
            "Where", "Select", "SelectMany", "Join", "GroupJoin",
            "OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending",
            "GroupBy", "Skip", "Take", "SkipWhile", "TakeWhile", "SkipLast", "TakeLast",
            "Any", "All", "Count", "LongCount",
            "First", "FirstOrDefault", "Last", "LastOrDefault", "Single", "SingleOrDefault",
            "Distinct", "DistinctBy", "Concat", "Union", "UnionBy", "Intersect", "IntersectBy",
            "Except", "ExceptBy", "Reverse", "AsEnumerable", "AsQueryable",
            "ToList", "ToArray", "ToDictionary", "ToHashSet", "ToLookup",
            "Aggregate", "Sum", "Average", "Min", "Max", "MaxBy", "MinBy",
            "Contains", "SequenceEqual", "ElementAt", "ElementAtOrDefault",
            "DefaultIfEmpty", "Append", "Prepend", "Chunk", "Cast", "OfType", "Zip",
            "Order", "OrderDescending",
        },
        StringComparer.Ordinal);

    private readonly SemanticModel _semantic;

    public LinqRouteClassifier(SemanticModel semantic) => _semantic = semantic;

    public LinqRouteDecision ClassifyInvocation(InvocationExpressionSyntax invocation)
    {
        // M0 finding: nameof(...) is syntactically an InvocationExpression but
        // a compiler pseudo-function (GetSymbolInfo is always null). Drop it
        // before routing so it never becomes an LC0010 ambiguity.
        if (IsNameofPseudoCall(invocation))
        {
            return LinqRouteDecision.NonLinq(null, null);
        }

        var info = _semantic.GetSymbolInfo(invocation);
        var symbol = NormalizeReduced(info.Symbol as IMethodSymbol);

        var expressionArgumentCount = 0;
        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            if (ClassifyArgument(argument.Expression).Kind == LinqRouteKind.ExpressionArgument)
            {
                expressionArgumentCount++;
            }
        }

        if (symbol is null)
        {
            // No unique bound method. Candidates (overload resolution failure,
            // ambiguity, wrong return type, …) are deliberately NOT routed by
            // their containing type: the gate only trusts a unique compiler
            // decision. CandidateReason.None means nothing was found at all
            // (missing metadata / error-type receiver / dynamic).
            var ambiguity = info is { Symbol: null, CandidateSymbols.Length: > 0 }
                ? LinqRouteAmbiguity.Candidate
                : LinqRouteAmbiguity.Unbound;
            return new LinqRouteDecision(
                LinqRouteKind.Ambiguous,
                null,
                null,
                ambiguity,
                info.CandidateReason,
                expressionArgumentCount);
        }

        var rawHost = symbol.ContainingType.OriginalDefinition.ToDisplayString();
        var hostDisplay = LinqHosts.NormalizeToStaticHost(rawHost);

        if (hostDisplay == EnumerableHost)
        {
            // D2/§4.2 step 5: Expression<> argument on an Enumerable host is a
            // BCL-impossible shape (Enumerable operators take Func<>). Treat it
            // as an unsupported node rather than silently expanding.
            if (expressionArgumentCount > 0)
            {
                return new LinqRouteDecision(
                    LinqRouteKind.Ambiguous,
                    symbol,
                    EnumerableHost,
                    LinqRouteAmbiguity.UnsupportedExpressionNode,
                    CandidateReason.None,
                    expressionArgumentCount);
            }

            return new LinqRouteDecision(
                LinqRouteKind.Enumerable,
                symbol,
                EnumerableHost,
                LinqRouteAmbiguity.None,
                CandidateReason.None,
                0);
        }

        if (hostDisplay == QueryableHost)
        {
            return new LinqRouteDecision(
                LinqRouteKind.Queryable,
                symbol,
                QueryableHost,
                LinqRouteAmbiguity.None,
                CandidateReason.None,
                expressionArgumentCount);
        }

        return LinqRouteDecision.NonLinq(symbol, rawHost);
    }

    public LinqRouteDecision ClassifyArgument(ExpressionSyntax argument)
    {
        var convertedType = _semantic.GetTypeInfo(argument).ConvertedType;
        if (IsExpressionType(convertedType))
        {
            return new LinqRouteDecision(
                LinqRouteKind.ExpressionArgument,
                null,
                convertedType!.ToDisplayString(),
                LinqRouteAmbiguity.None,
                CandidateReason.None,
                0);
        }

        return new LinqRouteDecision(
            LinqRouteKind.NonLinq,
            null,
            convertedType?.ToDisplayString(),
            LinqRouteAmbiguity.None,
            CandidateReason.None,
            0);
    }

    /// <summary>
    /// D2: true when the node's converted type is
    /// <see cref="System.Linq.Expressions.Expression{TDelegate}"/>.
    /// </summary>
    public bool IsExpressionQuoted(ExpressionSyntax node) =>
        ClassifyArgument(node).Kind == LinqRouteKind.ExpressionArgument;

    public LinqChainDecision ClassifyChain(IReadOnlyList<InvocationExpressionSyntax> chain)
    {
        var nodes = chain.Select(ClassifyInvocation).ToArray();

        var hasAmbiguous = nodes.Any(n => n.Kind == LinqRouteKind.Ambiguous);
        var hasQueryable = nodes.Any(n => n.Kind == LinqRouteKind.Queryable || n.ExpressionArgumentCount > 0);
        var hasEnumerable = nodes.Any(n => n.Kind == LinqRouteKind.Enumerable);

        var route = hasAmbiguous
            ? LinqChainRoute.PreserveAmbiguous
            : hasQueryable && hasEnumerable
                ? LinqChainRoute.PreserveMixed
                : hasQueryable
                    ? LinqChainRoute.PreserveQueryable
                    : LinqChainRoute.Expandable;

        return new LinqChainDecision(route, nodes);
    }

    /// <summary>
    /// Enumerate maximal receiver-member-access invocation chains in a syntax
    /// subtree (outermost invocation first). Nested invocations inside arguments
    /// are separate chains; conditional-access chains are not followed.
    /// M1 tightening: <see cref="nameof"/> pseudo-calls are excluded and only
    /// chains containing at least one node whose member name is a known LINQ
    /// operator are returned (the M0 prototype reported every invocation root,
    /// which inflated chain counts with non-LINQ/nameof singletons).
    /// </summary>
    public static IReadOnlyList<List<InvocationExpressionSyntax>> FindChains(SyntaxNode root)
    {
        var invocations = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => !IsNameofPseudoCall(invocation))
            .ToHashSet();

        var childToParent = new Dictionary<InvocationExpressionSyntax, InvocationExpressionSyntax>();
        foreach (var invocation in invocations)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax { Expression: InvocationExpressionSyntax inner }
                && invocations.Contains(inner))
            {
                childToParent[inner] = invocation;
            }
        }

        var chains = new List<List<InvocationExpressionSyntax>>();
        foreach (var rootInvocation in invocations.Where(i => !childToParent.ContainsKey(i)))
        {
            var chain = new List<InvocationExpressionSyntax>();
            var current = rootInvocation;
            while (true)
            {
                chain.Add(current);
                if (current.Expression is MemberAccessExpressionSyntax { Expression: InvocationExpressionSyntax next }
                    && invocations.Contains(next))
                {
                    current = next;
                }
                else
                {
                    break;
                }
            }

            if (chain.Any(HasLinqOperatorName))
            {
                // Outermost-first order matches source reading order.
                chains.Add(chain);
            }
        }

        return chains
            .OrderBy(chain => chain[0].SpanStart)
            .ToList();
    }

    /// <summary>True for member-access invocations whose member token is a known LINQ operator name.</summary>
    public static bool HasLinqOperatorName(InvocationExpressionSyntax invocation) =>
        invocation.Expression is MemberAccessExpressionSyntax memberAccess
        && KnownOperatorNames.Contains(memberAccess.Name.Identifier.Text);

    /// <summary>
    /// <c>nameof(...)</c> is an InvocationExpressionSyntax syntactically but a
    /// compiler pseudo-function (keyword token), never a real call site.
    /// </summary>
    public static bool IsNameofPseudoCall(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Text == "nameof",
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text == "nameof",
            _ => false,
        };
    }

    internal static bool IsExpressionType(ITypeSymbol? type)
    {
        return type is INamedTypeSymbol named
            && named.Name == "Expression"
            && named.ContainingNamespace.ToDisplayString() == ExpressionNamespace
            && named.TypeParameters.Length == 1;
    }

    private static IMethodSymbol? NormalizeReduced(IMethodSymbol? symbol)
    {
        if (symbol is null)
        {
            return null;
        }

        return symbol.MethodKind == MethodKind.ReducedExtension
            ? symbol.ReducedFrom ?? symbol
            : symbol;
    }
}
