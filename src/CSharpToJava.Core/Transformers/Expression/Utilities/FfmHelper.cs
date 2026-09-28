using System;
using CSharpToJava.Core.Context;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.Transformers.Expression.Utilities;

public class FixedPointerInfo
{
    public string VariableName { get; set; } = "";
    public string CSharpElementTypeName { get; set; } = "";
    public string ValueLayoutName { get; set; } = "";
    public int ElementSize { get; set; }
    public bool NeedsUnsignedMask { get; set; }
    public string MaskSuffix { get; set; } = "";
    public string WriteCast { get; set; } = "";
}

public static class FfmHelper
{
    public static string GetValueLayoutName(string csharpElementType) => csharpElementType switch
    {
        "byte" or "Byte" or "System.Byte" => "JAVA_BYTE",
        "sbyte" or "SByte" or "System.SByte" => "JAVA_BYTE",
        "char" or "Char" or "System.Char" => "JAVA_CHAR",
        "short" or "Short" or "System.Int16" => "JAVA_SHORT",
        "ushort" or "UInt16" or "System.UInt16" => "JAVA_CHAR",
        "int" or "Int32" or "System.Int32" => "JAVA_INT",
        "uint" or "UInt32" or "System.UInt32" => "JAVA_INT",
        "long" or "Int64" or "System.Int64" => "JAVA_LONG",
        "ulong" or "UInt64" or "System.UInt64" => "JAVA_LONG",
        "float" or "Single" or "System.Single" => "JAVA_FLOAT",
        "double" or "Double" or "System.Double" => "JAVA_DOUBLE",
        "bool" or "Boolean" or "System.Boolean" => "JAVA_BOOLEAN",
        _ => "JAVA_BYTE"
    };

    public static int GetElementSize(string csharpElementType) => csharpElementType switch
    {
        "byte" or "Byte" or "System.Byte" => 1,
        "sbyte" or "SByte" or "System.SByte" => 1,
        "char" or "Char" or "System.Char" => 2,
        "short" or "Short" or "System.Int16" => 2,
        "ushort" or "UInt16" or "System.UInt16" => 2,
        "int" or "Int32" or "System.Int32" => 4,
        "uint" or "UInt32" or "System.UInt32" => 4,
        "long" or "Int64" or "System.Int64" => 8,
        "ulong" or "UInt64" or "System.UInt64" => 8,
        "float" or "Single" or "System.Single" => 4,
        "double" or "Double" or "System.Double" => 8,
        "bool" or "Boolean" or "System.Boolean" => 1,
        _ => 1
    };

    public static bool NeedsUnsignedMask(string csharpElementType) => csharpElementType switch
    {
        "byte" or "Byte" or "System.Byte" => true,
        "ushort" or "UInt16" or "System.UInt16" => true,
        "uint" or "UInt32" or "System.UInt32" => true,
        _ => false
    };

    public static string GetMaskSuffix(string csharpElementType) => csharpElementType switch
    {
        "byte" or "Byte" or "System.Byte" => "& 0xFF",
        "ushort" or "UInt16" or "System.UInt16" => "& 0xFFFF",
        "uint" or "UInt32" or "System.UInt32" => "& 0xFFFFFFFFL",
        _ => ""
    };

    public static string GetWriteCast(string csharpElementType) => csharpElementType switch
    {
        "byte" or "Byte" or "System.Byte" => "(byte)",
        "ushort" or "UInt16" or "System.UInt16" => "(char)",
        "uint" or "UInt32" or "System.UInt32" => "(int)",
        _ => ""
    };

    public static string GetScratchArrayType(string csharpElementType) => csharpElementType switch
    {
        "byte" or "Byte" or "System.Byte" => "byte",
        "sbyte" or "SByte" or "System.SByte" => "byte",
        "char" or "Char" or "System.Char" => "char",
        "short" or "Short" or "System.Int16" => "short",
        "ushort" or "UInt16" or "System.UInt16" => "char",
        "int" or "Int32" or "System.Int32" => "int",
        "uint" or "UInt32" or "System.UInt32" => "int",
        "long" or "Int64" or "System.Int64" => "long",
        "ulong" or "UInt64" or "System.UInt64" => "long",
        "float" or "Single" or "System.Single" => "float",
        "double" or "Double" or "System.Double" => "double",
        "bool" or "Boolean" or "System.Boolean" => "byte",
        _ => "byte"
    };

    public static string GenerateAddressOfScratchInit(string segmentName, string sourceExpression, FixedPointerInfo info)
    {
        var arrayType = GetScratchArrayType(info.CSharpElementTypeName);
        var valueExpression = IsBooleanElementType(info.CSharpElementTypeName)
            ? $"{sourceExpression} ? (byte) 1 : (byte) 0"
            : info.WriteCast.Length > 0
            ? $"({info.WriteCast}({sourceExpression}))"
            : sourceExpression;
        return $"MemorySegment {segmentName} = MemorySegment.ofArray(new {arrayType}[] {{ {valueExpression} }});";
    }

    private static bool IsBooleanElementType(string csharpElementType) => csharpElementType switch
    {
        "bool" or "Boolean" or "System.Boolean" => true,
        _ => false
    };

    public static string GetPointerElementTypeName(TypeSyntax elementType)
    {
        if (elementType is PredefinedTypeSyntax predefined)
            return predefined.Keyword.Text;
        if (elementType is IdentifierNameSyntax identifier)
            return identifier.Identifier.Text;
        return elementType.ToString();
    }

    public static string GetPointerElementTypeName(ITypeSymbol elementType)
    {
        return elementType.SpecialType switch
        {
            SpecialType.System_Byte => "byte",
            SpecialType.System_SByte => "sbyte",
            SpecialType.System_Char => "char",
            SpecialType.System_Int16 => "short",
            SpecialType.System_UInt16 => "ushort",
            SpecialType.System_Int32 => "int",
            SpecialType.System_UInt32 => "uint",
            SpecialType.System_Int64 => "long",
            SpecialType.System_UInt64 => "ulong",
            SpecialType.System_Single => "float",
            SpecialType.System_Double => "double",
            SpecialType.System_Boolean => "bool",
            _ => elementType.Name
        };
    }

    public static FixedPointerInfo CreatePointerInfo(string variableName, string csharpElementType)
    {
        return new FixedPointerInfo
        {
            VariableName = variableName,
            CSharpElementTypeName = csharpElementType,
            ValueLayoutName = GetValueLayoutName(csharpElementType),
            ElementSize = GetElementSize(csharpElementType),
            NeedsUnsignedMask = NeedsUnsignedMask(csharpElementType),
            MaskSuffix = GetMaskSuffix(csharpElementType),
            WriteCast = GetWriteCast(csharpElementType)
        };
    }

    public static string GeneratePointerRead(string segmentExpr, FixedPointerInfo info, string offsetExpr)
    {
        string helper = info.ValueLayoutName switch
        {
            "JAVA_CHAR" => $"(char) MemorySegmentHelper.getChar({segmentExpr}, {offsetExpr})",
            "JAVA_SHORT" => $"MemorySegmentHelper.getShort({segmentExpr}, {offsetExpr})",
            "JAVA_INT" => $"MemorySegmentHelper.getInt({segmentExpr}, {offsetExpr})",
            "JAVA_LONG" => $"MemorySegmentHelper.getLong({segmentExpr}, {offsetExpr})",
            _ => null
        };
        if (helper != null)
            return helper;
        string read = $"{segmentExpr}.get(ValueLayout.{info.ValueLayoutName}, {offsetExpr})";
        return info.NeedsUnsignedMask ? $"({read} {info.MaskSuffix})" : read;
    }

    /// <summary>
    /// Generate pointer read with out-of-bounds protection.
    /// C# raw pointer reads have no bounds checking (buffer over-read is undefined behavior but doesn't crash).
    /// Java's MemorySegment.get() strictly checks bounds and throws IndexOutOfBoundsException.
    /// When offset + elementSize exceeds the base segment size, return the type's default value
    /// to safely simulate C#'s undefined behavior (reading garbage that won't match expected values).
    /// </summary>
    public static string GeneratePointerReadWithBoundsCheck(
        string segmentExpr, FixedPointerInfo info, string offsetExpr, string baseSegmentExpr,
        ConversionContext? context = null)
    {
        // When the pointer has been moved (segmentExpr != baseSegmentExpr),
        // calculate the absolute offset from the base segment start.
        string absOffsetExpr = segmentExpr == baseSegmentExpr
            ? offsetExpr
            : $"({segmentExpr}.address() - {baseSegmentExpr}.address() + ({offsetExpr}))";

        // When the offset expression contains side effects (e.g., start++),
        // evaluate it once into a temporary variable to avoid double evaluation
        // in the ternary condition and branch.
        string safeOffsetExpr = absOffsetExpr;
        if (context != null && HasSideEffect(offsetExpr))
        {
            var offsetVar = context.GenerateSyntheticName("_offset");
            context.AddPreStatement($"long {offsetVar} = {absOffsetExpr}");
            safeOffsetExpr = offsetVar;
        }

        string read = info.ValueLayoutName switch
        {
            "JAVA_CHAR" => $"(char) MemorySegmentHelper.getChar({baseSegmentExpr}, {safeOffsetExpr})",
            "JAVA_SHORT" => $"MemorySegmentHelper.getShort({baseSegmentExpr}, {safeOffsetExpr})",
            "JAVA_INT" => $"MemorySegmentHelper.getInt({baseSegmentExpr}, {safeOffsetExpr})",
            "JAVA_LONG" => $"MemorySegmentHelper.getLong({baseSegmentExpr}, {safeOffsetExpr})",
            _ => $"{baseSegmentExpr}.get(ValueLayout.{info.ValueLayoutName}, {safeOffsetExpr})"
        };
        if (info.NeedsUnsignedMask)
            read = $"({read} {info.MaskSuffix})";

        string defaultValue = GetDefaultReadValue(info.CSharpElementTypeName);
        return $"({safeOffsetExpr} + {info.ElementSize} <= {baseSegmentExpr}.byteSize() ? {read} : {defaultValue})";
    }

    private static bool HasSideEffect(string expr)
    {
        // Expressions containing ++ or -- have side effects and must not be
        // evaluated more than once. Other expressions (variables, constants,
        // casts, arithmetic, address() calls) are safe to evaluate repeatedly.
        return expr.Contains("++") || expr.Contains("--");
    }

    private static string GetDefaultReadValue(string csharpElementType) => csharpElementType switch
    {
        "byte" or "sbyte" => "0",
        "char" or "ushort" => "'\\0'",
        "short" => "0",
        "int" or "uint" => "0",
        "long" or "ulong" => "0L",
        "float" => "0.0f",
        "double" => "0.0d",
        "bool" => "false",
        _ => "0"
    };

    public static string GeneratePointerWrite(string segmentExpr, FixedPointerInfo info, string offsetExpr, string valueExpr)
    {
        string castValue = info.WriteCast.Length > 0 ? $"({info.WriteCast}({valueExpr}))" : valueExpr;
        return info.ValueLayoutName switch
        {
            "JAVA_CHAR" => $"MemorySegmentHelper.setChar({segmentExpr}, {offsetExpr}, {castValue})",
            "JAVA_SHORT" => $"MemorySegmentHelper.setShort({segmentExpr}, {offsetExpr}, {castValue})",
            "JAVA_INT" => $"MemorySegmentHelper.setInt({segmentExpr}, {offsetExpr}, {castValue})",
            "JAVA_LONG" => $"MemorySegmentHelper.setLong({segmentExpr}, {offsetExpr}, {castValue})",
            _ => $"{segmentExpr}.set(ValueLayout.{info.ValueLayoutName}, {offsetExpr}, {castValue})"
        };
    }

    public static string GeneratePointerArithmetic(string segmentExpr, FixedPointerInfo info, string offsetExpr)
    {
        if (info.ElementSize == 1)
            return $"{segmentExpr}.asSlice({offsetExpr})";
        return $"{segmentExpr}.asSlice((long)({offsetExpr}) * {info.ElementSize})";
    }

    public static string GenerateMemorySegmentInit(string variableName, string initializerExpr, FixedPointerInfo info, bool isString, bool isNull, string? baseVarName = null)
    {
        if (isNull)
            return $"MemorySegment {variableName} = MemorySegment.NULL;";
        if (isString)
        {
            if (baseVarName != null)
                return $"MemorySegment {baseVarName} = MemorySegment.ofArray({initializerExpr}.toCharArray());\nMemorySegment {variableName} = {baseVarName};";
            return $"MemorySegment {variableName} = MemorySegment.ofArray({initializerExpr}.toCharArray());";
        }
        if (baseVarName != null)
            return $"MemorySegment {baseVarName} = MemorySegment.ofArray({initializerExpr});\nMemorySegment {variableName} = {baseVarName};";
        return $"MemorySegment {variableName} = MemorySegment.ofArray({initializerExpr});";
    }

    public static string[] GetRequiredImports(bool usesArena)
    {
        if (usesArena)
            return ["java.lang.foreign.MemorySegment", "java.lang.foreign.ValueLayout", "java.lang.foreign.Arena"];
        return ["java.lang.foreign.MemorySegment", "java.lang.foreign.ValueLayout"];
    }

    /// <summary>
    /// Renders a binary pointer operation: p + n, p - p, p - n, pointer ordering/equality.
    /// Returns null when the expression is not a handled pointer operation so the caller
    /// continues the normal pipeline (operands are transformed lazily, only on a match).
    /// </summary>
    public static string? TryTransformPointerBinary(BinaryExpressionSyntax node, string op, ConversionContext ctx)
    {
        var leftType = ctx.GetTypeInfo(node.Left).Type;
        var rightType = ctx.GetTypeInfo(node.Right).Type;
        var leftIsPtr = leftType is IPointerTypeSymbol;
        var rightIsPtr = rightType is IPointerTypeSymbol;
        var f = ExpressionTransformerFacade.Instance;

        if (op == "+" && leftType is IPointerTypeSymbol addPtr)
        {
            var (left, right) = (f.Transform(node.Left, ctx), f.Transform(node.Right, ctx));
            return PointerAddSub(left, right, addPtr, ctx, "+");
        }
        if (op == "-" && leftType is IPointerTypeSymbol subPtr)
        {
            var (left, right) = (f.Transform(node.Left, ctx), f.Transform(node.Right, ctx));
            return rightIsPtr ? PointerDiff(left, right, subPtr, ctx)
                              : PointerAddSub(left, right, subPtr, ctx, "-");
        }
        if (op is "==" or "!=" && (leftIsPtr || rightIsPtr))
        {
            var (left, right) = (f.Transform(node.Left, ctx), f.Transform(node.Right, ctx));
            return PointerEquals(node, op, left, right, leftIsPtr, rightIsPtr, ctx);
        }
        if (op is "<" or ">" or "<=" or ">=" && leftIsPtr && rightIsPtr)
        {
            var (left, right) = (f.Transform(node.Left, ctx), f.Transform(node.Right, ctx));
            return $"{PointerAddress(node.Left, left, ctx)} {op} {PointerAddress(node.Right, right, ctx)}";
        }
        return null;
    }

    /// <summary>p + n / p - n, with in-bounds slices preferred over raw advance.</summary>
    private static string? PointerAddSub(string left, string right, IPointerTypeSymbol ptr,
        ConversionContext ctx, string sign)
    {
        var info = ctx.FindPointerInfo(left.Trim())
            ?? (ptr.PointedAtType != null ? CreatePointerInfo("", ptr.PointedAtType.ToDisplayString()) : null);
        if (info == null) return null; // fall back to standard rendering

        var scale = info.ElementSize == 1 ? right : $"(long)({right}) * {info.ElementSize}";
        if (ctx.TryGetPointerBase(left.Trim(), out var baseVar))
        {
            var delta = sign == "+" ? scale : $"-{scale}";
            var offset = $"{left}.address() - {baseVar}.address() {sign} {scale}";
            return $"({offset} >= 0 && {offset} <= {baseVar}.byteSize() " +
                   $"? {baseVar}.asSlice({offset}) : MemorySegmentHelper.advance({left}, {delta}))";
        }
        if (sign == "-")
            return info.ElementSize == 1
                ? $"MemorySegmentHelper.advance({left}, -{right})"
                : $"MemorySegmentHelper.advance({left}, -(long)({right}) * {info.ElementSize})";
        return GeneratePointerArithmetic(left.Trim(), info, right);
    }

    private static string PointerDiff(string left, string right, IPointerTypeSymbol leftPtr, ConversionContext ctx)
    {
        var info = ctx.FindPointerInfo(left.Trim())
            ?? CreatePointerInfo("", leftPtr.PointedAtType?.ToDisplayString() ?? "byte");
        var elementSize = info?.ElementSize ?? 1;
        return elementSize == 1
            ? $"({left}.address() - {right}.address())"
            : $"({left}.address() - {right}.address()) / {elementSize}";
    }

    private static string? PointerEquals(BinaryExpressionSyntax node, string op,
        string left, string right, bool leftIsPtr, bool rightIsPtr, ConversionContext ctx)
    {
        if (leftIsPtr && rightIsPtr)
            return $"{PointerAddress(node.Left, left, ctx)} {op} {PointerAddress(node.Right, right, ctx)}";

        var leftIsNull = node.Left.IsKind(SyntaxKind.NullLiteralExpression);
        var rightIsNull = node.Right.IsKind(SyntaxKind.NullLiteralExpression);
        if (!((leftIsPtr && rightIsNull) || (rightIsPtr && leftIsNull)))
            return null; // pointer vs non-null/non-pointer: fall back to standard rendering

        // Segments from asSlice() are never reference-equal; null also means zero-address.
        var pointer = leftIsPtr ? left : right;
        return op == "=="
            ? $"({pointer} == null || {pointer}.address() == 0)"
            : $"({pointer} != null && {pointer}.address() != 0)";
    }

    /// <summary>Pointer operands compare via .address(); (byte*)n casts of integrals stay raw.</summary>
    private static string PointerAddress(ExpressionSyntax node, string transformed, ConversionContext ctx)
    {
        if (node is CastExpressionSyntax cast
            && ctx.GetTypeInfo(cast.Expression).Type is { } inner
            && inner.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64
                or SpecialType.System_Int16 or SpecialType.System_UInt32 or SpecialType.System_UInt64
                or SpecialType.System_UInt16 or SpecialType.System_Byte or SpecialType.System_SByte
                or SpecialType.System_Char or SpecialType.System_IntPtr or SpecialType.System_UIntPtr)
            return transformed;
        return transformed + ".address()";
    }
}
