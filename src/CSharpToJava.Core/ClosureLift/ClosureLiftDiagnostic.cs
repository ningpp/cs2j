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

    // ── C45 (design §6): the named-method / delegate-site stages use CS2J45xx. ──

    /// <summary>Lambda (own or delegate) parameter carries ref/out/in/ref readonly, or delegate returns ref (D6②/D23).</summary>
    public const string RefLambdaParameter = "CS2J4501";

    /// <summary>Captured symbol cannot be resolved to a supported declarator (D5/D15; C45 read-only path mirror of CS2J3202).</summary>
    public const string UnresolvableCapture = "CS2J4502";

    /// <summary>Residual anonymous-method node counted by the tree residual gate (E05 output missing; design §6).</summary>
    public const string ResidualAnonymousMethodGate = "CS2J4503";

    /// <summary>Unsupported lambda host: static lambda in a field/property initializer (D18③/D21, M2 explicit row).</summary>
    public const string UnsupportedHost = "CS2J4504";

    /// <summary>Captured symbol of an unspeakable kind reached the C45 read-only path (defensive mirror of CS2J3205, design §6).</summary>
    public const string UnsupportedCaptureKindGate = "CS2J4505";

    /// <summary>Lambda ConvertedType is not a resolvable delegate type; the named-method signature cannot be synthesized (D8/D12).</summary>
    public const string UnresolvableDelegateType = "CS2J4509";

    /// <summary>Residual gate (§7.3): statement lambdas remain in the output beyond the accounted skip buckets (M2 strict Error).</summary>
    public const string ResidualStatementGate = "CS2J4510";
}
