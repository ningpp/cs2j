using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.PartialType;

/// <summary>
/// Core logic for finding and merging C# partial types using Roslyn's semantic model.
/// Uses symbol-level analysis to correctly identify and merge partial type declarations.
/// </summary>
public class PartialTypeMerger
{
    private readonly DiagnosticCollector _diagnostics;

    public PartialTypeMerger(DiagnosticCollector diagnostics)
    {
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Finds all types in the compilation, grouping partial types together.
    /// Returns a list where each entry represents either a single non-partial type
    /// or a group of partial types to be merged.
    /// </summary>
    public List<PartialTypeGroup> FindAndGroupTypes(CSharpCompilation compilation)
    {
        var typeGroups = new Dictionary<string, PartialTypeGroup>();
        var processedSymbols = new HashSet<INamedTypeSymbol>();

        // Traverse the namespace hierarchy to find all types
        FindTypesInNamespace(compilation.GlobalNamespace, typeGroups, processedSymbols);

        return typeGroups.Values.ToList();
    }

    /// <summary>
    /// Recursively finds all types in a namespace and its sub-namespaces.
    /// </summary>
    private void FindTypesInNamespace(
        INamespaceSymbol namespaceSymbol,
        Dictionary<string, PartialTypeGroup> typeGroups,
        HashSet<INamedTypeSymbol> processedSymbols)
    {
        // Process types in this namespace
        foreach (var typeMember in namespaceSymbol.GetTypeMembers())
        {
            if (processedSymbols.Contains(typeMember))
                continue;

            processedSymbols.Add(typeMember);

            // Get the type's unique key (full name including namespace)
            var typeKey = GetTypeKey(typeMember);

            if (!typeGroups.ContainsKey(typeKey))
            {
                // Create a new group for this type
                var partialParts = CollectPartialParts(typeMember);
                var syntaxNodes = CollectSyntaxNodes(partialParts);

                typeGroups[typeKey] = new PartialTypeGroup(
                    typeMember,
                    partialParts,
                    syntaxNodes);

                // Log if we found a partial type
                if (partialParts.Count > 1)
                {
                    _diagnostics.Info(
                        $"Found partial type '{typeMember.Name}' with {partialParts.Count} parts",
                        GetFirstLocation(syntaxNodes));
                }
            }
        }

        // Recursively process nested namespaces
        foreach (var childNamespace in namespaceSymbol.GetNamespaceMembers())
        {
            FindTypesInNamespace(childNamespace, typeGroups, processedSymbols);
        }
    }

    /// <summary>
    /// Collects all partial parts of a type declaration.
    /// For partial types, this returns all INamedTypeSymbols that are parts of the same type.
    /// For non-partial types, this returns a list with just the type itself.
    /// </summary>
    private List<INamedTypeSymbol> CollectPartialParts(INamedTypeSymbol typeSymbol)
    {
        var parts = new List<INamedTypeSymbol>();

        // Check if this is a partial type
        if (typeSymbol.IsPartialDefinition || typeSymbol.HasPartialDefinitions)
        {
            // This is a partial type - collect all parts
            var allParts = new HashSet<INamedTypeSymbol>();
            var comparer = SymbolEqualityComparer.Default;

            // Start with this type
            allParts.Add(typeSymbol);

            // Add all partial definitions
            if (typeSymbol.PartialDefinitions != null)
            {
                foreach (var part in typeSymbol.PartialDefinitions)
                {
                    allParts.Add(part);
                }
            }

            // Also check if this type is itself a partial definition
            // and find its sibling parts
            foreach (var candidate in typeSymbol.ContainingType?.GetTypeMembers() ?? Enumerable.Empty<INamedTypeSymbol>())
            {
                if (candidate.Name == typeSymbol.Name && candidate.IsPartialDefinition)
                {
                    allParts.Add(candidate);
                }
            }

            parts.AddRange(allParts);
        }
        else
        {
            // Not a partial type - just add the type itself
            parts.Add(typeSymbol);
        }

        return parts;
    }

    /// <summary>
    /// Collects the syntax nodes for all partial parts.
    /// </summary>
    private List<TypeDeclarationSyntax> CollectSyntaxNodes(List<INamedTypeSymbol> partialParts)
    {
        var syntaxNodes = new List<TypeDeclarationSyntax>();

        foreach (var part in partialParts)
        {
            // Try to find the syntax node for this symbol
            foreach (var syntaxRef in part.DeclaringSyntaxReferences)
            {
                try
                {
                    var syntax = syntaxRef.GetSyntax();
                    if (syntax is TypeDeclarationSyntax typeSyntax)
                    {
                        syntaxNodes.Add(typeSyntax);
                        break; // Use the first syntax node for each symbol
                    }
                }
                catch
                {
                    // Ignore syntax references that can't be resolved
                    // This can happen with generated code or external references
                }
            }
        }

        return syntaxNodes;
    }

    /// <summary>
    /// Gets a unique key for a type symbol (full name including namespace).
    /// </summary>
    private static string GetTypeKey(INamedTypeSymbol typeSymbol)
    {
        return typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    /// <summary>
    /// Gets the location of the first syntax node for diagnostics.
    /// </summary>
    private static Location? GetFirstLocation(List<TypeDeclarationSyntax> syntaxNodes)
    {
        return syntaxNodes.FirstOrDefault()?.GetLocation();
    }

    /// <summary>
    /// Merges all partial type groups into merged type declarations.
    /// </summary>
    public List<MergedTypeDeclaration> MergeAllTypes(
        CSharpCompilation compilation,
        ConversionContext context)
    {
        var typeGroups = FindAndGroupTypes(compilation);
        var mergedTypes = new List<MergedTypeDeclaration>();

        foreach (var group in typeGroups)
        {
            // Skip groups with no syntax nodes
            if (group.SyntaxNodes.Count == 0)
            {
                _diagnostics.Warning($"Type '{group.TypeSymbol.Name}' has no syntax nodes, skipping");
                continue;
            }

            // For each group, create a merged declaration
            var merged = MergedTypeDeclaration.FromPartialTypeGroup(
                group,
                context.SemanticModel ?? compilation.GetSemanticModel(group.SyntaxNodes[0].SyntaxTree));

            mergedTypes.Add(merged);

            if (group.IsPartial)
            {
                _diagnostics.Info(
                    $"Merged {group.PartialParts.Count} partial parts into '{group.TypeSymbol.Name}'",
                    merged.MergedSyntax.GetLocation());
            }
        }

        return mergedTypes;
    }

    /// <summary>
    /// Validates that all partial parts of a type are compatible.
    /// Emits diagnostics for any conflicts found.
    /// </summary>
    public bool ValidatePartialTypeGroup(PartialTypeGroup group, SemanticModel semanticModel)
    {
        var isValid = true;

        if (!group.IsPartial)
            return true;

        // Check for type parameter consistency
        var firstTypeParams = group.PartialParts[0].TypeParameters;
        for (int i = 1; i < group.PartialParts.Count; i++)
        {
            var currentTypeParams = group.PartialParts[i].TypeParameters;
            if (currentTypeParams.Length != firstTypeParams.Length)
            {
                var location = i < group.SyntaxNodes.Count ? group.SyntaxNodes[i].GetLocation() : null;
                _diagnostics.Error(
                    $"Partial type '{group.TypeSymbol.Name}' has inconsistent type parameters across parts",
                    location);
                isValid = false;
            }
        }

        // Check for accessibility consistency
        var firstAccessibility = group.PartialParts[0].DeclaredAccessibility;
        for (int i = 1; i < group.PartialParts.Count; i++)
        {
            var currentAccessibility = group.PartialParts[i].DeclaredAccessibility;
            if (currentAccessibility != firstAccessibility)
            {
                var location = i < group.SyntaxNodes.Count ? group.SyntaxNodes[i].GetLocation() : null;
                _diagnostics.Warning(
                    $"Partial type '{group.TypeSymbol.Name}' has inconsistent accessibility across parts. Using {firstAccessibility}.",
                    location);
            }
        }

        // Check for base class consistency
        var firstBaseType = group.PartialParts[0].BaseType;
        for (int i = 1; i < group.PartialParts.Count; i++)
        {
            var currentBaseType = group.PartialParts[i].BaseType;
            if (!SymbolEqualityComparer.Default.Equals(firstBaseType, currentBaseType))
            {
                var location = i < group.SyntaxNodes.Count ? group.SyntaxNodes[i].GetLocation() : null;
                _diagnostics.Error(
                    $"Partial type '{group.TypeSymbol.Name}' has different base classes across parts",
                    location);
                isValid = false;
            }
        }

        return isValid;
    }
}
