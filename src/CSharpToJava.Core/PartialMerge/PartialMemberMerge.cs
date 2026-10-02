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
                if (NestedOverrides.TryGetValue(member.ToFullString().Trim(), out var replacement))
                {
                    yield return replacement;
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
    public static Dictionary<string, TypeDeclarationSyntax> NestedOverrides { get; }
        = new(StringComparer.Ordinal);
}
