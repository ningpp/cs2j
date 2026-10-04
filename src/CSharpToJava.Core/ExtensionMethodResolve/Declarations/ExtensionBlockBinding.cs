using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExtensionMethodResolve.Declarations;

/// <summary>Semantic facts about the block receiver parameter.</summary>
internal sealed record BlockReceiverInfo(
    ITypeSymbol Type,
    string Name,
    RefKind RefKind,
    ScopedKind ScopedKind);

/// <summary>Plan for one extension block: host (block parent), receiver, type parameters and members.</summary>
internal sealed class BlockPlan
{
    public required ExtensionBlockDeclarationSyntax Block { get; init; }

    /// <summary>Block parent class (classic host); NOT the synthetic TypeKind.Extension container.</summary>
    public required INamedTypeSymbol Host { get; init; }

    public required BlockReceiverInfo Receiver { get; init; }

    public required IReadOnlyList<TypeParameterSyntax> BlockTypeParameters { get; init; }

    public required IReadOnlyList<TypeParameterConstraintClauseSyntax> BlockConstraints { get; init; }

    public required IReadOnlyList<MemberPlan> Members { get; init; }

    public required SyntaxAnnotation BlockAnnotation { get; init; }
}

/// <summary>Polymorphic plan for one block member declaration.</summary>
internal abstract record MemberPlan
{
    public required SyntaxNode Declaration { get; init; }

    public required string Name { get; init; }
}

/// <summary>EMD-METHOD member plan (block instance extension method).</summary>
internal sealed record MethodMemberPlan : MemberPlan
{
    public required IMethodSymbol Symbol { get; init; }

    public required IReadOnlyList<TypeParameterSyntax> MemberTypeParameters { get; init; }

    public required IReadOnlyList<TypeParameterConstraintClauseSyntax> MemberConstraints { get; init; }
}

/// <summary>EMD-PROP member plan (block instance extension property).</summary>
internal sealed record PropertyMemberPlan : MemberPlan
{
    public required IPropertySymbol Symbol { get; init; }

    public required PropertyDeclarationSyntax PropertyDeclaration { get; init; }

    public bool HasGet { get; init; }

    public bool HasSet { get; init; }
}

/// <summary>
/// Call site plan for a block method: <c>receiver.M(args)</c> (receiver
/// node from the source member access) OR a bare <c>M(args)</c> inside the
/// block (receiver synthesized as the block receiver).
/// </summary>
internal sealed record MethodSitePlan
{
    public required InvocationExpressionSyntax Invocation { get; init; }

    public required IMethodSymbol BoundSymbol { get; init; }

    public required MethodMemberPlan Member { get; init; }

    public required BlockPlan Block { get; init; }

    /// <summary>Source name node (GenericName keeps explicit type arguments).</summary>
    public required SimpleNameSyntax SourceName { get; init; }

    /// <summary>Receiver to forward as the first static-call argument.</summary>
    public required ExpressionSyntax Receiver { get; init; }

    public required SyntaxAnnotation Annotation { get; init; }
}

/// <summary>
/// Read site plan bound to a block property: <c>receiver.P</c> (member
/// access) OR a bare <c>P</c> inside the block (receiver synthesized).
/// </summary>
internal sealed record PropertyReadSitePlan
{
    /// <summary>Original site expression (MemberAccess or IdentifierName).</summary>
    public required ExpressionSyntax Site { get; init; }

    public required IPropertySymbol BoundSymbol { get; init; }

    public required PropertyMemberPlan Member { get; init; }

    public required BlockPlan Block { get; init; }

    /// <summary>Receiver to forward as the getter argument.</summary>
    public required ExpressionSyntax Receiver { get; init; }

    public required SyntaxAnnotation Annotation { get; init; }
}

/// <summary>Write site plan: <c>receiver.P = v</c> simple assignment bound to a block property.</summary>
internal sealed record PropertyWriteSitePlan
{
    public required AssignmentExpressionSyntax Assignment { get; init; }

    public required MemberAccessExpressionSyntax Target { get; init; }

    public required IPropertySymbol BoundSymbol { get; init; }

    public required PropertyMemberPlan Member { get; init; }

    public required BlockPlan Block { get; init; }

    public required SyntaxAnnotation Annotation { get; init; }
}

/// <summary>
/// Statement-level compound/inc-dec plan for a block property.
/// <see cref="CompoundKind"/> is set for compound assignments;
/// <see cref="UnaryKind"/> for ++/-- (only one).
/// </summary>
internal sealed record PropertyCompoundSitePlan
{
    public required ExpressionStatementSyntax Statement { get; init; }

    public required MemberAccessExpressionSyntax Target { get; init; }

    public required IPropertySymbol BoundSymbol { get; init; }

    public required PropertyMemberPlan Member { get; init; }

    public required BlockPlan Block { get; init; }

    public SyntaxKind CompoundKind { get; init; }

    public SyntaxKind UnaryKind { get; init; }

    public required bool HoistReceiver { get; init; }

    public string? ReceiverTemp { get; init; }

    public required SyntaxAnnotation StatementAnnotation { get; init; }
}

/// <summary>Seam node: property member-binding inside a planned conditional → invocation member-binding.</summary>
internal sealed record ConditionalSeamNode(
    MemberBindingExpressionSyntax Binding,
    InvocationExpressionSyntax Replacement,
    SyntaxAnnotation Annotation,
    ExtensionConditionalPlan Plan);

/// <summary>
/// A block-member call site deliberately left as sugar in M1 (paired
/// diagnostic): non-liftable null-conditional chains, expression-tree sites,
/// extended conditional shapes.
/// </summary>
internal sealed record DeferredSitePlan
{
    public required SyntaxNode Site { get; init; }

    public required bool IsProperty { get; init; }

    /// <summary>Diagnostic code paired with the retained sugar.</summary>
    public required string Code { get; init; }

    /// <summary>Diagnostic reason/detail.</summary>
    public required string Detail { get; init; }
}

/// <summary>Complete block analysis output for one syntax tree.</summary>
internal sealed record BlockAnalysisResult
{
    public required IReadOnlyList<BlockPlan> BlockPlans { get; init; }

    public required IReadOnlyList<MethodSitePlan> MethodSites { get; init; }

    public required IReadOnlyList<PropertyReadSitePlan> ReadSites { get; init; }

    public required IReadOnlyList<PropertyWriteSitePlan> WriteSites { get; init; }

    public required IReadOnlyList<PropertyCompoundSitePlan> CompoundSites { get; init; }

    /// <summary>Synthetic C19 conditional plans (block methods/properties), to lower with the classic plans.</summary>
    public required IReadOnlyList<ExtensionConditionalPlan> ConditionalPlans { get; init; }

    /// <summary>Property member-binding seam transforms for the synthetic conditional plans.</summary>
    public required IReadOnlyList<ConditionalSeamNode> ConditionalSeams { get; init; }

    /// <summary>Spine-call → explicit block-parent host name map for the synthetic conditional plans.</summary>
    public required IReadOnlyDictionary<ConditionalSpineCall, NameSyntax> ConditionalHostOverrides { get; init; }

    public required IReadOnlyList<DeferredSitePlan> DeferredSites { get; init; }

    public required List<ExtensionDiagnostic> Diagnostics { get; init; }

    public required ExtensionBlockStatistics Statistics { get; init; }
}
