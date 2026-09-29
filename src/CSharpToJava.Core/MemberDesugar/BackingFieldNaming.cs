using CSharpToJava.Core.Context;
using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.MemberDesugar;

/// <summary>后备字段确定性命名 + 跨规则预留表（设计决策 3）。
/// 基名直接复用 <see cref="JavaNaming.GetAutoPropertyBackingFieldName"/>，
/// 保证「预处理前转换器隐含的 Java 字段名」==「去糖 C# 显式字段名」。
/// 在其之上再对齐 C# CS0102 全量成员面（方法/事件/嵌套类型等）与本族已生成名追加 Value 消歧。</summary>
internal static class BackingFieldNaming
{
    /// <summary>为待生成后备字段一次性算定名字：同类型按源序，首声明者保留裸名、后撞者加 Value。</summary>
    public static IReadOnlyDictionary<IPropertySymbol, string> Assign(
        IEnumerable<IPropertySymbol> propertiesNeedingFields)
    {
        var result = new Dictionary<IPropertySymbol, string>(SymbolEqualityComparer.Default);
        foreach (var group in propertiesNeedingFields
                     .Where(p => p.ContainingType != null)
                     .GroupBy(p => p.ContainingType!.ToDisplayString())
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var reserved = new HashSet<string>(StringComparer.Ordinal);
            foreach (var prop in group
                         .OrderBy(p => p.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? int.MaxValue)
                         .ThenBy(p => p.Name, StringComparer.Ordinal))
            {
                var name = JavaNaming.GetAutoPropertyBackingFieldName(prop);
                while (IsTaken(name, prop, prop.ContainingType!) || reserved.Contains(name))
                    name += "Value";
                reserved.Add(name);
                result[prop] = name;
            }
        }
        return result;
    }

    private static bool IsTaken(string name, IPropertySymbol prop, INamedTypeSymbol type)
        => type.GetMembers(name).Any(m =>
            !SymbolEqualityComparer.Default.Equals(m, prop) && !m.IsImplicitlyDeclared);
}
