using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.ClosedMetadata;

/// <summary>
/// Core entry for the closed-metadata (C30) pass: bind the whole-tree compilation
/// once, analyze the closed-type registration plan, rewrite file trees, expose
/// generated files and the residual gate (design §5.3 three-phase data flow).
///
/// The CM-ENUM collector fills the registration record set, the registration
/// generator emits one <c>_Cs2jClosedTypes.&lt;ProjectName&gt;.cs</c> per owning
/// project (A1 declaration-driven bootstrap, D6), the CM-LINK consumer linker
/// inserts idempotent bootstrap guards at D8 serializer entry statements (M4b),
/// and the residual gate covers all six §5.4 rules (M3 content rules 2/5 plus
/// the M4c rewritten-tree rules 1/3/4/6). With no holder-name resolver wired,
/// <see cref="RewriteFile"/> and the tree gate are inert and every file stays a
/// clean byte-level copy.
/// </summary>
public sealed class ClosedMetadataPreprocessor
{
    private Compilation? _compilation;
    private ClosedMetadataOptions _options = new();
    private ClosedMetadataAnalysis _analysis = ClosedMetadataAnalysis.Empty;
    private IReadOnlyList<ClosedMetadataGeneratedFile> _generatedFiles = Array.Empty<ClosedMetadataGeneratedFile>();

    public ClosedMetadataAnalysis Analysis => _analysis;

    /// <summary>
    /// Maps a record ownership file to its project's holder type name (CM-LINK).
    /// When unset, <see cref="RewriteFile"/> performs no consumer linking.
    /// </summary>
    public Func<string, string?>? HolderNameResolver { get; set; }

    public void BindCompilation(Compilation compilation) => _compilation = compilation;

    public ClosedMetadataAnalysis Analyze(ClosedMetadataOptions options)
    {
        ArgumentNullException.ThrowIfNull(_compilation);
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        var collector = new ClosedMetadataCollector(_compilation, options);
        return _analysis = collector.Analyze();
    }

    /// <summary>
    /// Rewrite the syntax tree backing <paramref name="model"/> by inserting bootstrap
    /// guards in front of D8 serializer entry statements (CM-LINK, M4b). Returns null
    /// when the file is a clean copy (no resolver wired, no servable consumer site, or
    /// an empty registration set). <paramref name="selfHolderName"/> is the rewritten
    /// file's own project holder (null when it cannot be determined); framework-only
    /// closures are self-guarded rather than cross-referenced without a guaranteed
    /// project reference.
    /// </summary>
    public ClosedMetadataFileResult? RewriteFile(SemanticModel model, string? selfHolderName = null)
    {
        ArgumentNullException.ThrowIfNull(_compilation);
        if (HolderNameResolver is null || _analysis.ClosedTypes.Count == 0)
        {
            return null;
        }

        var linker = new ClosedMetadataConsumptionLinker(_analysis, HolderNameResolver, selfHolderName);
        return linker.LinkFile(model);
    }

    /// <summary>
    /// Builds the per-project generated registration files (D6) from records already
    /// mapped to their owning project. Files are produced in project-name order and
    /// each file's registrations follow the normalized-key order of its records.
    /// </summary>
    public IReadOnlyList<ClosedMetadataGeneratedFile> GenerateRegistrationFiles(
        IReadOnlyDictionary<string, IReadOnlyList<ClosedTypeRecord>> recordsByProject)
    {
        var files = new List<ClosedMetadataGeneratedFile>();
        foreach (var (projectName, records) in recordsByProject.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            files.Add(new ClosedMetadataGeneratedFile(
                projectName,
                $"{ClosedMetadataRegistrationGenerator.GeneratedFileStem}.{projectName}.cs",
                ClosedMetadataRegistrationGenerator.Generate(projectName, records)));
        }

        return _generatedFiles = files;
    }

    /// <summary>Per-project generated registration files (D6).</summary>
    public IReadOnlyList<ClosedMetadataGeneratedFile> GeneratedFileSpecs => _generatedFiles;

    /// <summary>
    /// Content gate over the generated registration files (design §5.4 rules 2/5):
    /// duplicate registration statements inside a generated file and
    /// duplicate/conflicting normalized keys across generated files. Findings carry
    /// CS2J1220 (strict Error, otherwise Warning). The tree-shape rules 1/3/4/6 live
    /// in <see cref="ResidualScan(Compilation, ClosedMetadataOverrides?)"/>.
    /// </summary>
    public ClosedMetadataResidualResult ResidualScan(IReadOnlyList<ClosedMetadataGeneratedFile> files)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var diagnostics = new List<ClosedMetadataDiagnostic>();

        var duplicateRegistrations = 0;
        var keyOwner = new Dictionary<string, string>(StringComparer.Ordinal);
        var conflictingKeys = 0;
        foreach (var file in files)
        {
            var seenInFile = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in file.Content.Split('\n'))
            {
                var key = ExtractRegistrationKey(line);
                if (key is null)
                {
                    continue;
                }

                if (!seenInFile.Add(key))
                {
                    duplicateRegistrations++;
                    continue;
                }

                if (!keyOwner.TryAdd(key, file.FileName))
                {
                    conflictingKeys++;
                }
            }
        }

        if (duplicateRegistrations > 0)
        {
            counts["duplicate-registration"] = duplicateRegistrations;
        }

        if (conflictingKeys > 0)
        {
            counts["conflicting-registration-key"] = conflictingKeys;
        }

        if (counts.Count > 0)
        {
            diagnostics.Add(new ClosedMetadataDiagnostic(
                _options.Strict ? ClosedMetadataSeverity.Error : ClosedMetadataSeverity.Warning,
                ClosedMetadataRule.Residual,
                ClosedMetadataCodes.ResidualFound,
                $"{counts.Values.Sum()} residual registration site(s) after closed-metadata " +
                $"({string.Join(", ", counts.Select(pair => $"{pair.Key}={pair.Value}"))}); the generated " +
                "registration set must be deduplicated and conflict-free (design §5.4)."));
        }

        return new ClosedMetadataResidualResult(counts, diagnostics);
    }

    /// <summary>
    /// Post-rewrite tree gate over the recompiled intermediate tree (design §5.4
    /// rules 1/3/4/6, M4c): erased-Type serializer consumption, bare
    /// <c>typeof(闭型)</c> passthrough, unguarded consumer entries and guard leaks
    /// into expression-tree/source-gen zones. Requires the holder-name resolver
    /// wired for CM-LINK; without one the gate has no ownership map and reports
    /// nothing. Explicit skip/force overrides suppress findings with a CLO001 trace.
    /// </summary>
    public ClosedMetadataResidualResult ResidualScan(
        Compilation rewritten,
        ClosedMetadataOverrides? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(rewritten);
        if (HolderNameResolver is null)
        {
            return new ClosedMetadataResidualResult(
                new Dictionary<string, int>(StringComparer.Ordinal),
                Array.Empty<ClosedMetadataDiagnostic>());
        }

        return ClosedMetadataResidualScanner.Scan(
            rewritten, _analysis, HolderNameResolver, _options, overrides);
    }

    private static string? ExtractRegistrationKey(string line)
    {
        const string marker = "__cs2j_register_closed(\"";
        var start = line.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var rest = line[(start + marker.Length)..];
        var end = rest.IndexOf('"', StringComparison.Ordinal);
        return end < 0 ? null : rest[..end];
    }
}
