using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.LinqChannel;

/// <summary>
/// Three-way route of one LINQ invocation site plus the two non-LINQ buckets
/// (design §1.3/§4.2, D1/D2/D6).
/// </summary>
public enum LinqRouteKind
{
    /// <summary>Bound to <c>System.Linq.Enumerable</c>; eligible for procedural expansion.</summary>
    Enumerable,

    /// <summary>Bound to <c>System.Linq.Queryable</c>; the call node must be preserved.</summary>
    Queryable,

    /// <summary>An argument converts to <c>Expression&lt;TDelegate&gt;</c> (the third route).</summary>
    ExpressionArgument,

    /// <summary>Bound method belongs to neither LINQ host (provider extensions included).</summary>
    NonLinq,

    /// <summary>No unique symbol was bound; the chain is conservatively preserved (LC0010).</summary>
    Ambiguous,
}

/// <summary>Why an invocation could not be routed by bound symbol.</summary>
public enum LinqRouteAmbiguity
{
    None = 0,

    /// <summary>No symbol and no candidates (missing metadata / error-type receiver / dynamic).</summary>
    Unbound,

    /// <summary>Overload resolution produced candidates but no winner.</summary>
    Candidate,

    /// <summary>Enumerable host with an <c>Expression&lt;&gt;</c> argument (BCL-impossible shape).</summary>
    UnsupportedExpressionNode,
}

/// <summary>
/// Routing decision for one invocation. Candidate symbols are never used for
/// host routing: when overload resolution itself failed, the compiler picked
/// no winner, so the gate must preserve instead of second-guessing (D1).
/// </summary>
public readonly record struct LinqRouteDecision(
    LinqRouteKind Kind,
    IMethodSymbol? BoundMethod,
    string? HostDisplay,
    LinqRouteAmbiguity Ambiguity,
    CandidateReason CandidateReason,
    int ExpressionArgumentCount)
{
    /// <summary>Sites the channel must not proceduralize (M1: observe-only; M3: hard preserve).</summary>
    public bool IsPreserved =>
        Kind is LinqRouteKind.Queryable or LinqRouteKind.Ambiguous
        || ExpressionArgumentCount > 0;

    public static LinqRouteDecision NonLinq(IMethodSymbol? method, string? host) =>
        new(LinqRouteKind.NonLinq, method, host, LinqRouteAmbiguity.None, CandidateReason.None, 0);
}

/// <summary>Chain-level route under the v1 conservative rule (D6): one preserve node preserves the whole chain.</summary>
public enum LinqChainRoute
{
    /// <summary>Every node is Enumerable/NonLinq: eligible for procedural expansion.</summary>
    Expandable,

    /// <summary>All LINQ nodes on the chain bind to <c>System.Linq.Queryable</c>.</summary>
    PreserveQueryable,

    /// <summary>Chain crosses Enumerable and Queryable hosts (D6 boundary mix).</summary>
    PreserveMixed,

    /// <summary>At least one node failed to bind a unique symbol (LC0010).</summary>
    PreserveAmbiguous,
}

/// <summary>Per-node decisions for one member-access LINQ chain (outermost node first).</summary>
public readonly record struct LinqChainDecision(
    LinqChainRoute Route,
    IReadOnlyList<LinqRouteDecision> Nodes);
