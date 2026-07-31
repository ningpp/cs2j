using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ReadOnlyStructMaker;

internal static class ConstructorGenerator
{
    private static readonly HashSet<string> ObjectMemberNames = new()
        { "GetType", "Equals", "GetHashCode", "ToString" };

    public static bool HasNameConflict(IEnumerable<string> fieldNames)
        => fieldNames.Any(n => ObjectMemberNames.Contains(n));

    public static ConstructorDeclarationSyntax? Generate(
        string structName,
        IReadOnlyList<(string Name, TypeSyntax Type, bool IsPublic)> members)
    {
        if (members.Count == 0) return null;

        var parameters = members.Select(m =>
            SyntaxFactory.Parameter(SyntaxFactory.Identifier(ToCamelCase(m.Name)))
                .WithType(m.Type)).ToList();

        var assignments = members.Select(m =>
            SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.ThisExpression(),
                        SyntaxFactory.IdentifierName(m.Name)),
                    SyntaxFactory.IdentifierName(ToCamelCase(m.Name))))).ToList();

        var accessMod = members.All(m => !m.IsPublic)
            ? SyntaxKind.InternalKeyword
            : SyntaxKind.PublicKeyword;

        return SyntaxFactory.ConstructorDeclaration(structName)
            .AddModifiers(SyntaxFactory.Token(accessMod).WithTrailingTrivia(SyntaxFactory.Space))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters)))
            .WithBody(SyntaxFactory.Block(assignments))
            .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.Space, SyntaxFactory.Space, SyntaxFactory.Space, SyntaxFactory.Space);
    }

    private static string ToCamelCase(string name)
        => name.Length > 0 && char.IsUpper(name[0])
            ? char.ToLowerInvariant(name[0]) + name[1..]
            : name;
}
