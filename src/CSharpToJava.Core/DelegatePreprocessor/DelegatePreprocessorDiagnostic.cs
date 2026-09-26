namespace CSharpToJava.Core.DelegatePreprocessor;

public enum DelegatePreprocessorSeverity { Warning, Error }

public sealed record DelegatePreprocessorDiagnostic(
    DelegatePreprocessorSeverity Severity,
    string Message,
    string? FilePath = null,
    int Line = 0);
