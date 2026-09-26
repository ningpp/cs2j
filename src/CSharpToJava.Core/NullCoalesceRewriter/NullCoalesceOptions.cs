namespace CSharpToJava.Core.NullCoalesceRewriter;

/// <param name="Verbose">If true, emit Info-level diagnostics.</param>
/// <param name="Strict">If true, residual ?? / ??= produces Error; otherwise Warning.</param>
public sealed record NullCoalesceOptions(
    bool Verbose = false,
    bool Strict = false);
