using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Pipeline;

/// <summary>
/// TS 目标跨文件/跨包导入发射 pass（Task 40，spec §3.3）：项目模式每类型一文件，
/// 跨文件类型引用必须显式 ESM 导入——同模块 → 相对路径导入（"./Type.js"，NodeNext
/// 解析要求 .js 后缀）；跨项目 → workspace 包名导入（模块包名由
/// <see cref="ConversionOptions.TsModuleByAssemblyName"/>（MSBuild workspace 元数据
/// 引用）与 <see cref="ConversionOptions.TsModuleBySourcePathPrefix"/>（项目图语义
/// 上下文源目录）接线）。语义模型解析引用，只导入本转换实际发射的类型（源文件 +
/// 类型名 → 发射结果索引）；自引用/同文件不导入；新导入插在既有 import 块之后、
/// 按模块路径分组去重。在 ProjectNpmDependencyPass 之前运行：workspace 包名导入
/// 会被收集为 npm 依赖（CLI 侧过滤为 workspace 依赖）。
/// </summary>
public sealed class ProjectTsImportEmitPass : ICs2jPass<ProjectPassState>
{
    private static readonly System.Text.RegularExpressions.Regex ImportLinePattern = new(
        @"^import\s+\{\s*([^}]*?)\s*\}\s+from\s+""([^""]+)""",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public string Name => nameof(ProjectTsImportEmitPass);
    public Cs2jPassStage Stage => Cs2jPassStage.Emit;

    public void Execute(ProjectPassState state)
    {
        var results = state.Results
            .Where(result => result.Success && !string.IsNullOrWhiteSpace(result.GeneratedCode))
            .ToList();
        if (results.Count == 0)
        {
            return;
        }

        // 发射索引：(源文件, 类型名) → 发射结果（多类型同文件 → 各类型独立结果文件）
        var typeIndex = new Dictionary<(string File, string TypeName), ConversionResult>();
        foreach (var result in results)
        {
            if (result.Compilation == null)
            {
                continue;
            }

            foreach (var type in result.Compilation.TypeDeclarations)
            {
                if (!string.IsNullOrEmpty(result.SourceFilePath))
                {
                    typeIndex[(Path.GetFullPath(result.SourceFilePath), type.Name)] = result;
                }

                if (!string.IsNullOrEmpty(result.FileName) && Path.IsPathFullyQualified(result.FileName))
                {
                    // 多文件类型组（partial 合并等）：FileName 为源文件路径（已改 .ts 扩展名）
                    typeIndex.TryAdd(
                        (Path.GetFullPath(Path.ChangeExtension(result.FileName, ".cs")), type.Name), result);
                }
            }
        }

        foreach (var result in results)
        {
            if (string.IsNullOrEmpty(result.SourceFilePath) || string.IsNullOrEmpty(result.FileName))
            {
                continue;
            }

            var tree = state.Compilation.SyntaxTrees
                .FirstOrDefault(tree => PathsEqual(tree.FilePath, result.SourceFilePath));
            if (tree == null)
            {
                continue;
            }

            var currentNamespace = result.Package ?? string.Empty;
            var semanticModel = state.Compilation.GetSemanticModel(tree);
            var existingImports = CollectExistingImports(result.GeneratedCode);
            var pendingImports = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                if (node is not IdentifierNameSyntax and not GenericNameSyntax)
                {
                    continue;
                }

                var typeSymbol = ResolveNamedTypeSymbol(semanticModel, node);
                if (typeSymbol == null)
                {
                    continue;
                }

                var sourceLocation = typeSymbol.Locations.FirstOrDefault(location => location.IsInSource);
                if (sourceLocation != null)
                {
                    var sourcePath = sourceLocation.SourceTree.FilePath;
                    var key = (Path.GetFullPath(sourcePath), typeSymbol.Name);
                    if (typeIndex.TryGetValue(key, out var target) && !ReferenceEquals(target, result))
                    {
                        // 同模块：相对路径导入（目标发射文件所在命名空间目录）。
                        // 注意源文件与发射文件并非一一对应：一个 .cs 可含多个类型
                        // （如 MyException + ExceptionFlow 同文件），各类型独立发射，
                        // 因此“同源文件”不代表“同发射文件”——仅当目标结果就是
                        // 当前结果（自引用）时才跳过。
                        var modulePath = ComputeRelativeImportPath(
                            currentNamespace, target.Package ?? string.Empty, target.FileName!);
                        AddPendingImport(pendingImports, existingImports, modulePath, typeSymbol.Name);
                    }
                    else if (!PathsEqual(sourcePath, tree.FilePath))
                    {
                        // 跨项目：源目录前缀映射（项目图/语义上下文目录）。
                        // 同源文件且未命中发射索引（blocked/自引用）跳过。
                        var moduleName = ResolveModuleBySourcePath(state, sourcePath);
                        if (moduleName != null)
                        {
                            AddPendingImport(pendingImports, existingImports, moduleName, typeSymbol.Name);
                        }
                    }
                }
                else
                {
                    // 元数据符号：程序集名 → workspace 模块包名（MSBuild workspace 模式）
                    var assemblyName = typeSymbol.ContainingAssembly?.Name;
                    var byAssembly = state.Context.Options.TsModuleByAssemblyName;
                    if (!string.IsNullOrEmpty(assemblyName)
                        && byAssembly != null
                        && byAssembly.TryGetValue(assemblyName, out var moduleName))
                    {
                        AddPendingImport(pendingImports, existingImports, moduleName, typeSymbol.Name);
                    }
                }
            }

            if (pendingImports.Count > 0)
            {
                result.GeneratedCode = InsertImports(result.GeneratedCode, pendingImports);
            }
        }
    }

    /// <summary>解析命名类型符号（含 using 别名与候选符号）。</summary>
    private static INamedTypeSymbol? ResolveNamedTypeSymbol(SemanticModel semanticModel, SyntaxNode node)
    {
        var symbolInfo = semanticModel.GetSymbolInfo(node);
        if (TryNamedType(symbolInfo.Symbol, out var type))
        {
            return type;
        }

        foreach (var candidate in symbolInfo.CandidateSymbols)
        {
            if (TryNamedType(candidate, out type))
            {
                return type;
            }
        }

        return null;
    }

    private static bool TryNamedType(ISymbol? symbol, out INamedTypeSymbol? type)
    {
        if (symbol is IAliasSymbol alias)
        {
            symbol = alias.Target;
        }
        type = symbol as INamedTypeSymbol;
        return type != null;
    }

    /// <summary>
    /// 同模块相对导入路径：当前/目标命名空间目录差（App + App.Models → ./Models/Type.js；
    /// App.Models + App → ../Type.js），文件名为目标发射文件名（.test.ts 保留）。
    /// </summary>
    internal static string ComputeRelativeImportPath(
        string currentNamespace, string targetNamespace, string targetFileName)
    {
        var currentParts = SplitNamespace(currentNamespace);
        var targetParts = SplitNamespace(targetNamespace);
        var common = 0;
        while (common < currentParts.Length
            && common < targetParts.Length
            && string.Equals(currentParts[common], targetParts[common], StringComparison.Ordinal))
        {
            common++;
        }

        var segments = new List<string>();
        for (var i = common; i < currentParts.Length; i++)
        {
            segments.Add("..");
        }
        for (var i = common; i < targetParts.Length; i++)
        {
            segments.Add(targetParts[i]);
        }

        var directory = segments.Count == 0
            ? "./"
            : (segments[0] == ".." ? string.Join("/", segments) : "./" + string.Join("/", segments)) + "/";
        return directory + Path.ChangeExtension(targetFileName, ".js");
    }

    private static string[] SplitNamespace(string? ns) =>
        string.IsNullOrEmpty(ns) ? [] : ns.Split('.', StringSplitOptions.RemoveEmptyEntries);

    private static string? ResolveModuleBySourcePath(ProjectPassState state, string sourcePath)
    {
        var byPath = state.Context.Options.TsModuleBySourcePathPrefix;
        if (byPath == null || byPath.Count == 0)
        {
            return null;
        }

        var fullPath = Path.GetFullPath(sourcePath);
        foreach (var pair in byPath.OrderByDescending(pair => pair.Key.Length))
        {
            if (fullPath.StartsWith(Path.GetFullPath(pair.Key), StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    /// <summary>解析既有 import 行（发射器 compat/vitest 导入），新导入与之去重。</summary>
    private static Dictionary<string, SortedSet<string>> CollectExistingImports(string code)
    {
        var existing = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var line in code.Split('\n'))
        {
            var match = ImportLinePattern.Match(line.Trim());
            if (!match.Success)
            {
                continue;
            }

            var modulePath = match.Groups[2].Value;
            if (!existing.TryGetValue(modulePath, out var names))
            {
                existing[modulePath] = names = new SortedSet<string>(StringComparer.Ordinal);
            }
            foreach (var name in match.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                names.Add(name);
            }
        }
        return existing;
    }

    private static void AddPendingImport(
        Dictionary<string, SortedSet<string>> pending,
        Dictionary<string, SortedSet<string>> existing,
        string modulePath,
        string typeName)
    {
        if (existing.TryGetValue(modulePath, out var existingNames) && existingNames.Contains(typeName))
        {
            return;
        }
        if (existing.Values.Any(names => names.Contains(typeName)))
        {
            return; // 同名已从其他模块导入：避免标识符冲突
        }
        if (!pending.TryGetValue(modulePath, out var pendingNames))
        {
            pending[modulePath] = pendingNames = new SortedSet<string>(StringComparer.Ordinal);
        }
        pendingNames.Add(typeName);
    }

    /// <summary>
    /// 在既有 import 块之后插入新导入：保留原文本（含行尾风格），把新导入行
    /// 插在首个非 import/非空行之前；无既有导入时插在文件顶部。
    /// </summary>
    private static string InsertImports(string code, Dictionary<string, SortedSet<string>> imports)
    {
        var eol = code.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = code.Split('\n');
        var firstCodeLine = 0;
        while (firstCodeLine < lines.Length)
        {
            var line = lines[firstCodeLine].TrimEnd('\r', ' ', '\t');
            if (line.Length == 0 || line.StartsWith("import ", StringComparison.Ordinal))
            {
                firstCodeLine++;
                continue;
            }
            break;
        }

        var builder = new StringBuilder();
        if (firstCodeLine > 0)
        {
            // 保留既有行（含 \r 行尾），补回被 Split 吃掉的换行
            builder.Append(string.Join('\n', lines[..firstCodeLine]));
            builder.Append('\n');
        }
        foreach (var (modulePath, names) in imports.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            builder.Append("import { ").Append(string.Join(", ", names))
                .Append(" } from \"").Append(modulePath).Append("\";").Append(eol);
        }
        builder.Append(eol);
        builder.Append(string.Join('\n', lines[firstCodeLine..]));
        return builder.ToString();
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return false;
        }
        return string.Equals(
            Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }
}