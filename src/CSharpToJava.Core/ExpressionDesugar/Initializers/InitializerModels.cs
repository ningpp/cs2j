using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.Initializers;

/// <summary>
/// Classification of the position an initializer site occupies (design §2
/// decisions 10/11/12), used for context-lowering dispatch and statistics
/// bucketing.
/// </summary>
public enum InitializerContextKind
{
    Unknown,

    /// <summary>Local-declaration / using-declaration right-hand side.</summary>
    LocalDecl,

    /// <summary>return / yield return expression.</summary>
    Return,

    /// <summary>throw expression.</summary>
    Throw,

    /// <summary>Assignment right-hand side.</summary>
    Assignment,

    /// <summary>Expression-statement receiver.</summary>
    ExpressionStatement,

    /// <summary>using statement resource (statements).</summary>
    Using,

    /// <summary>?: branch.</summary>
    Ternary,

    /// <summary>?? right operand.</summary>
    NullCoalescing,

    /// <summary>Argument position (interleaved with preceding arguments).</summary>
    Argument,

    /// <summary>Field initializer (anchor-free, CS2J0538).</summary>
    FieldInitializer,

    /// <summary>Property initializer (anchor-free, CS2J0538).</summary>
    PropertyInitializer,

    /// <summary>Attribute argument (anchor-free, CS2J0538).</summary>
    AttributeArgument,

    /// <summary>Default parameter value (anchor-free, CS2J0538).</summary>
    DefaultParameter,

    /// <summary>Query clause (anchor-free, CS2J0538).</summary>
    QueryClause,

    /// <summary>base(...) / : this(...) constructor-chain argument (CS2J0537).</summary>
    ConstructorChain,

    /// <summary>Lambda / method-group inferred target (CS2J0537).</summary>
    LambdaTarget,
}

/// <summary>Decided lowering outcome for one initializer site.</summary>
public enum InitializerOutcome
{
    /// <summary>Analyzed but not yet decided (M0 idle state).</summary>
    Pending,

    /// <summary>Construct statement + ordered member-assignment sequence.</summary>
    ObjectSequence,

    /// <summary>Construct statement (arguments preserved) + ordered Add sequence.</summary>
    CollectionSequence,

    /// <summary>Bare target-typed <c>new()</c> explicitized to <c>new T(…)</c>.</summary>
    TargetTypedNewExplicit,

    /// <summary>
    /// Zero-temp explicitization: the initializer carries no members, so it is
    /// folded into <c>new T(args)</c> (covers <c>new C { }</c> and drops the
    /// empty initializer syntax).
    /// </summary>
    ZeroTempExplicit,

    /// <summary>Site intentionally kept, paired with a diagnostic.</summary>
    SkippedDiagnostic,

    /// <summary>
    /// Site nested inside a C36 collection-expression site: kept byte-identical
    /// and left out of the rewriter map so no ancestor is rebuilt (C36 looks its
    /// plans up by original node reference). Paired by the deferred counter.
    /// </summary>
    DeferredForCoexistence,

    /// <summary>Site in a v1-unsupported/not-liftable position (CS2J0537/0538).</summary>
    UnsupportedContext,
}

/// <summary>Classification of a collection initializer element's Add binding (design §2 decision 8).</summary>
public enum AddKind
{
    /// <summary>Add binding not (yet) resolved.</summary>
    Unresolved,

    /// <summary>Single-argument instance Add.</summary>
    Single,

    /// <summary>Multi-argument Add from a complex element initializer <c>{ a, b }</c>.</summary>
    Multi,

    /// <summary>Reduced extension-method Add.</summary>
    Extension,

    /// <summary>params Add in expanded form.</summary>
    Params,

    /// <summary>Add returning non-void (fluent); the value is discarded.</summary>
    Fluent,

    /// <summary>IEnumerable-without-Add fallback shape (M0 probe decides the legal subset).</summary>
    EnumerableFallback,
}

/// <summary>
/// Target-typed <c>new()</c> inference result (design §2 decision 10):
/// ConvertedType, nullability and source context.
/// </summary>
public sealed class TargetTypeInfo
{
    /// <summary><c>GetTypeInfo(node).ConvertedType</c> — the single source of truth.</summary>
    public ITypeSymbol? ConvertedType { get; init; }

    /// <summary>True when the converted type carried a nullable annotation.</summary>
    public bool IsNullable { get; init; }

    /// <summary>Underlying type with any nullable annotation stripped.</summary>
    public ITypeSymbol? UnderlyingType { get; init; }

    /// <summary>Source context classification.</summary>
    public InitializerContextKind Context { get; init; } = InitializerContextKind.Unknown;
}

/// <summary>
/// One member item of an object/collection initializer plan. The abstract
/// shape is append-only; concrete kinds are added by later milestones.
/// </summary>
public abstract class InitializerMemberPlan
{
    /// <summary>The initializer item syntax (assignment / element / complex element).</summary>
    public required ExpressionSyntax Syntax { get; init; }

    /// <summary>Source order inside the initializer (0-based).</summary>
    public int SourceOrder { get; init; }
}

/// <summary>Property-assignment member plan (<c>P = v</c>).</summary>
public sealed class PropertySetPlan : InitializerMemberPlan
{
    public IPropertySymbol? Property { get; init; }
    public required ExpressionSyntax Value { get; init; }
}

/// <summary>Field-assignment member plan (<c>F = v</c>).</summary>
public sealed class FieldSetPlan : InitializerMemberPlan
{
    public IFieldSymbol? Field { get; init; }
    public required ExpressionSyntax Value { get; init; }
}

/// <summary>Indexer-assignment member plan (<c>[k] = v</c>, incl. multi-dimensional).</summary>
public sealed class IndexSetPlan : InitializerMemberPlan
{
    public IPropertySymbol? Indexer { get; init; }
    public IReadOnlyList<ExpressionSyntax> Arguments { get; init; } =
        Array.Empty<ExpressionSyntax>();
    public required ExpressionSyntax Value { get; init; }
}

/// <summary>Nested object-creation member plan (<c>P = new T { … }</c>).</summary>
public sealed class NestedCreatePlan : InitializerMemberPlan
{
    public required InitializerPlan Nested { get; init; }
}

/// <summary>Nested member-initializer plan (<c>P = { … }</c>, mutates the existing member).</summary>
public sealed class NestedMemberInitPlan : InitializerMemberPlan
{
    public IReadOnlyList<InitializerMemberPlan> Members { get; init; } =
        Array.Empty<InitializerMemberPlan>();
}

/// <summary>Collection element Add member plan (<c>AddMethod</c> bound by the compiler).</summary>
public sealed class CollectionAddPlan : InitializerMemberPlan
{
    public IMethodSymbol? AddMethod { get; init; }
    public AddKind AddKind { get; init; } = AddKind.Unresolved;
    public IReadOnlyList<ExpressionSyntax> Arguments { get; init; } =
        Array.Empty<ExpressionSyntax>();
}

/// <summary>Full per-site plan (design §4 InitializerModels) produced by Analyze.</summary>
public sealed class InitializerPlan
{
    /// <summary>The creation syntax (explicit or target-typed) owning the initializer.</summary>
    public required BaseObjectCreationExpressionSyntax Node { get; init; }

    /// <summary>Resolved created type (explicit type or target-typed ConvertedType).</summary>
    public ITypeSymbol? CreatedType { get; init; }

    /// <summary>True for <see cref="ImplicitObjectCreationExpressionSyntax"/> (<c>new()</c>).</summary>
    public bool IsTargetTyped { get; init; }

    /// <summary>Constructor argument expressions (preserved verbatim, IN-3).</summary>
    public IReadOnlyList<ExpressionSyntax> Arguments { get; init; } =
        Array.Empty<ExpressionSyntax>();

    /// <summary>Target-typed inference result; null for explicit creations.</summary>
    public TargetTypeInfo? TargetType { get; init; }

    /// <summary>Member plans in source order (every initializer item represented).</summary>
    public List<InitializerMemberPlan> Members { get; init; } = new();

    /// <summary>Source-context classification.</summary>
    public InitializerContextKind Context { get; init; } = InitializerContextKind.Unknown;

    public InitializerOutcome Outcome { get; set; } = InitializerOutcome.Pending;

    /// <summary>Nesting depth (0 = outermost creation with an initializer).</summary>
    public int Depth { get; init; }

    /// <summary>True when the creation carries a (object/collection) initializer.</summary>
    public bool HasInitializer => Node.Initializer != null;

    /// <summary>True for a bare target-typed <c>new()</c> without an initializer (IN-9).</summary>
    public bool IsBareTargetTypedNew => IsTargetTyped && !HasInitializer;

    /// <summary>
    /// Pre-allocated <c>__ed_n</c> receiver temp (M1 sequence sites only;
    /// allocated deterministically in Analyze, consumed in Rewrite).
    /// </summary>
    public string? TempName { get; set; }

    /// <summary>Statement-lift order inside the anchor (post-order, inner first).</summary>
    public int LiftOrder { get; set; }

    /// <summary>
    /// Skip bookkeeping: a real CS2J05xx code for diagnostic-paired skips, the
    /// M2 sentinel for shape/context deferrals, null when the site is handled.
    /// </summary>
    public string? SkipCode { get; set; }
}