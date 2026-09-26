namespace CSharpToJava.Core.NullCoalesceRewriter;

public enum NullCoalesceSeverity { Warning, Error }

public sealed record NullCoalesceDiagnostic(
    NullCoalesceSeverity Severity,
    string Message,
    string? FilePath = null,
    int Line = 0);
