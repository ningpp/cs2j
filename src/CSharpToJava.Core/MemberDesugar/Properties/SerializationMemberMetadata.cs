using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Properties;

/// <summary>
/// 序列化成员面保真（C11 决策 14 / 设计开放问题 1 的「可选标记」落地）：自动/全属性
/// 降形为 public 后备字段后，System.Text.Json 的反射契约随之改变——默认
/// IncludeFields=false 不枚举 public 字段，载荷里按原属性名出现的成员无法绑定
/// （真实 SDK：json 测试工程 ContinuationTests 1122 例、构造器参数绑定 86 例，均因
/// 「S」等原成员名消失而失败）。
///
/// 修复分两半：
/// 1. 后备字段名保留声明原名（<see cref="BackingFieldNaming.Assign"/> 的 preserve 模式），
///    使 STJ 默认成员名/PropertyNamingPolicy 与原属性逐字一致（也顺手对齐
///    XmlSerializer/Newtonsoft 的字段名）。
/// 2. 对 public 属性的后备字段补发 [JsonInclude]，让默认 IncludeFields=false 的 STJ
///    仍把它当作可序列化成员。字段名已是原名，故不再补 [JsonPropertyName]——属性名
///    特性会压过运行时 PropertyNamingPolicy，反而破坏策略化命名的既有语义。
///
/// 特性类型按「该文件所属工程可解析到的命名空间」选取：合并整树编译里
/// System.Text.Json.Serialization（BCL）与 SDK 移植版 dotnet.text.json.Serialization
/// 可能同时可见，而各个子工程只引用其中之一，直接按 compilation 全量解析会发射目标
/// 工程看不见的类型（CS0246）。因此只在文件自身的 using、所在命名空间链、以及同工程
/// 全局 using 中命中 JsonIncludeAttribute 时才发射；命中不了就静默不发射（保持现状，
/// 绝不引入新依赖）。
/// </summary>
internal static class SerializationMemberMetadata
{
    private const string AttributeShortName = "JsonInclude";
    private const string AttributeMetadataName = ".JsonIncludeAttribute";

    private static readonly ConditionalWeakTable<Compilation, ScopeCache> Caches = new();

    private sealed class ScopeCache
    {
        public readonly ConcurrentDictionary<string, string?> ProjectRootByDirectory =
            new(StringComparer.OrdinalIgnoreCase);
        public readonly ConcurrentDictionary<string, IReadOnlyList<string>> GlobalUsingsByRoot =
            new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 生成承载 [JsonInclude] 的特性列表；不满足条件时返回 null（调用方原样发射字段）。
    /// <paramref name="emittedFieldAttributes"/> 是已路由到该字段的特性（PD-10）——
    /// 同一字段重复的 [JsonInclude] 会触发 CS0579。
    /// </summary>
    public static AttributeListSyntax? BuildJsonInclude(
        SemanticModel model,
        PropertyDeclarationSyntax bindingDecl,
        IEnumerable<AttributeListSyntax> emittedFieldAttributes)
    {
        var prop = model.GetDeclaredSymbol(bindingDecl);
        // STJ 只枚举「至少一个 public 访问器」的成员；public 属性恰好覆盖该集合，
        // 其余可访问性（含显式接口实现的 private）原属性本就不参与序列化。
        // static 成员 STJ 一律跳过，原属性亦然，补标记反而改变成员面。
        if (prop is not { DeclaredAccessibility: Accessibility.Public, IsStatic: false }
            || prop.IsIndexer)
        {
            return null;
        }
        if (emittedFieldAttributes.Any(list =>
                list.Attributes.Any(attribute => IsAttribute(attribute, AttributeShortName))))
            return null;

        var attributeType = ResolveAttributeType(model, bindingDecl);
        if (attributeType == null) return null;

        return SyntaxFactory.AttributeList(
            SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Attribute(
                    SyntaxFactory.ParseName("global::" + attributeType.ToDisplayString()),
                    SyntaxFactory.AttributeArgumentList())));
    }

    private static bool IsAttribute(AttributeSyntax attribute, string shortName)
    {
        var name = attribute.Name switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
            _ => attribute.Name.ToString(),
        };
        return name == shortName
               || name == shortName + "Attribute";
    }

    private static ITypeSymbol? ResolveAttributeType(SemanticModel model, SyntaxNode anchor)
    {
        var compilation = model.Compilation;
        foreach (var ns in NamespacesInScope(model, anchor))
        {
            if (ns.Length == 0) continue;
            var candidate = compilation.GetTypeByMetadataName(ns + AttributeMetadataName);
            if (candidate is { TypeKind: TypeKind.Class, DeclaredAccessibility: Accessibility.Public })
                return candidate;
        }
        return null;
    }

    /// <summary>文件可见命名空间：文件自身 using（含 global）、外层命名空间链、同工程全局 using。</summary>
    private static IEnumerable<string> NamespacesInScope(SemanticModel model, SyntaxNode anchor)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (anchor.SyntaxTree.GetRoot() is CompilationUnitSyntax unit)
        {
            foreach (var directive in unit.Usings)
            {
                if (directive.StaticKeyword.IsKind(SyntaxKind.StaticKeyword)) continue;
                if (directive.Name is { } name
                    && seen.Add(CleanNamespace(name.ToString())))
                {
                    yield return CleanNamespace(name.ToString());
                }
            }
        }

        foreach (var declaration in anchor.Ancestors().OfType<BaseNamespaceDeclarationSyntax>())
        {
            if (declaration.Name is { } name
                && seen.Add(CleanNamespace(name.ToString())))
            {
                yield return CleanNamespace(name.ToString());
            }
        }

        foreach (var ns in ProjectGlobalUsings(model.Compilation, anchor.SyntaxTree.FilePath))
        {
            if (seen.Add(ns)) yield return ns;
        }
    }

    private static string CleanNamespace(string name)
    {
        var text = name.Trim();
        return text.StartsWith("global::", StringComparison.Ordinal)
            ? text["global::".Length..]
            : text;
    }

    private static IReadOnlyList<string> ProjectGlobalUsings(
        Compilation compilation, string? filePath)
    {
        var root = ProjectRoot(compilation, filePath);
        if (root == null) return Array.Empty<string>();
        return Caches.GetValue(compilation, static _ => new ScopeCache())
            .GlobalUsingsByRoot.GetOrAdd(root, r => CollectGlobalUsings(compilation, r));
    }

    private static IReadOnlyList<string> CollectGlobalUsings(
        Compilation compilation, string root)
    {
        var namespaces = new List<string>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (!IsUnder(tree.FilePath, root)) continue;
            if (!tree.TryGetRoot(out var node) || node is not CompilationUnitSyntax unit) continue;
            foreach (var directive in unit.Usings)
            {
                if (!directive.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)) continue;
                if (directive.StaticKeyword.IsKind(SyntaxKind.StaticKeyword)) continue;
                if (directive.Name is { } name)
                    namespaces.Add(CleanNamespace(name.ToString()));
            }
        }
        return namespaces.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>最近的含 csproj 目录（即文件所属工程根），结果按目录缓存。</summary>
    private static string? ProjectRoot(Compilation compilation, string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return null;
        var cache = Caches.GetValue(compilation, static _ => new ScopeCache());
        string directory;
        try
        {
            directory = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? string.Empty;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        if (directory.Length == 0) return null;
        return cache.ProjectRootByDirectory.GetOrAdd(directory, FindProjectRoot);
    }

    private static string? FindProjectRoot(string start)
    {
        var current = start;
        while (current.Length > 0)
        {
            try
            {
                if (Directory.Exists(current)
                    && Directory.EnumerateFiles(current, "*.csproj").Any())
                {
                    return current;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            var parent = Path.GetDirectoryName(current);
            if (parent == null || parent == current) return null;
            current = parent;
        }
        return null;
    }

    private static bool IsUnder(string? path, string root)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && full.Length > root.Length
            && (full[root.Length] == Path.DirectorySeparatorChar
                || full[root.Length] == Path.AltDirectorySeparatorChar))
        {
            return true;
        }
        return false;
    }
}
