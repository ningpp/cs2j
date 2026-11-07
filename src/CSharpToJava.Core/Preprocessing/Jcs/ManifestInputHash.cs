using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace CSharpToJava.Core.Preprocessing.Jcs;

/// <summary>
/// R12 consumer-side input hash. Re-derived from the final tree (never from
/// the manifest's own claims): compile sources, project assets, the solution
/// variant and the resolved reference set. Timestamps and absolute roots are
/// excluded, so one tree copied elsewhere hashes identically.
/// </summary>
public static class ManifestInputHash
{
    private static readonly HashSet<string> SkippedDirectoryNames =
        new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git" };

    private static readonly HashSet<string> SkippedFileNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "preprocess-manifest.json",
            "preprocess-report.json",
            "preprocess-residual-report.json",
            "cs2j-workspace-plan.json",
            "api-manifest.json",
        };

    private static readonly HashSet<string> SourceExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".cs" };

    private static readonly HashSet<string> ProjectAssetExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".csproj", ".props", ".targets", ".sln", ".slnx",
        };

    public static string Compute(string treeRoot, ManifestSolutionVariant variant)
    {
        ArgumentException.ThrowIfNullOrEmpty(treeRoot);
        ArgumentNullException.ThrowIfNull(variant);

        var root = Path.GetFullPath(treeRoot);
        var lines = new List<string>
        {
            "solutionVariant|" + variant.Configuration,
        };

        foreach (var project in variant.Projects
                     .OrderBy(entry => entry.Path, StringComparer.Ordinal)
                     .ThenBy(entry => entry.Tfm, StringComparer.Ordinal))
        {
            lines.Add($"project|{project.Path}|{project.Tfm}");
        }

        foreach (var file in EnumerateFiles(root, SourceExtensions)
                     .OrderBy(path => ToRelativePath(root, path), StringComparer.Ordinal))
        {
            lines.Add($"source|{ToRelativePath(root, file)}|{HashFile(file)}");
        }

        foreach (var file in EnumerateFiles(root, ProjectAssetExtensions)
                     .OrderBy(path => ToRelativePath(root, path), StringComparer.Ordinal))
        {
            lines.Add($"asset|{ToRelativePath(root, file)}|{HashFile(file)}");
        }

        foreach (var reference in CollectReferences(root))
        {
            lines.Add("reference|" + reference);
        }

        return Hash(string.Join('\n', lines));
    }

    internal static IEnumerable<string> EnumerateProjectFiles(string treeRoot)
        => EnumerateFiles(treeRoot, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".csproj" });

    internal static IEnumerable<string> EnumerateProjectAssetFiles(string treeRoot)
        => EnumerateFiles(treeRoot, ProjectAssetExtensions);

    internal static IReadOnlyList<string> EnumerateSourceFiles(string treeRoot)
        => EnumerateFiles(treeRoot, SourceExtensions)
            .OrderBy(path => ToRelativePath(treeRoot, path), StringComparer.Ordinal)
            .ToList();

    internal static string ToRelativePath(string treeRoot, string path)
        => Path.GetRelativePath(Path.GetFullPath(treeRoot), path).Replace('\\', '/');

    private static IEnumerable<string> EnumerateFiles(
        string treeRoot,
        IReadOnlySet<string> extensions)
    {
        var root = Path.GetFullPath(treeRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Tree root not found: {root}");
        }

        foreach (var file in EnumerateFilesRecursive(root))
        {
            if (SkippedFileNames.Contains(Path.GetFileName(file)))
            {
                continue;
            }

            if (extensions.Contains(Path.GetExtension(file)))
            {
                yield return file;
            }
        }
    }

    private static IEnumerable<string> EnumerateFilesRecursive(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            yield return file;
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(child);
            if (SkippedDirectoryNames.Contains(name)
                || name.StartsWith(".cs2j-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var file in EnumerateFilesRecursive(child))
            {
                yield return file;
            }
        }
    }

    private static string HashFile(string path)
    {
        var text = File.ReadAllText(path)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return Hash(text);
    }

    private static IReadOnlyList<string> CollectReferences(string treeRoot)
    {
        var references = new List<string>();

        foreach (var asset in EnumerateProjectAssetFiles(treeRoot))
        {
            XDocument document;
            try
            {
                document = XDocument.Load(asset);
            }
            catch (Exception)
            {
                // .sln and other non-XML assets still contribute through their
                // content hash; they simply declare no XML reference entries.
                continue;
            }

            foreach (var element in document.Descendants())
            {
                switch (element.Name.LocalName)
                {
                    case "PackageReference":
                        AddPackageReference(element, references);
                        break;
                    case "ProjectReference":
                        AddProjectReference(treeRoot, asset, element, references);
                        break;
                    case "Reference":
                        AddAssemblyReference(element, references);
                        break;
                }
            }
        }

        return references
            .Distinct(StringComparer.Ordinal)
            .OrderBy(reference => reference, StringComparer.Ordinal)
            .ToList();
    }

    private static void AddPackageReference(XElement element, List<string> references)
    {
        var include = element.Attribute("Include")?.Value.Trim();
        if (string.IsNullOrEmpty(include))
        {
            return;
        }

        var version = element.Attribute("Version")?.Value.Trim()
            ?? element.Elements()
                .FirstOrDefault(child => child.Name.LocalName == "Version")?.Value.Trim()
            ?? string.Empty;

        references.Add($"package|{include}|{version}");
    }

    private static void AddProjectReference(
        string treeRoot,
        string assetPath,
        XElement element,
        List<string> references)
    {
        var include = element.Attribute("Include")?.Value.Trim();
        if (string.IsNullOrEmpty(include))
        {
            return;
        }

        var declared = include.Replace('\\', '/');
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(assetPath));
            if (directory is not null)
            {
                var full = Path.GetFullPath(Path.Combine(directory, include));
                var relative = Path.GetRelativePath(treeRoot, full).Replace('\\', '/');
                if (!Path.IsPathRooted(relative) && !relative.StartsWith("..", StringComparison.Ordinal))
                {
                    declared = relative;
                }
            }
        }
        catch (Exception)
        {
            // Invalid/absolute include: keep the declared text so the hash
            // stays deterministic instead of inventing a machine path.
        }

        references.Add("project|" + declared);
    }

    private static void AddAssemblyReference(XElement element, List<string> references)
    {
        var include = element.Attribute("Include")?.Value.Trim();
        if (string.IsNullOrEmpty(include))
        {
            return;
        }

        var simpleName = include;
        var comma = simpleName.IndexOf(',');
        if (comma >= 0)
        {
            simpleName = simpleName[..comma];
        }

        var separator = Math.Max(simpleName.LastIndexOf('\\'), simpleName.LastIndexOf('/'));
        if (separator >= 0)
        {
            simpleName = simpleName[(separator + 1)..];
        }

        if (simpleName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            simpleName = simpleName[..^4];
        }

        simpleName = simpleName.Trim();
        if (simpleName.Length > 0)
        {
            references.Add("reference|" + simpleName);
        }
    }

    private static string Hash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return "sha256:" + Convert.ToHexStringLower(bytes);
    }
}
