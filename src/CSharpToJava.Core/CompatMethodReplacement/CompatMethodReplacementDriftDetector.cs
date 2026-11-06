namespace CSharpToJava.Core.CompatMethodReplacement;

/// <summary>
/// W-09 drift detection (upgrade plan §6.4-R0): a registered entry whose
/// DECLARING TYPE exists in the processed corpus (symbol level) but whose
/// signature matched nothing means the library drifted away from the table —
/// historically this no-op'ed silently (the rt10 <c>ToUtf8</c> 3-param
/// char-span rewrite was invisible for exactly this reason). The detector is
/// family-aware: entries of one family (same namespace/type/method — e.g. the
/// rt10 3-param form plus the legacy 3.x 4-param form) count as healthy when
/// ANY sibling matched, so intentionally retained legacy signatures do not
/// trip the gate.
/// </summary>
public static class CompatMethodReplacementDriftDetector
{
    /// <summary>
    /// Returns the entries whose family matched nothing while their declaring
    /// type WAS observed in the corpus. The caller reports each as an
    /// Error-severity CMR020 diagnostic.
    /// </summary>
    /// <param name="registry">The replacement table in use.</param>
    /// <param name="matchedEntryKeys">Entry keys that matched at least one declaration.</param>
    /// <param name="declaredTypeKeys">
    /// Normalized <c>namespace|type</c> keys observed by the rewriter (see
    /// <see cref="CompatMethodReplacementStatistics.DeclaredTypeKeys"/>).
    /// </param>
    public static IReadOnlyList<CompatMethodReplacementEntry> DetectDriftedEntries(
        CompatMethodReplacementRegistry registry,
        IReadOnlyCollection<string> matchedEntryKeys,
        IReadOnlyCollection<string> declaredTypeKeys)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(matchedEntryKeys);
        ArgumentNullException.ThrowIfNull(declaredTypeKeys);

        var drifted = new List<CompatMethodReplacementEntry>();
        foreach (var family in registry.Entries.GroupBy(
                     entry => entry.FamilyKey, StringComparer.Ordinal))
        {
            if (family.Any(entry => matchedEntryKeys.Contains(entry.Key)))
                continue;

            if (!declaredTypeKeys.Contains(family.First().DeclaredTypeKey))
                continue;

            // The declaring type exists but no sibling signature matched: the
            // corpus's version of this method is unknown to the table.
            drifted.AddRange(family);
        }

        return drifted;
    }
}
