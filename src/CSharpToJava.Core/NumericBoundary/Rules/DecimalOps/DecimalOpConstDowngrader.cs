using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Linq;
using NB = CSharpToJava.Core.NumericBoundary.NumericBoundarySyntaxFacts;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.NumericBoundary.Rules.DecimalOps;

/// <summary>
/// DC-12 post-pass: marked <c>const decimal</c> fields become
/// <c>static readonly decimal</c> and marked locals drop the <c>const</c>
/// keyword. Declarations mixing downgraded and preserved declarators are split
/// into separate declarations (const is per-declaration in source but the
/// modifier belongs to the whole declaration).
/// </summary>
internal sealed class DecimalOpConstDowngrader : CSharpSyntaxRewriter
{
    private readonly DecimalOpStatistics _stats;

    public DecimalOpConstDowngrader(DecimalOpStatistics stats)
    {
        _stats = stats;
    }

    public bool MadeChanges { get; private set; }

    public override SyntaxList<TNode> VisitList<TNode>(SyntaxList<TNode> list)
    {
        var visited = base.VisitList(list);
        if (typeof(TNode) == typeof(MemberDeclarationSyntax))
        {
            return ExpandMembers(visited);
        }

        if (typeof(TNode) == typeof(StatementSyntax))
        {
            return ExpandStatements(visited);
        }

        return visited;
    }

    private SyntaxList<TNode> ExpandMembers<TNode>(SyntaxList<TNode> list) where TNode : SyntaxNode
    {
        var result = new List<MemberDeclarationSyntax>();
        foreach (var item in list)
        {
            switch (item)
            {
                case FieldDeclarationSyntax field when HasMarked(field.Declaration):
                    MadeChanges = true;
                    var fieldFirst = true;
                    foreach (var variable in field.Declaration.Variables)
                    {
                        var downgraded = IsMarked(variable);
                        if (downgraded)
                        {
                            _stats.ConstantsDowngraded++;
                        }

                        var split = BuildField(field, variable, downgraded, fieldFirst);
                        fieldFirst = false;
                        result.Add(split);
                    }

                    break;

                case GlobalStatementSyntax global when global.Statement is LocalDeclarationStatementSyntax local
                    && HasMarked(local.Declaration):
                    MadeChanges = true;
                    var globalFirst = true;
                    foreach (var variable in local.Declaration.Variables)
                    {
                        var splitLocal = BuildLocal(local, variable, IsMarked(variable), globalFirst);
                        globalFirst = false;
                        result.Add(GlobalStatement(splitLocal)
                            .WithLeadingTrivia(splitLocal.GetLeadingTrivia()));
                    }

                    break;

                default:
                    result.Add((MemberDeclarationSyntax)(object)item);
                    break;
            }
        }

        return List(result.Cast<TNode>());
    }

    private SyntaxList<TNode> ExpandStatements<TNode>(SyntaxList<TNode> list) where TNode : SyntaxNode
    {
        var result = new List<StatementSyntax>();
        foreach (var item in list)
        {
            if (item is LocalDeclarationStatementSyntax local && HasMarked(local.Declaration))
            {
                MadeChanges = true;
                var first = true;
                foreach (var variable in local.Declaration.Variables)
                {
                    result.Add(BuildLocal(local, variable, IsMarked(variable), first));
                    first = false;
                }
            }
            else
            {
                result.Add((StatementSyntax)(object)item);
            }
        }

        return List(result.Cast<TNode>());
    }

    private static bool HasMarked(VariableDeclarationSyntax declaration) =>
        declaration.Variables.Any(IsMarked);

    private static bool IsMarked(VariableDeclaratorSyntax variable) =>
        variable.GetAnnotations(DecimalOpSyntaxFacts.ConstDowngradeAnnotationKind).Any();

    private static FieldDeclarationSyntax BuildField(
        FieldDeclarationSyntax original,
        VariableDeclaratorSyntax variable,
        bool downgraded,
        bool isFirst)
    {
        var modifiers = downgraded
            ? DowngradedFieldModifiers(original.Modifiers)
            : original.Modifiers;

        var declaration = VariableDeclaration(original.Declaration.Type, SingletonSeparatedList(variable));
        var field = FieldDeclaration(original.AttributeLists, modifiers, declaration)
            .WithSemicolonToken(original.SemicolonToken)
            .WithLeadingTrivia(isFirst
                ? original.GetLeadingTrivia()
                : TriviaList(CarriageReturnLineFeed))
            .WithTrailingTrivia(original.GetTrailingTrivia());

        return downgraded
            ? field.WithAdditionalAnnotations(NB.RewriteAnnotation, DecimalOpSyntaxFacts.RewriteAnnotation)
            : field;
    }

    private LocalDeclarationStatementSyntax BuildLocal(
        LocalDeclarationStatementSyntax original,
        VariableDeclaratorSyntax variable,
        bool downgraded,
        bool isFirst)
    {
        if (downgraded)
        {
            _stats.ConstantsDowngraded++;
        }

        var modifiers = downgraded
            ? TokenList(original.Modifiers.Where(m => !m.IsKind(SyntaxKind.ConstKeyword)))
            : original.Modifiers;

        var declaration = VariableDeclaration(original.Declaration.Type, SingletonSeparatedList(variable));
        var local = LocalDeclarationStatement(original.AttributeLists, modifiers, declaration)
            .WithSemicolonToken(original.SemicolonToken)
            .WithLeadingTrivia(isFirst
                ? original.GetLeadingTrivia()
                : TriviaList(CarriageReturnLineFeed))
            .WithTrailingTrivia(original.GetTrailingTrivia());

        return downgraded
            ? local.WithAdditionalAnnotations(NB.RewriteAnnotation, DecimalOpSyntaxFacts.RewriteAnnotation)
            : local;
    }

    /// <summary>
    /// <c>const</c> fields are implicitly static: replace <c>const</c> with
    /// <c>static readonly</c> while preserving the original accessibility
    /// (and other) modifiers, e.g. <c>public const</c> → <c>public static readonly</c>.
    /// </summary>
    private static SyntaxTokenList DowngradedFieldModifiers(SyntaxTokenList originalModifiers)
    {
        var leading = originalModifiers.Count > 0
            ? originalModifiers[0].LeadingTrivia
            : TriviaList();

        var staticToken = Token(leading, SyntaxKind.StaticKeyword, TriviaList(Space));
        var readonlyToken = Token(TriviaList(), SyntaxKind.ReadOnlyKeyword, TriviaList(Space));

        var tokens = new List<SyntaxToken>();
        var inserted = false;
        foreach (var modifier in originalModifiers)
        {
            if (modifier.IsKind(SyntaxKind.ConstKeyword))
            {
                tokens.Add(staticToken);
                tokens.Add(readonlyToken);
                inserted = true;
                continue;
            }

            // Keep accessibility modifiers in front of static/readonly.
            var isAccessibility = modifier.Kind() is SyntaxKind.PublicKeyword
                or SyntaxKind.PrivateKeyword
                or SyntaxKind.ProtectedKeyword
                or SyntaxKind.InternalKeyword;
            if (!inserted && isAccessibility)
            {
                tokens.Add(modifier);
            }
            else
            {
                if (!inserted)
                {
                    tokens.Add(staticToken);
                    tokens.Add(readonlyToken);
                    inserted = true;
                }

                tokens.Add(modifier);
            }
        }

        if (!inserted)
        {
            tokens.Add(staticToken);
            tokens.Add(readonlyToken);
        }

        return TokenList(tokens);
    }
}
