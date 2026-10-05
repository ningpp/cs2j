using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ThreadStaticRewrite;

/// <summary>Marks nodes produced by the thread-static rewriter (TS-1 declarations, TS-2..TS-4 sites).</summary>
internal static class ThreadStaticAnnotations
{
    public const string Kind = "Cs2jThreadStaticRewrite";

    public static SyntaxAnnotation Annotation { get; } = new(Kind);

    public static T Mark<T>(T node) where T : SyntaxNode => node.WithAdditionalAnnotations(Annotation);
}

/// <summary>
/// Applies active [ThreadStatic] plans to one syntax tree:
/// TS-1 declaration rewrite (+ multi-variable split), TS-2 reads, TS-3 writes,
/// TS-4 compound assignments and ++/-- (all become the corresponding .Value forms).
/// Only semantic-model-bound nodes are accepted; there is no text replacement.
/// </summary>
internal sealed class ThreadStaticRewriter : CSharpSyntaxRewriter
{
    private const string ThreadStaticAttributeName = "System.ThreadStaticAttribute";

    private readonly Dictionary<SyntaxNode, ReferenceSite> _sitesByNode;
    private readonly Dictionary<FieldDeclarationSyntax, IReadOnlyList<ThreadStaticFieldPlan>> _plansByDeclaration;
    private readonly ThreadStaticStatistics _stats;

    public ThreadStaticRewriter(
        Dictionary<SyntaxNode, ReferenceSite> sitesByNode,
        Dictionary<FieldDeclarationSyntax, IReadOnlyList<ThreadStaticFieldPlan>> plansByDeclaration,
        ThreadStaticStatistics stats)
    {
        _sitesByNode = sitesByNode;
        _plansByDeclaration = plansByDeclaration;
        _stats = stats;
    }

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
        => RewriteTypeDeclaration(node, base.VisitClassDeclaration(node));

    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
        => RewriteTypeDeclaration(node, base.VisitStructDeclaration(node));

    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node)
        => RewriteTypeDeclaration(node, base.VisitRecordDeclaration(node));

    public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
        => RewriteTypeDeclaration(node, base.VisitInterfaceDeclaration(node));

    private SyntaxNode? RewriteTypeDeclaration(TypeDeclarationSyntax node, SyntaxNode? visitedBase)
    {
        if (visitedBase is not TypeDeclarationSyntax visited)
        {
            return visitedBase;
        }

        var output = new List<MemberDeclarationSyntax>(visited.Members.Count);
        for (var i = 0; i < node.Members.Count; i++)
        {
            var originalMember = node.Members[i];
            var visitedMember = visited.Members[i];
            if (originalMember is FieldDeclarationSyntax originalField
                && visitedMember is FieldDeclarationSyntax visitedFieldDeclaration
                && _plansByDeclaration.TryGetValue(originalField, out var plans))
            {
                output.AddRange(BuildFieldDeclarations(originalField, visitedFieldDeclaration, plans));
            }
            else
            {
                output.Add(visitedMember);
            }
        }

        return visited.WithMembers(SyntaxFactory.List(output));
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        var visited = (ExpressionSyntax?)base.VisitIdentifierName(node) ?? node;
        return _sitesByNode.TryGetValue(node, out var site)
            ? WrapValue(visited, site)
            : visited;
    }

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var visited = (ExpressionSyntax?)base.VisitMemberAccessExpression(node) ?? node;
        return _sitesByNode.TryGetValue(node, out var site)
            ? WrapValue(visited, site)
            : visited;
    }

    public override SyntaxNode? VisitQualifiedName(QualifiedNameSyntax node)
    {
        var visited = (ExpressionSyntax?)base.VisitQualifiedName(node) ?? node;
        return _sitesByNode.TryGetValue(node, out var site)
            ? WrapValue(visited, site)
            : visited;
    }

    private ExpressionSyntax WrapValue(ExpressionSyntax accessExpression, ReferenceSite site)
    {
        switch (site.Kind)
        {
            case ThreadStaticSiteKind.Write:
                _stats.WritesRewritten++;
                break;
            case ThreadStaticSiteKind.Compound:
                _stats.CompoundSitesRewritten++;
                break;
            case ThreadStaticSiteKind.Increment:
                _stats.IncrementSitesRewritten++;
                break;
            default:
                _stats.ReadsRewritten++;
                break;
        }

        return ThreadStaticAnnotations.Mark(SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            accessExpression,
            SyntaxFactory.IdentifierName("Value")));
    }

    // ---- TS-1: field declarations (incl. multi-variable split) ----

    private IEnumerable<MemberDeclarationSyntax> BuildFieldDeclarations(
        FieldDeclarationSyntax originalField,
        FieldDeclarationSyntax visitedField,
        IReadOnlyList<ThreadStaticFieldPlan> plans)
    {
        var isFirst = true;
        for (var i = 0; i < originalField.Declaration.Variables.Count; i++)
        {
            var originalDeclarator = originalField.Declaration.Variables[i];
            var visitedDeclarator = visitedField.Declaration.Variables[i];
            var plan = plans.FirstOrDefault(p => p.Declarator == originalDeclarator);

            FieldDeclarationSyntax built;
            if (plan is { Active: true })
            {
                built = BuildHolderFieldDeclaration(originalField, visitedField, visitedDeclarator, plan);
                _stats.FieldsRewritten++;
                if (plan.InitializerKind == ThreadStaticInitializerKind.Inline)
                {
                    _stats.InitializersNormalized++;
                }
            }
            else
            {
                // Skipped (or non-hit) variable: keep its attributes and declarator verbatim.
                built = visitedField.WithDeclaration(
                    visitedField.Declaration.WithVariables(
                        SyntaxFactory.SingletonSeparatedList(visitedDeclarator)));
            }

            if (isFirst)
            {
                built = built.WithLeadingTrivia(visitedField.GetLeadingTrivia());
                isFirst = false;
            }
            else
            {
                built = built.WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);
            }

            yield return built;
        }
    }

    private static FieldDeclarationSyntax BuildHolderFieldDeclaration(
        FieldDeclarationSyntax originalField,
        FieldDeclarationSyntax visitedField,
        VariableDeclaratorSyntax visitedDeclarator,
        ThreadStaticFieldPlan plan)
    {
        // Attribute lists and modifiers never contain reference sites, so they are taken
        // from the original declaration where symbol application references are identity-stable.
        // Only the initializer expression can contain rewritten sites, hence the visited declarator.
        var attributeLists = RemoveThreadStaticAttributes(originalField, plan);

        var modifiers = originalField.Modifiers;
        if (!modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword)))
        {
            modifiers = modifiers.Add(SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));
        }

        var typeText = originalField.Declaration.Type.ToString();
        var holderType = SyntaxFactory.ParseTypeName(
            "global::System.Threading.ThreadLocal<" + typeText + ">");

        ExpressionSyntax holderInitializer = visitedDeclarator.Initializer?.Value is { } initializer
            ? SyntaxFactory.ParseExpression(
                "new global::System.Threading.ThreadLocal<" + typeText + ">(() => " + initializer + ")")
            : SyntaxFactory.ParseExpression(
                "new global::System.Threading.ThreadLocal<" + typeText + ">()");

        var newDeclarator = SyntaxFactory.VariableDeclarator(
            SyntaxFactory.Identifier(visitedDeclarator.Identifier.ValueText))
            .WithInitializer(SyntaxFactory.EqualsValueClause(holderInitializer));

        var declaration = SyntaxFactory.FieldDeclaration(
            attributeLists,
            modifiers,
            SyntaxFactory.VariableDeclaration(holderType,
                SyntaxFactory.SingletonSeparatedList(newDeclarator)));

        return ThreadStaticAnnotations.Mark(declaration).WithTrailingTrivia(visitedField.GetTrailingTrivia());
    }

    /// <summary>Drops only the ThreadStatic attribute lists/entries; other attributes are preserved.</summary>
    private static SyntaxList<AttributeListSyntax> RemoveThreadStaticAttributes(
        FieldDeclarationSyntax originalField,
        ThreadStaticFieldPlan plan)
    {
        // On the original (unvisited) tree, ApplicationSyntaxReference.GetSyntax() returns
        // the exact AttributeSyntax nodes present in originalField, so reference identity is safe.
        var removed = new HashSet<AttributeSyntax>();
        foreach (var data in plan.Field.GetAttributes())
        {
            if (data.AttributeClass?.ToDisplayString() != ThreadStaticAttributeName) continue;
            if (data.ApplicationSyntaxReference?.GetSyntax() is AttributeSyntax attribute)
            {
                removed.Add(attribute);
            }
        }

        var keptLists = new List<AttributeListSyntax>();
        foreach (var list in originalField.AttributeLists)
        {
            var keptAttributes = list.Attributes.Where(a => !removed.Contains(a)).ToArray();
            if (keptAttributes.Length == 0) continue;
            keptLists.Add(keptAttributes.Length == list.Attributes.Count
                ? list
                : list.WithAttributes(SyntaxFactory.SeparatedList(keptAttributes)));
        }

        return SyntaxFactory.List(keptLists);
    }
}
