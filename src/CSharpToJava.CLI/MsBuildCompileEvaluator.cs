using System.Diagnostics;
using System.Text.Json;

namespace CSharpToJava.CLI;

/// <summary>MSBuild 真实求值得到的编译集。</summary>
internal sealed class MsBuildCompileEvaluation
{
    public required IReadOnlyList<string> Files { get; init; }

    public string? TargetFramework { get; init; }

    public string? TargetFrameworks { get; init; }
}

/// <summary>
/// 语法求值欠定时升级到真正的 MSBuild 求值：
/// <c>dotnet msbuild &lt;proj&gt; -getProperty:TargetFramework … -getItem:Compile</c>，
/// 解析返回 JSON 的 <c>Properties</c>/<c>Items.Compile</c>。任何异常/超时/非 0
/// 退出都返回 null，绝不抛给调用方；结果按项目路径 + 文件写入时间缓存，避免
/// 多次 pass 重复调用。
/// </summary>
internal static class MsBuildCompileEvaluator
{
    private static readonly TimeSpan EvaluationTimeout = TimeSpan.FromSeconds(120);
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, MsBuildCompileEvaluation?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>测试注入缝；生产路径保持为 null。</summary>
    internal static Func<string, MsBuildCompileEvaluation?>? EvaluationOverride { get; set; }

    /// <summary>
    /// PackageReference 求值的独立测试注入缝（与 <see cref="EvaluationOverride"/>
    /// 的编译集求值互不复用）。
    /// </summary>
    internal static Func<string, IReadOnlyDictionary<string, string>?>? PackageReferenceEvaluationOverride { get; set; }

    private static readonly object PackageReferenceCacheGate = new();
    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>?> PackageReferenceCache =
        new(StringComparer.OrdinalIgnoreCase);

    internal static MsBuildCompileEvaluation? TryEvaluate(string projectPath)
    {
        if (EvaluationOverride != null)
        {
            return EvaluationOverride(projectPath);
        }

        string fullPath;
        long ticks;
        try
        {
            fullPath = Path.GetFullPath(projectPath);
            if (!File.Exists(fullPath))
            {
                return null;
            }

            ticks = File.GetLastWriteTimeUtc(fullPath).Ticks;
        }
        catch (Exception)
        {
            return null;
        }

        var cacheKey = fullPath + "|" + ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        lock (CacheGate)
        {
            if (Cache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        var evaluation = EvaluateCore(fullPath);

        lock (CacheGate)
        {
            Cache[cacheKey] = evaluation;
        }

        return evaluation;
    }

    private static MsBuildCompileEvaluation? EvaluateCore(string projectPath)
    {
        try
        {
            var projectDirectory = Path.GetDirectoryName(projectPath)!;
            var evaluation = RunAndParse(projectPath, projectDirectory, targetFramework: null);
            if (evaluation == null)
            {
                return null;
            }

            // 多目标项目在“外层构建”下 TargetFramework 为空，Compile 项是在
            // 未选定 TFM 的上下文里求值的（互斥条件组会两边都收/收错边）。
            // 此时必须用第一个非空 TFM 重跑一次，得到内层构建的真值。
            var needsTargetFrameworkRerun = evaluation.TargetFrameworks != null
                && (evaluation.Files.Count == 0 || string.IsNullOrEmpty(evaluation.TargetFramework));
            if (!needsTargetFrameworkRerun)
            {
                return evaluation;
            }

            var targetFramework = FirstTargetFramework(evaluation.TargetFrameworks);
            if (targetFramework == null)
            {
                return evaluation;
            }

            return RunAndParse(projectPath, projectDirectory, targetFramework) ?? evaluation;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? FirstTargetFramework(string? targetFrameworks)
    {
        if (string.IsNullOrWhiteSpace(targetFrameworks))
        {
            return null;
        }

        return targetFrameworks
            .Split(';', StringSplitOptions.TrimEntries)
            .FirstOrDefault(value => value.Length > 0);
    }

    private static MsBuildCompileEvaluation? RunAndParse(
        string projectPath,
        string projectDirectory,
        string? targetFramework)
    {
        var propertyArgument = targetFramework == null
            ? string.Empty
            : $"-p:TargetFramework=\"{targetFramework}\" ";
        var arguments =
            $"msbuild \"{projectPath}\" {propertyArgument}" +
            "-getProperty:TargetFramework -getProperty:TargetFrameworks -getItem:Compile --nologo";

        var startInfo = new ProcessStartInfo("dotnet", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            return null;
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)EvaluationTimeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // 超时后的清理尽力而为。
            }

            return null;
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        _ = stderrTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            return null;
        }

        return ParseEvaluationJson(stdout, projectDirectory);
    }

    /// <summary>
    /// 解析 <c>-getProperty/-getItem</c> 的 JSON 输出。Compile 的 Identity 以
    /// 项目目录为基准转绝对路径并去重（保序）。
    /// </summary>
    internal static MsBuildCompileEvaluation? ParseEvaluationJson(
        string json,
        string projectDirectory)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            string? targetFramework = null;
            string? targetFrameworks = null;
            if (root.TryGetProperty("Properties", out var properties)
                && properties.ValueKind == JsonValueKind.Object)
            {
                if (properties.TryGetProperty("TargetFramework", out var targetFrameworkElement)
                    && targetFrameworkElement.ValueKind == JsonValueKind.String)
                {
                    targetFramework = NullIfEmpty(targetFrameworkElement.GetString());
                }

                if (properties.TryGetProperty("TargetFrameworks", out var targetFrameworksElement)
                    && targetFrameworksElement.ValueKind == JsonValueKind.String)
                {
                    targetFrameworks = NullIfEmpty(targetFrameworksElement.GetString());
                }
            }

            var files = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("Items", out var items)
                && items.ValueKind == JsonValueKind.Object
                && items.TryGetProperty("Compile", out var compile)
                && compile.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in compile.EnumerateArray())
                {
                    if (!item.TryGetProperty("Identity", out var identityElement)
                        || identityElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var identity = identityElement.GetString();
                    if (string.IsNullOrWhiteSpace(identity))
                    {
                        continue;
                    }

                    string fullPath;
                    try
                    {
                        fullPath = Path.GetFullPath(Path.Combine(projectDirectory, identity));
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (seen.Add(fullPath))
                    {
                        files.Add(fullPath);
                    }
                }
            }

            return new MsBuildCompileEvaluation
            {
                Files = files,
                TargetFramework = targetFramework,
                TargetFrameworks = targetFrameworks,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 用 MSBuild 真实求值解析项目的 <c>PackageReference</c> 版本（Identity → Version），
    /// 供中间树在缺失 eng/*.props 时把 <c>Version="$(...)"</c> 钉成字面值。任何异常/
    /// 超时/非 0 退出都返回 null；结果按项目路径 + 文件写入时间缓存。
    /// </summary>
    internal static IReadOnlyDictionary<string, string>? TryEvaluatePackageReferences(string projectPath)
    {
        if (PackageReferenceEvaluationOverride != null)
        {
            return PackageReferenceEvaluationOverride(projectPath);
        }

        string fullPath;
        long ticks;
        try
        {
            fullPath = Path.GetFullPath(projectPath);
            if (!File.Exists(fullPath))
            {
                return null;
            }

            ticks = File.GetLastWriteTimeUtc(fullPath).Ticks;
        }
        catch (Exception)
        {
            return null;
        }

        var cacheKey = fullPath + "|" + ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        lock (PackageReferenceCacheGate)
        {
            if (PackageReferenceCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        var evaluation = EvaluatePackageReferencesCore(fullPath);

        lock (PackageReferenceCacheGate)
        {
            PackageReferenceCache[cacheKey] = evaluation;
        }

        return evaluation;
    }

    private static IReadOnlyDictionary<string, string>? EvaluatePackageReferencesCore(string projectPath)
    {
        try
        {
            var startInfo = new ProcessStartInfo(
                "dotnet",
                $"msbuild \"{projectPath}\" -getItem:PackageReference --nologo")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)EvaluationTimeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // 超时后的清理尽力而为。
                }

                return null;
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            _ = stderrTask.GetAwaiter().GetResult();

            if (process.ExitCode != 0)
            {
                return null;
            }

            return ParsePackageReferenceJson(stdout);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 解析 <c>-getItem:PackageReference</c> 的 JSON 输出。Identity 为包名，
    /// Version 取同层元数据；版本为空或仍是未求值的属性表达式时丢弃该项。
    /// </summary>
    internal static IReadOnlyDictionary<string, string>? ParsePackageReferenceJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Items", out var items)
                || items.ValueKind != JsonValueKind.Object
                || !items.TryGetProperty("PackageReference", out var packageReferences)
                || packageReferences.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in packageReferences.EnumerateArray())
            {
                if (!item.TryGetProperty("Identity", out var identityElement)
                    || identityElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var identity = identityElement.GetString();
                if (string.IsNullOrWhiteSpace(identity))
                {
                    continue;
                }

                if (!item.TryGetProperty("Version", out var versionElement)
                    || versionElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var version = versionElement.GetString();
                if (string.IsNullOrWhiteSpace(version) || version.Contains("$(", StringComparison.Ordinal))
                {
                    continue;
                }

                versions[identity.Trim()] = version.Trim();
            }

            return versions.Count == 0 ? null : versions;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
