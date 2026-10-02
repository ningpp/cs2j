using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.DefaultParameterStripper.ParamsCollections;

/// <summary>
/// Classification of a params target type (design §2.1 matrix K1-K8). The kind
/// is derived purely from the parameter's type symbol — source parameter
/// symbols never carry params attributes (M0 probe Q2), so
/// <c>IParameterSymbol.IsParams</c>/<c>IsParamsCollection</c> are the only
/// authority for "is params" and are checked by the collector.
/// </summary>
public enum ParamsParamKind
{
    /// <summary>K1: one-dimensional array (classic params, incl. generic element types).</summary>
    Array,

    /// <summary>K2: System.Span&lt;T&gt; / System.ReadOnlySpan&lt;T&gt;.</summary>
    Span,

    /// <summary>K3: type carrying [CollectionBuilder] (v1 blocked with CS2J0607).</summary>
    Builder,

    /// <summary>K4: concrete collection-initializer type (IEnumerable&lt;T&gt; + accessible parameterless ctor + instance Add). List&lt;T&gt; lands here.</summary>
    Initializable,

    /// <summary>K5: one of the five legal collection interfaces.</summary>
    Interface,

    /// <summary>K6/K7/K8: unsupported (ref-struct element, outside the legal set, error type).</summary>
    Unsupported,
}

/// <summary>
/// Empty-call-site shape, fixed by the M0 probe (adjudication 5): the compiler
/// lowers empty params of kind Array/Span/Interface to the
/// System.Array.Empty&lt;T&gt; singleton, while Initializable concrete types
/// get a fresh instance per call.
/// </summary>
public enum ParamsEmptyStateShape
{
    /// <summary>Emit System.Array.Empty&lt;T&gt;() (K1/K2/K5 — same shape as the compiler lowering).</summary>
    ArrayEmpty,

    /// <summary>Emit a new instance, e.g. new List&lt;T&gt;() (K4; K3 factory shape is decided in M2).</summary>
    NewInstance,
}

/// <summary>Declaration-side strategy (design §2.3): A keeps the params modifier (arrays only), B strips it.</summary>
public enum ParamsDeclStrategy
{
    /// <summary>Strategy A: classic <c>params T[]</c> stays untouched (maps directly to Java varargs).</summary>
    KeepParams,

    /// <summary>Strategy B: non-array params collection is materialized into a plain parameter.</summary>
    Materialize,
}

/// <summary>Call-site form (design §1.2): A = comma-expanded list, C = empty argument list, B = single collection argument.</summary>
public enum ParamsSiteForm
{
    /// <summary>Form A: <c>M(1, 2, 3)</c> — compiler-synthesized expansion.</summary>
    ExpandedList,

    /// <summary>Form C: <c>M()</c> — compiler-synthesized empty collection.</summary>
    Empty,

    /// <summary>Form B: <c>M(c)</c> — user-supplied single collection argument (always passes through).</summary>
    SingleCollection,
}

/// <summary>What the argument rewriter must do with a params call site.</summary>
public enum ParamsSiteOutcome
{
    /// <summary>Wrap the expansion into an explicit collection construction.</summary>
    Wrap,

    /// <summary>Pass the user argument through unchanged (reference identity is preserved).</summary>
    Passthrough,

    /// <summary>Leave the site untouched and emit a diagnostic.</summary>
    Skip,
}

/// <summary>
/// Builder type + factory method name read from
/// <c>System.Runtime.CompilerServices.CollectionBuilderAttribute</c>
/// (constructor arguments: Type + string; M0 probe Q7).
/// </summary>
public sealed record CollectionBuilderInfo(
    INamedTypeSymbol BuilderType,
    string FactoryMethodName);

/// <summary>
/// Result of classifying a params parameter's type: the kind plus everything
/// the emitter needs (element type T, canonical concrete class, builder info,
/// empty-state shape) and the diagnostic code when the site must be skipped.
/// </summary>
/// <param name="Kind">Classified kind from the §2.1 matrix.</param>
/// <param name="ParameterType">The declared params parameter type.</param>
/// <param name="ElementType">Element type T when the kind carries one; otherwise null.</param>
/// <param name="CanonicalConcreteType">
/// Concrete class used for expansion: the type itself for K4, constructed
/// List&lt;T&gt; for K5; null for array/span/builder kinds.
/// </param>
/// <param name="BuilderInfo">Builder type + factory name for K3; otherwise null.</param>
/// <param name="EmptyState">Empty-call-site shape (M0 adjudication 5).</param>
/// <param name="SkipReason">CS2J06xx code explaining an Unsupported kind; null for supported kinds.</param>
public sealed record ParamsTargetClassification(
    ParamsParamKind Kind,
    ITypeSymbol ParameterType,
    ITypeSymbol? ElementType,
    INamedTypeSymbol? CanonicalConcreteType,
    CollectionBuilderInfo? BuilderInfo,
    ParamsEmptyStateShape EmptyState,
    string? SkipReason = null);

/// <summary>
/// Per-call-site plan produced by the params argument rewriter (T7/T8). The
/// expansion element syntax is always taken from the source ArgumentList — the
/// synthesized IOperation Value has no syntax (M0 probe Q3/R8).
/// </summary>
public sealed record ParamsSitePlan(
    IMethodSymbol TargetMethod,
    int ParamsSlotIndex,
    ITypeSymbol ParameterType,
    ParamsParamKind Kind,
    ParamsSiteForm Form,
    ParamsSiteOutcome Outcome,
    SeparatedSyntaxList<ArgumentSyntax> ExpansionArguments,
    INamedTypeSymbol? CanonicalConcreteType = null);

/// <summary>
/// Per-declaration plan registered by <see cref="ParamsSignatureCollector"/>:
/// one entry per method-like symbol whose last parameter is a params parameter.
/// </summary>
public sealed class ParamsDeclPlan
{
    public ParamsDeclPlan(
        SyntaxNode declarationNode,
        IMethodSymbol symbol,
        ParamsDeclStrategy strategy,
        int paramsSlotIndex,
        ParamsTargetClassification classification,
        ImmutableArray<IMethodSymbol> virtualFamily)
    {
        DeclarationNode = declarationNode;
        Symbol = symbol;
        Strategy = strategy;
        ParamsSlotIndex = paramsSlotIndex;
        Classification = classification;
        VirtualFamily = virtualFamily;
    }

    /// <summary>The declaring syntax node (method/ctor/local function/delegate/type declaration).</summary>
    public SyntaxNode DeclarationNode { get; }

    /// <summary>The declared method-like symbol (delegate invokes use DelegateInvokeMethod; primary ctors the instance ctor).</summary>
    public IMethodSymbol Symbol { get; }

    /// <summary>Strategy A keeps the params modifier (arrays), B materializes (non-arrays).</summary>
    public ParamsDeclStrategy Strategy { get; }

    /// <summary>Zero-based index of the params slot (always the last parameter).</summary>
    public int ParamsSlotIndex { get; }

    /// <summary>Type-symbol classification of the params target.</summary>
    public ParamsTargetClassification Classification { get; }

    /// <summary>
    /// The override/interface family of the slot (M0 probe Q10: all members
    /// carry identical params flags), used by the declaration materializer to
    /// keep strategy B consistent across the whole family (CS2J0612 fail-fast).
    /// </summary>
    public ImmutableArray<IMethodSymbol> VirtualFamily { get; }
}
