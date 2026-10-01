namespace CSharpToJava.Core.EqualityConcat;

/// <summary>Result of a single equality/concat preprocessing run.</summary>
public sealed record EqualityConcatResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<EqualityConcatDiagnostic> Diagnostics,
    EqualityConcatStatistics Statistics);
