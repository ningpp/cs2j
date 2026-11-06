using CSharpToJava.Core.ConditionalDirectiveStrip;

namespace CSharpToJava.CLI;

/// <summary>
/// 解析项目真正生效的单一 TFM：先做语法读取；TFM 列表为空或原始值含未展开
/// 宏（运行时仓库的 <c>$(NetCoreAppCurrent)</c> 这类）时，升级到
/// <see cref="MsBuildCompileEvaluator"/> 的真实求值。结果按项目路径 + 配置缓存，
/// 失败返回 null（调用方保持原有语法回退行为）。
/// </summary>
internal static class ResolvedProjectTfm
{
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);

    internal static string? Resolve(string projectPath, string configuration = "Release")
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(projectPath);
        }
        catch (Exception)
        {
            return null;
        }

        if (!File.Exists(fullPath))
        {
            return null;
        }

        var cacheKey = fullPath + "|" + configuration;
        lock (CacheGate)
        {
            if (Cache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        var resolved = ResolveCore(fullPath, configuration);

        lock (CacheGate)
        {
            Cache[cacheKey] = resolved;
        }

        return resolved;
    }

    /// <summary>测试隔离用：清空按项目路径缓存的解析结果。</summary>
    internal static void ClearCache()
    {
        lock (CacheGate)
        {
            Cache.Clear();
        }
    }

    private static string? ResolveCore(string projectPath, string configuration)
    {
        var declared = ProjectSymbolResolver.ReadTfms(
            projectPath, configuration, warnings: null, out var unresolved);
        if (!unresolved && declared.Count > 0)
        {
            return declared[0];
        }

        var evaluation = MsBuildCompileEvaluator.TryEvaluate(projectPath);
        if (evaluation == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(evaluation.TargetFramework))
        {
            return evaluation.TargetFramework.Trim();
        }

        return FirstTargetFramework(evaluation.TargetFrameworks);
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
}
