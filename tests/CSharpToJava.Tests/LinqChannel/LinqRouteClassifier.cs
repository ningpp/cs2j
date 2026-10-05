// C26 M0 probe: prototype LinqRouteClassifier.
//
// This is the research/prototype implementation pinned by the C26 LINQ channel
// design (docs/superpowers/specs/2026-09-29-c26-linq-channel-design.md §4.2,
// decisions D1/D2/D6). It lives INSIDE the test project on purpose: M1 will
// promote it to src/CSharpToJava.Core/LinqChannel/LinqRouteClassifier.cs.
// No production code references it.
//
// Algorithm (verbatim from §4.2, safety-first reading of the M0 task):
//   * Route an invocation ONLY by the bound method symbol's ContainingType
//     (System.Linq.Enumerable vs System.Linq.Queryable). Syntax names never
//     participate in routing (D1).
//   * ReducedExtension symbols are normalized through ReducedFrom first.
//   * When no unique symbol was bound, result is Ambiguous, split by
//     CandidateReason into Unbound (CandidateReason.None) / Candidate
//     (everything else). Candidate symbols are NOT used for host routing:
//     when overload resolution itself failed, the compiler picked no winner,
//     so the gate must preserve (LC0010) instead of second-guessing.
//   * Each argument's GetTypeInfo(arg).ConvertedType is checked for
//     System.Linq.Expressions.Expression<T> (D2).
//   * ClassifyChain preserves the whole chain when any node is
//     Queryable/Expression/Ambiguous (D6, v1 conservative chain rule).

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Tests.LinqChannel.Probe;

public enum LinqRouteKind
{
    Enumerable,
    Queryable,
    ExpressionArgument,
    NonLinq,
    Ambiguous,
}

public enum LinqRouteAmbiguity
{
    None = 0,
    Unbound,
    Candidate,
    UnsupportedExpressionNode,
}

public readonly record struct LinqRouteDecision(
    LinqRouteKind Kind,
    IMethodSymbol? BoundMethod,
    string? HostDisplay,
    LinqRouteAmbiguity Ambiguity,
    CandidateReason CandidateReason,
    int ExpressionArgumentCount)
{
    public bool IsPreserved =>
        Kind is LinqRouteKind.Queryable or LinqRouteKind.Ambiguous
        || ExpressionArgumentCount > 0;

    public static LinqRouteDecision NonLinq(IMethodSymbol method, string host) =>
        new(LinqRouteKind.NonLinq, method, host, LinqRouteAmbiguity.None, CandidateReason.None, 0);
}

public enum LinqChainRoute
{
    /// <summary>Every node is Enumerable/NonLinq: eligible for procedural expansion.</summary>
    Expandable,
    /// <summary>All LINQ nodes on the chain bind to System.Linq.Queryable.</summary>
    PreserveQueryable,
    /// <summary>Chain crosses Enumerable and Queryable hosts (D6 boundary mix).</summary>
    PreserveMixed,
    /// <summary>At least one node failed to bind a unique symbol (LC0010).</summary>
    PreserveAmbiguous,
}

public readonly record struct LinqChainDecision(
    LinqChainRoute Route,
    IReadOnlyList<LinqRouteDecision> Nodes);

/// <summary>
/// M0 prototype of the C26 three-way route gate. M1 promotion target:
/// src/CSharpToJava.Core/LinqChannel/LinqRouteClassifier.cs.
/// </summary>
public sealed class LinqRouteClassifier
{
    public const string EnumerableHost = "System.Linq.Enumerable";
    public const string QueryableHost = "System.Linq.Queryable";
    public const string ExpressionNamespace = "System.Linq.Expressions";

    private readonly SemanticModel _semantic;

    public LinqRouteClassifier(SemanticModel semantic) => _semantic = semantic;

    public LinqRouteDecision ClassifyInvocation(InvocationExpressionSyntax invocation)
    {
        var info = _semantic.GetSymbolInfo(invocation);
        var symbol = NormalizeReduced(info.Symbol as IMethodSymbol);

        var expressionArgumentCount = 0;
        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            if (ClassifyArgument(argument.Expression).Kind == LinqRouteKind.ExpressionArgument)
                expressionArgumentCount++;
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

        var hostDisplay = symbol.ContainingType.OriginalDefinition.ToDisplayString();

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
                    hostDisplay,
                    LinqRouteAmbiguity.UnsupportedExpressionNode,
                    CandidateReason.None,
                    expressionArgumentCount);
            }

            return new LinqRouteDecision(
                LinqRouteKind.Enumerable,
                symbol,
                hostDisplay,
                LinqRouteAmbiguity.None,
                CandidateReason.None,
                0);
        }

        if (hostDisplay == QueryableHost)
        {
            return new LinqRouteDecision(
                LinqRouteKind.Queryable,
                symbol,
                hostDisplay,
                LinqRouteAmbiguity.None,
                CandidateReason.None,
                expressionArgumentCount);
        }

        return LinqRouteDecision.NonLinq(symbol, hostDisplay);
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
    /// System.Linq.Expressions.Expression&lt;TDelegate&gt;.
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
    /// subtree (outermost invocation first). Nested invocations inside
    /// arguments are separate chains; conditional-access chains are not
    /// followed by the M0 prototype (reported as a known limitation for M1).
    /// </summary>
    public static IReadOnlyList<List<InvocationExpressionSyntax>> FindChains(SyntaxNode root)
    {
        var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>().ToHashSet();

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

            // Outermost-first order matches source reading order.
            chains.Add(chain);
        }

        return chains;
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
            return null;
        return symbol.MethodKind == MethodKind.ReducedExtension
            ? symbol.ReducedFrom ?? symbol
            : symbol;
    }
}
