namespace CSharpToJava.Core.ClosedMetadata;

public enum ClosedMetadataSeverity { Info, Warning, Error }

public enum ClosedMetadataRule
{
    /// <summary>Closed-type enumeration findings (CM-ENUM-*).</summary>
    Enumeration,
    /// <summary>Registration key normalization/conflict findings.</summary>
    Key,
    /// <summary>Closed typeof-tree -> Type construction mapping findings (CM-TOKEN).</summary>
    TokenMapping,
    /// <summary>STJ source-generation / expression-tree context findings (CM-SRCGEN).</summary>
    SourceGeneration,
    /// <summary>Chain position findings (CS2J1205 hard RunAfter dependency).</summary>
    ChainPosition,
    /// <summary>Dynamic closed-type sites A1 cannot cover (CM-DYN).</summary>
    DynamicSite,
    /// <summary>Post-rewrite residual gate findings (design §5.4).</summary>
    Residual,
    /// <summary>Restore/build/test infrastructure findings.</summary>
    Infrastructure,
}

public sealed record ClosedMetadataDiagnostic(
    ClosedMetadataSeverity Severity,
    ClosedMetadataRule Rule,
    string Code,
    string Message,
    string? FilePath = null,
    int? Line = null)
{
    public override string ToString()
    {
        var level = Severity switch
        {
            ClosedMetadataSeverity.Error => "error",
            ClosedMetadataSeverity.Warning => "warning",
            _ => "info",
        };
        return $"{FilePath ?? "<memory>"}:{Line?.ToString() ?? "?"}: {level} {Code}: [{Rule}] {Message}";
    }
}

/// <summary>CS2J12xx diagnostic codes for the closed-metadata pass (spec §7).</summary>
public static class ClosedMetadataCodes
{
    /// <summary>Consumer-needed closed type has no source symbol; cannot register C#-side (Warning; strict Error).</summary>
    public const string ClosedTypeWithoutSourceSymbol = "CS2J1201";

    /// <summary>One normalized key resolves to inconsistent Java target closed types (Error).</summary>
    public const string KeyConflict = "CS2J1202";

    /// <summary>Java target type undecidable in the current symbol shape (Warning/Error).</summary>
    public const string JavaTargetUndecidable = "CS2J1203";

    /// <summary>STJ source-generation / AOT context outside the served surface set (Warning).</summary>
    public const string SourceGeneratedContext = "CS2J1204";

    /// <summary>closed-metadata enabled while generic-deepen is not (Error; chain hard dependency §4.4).</summary>
    public const string GenericDeepenRequired = "CS2J1205";

    /// <summary>Dynamic closed-type site (runtime-bound T) A1 cannot cover (Info; strict Warning).</summary>
    public const string DynamicClosedTypeSite = "CS2J1210";

    /// <summary>Residual gate: unregistered closed-type consumption / bare typeof passthrough (Warning; strict Error).</summary>
    public const string ResidualFound = "CS2J1220";

    /// <summary>Intermediate-tree restore/build failed (Error).</summary>
    public const string RestoreOrBuildFailed = "CS2J1230";

    /// <summary>Intermediate-tree tests failed (Error).</summary>
    public const string TestsFailed = "CS2J1231";

    /// <summary>Single-file mode plan not self-contained (Warning).</summary>
    public const string PlanNotSelfContained = "CS2J1290";

    /// <summary>Verbose detail line (per registration/dedup/consumer link).</summary>
    public const string Info = "CLO001";
}
