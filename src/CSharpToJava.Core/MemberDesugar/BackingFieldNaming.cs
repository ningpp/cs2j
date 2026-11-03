using CSharpToJava.Core.Context;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Core.MemberDesugar;

/// <summary>后备字段确定性命名 + 跨规则预留表（设计决策 3）。
/// 基名复用 <see cref="JavaNaming"/> 的 camelCase + Java 关键字转义规则，
/// 保证「预处理前转换器隐含的 Java 字段名」==「去糖 C# 显式字段名」。
/// 在其之上再对齐 C# CS0102 全量成员面（方法/事件/嵌套类型等）与本族已生成名追加 Value 消歧。
/// 名字一律取声明标识符：显式接口实现的符号 Name 是 "IFoo.X" 形态，不是合法标识符。</summary>
internal static class BackingFieldNaming
{
    /// <summary>为待生成后备字段一次性算定名字：同类型按源序，首声明者保留裸名、后撞者加 Value。
    /// <paramref name="preserveDeclarationName"/> 为 true 时字段名直接取声明标识符原样（不做
    /// camelCase），保住户成员的反射/序列化名字面量（分工见
    /// <see cref="SerializationMemberMetadata"/>）。</summary>
    public static IReadOnlyDictionary<IPropertySymbol, string> Assign(
        IEnumerable<(IPropertySymbol Prop, string DeclName)> propertiesNeedingFields,
        bool preserveDeclarationName = false)
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
                // PD-5/序列化面：默认 camelCase（与转换器既有的 Java 字段名派生一致）；
                // preserve 模式保留声明原名（首字母大小写不折叠）。
                var baseName = preserveDeclarationName || declName.Length == 0
                    ? declName
                    : char.ToLowerInvariant(declName[0]) + declName[1..];
                var name = JavaNaming.EscapeJavaKeyword(baseName);
                while (IsTaken(name, prop, prop.ContainingType!)
                       || reserved.Contains(name)
                       // camelCase 后的名字可能撞 C# 保留关键字：Java 关键字转义只覆盖
                       // Java 侧（int → intValue），而 String/Object/Base 这类仅 C#
                       // 保留的单词会原样留下（真实 SDK：`public string string;` 使
                       // 本地解析校验失败、声明文件回滚而使用点仍被改写 → CS1061）。
                       || IsCSharpReservedKeyword(name)
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

    /// <summary>生成的标识符必须是合法 C# 标识符：保留关键字（string/object/base…）不能作字段名。</summary>
    private static bool IsCSharpReservedKeyword(string name)
        => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None;

    private static bool IsTaken(string name, IPropertySymbol prop, INamedTypeSymbol type)
        => type.GetMembers(name).Any(m =>
            !SymbolEqualityComparer.Default.Equals(m, prop) && !m.IsImplicitlyDeclared);
}
