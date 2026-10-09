using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.ExpressionDesugar.CollectionExpr;

namespace CSharpToJava.Core.DefaultParameterStripper.ParamsCollections;

/// <summary>
/// Emits the explicit collection-construction expressions that replace
/// compiler-synthesized params expansion arguments (design §2.1 matrix, T8).
///
/// Everything emitted here is an ordinary, immediately-compilable expression
/// with zero temporaries (params argument positions never need statement
/// form): array creations with an initializer, <c>global::System.Array.Empty&lt;T&gt;()</c>
/// invocations and object creations (optionally with a collection initializer).
///
/// Element syntax always comes from the source ArgumentList (the synthesized
/// IOperation Value has no syntax of its own — M0 probe Q3/R8); this emitter
/// only receives already-extracted element expressions.
///
/// Empty-state shapes follow M0 adjudication 5 (probe Q4/Q13): kinds whose
/// empty lowering is the compiler's Array.Empty singleton (K1 array / K2 span
/// / K5 interface) emit <c>global::System.Array.Empty&lt;T&gt;()</c>; K4
/// initializable concrete types get a fresh instance, matching the compiler.
/// </summary>
internal static class CollectionConstructionEmitter
{
    /// <summary>
    /// Renders the element type with the fully qualified format so the emitted
    /// expression compiles without any using directive (same discipline as the
    /// E08 DefaultParameterValue attribute).
    /// </summary>
    public static string TypeName(ITypeSymbol type)
        => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    /// <summary>
    /// Form A (comma expansion): builds the collection construction carrying
    /// every expansion element. K1/K2 → <c>new T[]{ e1, e2 }</c>; K5 →
    /// <c>new global::System.Collections.Generic.List&lt;T&gt;(){ e1, e2 }</c>;
    /// K4 → <c>new C&lt;T&gt;(){ e1, e2 }</c>.
    /// </summary>
    public static ExpressionSyntax EmitExpansion(
        ParamsTargetClassification classification,
        IReadOnlyList<ExpressionSyntax> elements)
    {
        return classification.Kind switch
        {
            ParamsParamKind.Array or ParamsParamKind.Span
                => NewArray(classification.ElementType!, elements),
            ParamsParamKind.Interface or ParamsParamKind.Initializable
                => NewCollection(classification.CanonicalConcreteType!, elements),
            _
                => throw new InvalidOperationException(
                    $"params kind '{classification.Kind}' has no expansion emission (site must be skipped)"),
        };
    }

    /// <summary>
    /// Form C (empty argument list): the compiler-shaped empty collection per
    /// adjudication 5 — <c>global::System.Array.Empty&lt;T&gt;()</c> for
    /// ArrayEmpty kinds, <c>new C&lt;T&gt;()</c> for NewInstance kinds. K3
    /// builder kinds go through <see cref="EmitBuilderFactory"/> (the empty
    /// shape is <c>B.Create(new T[]{})</c>).
    /// </summary>
    public static ExpressionSyntax EmitEmpty(ParamsTargetClassification classification)
        => classification.EmptyState == ParamsEmptyStateShape.ArrayEmpty
            ? ArrayEmpty(classification.ElementType!)
            : NewInstance(classification.CanonicalConcreteType!);

    /// <summary>
    /// K3 (M2 PC-9): <c>global::B.Create&lt;...&gt;(new TElem[]{ e1, e2 })</c>;
    /// an empty site passes an empty element list and emits
    /// <c>global::B.Create&lt;...&gt;(new TElem[]{})</c> (probe §13: the
    /// compiler binds the same factory for the empty form). The factory symbol
    /// is either the compiler-bound <c>ConstructMethod</c> or the shared C36
    /// CE-10 analyzer result, so generic arity is already constructed and
    /// rendered explicitly (e.g. <c>Imm.Create&lt;string&gt;(…)</c>).
    /// </summary>
    public static ExpressionSyntax EmitBuilderFactory(
        IMethodSymbol factory,
        ITypeSymbol elementType,
        IReadOnlyList<ExpressionSyntax> elements)
        => CollectionExprEmitter.BuildBuilderFactory(factory, elementType, elements);

    /// <summary><c>global::System.Array.Empty&lt;T&gt;()</c>.</summary>
    public static ExpressionSyntax ArrayEmpty(ITypeSymbol elementType)
        => SyntaxFactory.ParseExpression(
            $"global::System.Array.Empty<{TypeName(elementType)}>()");

    /// <summary><c>new C()</c> — fresh instance of the concrete collection type.</summary>
    public static ExpressionSyntax NewInstance(INamedTypeSymbol concreteType)
        => SyntaxFactory.ObjectCreationExpression(
            SyntaxFactory.ParseTypeName(TypeName(concreteType)),
            SyntaxFactory.ArgumentList(),
            initializer: null);

    /// <summary><c>new T[]{ e1, e2 }</c> — plain array creation (K2 relies on the
    /// existing T[] → Span/ReadOnlySpan implicit reference conversion; no AsSpan).</summary>
    public static ExpressionSyntax NewArray(ITypeSymbol elementType, IReadOnlyList<ExpressionSyntax> elements)
    {
        var arrayType = SyntaxFactory.ArrayType(
            SyntaxFactory.ParseTypeName(TypeName(elementType)),
            SyntaxFactory.SingletonList(SyntaxFactory.ArrayRankSpecifier()));
        var initializer = SyntaxFactory.InitializerExpression(
            SyntaxKind.ArrayInitializerExpression,
            SyntaxFactory.SeparatedList(elements));
        return SyntaxFactory.ArrayCreationExpression(arrayType, initializer);
    }

    /// <summary><c>new C&lt;T&gt;(){ e1, e2 }</c> — object creation with a collection
    /// initializer (each element = one Add call at runtime, semantics preserved).</summary>
    public static ExpressionSyntax NewCollection(INamedTypeSymbol concreteType, IReadOnlyList<ExpressionSyntax> elements)
        => SyntaxFactory.ObjectCreationExpression(
            SyntaxFactory.ParseTypeName(TypeName(concreteType)),
            SyntaxFactory.ArgumentList(),
            SyntaxFactory.InitializerExpression(
                SyntaxKind.CollectionInitializerExpression,
                SyntaxFactory.SeparatedList(elements)));
}
