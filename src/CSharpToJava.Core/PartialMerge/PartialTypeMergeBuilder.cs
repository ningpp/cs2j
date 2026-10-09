using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.PartialMerge;

/// <summary>
/// Builds the merged declaration on the primary part node (design §4.2):
/// kind/record parameter list stay primary's; modifier union in canonical order;
/// semantic base/interface dedup; attribute concat; constraint union; members in
/// part order; the partial modifier is KEPT (design D2).
/// </summary>
internal static class PartialTypeMergeBuilder
{
    // Canonical type-modifier order; readonly precedes partial (CS1585).
    private static readonly SyntaxKind[] ModifierOrder =
    [
        SyntaxKind.PublicKeyword,
        SyntaxKind.PrivateKeyword,
        SyntaxKind.ProtectedKeyword,
        SyntaxKind.InternalKeyword,
        // "protected internal" / "private protected" are pairs of the tokens above.
        SyntaxKind.NewKeyword,
        SyntaxKind.StaticKeyword,
        SyntaxKind.UnsafeKeyword,
        SyntaxKind.AbstractKeyword,
        SyntaxKind.SealedKeyword,
        SyntaxKind.ReadOnlyKeyword,
        SyntaxKind.RefKeyword,
        SyntaxKind.VirtualKeyword,
        SyntaxKind.PartialKeyword,
    ];

    public static TypeDeclarationSyntax Build(
        CSharpCompilation compilation,
        TypeGroupPlan group,
        PartialMemberCollapse? collapse = null)
    {
        var primary = group.Primary.Declaration;

        var merged = primary
            .WithMembers(SyntaxFactory.List(PartialMemberMerge.CollectMembers(group, collapse)))
            .WithModifiers(BuildMergedModifiers(group))
            .WithBaseList(BuildMergedBaseList(compilation, group))
            .WithAttributeLists(BuildMergedAttributes(group))
            .WithConstraintClauses(BuildMergedConstraints(group))
            .WithTypeParameterList(primary.TypeParameterList)
            // A bodyless primary ("record R(...);") has missing braces and a
            // semicolon; once members are added synthesize the body (CS1514).
            .WithOpenBraceToken(primary.OpenBraceToken.Kind() != SyntaxKind.OpenBraceToken
                ? SyntaxFactory.Token(SyntaxKind.OpenBraceToken)
                    .WithLeadingTrivia(SyntaxFactory.Space)
                : primary.OpenBraceToken)
            .WithCloseBraceToken(primary.CloseBraceToken.Kind() != SyntaxKind.CloseBraceToken
                ? SyntaxFactory.Token(SyntaxKind.CloseBraceToken)
                : primary.CloseBraceToken)
            .WithSemicolonToken(default);

        // Documentation first (D6 deterministic XML merge), then the provenance
        // banner; byte ordering is locked by the group 9 unit test.
        var docTrivia = PartialDocCommentMerger.BuildTypeLeadingTrivia(group);
        var banner = SyntaxFactory.Comment(
            "// cs2j-partial-merge: merged from " + group.Parts.Count + " part(s): " +
            string.Join(", ", group.Parts.Select(part => part.RelativePath)));
        return merged.WithLeadingTrivia(
            SyntaxFactory.TriviaList(docTrivia)
                .Add(SyntaxFactory.CarriageReturnLineFeed)
                .Add(banner)
                .Add(SyntaxFactory.CarriageReturnLineFeed));
    }

    private static SyntaxTokenList BuildMergedModifiers(TypeGroupPlan group)
    {
        var kinds = group.Parts
            .SelectMany(part => part.Declaration.Modifiers)
            .Select(token => token.Kind())
            .Distinct()
            .OrderBy(kind => Array.IndexOf(ModifierOrder, kind))
            // Manually built tokens carry no trivia; add the separating space.
            .Select(kind => SyntaxFactory.Token(kind)
                .WithTrailingTrivia(SyntaxFactory.Space))
            .ToList();

        return SyntaxFactory.TokenList(kinds);
    }

    private static BaseListSyntax? BuildMergedBaseList(CSharpCompilation compilation, TypeGroupPlan group)
    {
        var seenTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var bases = new List<BaseTypeSyntax>();

        foreach (var part in group.Parts)
        {
            if (part.Declaration.BaseList is null)
            {
                continue;
            }

            var semanticModel = compilation.GetSemanticModel(part.Declaration.SyntaxTree);
            foreach (var baseType in part.Declaration.BaseList.Types)
            {
                if (semanticModel.GetSymbolInfo(baseType.Type).Symbol is not INamedTypeSymbol baseSymbol)
                {
                    // Unresolved in source: keep syntax verbatim; candidate diff is the backstop.
                    bases.Add(baseType);
                    continue;
                }

                // object implicit base never appears in syntax; dedup interfaces and
                // same-symbol bases by semantic identity (IEnumerable<int> == full name).
                if (!seenTypes.Add(baseSymbol))
                {
                    continue;
                }

                bases.Add(baseType);
            }
        }

        return bases.Count == 0 ? null : SyntaxFactory.BaseList(SyntaxFactory.SeparatedList(bases));
    }

    private static SyntaxList<AttributeListSyntax> BuildMergedAttributes(TypeGroupPlan group)
        => SyntaxFactory.List(group.Parts.SelectMany(part => part.Declaration.AttributeLists));

    private static SyntaxList<TypeParameterConstraintClauseSyntax> BuildMergedConstraints(
        TypeGroupPlan group)
    {
        // M1: primary's clauses first, then clauses for params not already covered;
        // same-name constraint union is refined in M2 against symbol identities.
        var primary = group.Primary.Declaration;
        var clauses = new List<TypeParameterConstraintClauseSyntax>(primary.ConstraintClauses);
        var covered = new HashSet<string>(
            clauses.Select(clause => clause.Name.ToString()), StringComparer.Ordinal);

        foreach (var part in group.Parts.Skip(1))
        {
            foreach (var clause in part.Declaration.ConstraintClauses)
            {
                if (covered.Add(clause.Name.ToString()))
                {
                    clauses.Add(clause);
                }
            }
        }

        return SyntaxFactory.List(clauses);
    }
}
