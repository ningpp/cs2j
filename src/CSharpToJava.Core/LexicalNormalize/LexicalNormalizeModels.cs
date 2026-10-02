namespace CSharpToJava.Core.LexicalNormalize;

public enum LexicalNormalizeSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>CS2J55xx diagnostic produced by the lexical-normalize pass.</summary>
public sealed record LexicalNormalizeDiagnostic(
    LexicalNormalizeSeverity Severity,
    string Code,
    string Message,
    int? Line = null);

/// <summary>Pure-syntax options; deliberately contains no preprocessor Symbols (boundary with E01).</summary>
public sealed record LexicalNormalizeOptions
{
    /// <summary>LN-1: strip active #line directives (default on).</summary>
    public bool StripLineDirectives { get; init; } = true;

    /// <summary>LN-7: rewrite file-scoped namespaces into block namespaces (default off; double opt-in).</summary>
    public bool NormalizeFileScopedNamespaces { get; init; } = false;

    /// <summary>LN-4: severity for extern alias declarations/use sites.</summary>
    public LexicalNormalizeSeverity ExternAliasSeverity { get; init; } = LexicalNormalizeSeverity.Error;

    /// <summary>LN-5: cross-file duplicate global using audit (AfterFilesAsync).</summary>
    public bool AuditGlobalUsingDuplicates { get; init; } = true;

    /// <summary>Upgrade every Warning to Error.</summary>
    public bool Strict { get; init; } = false;

    /// <summary>Whether Info diagnostics are surfaced (set false to suppress).</summary>
    public bool ReportInfo { get; init; } = true;
}

public sealed record LexicalNormalizeStatistics
{
    public required int FilesScanned { get; init; }
    public required int FilesTransformed { get; init; }
    public required int FilesSkippedClean { get; init; }
    public required int FilesDirtyNoRewrite { get; init; }
    public required int ResidualCount { get; init; }
    public required int LineDirectivesRemoved { get; init; }
    public required int ErrorDirectivesSeen { get; init; }
    public required int ResidualConditionalDirectives { get; init; }
    public required int ExternAliasDirectives { get; init; }
    public required int ExternAliasQualifiedSites { get; init; }
    public required int GlobalUsingsPlain { get; init; }
    public required int GlobalUsingsStatic { get; init; }
    public required int GlobalUsingsAlias { get; init; }
    public required int StaticUsings { get; init; }
    public required int DuplicateGlobalUsings { get; init; }
    public required int FileScopedNamespacesNormalized { get; init; }
    public required IReadOnlyDictionary<string, int> ErrorsByCode { get; init; }
}

public sealed record LexicalNormalizeResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<LexicalNormalizeDiagnostic> Diagnostics,
    LexicalNormalizeStatistics Statistics,
    IReadOnlyList<GlobalUsingRecord> GlobalUsings);

/// <summary>CS2J55xx code constants (design §6).</summary>
internal static class LexicalNormalizeCodes
{
    public const string BuildFailed = "CS2J5503";
    public const string TestsFailed = "CS2J5504";
    public const string LineRemovedInfo = "CS2J5510";
    public const string ErrorDirective = "CS2J5511";
    public const string ConditionalFirst = "CS2J5512";
    public const string ExternAlias = "CS2J5520";
    public const string DuplicateGlobalUsing = "CS2J5521";
    public const string StaticUsingInfo = "CS2J5522";
    public const string Residual = "CS2J5540";
    public const string FileScopedInfo = "CS2J5541";
    public const string StructuralCheck = "CS2J5542";
}
