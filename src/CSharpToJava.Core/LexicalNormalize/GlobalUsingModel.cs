namespace CSharpToJava.Core.LexicalNormalize;

public enum GlobalUsingKind
{
    Plain,
    Static,
    Alias,
}

/// <summary>
/// One global using directive observed at syntax level. SourcePath is attached by
/// the adapter for cross-file duplicate grouping.
/// </summary>
public sealed record GlobalUsingRecord(
    GlobalUsingKind Kind,
    string NormalizedText,
    int Line,
    bool IsStatic,
    bool IsAlias,
    string? SourcePath = null);
