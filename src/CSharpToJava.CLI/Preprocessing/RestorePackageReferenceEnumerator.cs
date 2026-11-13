using System.Text.Json;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace CSharpToJava.CLI.Preprocessing;

/// <summary>
/// Enumerates compile-time NuGet package assemblies from a restored
/// intermediate tree's <c>obj/project.assets.json</c> files (M3, design
/// appendix ruling 2). The merged compilation binds every project's sources
/// together, so the per-project compile assemblies are unioned into one
/// reference set (grouped/enumerated per project, consumed merged — no
/// per-project compilation is built).
///
/// Rules:
/// - <c>type=project</c> entries are skipped (their sources are in the merged
///   compilation; referencing their built output would duplicate types).
/// - Assemblies whose simple name is already in the TPA set are skipped (the
///   runtime stack wins; avoids duplicate metadata identities).
/// - Only <c>compile</c> entries are used (reference facades), falling back to
///   <c>runtime</c> entries when a package ships no compile list.
/// - When the tree carries no restore output at all, the machine's NuGet global
///   package cache is used instead, driven by the tree's csproj
///   <c>PackageReference</c> graph and each package's nuspec dependency
///   closure (see <see cref="EnumerateFromGlobalPackageCache"/>). This makes a
///   chained intermediate tree bind package-declared base types without a
///   restore of that tree.
/// </summary>
internal static class RestorePackageReferenceEnumerator
{
    public static IReadOnlyList<MetadataReference> Enumerate(string intermediateRoot, out int addedCount)
        => Enumerate(intermediateRoot, packageCacheRoot: null, out addedCount);

    internal static IReadOnlyList<MetadataReference> Enumerate(
        string intermediateRoot, string? packageCacheRoot, out int addedCount)
    {
        var references = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);
        var tpaSimpleNames = GetTrustedPlatformAssemblySimpleNames();

        foreach (var assetsFile in Directory.EnumerateFiles(
                     intermediateRoot, "project.assets.json", SearchOption.AllDirectories))
        {
            // Restore artifacts live under an obj directory; the SDK's
            // project-extensions path can nest the file (e.g.
            // obj/<configuration>/<project>/project.assets.json), so accept any
            // depth below obj instead of only obj/project.assets.json.
            var normalized = assetsFile.Replace('\\', '/');
            if (!string.Equals(
                    normalized[(normalized.LastIndexOf('/') + 1)..],
                    "project.assets.json", StringComparison.OrdinalIgnoreCase)
                || !normalized.Split('/').Any(segment =>
                    string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            try
            {
                ReadAssetsFile(assetsFile, references, tpaSimpleNames);
            }
            catch (IOException)
            {
                // A locked/partial assets file must never silently degrade the
                // picture: skip enumeration for it; bind diagnostics stay loud.
            }
            catch (JsonException)
            {
                // Malformed restore output: ignore this project; restore
                // failure is reported separately by the owning pass's
                // restore-failed diagnostic (W-03: the 12xx code family was
                // split per pass — extension-methods CS2J1930, thread-static
                // CS2J1630, closed-metadata CS2J1230).
            }
        }

        // No restore output anywhere in the tree (a chained intermediate tree
        // normally has none): fall back to the NuGet global package cache so
        // package-declared base types still bind. Exactly one of the two
        // sources is used — assets output is never widened by the fallback.
        if (references.Count == 0)
        {
            try
            {
                EnumerateFromGlobalPackageCache(
                    intermediateRoot, packageCacheRoot, references, tpaSimpleNames);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (System.Xml.XmlException)
            {
            }
        }

        addedCount = references.Count;
        return references.Values.ToList();
    }

    /// <summary>
    /// Fallback for intermediate trees that carry no restore output: resolve
    /// compile references from the machine's NuGet global package folder using
    /// the tree's csproj <c>PackageReference</c> graph plus each package's
    /// nuspec dependency closure. Best-effort: missing package directories,
    /// unreadable XML and I/O failures are swallowed so the chain exit code
    /// never changes.
    /// </summary>
    private static void EnumerateFromGlobalPackageCache(
        string root,
        string? packageCacheRoot,
        Dictionary<string, MetadataReference> references,
        HashSet<string> tpaSimpleNames)
    {
        var packagesRoot = ResolvePackageCacheRoot(packageCacheRoot);
        if (packagesRoot == null || !Directory.Exists(packagesRoot))
        {
            return;
        }

        var packages = ReadPackageReferences(root);
        if (packages.Count == 0)
        {
            return;
        }

        // One assembly per simple name, highest version wins (the merged
        // compilation unions every project's package assemblies, so two
        // versions of one assembly would make its types ambiguous).
        var bySimpleName = new Dictionary<string, (Version Version, string Path)>(
            StringComparer.OrdinalIgnoreCase);
        var visitedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
        {
            CollectPackage(
                package.Id, package.Version, packagesRoot, bySimpleName, visitedPackages, tpaSimpleNames);
        }

        foreach (var entry in bySimpleName.Values)
        {
            if (references.ContainsKey(entry.Path))
            {
                continue;
            }

            try
            {
                references[entry.Path] = MetadataReference.CreateFromFile(entry.Path);
            }
            catch (IOException)
            {
                // Best-effort binding aid: a vanished/locked assembly is skipped.
            }
        }
    }

    private static string? ResolvePackageCacheRoot(string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            return explicitRoot;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile)
            ? null
            : Path.Combine(profile, ".nuget", "packages");
    }

    /// <summary>
    /// Collects <c>(id, version)</c> pairs from every csproj below the tree.
    /// <c>VersionOverride</c> wins over <c>Version</c>; a missing version is
    /// looked up in <c>Directory.Packages.props</c> (<c>PackageVersion</c>).
    /// Entries whose id/version still contains an MSBuild property are skipped.
    /// </summary>
    private static List<(string Id, string Version)> ReadPackageReferences(string root)
    {
        var result = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var centralVersions = ReadCentralPackageVersions(root);

        foreach (var projectFile in Directory.EnumerateFiles(
                     root, "*.csproj", SearchOption.AllDirectories))
        {
            XDocument document;
            try
            {
                document = XDocument.Load(projectFile);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (System.Xml.XmlException)
            {
                continue;
            }

            foreach (var element in document.Descendants()
                         .Where(element => element.Name.LocalName == "PackageReference"))
            {
                var id = element.Attribute("Include")?.Value
                         ?? element.Attribute("Update")?.Value;
                if (string.IsNullOrWhiteSpace(id)
                    || id.Contains("$(", StringComparison.Ordinal))
                {
                    continue;
                }

                var version = element.Attribute("VersionOverride")?.Value
                              ?? element.Attribute("Version")?.Value
                              ?? element.Elements()
                                  .FirstOrDefault(child => child.Name.LocalName == "VersionOverride")?.Value
                              ?? element.Elements()
                                  .FirstOrDefault(child => child.Name.LocalName == "Version")?.Value;
                if (string.IsNullOrWhiteSpace(version)
                    && centralVersions.TryGetValue(id, out var centralVersion))
                {
                    version = centralVersion;
                }

                if (string.IsNullOrWhiteSpace(version)
                    || version.Contains("$(", StringComparison.Ordinal))
                {
                    continue;
                }

                if (seen.Add(id + "/" + version))
                {
                    result.Add((id, version));
                }
            }
        }

        return result;
    }

    private static Dictionary<string, string> ReadCentralPackageVersions(string root)
    {
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var propsFile in Directory.EnumerateFiles(
                     root, "Directory.Packages.props", SearchOption.AllDirectories))
        {
            try
            {
                var document = XDocument.Load(propsFile);
                foreach (var element in document.Descendants()
                             .Where(element => element.Name.LocalName == "PackageVersion"))
                {
                    var id = element.Attribute("Include")?.Value;
                    var version = element.Attribute("Version")?.Value;
                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(version))
                    {
                        versions.TryAdd(id, version);
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (System.Xml.XmlException)
            {
            }
        }

        return versions;
    }

    /// <summary>
    /// Adds one package's compile assemblies and recurses into its nuspec
    /// dependency closure. <paramref name="visitedPackages"/> guards cycles.
    /// </summary>
    private static void CollectPackage(
        string id,
        string version,
        string packagesRoot,
        Dictionary<string, (Version Version, string Path)> bySimpleName,
        HashSet<string> visitedPackages,
        HashSet<string> tpaSimpleNames)
    {
        var versionSegment = ResolveVersionDirectory(packagesRoot, id, version);
        if (versionSegment == null)
        {
            return;
        }

        var packageDirectory = Path.Combine(packagesRoot, id.ToLowerInvariant(), versionSegment);
        if (!visitedPackages.Add(packageDirectory))
        {
            return;
        }

        var packageVersion = Version.TryParse(versionSegment.Split('-', '+')[0], out var parsed)
            ? parsed
            : new Version(0, 0);

        var assemblyDirectory = SelectAssemblyDirectory(packageDirectory);
        if (assemblyDirectory != null)
        {
            foreach (var dll in Directory.EnumerateFiles(
                         assemblyDirectory, "*.dll", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(dll);
                if (fileName.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(fileName, "_._", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var simpleName = Path.GetFileNameWithoutExtension(dll);
                if (tpaSimpleNames.Contains(simpleName))
                {
                    continue;
                }

                if (!bySimpleName.TryGetValue(simpleName, out var existing)
                    || packageVersion > existing.Version)
                {
                    bySimpleName[simpleName] = (packageVersion, dll);
                }
            }
        }

        foreach (var dependency in ReadNuspecDependencies(packageDirectory))
        {
            CollectPackage(
                dependency.Id,
                dependency.Version,
                packagesRoot,
                bySimpleName,
                visitedPackages,
                tpaSimpleNames);
        }
    }

    /// <summary>
    /// Resolves the on-disk version directory of an installed package: the exact
    /// version when present, otherwise the highest installed version for a
    /// range/floating spec. A declared exact version that is not installed is
    /// NOT substituted (nothing is invented).
    /// </summary>
    private static string? ResolveVersionDirectory(string packagesRoot, string id, string version)
    {
        var packageDirectory = Path.Combine(packagesRoot, id.ToLowerInvariant());
        var exact = NormalizeDependencyVersion(version);
        if (exact != null)
        {
            return Directory.Exists(Path.Combine(packageDirectory, exact)) ? exact : null;
        }

        if (!Directory.Exists(packageDirectory))
        {
            return null;
        }

        string? best = null;
        Version? bestVersion = null;
        foreach (var candidate in Directory.EnumerateDirectories(packageDirectory))
        {
            var name = Path.GetFileName(candidate);
            if (!Version.TryParse(name.Split('-', '+')[0], out var candidateVersion))
            {
                continue;
            }

            if (bestVersion == null || candidateVersion > bestVersion)
            {
                bestVersion = candidateVersion;
                best = name;
            }
        }

        return best;
    }

    /// <summary>
    /// Extracts a plain version from a nuspec version range
    /// (e.g. <c>[2.9.3, )</c> -> <c>2.9.3</c>); null for open ranges/floating
    /// specs so the caller may fall back to the highest installed version.
    /// </summary>
    private static string? NormalizeDependencyVersion(string version)
    {
        var trimmed = version.Trim().TrimStart('[', '(').TrimEnd(']', ')');
        var comma = trimmed.IndexOf(',');
        if (comma >= 0)
        {
            trimmed = trimmed[..comma].Trim();
        }

        return trimmed.Length == 0
               || trimmed.Contains('*')
               || trimmed.Contains("$(", StringComparison.Ordinal)
            ? null
            : trimmed;
    }

    /// <summary>Compile assemblies prefer <c>ref/</c> facades, then <c>lib/</c>.</summary>
    private static string? SelectAssemblyDirectory(string packageDirectory)
    {
        foreach (var folderName in new[] { "ref", "lib" })
        {
            var best = SelectBestFrameworkDirectory(
                Path.Combine(packageDirectory, folderName));
            if (best != null)
            {
                return best;
            }
        }

        return null;
    }

    private static string? SelectBestFrameworkDirectory(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return null;
        }

        string? best = null;
        var bestRank = int.MinValue;
        foreach (var directory in Directory.EnumerateDirectories(folder))
        {
            var rank = RankTargetFramework(Path.GetFileName(directory));
            if (rank > bestRank)
            {
                bestRank = rank;
                best = directory;
            }
        }

        return best;
    }

    /// <summary>
    /// "Most appropriate" TFM ordering: net10.0 &gt; … &gt; net5.0 &gt;
    /// netcoreapp3.1 &gt; netstandard2.1 &gt; netstandard2.0 &gt;
    /// netstandard1.x &gt; net4x. Accepts both folder names (<c>net9.0</c>) and
    /// nuspec group names (<c>.NETStandard2.0</c>,
    /// <c>.NETStandard,Version=v2.0</c>, <c>.NETFramework4.6.1</c>).
    /// </summary>
    private static int RankTargetFramework(string? targetFramework)
    {
        var normalized = NormalizeTargetFramework(targetFramework);
        if (normalized.Length == 0)
        {
            return int.MinValue;
        }

        if (normalized.StartsWith("netstandard", StringComparison.Ordinal))
        {
            var version = ParseFrameworkVersion(normalized["netstandard".Length..]);
            if (version == null)
            {
                return 0;
            }

            return version.Major == 2
                ? 8000 + version.Minor
                : 7000 + version.Major * 10 + version.Minor;
        }

        if (normalized.StartsWith("netcoreapp", StringComparison.Ordinal))
        {
            var version = ParseFrameworkVersion(normalized["netcoreapp".Length..]);
            return version == null ? 0 : 9000 + version.Major * 10 + version.Minor;
        }

        if (normalized.StartsWith("netfx", StringComparison.Ordinal))
        {
            return 6000 + ParseFrameworkDigits(normalized["netfx".Length..]);
        }

        if (!normalized.StartsWith("net", StringComparison.Ordinal))
        {
            return 0;
        }

        var rest = normalized[3..];
        if (!rest.Contains('.'))
        {
            // net461 / net48 style .NET Framework shorthand.
            return 6000 + ParseFrameworkDigits(rest);
        }

        var modern = ParseFrameworkVersion(rest);
        return modern == null ? 0 : 10000 + modern.Major * 100 + modern.Minor;
    }

    private static string NormalizeTargetFramework(string? targetFramework)
    {
        if (string.IsNullOrWhiteSpace(targetFramework))
        {
            return string.Empty;
        }

        var value = targetFramework.Trim();
        if (value.StartsWith('.'))
        {
            value = value[1..];
            var comma = value.IndexOf(',');
            if (comma >= 0)
            {
                var name = value[..comma];
                var rest = value[(comma + 1)..].Trim();
                if (rest.StartsWith("Version=", StringComparison.OrdinalIgnoreCase))
                {
                    rest = rest["Version=".Length..];
                }

                rest = rest.TrimStart('v', 'V');
                value = name + rest;
            }
        }

        var dash = value.IndexOf('-');
        if (dash > 0)
        {
            value = value[..dash]; // net9.0-windows -> net9.0
        }

        var index = 0;
        while (index < value.Length && !char.IsDigit(value[index]))
        {
            index++;
        }

        var prefix = value[..index].TrimEnd('.', '-').ToLowerInvariant();
        var version = value[index..];
        return prefix switch
        {
            "netstandard" or "netplatform" => "netstandard" + version,
            "netcoreapp" => "netcoreapp" + version,
            "netframework" => "netfx" + version.Replace(".", string.Empty),
            _ => prefix + version,
        };
    }

    private static Version? ParseFrameworkVersion(string value)
        => Version.TryParse(value, out var version) ? version : null;

    private static int ParseFrameworkDigits(string value)
        => int.TryParse(value, out var digits) ? Math.Min(digits, 999) : 0;

    /// <summary>
    /// Dependencies of one package: the most appropriate <c>group</c> when the
    /// nuspec groups by target framework, otherwise the flat dependency list.
    /// </summary>
    private static IReadOnlyList<(string Id, string Version)> ReadNuspecDependencies(
        string packageDirectory)
    {
        string[] nuspecFiles;
        try
        {
            nuspecFiles = Directory.GetFiles(
                packageDirectory, "*.nuspec", SearchOption.TopDirectoryOnly);
        }
        catch (IOException)
        {
            return Array.Empty<(string, string)>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<(string, string)>();
        }

        if (nuspecFiles.Length == 0)
        {
            return Array.Empty<(string, string)>();
        }

        XDocument document;
        try
        {
            document = XDocument.Load(nuspecFiles[0]);
        }
        catch (IOException)
        {
            return Array.Empty<(string, string)>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<(string, string)>();
        }
        catch (System.Xml.XmlException)
        {
            return Array.Empty<(string, string)>();
        }

        var dependenciesElement = document.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "dependencies");
        if (dependenciesElement == null)
        {
            return Array.Empty<(string, string)>();
        }

        var groups = dependenciesElement.Elements()
            .Where(element => element.Name.LocalName == "group")
            .ToList();
        var dependencyElements = groups.Count > 0
            ? groups
                .OrderByDescending(group => RankTargetFramework(
                    group.Attribute("targetFramework")?.Value))
                .First()
                .Elements()
                .Where(element => element.Name.LocalName == "dependency")
            : dependenciesElement.Elements()
                .Where(element => element.Name.LocalName == "dependency");

        var result = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in dependencyElements)
        {
            var id = element.Attribute("id")?.Value;
            var version = element.Attribute("version")?.Value;
            if (string.IsNullOrWhiteSpace(id)
                || string.IsNullOrWhiteSpace(version)
                || id.Contains("$(", StringComparison.Ordinal))
            {
                continue;
            }

            if (seen.Add(id + "/" + version))
            {
                result.Add((id, version));
            }
        }

        return result;
    }

    /// <summary>
    /// Enumeration result reduced to one assembly per simple name (highest
    /// version). The merged pass compilation unions every project's package
    /// assemblies, so different projects can contribute different versions of
    /// the same assembly; referencing two of them makes their types ambiguous
    /// (CS0433) and overload resolution never binds.
    /// </summary>
    public static IReadOnlyList<MetadataReference> EnumerateOnePerAssemblyName(
        string root, out int addedCount)
    {
        var bySimpleName = new Dictionary<string, (Version Version, MetadataReference Reference)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var reference in Enumerate(root, out _))
        {
            var path = reference.Display ?? string.Empty;
            var simpleName = Path.GetFileNameWithoutExtension(path);
            if (simpleName.Length == 0)
            {
                continue;
            }

            var version = ParsePackageVersion(path) ?? new Version(0, 0);
            if (!bySimpleName.TryGetValue(simpleName, out var existing)
                || version > existing.Version)
            {
                bySimpleName[simpleName] = (version, reference);
            }
        }

        addedCount = bySimpleName.Count;
        return bySimpleName.Values.Select(entry => entry.Reference).ToList();
    }

    /// <summary>
    /// Extracts the package version directory (e.g. <c>…/xunit.assert/2.9.3/lib/…</c>)
    /// from a restored assembly path; null when the path carries no version segment.
    /// </summary>
    private static Version? ParsePackageVersion(string path)
    {
        foreach (var segment in path.Split(
                     Path.DirectorySeparatorChar,
                     Path.AltDirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (!char.IsDigit(segment[0]))
            {
                continue;
            }

            var numeric = segment.Split('-', '+')[0];
            if (Version.TryParse(numeric, out var version))
            {
                return version;
            }
        }

        return null;
    }

    private static void ReadAssetsFile(
        string assetsFile,
        Dictionary<string, MetadataReference> references,
        HashSet<string> tpaSimpleNames)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(assetsFile));
        var root = document.RootElement;

        var packageFolders = new List<string>();
        if (root.TryGetProperty("packageFolders", out var folderElement)
            && folderElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var folder in folderElement.EnumerateObject())
            {
                packageFolders.Add(folder.Name);
            }
        }

        if (packageFolders.Count == 0 || !root.TryGetProperty("libraries", out var librariesElement)
            || !root.TryGetProperty("targets", out var targetsElement))
        {
            return;
        }

        foreach (var target in targetsElement.EnumerateObject())
        {
            if (target.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var libraryEntry in target.Value.EnumerateObject())
            {
                var libraryId = libraryEntry.Name; // "Package.Id/1.2.3"
                var entry = libraryEntry.Value;
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!IsPackageLibrary(librariesElement, libraryId))
                {
                    continue; // project references: sources are already merged
                }

                if (!TryGetLibraryRelativePath(librariesElement, libraryId, out var libraryRelativePath))
                {
                    continue;
                }

                var dllKeys = EnumerateCompileAssemblies(entry);
                foreach (var dllKey in dllKeys)
                {
                    var simpleName = Path.GetFileNameWithoutExtension(dllKey);
                    if (tpaSimpleNames.Contains(simpleName))
                    {
                        continue;
                    }

                    foreach (var folder in packageFolders)
                    {
                        var fullPath = Path.GetFullPath(Path.Combine(
                            folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                            libraryRelativePath,
                            dllKey.Replace('/', Path.DirectorySeparatorChar)));
                        if (!File.Exists(fullPath) || references.ContainsKey(fullPath))
                        {
                            continue;
                        }

                        references[fullPath] = MetadataReference.CreateFromFile(fullPath);
                        break;
                    }
                }
            }
        }
    }

    private static bool IsPackageLibrary(JsonElement librariesElement, string libraryId)
    {
        if (!librariesElement.TryGetProperty(libraryId, out var library))
        {
            return false;
        }

        return library.TryGetProperty("type", out var typeElement)
               && string.Equals(typeElement.GetString(), "package", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetLibraryRelativePath(
        JsonElement librariesElement,
        string libraryId,
        out string relativePath)
    {
        if (librariesElement.TryGetProperty(libraryId, out var library)
            && library.TryGetProperty("path", out var pathElement)
            && pathElement.ValueKind == JsonValueKind.String)
        {
            relativePath = pathElement.GetString()!;
            return true;
        }

        // Default NuGet layout: lowercased package id + version segment.
        var slash = libraryId.IndexOf('/');
        if (slash <= 0 || slash >= libraryId.Length - 1)
        {
            relativePath = string.Empty;
            return false;
        }

        relativePath = libraryId[..slash].ToLowerInvariant()
                       + Path.DirectorySeparatorChar
                       + libraryId[(slash + 1)..];
        return true;
    }

    private static IEnumerable<string> EnumerateCompileAssemblies(JsonElement libraryTargetEntry)
    {
        if (libraryTargetEntry.TryGetProperty("compile", out var compileElement)
            && compileElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in compileElement.EnumerateObject())
            {
                if (item.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    yield return item.Name;
                }
            }

            yield break;
        }

        if (libraryTargetEntry.TryGetProperty("runtime", out var runtimeElement)
            && runtimeElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in runtimeElement.EnumerateObject())
            {
                if (item.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    && item.Name.StartsWith("lib/", StringComparison.OrdinalIgnoreCase))
                {
                    yield return item.Name;
                }
            }
        }
    }

    private static HashSet<string> GetTrustedPlatformAssemblySimpleNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var trustedAssemblies = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string)?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        if (trustedAssemblies != null)
        {
            foreach (var assembly in trustedAssemblies)
            {
                names.Add(Path.GetFileNameWithoutExtension(assembly));
            }
        }

        return names;
    }
}
