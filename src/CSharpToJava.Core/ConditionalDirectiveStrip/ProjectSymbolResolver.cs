using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CSharpToJava.Core.ConditionalDirectiveStrip;

public sealed record ProjectSymbols(
    IReadOnlyList<string> Symbols,
    IReadOnlyList<string> Warnings,
    bool PreserveDirectives = false);

public static class ProjectSymbolResolver
{
    private static readonly string[] NetStandardVersions = ["1.0", "1.1", "1.2", "1.3", "1.4", "1.5", "1.6", "2.0", "2.1"];
    private static readonly string[] NetCoreAppVersions = ["1.0", "1.1", "2.0", "2.1", "2.2", "3.0", "3.1"];
    private static readonly string[] NetFxVersions = ["20", "35", "40", "45", "451", "452", "46", "461", "462", "47", "471", "472", "48", "481"];

    public static ProjectSymbols ResolveForFile(
        string filePath,
        string configuration = "Release",
        string? tfm = null,
        IEnumerable<string>? extraDefines = null,
        string? resolvedTfm = null,
        string? sourceRoot = null,
        Func<string, string?>? projectTfmResolver = null,
        string? sourceCode = null)
    {
        // Shared source files are compiled by several projects with different
        // DefineConstants. Stripping them once with a single symbol set picks
        // one project's branch and breaks the others (CS0433 for public/internal
        // splits, CS0103 for member definitions). Resolve the real owners when a
        // project tree is known; otherwise keep the historical nearest-project
        // behavior for single-project / loose files.
        var treeRoot = FirstNonEmpty(sourceRoot);
        if (treeRoot == null || !IsSameOrBelow(Path.GetFullPath(filePath), Path.GetFullPath(treeRoot)))
        {
            // The caller-supplied root does not contain this file (for example a
            // later pass reading an intermediate tree); fall back to the tree
            // discovered from the file itself.
            treeRoot = FindProjectTreeRoot(filePath);
        }

        if (treeRoot != null &&
            IsSameOrBelow(Path.GetFullPath(filePath), Path.GetFullPath(treeRoot)))
        {
            return ResolveForTreeOwners(
                filePath, treeRoot, configuration, tfm, extraDefines, resolvedTfm, projectTfmResolver, sourceCode);
        }

        return ResolveForProject(
            FindNearestProjectForFile(filePath), configuration, tfm, extraDefines, resolvedTfm);
    }

    private static ProjectSymbols ResolveForTreeOwners(
        string filePath,
        string sourceRoot,
        string configuration,
        string? tfm,
        IEnumerable<string>? extraDefines,
        string? resolvedTfm,
        Func<string, string?>? projectTfmResolver,
        string? sourceCode)
    {
        var index = ProjectCompileIndex.ForRoot(sourceRoot, configuration);
        if (index.ProjectCount == 0)
        {
            // No project tree at all: preserve the historical behavior (loose
            // files are stripped with the nearest project / empty symbols).
            return ResolveForProject(
                FindNearestProjectForFile(filePath), configuration, tfm, extraDefines, resolvedTfm);
        }

        var owners = index.FindOwningProjects(filePath);
        if (owners.Count == 0)
        {
            // Not compiled by any project: stripping would guess symbols and can
            // only corrupt the file, so keep the directives untouched and let the
            // real compiler evaluate them.
            return new ProjectSymbols(Array.Empty<string>(), Array.Empty<string>(), PreserveDirectives: true);
        }

        var nearest = FindNearestProjectForFile(filePath);
        string? TfmFor(string project) =>
            string.Equals(project, nearest, StringComparison.OrdinalIgnoreCase)
                ? resolvedTfm
                : projectTfmResolver?.Invoke(project);

        if (owners.Count == 1)
        {
            return ResolveForProject(owners[0], configuration, tfm, extraDefines, TfmFor(owners[0]));
        }

        var results = owners
            .Select(owner => ResolveForProject(owner, configuration, tfm, extraDefines, TfmFor(owner)))
            .ToList();
        if (results.All(result => SymbolSetsEqual(results[0], result))
            || (sourceCode != null && ProduceSameStrippedOutput(sourceCode, results)))
        {
            // Either the symbol sets are identical, or the differing symbols do
            // not influence any directive in this file: one stripped form is
            // valid for every owner.
            return results[0];
        }

        // Conflicting symbol sets: no single stripped form works for every
        // owner, so preserve the directives (the C# compiler then evaluates
        // them per project) and report one warning for the file.
        var warning =
            $"preserved conditional directives: file is compiled by {owners.Count} projects " +
            $"with different preprocessor symbols ({string.Join(", ", owners.Select(Path.GetFileName))})";
        return new ProjectSymbols(
            Array.Empty<string>(),
            [warning],
            PreserveDirectives: true);
    }

    private static bool SymbolSetsEqual(ProjectSymbols left, ProjectSymbols right)
        => new HashSet<string>(left.Symbols, StringComparer.Ordinal)
            .SetEquals(new HashSet<string>(right.Symbols, StringComparer.Ordinal));

    /// <summary>
    /// True when every owner's symbol set selects the same branches for
    /// <paramref name="sourceCode"/>. Symbol sets can differ in macros the file
    /// never tests (for example SYSTEM_TEXT_REGULAREXPRESSIONS); rejecting the
    /// strip in that case only loses information and can desynchronize the file
    /// from consumers stripped elsewhere.
    /// </summary>
    private static bool ProduceSameStrippedOutput(
        string sourceCode,
        IReadOnlyList<ProjectSymbols> results)
    {
        string? first = null;
        foreach (var result in results)
        {
            var output = new ConditionalDirectiveStripper().Strip(
                sourceCode,
                new ConditionalDirectiveStripOptions { Symbols = result.Symbols }).OutputCode;
            if (first == null)
            {
                first = output;
            }
            else if (!string.Equals(first, output, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static ProjectSymbols ResolveForProject(
        string? project,
        string configuration,
        string? tfm,
        IEnumerable<string>? extraDefines,
        string? resolvedTfm)
    {
        var warnings = new List<string>();
        var symbols = new List<string>();
        var tfms = new List<string>();
        var externalTfm = FirstNonEmpty(tfm) ?? FirstNonEmpty(resolvedTfm);

        if (project != null)
        {
            foreach (var props in GetAncestorDirectoryBuildProps(project).Reverse())
            {
                ApplyDefineConstants(props, configuration, symbols, warnings);
            }

            ApplyDefineConstants(project, configuration, symbols, warnings);
            // An explicitly supplied TFM (CLI --tfm) or one already resolved by
            // MSBuild evaluation is authoritative: never fall back to the XML
            // read, which would emit "skipped unresolvable TFM value" for
            // repository macros such as $(NetCoreAppCurrent).
            if (externalTfm == null)
            {
                tfms = ReadTfms(project, configuration, warnings, out _);
            }
        }

        // SDK-style projects define DEBUG for the Debug configuration through
        // MSBuild defaults, not the csproj XML; synthesize it so `#if DEBUG`
        // members (RegexCode.ToString, RegexOptions.Debug, …) survive the strip.
        if (string.Equals(configuration, "Debug", StringComparison.OrdinalIgnoreCase)
            && !symbols.Contains("DEBUG"))
        {
            symbols.Add("DEBUG");
        }

        var chosenTfm = PickTfm(tfms, externalTfm);
        if (chosenTfm != null && !TrySynthesizeTfmSymbols(chosenTfm, symbols))
        {
            warnings.Add($"unrecognized TFM '{chosenTfm}'");
        }

        if (extraDefines != null)
        {
            symbols.AddRange(extraDefines
                .Where(symbol => !string.IsNullOrWhiteSpace(symbol))
                .Select(symbol => symbol.Trim()));
        }

        return new ProjectSymbols(Dedup(symbols), warnings);
    }

    /// <summary>
    /// Resolve the project that actually compiles <paramref name="filePath"/>.
    /// A directory can host more than one project (for example the runtime's
    /// System.Private.CoreLib directory also contains a Globalization project);
    /// taking the first project by path would apply the wrong DefineConstants
    /// and select the wrong conditional branches.
    /// </summary>
    internal static string? FindNearestProjectForFile(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var directory = Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrEmpty(directory))
        {
            var projects = Directory.EnumerateFiles(directory, "*.csproj")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(Path.GetFullPath)
                .ToList();
            if (projects.Count == 1)
            {
                return projects[0];
            }

            if (projects.Count > 1)
            {
                var owners = projects
                    .Where(project => ProjectCompilesFile(project, fullPath))
                    .ToList();
                return owners.Count > 0 ? owners[0] : projects[0];
            }

            var parent = Path.GetDirectoryName(directory);
            if (parent == null || string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            directory = parent;
        }

        return null;
    }

    /// <summary>
    /// Locate the source tree that owns <paramref name="filePath"/> by walking up
    /// to the outermost directory containing a solution file. Shared sources are
    /// linked from projects anywhere below that root, so the nearest directory
    /// (or nearest csproj) is not enough to find every real compiler.
    /// </summary>
    internal static string? FindProjectTreeRoot(string filePath)
    {
        string? fullPath;
        try
        {
            fullPath = Path.GetFullPath(filePath);
        }
        catch (Exception)
        {
            return null;
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        var cached = TreeRootByDirectory.GetOrAdd(directory, static start =>
        {
            string? root = null;
            var current = start;
            while (!string.IsNullOrEmpty(current))
            {
                try
                {
                    if (Directory.EnumerateFiles(current, "*.sln").Any())
                    {
                        root = current;
                    }
                }
                catch (Exception)
                {
                    // Unreadable ancestor: keep the best root found so far.
                }

                var parent = Path.GetDirectoryName(current);
                if (parent == null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                current = parent;
            }

            return root;
        });

        return cached;
    }

    private static readonly ConcurrentDictionary<string, string?> TreeRootByDirectory =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Projects that compile <paramref name="filePath"/>, resolved from the
    /// cached compile-ownership index of the tree rooted at <paramref name="sourceRoot"/>.
    /// </summary>
    internal static IReadOnlyList<string> FindOwningProjects(
        string filePath,
        string sourceRoot,
        string configuration)
        => ProjectCompileIndex.ForRoot(sourceRoot, configuration).FindOwningProjects(filePath);

    private static bool ProjectCompilesFile(string projectPath, string filePath)
    {
        var document = LoadDocument(projectPath, warnings: null);
        if (document?.Root == null)
        {
            return false;
        }

        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        bool? included = null;
        foreach (var element in document.Root.Descendants().Where(element =>
                     element.Name.LocalName.Equals("Compile", StringComparison.OrdinalIgnoreCase)))
        {
            var include = AttributeNamed(element, "Include");
            if (include != null && PatternsMatch(include, projectDirectory, filePath))
            {
                included = true;
                continue;
            }

            var remove = AttributeNamed(element, "Remove");
            if (remove != null && PatternsMatch(remove, projectDirectory, filePath))
            {
                included = false;
            }
        }

        if (included.HasValue)
        {
            return included.Value;
        }

        // No explicit Compile item claims the file: SDK projects include
        // sources below their directory unless default items are disabled.
        var enableDefault = document.Root.Descendants()
            .Where(element => element.Name.LocalName.Equals("EnableDefaultCompileItems", StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Value.Trim())
            .LastOrDefault();
        if (string.Equals(enableDefault, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsSameOrBelow(filePath, projectDirectory);
    }

    private static bool PatternsMatch(string value, string projectDirectory, string filePath)
        => value.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(pattern => pattern.Trim())
            .Where(pattern => pattern.Length > 0)
            .Any(pattern => GlobMatches(pattern, projectDirectory, filePath));

    private static bool GlobMatches(string pattern, string projectDirectory, string filePath)
    {
        var expanded = pattern.Replace(
            "$(MSBuildThisFileDirectory)",
            projectDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
        if (expanded.Contains("$(", StringComparison.Ordinal))
        {
            return false;
        }

        var combined = Path.IsPathRooted(expanded)
            ? expanded
            : Path.Combine(projectDirectory, expanded);
        var normalizedPattern = NormalizePath(combined);
        var normalizedFile = NormalizePath(filePath);
        var regex = "^" + Regex.Escape(normalizedPattern)
            .Replace(@"\*\*/", "(?:.*/)?", StringComparison.Ordinal)
            .Replace(@"\*\*", ".*", StringComparison.Ordinal)
            .Replace(@"\*", "[^/]*", StringComparison.Ordinal)
            .Replace(@"\?", "[^/]", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(normalizedFile, regex, RegexOptions.IgnoreCase);
    }

    internal static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        var root = string.Empty;
        if (normalized.Length >= 2 && normalized[1] == ':')
        {
            root = normalized[..2] + "/";
            normalized = normalized[3..];
        }
        else if (normalized.StartsWith("/", StringComparison.Ordinal))
        {
            root = "/";
            normalized = normalized[1..];
        }

        var segments = new List<string>();
        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(segment);
        }

        return root + string.Join('/', segments);
    }

    internal static bool IsSameOrBelow(string path, string rootDirectory)
    {
        var normalizedPath = NormalizePath(path);
        var normalizedRoot = NormalizePath(rootDirectory);
        return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? FindNearestProject(string? startDirectory)
    {
        var directory = startDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            var project = Directory.EnumerateFiles(directory, "*.csproj")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (project != null)
            {
                return Path.GetFullPath(project);
            }

            var parent = Path.GetDirectoryName(directory);
            if (parent == null || string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            directory = parent;
        }

        return null;
    }

    private static IEnumerable<string> GetAncestorDirectoryBuildProps(string projectPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        while (!string.IsNullOrEmpty(directory))
        {
            var props = Path.Combine(directory, "Directory.Build.props");
            if (File.Exists(props))
            {
                yield return props;
            }

            var parent = Path.GetDirectoryName(directory);
            if (parent == null || string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase))
            {
                yield break;
            }

            directory = parent;
        }
    }

    private static void ApplyDefineConstants(
        string file,
        string configuration,
        List<string> symbols,
        List<string> warnings)
    {
        var document = LoadDocument(file, warnings);
        if (document?.Root == null)
        {
            return;
        }

        foreach (var group in ChildrenNamed(document.Root, "PropertyGroup"))
        {
            if (!EvaluateCondition(AttributeNamed(group, "Condition"), configuration))
            {
                continue;
            }

            foreach (var element in ChildrenNamed(group, "DefineConstants"))
            {
                if (!EvaluateCondition(AttributeNamed(element, "Condition"), configuration))
                {
                    continue;
                }

                var value = element.Value;
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var appends = false;
                var parts = new List<string>();
                foreach (var raw in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var part = raw.Trim();
                    if (part.Length == 0)
                    {
                        continue;
                    }

                    if (part.Equals("$(DefineConstants)", StringComparison.OrdinalIgnoreCase))
                    {
                        appends = true;
                        continue;
                    }

                    if (part.Contains("$(", StringComparison.Ordinal))
                    {
                        warnings.Add($"skipped unresolvable macro '{part}' in {file}");
                        continue;
                    }

                    parts.Add(part);
                }

                if (!appends)
                {
                    symbols.Clear();
                }

                symbols.AddRange(parts);
            }
        }
    }

    internal static bool EvaluateCondition(string? condition, string configuration)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return true;
        }

        foreach (var orGroup in Regex.Split(condition, @"\s+or\s+", RegexOptions.IgnoreCase))
        {
            var all = true;
            foreach (var clause in Regex.Split(orGroup, @"\s+and\s+", RegexOptions.IgnoreCase))
            {
                var match = Regex.Match(
                    clause,
                    @"^\s*'?\$\(Configuration\)'?\s*(==|!=)\s*['""]([^'""]*)['""]\s*$",
                    RegexOptions.IgnoreCase);
                if (!match.Success)
                {
                    continue;
                }

                var equal = string.Equals(configuration, match.Groups[2].Value, StringComparison.OrdinalIgnoreCase);
                if ((match.Groups[1].Value == "==") != equal)
                {
                    all = false;
                    break;
                }
            }

            if (all)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 纯 XML 读取项目声明的 TFM 列表。<paramref name="unresolved"/> 表示读到
    /// 含 <c>$(</c> 的原始值（例如运行时仓库的 <c>$(NetCoreAppCurrent)</c>）；
    /// 此时调用方应升级到 MSBuild 真实求值，而不是静默丢弃全部 TFM。
    /// <paramref name="warnings"/> 为 null 时只做探测、不产生告警。
    /// </summary>
    internal static List<string> ReadTfms(
        string projectPath,
        string configuration,
        List<string>? warnings,
        out bool unresolved)
    {
        unresolved = false;
        var result = new List<string>();
        var document = LoadDocument(projectPath, warnings);
        if (document?.Root == null)
        {
            return result;
        }

        foreach (var group in ChildrenNamed(document.Root, "PropertyGroup"))
        {
            if (!EvaluateCondition(AttributeNamed(group, "Condition"), configuration))
            {
                continue;
            }

            foreach (var element in group.Elements().Where(element =>
                         element.Name.LocalName.Equals("TargetFramework", StringComparison.OrdinalIgnoreCase) ||
                         element.Name.LocalName.Equals("TargetFrameworks", StringComparison.OrdinalIgnoreCase)))
            {
                if (!EvaluateCondition(AttributeNamed(element, "Condition"), configuration))
                {
                    continue;
                }

                var value = element.Value.Trim();
                if (value.Length == 0)
                {
                    continue;
                }

                if (value.Contains("$(", StringComparison.Ordinal))
                {
                    unresolved = true;
                    warnings?.Add($"skipped unresolvable TFM value '{value}' in {projectPath}");
                    continue;
                }

                result.Clear();
                result.AddRange(value.Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Select(item => item.Trim())
                    .Where(item => item.Length > 0));
            }
        }

        return result;
    }

    internal static string? PickTfm(IReadOnlyList<string> tfms, string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return requested.Trim();
        }

        return tfms.FirstOrDefault();
    }

    internal static bool TrySynthesizeTfmSymbols(string tfm, List<string> symbols)
    {
        var value = tfm.Trim();
        const string platformSuffix = @"(?:-[A-Za-z0-9.]+)?$";

        var netStandard = Regex.Match(value, @"^netstandard(\d+)\.(\d+)" + platformSuffix, RegexOptions.IgnoreCase);
        if (netStandard.Success)
        {
            var target = $"{netStandard.Groups[1].Value}.{netStandard.Groups[2].Value}";
            symbols.Add("NETSTANDARD");
            symbols.Add($"NETSTANDARD{VersionToSymbol(target)}");
            foreach (var lower in NetStandardVersions.Where(version => CompareVersions(version, target) <= 0))
            {
                symbols.Add($"NETSTANDARD{VersionToSymbol(lower)}_OR_GREATER");
            }

            return true;
        }

        var netCoreApp = Regex.Match(value, @"^netcoreapp(\d+)\.(\d+)" + platformSuffix, RegexOptions.IgnoreCase);
        if (netCoreApp.Success)
        {
            var target = $"{netCoreApp.Groups[1].Value}.{netCoreApp.Groups[2].Value}";
            symbols.Add("NETCOREAPP");
            symbols.Add($"NETCOREAPP{VersionToSymbol(target)}");
            foreach (var lower in NetCoreAppVersions.Where(version => CompareVersions(version, target) <= 0))
            {
                symbols.Add($"NETCOREAPP{VersionToSymbol(lower)}_OR_GREATER");
            }

            return true;
        }

        var netModern = Regex.Match(value, @"^net(\d+)\.(\d+)" + platformSuffix, RegexOptions.IgnoreCase);
        if (netModern.Success && int.Parse(netModern.Groups[1].Value) >= 5)
        {
            var major = int.Parse(netModern.Groups[1].Value);
            var minor = int.Parse(netModern.Groups[2].Value);
            symbols.Add("NET");
            symbols.Add($"NET{major}_{minor}");
            symbols.Add("NETCOREAPP");
            symbols.Add($"NET{major}_{minor}_OR_GREATER");
            for (var lowerMajor = 5; lowerMajor < major; lowerMajor++)
            {
                symbols.Add($"NET{lowerMajor}_0_OR_GREATER");
            }

            foreach (var lower in NetCoreAppVersions)
            {
                symbols.Add($"NETCOREAPP{VersionToSymbol(lower)}_OR_GREATER");
            }

            return true;
        }

        var netFramework = Regex.Match(value, @"^net(\d{2,3})" + platformSuffix, RegexOptions.IgnoreCase);
        if (netFramework.Success)
        {
            var code = netFramework.Groups[1].Value;
            symbols.Add("NETFRAMEWORK");
            symbols.Add($"NET{code}");
            foreach (var lower in NetFxVersions.Where(version => CompareNetFxVersions(version, code) <= 0))
            {
                symbols.Add($"NET{lower}_OR_GREATER");
            }

            return true;
        }

        return false;
    }

    private static XDocument? LoadDocument(string path, List<string>? warnings)
    {
        try
        {
            return XDocument.Load(path);
        }
        catch (Exception exception)
        {
            warnings?.Add($"cannot read {path}: {exception.Message}");
            return null;
        }
    }

    private static string? FirstNonEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IEnumerable<XElement> ChildrenNamed(XContainer parent, string localName) =>
        parent.Elements().Where(element => element.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));

    private static string? AttributeNamed(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))?.Value;

    private static string VersionToSymbol(string version) => version.Replace('.', '_');

    private static int CompareVersions(string left, string right)
    {
        var leftParts = left.Split('.').Select(int.Parse).ToArray();
        var rightParts = right.Split('.').Select(int.Parse).ToArray();
        for (var index = 0; index < Math.Max(leftParts.Length, rightParts.Length); index++)
        {
            var comparison = (index < leftParts.Length ? leftParts[index] : 0)
                .CompareTo(index < rightParts.Length ? rightParts[index] : 0);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    private static int CompareNetFxVersions(string leftCode, string rightCode) =>
        (ToNetFxTuple(leftCode), ToNetFxTuple(rightCode)) switch
        {
            var (left, right) when left.major != right.major => left.major.CompareTo(right.major),
            var (left, right) when left.minor != right.minor => left.minor.CompareTo(right.minor),
            var (left, right) => left.patch.CompareTo(right.patch),
        };

    private static (int major, int minor, int patch) ToNetFxTuple(string code) => code.Length switch
    {
        2 => (code[0] - '0', code[1] - '0', 0),
        3 => (code[0] - '0', code[1] - '0', code[2] - '0'),
        _ => (0, 0, 0),
    };

    private static List<string> Dedup(IEnumerable<string> symbols)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var symbol in symbols)
        {
            if (seen.Add(symbol))
            {
                result.Add(symbol);
            }
        }

        return result;
    }
}

/// <summary>
/// Compile-ownership index for every project below a source tree. Shared
/// sources are linked into projects with explicit <c>Compile Include</c> items
/// (and into SDK projects through the default item glob), so deciding "which
/// project compiles this file" requires the whole tree, not the nearest csproj.
/// Built once per (root, configuration) and cached for the preprocessing run.
/// </summary>
internal sealed class ProjectCompileIndex
{
    private static readonly ConcurrentDictionary<string, ProjectCompileIndex> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<IndexedCompileProject> _projects;

    public static ProjectCompileIndex ForRoot(string root, string configuration)
    {
        var fullRoot = Path.GetFullPath(root);
        if (File.Exists(fullRoot))
        {
            fullRoot = Path.GetDirectoryName(fullRoot)!;
        }

        var key = fullRoot + "\0" + configuration;
        return Cache.GetOrAdd(key, _ => new ProjectCompileIndex(fullRoot, configuration));
    }

    private ProjectCompileIndex(string root, string configuration)
    {
        _projects = EnumerateProjectFiles(root)
            .Select(project => IndexedCompileProject.TryLoad(project, configuration))
            .Where(project => project != null)
            .Select(project => project!)
            .OrderBy(project => project.ProjectPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public int ProjectCount => _projects.Count;

    public IReadOnlyList<string> FindOwningProjects(string filePath)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(filePath);
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }

        return _projects
            .Where(project => project.Compiles(fullPath))
            .Select(project => project.ProjectPath)
            .ToList();
    }

    private static IEnumerable<string> EnumerateProjectFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> projects;
            try
            {
                projects = Directory.EnumerateFiles(current, "*.csproj").ToList();
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var project in projects)
            {
                yield return Path.GetFullPath(project);
            }

            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(current).ToList();
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var directory in directories)
            {
                if (IsIgnoredDirectoryName(Path.GetFileName(directory)))
                {
                    continue;
                }

                pending.Push(Path.GetFullPath(directory));
            }
        }
    }

    internal static bool IsIgnoredDirectoryName(string name)
        => name.Equals("bin", StringComparison.OrdinalIgnoreCase)
           || name.Equals("obj", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One project's ordered compile items, parsed from its csproj XML.</summary>
internal sealed class IndexedCompileProject
{
    private sealed record CompileRule(bool Include, string Pattern);

    private readonly List<CompileRule> _rules;
    private readonly bool _defaultCompileItems;

    private IndexedCompileProject(
        string projectPath,
        List<CompileRule> rules,
        bool defaultCompileItems)
    {
        ProjectPath = projectPath;
        ProjectDirectory = Path.GetDirectoryName(projectPath)!;
        _rules = rules;
        _defaultCompileItems = defaultCompileItems;
    }

    public string ProjectPath { get; }
    public string ProjectDirectory { get; }

    public static IndexedCompileProject? TryLoad(string projectPath, string configuration)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(projectPath);
        }
        catch (Exception)
        {
            return null;
        }

        if (document.Root == null)
        {
            return null;
        }

        var rules = new List<CompileRule>();
        var defaultCompileItems = true;
        foreach (var element in document.Root.Descendants())
        {
            var name = element.Name.LocalName;
            if (name.Equals("EnableDefaultCompileItems", StringComparison.OrdinalIgnoreCase))
            {
                if (ProjectSymbolResolver.EvaluateCondition(AttributeNamed(element, "Condition"), configuration))
                {
                    defaultCompileItems = !element.Value.Trim()
                        .Equals("false", StringComparison.OrdinalIgnoreCase);
                }

                continue;
            }

            if (!name.Equals("Compile", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A condition that cannot be evaluated is treated as included, which
            // over-attributes rather than silently dropping an owner.
            if (!ProjectSymbolResolver.EvaluateCondition(AttributeNamed(element, "Condition"), configuration))
            {
                continue;
            }

            var include = AttributeNamed(element, "Include");
            if (!string.IsNullOrWhiteSpace(include))
            {
                rules.Add(new CompileRule(Include: true, include));
            }

            var remove = AttributeNamed(element, "Remove");
            if (!string.IsNullOrWhiteSpace(remove))
            {
                rules.Add(new CompileRule(Include: false, remove));
            }
        }

        return new IndexedCompileProject(projectPath, rules, defaultCompileItems);
    }

    public bool Compiles(string filePath)
    {
        bool? included = null;
        foreach (var rule in _rules)
        {
            if (PatternsMatch(rule.Pattern, ProjectDirectory, filePath))
            {
                included = rule.Include;
            }
        }

        if (included.HasValue)
        {
            return included.Value;
        }

        return _defaultCompileItems
               && ProjectSymbolResolver.IsSameOrBelow(filePath, ProjectDirectory)
               && !IsUnderIgnoredDirectory(filePath, ProjectDirectory);
    }

    private static bool PatternsMatch(string value, string projectDirectory, string filePath)
        => value.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(pattern => pattern.Trim())
            .Where(pattern => pattern.Length > 0)
            .Any(pattern => GlobMatches(pattern, projectDirectory, filePath));

    private static bool GlobMatches(string pattern, string projectDirectory, string filePath)
    {
        var expanded = pattern.Replace(
            "$(MSBuildThisFileDirectory)",
            projectDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
        if (expanded.Contains("$(", StringComparison.Ordinal))
        {
            return false;
        }

        var combined = Path.IsPathRooted(expanded)
            ? expanded
            : Path.Combine(projectDirectory, expanded);
        return Regex.IsMatch(
            ProjectSymbolResolver.NormalizePath(filePath),
            "^" + GlobToRegex(ProjectSymbolResolver.NormalizePath(combined)) + "$",
            RegexOptions.IgnoreCase);
    }

    private static string GlobToRegex(string pattern)
        => Regex.Escape(pattern)
            .Replace(@"\*\*/", "(?:.*/)?", StringComparison.Ordinal)
            .Replace(@"\*\*", ".*", StringComparison.Ordinal)
            .Replace(@"\*", "[^/]*", StringComparison.Ordinal)
            .Replace(@"\?", "[^/]", StringComparison.Ordinal);

    private static bool IsUnderIgnoredDirectory(string filePath, string projectDirectory)
    {
        var relative = Path.GetRelativePath(projectDirectory, filePath);
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (ProjectCompileIndex.IsIgnoredDirectoryName(segment))
            {
                return true;
            }
        }

        return false;
    }

    private static string? AttributeNamed(XElement element, string localName)
        => element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))
            ?.Value;
}
