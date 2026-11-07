using System.Xml.Linq;

namespace CSharpToJava.Core.Preprocessing.Jcs;

/// <summary>
/// R12 (upgrade plan 附录 F): deterministic, versioned capability handshake
/// written next to the final preprocessed tree. Field order is declaration
/// order, there is no timestamp and no absolute path.
/// </summary>
public sealed record PreprocessManifest(
    int SchemaVersion,
    string SubsetVersion,
    string Profile,
    ManifestSolutionVariant SolutionVariant,
    IReadOnlyList<ManifestPassPlanEntry> PassPlan,
    string ContractHash,
    string InputHash,
    IReadOnlyList<JcsCapability> Capabilities,
    ManifestArtifacts Artifacts)
{
    public const int ManifestSchemaVersion = 1;
    public const string ManifestSubsetVersion = "JCS-1";
}

/// <summary>Solution shape the manifest was produced for (relative project paths).</summary>
public sealed record ManifestSolutionVariant(
    string Configuration,
    IReadOnlyList<ManifestProjectEntry> Projects)
{
    /// <summary>
    /// Rebuilds the variant from the tree itself (raw TFM text, no MSBuild):
    /// TargetFramework/TargetFrameworks are split on ';', de-duplicated and
    /// sorted by path then TFM. <c>$(...)</c> values are preserved verbatim.
    /// </summary>
    public static ManifestSolutionVariant FromTree(
        string treeRoot,
        string configuration = "default")
    {
        ArgumentException.ThrowIfNullOrEmpty(treeRoot);
        var root = Path.GetFullPath(treeRoot);
        var entries = new List<ManifestProjectEntry>();

        foreach (var projectPath in ManifestInputHash.EnumerateProjectFiles(root))
        {
            var relative = ManifestInputHash.ToRelativePath(root, projectPath);
            foreach (var tfm in ReadTfms(projectPath))
            {
                entries.Add(new ManifestProjectEntry(relative, tfm));
            }
        }

        var ordered = entries
            .Distinct()
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ThenBy(entry => entry.Tfm, StringComparer.Ordinal)
            .ToList();

        return new ManifestSolutionVariant(
            string.IsNullOrWhiteSpace(configuration) ? "default" : configuration,
            ordered);
    }

    private static IReadOnlyList<string> ReadTfms(string projectPath)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(projectPath);
        }
        catch (Exception)
        {
            return new[] { string.Empty };
        }

        var values = document
            .Descendants()
            .Where(element => element.Name.LocalName is "TargetFramework" or "TargetFrameworks")
            .Select(element => element.Value.Trim())
            .Where(value => value.Length > 0)
            .SelectMany(value => value.Split(';'))
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();

        return values.Count == 0 ? new[] { string.Empty } : values;
    }
}

/// <summary>One project × TFM pair inside the final tree.</summary>
public sealed record ManifestProjectEntry(string Path, string Tfm);

/// <summary>One enabled pass and its contract hash.</summary>
public sealed record ManifestPassPlanEntry(string Id, string ContractHash);

/// <summary>Report artifacts referenced by the manifest; null entries are omitted.</summary>
public sealed record ManifestArtifacts(
    string? ApiManifest,
    string? ResidualReport,
    string? ComplexityLedger);


