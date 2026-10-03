using System.Text.Json;
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
/// </summary>
internal static class RestorePackageReferenceEnumerator
{
    public static IReadOnlyList<MetadataReference> Enumerate(string intermediateRoot, out int addedCount)
    {
        var references = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);
        var tpaSimpleNames = GetTrustedPlatformAssemblySimpleNames();

        foreach (var assetsFile in Directory.EnumerateFiles(
                     intermediateRoot, "project.assets.json", SearchOption.AllDirectories))
        {
            var normalized = assetsFile.Replace('\\', '/');
            if (!normalized.EndsWith("/obj/project.assets.json", StringComparison.OrdinalIgnoreCase))
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
                // Malformed restore output: ignore this project; restore failure
                // is reported separately via CS2J1230.
            }
        }

        addedCount = references.Count;
        return references.Values.ToList();
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
