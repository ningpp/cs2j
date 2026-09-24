using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CSharpToJava.Core.ConditionalDirectiveStrip;

public sealed record ProjectSymbols(IReadOnlyList<string> Symbols, IReadOnlyList<string> Warnings);

/// <summary>
/// 轻量符号解析（设计 §5；不做完整 MSBuild 属性求值）：
/// 最近祖先 .csproj + 沿途 Directory.Build.props（父→子）依次应用 <DefineConstants>——
/// 含 $(DefineConstants) 的值为追加语义，裸值为替换语义（MSBuild 一致）；
/// 仅求值 $(Configuration) 的 ==/!= 条件子句，其余条件保守视为包含；
/// 由 TargetFramework(s) 合成 SDK 注入的 TFM 符号（含 _OR_GREATER 家族）；
/// 无法解析的宏跳过并产生 Warning（--define 兜底）。
/// </summary>
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
        if (project != null)
        {
            foreach (var props in GetAncestorDirectoryBuildProps(project).Reverse())
            {
                ApplyDefineConstants(props, configuration, symbols, warnings);
            }

            ApplyDefineConstants(project, configuration, symbols, warnings);

            var tfms = ReadTfms(project, warnings);
            var chosen = PickTfm(tfms, tfm);
            if (chosen == null)
            {
                if (tfms.Count > 0)
                {
                    warnings.Add($"no TFM selected from [{string.Join(';', tfms)}]");
                }
            }
            else if (!TrySynthesizeTfmSymbols(chosen, symbols))
            {
                warnings.Add($"unrecognized TFM '{chosen}'");
            }
        }

        if (extraDefines != null)
        {
            symbols.AddRange(extraDefines.Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        return new ProjectSymbols(Dedup(symbols), warnings);
    }

    internal static string? FindNearestProject(string? startDirectory)
    {
        var dir = startDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var project = Directory.EnumerateFiles(dir, "*.csproj").FirstOrDefault();
            if (project != null)
            {
                return project;
            }

            var parent = Path.GetDirectoryName(dir);
            if (parent == null || string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            dir = parent;
        }

        return null;
    }

    private static IEnumerable<string> GetAncestorDirectoryBuildProps(string projectPath)
    {
        // 自项目目录向上收集，返回时 Reverse() 得到父→子（MSBuild 导入顺序）
        var dir = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        while (!string.IsNullOrEmpty(dir))
        {
            var props = Path.Combine(dir, "Directory.Build.props");
            if (File.Exists(props))
            {
                yield return props;
            }

            var parent = Path.GetDirectoryName(dir);
            if (parent == null || string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase))
            {
                yield break;
            }

            dir = parent;
        }
    }

    private static void ApplyDefineConstants(
        string file, string configuration, List<string> symbols, List<string> warnings)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(file);
        }
        catch (Exception ex)
        {
            warnings.Add($"cannot read {file}: {ex.Message}");
            return;
        }

        if (document.Root == null)
        {
            return;
        }

        foreach (var group in document.Root.Elements("PropertyGroup"))
        {
            if (!EvaluateCondition(group.Attribute("Condition")?.Value, configuration))
            {
                continue;
            }

            foreach (var element in group.Elements("DefineConstants"))
            {
                if (!EvaluateCondition(element.Attribute("Condition")?.Value, configuration))
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

                    if (part.Contains("$("))
                    {
                        warnings.Add($"skipped unresolvable macro '{part}' in {file}");
                        continue;
                    }

                    parts.Add(part);
                }

                if (appends)
                {
                    symbols.AddRange(parts);
                }
                else
                {
                    symbols.Clear();
                    symbols.AddRange(parts);
                }
            }
        }
    }

    /// <summary>
    /// 仅求值 $(Configuration) 的 ==/!= 子句（MSBuild 字符串比较不区分大小写）；
    /// And 优先于 Or；不含 $(Configuration) 的子句保守视为真。
    /// </summary>
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
                    continue; // 未知子句 → 真（保守包含）
                }

                var equal = string.Equals(configuration, match.Groups[2].Value, StringComparison.OrdinalIgnoreCase);
                var clauseValue = match.Groups[1].Value == "==" ? equal : !equal;
                if (!clauseValue)
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

    private static List<string> ReadTfms(string projectPath, List<string> warnings)
    {
        var result = new List<string>();
        XDocument document;
        try
        {
            document = XDocument.Load(projectPath);
        }
        catch (Exception ex)
        {
            warnings.Add($"cannot read {projectPath}: {ex.Message}");
            return result;
        }

        if (document.Root == null)
        {
            return result;
        }

        foreach (var group in document.Root.Elements("PropertyGroup"))
        {
            if (!EvaluateCondition(group.Attribute("Condition")?.Value, "Release"))
            {
                continue;
            }

            foreach (var element in group.Elements())
            {
                if (!EvaluateCondition(element.Attribute("Condition")?.Value, "Release"))
                {
                    continue;
                }

                var value = element.Value.Trim();
                if (value.Length == 0 || value.Contains("$("))
                {
                    if (value.Contains("$(") && (element.Name.LocalName is "TargetFramework" or "TargetFrameworks"))
                    {
                        var macro = value.Trim('$', '(', ')', ' ').Trim();
                        warnings.Add($"skipped unresolvable TFM macro '{macro}' in {projectPath}");
                    }

                    continue;
                }

                if (element.Name.LocalName.Equals("TargetFrameworks", StringComparison.OrdinalIgnoreCase))
                {
                    result.Clear();
                    result.AddRange(value.Split(';', StringSplitOptions.RemoveEmptyEntries)
                        .Select(t => t.Trim())
                        .Where(t => t.Length > 0));
                }
                else if (element.Name.LocalName.Equals("TargetFramework", StringComparison.OrdinalIgnoreCase))
                {
                    result.Clear();
                    result.Add(value);
                }
            }
        }

        return result;
    }

    internal static string? PickTfm(List<string> tfms, string? requested)
    {
        if (requested != null)
        {
            var match = tfms.FirstOrDefault(t => string.Equals(t, requested, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                return match;
            }
        }

        return tfms.FirstOrDefault();
    }

    /// <summary>按 SDK 实际行为合成 TFM 符号；无法识别返回 false。</summary>
    internal static bool TrySynthesizeTfmSymbols(string tfm, List<string> symbols)
    {
        var value = tfm.Trim();

        var netStandard = Regex.Match(value, @"^netstandard(\d+)\.(\d+)$", RegexOptions.IgnoreCase);
        if (netStandard.Success)
        {
            var version = $"{netStandard.Groups[1].Value}_{netStandard.Groups[2].Value}";
            symbols.Add("NETSTANDARD");
            symbols.Add($"NETSTANDARD{version}");
            foreach (var lower in NetStandardVersions)
            {
                if (CompareVersions(lower, $"{netStandard.Groups[1].Value}.{netStandard.Groups[2].Value}") <= 0)
                {
                    symbols.Add($"NETSTANDARD{VersionToSymbol(lower)}_OR_GREATER");
                }
            }

            return true;
        }

        var netCoreApp = Regex.Match(value, @"^netcoreapp(\d+)\.(\d+)$", RegexOptions.IgnoreCase);
        if (netCoreApp.Success)
        {
            var version = $"{netCoreApp.Groups[1].Value}_{netCoreApp.Groups[2].Value}";
            symbols.Add("NETCOREAPP");
            symbols.Add($"NETCOREAPP{version}");
            foreach (var lower in NetCoreAppVersions)
            {
                symbols.Add($"NETCOREAPP{VersionToSymbol(lower)}_OR_GREATER");
            }

            return true;
        }

        var netModern = Regex.Match(value, @"^net(\d+)\.(\d+)$", RegexOptions.IgnoreCase);
        if (netModern.Success && int.Parse(netModern.Groups[1].Value) >= 5)
        {
            var major = int.Parse(netModern.Groups[1].Value);
            symbols.Add("NET");
            symbols.Add($"NET{major}_{netModern.Groups[2].Value}");
            symbols.Add($"NET{major}_{netModern.Groups[2].Value}_OR_GREATER");
            for (var n = 5; n < major; n++)
            {
                symbols.Add($"NET{n}_0_OR_GREATER");
            }

            foreach (var lower in NetCoreAppVersions)
            {
                symbols.Add($"NETCOREAPP{VersionToSymbol(lower)}_OR_GREATER");
            }

            return true;
        }

        var netFramework = Regex.Match(value, @"^net(\d{2,3})$", RegexOptions.IgnoreCase);
        if (netFramework.Success)
        {
            var code = netFramework.Groups[1].Value;
            symbols.Add("NETFRAMEWORK");
            symbols.Add($"NET{code}");
            foreach (var lower in NetFxVersions)
            {
                if (CompareNetFxVersions(lower, code) <= 0)
                {
                    symbols.Add($"NET{lower}_OR_GREATER");
                }
            }

            return true;
        }

        return false;
    }

    private static string VersionToSymbol(string version) => version.Replace(".", "_");

    private static int CompareVersions(string left, string right)
    {
        var leftParts = left.Split('.').Select(int.Parse).ToArray();
        var rightParts = right.Split('.').Select(int.Parse).ToArray();
        for (var i = 0; i < Math.Max(leftParts.Length, rightParts.Length); i++)
        {
            var comparison = (i < leftParts.Length ? leftParts[i] : 0).CompareTo(i < rightParts.Length ? rightParts[i] : 0);
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
            var (l, r) when l.major != r.major => l.major.CompareTo(r.major),
            var (l, r) when l.minor != r.minor => l.minor.CompareTo(r.minor),
            var (l, r) => l.patch.CompareTo(r.patch),
        };

    private static (int major, int minor, int patch) ToNetFxTuple(string code) => code.Length switch
    {
        2 => (code[0] - '0', code[1] - '0', 0),
        3 => (code[0] - '0', code[1] - '0', code[2] - '0'),
        _ => (0, 0, 0),
    };

    private static List<string> Dedup(List<string> symbols)
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
