using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.ClosedMetadata;

/// <summary>
/// Core entry for the closed-metadata (C30) pass: bind the whole-tree compilation
/// once, analyze the closed-type registration plan, rewrite file trees, expose
/// generated files and the residual gate (design §5.3 three-phase data flow).
///
/// M3 scope (design §10): the CM-ENUM collector fills the registration record set,
/// the registration generator emits one <c>_Cs2jClosedTypes.&lt;ProjectName&gt;.cs</c> per
/// owning project (A1 declaration-driven bootstrap, D6) and the residual gate covers
/// the minimal M3 rules. Per-file rewrites stay a clean byte copy (null result) — the
/// consumer linker and the typeof-site rewrites land with M4.
/// </summary>
public sealed class ClosedMetadataPreprocessor
{
    private Compilation? _compilation;
    private ClosedMetadataOptions _options = new();
    private ClosedMetadataAnalysis _analysis = ClosedMetadataAnalysis.Empty;
    private IReadOnlyList<ClosedMetadataGeneratedFile> _generatedFiles = Array.Empty<ClosedMetadataGeneratedFile>();

    public ClosedMetadataAnalysis Analysis => _analysis;

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
    /// Rewrite the syntax tree backing <paramref name="model"/>. M3: always returns null
    /// so the runner keeps the file as a clean byte-level copy (consumer-side rewrites
    /// are M4 work).
    /// </summary>
    public ClosedMetadataFileResult? RewriteFile(SemanticModel model)
    {
        ArgumentNullException.ThrowIfNull(_compilation);
        return null;
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
    /// Post-rewrite residual gate keyed by rule (design §5.4). M3 minimal scope:
    /// duplicate registration statements inside a generated file and
    /// duplicate/conflicting normalized keys across generated files; findings carry
    /// CS2J1220 (strict Error, otherwise Warning). The remaining rules ship with the
    /// M4 gate.
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
