using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.PartialMerge;

/// <summary>
/// Member collection and (M3) partial-pair collapse (design §4.5). M1 policy:
/// concatenate members in "part order × declaration order" with NO text-key
/// dedup — the legacy MergedTypeDeclaration text-key behavior (int vs
/// System.Int32 mismatch, first-wins) is deliberately not revived. Partial
/// method/property pairs therefore survive as two members in v1.
/// </summary>
internal static class PartialMemberMerge
{
    public static IEnumerable<MemberDeclarationSyntax> CollectMembers(TypeGroupPlan group)
    {
        foreach (var part in group.Parts)
        {
            foreach (var member in part.Declaration.Members)
            {
                // Nested partial types that are themselves groups are replaced by
                // their merged node (lookup supplied by the analyzer, depth-first).
                if (NestedOverrides.TryGetValue(member.ToFullString().Trim(), out var nested))
                {
                    // Non-primary part of a nested group: the merged node is emitted
                    // once at the nested primary's slot; this member disappears.
                    if (nested.Remove)
                    {
                        continue;
                    }

                    yield return nested.Merged;
                }
                else
                {
                    yield return member;
                }
            }
        }
    }

    /// <summary>
    /// Keyed (for M1) by the nested declaration's trimmed full text; analyzer fills
    /// it depth-first. If collisions appear in real fixtures, switch to an
    /// annotation-based key before M2.
    /// </summary>
    public static Dictionary<string, NestedOverride> NestedOverrides { get; }
        = new(StringComparer.Ordinal);
}

/// <summary>
/// What to do with a member that is a part of a nested partial group.
/// Remove marks the non-primary parts (merged node is emitted at the primary slot).
/// </summary>
internal sealed record NestedOverride(TypeDeclarationSyntax Merged, bool Remove);
