namespace CSharpToJava.Core.DefaultParameterStripper.ParamsCollections;

/// <summary>
/// Statistics for the C48 params-collections rules (design §5-3). Field names
/// follow the design so the CLI merge flow (T19) can add them to the existing
/// strip-default statistics merge without renaming.
/// </summary>
public sealed class ParamsStatistics
{
    /// <summary>
    /// Params declaration sites, bucketed by kind name
    /// (Array/Span/Builder/Initializable/Interface/Unsupported — the
    /// <see cref="ParamsParamKind"/> member names).
    /// </summary>
    public Dictionary<string, int> ParamsSites { get; } = new(StringComparer.Ordinal);

    /// <summary>Form-A call sites rewritten into an explicit collection construction.</summary>
    public int ParamsCallSitesExpanded;

    /// <summary>Form-C call sites (empty argument list) rewritten.</summary>
    public int ParamsCallSitesEmpty;

    /// <summary>Form-B call sites passed through (single collection argument, identity preserved).</summary>
    public int ParamsCallSitesPassthrough;

    /// <summary>Strategy-B declarations materialized (params modifier stripped).</summary>
    public int ParamsDeclarationsMaterialized;

    /// <summary>Classic array (K1) call sites explicitized — only when ExpandArrayCallSites is on.</summary>
    public int ArrayCallSitesExpanded;

    /// <summary>K2 Span sites whose expansion was emitted as an array (0604).</summary>
    public int SpanAsArraySites;

    /// <summary>K3 builder sites blocked with CS2J0607 (M2: only the residual unbindable path).</summary>
    public int BuilderSitesBlocked;

    /// <summary>M2 K3 sites emitted through the IOperation-bound builder factory (B.Create(new T[]{…})).</summary>
    public int BuilderFactorySites;

    /// <summary>M2 K4 sites whose instance Add was resolved through real compiler/symbol binding (PC-3).</summary>
    public int InitializerAddBoundSites;

    /// <summary>Skip counters keyed by the CS2J06xx diagnostic code that caused the skip.</summary>
    public Dictionary<string, int> SkippedByReason { get; } = new(StringComparer.Ordinal);

    /// <summary>Residual-gate failures (CS2J0619).</summary>
    public int ResidualCount;

    public void RecordSite(ParamsParamKind kind)
    {
        var key = kind.ToString();
        ParamsSites[key] = ParamsSites.TryGetValue(key, out var count) ? count + 1 : 1;
    }

    public void RecordSkipped(string diagnosticCode)
    {
        SkippedByReason[diagnosticCode] = SkippedByReason.TryGetValue(diagnosticCode, out var count)
            ? count + 1
            : 1;
    }

    /// <summary>Adds another run's counters into this one (per-tree/per-strip accumulation).</summary>
    public void Merge(ParamsStatistics other)
    {
        foreach (var (key, count) in other.ParamsSites)
        {
            ParamsSites[key] = ParamsSites.TryGetValue(key, out var current) ? current + count : count;
        }

        ParamsCallSitesExpanded += other.ParamsCallSitesExpanded;
        ParamsCallSitesEmpty += other.ParamsCallSitesEmpty;
        ParamsCallSitesPassthrough += other.ParamsCallSitesPassthrough;
        ParamsDeclarationsMaterialized += other.ParamsDeclarationsMaterialized;
        ArrayCallSitesExpanded += other.ArrayCallSitesExpanded;
        SpanAsArraySites += other.SpanAsArraySites;
        BuilderSitesBlocked += other.BuilderSitesBlocked;
        BuilderFactorySites += other.BuilderFactorySites;
        InitializerAddBoundSites += other.InitializerAddBoundSites;

        foreach (var (key, count) in other.SkippedByReason)
        {
            SkippedByReason[key] = SkippedByReason.TryGetValue(key, out var current) ? current + count : count;
        }

        ResidualCount += other.ResidualCount;
    }
}
