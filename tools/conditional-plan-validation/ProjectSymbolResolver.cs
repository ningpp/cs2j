using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CSharpToJava.Core.ConditionalDirectiveStrip;

public sealed record ProjectSymbols(IReadOnlyList<string> Symbols, IReadOnlyList<string> Warnings);

public static class ProjectSymbolResolver
{
    private static readonly string[] NetStandardVersions = ["1.0", "1.1", "1.2", "1.3", "1.4", "1.5", "1.6", "2.0", "2.1"];
    private static readonly string[] NetCoreAppVersions = ["1.0", "1.1", "2.0", "2.1", "2.2", "3.0", "3.1"];
    private static readonly string[] NetFxVersions = ["20", "35", "40", "45", "451", "452", "46", "461", "462", "47", "471", "472", "48", "481"];

    public static ProjectSymbols ResolveForFile(
        string filePath,
        string configuration = "Release",
        string? tfm = null,
        IEnumerable<string>? extraDefines = null)
    {
        var warnings = new List<string>();
        var symbols = new List<string>();
        var project = FindNearestProject(Path.GetDirectoryName(Path.GetFullPath(filePath)));
        var tfms = new List<string>();

        if (project != null)
        {
            foreach (var props in GetAncestorDirectoryBuildProps(project).Reverse())
            {
                ApplyDefineConstants(props, configuration, symbols, warnings);
            }

            ApplyDefineConstants(project, configuration, symbols, warnings);
            tfms = ReadTfms(project, configuration, warnings);
        }

        var chosenTfm = PickTfm(tfms, tfm);
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

    private static List<string> ReadTfms(string projectPath, string configuration, List<string> warnings)
    {
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
                    warnings.Add($"skipped unresolvable TFM value '{value}' in {projectPath}");
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

    private static XDocument? LoadDocument(string path, List<string> warnings)
    {
        try
        {
            return XDocument.Load(path);
        }
        catch (Exception exception)
        {
            warnings.Add($"cannot read {path}: {exception.Message}");
            return null;
        }
    }

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
