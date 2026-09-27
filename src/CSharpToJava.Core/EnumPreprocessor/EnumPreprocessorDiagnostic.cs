namespace CSharpToJava.Core.EnumPreprocessor;

public enum EnumPreprocessorSeverity { Warning, Error }

public sealed record EnumPreprocessorDiagnostic(
    EnumPreprocessorSeverity Severity,
    string Message,
    string? FilePath = null,
    int Line = 0);
