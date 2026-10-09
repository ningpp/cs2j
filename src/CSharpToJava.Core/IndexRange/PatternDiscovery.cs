using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.IndexRange;

/// <summary>
/// M4 pattern discovery for sites the compiler bound to an explicit
/// <c>this[System.Index]</c>/<c>this[System.Range]</c> indexer (or to a
/// single-<c>Range</c>-argument method) instead of performing the built-in
/// pattern downgrade. Every member is resolved through the compiler's own
/// member/overload resolution (<see cref="SemanticModel.GetSpeculativeSymbolInfo"/>):
/// the synthesized expressions are bound at the site position, so
/// accessibility, hiding and extension methods are all honored; source name
/// text is never trusted.
/// </summary>
internal static class PatternDiscovery
{
    internal readonly record struct PatternMembers(
        IPropertySymbol? LengthProperty,
        IPropertySymbol? IntIndexer,
        IMethodSymbol? IntSlice)
    {
        public bool HasLength => LengthProperty != null;
    }

    /// <summary>
    /// Discovers the int-based pattern members (<c>Length</c>/<c>Count</c>,
    /// <c>this[int]</c>, <c>Slice(int, int)</c>) reachable on
    /// <paramref name="receiver"/> at <paramref name="site"/>. Members that
    /// cannot be bound stay null — callers must keep emitting IR1xx rather
    /// than guessing.
    /// </summary>
    internal static PatternMembers Discover(
        SyntaxNode site,
        ExpressionSyntax receiver,
        SemanticModel model)
    {
        var position = site.SpanStart;
        var detached = ParenthesizedExpression(receiver.WithoutTrivia());

        var length = BindProperty(model, position,
            SimpleMemberAccess(detached.WithoutTrivia(), "Length"))
            ?? BindProperty(model, position,
                SimpleMemberAccess(detached.WithoutTrivia(), "Count"));

        var intIndexer = BindIndexer(model, position, detached.WithoutTrivia());
        var intSlice = BindIntSlice(model, position, detached.WithoutTrivia());

        return new PatternMembers(length, intIndexer, intSlice);
    }

    /// <summary>
    /// Binds a synthetic <c>receiver.MethodName(0, 0)</c> invocation, used to
    /// discover the two-int overload of a range-argument method (instance or
    /// reduced extension — speculative binding runs full overload resolution).
    /// </summary>
    internal static IMethodSymbol? BindIntOverload(
        SyntaxNode site,
        ExpressionSyntax receiver,
        string methodName,
        SemanticModel model)
    {
        var position = site.SpanStart;
        var invocation = InvocationExpression(
            SimpleMemberAccess(
                ParenthesizedExpression(receiver.WithoutTrivia()),
                methodName),
            BuildArgumentList(ZeroLiteral(), ZeroLiteral()));
        return BindMethod(model, position, invocation, expectedParameters: 2);
    }

    /// <summary>
    /// Binds a synthetic static-form extension invocation
    /// <c>ExtType.MethodName(receiver, 0, 0)</c>.
    /// </summary>
    internal static IMethodSymbol? BindStaticIntOverload(
        SyntaxNode site,
        ExpressionSyntax typeExpression,
        ExpressionSyntax receiver,
        string methodName,
        SemanticModel model)
    {
        var position = site.SpanStart;
        var invocation = InvocationExpression(
            SimpleMemberAccess(typeExpression.WithoutTrivia(), methodName),
            BuildArgumentList(
                ParenthesizedExpression(receiver.WithoutTrivia()),
                ZeroLiteral(), ZeroLiteral()));
        return BindMethod(model, position, invocation, expectedParameters: 3);
    }

    private static IPropertySymbol? BindProperty(
        SemanticModel model, int position, ExpressionSyntax expression)
    {
        if (!TryBind(model, position, expression, out var symbol))
            return null;

        return symbol as IPropertySymbol;
    }

    private static IPropertySymbol? BindIndexer(
        SemanticModel model, int position, ExpressionSyntax receiver)
    {
        var access = ElementAccessExpression(
            receiver,
            BracketedArgumentList(
                SingletonSeparatedList(Argument(ZeroLiteral()))));
        if (!TryBind(model, position, access, out var symbol))
            return null;

        if (symbol is not IPropertySymbol property || !property.IsIndexer)
            return null;

        return property.Parameters.Length == 1
               && property.Parameters[0].Type.SpecialType
                   == SpecialType.System_Int32
            ? property
            : null;
    }

    private static IMethodSymbol? BindIntSlice(
        SemanticModel model, int position, ExpressionSyntax receiver)
    {
        var invocation = InvocationExpression(
            SimpleMemberAccess(receiver, "Slice"),
            BuildArgumentList(ZeroLiteral(), ZeroLiteral()));
        return BindMethod(model, position, invocation, expectedParameters: 2);
    }

    private static MemberAccessExpressionSyntax SimpleMemberAccess(
        ExpressionSyntax expression, string memberName)
        => MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            expression,
            IdentifierName(memberName));

    private static LiteralExpressionSyntax ZeroLiteral()
        => LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(0));

    private static IMethodSymbol? BindMethod(
        SemanticModel model,
        int position,
        ExpressionSyntax expression,
        int expectedParameters)
    {
        if (!TryBind(model, position, expression, out var symbol))
            return null;

        if (symbol is not IMethodSymbol method
            || method.Parameters.Length != expectedParameters)
            return null;

        // The synthesized literal arguments only bind when every parameter is
        // int; require that explicitly so a same-arity overload taking other
        // parameter types is never mistaken for the int pattern.
        for (var i = expectedParameters - 2; i < method.Parameters.Length; i++)
        {
            if (method.Parameters[i].Type.SpecialType != SpecialType.System_Int32)
                return null;
        }

        return method;
    }

    private static bool TryBind(
        SemanticModel model,
        int position,
        ExpressionSyntax expression,
        out ISymbol? symbol)
    {
        symbol = null;
        try
        {
            var info = model.GetSpeculativeSymbolInfo(
                position, expression, SpeculativeBindingOption.BindAsExpression);
            symbol = info.Symbol;
        }
        catch (ArgumentException)
        {
            // Position outside a scope supporting speculative binding
            // (e.g. attribute argument): the site stays unrewritten.
            return false;
        }

        return symbol != null;
    }

    private static ArgumentListSyntax BuildArgumentList(
        params ExpressionSyntax[] arguments)
        => ArgumentList(SeparatedList(arguments.Select(Argument)));
}
