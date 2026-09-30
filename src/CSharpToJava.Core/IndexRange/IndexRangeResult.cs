namespace CSharpToJava.Core.IndexRange;

/// <summary>Result of a single index-range preprocessing run.</summary>
public sealed record IndexRangeResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<IndexRangeDiagnostic> Diagnostics,
    IndexRangeStatistics Statistics);
