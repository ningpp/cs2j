namespace CSharpToJava.Core.PartialMerge;

/// <summary>
/// Baseline-error view for a rewritten file (design §4.1 / §6, D9).
/// <para>
/// The merge moves members — and with them their method bodies — from every
/// non-primary part into the primary file. A pre-existing error reported in a
/// part file therefore reappears at the merged declaration's location in the
/// primary file, and a baseline keyed per file alone grades it as new: it either
/// rolls the group back or, when it cannot be attributed to a single group,
/// rolls the whole tree back (CS2J2002).
/// </para>
/// <para>
/// Real SDK example: a
/// <c>#if SYSTEM_TEXT_REGULAREXPRESSIONS</c>-gated method body in
/// <c>SymbolicRegexMatcher.cs</c> calls a member declared in the same gated
/// region of <c>RegexFindOptimizations.cs</c>. The pass' whole-tree parse
/// options define only <c>NETCOREAPP</c>, so the region is absent from both the
/// baseline and the candidate compilation and the body already reports CS1061
/// at baseline; merging <c>SymbolicRegexMatcher.Automata.cs</c> (path order
/// makes it primary) relocates that body and the error into the primary file.
/// </para>
/// <para>
/// The effective baseline of a primary file is therefore the union of its own
/// pre-existing errors and those of all its group's part files. Non-primary
/// files only lose the merged type node; the rest of their content is untouched,
/// so they keep their own view. Nested groups need no special case: a nested
/// type can only be declared inside its containing type's parts, so the
/// containing group's parts already cover every file the nested members came
/// from.
/// </para>
/// </summary>
internal static class PartialMergeBaseline
{
    /// <summary>
    /// Effective baseline keys for one rewritten file. <paramref name="keysForFile"/>
    /// receives a source file path (the same paths the plan's parts carry) and
    /// returns that file's ORIGINAL error keys.
    /// </summary>
    public static HashSet<(string Id, string Message)> ForRewrittenFile(
        PartialMergePlan plan,
        string filePath,
        Func<string, HashSet<(string Id, string Message)>> keysForFile)
    {
        var keys = new HashSet<(string Id, string Message)>(keysForFile(filePath));
        foreach (var group in plan.Groups)
        {
            if (group.MergedDeclaration is null
                || !string.Equals(
                    group.Primary.SourceFilePath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var part in group.Parts)
            {
                keys.UnionWith(keysForFile(part.SourceFilePath));
            }
        }

        return keys;
    }
}
