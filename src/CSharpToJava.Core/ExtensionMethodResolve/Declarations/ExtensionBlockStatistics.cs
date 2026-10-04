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

    /// <summary>M2: block operators materialized (one method per operator).</summary>
    public int OperatorsEmitted { get; set; }

    /// <summary>M2: block static methods materialized.</summary>
    public int StaticMethodsEmitted { get; set; }

    /// <summary>M2: block static property getters materialized.</summary>
    public int StaticPropertyGettersEmitted { get; set; }

    /// <summary>M2: block static property setters materialized.</summary>
    public int StaticPropertySettersEmitted { get; set; }

    public int MethodSitesRewritten { get; set; }
    public int PropertyReadSitesRewritten { get; set; }
    public int PropertyWriteSitesRewritten { get; set; }
    public int CompoundSitesRewritten { get; set; }

    /// <summary>M2: binary operator sites rewritten.</summary>
    public int OperatorBinarySitesRewritten { get; set; }

    /// <summary>M2: unary operator sites rewritten.</summary>
    public int OperatorUnarySitesRewritten { get; set; }

    /// <summary>M2: compound-assignment operator sites rewritten.</summary>
    public int OperatorCompoundSitesRewritten { get; set; }

    /// <summary>M2: type-name qualified static method sites rewritten.</summary>
    public int StaticMethodSitesRewritten { get; set; }

    /// <summary>M2: type-name qualified static property read sites rewritten.</summary>
    public int StaticPropertyReadSitesRewritten { get; set; }

    /// <summary>M2: type-name qualified static property write sites rewritten.</summary>
    public int StaticPropertyWriteSitesRewritten { get; set; }

    public int PropertyConditionalsLifted { get; set; }
    public int MethodConditionalsLifted { get; set; }
    public int ConditionalsDeferred { get; set; }

    public int IndexersRejected { get; set; }
    public int UnsupportedMembers { get; set; }
    public int SitesSkippedExpressionTree { get; set; }

    /// <summary>M2: blocks rejected for non-materializable receivers (CS2J1211).</summary>
    public int UnsupportedReceivers { get; set; }

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
        OperatorsEmitted += other.OperatorsEmitted;
        StaticMethodsEmitted += other.StaticMethodsEmitted;
        StaticPropertyGettersEmitted += other.StaticPropertyGettersEmitted;
        StaticPropertySettersEmitted += other.StaticPropertySettersEmitted;
        MethodSitesRewritten += other.MethodSitesRewritten;
        PropertyReadSitesRewritten += other.PropertyReadSitesRewritten;
        PropertyWriteSitesRewritten += other.PropertyWriteSitesRewritten;
        CompoundSitesRewritten += other.CompoundSitesRewritten;
        OperatorBinarySitesRewritten += other.OperatorBinarySitesRewritten;
        OperatorUnarySitesRewritten += other.OperatorUnarySitesRewritten;
        OperatorCompoundSitesRewritten += other.OperatorCompoundSitesRewritten;
        StaticMethodSitesRewritten += other.StaticMethodSitesRewritten;
        StaticPropertyReadSitesRewritten += other.StaticPropertyReadSitesRewritten;
        StaticPropertyWriteSitesRewritten += other.StaticPropertyWriteSitesRewritten;
        PropertyConditionalsLifted += other.PropertyConditionalsLifted;
        MethodConditionalsLifted += other.MethodConditionalsLifted;
        ConditionalsDeferred += other.ConditionalsDeferred;
        IndexersRejected += other.IndexersRejected;
        UnsupportedMembers += other.UnsupportedMembers;
        SitesSkippedExpressionTree += other.SitesSkippedExpressionTree;
        UnsupportedReceivers += other.UnsupportedReceivers;
        GateErrors += other.GateErrors;
        ProbeErrors += other.ProbeErrors;
    }

    /// <summary>One-line summary for verbose logging.</summary>
    public override string ToString()
        => $"blocks={BlocksMaterialized}/{BlocksScanned}, methods={MethodsEmitted}, "
           + $"getters={PropertyGettersEmitted}, setters={PropertySettersEmitted}, "
           + $"operators={OperatorsEmitted}, staticMethods={StaticMethodsEmitted}, "
           + $"staticGetters={StaticPropertyGettersEmitted}, staticSetters={StaticPropertySettersEmitted}, "
           + $"methodSites={MethodSitesRewritten}, readSites={PropertyReadSitesRewritten}, "
           + $"writeSites={PropertyWriteSitesRewritten}, compound={CompoundSitesRewritten}, "
           + $"opBinary={OperatorBinarySitesRewritten}, opUnary={OperatorUnarySitesRewritten}, "
           + $"opCompound={OperatorCompoundSitesRewritten}, "
           + $"staticMethodSites={StaticMethodSitesRewritten}, "
           + $"staticReadSites={StaticPropertyReadSitesRewritten}, "
           + $"staticWriteSites={StaticPropertyWriteSitesRewritten}, "
           + $"conditionals={PropertyConditionalsLifted + MethodConditionalsLifted}, "
           + $"deferred={ConditionalsDeferred}, indexersRejected={IndexersRejected}, "
           + $"unsupported={UnsupportedMembers}, unsupportedReceivers={UnsupportedReceivers}";
}
