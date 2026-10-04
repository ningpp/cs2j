namespace CSharpToJava.Core.ExtensionMethodResolve.Declarations;

/// <summary>Per-pass counters for C49 extension-block materialization.</summary>
public sealed class ExtensionBlockStatistics
{
    public int FilesScanned { get; set; }
    public int FilesTransformed { get; set; }

    public int BlocksScanned { get; set; }
    public int BlocksMaterialized { get; set; }

    public int MethodsEmitted { get; set; }
    public int PropertyGettersEmitted { get; set; }
    public int PropertySettersEmitted { get; set; }

    public int MethodSitesRewritten { get; set; }
    public int PropertyReadSitesRewritten { get; set; }
    public int PropertyWriteSitesRewritten { get; set; }
    public int CompoundSitesRewritten { get; set; }

    public int PropertyConditionalsLifted { get; set; }
    public int MethodConditionalsLifted { get; set; }
    public int ConditionalsDeferred { get; set; }

    public int IndexersRejected { get; set; }
    public int UnsupportedMembers { get; set; }
    public int SitesSkippedExpressionTree { get; set; }

    public int GateErrors { get; set; }
    public int ProbeErrors { get; set; }

    /// <summary>Merge counters from one file into this pass aggregate.</summary>
    public void Merge(ExtensionBlockStatistics other)
    {
        ArgumentNullException.ThrowIfNull(other);

        FilesScanned += other.FilesScanned;
        FilesTransformed += other.FilesTransformed;
        BlocksScanned += other.BlocksScanned;
        BlocksMaterialized += other.BlocksMaterialized;
        MethodsEmitted += other.MethodsEmitted;
        PropertyGettersEmitted += other.PropertyGettersEmitted;
        PropertySettersEmitted += other.PropertySettersEmitted;
        MethodSitesRewritten += other.MethodSitesRewritten;
        PropertyReadSitesRewritten += other.PropertyReadSitesRewritten;
        PropertyWriteSitesRewritten += other.PropertyWriteSitesRewritten;
        CompoundSitesRewritten += other.CompoundSitesRewritten;
        PropertyConditionalsLifted += other.PropertyConditionalsLifted;
        MethodConditionalsLifted += other.MethodConditionalsLifted;
        ConditionalsDeferred += other.ConditionalsDeferred;
        IndexersRejected += other.IndexersRejected;
        UnsupportedMembers += other.UnsupportedMembers;
        SitesSkippedExpressionTree += other.SitesSkippedExpressionTree;
        GateErrors += other.GateErrors;
        ProbeErrors += other.ProbeErrors;
    }

    /// <summary>One-line summary for verbose logging.</summary>
    public override string ToString()
        => $"blocks={BlocksMaterialized}/{BlocksScanned}, methods={MethodsEmitted}, "
           + $"getters={PropertyGettersEmitted}, setters={PropertySettersEmitted}, "
           + $"methodSites={MethodSitesRewritten}, readSites={PropertyReadSitesRewritten}, "
           + $"writeSites={PropertyWriteSitesRewritten}, compound={CompoundSitesRewritten}, "
           + $"conditionals={PropertyConditionalsLifted + MethodConditionalsLifted}, "
           + $"deferred={ConditionalsDeferred}, indexersRejected={IndexersRejected}, "
           + $"unsupported={UnsupportedMembers}";
}
