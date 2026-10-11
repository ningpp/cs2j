using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.PartialMerge;

/// <summary>
/// Symbol-driven collection of multi-part type declarations (design §4.1). Walks
/// GlobalNamespace recursively; only types with ≥2 source DeclaringSyntaxReferences
/// become groups. Single-part types cost nothing; the implicit class synthesized
/// for top-level statements is skipped (design D8).
/// </summary>
internal static class PartialTypePartCollector
{
    public static (List<TypeGroupPlan> MultiPartGroups, List<TypeGroupPlan> SinglePartTypes,
            int TopLevelSkipped, int TypesScanned)
        Collect(
        CSharpCompilation compilation,
        string sourceRoot)
    {
        var groups = new List<TypeGroupPlan>();
        var singles = new List<TypeGroupPlan>();
        var topLevelSkipped = 0;
        var typesScanned = 0;

        VisitNamespace(compilation.GlobalNamespace);

        return (groups, singles, topLevelSkipped, typesScanned);

        void VisitNamespace(INamespaceSymbol namespaceSymbol)
        {
            foreach (var type in namespaceSymbol.GetTypeMembers())
            {
                VisitType(type);
            }

            foreach (var nested in namespaceSymbol.GetNamespaceMembers())
            {
                VisitNamespace(nested);
            }
        }

        void VisitType(INamedTypeSymbol type)
        {
            // Top-level statements synthesize an implicit Program container
            // (D8). IsImplicitClass is the historical marker, but across Roslyn
            // versions the synthesized class may report IsImplicitClass=false;
            // the authoritative shape is a DeclaringSyntaxReference whose node
            // is the CompilationUnit itself (a real type always has a
            // TypeDeclarationSyntax node).
            if (type.IsImplicitClass || HasCompilationUnitDeclaration(type))
            {
                topLevelSkipped++;
                return;
            }

            foreach (var nestedType in type.GetTypeMembers())
            {
                VisitType(nestedType);
            }

            // Only real source trees under the source root participate; the
            // synthesized global-usings tree (synthetic path) is excluded.
            //
            // Partial merge only ever unites TypeDeclarationSyntax parts
            // (class/struct/interface/record). Enums and delegates declare
            // through EnumDeclarationSyntax/DelegateDeclarationSyntax — both
            // are BaseTypeDeclarationSyntax, not TypeDeclarationSyntax — and
            // neither can be partial in C#. The node-kind filter here is the
            // authoritative guard: the parts below are stored and cast as
            // TypeDeclarationSyntax throughout the analyzer/merge pipeline.
            var sourceReferences = type.DeclaringSyntaxReferences
                .Where(reference =>
                    reference.SyntaxTree.FilePath.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase)
                    && !reference.SyntaxTree.FilePath.Contains("__cs2j", StringComparison.Ordinal))
                .Select(reference => (Reference: reference, Node: reference.GetSyntax() as TypeDeclarationSyntax))
                .Where(part => part.Node is not null)
                .ToList();

            if (sourceReferences.Count == 0)
            {
                return;
            }

            typesScanned++;

            if (sourceReferences.Count == 1)
            {
                // M3: a single-part type is only rewritten when AssumeNoGeneratedPartial
                // removes a classic declaration; the analyzer decides whether to keep it.
                var (reference, singleNode) = sourceReferences[0];
                singles.Add(new TypeGroupPlan
                {
                    TypeFullName = type.ToDisplayString(),
                    Symbol = type,
                    Parts =
                    [
                        new TypePartInfo
                        {
                            Declaration = singleNode!,
                            SourceFilePath = reference.SyntaxTree.FilePath,
                            RelativePath = Path.GetRelativePath(sourceRoot, reference.SyntaxTree.FilePath)
                                .Replace('\\', '/'),
                            Ordinal = 0,
                        },
                    ],
                });
                return;
            }

            // D1 two-key determinism: relative path ordinal asc, then span.Start asc.
            var orderedParts = sourceReferences
                .Select(part =>
                {
                    var reference = part.Reference;
                    return new
                    {
                        Node = part.Node!,
                        FilePath = reference.SyntaxTree.FilePath,
                        RelativePath = Path.GetRelativePath(sourceRoot, reference.SyntaxTree.FilePath)
                            .Replace('\\', '/'),
                    };
                })
                .OrderBy(part => part.RelativePath, StringComparer.Ordinal)
                .ThenBy(part => part.Node.SpanStart)
                .Select((part, index) => new TypePartInfo
                {
                    Declaration = part.Node,
                    SourceFilePath = part.FilePath,
                    RelativePath = part.RelativePath,
                    Ordinal = index,
                })
                .ToList();

            groups.Add(new TypeGroupPlan
            {
                TypeFullName = type.ToDisplayString(),
                Symbol = type,
                Parts = orderedParts,
            });
        }
    }

    /// <summary>
    /// True when any declaring syntax reference of <paramref name="type"/>
    /// resolves to the compilation unit itself (the synthesized top-level
    /// statements Program container) rather than a type declaration node.
    /// </summary>
    private static bool HasCompilationUnitDeclaration(INamedTypeSymbol type)
    {
        foreach (var reference in type.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is CompilationUnitSyntax)
            {
                return true;
            }
        }

        return false;
    }
}
