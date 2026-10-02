namespace CSharpToJava.Core.ExpressionDesugar;

/// <summary>Result of a single expression-desugar family preprocessing run.</summary>
public sealed record ExpressionDesugarResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<ExpressionDesugarDiagnostic> Diagnostics,
    ExpressionDesugarStatistics Statistics);
