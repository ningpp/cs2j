using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.MemberDesugar.Indexers;

internal sealed class IndexerPlan : MemberDesugarPlan
{
    public required IPropertySymbol Indexer { get; init; }
    /// <summary>索引器元数据名（"Item" 或 [IndexerName] 自定义名；显式接口实现的
    /// Name 是 "IFoo.Item" 形态，方法名必须用 MetadataName，D3）。</summary>
    public required string MetadataName { get; init; }
    public required string GetterMethodName { get; init; }
    public required string SetterMethodName { get; init; }
    /// <summary>set/init 访问器是否存在（init 亦算，放宽为 set，D8）。</summary>
    public required bool HasSetter { get; init; }
    public required bool HasInitAccessor { get; init; }

    public bool ReturnsByRef => Indexer.ReturnsByRef || Indexer.ReturnsByRefReadonly;
    public bool IsExplicitImplementation => Indexer.ExplicitInterfaceImplementations.Length > 0;

    public override bool OwnsTouch(string filePath, string? typeName, string? memberName)
    {
        if (!TouchedFiles.Contains(filePath)) return false;
        if (!string.Equals(typeName, Indexer.ContainingType?.Name, StringComparison.Ordinal)) return false;
        return memberName != null
               && (string.Equals(memberName, GetterMethodName, StringComparison.Ordinal)
                   || string.Equals(memberName, SetterMethodName, StringComparison.Ordinal)
                   || string.Equals(memberName, MetadataName, StringComparison.Ordinal));
    }

    public static string BuildId(IPropertySymbol prop)
    {
        var span = prop.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? 0;
        return $"MD:{prop.ContainingType?.ToDisplayString()}::{prop.MetadataName}@{span}";
    }

    /// <summary>跨 Compilation 稳定键（属性 pass 改写后重解析树上的符号与原树符号
    /// 不相等，站点查找必须按值匹配；重载按参数类型区分）。</summary>
    public static string SiteKey(IPropertySymbol prop)
        => $"{prop.ContainingType?.ToDisplayString()}::{prop.MetadataName}" +
           $"({string.Join(",", prop.Parameters.Select(x => x.Type.ToDisplayString()))})";
}
