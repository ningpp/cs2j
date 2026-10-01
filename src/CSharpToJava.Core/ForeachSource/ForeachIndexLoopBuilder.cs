using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ForeachSource;

/// <summary>
/// Shared index-loop synthesis for C40 (design §1.3 ②, §4.3 FA-5/FR-1/FR-2,
/// §4.4-4/5/6). FA-5 (M2) uses it with <see cref="RefKind.None"/> for plain
/// <c>Span&lt;T&gt;</c>/<c>ReadOnlySpan&lt;T&gt;</c> foreach; M3 passes
/// <see cref="RefKind.Ref"/>/<see cref="RefKind.RefReadOnly"/> and gets
/// <c>ref T item = ref src[i]</c> locals. The source is cached exactly once
/// (<c>var __cs2jFrSrc = …;</c>, hard invariant 6); Length/indexer member
/// names come from the resolved <see cref="ForeachBinding"/> symbols.
/// </summary>
internal static class ForeachIndexLoopBuilder
{
    /// <summary>
    /// Resolve the length member (instance public <c>Length</c> property/field,
    /// falling back to <c>Count</c>) and the single-<c>int</c>-argument
    /// indexer of an index-pattern collection. True only when both exist.
    /// </summary>
    public static bool TryFindIndexMembers(
        ITypeSymbol collectionType,
        out ISymbol? lengthMember,
        out IPropertySymbol? indexer)
    {
        lengthMember = null;
        indexer = null;

        foreach (var name in new[] { "Length", "Count" })
        {
            foreach (var member in collectionType.GetMembers(name))
            {
                if (member.IsStatic
                    || member.DeclaredAccessibility != Accessibility.Public
                    || member is not (IPropertySymbol or IFieldSymbol))
                {
                    continue;
                }

                lengthMember = member;
                break;
            }

            if (lengthMember != null)
            {
                break;
            }
        }

        if (lengthMember == null)
        {
            return false;
        }

        foreach (var member in collectionType.GetMembers())
        {
            if (member is not IPropertySymbol { IsIndexer: true, Parameters.Length: 1 } candidate
                || candidate.DeclaredAccessibility != Accessibility.Public
                || candidate.IsStatic)
            {
                continue;
            }

            if (candidate.Parameters[0].Type.SpecialType == SpecialType.System_Int32)
            {
                indexer = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Build the lowered block:
    /// <code>
    /// var __cs2jFrSrc = &lt;expr&gt;;
    /// for (int __cs2jFrI = 0; __cs2jFrI &lt; __cs2jFrSrc.Length; __cs2jFrI++)
    /// {
    ///     [ref [readonly]] T item = [ref ]__cs2jFrSrc[__cs2jFrI];   // cast when symbols differ
    ///     body
    /// }
    /// </code>
    /// Element declaration rules match FA (decision 7): plain <c>var</c> with
    /// matching indexer type stays <c>var</c> without a cast; an explicit
    /// element type or a symbol mismatch renders the fully qualified type and
    /// inserts the cast.
    /// </summary>
    public static BlockSyntax Build(
        ForEachStatementSyntax node,
        string sourceName,
        string indexName,
        ISymbol lengthMember,
        RefKind elementRefKind,
        ITypeSymbol? elementType,
        bool needsCast)
    {
        var sourceAccess = SyntaxFactory.IdentifierName(sourceName);
        var indexAccessName = SyntaxFactory.IdentifierName(indexName);

        // Invariant 6: source evaluated exactly once; Length and every index
        // access below go through the cached local.
        var sourceDecl = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(sourceName)
                        .WithInitializer(SyntaxFactory.EqualsValueClause(node.Expression)))));

        var elementAccess = SyntaxFactory.ElementAccessExpression(sourceAccess)
            .WithArgumentList(
                SyntaxFactory.BracketedArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(indexAccessName))));

        var itemIdentifier = node.Identifier.WithoutTrivia();
        var isVar = node.Type is IdentifierNameSyntax { Identifier.Text: "var" };

        TypeSyntax declaredTypeSyntax;
        ExpressionSyntax itemValue = elementAccess;
        if (elementRefKind == RefKind.None && isVar && !needsCast)
        {
            declaredTypeSyntax = SyntaxFactory.IdentifierName("var");
        }
        else
        {
            var rendered = elementType!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            declaredTypeSyntax = SyntaxFactory.ParseTypeName(rendered);
            if (needsCast)
            {
                itemValue = SyntaxFactory.CastExpression(
                    declaredTypeSyntax.WithoutTrivia(),
                    elementAccess);
            }
        }

        // ref / ref readonly locals need an explicit type (var is illegal in a
        // ref local declaration) and a ref expression initializer.
        if (elementRefKind is RefKind.Ref or RefKind.RefReadOnly)
        {
            var refType = SyntaxFactory.RefType(
                elementRefKind == RefKind.RefReadOnly
                    ? SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)
                    : default,
                SyntaxFactory.Token(SyntaxKind.RefKeyword),
                declaredTypeSyntax);
            itemValue = SyntaxFactory.RefExpression(
                SyntaxFactory.Token(SyntaxKind.RefKeyword),
                elementAccess);

            var refItemDecl = SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(refType)
                    .WithVariables(SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator(itemIdentifier)
                            .WithInitializer(SyntaxFactory.EqualsValueClause(itemValue)))));

            var refBody = ForeachSourceSyntaxFacts.PrependStatements(
                node.Statement, new[] { refItemDecl });
            return AssembleBlock(node, sourceDecl, BuildFor(
                indexName, sourceAccess, lengthMember, refBody));
        }

        var itemDecl = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(declaredTypeSyntax)
                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(itemIdentifier)
                        .WithInitializer(SyntaxFactory.EqualsValueClause(itemValue)))));

        var body = ForeachSourceSyntaxFacts.PrependStatements(
            node.Statement, new[] { itemDecl });
        var forStatement = BuildFor(indexName, sourceAccess, lengthMember, body);

        return AssembleBlock(node, sourceDecl, forStatement);
    }

    private static ForStatementSyntax BuildFor(
        string indexName,
        IdentifierNameSyntax sourceAccess,
        ISymbol lengthMember,
        StatementSyntax body)
    {
        var indexAccess = SyntaxFactory.IdentifierName(indexName);
        var indexDecl = SyntaxFactory.VariableDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)))
            .WithVariables(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.VariableDeclarator(indexName)
                    .WithInitializer(SyntaxFactory.EqualsValueClause(
                        SyntaxFactory.LiteralExpression(
                            SyntaxKind.NumericLiteralExpression,
                            SyntaxFactory.Literal(0))))));

        var lengthAccess = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            sourceAccess,
            SyntaxFactory.Token(SyntaxKind.DotToken),
            SyntaxFactory.IdentifierName(lengthMember.Name));

        var condition = SyntaxFactory.BinaryExpression(
            SyntaxKind.LessThanExpression,
            indexAccess,
            lengthAccess);

        var increment = SyntaxFactory.PostfixUnaryExpression(
            SyntaxKind.PostIncrementExpression,
            SyntaxFactory.IdentifierName(indexName));

        return SyntaxFactory.ForStatement(body)
            .WithDeclaration(indexDecl)
            .WithCondition(condition)
            .WithIncrementors(SyntaxFactory.SingletonSeparatedList<ExpressionSyntax>(increment));
    }

    private static BlockSyntax AssembleBlock(
        ForEachStatementSyntax node,
        LocalDeclarationStatementSyntax sourceDecl,
        StatementSyntax forStatement)
    {
        var statements = SyntaxFactory.List(new StatementSyntax[] { sourceDecl, forStatement });
        // Original node trivia lands on the first replacement statement; the
        // enclosing block (multi-statement replacement, E07 discipline)
        // carries the trailing trivia. Labels inside the moved body survive.
        var first = statements[0].WithLeadingTrivia(node.GetLeadingTrivia());
        statements = statements.Replace(statements[0], first);
        return SyntaxFactory.Block(statements)
            .WithTrailingTrivia(node.GetTrailingTrivia());
    }
}
