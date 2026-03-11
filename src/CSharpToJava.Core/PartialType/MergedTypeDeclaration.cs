using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.PartialType;

/// <summary>
/// Represents a merged type declaration that combines all partial parts.
/// This is passed to the transformers for conversion to Java.
/// </summary>
public class MergedTypeDeclaration
{
    /// <summary>
    /// The type symbol from the semantic model.
    /// </summary>
    public INamedTypeSymbol TypeSymbol { get; }

    /// <summary>
    /// The combined syntax node representing all merged parts.
    /// This is a synthesized syntax tree that contains all members from all partial parts.
    /// </summary>
    public TypeDeclarationSyntax MergedSyntax { get; }

    /// <summary>
    /// The original partial parts that were merged.
    /// </summary>
    public List<INamedTypeSymbol> PartialParts { get; }

    /// <summary>
    /// The original syntax nodes for each partial part.
    /// </summary>
    public List<TypeDeclarationSyntax> OriginalSyntaxNodes { get; }

    /// <summary>
    /// The file name for the output Java file.
    /// For partial types, this is {TypeName}.java regardless of which file was primary.
    /// </summary>
    public string OutputFileName { get; }

    /// <summary>
    /// Whether this type was merged from multiple partial declarations.
    /// </summary>
    public bool IsMerged => PartialParts.Count > 1;

    public MergedTypeDeclaration(
        INamedTypeSymbol typeSymbol,
        TypeDeclarationSyntax mergedSyntax,
        List<INamedTypeSymbol> partialParts,
        List<TypeDeclarationSyntax> originalSyntaxNodes,
        string outputFileName)
    {
        TypeSymbol = typeSymbol;
        MergedSyntax = mergedSyntax;
        PartialParts = partialParts;
        OriginalSyntaxNodes = originalSyntaxNodes;
        OutputFileName = outputFileName;
    }

    /// <summary>
    /// Creates a merged type declaration from a partial type group.
    /// This synthesizes a single syntax tree containing all members from all partial parts.
    /// </summary>
    public static MergedTypeDeclaration FromPartialTypeGroup(
        PartialTypeGroup group,
        SemanticModel semanticModel)
    {
        if (group.SyntaxNodes.Count == 0)
        {
            throw new ArgumentException($"Type '{group.TypeSymbol.Name}' has no syntax nodes to merge", nameof(group));
        }

        // Use the first syntax node as the template
        var primaryNode = group.SyntaxNodes[0];

        // Collect all members from all partial parts
        var allMembers = new List<MemberDeclarationSyntax>();
        var seenMembers = new HashSet<string>();

        foreach (var syntaxNode in group.SyntaxNodes)
        {
            foreach (var member in syntaxNode.Members)
            {
                var memberKey = GetMemberKey(member);
                if (seenMembers.Add(memberKey))
                {
                    allMembers.Add(member);
                }
            }
        }

        // Create a new syntax node with all members
        TypeDeclarationSyntax mergedNode;

        if (primaryNode is ClassDeclarationSyntax classDecl)
        {
            mergedNode = classDecl
                .WithMembers(new SyntaxList<MemberDeclarationSyntax>(allMembers))
                .WithModifiers(FilterPartialModifiers(classDecl.Modifiers));
        }
        else if (primaryNode is StructDeclarationSyntax structDecl)
        {
            mergedNode = structDecl
                .WithMembers(new SyntaxList<MemberDeclarationSyntax>(allMembers))
                .WithModifiers(FilterPartialModifiers(structDecl.Modifiers));
        }
        else if (primaryNode is InterfaceDeclarationSyntax interfaceDecl)
        {
            mergedNode = interfaceDecl
                .WithMembers(new SyntaxList<MemberDeclarationSyntax>(allMembers))
                .WithModifiers(FilterPartialModifiers(interfaceDecl.Modifiers));
        }
        else
        {
            mergedNode = primaryNode
                .WithMembers(new SyntaxList<MemberDeclarationSyntax>(allMembers))
                .WithModifiers(FilterPartialModifiers(primaryNode.Modifiers));
        }

        // Generate output file name
        var outputFileName = $"{group.TypeSymbol.Name}.java";

        return new MergedTypeDeclaration(
            group.TypeSymbol,
            mergedNode,
            group.PartialParts,
            group.SyntaxNodes,
            outputFileName);
    }

    /// <summary>
    /// Removes the 'partial' modifier from the modifier list.
    /// Java doesn't support partial types, so we remove this modifier.
    /// </summary>
    private static SyntaxTokenList FilterPartialModifiers(SyntaxTokenList modifiers)
    {
        return new SyntaxTokenList(
            modifiers.Where(m => !m.IsKind(SyntaxKind.PartialKeyword)));
    }

    /// <summary>
    /// Creates a unique key for a member to detect duplicates.
    /// </summary>
    private static string GetMemberKey(MemberDeclarationSyntax member)
    {
        // For methods, include the name and parameter count
        if (member is MethodDeclarationSyntax method)
        {
            return $"method:{method.Identifier.Text}:{method.ParameterList?.Parameters.Count ?? 0}";
        }

        // For properties, use the identifier
        if (member is PropertyDeclarationSyntax property)
        {
            return $"property:{property.Identifier.Text}";
        }

        // For fields, use a combination of variable names
        if (member is FieldDeclarationSyntax field)
        {
            var variables = string.Join(",",
                field.Declaration.Variables.Select(v => v.Identifier.Text));
            return $"field:{variables}";
        }

        // For constructors, use parameter count
        if (member is ConstructorDeclarationSyntax ctor)
        {
            return $"ctor:{ctor.ParameterList?.Parameters.Count ?? 0}";
        }

        // For other members, use the kind and a hash of the syntax
        return $"other:{member.Kind()}:{member.GetHashCode()}";
    }
}
