using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ForeachSource;

/// <summary>
/// Syntax construction helpers shared by the foreach-source rules:
/// ToCharArray() wrapping, fresh synthetic identifier allocation with
/// method/block-scope collision scanning (ported from E07), block wrapping
/// and trivia preservation.
/// </summary>
internal static class ForeachSourceSyntaxFacts
{
    public const string EntryName = "__cs2jFsEntry";
    public const string InnerName = "__cs2jFsInner";
    public const string CharTempName = "__cs2jFsCh";

    // C40 synthetic temporary base names (design §4.1; distinct from C16/E07).
    public const string FaSourceName = "__cs2jFaSrc";
    public const string FaEnumeratorName = "__cs2jFaE";
    public const string FrSourceName = "__cs2jFrSrc";
    public const string FrIndexName = "__cs2jFrI";
    public const string FrCurrentName = "__cs2jFrCur";

    /// <summary>
    /// Build <c>expression.ToCharArray()</c> without any elastic trivia: the
    /// invocation slots into the existing foreach <c>in</c> clause, so all
    /// surrounding whitespace stays untouched. Expressions below primary
    /// precedence (e.g. <c>"a" + s</c>) are parenthesized first — otherwise
    /// the emitted text would re-parse as <c>"a" + s.ToCharArray()</c>.
    /// </summary>
    public static InvocationExpressionSyntax ToCharArrayInvocation(ExpressionSyntax expression)
    {
        var receiver = IsPrimaryReceiver(expression)
            ? expression
            : SyntaxFactory.ParenthesizedExpression(expression.WithoutTrivia())
                .WithTriviaFrom(expression);

        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                receiver,
                SyntaxFactory.Token(SyntaxKind.DotToken),
                SyntaxFactory.IdentifierName(nameof(string.ToCharArray))),
            SyntaxFactory.ArgumentList());
    }

    /// <summary>
    /// Kinds that bind at primary precedence and therefore need no wrapping
    /// before <c>.ToCharArray()</c>.
    /// </summary>
    private static bool IsPrimaryReceiver(ExpressionSyntax expression)
        => expression.Kind() is SyntaxKind.IdentifierName
            or SyntaxKind.GenericName
            or SyntaxKind.MemberBindingExpression // cannot appear here, harmless
            or SyntaxKind.SimpleMemberAccessExpression
            or SyntaxKind.PointerMemberAccessExpression
            or SyntaxKind.InvocationExpression
            or SyntaxKind.ElementAccessExpression
            or SyntaxKind.InterpolatedStringExpression
            or SyntaxKind.ThisExpression
            or SyntaxKind.BaseExpression
            or SyntaxKind.ParenthesizedExpression
            or SyntaxKind.ConditionalAccessExpression
            or SyntaxKind.ObjectCreationExpression
            or SyntaxKind.ImplicitObjectCreationExpression
            or SyntaxKind.ArrayCreationExpression
            or SyntaxKind.ImplicitArrayCreationExpression
            or SyntaxKind.StackAllocArrayCreationExpression
            or SyntaxKind.DefaultExpression
            or SyntaxKind.TypeOfExpression
            or SyntaxKind.SizeOfExpression
            or SyntaxKind.CheckedExpression
            or SyntaxKind.AnonymousObjectCreationExpression
            or SyntaxKind.CharacterLiteralExpression
            or SyntaxKind.StringLiteralExpression
            or SyntaxKind.TrueLiteralExpression
            or SyntaxKind.FalseLiteralExpression
            or SyntaxKind.NullLiteralExpression;

    /// <summary>
    /// Prepend statements to a loop body; wrap a non-block body (single
    /// statement) in a block, ported verbatim in spirit from E07's
    /// PrependStatements.
    /// </summary>
    public static StatementSyntax PrependStatements(StatementSyntax body, IReadOnlyList<StatementSyntax> prepend)
    {
        if (body is BlockSyntax block)
        {
            return block.WithStatements(
                SyntaxFactory.List(prepend.Concat(block.Statements)));
        }

        return SyntaxFactory.Block(
            SyntaxFactory.List(prepend.Concat(new[] { body })));
    }

    /// <summary>
    /// Allocate a fresh identifier derived from <paramref name="baseName"/>
    /// that does not collide with any identifier, local declarator or
    /// parameter visible in the enclosing method/constructor/block scope.
    /// The first call for a base returns it unchanged; later calls get a
    /// numeric suffix.
    /// </summary>
    public static string FreshName(
        string baseName,
        SyntaxNode scopeNode,
        Dictionary<string, int> counters)
    {
        SyntaxNode? scope = scopeNode.FirstAncestorOrSelf<MethodDeclarationSyntax>();
        scope ??= scopeNode.FirstAncestorOrSelf<ConstructorDeclarationSyntax>();
        scope ??= scopeNode.FirstAncestorOrSelf<LocalFunctionStatementSyntax>();
        scope ??= scopeNode.FirstAncestorOrSelf<BlockSyntax>();

        var existingNames = new HashSet<string>(StringComparer.Ordinal);
        if (scope != null)
        {
            foreach (var id in scope.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                existingNames.Add(id.Identifier.Text);
            }
            foreach (var decl in scope.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            {
                existingNames.Add(decl.Identifier.Text);
            }
            foreach (var param in scope.DescendantNodes().OfType<ParameterSyntax>())
            {
                existingNames.Add(param.Identifier.Text);
            }
            foreach (var single in scope.DescendantNodes().OfType<SingleVariableDesignationSyntax>())
            {
                existingNames.Add(single.Identifier.Text);
            }
        }

        var counter = counters.TryGetValue(baseName, out var c) ? c : 0;
        string name;
        do
        {
            name = counter == 0 ? baseName : baseName + counter;
            counter++;
        }
        while (existingNames.Contains(name));

        counters[baseName] = counter;
        return name;
    }
}
