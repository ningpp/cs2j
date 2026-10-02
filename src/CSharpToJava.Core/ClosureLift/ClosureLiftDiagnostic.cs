namespace CSharpToJava.Core.ClosureLift;

public enum ClosureLiftSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// One CS2J32xx row. Every row carries file/line/member/symbol/reason
/// (design §6-4); the adapter fills FilePath.
/// </summary>
public sealed record ClosureLiftDiagnostic(
    ClosureLiftSeverity Severity,
    string Code,
    string Message,
    string? FilePath = null,
    int Line = 0,
    string? MemberName = null,
    string? SymbolName = null,
    TriggerReason? Reason = null)
{
    public ClosureLiftDiagnostic WithFilePath(string? filePath) => this with { FilePath = filePath };
}

/// <summary>CS2J32xx code constants (design §4.7 / §6).</summary>
internal static class ClosureLiftCodes
{
    /// <summary>Mutable capture of a ref local or by-ref parameter before C01 shape arrives (D6).</summary>
    public const string RefCapture = "CS2J3201";

    /// <summary>Symbol unresolvable / ErrorType; the complete site set cannot be guaranteed (D15).</summary>
    public const string UnresolvableSymbol = "CS2J3202";

    /// <summary>Residual anonymous method (E05 output missing) (D14).</summary>
    public const string ResidualAnonymousMethod = "CS2J3203";

    /// <summary>Residual local function cross-boundary capture / lambda in field-or-property initializer (D14, D16).</summary>
    public const string UnsupportedBoundary = "CS2J3204";

    /// <summary>Pointer/anonymous capture kind that cannot be spelled in source (D15).</summary>
    public const string UnsupportedCaptureKind = "CS2J3205";

    /// <summary>Mutable symbol referenced by both a statement lambda and a quoted expression-tree lambda (D11).</summary>
    public const string ExpressionTreeMix = "CS2J3206";

    /// <summary>AuditOnly report row: member/symbol/reason/site count/foreach/prospective names.</summary>
    public const string AuditRow = "CS2J3207";
}
