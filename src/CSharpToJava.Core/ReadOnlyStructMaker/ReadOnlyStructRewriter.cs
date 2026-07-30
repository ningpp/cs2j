using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ReadOnlyStructMaker;

internal sealed class ReadOnlyStructRewriter : CSharpSyntaxRewriter
{
    private readonly Dictionary<StructDeclarationSyntax, AnalyzeResult> _results;
    private readonly ReadOnlyStructMakerOptions _options;

    public ReadOnlyStructRewriter(Dictionary<StructDeclarationSyntax, AnalyzeResult> results,
        ReadOnlyStructMakerOptions options)
    {
        _results = results;
        _options = options;
    }

    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
    {
        if (!_results.TryGetValue(node, out var result) || !result.ShouldRewrite)
            return base.VisitStructDeclaration(node);

        var rewritten = result.Level switch
        {
            ConversionLevel.DirectAdd => ApplyDirectAdd(node),
            ConversionLevel.PropertyConvert => ApplyPropertyConvert(node),
            ConversionLevel.DataContainer => ApplyDataContainer(node),
            ConversionLevel.MethodMigrate => ApplyMethodMigrate(node, result),
            _ => node
        };

        return base.VisitStructDeclaration(rewritten);
    }

    internal static StructDeclarationSyntax ApplyDirectAdd(StructDeclarationSyntax node)
    {
        if (node.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword)))
            return node;

        var readonlyToken = SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)
            .WithTrailingTrivia(SyntaxFactory.Space);

        if (node.Modifiers.Count > 0)
        {
            // Insert readonly as the last modifier (before 'struct' keyword)
            return node.AddModifiers(readonlyToken);
        }

        // No modifiers: insert before 'struct' keyword, preserving leading trivia
        var leadingTrivia = node.Keyword.LeadingTrivia;
        readonlyToken = readonlyToken.WithLeadingTrivia(leadingTrivia);
        var newKeyword = node.Keyword.WithLeadingTrivia(SyntaxFactory.TriviaList());
        return node.WithKeyword(newKeyword).AddModifiers(readonlyToken);
    }

    private static StructDeclarationSyntax ApplyPropertyConvert(StructDeclarationSyntax node)
    {
        // L2: remove 'private set' from accessors, then add readonly
        var rewriter = new PrivateSetRemover();
        var result = (StructDeclarationSyntax)rewriter.Visit(node)!;
        return ApplyDirectAdd(result);
    }

    private StructDeclarationSyntax ApplyDataContainer(StructDeclarationSyntax node)
    {
        // L3: convert get/set properties to get-only, fields to properties, generate ctor
        var structName = node.Identifier.Text;
        var membersForCtor = new List<(string Name, TypeSyntax Type, bool IsPublic)>();
        var newMembers = new SyntaxList<MemberDeclarationSyntax>();

        foreach (var member in node.Members)
        {
            if (member is PropertyDeclarationSyntax prop &&
                prop.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) == true)
            {
                // Convert get/set → get-only
                var getter = prop.AccessorList.Accessors.First(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
                var newProp = prop.WithAccessorList(
                    SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(getter)));
                newMembers = newMembers.Add(newProp);
                membersForCtor.Add((prop.Identifier.Text, prop.Type,
                    prop.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword))));
            }
            else if (member is FieldDeclarationSyntax field &&
                     !field.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword)) &&
                     !field.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
            {
                // Convert internal/private fields → get-only properties
                foreach (var variable in field.Declaration.Variables)
                {
                    var fieldProp = SyntaxFactory.PropertyDeclaration(field.Declaration.Type, variable.Identifier.Text)
                        .WithModifiers(field.Modifiers)
                        .WithAccessorList(SyntaxFactory.AccessorList(
                            SyntaxFactory.SingletonList(
                                SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)))))
                        .WithTrailingTrivia(variable.Identifier.TrailingTrivia);
                    newMembers = newMembers.Add(fieldProp);
                    membersForCtor.Add((variable.Identifier.Text, field.Declaration.Type, false));
                }
            }
            else
            {
                newMembers = newMembers.Add(member);
            }
        }

        var rewritten = node.WithMembers(newMembers);

        // Generate constructor if no equivalent exists
        if (membersForCtor.Count > 0)
        {
            var ctor = ConstructorGenerator.Generate(structName, membersForCtor);
            if (ctor != null && !HasEquivalentCtor(rewritten, ctor))
            {
                rewritten = rewritten.AddMembers(ctor);
            }
        }

        return ApplyDirectAdd(rewritten);
    }

    private StructDeclarationSyntax ApplyMethodMigrate(StructDeclarationSyntax node, AnalyzeResult result)
    {
        // L5: migrate mutating methods (placeholder - full impl in Task 9)
        var rewritten = node;
        if (result.MethodMigrations != null)
        {
            var migrator = new MethodMigrator(node.Identifier.Text);
            foreach (var migration in result.MethodMigrations)
            {
                var migrated = migrator.Migrate(migration.Syntax);
                var current = rewritten.DescendantNodes().OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(m => m.Identifier.Text == migration.Syntax.Identifier.Text &&
                                         m.ParameterList.Parameters.Count == migration.Syntax.ParameterList.Parameters.Count);
                if (current != null)
                {
                    rewritten = rewritten.ReplaceNode(current, migrated);
                }
            }
        }
        return ApplyDirectAdd(rewritten);
    }

    private static bool HasEquivalentCtor(StructDeclarationSyntax node, ConstructorDeclarationSyntax newCtor)
    {
        var newParams = newCtor.ParameterList.Parameters;
        return node.Members.OfType<ConstructorDeclarationSyntax>().Any(existing =>
        {
            var ep = existing.ParameterList.Parameters;
            if (ep.Count != newParams.Count) return false;
            for (int i = 0; i < ep.Count; i++)
                if (ep[i].Type?.ToString() != newParams[i].Type?.ToString()) return false;
            return true;
        });
    }

    private sealed class PrivateSetRemover : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitAccessorList(AccessorListSyntax node)
        {
            var accessors = node.Accessors.Where(a =>
                !(a.IsKind(SyntaxKind.SetAccessorDeclaration) &&
                  a.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword))));
            return node.WithAccessors(SyntaxFactory.List(accessors));
        }
    }
}
