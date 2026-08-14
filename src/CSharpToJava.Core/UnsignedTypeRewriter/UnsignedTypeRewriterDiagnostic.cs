namespace CSharpToJava.Core.UnsignedTypeRewriter;

public enum UnsignedTypeSeverity { Info, Warning, Error }

public sealed record UnsignedTypeRewriterDiagnostic(
    UnsignedTypeSeverity Severity, string Message);
