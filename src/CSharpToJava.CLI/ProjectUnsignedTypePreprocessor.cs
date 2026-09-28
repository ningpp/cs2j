using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.UnsignedTypeRewriter;
using Microsoft.CodeAnalysis;
using System.Xml.Linq;

namespace CSharpToJava.CLI;

internal sealed class ProjectUnsignedTypePreprocessRequest
{
    public required string SourcePath { get; init; }
    public required string DestinationRoot { get; init; }
    public bool Force { get; init; } = true;
    public bool Verbose { get; init; }
    public UnsignedTypeRewriterOptions RewriterOptions { get; init; } = new();
    public bool FilterUnsupportedProjects { get; init; } = true;

    /// <summary>
    /// Explicit namespace mappings from the type-mapping config (e.g.
    /// System.Windows.Forms → dotnet.winforms.swing). Windows-only projects whose
    /// platform namespaces are covered are kept instead of excluded.
    /// </summary>
    public IReadOnlyCollection<string>? CompatNamespacePrefixes { get; init; }
}

internal sealed class ProjectUnsignedTypePreprocessResult
{
    public required string OriginalSourcePath { get; init; }
    public required string SourceRoot { get; init; }
    public required string IntermediateRoot { get; init; }
    public required string PreprocessedSourcePath { get; init; }
    public required bool Success { get; init; }
    public required UnsignedTypeRewriterStatistics Statistics { get; init; }
    public required IReadOnlyList<UnsignedTypeRewriterDiagnostic> Diagnostics { get; init; }
}

/// <summary>
/// Thin compatibility facade over the unified <see cref="ProjectPassRunner"/> +
/// <see cref="UnsignedPassAdapter"/>. The shared reference-set / TFM helpers below
/// remain here because other (not-yet-migrated) passes and standalone verbs build
/// their own compilations through them.
/// </summary>
internal static class ProjectUnsignedTypePreprocessor
{
    public const string IntermediateDirectoryName = ".cs2j-unsigned-src";

    public static async Task<ProjectUnsignedTypePreprocessResult> PreprocessAsync(
        ProjectUnsignedTypePreprocessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationRoot);

        var adapter = new UnsignedPassAdapter(
            request.RewriterOptions,
            request.Verbose,
            request.FilterUnsupportedProjects,
            request.CompatNamespacePrefixes);

        var run = await ProjectPassRunner.RunAsync(
            adapter,
            new ProjectPassRunRequest
            {
                SourcePath = request.SourcePath,
                DestinationRoot = request.DestinationRoot,
                Force = request.Force,
            });

        return new ProjectUnsignedTypePreprocessResult
        {
            OriginalSourcePath = run.OriginalSourcePath,
            SourceRoot = run.SourceRoot,
            IntermediateRoot = run.IntermediateRoot,
            PreprocessedSourcePath = run.PreprocessedSourcePath,
            Success = run.Success,
            Statistics = adapter.Statistics,
            Diagnostics = adapter.Diagnostics.ToList(),
        };
    }

    internal static List<MetadataReference> GetBasicReferences()
    {
        var references = new List<MetadataReference>();
        var addedSimpleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?.Split(Path.PathSeparator);
        if (trustedAssemblies != null)
        {
            foreach (var assembly in trustedAssemblies)
            {
                if (File.Exists(assembly))
                {
                    references.Add(MetadataReference.CreateFromFile(assembly));
                    addedSimpleNames.Add(Path.GetFileNameWithoutExtension(assembly));
                }
            }
        }
        else
        {
            var coreAssembly = typeof(object).Assembly;
            references.Add(MetadataReference.CreateFromFile(coreAssembly.Location));
            addedSimpleNames.Add(coreAssembly.GetName().Name ?? "System.Private.CoreLib");
            var runtimeDir = Path.GetDirectoryName(coreAssembly.Location)!;
            var runtimeAssembly = Path.Combine(runtimeDir, "System.Runtime.dll");
            if (File.Exists(runtimeAssembly))
            {
                references.Add(MetadataReference.CreateFromFile(runtimeAssembly));
            }
        }

        AddWindowsDesktopReferences(references, addedSimpleNames);
        return references;
    }

    private static void AddWindowsDesktopReferences(List<MetadataReference> references, HashSet<string> addedSimpleNames)
    {
        var coreRuntimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (string.IsNullOrEmpty(coreRuntimeDir))
        {
            return;
        }

        var windowsDesktopRoot = Path.GetFullPath(Path.Combine(coreRuntimeDir, "..", "..", "Microsoft.WindowsDesktop.App"));
        if (!Directory.Exists(windowsDesktopRoot))
        {
            return;
        }

        var currentVersion = Environment.Version.ToString(3);
        var versions = Directory.EnumerateDirectories(windowsDesktopRoot)
            .Select(Path.GetFileName)
            .Where(v => !string.IsNullOrEmpty(v) && Version.TryParse(v, out _))
            .OrderByDescending(v => Version.Parse(v!))
            .ToList();
        var bestVersion = versions.FirstOrDefault(v => string.Equals(v, currentVersion, StringComparison.OrdinalIgnoreCase))
                          ?? versions.FirstOrDefault();
        if (bestVersion == null)
        {
            return;
        }

        foreach (var assembly in Directory.EnumerateFiles(Path.Combine(windowsDesktopRoot, bestVersion), "*.dll"))
        {
            var simpleName = Path.GetFileNameWithoutExtension(assembly);
            if (string.IsNullOrEmpty(simpleName) || !addedSimpleNames.Add(simpleName))
            {
                continue;
            }

            try
            {
                references.Add(MetadataReference.CreateFromFile(assembly));
            }
            catch (IOException)
            {
            }
            catch (BadImageFormatException)
            {
            }
        }
    }

    internal static string? GetFirstTargetFramework(XElement root)
    {
        var frameworks = GetPropertyValue(root, "TargetFrameworks");
        if (!string.IsNullOrWhiteSpace(frameworks))
        {
            return frameworks!.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        }

        return GetPropertyValue(root, "TargetFramework")?.Trim();
    }

    internal static bool IsTargetFrameworkAtLeastNet10(string? targetFramework)
    {
        if (string.IsNullOrWhiteSpace(targetFramework))
        {
            return false;
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            targetFramework,
            @"^net(?:coreapp)?(\d+(?:\.\d+)?)(?:-[a-z0-9.]+)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return false;
        }

        var versionText = match.Groups[1].Value;
        double version;
        if (versionText.Contains('.', StringComparison.Ordinal))
        {
            if (!double.TryParse(
                    versionText,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out version))
            {
                return false;
            }
        }
        else if (versionText.Length > 1
                 && versionText[0] == '4'
                 && int.TryParse(
                     versionText[1..],
                     System.Globalization.NumberStyles.None,
                     System.Globalization.CultureInfo.InvariantCulture,
                     out var frameworkMinor))
        {
            // 无点式 .NET Framework 版本（net472/net48）表示 4.<xx>。
            version = 4.0 + frameworkMinor / 100.0;
        }
        else if (!double.TryParse(
                     versionText,
                     System.Globalization.NumberStyles.Float,
                     System.Globalization.CultureInfo.InvariantCulture,
                     out version))
        {
            return false;
        }

        return version >= 10.0;
    }

    private static string? GetPropertyValue(XElement root, string propertyName)
    {
        return root.Descendants()
            .Where(element => element.Name.LocalName == propertyName)
            .Select(element => (string?)element.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}
