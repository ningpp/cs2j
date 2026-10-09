namespace CSharpToJava.Core.PartialMerge;

public enum PartialMergeSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>CS2J20xx diagnostic produced by the partial-merge pass.</summary>
public sealed record PartialMergeDiagnostic(
    PartialMergeSeverity Severity,
    string Code,
    string Message,
    string? FilePath = null,
    int? Line = null,
    int? Column = null,
    string? TypeFullName = null)
{
    public string ToMsBuildString()
    {
        var severity = Severity == PartialMergeSeverity.Error ? "error"
            : Severity == PartialMergeSeverity.Warning ? "warning" : "info";
        var location = FilePath != null
            ? $"{FilePath}({Line ?? 0},{Column ?? 0}): "
            : "";
        return $"{location}{severity} {Code}: {Message}";
    }
}

/// <summary>
/// Options for partial-merge. Only two knobs by design: Strict and (M3) the
/// generator-part assumption. No per-file syntax toggles (design §4.6).
/// </summary>
public sealed record PartialMergeOptions
{
    /// <summary>Unhandled residual multi-part declarations / unattributable new errors fail the run.</summary>
    public bool Strict { get; init; } = true;

    /// <summary>M3 only: allow removing classic partial declarations with no in-source implementation.</summary>
    public bool AssumeNoGeneratedPartial { get; init; } = false;
}

public sealed record PartialMergeStatistics
{
    public required int FilesScanned { get; init; }
    public required int FilesTransformed { get; init; }
    public required int FilesSkippedClean { get; init; }
    public required int FilesDirtyNoRewrite { get; init; }
    public required int ResidualCount { get; init; }
    public required int TypesScanned { get; init; }
    public required int GroupsMerged { get; init; }
    public required int PartsRemoved { get; init; }
    public required int MembersCollapsed { get; init; }
    public required int GroupsRolledBack { get; init; }
    public required int Conflicts { get; init; }
    public required int SkippedBaseline { get; init; }
    public required int SkippedTopLevel { get; init; }

    /// <summary>M3: classic declaration-only partial methods removed under AssumeNoGeneratedPartial.</summary>
    public required int PartialDeclarationsRemoved { get; init; }

    /// <summary>M3: call statements stripped for the removed classic partial methods.</summary>
    public required int PartialCallSitesRemoved { get; init; }

    public required IReadOnlyDictionary<string, int> ErrorsByCode { get; init; }
}

/// <summary>Internal mutable accumulator; Build() yields the immutable statistics record.</summary>
internal sealed class MutablePartialMergeStats
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int FilesDirtyNoRewrite;
    public int ResidualCount;
    public int TypesScanned;
    public int GroupsMerged;
    public int PartsRemoved;
    public int MembersCollapsed;
    public int GroupsRolledBack;
    public int Conflicts;
    public int SkippedBaseline;
    public int SkippedTopLevel;
    public int PartialDeclarationsRemoved;
    public int PartialCallSitesRemoved;

    private readonly Dictionary<string, int> _errorsByCode = new(StringComparer.Ordinal);

    public void AddError(string code)
        => _errorsByCode[code] = _errorsByCode.TryGetValue(code, out var count) ? count + 1 : 1;

    public PartialMergeStatistics Build() => new()
    {
        FilesScanned = FilesScanned,
        FilesTransformed = FilesTransformed,
        FilesSkippedClean = FilesSkippedClean,
        FilesDirtyNoRewrite = FilesDirtyNoRewrite,
        ResidualCount = ResidualCount,
        TypesScanned = TypesScanned,
        GroupsMerged = GroupsMerged,
        PartsRemoved = PartsRemoved,
        MembersCollapsed = MembersCollapsed,
        GroupsRolledBack = GroupsRolledBack,
        Conflicts = Conflicts,
        SkippedBaseline = SkippedBaseline,
        SkippedTopLevel = SkippedTopLevel,
        PartialDeclarationsRemoved = PartialDeclarationsRemoved,
        PartialCallSitesRemoved = PartialCallSitesRemoved,
        ErrorsByCode = new Dictionary<string, int>(_errorsByCode, StringComparer.Ordinal),
    };
}

/// <summary>CS2J20xx code constants (design §4.6 / §6).</summary>
internal static class PartialMergeCodes
{
    public const string GroupRolledBack = "CS2J2001";
    public const string WholeTreeRolledBack = "CS2J2002";
    public const string BaseTypeConflict = "CS2J2010";
    public const string TypeParameterMismatch = "CS2J2011";
    public const string ModifierConflict = "CS2J2012";
    public const string DuplicateMember = "CS2J2013";
    public const string AttributeConflict = "CS2J2014";
    public const string ExtendedPartialMissingImpl = "CS2J2015";
    public const string UnsupportedShape = "CS2J2016";
    public const string AnalyzerSeen = "CS2J2017";
    public const string Residual = "CS2J2018";

    /// <summary>M3: classic partial method could not be removed safely under AssumeNoGeneratedPartial.</summary>
    public const string PartialCallSiteNotRemovable = "CS2J2019";

    public const string BuildFailed = "CS2J2030";
    public const string TestsFailed = "CS2J2031";
}
