using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ConcurrencyNormalize;

/// <summary>
/// CN-7/CN-9 volatile 审计（v1 只审计不重写，设计 §2.5/§4.1）：判定谓词由
/// <see cref="MonitorRewriteAnalyzer"/> 调用；重写分支按设计明确不接实现
/// （<c>FrontLoadVolatile</c> 置 true 在 <see cref="ConcurrencyOptions"/> 即抛
/// <see cref="NotSupportedException"/>，这里不预埋半成品）。
/// 分类口径与转换器 FieldTransformer 对齐：基元 = 映射到 Java 8 基元类型的
/// 定宽类型；枚举/引用类型/其他一律非基元（对应 CS2J2940 Info）。
/// </summary>
internal static class VolatileRewriter
{
    public enum VolatileFieldClassification
    {
        Primitive,
        NonPrimitive,
    }

    /// <summary>CN-7：字段声明带 volatile 修饰符（转换器注释的诊断化目标）。</summary>
    public static bool IsVolatileFieldDeclaration(FieldDeclarationSyntax declaration)
        => declaration.Modifiers.Any(m => m.IsKind(SyntaxKind.VolatileKeyword));

    /// <summary>CN-7：volatile 字段基元/非基元分类。</summary>
    public static VolatileFieldClassification ClassifyField(IFieldSymbol field)
        => IsVolatilePrimitive(field.Type)
            ? VolatileFieldClassification.Primitive
            : VolatileFieldClassification.NonPrimitive;

    /// <summary>
    /// Java volatile 可直映的定宽基元集（bool/char/8 个定宽整数/float/double，
    /// 与 FieldTransformer 的 isJavaPrimitive 判定同口径）。枚举与引用类型归非基元。
    /// </summary>
    public static bool IsVolatilePrimitive(ITypeSymbol type)
    {
        return type is INamedTypeSymbol { } named
            && named.SpecialType is SpecialType.System_Boolean
                or SpecialType.System_Char
                or SpecialType.System_SByte
                or SpecialType.System_Byte
                or SpecialType.System_Int16
                or SpecialType.System_UInt16
                or SpecialType.System_Int32
                or SpecialType.System_UInt32
                or SpecialType.System_Int64
                or SpecialType.System_UInt64
                or SpecialType.System_Single
                or SpecialType.System_Double;
    }

    /// <summary>CN-9：System.Threading.Volatile 静态 API 命中（不改写，仅计数 + CS2J2960）。</summary>
    public static bool IsVolatileStaticApiMethod(IMethodSymbol method, INamedTypeSymbol? volatileType)
    {
        if (volatileType == null) return false;
        var containingType = method.OriginalDefinition?.ContainingType;
        return containingType != null
            && SymbolEqualityComparer.Default.Equals(containingType, volatileType);
    }
}
