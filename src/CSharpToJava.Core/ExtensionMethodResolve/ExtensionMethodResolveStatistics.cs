using System.Linq;

namespace CSharpToJava.Core.ExtensionMethodResolve;

/// <summary>
/// Counters for the extension-method-resolve pass (adapter merges per-file
/// results; design §4.1).
/// </summary>
public sealed class ExtensionMethodResolveStatistics
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesClean;

    /// <summary>Invocations whose symbol binds as <c>MethodKind.ReducedExtension</c>.</summary>
    public int CallsitesBoundReduced;

    /// <summary>Reduced call sites actually rewritten to explicit static calls.</summary>
    public int CallsitesRewritten;

    /// <summary>Rewritten sites whose host type required type arguments (EM-GEN-HOST, nested generic host).</summary>
    public int GenericHostQualified;

    /// <summary>Sites whose receiver parameter carried ref/in (EM-REF).</summary>
    public int RefReceiverSites;

    /// <summary>ref/in receiver sites for which the ref/in argument modifier was actually emitted.</summary>
    public int RefReceiverSitesEmitted;

    /// <summary>Null-conditional chains (<c>r?.M()</c>) lifted into temps + conditional static calls (EM-COND).</summary>
    public int ConditionalChainsLifted;

    /// <summary>Null-conditional chains left on CS2J1205/assert-only (EM-COND).</summary>
    public int ConditionalChainsDeferred;

    /// <summary>Reduced method groups rewritten into capture lambdas (EM-GROUP).</summary>
    public int GroupSitesRewritten;

    /// <summary>Reduced method groups left on CS2J1207 (EM-GROUP).</summary>
    public int GroupSitesDeferred;

    /// <summary>Reduced LINQ-host sites skipped untouched (EM-LINQ).</summary>
    public int LinqOwnedSkipped;

    /// <summary>Bound reduced sites whose host comes from metadata (EM-EXTERNAL).</summary>
    public int ExternalHostSites;

    /// <summary>External-host sites with a known Java landing (TypeMappings hit, silent).</summary>
    public int ExternalHostMappedSites;

    /// <summary>External-host sites without a Java landing (CS2J1203 reported).</summary>
    public int ExternalHostUnmappedSites;

    /// <summary>Bound reduced sites whose host is source inside the merged compilation (user + support).</summary>
    public int InCompilationHostSites;

    /// <summary>Subset of <see cref="InCompilationHostSites"/> hosted by injected support libraries.</summary>
    public int InCompilationSupportHostSites;

    /// <summary>User-host subset (<see cref="InCompilationHostSites"/> minus support).</summary>
    public int InCompilationUserHostSites;

    /// <summary>Extension-like sites conservatively skipped in single-file mode (CS2J1290).</summary>
    public int SingleFileDeferredSites;

    /// <summary>Rewritten sites located inside an Expression&lt;&gt; lambda (EM-EXPRTREE).</summary>
    public int ExpressionTreeSitesChecked;

    /// <summary>Expression-tree sites rolled back to the sugar after the identity assertion failed (CS2J1210).</summary>
    public int ExpressionTreeRollbacks;

    /// <summary>
    /// M4 EM-LINQ: user-extension sites on a member-access chain that also
    /// contains a LINQ-owned call; the whole chain is conservatively left as
    /// sugar (EM001 per site).
    /// </summary>
    public int MixedLinqChainSites;

    /// <summary>M4: bound reduced sites not selected because the host is overridden to skip.</summary>
    public int HostOverrideSitesSkipped;

    /// <summary>M4: sites explicitly rewritten because the host is overridden to force.</summary>
    public int HostOverrideSitesForced;

    /// <summary>M4 §4.4-4: files whose whole rewrite set was replayed from the original copy.</summary>
    public int FilesRolledBack;

    /// <summary>M4 gate 5 (§4.5): extension-method declarations carrying the <c>this</c> modifier before the pass.</summary>
    public int ThisModifierBaseline;

    /// <summary>M4 gate 5: <c>this</c>-modified first parameters still present in the post-rewrite tree.</summary>
    public int ThisModifierPreserved;

    /// <summary>Skip/undecidable sites bucketed by reason code (1201-1207/1210...).</summary>
    public Dictionary<string, int> SitesSkippedByReason { get; } = new(StringComparer.Ordinal);

    /// <summary>Residual gate findings bucketed by rule name (design §4.5).</summary>
    public Dictionary<string, int> ResidualsByRule { get; } = new(StringComparer.Ordinal);

    public void Merge(ExtensionMethodResolveStatistics source)
    {
        FilesScanned += source.FilesScanned;
        FilesTransformed += source.FilesTransformed;
        FilesClean += source.FilesClean;
        CallsitesBoundReduced += source.CallsitesBoundReduced;
        CallsitesRewritten += source.CallsitesRewritten;
        GenericHostQualified += source.GenericHostQualified;
        RefReceiverSites += source.RefReceiverSites;
        RefReceiverSitesEmitted += source.RefReceiverSitesEmitted;
        ConditionalChainsLifted += source.ConditionalChainsLifted;
        ConditionalChainsDeferred += source.ConditionalChainsDeferred;
        GroupSitesRewritten += source.GroupSitesRewritten;
        GroupSitesDeferred += source.GroupSitesDeferred;
        LinqOwnedSkipped += source.LinqOwnedSkipped;
        ExternalHostSites += source.ExternalHostSites;
        ExternalHostMappedSites += source.ExternalHostMappedSites;
        ExternalHostUnmappedSites += source.ExternalHostUnmappedSites;
        InCompilationHostSites += source.InCompilationHostSites;
        InCompilationSupportHostSites += source.InCompilationSupportHostSites;
        InCompilationUserHostSites += source.InCompilationUserHostSites;
        SingleFileDeferredSites += source.SingleFileDeferredSites;
        ExpressionTreeSitesChecked += source.ExpressionTreeSitesChecked;
        ExpressionTreeRollbacks += source.ExpressionTreeRollbacks;
        MixedLinqChainSites += source.MixedLinqChainSites;
        HostOverrideSitesSkipped += source.HostOverrideSitesSkipped;
        HostOverrideSitesForced += source.HostOverrideSitesForced;
        FilesRolledBack += source.FilesRolledBack;
        ThisModifierBaseline += source.ThisModifierBaseline;
        ThisModifierPreserved += source.ThisModifierPreserved;
        MergeCounters(SitesSkippedByReason, source.SitesSkippedByReason);
        MergeCounters(ResidualsByRule, source.ResidualsByRule);
    }

    public string SummaryLine()
        => $"extension-methods: {CallsitesRewritten} rewritten, {FilesClean} clean " +
           $"({CallsitesBoundReduced} reduced bound, {LinqOwnedSkipped} LINQ-owned skipped, " +
           $"{InCompilationUserHostSites} user + {InCompilationSupportHostSites} support in-compilation, " +
           $"{ExternalHostSites} external hosts [{ExternalHostMappedSites} mapped/{ExternalHostUnmappedSites} unmapped], " +
           $"{RefReceiverSitesEmitted}/{RefReceiverSites} ref/in receivers, " +
           $"{ConditionalChainsLifted} null-conditional lifted, {GroupSitesRewritten} groups rewritten, " +
           $"{ExpressionTreeSitesChecked} in expression trees, {ExpressionTreeRollbacks} ET rollbacks, " +
           $"{SingleFileDeferredSites} single-file deferred, " +
           $"{MixedLinqChainSites} mixed-LINQ chain sites conservatively skipped, " +
           $"{HostOverrideSitesSkipped}/{HostOverrideSitesForced} host override skip/force, " +
           $"{FilesRolledBack} files rolled back, " +
           $"{SitesSkippedByReason.Values.Sum()} skipped, " +
           $"{ResidualsByRule.Values.Sum()} residuals)";

    private static void MergeCounters(Dictionary<string, int> target, Dictionary<string, int> source)
    {
        foreach (var (key, value) in source)
        {
            target.TryGetValue(key, out var existing);
            target[key] = existing + value;
        }
    }
}
