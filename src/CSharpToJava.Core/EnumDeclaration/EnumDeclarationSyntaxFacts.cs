using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.EnumDeclaration;

/// <summary>
/// 纯函数事实集合（设计 §4.1 SyntaxFacts）：U 八分类/keyword、窄类型、
/// 可空解包、[Flags] 符号+语法双判、常量值读取、Enum API 分类、碰撞命名、
/// 重写标注。与 E04 EnumSyntaxRewriter 同口径的小函数在此复制（不引用其内部契约）。
/// </summary>
internal static class EnumDeclarationSyntaxFacts
{
    public const string RewriteAnnotationKind = "Cs2jEnumDeclarationRewrite";
    public static readonly SyntaxAnnotation RewriteAnnotation = new(RewriteAnnotationKind);

    public static readonly SpecialType[] IntegerSpecialTypes =
    {
        SpecialType.System_SByte,
        SpecialType.System_Byte,
        SpecialType.System_Int16,
        SpecialType.System_UInt16,
        SpecialType.System_Int32,
        SpecialType.System_UInt32,
        SpecialType.System_Int64,
        SpecialType.System_UInt64,
    };

    public static SyntaxKind GetTypeKeywordKind(SpecialType st) => st switch
    {
        SpecialType.System_Int32 => SyntaxKind.IntKeyword,
        SpecialType.System_UInt32 => SyntaxKind.UIntKeyword,
        SpecialType.System_Int64 => SyntaxKind.LongKeyword,
        SpecialType.System_UInt64 => SyntaxKind.ULongKeyword,
        SpecialType.System_Int16 => SyntaxKind.ShortKeyword,
        SpecialType.System_UInt16 => SyntaxKind.UShortKeyword,
        SpecialType.System_Byte => SyntaxKind.ByteKeyword,
        SpecialType.System_SByte => SyntaxKind.SByteKeyword,
        _ => SyntaxKind.None,
    };

    public static string GetTypeKeyword(SpecialType st) => st switch
    {
        SpecialType.System_Int32 => "int",
        SpecialType.System_UInt32 => "uint",
        SpecialType.System_Int64 => "long",
        SpecialType.System_UInt64 => "ulong",
        SpecialType.System_Int16 => "short",
        SpecialType.System_UInt16 => "ushort",
        SpecialType.System_Byte => "byte",
        SpecialType.System_SByte => "sbyte",
        _ => "int",
    };

    /// <summary>窄整数：byte/sbyte/short/ushort（D14 强转需 unchecked 包裹）。</summary>
    public static bool IsNarrowType(SpecialType st)
        => st is SpecialType.System_Byte or SpecialType.System_SByte
            or SpecialType.System_Int16 or SpecialType.System_UInt16;

    public static bool IsUnsigned(SpecialType st)
        => st is SpecialType.System_Byte or SpecialType.System_UInt16
            or SpecialType.System_UInt32 or SpecialType.System_UInt64;

    /// <summary>X 格式十六进制宽度（D6）：byte/sbyte X2、short/ushort X4、int/uint X8、long/ulong X16。</summary>
    public static int HexWidth(SpecialType st) => st switch
    {
        SpecialType.System_Byte or SpecialType.System_SByte => 2,
        SpecialType.System_Int16 or SpecialType.System_UInt16 => 4,
        SpecialType.System_Int32 or SpecialType.System_UInt32 => 8,
        SpecialType.System_Int64 or SpecialType.System_UInt64 => 16,
        _ => 8,
    };

    /// <summary>
    /// X 渲染时对 <c>(ulong)v</c> 施加的 U 位宽 mask（无符号十进制字面量 + UL 后缀）；
    /// long/ulong 为 64 位恒等，返回 null（不发射 mask）。
    /// 有符号窄类型负值经 (ulong) 会符号扩展为 64 位 1（sbyte -1 → 16 个 F），
    /// 必须先按 U 位宽截断：sbyte/short/int 的 -1 分别渲染为 FF/FFFF/FFFFFFFF（.NET X 语义）。
    /// </summary>
    public static string? HexMaskLiteral(SpecialType st) => st switch
    {
        SpecialType.System_Byte or SpecialType.System_SByte => "255UL",
        SpecialType.System_Int16 or SpecialType.System_UInt16 => "65535UL",
        SpecialType.System_Int32 or SpecialType.System_UInt32 => "4294967295UL",
        _ => null, // long/ulong：64 位，(ulong) 重解释即原位模式
    };

    /// <summary>解包 Nullable&lt;T&gt;；非可空返回原符号；非枚举返回 null。</summary>
    public static INamedTypeSymbol? UnwrapNullableEnum(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named) return null;
        if (named.TypeKind == TypeKind.Enum) return named;
        if (named.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T
            && named.TypeArguments.Length == 1
            && named.TypeArguments[0] is INamedTypeSymbol inner
            && inner.TypeKind == TypeKind.Enum)
        {
            return inner;
        }
        return null;
    }

    public static bool IsNullableOfEnum(ITypeSymbol? type, INamedTypeSymbol enumSymbol)
    {
        if (type is not INamedTypeSymbol named) return false;
        return named.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T
            && named.TypeArguments.Length == 1
            && SymbolEqualityComparer.Default.Equals(named.TypeArguments[0], enumSymbol);
    }

    /// <summary>[Flags] 符号判（System.FlagsAttribute/System.Flags 别名）。</summary>
    public static bool IsFlagsSymbol(INamedTypeSymbol enumSymbol)
        => enumSymbol.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString() is "System.FlagsAttribute" or "System.Flags");

    /// <summary>[Flags] 语法判（Flags/FlagsAttribute/全限定名，含 using System 别名）。</summary>
    public static bool IsFlagsSyntax(EnumDeclarationSyntax enumDecl)
        => enumDecl.AttributeLists
            .SelectMany(al => al.Attributes)
            .Any(a => a.Name.ToString() is "Flags" or "FlagsAttribute"
                or "System.Flags" or "System.FlagsAttribute");

    public static bool IsFlagsAttributeSyntax(AttributeSyntax attribute)
        => attribute.Name.ToString() is "Flags" or "FlagsAttribute"
            or "System.Flags" or "System.FlagsAttribute";

    /// <summary>读 enum 成员编译期常量值（D4）；取不到返回 false。</summary>
    public static bool TryGetConstantValue(IFieldSymbol field, out object? value)
    {
        value = field.ConstantValue;
        return value != null;
    }

    /// <summary>
    /// 把装箱常量值渲染为目标 U 的 C# 整数字面量（自带正确后缀），外层仍需 (U) 强转。
    /// </summary>
    public static string RenderIntegerLiteral(object? boxed, SpecialType underlying)
    {
        if (IsUnsigned(underlying))
        {
            var u = Convert.ToUInt64(boxed, CultureInfo.InvariantCulture);
            return underlying == SpecialType.System_UInt64
                ? u.ToString(CultureInfo.InvariantCulture) + "UL"
                : u.ToString(CultureInfo.InvariantCulture);
        }
        var l = Convert.ToInt64(boxed, CultureInfo.InvariantCulture);
        return underlying == SpecialType.System_Int64
            ? l.ToString(CultureInfo.InvariantCulture) + "L"
            : l.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>把常量值转 ulong 位模式（比较/重复值检测统一口径）。</summary>
    public static ulong ToUInt64(object? boxed, SpecialType underlying)
        => IsUnsigned(underlying)
            ? Convert.ToUInt64(boxed, CultureInfo.InvariantCulture)
            : unchecked((ulong)Convert.ToInt64(boxed, CultureInfo.InvariantCulture));

    // ── System.Enum 静态 API 分类（D8） ──

    public static readonly HashSet<string> EnumStaticMethods = new(StringComparer.Ordinal)
    {
        "GetValues", "GetNames", "GetName", "Parse", "TryParse",
        "IsDefined", "Format", "ToObject",
    };

    public static bool IsSystemEnumType(ITypeSymbol? type)
        => type?.ToDisplayString() is "System.Enum";

    // ── 序列化名字契约特性（D10/ED040） ──

    public static readonly HashSet<string> NameContractAttributeNames = new(StringComparer.Ordinal)
    {
        "System.Text.Json.Serialization.JsonStringEnumMemberNameAttribute",
        "JsonStringEnumMemberName",
        "System.Runtime.Serialization.EnumMemberAttribute",
        "EnumMember",
        "System.Xml.Serialization.XmlEnumAttribute",
        "XmlEnum",
    };

    public static bool IsNameContractAttribute(AttributeData attr)
        => NameContractAttributeNames.Contains(attr.AttributeClass?.ToDisplayString() ?? string.Empty)
            || NameContractAttributeNames.Contains(attr.AttributeClass?.Name ?? string.Empty);

    public static bool IsJsonStringEnumConverterAttribute(AttributeData attr)
    {
        var n = attr.AttributeClass?.ToDisplayString() ?? string.Empty;
        return n is "System.Text.Json.Serialization.JsonConverterAttribute"
            || attr.AttributeClass?.Name is "JsonConverter";
    }

    /// <summary>JsonConverter(typeof(JsonStringEnumConverter)) 实参判定（语法层，D10）。</summary>
    public static bool IsJsonStringEnumConverterSyntax(AttributeSyntax attribute, SemanticModel model)
    {
        if (attribute.ArgumentList == null) return false;
        foreach (var arg in attribute.ArgumentList.Arguments)
        {
            if (arg.Expression is TypeOfExpressionSyntax tos)
            {
                var ti = model.GetTypeInfo(tos.Type);
                if (ti.Type?.Name is "JsonStringEnumConverter") return true;
            }
        }
        return false;
    }

    // ── 碰撞命名（D16） ──

    /// <summary>在既有成员名集合中确定性消解内部名（Value → Value2…）。</summary>
    public static string UniqueMemberName(string preferred, ISet<string> existing)
    {
        if (!existing.Contains(preferred)) return preferred;
        for (var i = 2; ; i++)
        {
            var candidate = preferred + i.ToString(CultureInfo.InvariantCulture);
            if (!existing.Contains(candidate)) return candidate;
        }
    }

    public static bool IsInTree(ISymbol symbol, Compilation compilation)
        => symbol.Locations.Any(l => l.IsInSource
            && compilation.SyntaxTrees.Any(t =>
                string.Equals(t.FilePath, l.SourceTree?.FilePath, StringComparison.OrdinalIgnoreCase)));
}
