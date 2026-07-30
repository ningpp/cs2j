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
        var rewritten = node;
        var structName = node.Identifier.Text;

        // 1. Migrate mutating methods
        if (result.MethodMigrations != null)
        {
            var fieldNames = rewritten.Members.OfType<FieldDeclarationSyntax>()
                .Where(f => !f.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword) || m.IsKind(SyntaxKind.ConstKeyword)))
                .SelectMany(f => f.Declaration.Variables.Select(v => v.Identifier.Text))
                .ToHashSet();
            var migratedMethodNames = result.MethodMigrations
                .Select(m => m.Method.Name).ToHashSet();
            var migrator = new MethodMigrator(structName, fieldNames, migratedMethodNames);
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

        // 2. Migrate public property setters to WithXxx() methods
        var withMethods = new List<MemberDeclarationSyntax>();
        var propertiesToConvert = rewritten.Members.OfType<PropertyDeclarationSyntax>()
            .Where(p => p.AccessorList?.Accessors.Any(a =>
                a.IsKind(SyntaxKind.SetAccessorDeclaration) &&
                !a.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword))) == true)
            .ToList();

        foreach (var prop in propertiesToConvert)
        {
            var setter = prop.AccessorList!.Accessors.First(a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
            var withMethodName = "With" + prop.Identifier.Text;

            // Build WithXxx method body from setter body
            MethodDeclarationSyntax withMethod;
            if (setter.Body != null)
            {
                // Setter has a body — migrate it
                var setterBody = (BlockSyntax)new ThisToResultRewriter().Visit(setter.Body)!;
                // Replace `value` parameter references (already correct in setter body)
                var resultDecl = SyntaxFactory.LocalDeclarationStatement(
                    SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                        .WithVariables(SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.VariableDeclarator("result")
                                .WithInitializer(SyntaxFactory.EqualsValueClause(
                                    SyntaxFactory.ThisExpression())))))
                    .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);

                // Replace field assignments on this with result
                var newBody = setterBody.WithStatements(
                    setterBody.Statements.Insert(0, resultDecl));
                var returnStmt = SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName("result"))
                    .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed);
                newBody = newBody.AddStatements(returnStmt);

                var param = SyntaxFactory.Parameter(SyntaxFactory.Identifier("value"))
                    .WithType(prop.Type);

                withMethod = SyntaxFactory.MethodDeclaration(
                        SyntaxFactory.IdentifierName(structName), withMethodName)
                    .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space))
                    .AddParameterListParameters(param)
                    .WithBody(newBody);
            }
            else
            {
                // Auto-property setter — simple assignment
                var param = SyntaxFactory.Parameter(SyntaxFactory.Identifier("value"))
                    .WithType(prop.Type);

                var body = SyntaxFactory.Block(
                    SyntaxFactory.LocalDeclarationStatement(
                        SyntaxFactory.VariableDeclaration(SyntaxFactory.IdentifierName("var"))
                            .WithVariables(SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.VariableDeclarator("result")
                                    .WithInitializer(SyntaxFactory.EqualsValueClause(
                                        SyntaxFactory.ThisExpression()))))),
                    SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            SyntaxFactory.IdentifierName(prop.Identifier.Text),
                            SyntaxFactory.IdentifierName("value"))),
                    SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName("result")));

                // Replace field name with result.field
                body = (BlockSyntax)new ThisToResultRewriter().Visit(body)!;

                withMethod = SyntaxFactory.MethodDeclaration(
                        SyntaxFactory.IdentifierName(structName), withMethodName)
                    .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space))
                    .AddParameterListParameters(param)
                    .WithBody(body);
            }

            withMethods.Add(withMethod);

            // Remove setter from property (make it get-only)
            var getter = prop.AccessorList.Accessors.First(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
            var newProp = prop.WithAccessorList(
                SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(getter)));
            rewritten = rewritten.ReplaceNode(
                rewritten.Members.OfType<PropertyDeclarationSyntax>()
                    .First(p => p.Identifier.Text == prop.Identifier.Text),
                newProp);
        }

        // Add WithXxx methods
        if (withMethods.Count > 0)
        {
            rewritten = rewritten.AddMembers(withMethods.ToArray());
        }

        // NOTE: Do NOT add 'readonly' keyword for L5 method migration.
        // The Java converter would mark fields as final, but migrated methods
        // need to modify the cloned copy's fields (result._field = ...).
        return rewritten;
    }

    private sealed class ThisToResultRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitThisExpression(ThisExpressionSyntax node)
            => SyntaxFactory.IdentifierName("result").WithTriviaFrom(node);
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
