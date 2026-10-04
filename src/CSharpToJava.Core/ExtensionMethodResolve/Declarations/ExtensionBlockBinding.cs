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

    /// <summary>
    /// True when at least one site of this block is deferred (sugar
    /// retained): the block declaration and every one of its sites must be
    /// left untouched, or the retained sugar would no longer resolve.
    /// </summary>
    public bool HasDeferredSites { get; set; }
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
/// EMD-OP member plan (M2): a binary/unary/checked operator declared in a
/// block (<see cref="MethodKind.UserDefinedOperator"/>; conversion operators
/// are CS9282 and never reach a plan). <see cref="GeneratedName"/> is the
/// strict C14 name (<c>Add</c>/<c>Negate</c>/<c>AddChecked</c>…).
/// </summary>
internal sealed record OperatorMemberPlan : MemberPlan
{
    public required IMethodSymbol Symbol { get; init; }

    public required OperatorDeclarationSyntax OperatorDeclaration { get; init; }

    public required string GeneratedName { get; init; }
}

/// <summary>EMD-STATIC member plan (M2): a static method declared in a block.</summary>
internal sealed record StaticMethodMemberPlan : MemberPlan
{
    public required IMethodSymbol Symbol { get; init; }

    public required IReadOnlyList<TypeParameterSyntax> MemberTypeParameters { get; init; }

    public required IReadOnlyList<TypeParameterConstraintClauseSyntax> MemberConstraints { get; init; }
}

/// <summary>EMD-STATIC member plan (M2): a static property declared in a block.</summary>
internal sealed record StaticPropertyMemberPlan : MemberPlan
{
    public required IPropertySymbol Symbol { get; init; }

    public required PropertyDeclarationSyntax PropertyDeclaration { get; init; }

    public bool HasGet { get; init; }

    public bool HasSet { get; init; }
}

/// <summary>Marker interface for site plans bound to a block.</summary>
internal interface IBlockSitePlan
{
    BlockPlan Block { get; }
}

/// <summary>
/// Call site plan for a block method: <c>receiver.M(args)</c> (receiver
/// node from the source member access) OR a bare <c>M(args)</c> inside the
/// block (receiver synthesized as the block receiver).
/// </summary>
internal sealed record MethodSitePlan : IBlockSitePlan
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
internal sealed record PropertyReadSitePlan : IBlockSitePlan
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
internal sealed record PropertyWriteSitePlan : IBlockSitePlan
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
internal sealed record PropertyCompoundSitePlan : IBlockSitePlan
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

/// <summary>
/// EMD-OP binary site (M2): <c>a + b</c> bound to a block operator
/// (including the lifted <c>Vec? + Vec?</c> shape and checked variants;
/// short-circuit &amp;&amp;/|| sites are not planned).
/// </summary>
internal sealed record BinaryOperatorSitePlan : IBlockSitePlan
{
    public required BinaryExpressionSyntax Expression { get; init; }

    public required IMethodSymbol BoundSymbol { get; init; }

    public required OperatorMemberPlan Member { get; init; }

    public required BlockPlan Block { get; init; }

    public required bool IsLifted { get; init; }

    /// <summary>Syntax-level nullability of the left operand (analysis-time, original model).</summary>
    public required bool LeftNullable { get; init; }

    /// <summary>Syntax-level nullability of the right operand (analysis-time, original model).</summary>
    public required bool RightNullable { get; init; }

    /// <summary>Minimal display of the site result type at the original site span.</summary>
    public required string ResultTypeDisplay { get; init; }

    public required SyntaxAnnotation Annotation { get; init; }
}

/// <summary>EMD-OP unary site (M2): <c>-a</c> etc. bound to a block operator.</summary>
internal sealed record UnaryOperatorSitePlan : IBlockSitePlan
{
    public required ExpressionSyntax Expression { get; init; }

    public required IMethodSymbol BoundSymbol { get; init; }

    public required OperatorMemberPlan Member { get; init; }

    public required BlockPlan Block { get; init; }

    public required bool IsLifted { get; init; }

    /// <summary>Syntax-level nullability of the operand (analysis-time, original model).</summary>
    public required bool OperandNullable { get; init; }

    /// <summary>Minimal display of the site result type at the original site span.</summary>
    public required string ResultTypeDisplay { get; init; }

    public required SyntaxAnnotation Annotation { get; init; }
}

/// <summary>
/// EMD-OP compound site (M2): <c>a += b</c> whose
/// <c>ICompoundAssignmentOperation.OperatorMethod</c> is a block operator.
/// Rewriting yields <c>a = global::Host.Op(a, b)</c> (lifted: the C14 lifted
/// compound formula); complex lvalues at statement positions are hoisted.
/// </summary>
internal sealed record OperatorCompoundSitePlan : IBlockSitePlan
{
    public required AssignmentExpressionSyntax Assignment { get; init; }

    public required IMethodSymbol BoundSymbol { get; init; }

    public required OperatorMemberPlan Member { get; init; }

    public required BlockPlan Block { get; init; }

    public required bool IsLifted { get; init; }

    /// <summary>True when the left side can be inlined without double-evaluation; false → statement hoist.</summary>
    public required bool SimpleLvalue { get; init; }

    /// <summary>Syntax-level nullability of the compound left operand (analysis-time, original model).</summary>
    public required bool LeftNullable { get; init; }

    /// <summary>Syntax-level nullability of the compound right operand (analysis-time, original model).</summary>
    public required bool RightNullable { get; init; }

    /// <summary>Minimal display of the compound result type at the original site span.</summary>
    public required string ResultTypeDisplay { get; init; }

    public required SyntaxAnnotation Annotation { get; init; }
}

/// <summary>
/// EMD-STATIC method site (M2): a receiver-type-name qualified call
/// <c>TypeExpr.M(args)</c> bound to a block static method. The target name is
/// the source GenericName (arity n+m as written) or, for omitted source
/// arguments, a synthesized GenericName carrying the n explicit block
/// arguments (CS0411 boundary).
/// </summary>
internal sealed record StaticMethodSitePlan : IBlockSitePlan
{
    public required InvocationExpressionSyntax Invocation { get; init; }

    public required IMethodSymbol BoundSymbol { get; init; }

    public required StaticMethodMemberPlan Member { get; init; }

    public required BlockPlan Block { get; init; }

    /// <summary>Source name node: GenericName (n or n+m explicit args) or IdentifierName (omitted).</summary>
    public required SimpleNameSyntax SourceName { get; init; }

    /// <summary>Block type arguments (n), from the constructed extension container TypeArguments.</summary>
    public required IReadOnlyList<ITypeSymbol> BlockTypeArguments { get; init; }

    public required SyntaxAnnotation Annotation { get; init; }
}

/// <summary>EMD-STATIC property read site (M2): <c>TypeExpr.P</c> bound to a block static property.</summary>
internal sealed record StaticPropertyReadSitePlan : IBlockSitePlan
{
    public required MemberAccessExpressionSyntax Access { get; init; }

    public required IPropertySymbol BoundSymbol { get; init; }

    public required StaticPropertyMemberPlan Member { get; init; }

    public required BlockPlan Block { get; init; }

    /// <summary>Block type arguments (n) for the explicit getter type-argument list.</summary>
    public required IReadOnlyList<ITypeSymbol> BlockTypeArguments { get; init; }

    public required SyntaxAnnotation Annotation { get; init; }
}

/// <summary>EMD-STATIC property write site (M2): <c>TypeExpr.P = v</c> bound to a block static property.</summary>
internal sealed record StaticPropertyWriteSitePlan : IBlockSitePlan
{
    public required AssignmentExpressionSyntax Assignment { get; init; }

    public required MemberAccessExpressionSyntax Target { get; init; }

    public required IPropertySymbol BoundSymbol { get; init; }

    public required StaticPropertyMemberPlan Member { get; init; }

    public required BlockPlan Block { get; init; }

    /// <summary>Block type arguments (n) for the explicit setter type-argument list.</summary>
    public required IReadOnlyList<ITypeSymbol> BlockTypeArguments { get; init; }

    public required SyntaxAnnotation Annotation { get; init; }
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

    /// <summary>EMD-OP binary operator sites (M2).</summary>
    public required IReadOnlyList<BinaryOperatorSitePlan> BinaryOperatorSites { get; init; }

    /// <summary>EMD-OP unary operator sites (M2).</summary>
    public required IReadOnlyList<UnaryOperatorSitePlan> UnaryOperatorSites { get; init; }

    /// <summary>EMD-OP compound-assignment operator sites (M2).</summary>
    public required IReadOnlyList<OperatorCompoundSitePlan> OperatorCompoundSites { get; init; }

    /// <summary>EMD-STATIC type-name qualified method sites (M2).</summary>
    public required IReadOnlyList<StaticMethodSitePlan> StaticMethodSites { get; init; }

    /// <summary>EMD-STATIC type-name qualified static property read sites (M2).</summary>
    public required IReadOnlyList<StaticPropertyReadSitePlan> StaticPropertyReadSites { get; init; }

    /// <summary>EMD-STATIC type-name qualified static property write sites (M2).</summary>
    public required IReadOnlyList<StaticPropertyWriteSitePlan> StaticPropertyWriteSites { get; init; }

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
