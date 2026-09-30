namespace CSharpToJava.Core.IndexRange;

/// <summary>Severity of an index-range diagnostic.</summary>
public enum IndexRangeSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Diagnostic emitted by the index-range preprocessor.
/// <see cref="Code"/> uses the IR1xx code table (IR110/IR120/IR130/IR140/IR201).
/// </summary>
public sealed record IndexRangeDiagnostic(
    string? Code,
    IndexRangeSeverity Severity,
    string Message,
    string? FilePath = null);
