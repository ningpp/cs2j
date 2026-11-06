namespace CSharpToJava.Core.LinqChannel;

/// <summary>Severity levels used by channel diagnostics (independent of Roslyn severities).</summary>
public enum LinqChannelDiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// One channel diagnostic (design §6). The adapter renders these as MSBuild-style
/// lines and folds them into summary/diagnostic hooks; the JSON report keeps
/// the structured form.
/// </summary>
public sealed record LinqChannelDiagnostic(
    LinqChannelDiagnosticSeverity Severity,
    string Code,
    string Message,
    string? FilePath = null,
    int? Line = null)
{
    /// <summary>Renders <c>path(line): cs2j warning CS2J1701: message</c> style text.</summary>
    public string ToMsBuildString()
    {
        var severity = Severity.ToString().ToLowerInvariant();
        var location = FilePath is null
            ? string.Empty
            : Line is int line
                ? $"{FilePath}({line}): "
                : $"{FilePath}: ";
        return $"{location}cs2j {severity} {Code}: {Message}";
    }
}

/// <summary>
/// CS2J17xx code table for the C26 channel. W-03 (upgrade plan §6.3-3): the
/// family migrated from the CS2J13xx segment — where it collided with
/// attribute-normalize (CS2J1300-1390, the segment owner) — to CS2J17xx;
/// codes are append-only. Report consumers matching the old codes get a
/// one-version transition window via <see cref="LegacyCodeOf"/>.
/// </summary>
public static class LinqChannelDiagnosticCodes
{
    /// <summary>LINQ call symbol unbound/ambiguous; chain conservatively preserved. Warning (strict: Error).</summary>
    public const string AmbiguousPreserved = "CS2J1701";

    /// <summary>Route invariant violated: a Queryable/Expression node reached the expansion branch. Error + file rollback (M3).</summary>
    public const string RouteInvariantViolation = "CS2J1702";

    /// <summary>Unprovable node inside a quoted expression tree; site preserved and reported. Warning (strict: Error).</summary>
    public const string ExpressionTreeUnprovable = "CS2J1703";

    /// <summary>Mixed host chain preserved wholesale (D6). Info (verbose).</summary>
    public const string MixedChainPreserved = "CS2J1710";

    /// <summary>Provider-specific extension on a non-BCL host; untouched. Info (verbose).</summary>
    public const string ProviderCustomCall = "CS2J1720";

    /// <summary>Residual scan found a mis-routed node after rewrite (design §7.10). Warning (strict: Error).</summary>
    public const string ResidualMismatch = "CS2J1740";

    /// <summary>Intermediate-tree restore failed.</summary>
    public const string VerifyRestoreFailed = "CS2J1750";

    /// <summary>Intermediate-tree <c>dotnet build</c> failed.</summary>
    public const string VerifyBuildFailed = "CS2J1751";

    /// <summary>Intermediate-tree <c>dotnet test</c> failed.</summary>
    public const string VerifyTestsFailed = "CS2J1752";

    /// <summary>
    /// W-03 one-version transition window: the pre-migration CS2J13xx code a
    /// migrated code replaced, or null for unknown inputs.
    /// </summary>
    public static string? LegacyCodeOf(string code) => code switch
    {
        AmbiguousPreserved => "CS2J1301",
        RouteInvariantViolation => "CS2J1302",
        ExpressionTreeUnprovable => "CS2J1303",
        MixedChainPreserved => "CS2J1310",
        ProviderCustomCall => "CS2J1320",
        ResidualMismatch => "CS2J1340",
        VerifyRestoreFailed => "CS2J1350",
        VerifyBuildFailed => "CS2J1351",
        VerifyTestsFailed => "CS2J1352",
        _ => null,
    };
}
