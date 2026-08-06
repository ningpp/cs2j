using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CSharpToJava.Core.TypeScript;

/// <summary>
/// 收集 ESM import 声明并计算相对路径（spec §3.3）。
/// 规则：
/// - 同包内类型 -> 相对路径（./Type.js），NodeNext 模块解析要求 .js 后缀
/// - 跨包引用 -> workspace 包名导入（@proj/core）
/// - 兼容库符号 -> @csharptotypescript/compat
/// - 按模块路径去重，命名导入合并
/// </summary>
public sealed class TsImportCollector
{
    private readonly string? _currentNamespace;
    private readonly Dictionary<string, SortedSet<string>> _namedImports = new();

    public TsImportCollector(string? currentNamespace = null)
    {
        _currentNamespace = currentNamespace;
    }

    /// <summary>
    /// 添加具名导入（如 CsDateTime from @csharptotypescript/compat）。
    /// </summary>
    public void AddNamedImport(string name, string modulePath)
    {
        if (!_namedImports.TryGetValue(modulePath, out var names))
        {
            names = new SortedSet<string>();
            _namedImports[modulePath] = names;
        }
        names.Add(name);
    }

    /// <summary>
    /// 添加同项目内类型的相对路径导入。
    /// namespace -> 目录路径（App.Models -> App/Models/），文件名 = 类型名 + .js。
    /// </summary>
    public void AddRelativeImport(string typeName, string targetNamespace)
    {
        var prefix = ComputeRelativePath(targetNamespace);   // 形如 "./"、"./Models/"、"../X/"
        AddNamedImport(typeName, $"{prefix}{typeName}.js");
    }

    /// <summary>
    /// 计算从当前命名空间到目标命名空间的相对模块路径。
    /// App + App.Models -> ./Models/Type.js
    /// App.Models + App.Models -> ./Type.js
    /// </summary>
    private string ComputeRelativePath(string targetNamespace)
    {
        var currentParts = _currentNamespace?.Split('.') ?? System.Array.Empty<string>();
        var targetParts = targetNamespace.Split('.');

        // 找公共前缀
        int common = 0;
        while (common < currentParts.Length && common < targetParts.Length
               && currentParts[common] == targetParts[common])
        {
            common++;
        }

        // 目标在当前命名空间内（公共前缀 == 当前命名空间长度）
        if (common == currentParts.Length)
        {
            var remaining = targetParts.Skip(common).ToArray();
            if (remaining.Length == 0)
            {
                // 同命名空间：./Type.js（调用方拼接类型名）
                return "./";
            }
            return "./" + string.Join("/", remaining) + "/";
        }

        // 跨命名空间回退（简化处理：用相对 ../）
        var up = currentParts.Length - common;
        var down = targetParts.Skip(common).ToArray();
        var sb = new StringBuilder();
        for (int i = 0; i < up; i++)
        {
            sb.Append("../");
        }
        if (down.Length > 0)
        {
            sb.Append(string.Join("/", down)).Append('/');
        }
        return sb.ToString();
    }

    public IReadOnlyList<TsImportEntry> GetImports()
    {
        return _namedImports
            .OrderBy(kv => kv.Key)
            .Select(kv => new TsImportEntry(kv.Key, kv.Value.ToList()))
            .ToList();
    }

    /// <summary>
    /// 发射 import 语句块（每行一条，以换行结尾）。
    /// </summary>
    public string EmitImports()
    {
        if (_namedImports.Count == 0) return "";

        var sb = new StringBuilder();
        foreach (var entry in GetImports())
        {
            var names = string.Join(", ", entry.NamedImports);
            sb.AppendLine($"import {{ {names} }} from \"{entry.ModulePath}\";");
        }
        return sb.ToString();
    }
}

public sealed record TsImportEntry(string ModulePath, IReadOnlyList<string> NamedImports);