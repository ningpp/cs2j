using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>Why a symbol is lifted (design D2). Primary reason with deterministic precedence.</summary>
public enum TriggerReason
{
    ExternalWrite,
    InsideLambdaWrite,
    RefOutArgument,
    ForeachIteration,

    /// <summary>C45 M2 (D5): zero-write-site symbol lifted to a read-only display
    /// class field; lambda-body reads are field-backed, external sites keep the
    /// original local/parameter, and the field is snapshot-initialized at the
    /// instance creation point.</summary>
    ReadOnlyValue,

    /// <summary>C45 M3 (D4): an outer lambda's own parameter captured by a nested
    /// lambda; the parameter becomes a field of the outer lambda's home display
    /// class, entry-assigned when the migrated method starts.</summary>
    PromotedLambdaParameter,

    /// <summary>C45 M3 (D4): a local declared inside a migrated lambda body and
    /// mutably captured by a nested lambda; the cell becomes a field of the
    /// display group keyed at the local's declaration block.</summary>
    LambdaLocalMutable,
}

/// <summary>
/// One frozen lift unit (design §4.3): all symbols sharing one display class
/// (or one array-form storage set) and one instance. Group key is
/// (host member, declaration scope, foreach-per-iteration loop). Built by
/// HolderPlanner (Task 11) from CapturedSymbolAnalysis; the audit tail and
/// the transform path consume the SAME plans, so audit names == generated names.
/// <para>
/// C45 M2 evolution (design §4.1.1, D2 — additive only): this is the former
/// <c>HolderGroupPlan</c> renamed to <see cref="DisplayClassPlan"/> with every
/// C32 member kept verbatim; the trailing C45 members default to inert values,
/// so C32 consumers reading only the leading members produce byte-identical
/// output whenever the C45 fields are unset (M2 regression red line).
/// </para>
/// </summary>
internal sealed class DisplayClassPlan
{
    /// <summary>Type declaration the nested holder class is inserted into.</summary>
    public required TypeDeclarationSyntax HostType { get; init; }

    /// <summary>
    /// Routine the captures belong to (naming anchor + analysis root).
    /// SyntaxNode (not MemberDeclarationSyntax) because routine-typed
    /// accessors (get/set/add/remove) are analyzed as AccessorDeclarationSyntax.
    /// </summary>
    public required SyntaxNode HostMember { get; init; }

    /// <summary>Fully qualified display key of the host type (cross-file table key).</summary>
    public required string HostTypeKey { get; init; }

    /// <summary>Cs2jDisplayClass_&lt;member&gt;_&lt;n&gt; (unused textually in array form; still reserved).</summary>
    public required string ClassName { get; init; }

    /// <summary>__cs2jClosure&lt;n&gt;; array form uses per-symbol __cs2jArr_ names instead.</summary>
    public required string InstanceName { get; init; }

    /// <summary>True only for foreach iteration groups (instance created per iteration, D9).</summary>
    public required bool PerIteration { get; init; }

    /// <summary>Non-default comparator shape (design §4.4 tail / D1): one T[] per symbol.</summary>
    public required bool ArrayForm { get; init; }

    /// <summary>The foreach loop when <see cref="PerIteration"/>; null otherwise.</summary>
    public ForEachStatementSyntax? PerIterationLoop { get; init; }

    /// <summary>
    /// Method type parameter names (declaration order) a holder nested at class
    /// level must redeclare as its own arity. Host-type parameters are already
    /// in scope inside a nested class (C# scopes outer type parameters through
    /// nested types); only method arity is missing there.
    /// </summary>
    public IReadOnlyList<string> MethodTypeParameters { get; init; } = Array.Empty<string>();

    /// <summary>Generic suffix for the holder class name ("&lt;T1,T2&gt;"), or "".</summary>
    public string TypeArgumentSuffix =>
        MethodTypeParameters.Count == 0 ? string.Empty : "<" + string.Join(",", MethodTypeParameters) + ">";

    /// <summary>
    /// D4 (M3): chain-qualified type name for construction sites inside parent
    /// methods: `Cs2jDisplayClass_M_0&lt;T&gt;.Cs2jDisplayClass_M_1&lt;T&gt;`. Each
    /// display class redeclares the routine method arity independently.
    /// </summary>
    public string QualifiedClassName
    {
        get
        {
            var chain = new List<DisplayClassPlan> { this };
            for (var current = Parent; current != null; current = current.Parent)
                chain.Add(current);
            chain.Reverse();
            return string.Join(".", chain.Select(static g => g.ClassName + g.TypeArgumentSuffix));
        }
    }

    /// <summary>Lifted symbols in deterministic order (DeclaringNode.SpanStart), Task 11 freezes it.</summary>
    public List<CapturedSymbolPlan> Symbols { get; } = new();

    // ── C45 (design §4.1.1): additive members below; default values keep the
    //    C32 output byte-identical (D2 red line). ──

    /// <summary>D7: lambdas of this group reference host instance members/`this`;
    /// the class gains `private readonly THost _cs2jThis;` plus a constructor.</summary>
    public bool CapturesThis { get; set; }

    /// <summary>D19 informational environment: host-type parameter names first
    /// (already in nested-class scope, never redeclared), then the method
    /// parameters of <see cref="MethodTypeParameters"/> (the declared arity).</summary>
    public IReadOnlyList<string> ClassTypeParameters { get; init; } = Array.Empty<string>();

    /// <summary>
    /// D4 (M3): group scope anchor — the statement block the group instance
    /// belongs to (routine/block nesting), or the enclosing lambda node when
    /// the group lives in an expression-bodied lambda (block-wrapped at lift
    /// time). Registry key input (routine + anchor).
    /// </summary>
    public SyntaxNode? ScopeAnchor { get; set; }

    /// <summary>D4 (M3): enclosing display class; null = chain top.</summary>
    public DisplayClassPlan? Parent { get; set; }

    /// <summary>D4 (M3): the `_cs2jParent` field name on this class (null at chain top).</summary>
    public string? ParentFieldName { get; set; }

    /// <summary>D9: migrated lambda bodies as instance methods `Lambda_&lt;k&gt;`.</summary>
    public List<LambdaMethodPlan> Methods { get; } = new();

    /// <summary>D10: zero-capture host-level static methods (M2 keeps them in the
    /// plan bag; this member is the D2 contract slot, populated from M3 on).</summary>
    public List<StaticLambdaPlan> HostStaticMethods { get; } = new();

    /// <summary>D8: lambda nodes replaced by explicit delegate creations.</summary>
    public List<DelegateSitePlan> Sites { get; } = new();

    /// <summary>Field names already present in the emitted class (C32 mutable
    /// fields first); read-only field reservation continues this set.</summary>
    public HashSet<string> UsedFieldNames { get; } = new(StringComparer.Ordinal);

    /// <summary>True when the class shape grew beyond the C32 field-only holder
    /// (this field, read-only fields, lambda methods or a parent link) — i.e.
    /// the emitted class needs a consolidation edit; false keeps the emitted
    /// class untouched.</summary>
    public bool NeedsConsolidation =>
        CapturesThis
        || Parent != null
        || Symbols.Any(s => s.Reason is TriggerReason.ReadOnlyValue
            or TriggerReason.PromotedLambdaParameter
            or TriggerReason.LambdaLocalMutable)
        || Methods.Count > 0;
}

/// <summary>
/// C45 M2 (design §4.1.1 <c>LambdaMethodPlan</c>): one capturing lambda lifted
/// to an instance method `Lambda_&lt;k&gt;` on its display class. Anchored by the
/// received-tree SpanStart + raw kind (same pairing discipline as
/// <see cref="StaticLambdaPlan"/>) so <see cref="DelegateSiteRewriteStage"/>
/// resolves every occurrence — the original site and any body copy relocated
/// into a migrated enclosing method — without re-analysis. Signature and body
/// rewrite maps live on the stage-side <c>PendingInstanceLambda</c>; this plan
/// is the durable, per-class fact.
/// </summary>
/// <param name="SiteSpanStart">Absolute SpanStart of the planned lambda node.</param>
/// <param name="SiteRawKind">RawKind of the planned lambda node (defensive pairing).</param>
/// <param name="ClassName">Home display class (Cs2jDisplayClass_&lt;member&gt;_&lt;n&gt;).</param>
/// <param name="InstanceName">Receiver of the method group at delegate sites (D8).</param>
/// <param name="MethodName">Lambda_&lt;k&gt;, k per class in SpanStart order (D9).</param>
/// <param name="IsAsync">D17: async modifier carried over verbatim.</param>
/// <param name="DelegateTypeDisplay">Symbol-rendered target delegate (D8).</param>
internal sealed record LambdaMethodPlan(
    int SiteSpanStart,
    int SiteRawKind,
    string ClassName,
    string InstanceName,
    string MethodName,
    bool IsAsync,
    string DelegateTypeDisplay);

/// <summary>
/// C45 M2 (design §4.1.1 <c>DelegateSitePlan</c>, D8/D16): one capturing lambda
/// site rewritten to `new &lt;DelType&gt;(&lt;instance&gt;.Lambda_&lt;k&gt;)`. Span-anchored like
/// <see cref="StaticLambdaPlan"/> (single site vocabulary across the stage).
/// </summary>
internal sealed record DelegateSitePlan(
    int SiteSpanStart,
    int SiteRawKind,
    string DelegateTypeDisplay,
    string ReceiverExpression,
    string MethodName,
    bool IsStatic);

/// <summary>One lifted symbol within a <see cref="DisplayClassPlan"/>.</summary>
internal sealed record CapturedSymbolPlan(
    ISymbol Symbol,
    string OriginalName,
    /// <summary>Holder-class public field: _cs2jCap_&lt;name&gt; (collision-suffixed).</summary>
    string FieldName,
    /// <summary>Array-form storage local: __cs2jArr_&lt;name&gt;; equals FieldName in holder form.</summary>
    string StorageName,
    string TypeDisplayFullyQualified,
    SyntaxNode DeclaringNode,
    TriggerReason Reason,
    bool IsForeachIteration,
    ForEachStatementSyntax? ForeachLoop,
    ForStatementSyntax? ForLoop,
    VariableDeclaratorSyntax? Declarator,
    ParameterSyntax? Parameter,
    /// <summary>Frozen non-quoted sites (read/write/lock); the rewriter only touches these nodes.</summary>
    IReadOnlyList<ClosureSiteRef> Sites);
