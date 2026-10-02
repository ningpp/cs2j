using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.UnsafeFfm;

/// <summary>
/// UF061 封送布局判定的最小骨架（设计 D10，§9 M4）。白名单 opt-in
/// 子段未交付，pass 本身<b>不调用</b>本骨架，故 UF061 在 M4 不会触发；
/// 签名与判定口径在此冻结，供将来白名单通道直接消费，避免届时写死
/// 不可达分支。单测直接驱动本骨架以锁定行为。
/// 冻结事实（D10）：bool=1 字节（非平台默认 4）、char=UTF-16 两字节；
/// 字符串只读拷贝不回写；StructLayout/Pack/FieldOffset 不变；ref/out
/// 不穿 ABI；ValueLayout 真相与转换器 FfmHelper 表对齐（抽共享在白名单
/// 子段一并完成）。
/// </summary>
internal static class MarshalLayoutSkeleton
{
    /// <summary>bool 穿 ABI 固定 1 字节（D10②）。</summary>
    public const int BoolWidthBytes = 1;

    /// <summary>char 穿 ABI 固定 UTF-16 两字节（D10②）。</summary>
    public const int CharWidthBytes = 2;

    /// <summary>
    /// 判定候选 extern 方法的封送布局是否存在不可证明的阻断。
    /// <list type="bullet">
    /// <item>ref/out/in 实参穿 ABI（D10⑤）；</item>
    /// <item>union 风险：签名上的 struct 带 Explicit 布局（重叠偏移无法
    /// 静态消解）；</item>
    /// <item>无布局保证：穿 ABI 的 struct 持有非字符串引用类型字段
    /// （object/数组/接口/委托），其内存布局不可证明。</item>
    /// </list>
    /// </summary>
    public static bool TryGetLayoutBlocker(IMethodSymbol method, out string blocker)
    {
        foreach (var parameter in method.Parameters)
        {
            if (parameter.RefKind != RefKind.None)
            {
                blocker = $"parameter '{parameter.Name}' is {parameter.RefKind.ToString().ToLowerInvariant()}: " +
                    "ref/out/in arguments do not cross the FFM ABI (D10); route through a whitelisted " +
                    "facade that copies by value.";
                return true;
            }

            if (TryGetTypeLayoutBlocker(parameter.Type, out var typeBlocker))
            {
                blocker = $"parameter '{parameter.Name}': {typeBlocker}";
                return true;
            }
        }

        if (method.ReturnsVoid)
        {
            blocker = string.Empty;
            return false;
        }

        if (TryGetTypeLayoutBlocker(method.ReturnType, out var returnBlocker))
        {
            blocker = $"return type: {returnBlocker}";
            return true;
        }

        blocker = string.Empty;
        return false;
    }

    private static bool TryGetTypeLayoutBlocker(ITypeSymbol type, out string blocker)
    {
        if (type is INamedTypeSymbol { IsValueType: true } structType)
        {
            foreach (var attribute in structType.GetAttributes())
            {
                if (attribute.AttributeClass?.Name != "StructLayoutAttribute") continue;
                if (attribute.ConstructorArguments.Length > 0
                    && attribute.ConstructorArguments[0].Value is int kind && kind == 2)
                {
                    blocker = $"struct '{structType.Name}' uses StructLayout(LayoutKind.Explicit); overlapping " +
                        "field offsets cannot be proven non-overlapping (union risk, UF061).";
                    return true;
                }
            }

            foreach (var member in structType.GetMembers())
            {
                if (member is not IFieldSymbol field || field.IsStatic) continue;
                var fieldType = field.Type;
                if (fieldType.SpecialType == SpecialType.System_String) continue;
                if (fieldType.IsReferenceType || fieldType.TypeKind == TypeKind.Array)
                {
                    blocker = $"struct '{structType.Name}' field '{field.Name}' is a non-string reference " +
                        "type; its across-ABI layout is not provable (UF061).";
                    return true;
                }
            }
        }

        blocker = string.Empty;
        return false;
    }
}
