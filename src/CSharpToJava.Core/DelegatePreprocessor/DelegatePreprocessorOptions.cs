namespace CSharpToJava.Core.DelegatePreprocessor;

/// <param name="Verbose">If true, emit info-level diagnostics.</param>
/// <param name="Strict">If true, residual delegate operators produce Error instead of Warning.</param>
public sealed record DelegatePreprocessorOptions(
    bool Verbose = false,
    bool Strict = false);
