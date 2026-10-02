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
}

/// <summary>
/// One frozen lift unit (design §4.3): all symbols sharing one holder class
/// (or one array-form storage set) and one instance. Group key is
/// (host member, declaration scope, foreach-per-iteration loop). Built by
/// HolderPlanner (Task 11) from CapturedSymbolAnalysis; the audit tail and
/// the transform path consume the SAME plans, so audit names == generated names.
/// </summary>
internal sealed class HolderGroupPlan
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

    /// <summary>Lifted symbols in deterministic order (DeclaringNode.SpanStart), Task 11 freezes it.</summary>
    public List<CapturedSymbolPlan> Symbols { get; } = new();
}

/// <summary>One lifted symbol within a <see cref="HolderGroupPlan"/>.</summary>
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
