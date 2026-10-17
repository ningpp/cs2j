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
        string sourceRoot,
        bool nameSinglePartTypes)
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
            // Top-level statements synthesize an implicit Program container (D8);
            // it is skipped before its (local, never-partial) members are walked.
            if (type.IsImplicitClass)
            {
                topLevelSkipped++;
                return;
            }

            foreach (var nestedType in type.GetTypeMembers())
            {
                VisitType(nestedType);
            }

            var declaringReferences = type.DeclaringSyntaxReferences;
            if (declaringReferences.Length == 0)
            {
                return;
            }

            // Whole-cluster skip without GetSyntax: a multi-part group needs at least
            // two declaring references, and the single-part list is read only under
            // M3, so a lone declaration can neither form a group nor a needed single
            // and its node — the expensive half of the walk — is never resolved. A
            // node that is not a TypeDeclarationSyntax (e.g. the CompilationUnit an
            // implicit top-level container would name) cannot be a part and is
            // dropped by the cast below regardless.
            if (declaringReferences.Length <= 1 && !nameSinglePartTypes)
            {
                return;
            }

            var references = declaringReferences
                .Select(reference => (Reference: reference, Node: reference.GetSyntax()))
                .ToList();

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
            var sourceReferences = references
                .Where(item =>
                    item.Reference.SyntaxTree.FilePath.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase)
                    && !item.Reference.SyntaxTree.FilePath.Contains("__cs2j", StringComparison.Ordinal))
                .Select(item => (item.Reference, Node: item.Node as TypeDeclarationSyntax))
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
                    // A single-part type only matters under M3, so its display name
                    // (an expensive symbol formatting for every type in the tree) is
                    // produced only when M3 is armed.
                    TypeFullName = nameSinglePartTypes ? type.ToDisplayString() : string.Empty,
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
}
