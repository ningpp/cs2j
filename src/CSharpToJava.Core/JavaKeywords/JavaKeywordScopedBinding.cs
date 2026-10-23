using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Core.JavaKeywords;

/// <summary>
/// 整树合并编译里跨项目同名类型（如 <c>System.AssertExtensions</c> 与
/// <c>System.Tests.AssertExtensions</c>，后者靠项目级 global using 进入作用域）
/// 会让调用点绑定完全失败（Symbol=null、无候选），声明侧改名因此残留调用点。
/// 宿主可实现本接口提供「按文件所属项目分区」的小作用域编译，在原始模型查不到
/// 改名时于其中重新绑定调用点。返回 null 表示该文件无作用域编译（保持现状）。
/// </summary>
internal interface IJavaKeywordScopedBindingFallback
{
    SemanticModel? GetScopedModel(SyntaxTree tree);
}

/// <summary>
/// 缺省作用域化回退：把绑定编译的语法树按 common-root 之后的顶层目录分区，
/// 每区用同一批元数据引用 + 编译选项重建小编译，并补默认 SDK 隐式 usings。
/// 宿主若掌握项目划分与各项目 global usings，应注入更精确的实现（CLI 适配器）。
/// </summary>
internal sealed class TopLevelPartitionScopedBindingFallback : IJavaKeywordScopedBindingFallback
{
    private readonly CSharpCompilation _compilation;
    private readonly IReadOnlyDictionary<string, List<SyntaxTree>> _partitions;
    private readonly Dictionary<string, SemanticModel?> _modelCache = new(StringComparer.Ordinal);

    private TopLevelPartitionScopedBindingFallback(
        CSharpCompilation compilation,
        IReadOnlyDictionary<string, List<SyntaxTree>> partitions)
    {
        _compilation = compilation;
        _partitions = partitions;
    }

    public static TopLevelPartitionScopedBindingFallback Build(CSharpCompilation compilation)
    {
        var partitions = new Dictionary<string, List<SyntaxTree>>(StringComparer.Ordinal);
        foreach (var tree in compilation.SyntaxTrees)
        {
            var key = PartitionKey(tree.FilePath);
            if (!partitions.TryGetValue(key, out var list))
                partitions[key] = list = new List<SyntaxTree>();
            list.Add(tree);
        }
        return new TopLevelPartitionScopedBindingFallback(compilation, partitions);
    }

    public SemanticModel? GetScopedModel(SyntaxTree tree)
    {
        var key = PartitionKey(tree.FilePath);
        if (!_partitions.TryGetValue(key, out var trees))
            return null;

        if (!_modelCache.TryGetValue(key, out var cached))
        {
            cached = BuildScopedCompilation(key, trees).GetSemanticModel(tree);
            _modelCache[key] = cached;
        }
        return cached;
    }

    /// <summary>
    /// 分区键：优先取「真实文件系统上最近的含 *.csproj 的祖先目录」（管线中路径为
    /// 真实中间树路径，各项目的 csproj 已就位）；找不到时退化为文件所在目录（自成
    /// 一支，作用域内查不到即保持现状）。合并整树按此分区后，跨项目同名类型不再
    /// 同场竞技，作用域内重绑定可得到与真实 msbuild 一致的唯一结果。
    /// </summary>
    internal static string PartitionKey(string path)
    {
        var nearestCsprojDir = FindNearestProjectDir(path);
        if (nearestCsprojDir != null) return nearestCsprojDir;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        return string.IsNullOrEmpty(dir) ? path : dir;
    }

    private static readonly Dictionary<string, string?> ProjectDirCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>从文件目录向上找最近一个包含 *.csproj 的目录；文件系统不可达时返回 null。</summary>
    private static string? FindNearestProjectDir(string path)
    {
        if (ProjectDirCache.TryGetValue(path, out var cached)) return cached;

        string? result = null;
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            while (!string.IsNullOrEmpty(dir))
            {
                if (Directory.EnumerateFiles(dir, "*.csproj").Any())
                {
                    result = dir;
                    break;
                }
                var parent = Path.GetDirectoryName(dir);
                if (parent == dir || parent == null) break;
                dir = parent;
            }
        }
        catch (Exception)
        {
            result = null;
        }
        ProjectDirCache[path] = result;
        return result;
    }

    private CSharpCompilation BuildScopedCompilation(string key, List<SyntaxTree> trees)
    {
        // 各分区补充默认 SDK 隐式 usings；宿主注入的实现可用各项目自己的
        // <Using Include=... /> 集合获得更精确的绑定。
        var globalUsings = string.Join(
            Environment.NewLine,
            new[]
            {
                "System",
                "System.Collections.Generic",
                "System.IO",
                "System.Linq",
                "System.Net.Http",
                "System.Threading",
                "System.Threading.Tasks",
            }.Select(ns => $"global using {ns};"));
        var synthetic = CSharpSyntaxTree.ParseText(
            globalUsings,
            new CSharpParseOptions(LanguageVersion.Latest),
            path: $"__cs2j_scoped_global_usings_{key.GetHashCode():X}.cs");

        return CSharpCompilation.Create(
            $"java-keywords-scoped-{key.GetHashCode():X}",
            trees.Append(synthetic),
            _compilation.ExternalReferences,
            _compilation.Options);
    }
}
