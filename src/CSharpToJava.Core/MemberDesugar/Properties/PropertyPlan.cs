using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.MemberDesugar.Properties;

internal sealed class PropertyPlan : MemberDesugarPlan
{
    public required IPropertySymbol Property { get; init; }
    /// <summary>声明标识符文本（显式接口实现的 <see cref="IPropertySymbol.Name"/> 是
    /// "IFoo.X" 形态，方法名必须用声明标识符）。</summary>
    public required string DeclName { get; init; }
    /// <summary>后备字段名；计算属性为 null。</summary>
    public string? BackingFieldName { get; init; }
    public required string GetterMethodName { get; init; }
    public string? SetterMethodName { get; init; }
    /// <summary>set/init 访问器是否存在（init 亦算，放宽为 set）。</summary>
    public required bool HasSetter { get; init; }
    public required bool HasInitAccessor { get; init; }
    public required bool IsRequired { get; init; }

    public override bool OwnsTouch(string filePath, string? typeName, string? memberName)
    {
        if (!TouchedFiles.Contains(filePath)) return false;
        if (!string.Equals(typeName, Property.ContainingType?.Name, StringComparison.Ordinal)) return false;
        return string.Equals(memberName, DeclName, StringComparison.Ordinal)
            || (memberName != null && BackingFieldName != null
                && string.Equals(memberName, BackingFieldName, StringComparison.Ordinal))
            || string.Equals(memberName, GetterMethodName, StringComparison.Ordinal)
            || (SetterMethodName != null
                && string.Equals(memberName, SetterMethodName, StringComparison.Ordinal));
    }

    public static string BuildId(IPropertySymbol prop)
    {
        var span = prop.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? 0;
        return $"MD:{prop.ContainingType?.ToDisplayString()}::{prop.Name}@{span}";
    }

    /// <summary>跨编译稳定键（与 IndexerPlan.SiteKey 同因）：finalizer/event/operator
    /// 阶段重解析后，符号来自新的 CSharpCompilation，SymbolEqualityComparer 判不相等，
    /// 必须用「类型全名::元数据名」字符串键（普通属性无参数；类型内不可同名重载）。</summary>
    public static string SiteKey(IPropertySymbol prop)
        => $"{prop.ContainingType?.ToDisplayString()}::{prop.MetadataName}";
}
