using CSharpToJava.Core.Context;
using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.MemberDesugar;

/// <summary>后备字段确定性命名 + 跨规则预留表（设计决策 3）。
/// 基名复用 <see cref="JavaNaming"/> 的 camelCase + Java 关键字转义规则，
/// 保证「预处理前转换器隐含的 Java 字段名」==「去糖 C# 显式字段名」。
/// 在其之上再对齐 C# CS0102 全量成员面（方法/事件/嵌套类型等）与本族已生成名追加 Value 消歧。
/// 名字一律取声明标识符：显式接口实现的符号 Name 是 "IFoo.X" 形态，不是合法标识符。</summary>
internal static class BackingFieldNaming
{
    /// <summary>为待生成后备字段一次性算定名字：同类型按源序，首声明者保留裸名、后撞者加 Value。</summary>
    public static IReadOnlyDictionary<IPropertySymbol, string> Assign(
        IEnumerable<(IPropertySymbol Prop, string DeclName)> propertiesNeedingFields)
    {
        var result = new Dictionary<IPropertySymbol, string>(SymbolEqualityComparer.Default);
        foreach (var group in propertiesNeedingFields
                     .Where(p => p.Prop.ContainingType != null)
                     .GroupBy(p => p.Prop.ContainingType!.ToDisplayString())
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var reserved = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (prop, declName) in group
                         .OrderBy(p => p.Prop.Locations.FirstOrDefault(l => l.IsInSource)?.SourceSpan.Start ?? int.MaxValue)
                         .ThenBy(p => p.DeclName, StringComparer.Ordinal))
            {
                var camel = declName.Length > 0
                    ? char.ToLowerInvariant(declName[0]) + declName[1..]
                    : declName;
                var name = JavaNaming.EscapeJavaKeyword(camel);
                while (IsTaken(name, prop, prop.ContainingType!)
                       || reserved.Contains(name)
                       // set_X 的形参名固定为 value：带 setter 的后备字段不能与之重名，
                       // 否则合成体 value = value; 变成参数自赋值（字段永远不写入）。
                       || (name == "value" && prop.SetMethod != null))
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
