using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CSharpToJava.Core.PartialMerge;

/// <summary>One part declaration within a partial type group.</summary>
internal sealed record TypePartInfo
{
    public required TypeDeclarationSyntax Declaration { get; init; }
    public required string SourceFilePath { get; init; }
    public required string RelativePath { get; init; }
    public required int Ordinal { get; init; }
}

/// <summary>Analysis/merge state for one partial type group (plan atom, design D9).</summary>
internal sealed record TypeGroupPlan
{
    public required string TypeFullName { get; init; }
    public required INamedTypeSymbol Symbol { get; init; }
    public required IReadOnlyList<TypePartInfo> Parts { get; init; }

    /// <summary>Deterministic primary part: relative-path ordinal order, then smallest span (D1).</summary>
    public TypePartInfo Primary => Parts[0];

    /// <summary>Assigned by MergeBuilder; null when the group is skipped/rolled back.</summary>
    public TypeDeclarationSyntax? MergedDeclaration { get; set; }

    public bool SkipBaseline { get; set; }
    public bool SkipUnsupportedShape { get; set; }
    public bool RolledBack { get; set; }

    /// <summary>
    /// Nested group enclosed by an inactive (skipped/rolled-back) containing
    /// group. A nested group emits no file edits itself — its merged node only
    /// ever rides a merged containing declaration — so it cannot be merged this
    /// run and must not count as active or trip the residual gate.
    /// </summary>
    public bool SkipEnclosingInactive { get; set; }

    /// <summary>
    /// M3: single-part type rewritten only to drop a classic partial declaration
    /// under AssumeNoGeneratedPartial. No banner/usings/part-removal semantics apply.
    /// </summary>
    public bool IsMemberOnlyRewrite { get; set; }

    public IEnumerable<string> TouchedFiles =>
        Parts.Select(part => part.SourceFilePath).Distinct(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One deterministic text edit; Span is the declaration FullSpan in the source file.</summary>
internal abstract record FileEdit
{
    public required string FilePath { get; init; }
    public required TextSpan Span { get; init; }
}

/// <summary>Primary part: replace the whole declaration (incl. leading trivia) with the merged declaration.</summary>
internal sealed record ReplaceTypeEdit : FileEdit
{
    public required string Replacement { get; init; }
}

/// <summary>Non-primary part: remove the declaration and leave the single-line provenance comment (D1).</summary>
internal sealed record RemoveTypeEdit : FileEdit
{
    public required string Comment { get; init; }
}

/// <summary>
/// Primary file: zero-length insertion of usings gathered from all part files so
/// merged attributes/member bodies resolve. Span owns the primary declaration
/// (adapter edit filter); the actual insertion point is InsertionPosition.
/// </summary>
internal sealed record InsertUsingsEdit : FileEdit
{
    public required int InsertionPosition { get; init; }
    public required string UsingsText { get; init; }
}

/// <summary>Whole-tree analysis result handed to the rewriter/adapter.</summary>
internal sealed record PartialMergePlan
{
    public required IReadOnlyList<TypeGroupPlan> Groups { get; init; }
    public required ILookup<string, FileEdit> EditsByFile { get; init; }
    public required IReadOnlyList<PartialMergeDiagnostic> Diagnostics { get; init; }
    public required PartialMergeStatistics Statistics { get; init; }

    public IEnumerable<TypeGroupPlan> ActiveGroups =>
        Groups.Where(g => !g.SkipBaseline && !g.SkipUnsupportedShape
            && !g.RolledBack && !g.SkipEnclosingInactive);
}
