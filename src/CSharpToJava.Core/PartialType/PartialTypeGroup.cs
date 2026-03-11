using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.PartialType;

/// <summary>
/// Represents a group of partial type declarations that will be merged into a single type.
/// Uses Roslyn's symbol API to identify all parts of a partial type.
/// </summary>
public class PartialTypeGroup
{
    /// <summary>
    /// The primary type symbol representing the merged type.
    /// This is obtained from the compilation's semantic model.
    /// </summary>
    public INamedTypeSymbol TypeSymbol { get; }

    /// <summary>
    /// All partial parts of this type.
    /// For partial types, this includes all declarations across different files.
    /// For non-partial types, this contains only the single declaration.
    /// </summary>
    public List<INamedTypeSymbol> PartialParts { get; }

    /// <summary>
    /// The syntax nodes for each partial part.
    /// These are used for transformation after merging at the symbol level.
    /// </summary>
    public List<TypeDeclarationSyntax> SyntaxNodes { get; }

    /// <summary>
    /// Whether this type has multiple partial parts.
    /// </summary>
    public bool IsPartial => PartialParts.Count > 1;

    /// <summary>
    /// The namespace containing this type.
    /// </summary>
    public string Namespace => TypeSymbol.ContainingNamespace?.ToDisplayString() ?? string.Empty;

    public PartialTypeGroup(
        INamedTypeSymbol typeSymbol,
        List<INamedTypeSymbol> partialParts,
        List<TypeDeclarationSyntax> syntaxNodes)
    {
        TypeSymbol = typeSymbol;
        PartialParts = partialParts;
        SyntaxNodes = syntaxNodes;
    }

    /// <summary>
    /// Gets all members from all partial parts, excluding duplicate implementations.
    /// </summary>
    public IEnumerable<ISymbol> GetAllMembers()
    {
        var seenMembers = new HashSet<string>();
        var members = new List<ISymbol>();

        foreach (var part in PartialParts)
        {
            foreach (var member in part.GetMembers())
            {
                // Skip implicitly declared members (like backing fields for properties)
                if (member.IsImplicitlyDeclared)
                    continue;

                // Create a unique key for the member
                var memberKey = GetMemberKey(member);
                if (seenMembers.Add(memberKey))
                {
                    members.Add(member);
                }
            }
        }

        return members;
    }

    /// <summary>
    /// Creates a unique key for a member to detect duplicates.
    /// </summary>
    private static string GetMemberKey(ISymbol member)
    {
        // For methods, include parameter types to distinguish overloads
        if (member is IMethodSymbol method)
        {
            var parameters = string.Join(", ", method.Parameters.Select(p => p.Type.ToDisplayString()));
            return $"{member.Name}|{member.Kind}|({parameters})";
        }

        // For properties, include the type
        if (member is IPropertySymbol property)
        {
            return $"{member.Name}|{member.Kind}|{property.Type.ToDisplayString()}";
        }

        // For other members, use name and kind
        return $"{member.Name}|{member.Kind}";
    }
}
