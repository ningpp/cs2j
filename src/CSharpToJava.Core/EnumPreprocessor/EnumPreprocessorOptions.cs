namespace CSharpToJava.Core.EnumPreprocessor;

/// <param name="Verbose">If true, emit info-level diagnostics.</param>
/// <param name="Strict">If true, residual enum bitwise/HasFlag nodes produce Error instead of Warning.</param>
public sealed record EnumPreprocessorOptions(
    bool Verbose = false,
    bool Strict = false);
