namespace CSharpToJava.Core.ArrayMethodRewriter;

public enum ArrayMethodSeverity { Info, Warning, Error }

public sealed record ArrayMethodRewriterDiagnostic(
    ArrayMethodSeverity Severity, string Message);
