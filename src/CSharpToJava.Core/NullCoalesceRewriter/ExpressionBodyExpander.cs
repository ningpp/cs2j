using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.NullCoalesceRewriter;

/// <summary>
/// Converts expression-bodied ('=>') members whose body contains a ?? or ??=
/// operator into semantically equivalent block bodies, so the single-pass
/// null-coalesce rewriter gets a statement slot in which to land hoisted temp
/// declarations / if-guards:
///   value-yielding body:  'P => e;'  ->  'P { get { return e; } }'
///   statement body:       'M() => E;' ->  'M() { E; }'
/// Only bodies actually containing a coalesce operator are touched; clean
/// arrow members stay byte-identical. Guards inside an expanded lambda/local
/// function land inside that body and therefore keep invocation-time
/// semantics. Field/property/variable initializers have no host block and are
/// intentionally left untouched (they stay residual by design).
/// </summary>
internal sealed class ExpressionBodyExpander : CSharpSyntaxRewriter
{
    private readonly SemanticModel? _model;

    public ExpressionBodyExpander(SemanticModel? model) => _model = model;

    /// <summary>Number of expression bodies converted to block bodies.</summary>
    public int Expansions { get; private set; }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var yieldsValue = MethodYieldsValue(node, node.ReturnType);
        var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
        return visited.ExpressionBody is { } clause && ContainsCoalesce(clause.Expression)
            ? visited.WithExpressionBody(null).WithSemicolonToken(default)
                .WithBody(MakeBlock(clause, yieldsValue, visited.SemicolonToken))
                .WithAdditionalAnnotations(ExpansionMarker)
            : visited;
    }

    public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        var visited = (ConstructorDeclarationSyntax)base.VisitConstructorDeclaration(node)!;
        return visited.ExpressionBody is { } clause && ContainsCoalesce(clause.Expression)
            ? visited.WithExpressionBody(null).WithSemicolonToken(default)
                .WithBody(MakeBlock(clause, yieldsValue: false, visited.SemicolonToken))
                .WithAdditionalAnnotations(ExpansionMarker)
            : visited;
    }

    public override SyntaxNode? VisitDestructorDeclaration(DestructorDeclarationSyntax node)
    {
        var visited = (DestructorDeclarationSyntax)base.VisitDestructorDeclaration(node)!;
        return visited.ExpressionBody is { } clause && ContainsCoalesce(clause.Expression)
            ? visited.WithExpressionBody(null).WithSemicolonToken(default)
                .WithBody(MakeBlock(clause, yieldsValue: false, visited.SemicolonToken))
                .WithAdditionalAnnotations(ExpansionMarker)
            : visited;
    }

    public override SyntaxNode? VisitOperatorDeclaration(OperatorDeclarationSyntax node)
    {
        var visited = (OperatorDeclarationSyntax)base.VisitOperatorDeclaration(node)!;
        return visited.ExpressionBody is { } clause && ContainsCoalesce(clause.Expression)
            ? visited.WithExpressionBody(null).WithSemicolonToken(default)
                .WithBody(MakeBlock(clause, yieldsValue: true, visited.SemicolonToken))
                .WithAdditionalAnnotations(ExpansionMarker)
            : visited;
    }

    public override SyntaxNode? VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node)
    {
        var visited = (ConversionOperatorDeclarationSyntax)base.VisitConversionOperatorDeclaration(node)!;
        return visited.ExpressionBody is { } clause && ContainsCoalesce(clause.Expression)
            ? visited.WithExpressionBody(null).WithSemicolonToken(default)
                .WithBody(MakeBlock(clause, yieldsValue: true, visited.SemicolonToken))
                .WithAdditionalAnnotations(ExpansionMarker)
            : visited;
    }

    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        var yieldsValue = MethodYieldsValue(node, node.ReturnType);
        var visited = (LocalFunctionStatementSyntax)base.VisitLocalFunctionStatement(node)!;
        return visited.ExpressionBody is { } clause && ContainsCoalesce(clause.Expression)
            ? visited.WithExpressionBody(null).WithSemicolonToken(default)
                .WithBody(MakeBlock(clause, yieldsValue, visited.SemicolonToken))
                .WithAdditionalAnnotations(ExpansionMarker)
            : visited;
    }

    public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        var visited = (PropertyDeclarationSyntax)base.VisitPropertyDeclaration(node)!;
        if (visited.ExpressionBody is not { } clause || !ContainsCoalesce(clause.Expression))
        {
            return visited;
        }

        var getter = SyntaxFactory.AccessorDeclaration(
                SyntaxKind.GetAccessorDeclaration,
                MakeBlock(clause, yieldsValue: true, visited.SemicolonToken))
            .WithAdditionalAnnotations(ExpansionMarker);
        var accessorList = SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(getter))
            .WithAdditionalAnnotations(ExpansionMarker);
        return visited.WithExpressionBody(null).WithSemicolonToken(default)
            .WithAccessorList(accessorList).WithAdditionalAnnotations(ExpansionMarker);
    }

    public override SyntaxNode? VisitIndexerDeclaration(IndexerDeclarationSyntax node)
    {
        var visited = (IndexerDeclarationSyntax)base.VisitIndexerDeclaration(node)!;
        if (visited.ExpressionBody is not { } clause || !ContainsCoalesce(clause.Expression))
        {
            return visited;
        }

        var getter = SyntaxFactory.AccessorDeclaration(
                SyntaxKind.GetAccessorDeclaration,
                MakeBlock(clause, yieldsValue: true, visited.SemicolonToken))
            .WithAdditionalAnnotations(ExpansionMarker);
        var accessorList = SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(getter))
            .WithAdditionalAnnotations(ExpansionMarker);
        return visited.WithExpressionBody(null).WithSemicolonToken(default)
            .WithAccessorList(accessorList).WithAdditionalAnnotations(ExpansionMarker);
    }

    public override SyntaxNode? VisitAccessorDeclaration(AccessorDeclarationSyntax node)
    {
        var visited = (AccessorDeclarationSyntax)base.VisitAccessorDeclaration(node)!;
        if (visited.ExpressionBody is not { } clause || !ContainsCoalesce(clause.Expression))
        {
            return visited;
        }

        // get -> 'return e;'; set/init/add/remove -> 'e;'
        var yieldsValue = visited.IsKind(SyntaxKind.GetAccessorDeclaration);
        return visited.WithExpressionBody(null).WithSemicolonToken(default)
            .WithBody(MakeBlock(clause, yieldsValue, visited.SemicolonToken))
            .WithAdditionalAnnotations(ExpansionMarker);
    }

    public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
    {
        var yieldsValue = LambdaYieldsValue(node);
        var visited = (ParenthesizedLambdaExpressionSyntax)base.VisitParenthesizedLambdaExpression(node)!;
        return visited.ExpressionBody is { } body && ContainsCoalesce(body)
            ? visited.WithBlock(MakeLambdaBlock(body, yieldsValue)).WithExpressionBody(null)
            : visited;
    }

    public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
    {
        var yieldsValue = LambdaYieldsValue(node);
        var visited = (SimpleLambdaExpressionSyntax)base.VisitSimpleLambdaExpression(node)!;
        return visited.ExpressionBody is { } body && ContainsCoalesce(body)
            ? visited.WithBlock(MakeLambdaBlock(body, yieldsValue)).WithExpressionBody(null)
            : visited;
    }

    private BlockSyntax MakeBlock(
        ArrowExpressionClauseSyntax clause,
        bool yieldsValue,
        SyntaxToken bodySemicolon)
    {
        Expansions++;
        return SyntaxFactory.Block(BuildBodyStatement(clause.Expression, yieldsValue, bodySemicolon))
            .WithAdditionalAnnotations(ExpansionMarker);
    }

    private BlockSyntax MakeLambdaBlock(ExpressionSyntax body, bool yieldsValue)
    {
        Expansions++;
        return SyntaxFactory.Block(BuildBodyStatement(body, yieldsValue, default))
            .WithAdditionalAnnotations(ExpansionMarker);
    }

    private static StatementSyntax BuildBodyStatement(
        ExpressionSyntax expression,
        bool yieldsValue,
        SyntaxToken bodySemicolon)
    {
        StatementSyntax statement = yieldsValue
            ? SyntaxFactory.ReturnStatement(expression)
            : SyntaxFactory.ExpressionStatement(expression);

        // Preserve trailing comments attached to the original arrow-body ';'.
        var commentTrivia = bodySemicolon.TrailingTrivia
            .Where(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia)
                        || t.IsKind(SyntaxKind.MultiLineCommentTrivia))
            .ToList();
        if (commentTrivia.Count > 0)
        {
            var annotatedSemicolon = SyntaxFactory.Token(SyntaxKind.SemicolonToken)
                .WithTrailingTrivia(
                    SyntaxFactory.TriviaList(
                        commentTrivia.Concat(new[] { SyntaxFactory.CarriageReturnLineFeed })));
            statement = statement switch
            {
                ReturnStatementSyntax returnStatement =>
                    returnStatement.WithSemicolonToken(annotatedSemicolon),
                ExpressionStatementSyntax expressionStatement =>
                    expressionStatement.WithSemicolonToken(annotatedSemicolon),
                _ => statement,
            };
        }

        return statement.WithAdditionalAnnotations(Formatter.Annotation);
    }

    private static bool ContainsCoalesce(ExpressionSyntax? expression)
        => expression != null
           && expression.DescendantNodesAndSelf().Any(node =>
               node is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.CoalesceExpression)
               || node is AssignmentExpressionSyntax assignment
                   && assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression));

    /// <summary>
    /// Value-yielding methods/local functions wrap the body in 'return'.
    /// Semantic model first (handles 'void' aliases/records); syntactic
    /// 'void' keyword fallback when no model is available.
    /// </summary>
    private bool MethodYieldsValue(SyntaxNode declaration, TypeSyntax? returnType)
    {
        if (_model?.GetDeclaredSymbol(declaration) is IMethodSymbol method)
        {
            return !method.ReturnsVoid;
        }

        return returnType is not PredefinedTypeSyntax predefined
               || !predefined.Keyword.IsKind(SyntaxKind.VoidKeyword);
    }

    /// <summary>
    /// Lambdas carry no syntactic return type: derive void-ness from the
    /// converted delegate type's Invoke method. Without a model assume a value
    /// is yielded (the coalesce rewriter itself cannot run without one).
    /// </summary>
    private bool LambdaYieldsValue(LambdaExpressionSyntax lambda)
    {
        if (_model?.GetTypeInfo(lambda).ConvertedType is INamedTypeSymbol converted
            && converted.DelegateInvokeMethod is { } invoke)
        {
            return invoke.ReturnType.SpecialType != SpecialType.System_Void;
        }

        return true;
    }

    private static readonly SyntaxAnnotation ExpansionMarker = Formatter.Annotation;
}
