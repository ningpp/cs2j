namespace CSharpToJava.Core.CompatMethodReplacement;

/// <summary>
/// Options for the independent <c>compat-method-replacement</c> Expression pass.
/// The pass replaces the body of well-known methods (registered in the
/// <see cref="CompatMethodReplacementRegistry"/>) with a managed replacement so
/// the later <c>unsafe-ffm</c> array path never sees their pointer arithmetic.
/// </summary>
public sealed class CompatMethodReplacementOptions
{
    /// <summary>Master switch; when false the pass copies every file verbatim.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// The known-method replacement table. Defaults to
    /// <see cref="CompatMethodReplacementRegistry.Default"/>; supply a custom
    /// registry (or <see cref="CompatMethodReplacementRegistry.WithAdditionalEntries"/>)
    /// to extend the table without touching the pass.
    /// </summary>
    public CompatMethodReplacementRegistry Registry { get; init; } =
        CompatMethodReplacementRegistry.Default;

    /// <summary>
    /// Emit CMR001 for a registry entry that never matched a declaration in the
    /// processed file set (the pass ran but the expected method was absent).
    /// </summary>
    public bool ReportUnusedEntries { get; init; } = true;
}
