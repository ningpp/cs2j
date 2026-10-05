namespace CSharpToJava.Core.LinqChannel;

/// <summary>
/// LC0011 (CS2J1302) hard route invariant violation (design §6, §4.3):
/// <list type="bullet">
/// <item>a node whose route decision is Queryable/Expression/Mixed/Ambiguous
/// reached the stock rewriter's expansion branch, or</item>
/// <item>with the channel gate attached, the <c>n == null</c> syntax-name
/// fallback in <c>LinqRewriter.GetMethodFullName</c> was touched for a LINQ
/// operator in chain context (the fabricated-Enumerable identity hole,
/// design §1.3/R2).</item>
/// </list>
/// No well-formed channel input may ever produce this: it means the three-way
/// gate lost a node. The adapter treats it as a hard Error, rolls the affected
/// file all the way back to its ORIGINAL source bytes (undoing even the
/// stage-1 query desugar for that file) and fails the pass.
/// It deliberately does NOT derive from InvalidCastException/
/// NotSupportedException/ArgumentException — the embedded E16 wide catch must
/// not swallow it.
/// </summary>
public sealed class LinqRouteInvariantException : Exception
{
    public LinqRouteInvariantException(string message, string? filePath, int? line)
        : base(message)
    {
        FilePath = filePath;
        Line = line;
    }

    /// <summary>Project-relative file path when known; null for in-memory runs.</summary>
    public string? FilePath { get; }

    /// <summary>1-based source line of the violating node when known.</summary>
    public int? Line { get; }

    public LinqChannelDiagnostic ToDiagnostic()
        => new(
            LinqChannelDiagnosticSeverity.Error,
            LinqChannelDiagnosticCodes.RouteInvariantViolation,
            Message,
            FilePath,
            Line);
}
