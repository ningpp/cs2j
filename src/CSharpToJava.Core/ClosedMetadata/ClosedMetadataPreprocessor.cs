using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.ClosedMetadata;

/// <summary>
/// Core entry for the closed-metadata (C30) pass: bind the whole-tree compilation
/// once, analyze the closed-type registration plan, rewrite file trees, expose
/// generated files and the residual gate (design §5.3 three-phase data flow).
///
/// M2' skeleton (design §10): the collector, registration generator, consumer
/// linker and residual scanner land in M3/M4. Until then analysis produces an
/// empty record set, every rewrite is a clean byte copy (null result — the CLI
/// runner performs the verbatim file copy), no files are generated and the
/// residual scan reports zero findings.
/// </summary>
public sealed class ClosedMetadataPreprocessor
{
    private Compilation? _compilation;
    private ClosedMetadataAnalysis _analysis = ClosedMetadataAnalysis.Empty;

    public ClosedMetadataAnalysis Analysis => _analysis;

    public void BindCompilation(Compilation compilation) => _compilation = compilation;

    public ClosedMetadataAnalysis Analyze(ClosedMetadataOptions options)
    {
        ArgumentNullException.ThrowIfNull(_compilation);
        ArgumentNullException.ThrowIfNull(options);

        // M2': CM-ENUM-1..4 (ClosedMetadataCollector) is an M3 deliverable — the
        // registration plan stays empty and contributes no diagnostics of its own.
        return _analysis = ClosedMetadataAnalysis.Empty;
    }

    /// <summary>
    /// Rewrite the syntax tree backing <paramref name="model"/>. M2' skeleton:
    /// always returns null so the runner keeps the file as a clean byte-level copy.
    /// </summary>
    public ClosedMetadataFileResult? RewriteFile(SemanticModel model)
    {
        ArgumentNullException.ThrowIfNull(_compilation);
        return null;
    }

    /// <summary>
    /// Per-project generated registration files (M3: one
    /// <c>_Cs2jClosedTypes.&lt;ProjectName&gt;.cs</c> per owning project, D6).
    /// M2' skeleton: no generated files.
    /// </summary>
    public IReadOnlyList<ClosedMetadataGeneratedFile> GeneratedFileSpecs { get; } =
        Array.Empty<ClosedMetadataGeneratedFile>();

    /// <summary>
    /// Post-rewrite residual gate keyed by rule (design §5.4). M2' skeleton: zero
    /// findings — the scanner ships with the M3/M4 gate wiring.
    /// </summary>
    public IReadOnlyDictionary<string, int> ResidualScan() =>
        new Dictionary<string, int>(StringComparer.Ordinal);
}
