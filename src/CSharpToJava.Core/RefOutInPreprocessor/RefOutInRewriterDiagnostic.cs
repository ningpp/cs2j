using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.RefOutInPreprocessor;

public enum RefOutInSeverity { Info, Warning, Error }

public sealed record RefOutInRewriterDiagnostic(
    string Id,
    string Message,
    RefOutInSeverity Severity,
    Location? Location = null);
