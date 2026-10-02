namespace CSharpToJava.Core.ExpressionDesugar.Interpolations;

/// <summary>
/// C23 section statistics (design §6). The five common file counters,
/// <see cref="TempsLifted"/> and <see cref="ResidualCount"/> mirror the family
/// object; the family entry mirrors file outcomes into this section only when
/// the Interpolations rule is active (C23 disabled => zero difference).
/// </summary>
public sealed class InterpolationStatistics
{
    // ---- Common five file fields + family temps ----

    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int FilesDirtyNoRewrite;
    public int ResidualCount;
    public int TempsLifted;

    // ---- Sites / paths ----

    /// <summary>Total interpolated string sites visited by the rule.</summary>
    public int InterpolationSites;

    /// <summary>Sites expanded to string.Concat (CS2J0501).</summary>
    public int ConcatExpansions;

    /// <summary>Sites folded to a bare literal (CS2J0503).</summary>
    public int ConstantsFolded;

    /// <summary>Sites expanded to one string.Format call (CS2J0502, IE-FORMAT).</summary>
    public int FormatExpansions;

    /// <summary>
    /// Format sub-bucket: at least one hole carried an alignment clause
    /// (one expansion may increment several sub-buckets).
    /// </summary>
    public int FormatByAlignment;

    /// <summary>Format sub-bucket: at least one hole carried a format clause.</summary>
    public int FormatByFormatClause;

    /// <summary>
    /// Format sub-bucket: at least one hole statically implements
    /// System.IFormattable and is NOT on the Concat whitelist (whole-site
    /// upgrade so current-culture provider dispatch is preserved).
    /// </summary>
    public int FormatByIFormattableUpgrade;

    /// <summary>Sites nested inside another interpolation (depth &gt; 0).</summary>
    public int NestedInterpolationSites;

    // ---- Hole wrapping ----

    /// <summary>Holes wrapped for unsigned semantics (x.ToString()/((uint)x).ToString()).</summary>
    public int UnsignedHoleWraps;

    /// <summary>Proven-safe ref struct holes wrapped as x.ToString().</summary>
    public int RefStructToStringWraps;

    // ---- Gate outcomes ----

    /// <summary>Custom handler bindings (G0; Error/Keep disposition in SkippedByReason/diagnostics).</summary>
    public int CustomHandlerSites;

    /// <summary>FormattableString/IFormattable sites kept (CS2J0511).</summary>
    public int KeptFormattableStringSites;

    /// <summary>Sites inside Expression&lt;T&gt; lambdas skipped (CS2J0515).</summary>
    public int ExpressionTreeSitesSkipped;

    /// <summary>Sites rejected for a non-materializable ref struct/pointer hole (CS2J0513).</summary>
    public int RefStructOrPointerRejections;

    /// <summary>Sites skipped/kept by diagnostic code (code -> count).</summary>
    public readonly Dictionary<string, int> SkippedByReason = new();

    /// <summary>Interpolation nodes left in the output without a paired explanation (CS2J0516).</summary>
    public int ResidualInterpolationCount;

    public void AddSkip(string code)
    {
        SkippedByReason.TryGetValue(code, out var count);
        SkippedByReason[code] = count + 1;
    }

    public void MergeFrom(InterpolationStatistics other)
    {
        FilesScanned += other.FilesScanned;
        FilesTransformed += other.FilesTransformed;
        FilesSkippedClean += other.FilesSkippedClean;
        FilesDirtyNoRewrite += other.FilesDirtyNoRewrite;
        ResidualCount += other.ResidualCount;
        TempsLifted += other.TempsLifted;
        InterpolationSites += other.InterpolationSites;
        ConcatExpansions += other.ConcatExpansions;
        ConstantsFolded += other.ConstantsFolded;
        FormatExpansions += other.FormatExpansions;
        FormatByAlignment += other.FormatByAlignment;
        FormatByFormatClause += other.FormatByFormatClause;
        FormatByIFormattableUpgrade += other.FormatByIFormattableUpgrade;
        NestedInterpolationSites += other.NestedInterpolationSites;
        UnsignedHoleWraps += other.UnsignedHoleWraps;
        RefStructToStringWraps += other.RefStructToStringWraps;
        CustomHandlerSites += other.CustomHandlerSites;
        KeptFormattableStringSites += other.KeptFormattableStringSites;
        ExpressionTreeSitesSkipped += other.ExpressionTreeSitesSkipped;
        RefStructOrPointerRejections += other.RefStructOrPointerRejections;
        ResidualInterpolationCount += other.ResidualInterpolationCount;
        foreach (var pair in other.SkippedByReason)
        {
            SkippedByReason.TryGetValue(pair.Key, out var count);
            SkippedByReason[pair.Key] = count + pair.Value;
        }
    }
}
