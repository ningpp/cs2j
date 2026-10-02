using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.PartialMerge;

/// <summary>
/// Whole-tree analysis → PartialMergePlan (design §4.1). M1: collect groups,
/// build merged declarations depth-first (so nested merged nodes exist when the
/// containing type is built), emit one Replace edit for the primary and one
/// Remove edit per other part. Baseline snapshot/skip, CS2J2016 shape gates and
/// recompile-diff attribution arrive in M2.
/// </summary>
internal sealed class PartialMergeAnalyzer
{
    public PartialMergePlan Analyze(
        CSharpCompilation compilation,
        PartialMergeOptions options,
        string sourceRoot)
    {
        var diagnostics = new List<PartialMergeDiagnostic>();
        var (groups, topLevelSkipped) = PartialTypePartCollector.Collect(compilation, sourceRoot);

        // Static lookup is per-analysis; reset so successive runs in one process
        // never see a previous tree's aliases.
        PartialMemberMerge.NestedOverrides.Clear();

        // Build deepest-first; after a group is built, register each part's text
        // as an alias for its merged node so containing types (built afterwards)
        // replace the nested part members. Switch to annotation keys if real
        // fixtures produce text-key collisions.
        foreach (var group in groups
                     .OrderByDescending(g => g.TypeFullName.Count(c => c == '.')))
        {
            var merged = PartialTypeMergeBuilder.Build(compilation, group);
            group.MergedDeclaration = merged;

            foreach (var part in group.Parts)
            {
                // Primary slot carries the merged node inside the containing type;
                // non-primary nested parts are removed from it.
                PartialMemberMerge.NestedOverrides.TryAdd(
                    part.Declaration.ToFullString().Trim(),
                    new NestedOverride(merged, part.Ordinal != 0));
            }
        }

        // Nested groups emit no file-level edits: their merged nodes live inside
        // the containing merged declaration (member merge), and whole containing
        // declarations in other part files are removed anyway. Emitting edits for
        // them too would overlap the containing group's spans.
        var allPartNodes = groups
            .SelectMany(group => group.Parts.Select(part => part.Declaration))
            .ToHashSet();

        var edits = new List<FileEdit>();
        foreach (var group in groups)
        {
            if (group.MergedDeclaration is null)
            {
                continue;
            }

            var isNested = group.Primary.Declaration.Ancestors()
                .OfType<TypeDeclarationSyntax>()
                .Any(allPartNodes.Contains);
            if (isNested)
            {
                continue;
            }

            var primary = group.Primary;
            edits.Add(new ReplaceTypeEdit
            {
                FilePath = primary.SourceFilePath,
                Span = primary.Declaration.FullSpan,
                Replacement = group.MergedDeclaration.ToFullString(),
            });

            foreach (var otherPart in group.Parts.Skip(1))
            {
                edits.Add(new RemoveTypeEdit
                {
                    FilePath = otherPart.SourceFilePath,
                    Span = otherPart.Declaration.FullSpan,
                    Comment = "// cs2j-partial-merge: part of '" + group.TypeFullName +
                              "' merged into '" + primary.RelativePath + "'",
                });
            }
        }

        return new PartialMergePlan
        {
            Groups = groups,
            EditsByFile = edits.ToLookup(edit => edit.FilePath, StringComparer.OrdinalIgnoreCase),
            Diagnostics = diagnostics,
        };
    }
}
