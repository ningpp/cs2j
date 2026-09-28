namespace CSharpToJava.Core.IteratorLowering;

/// <param name="Strict">If true, unsupported iterator forms produce Error instead of Warning and fail the pass.</param>
public sealed record IteratorLoweringOptions(
    bool Strict = true);
