using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.MemberDesugar.Properties;

internal sealed class PropertyPlan : MemberDesugarPlan
{
    public required IPropertySymbol Property { get; init; }
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
        return string.Equals(memberName, Property.Name, StringComparison.Ordinal)
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
}
