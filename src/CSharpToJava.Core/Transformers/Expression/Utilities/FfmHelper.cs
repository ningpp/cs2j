using System;
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
            ? $"{info.WriteCast} {sourceExpression}"
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
        string read = $"{segmentExpr}.get(ValueLayout.{info.ValueLayoutName}, {offsetExpr})";
        return info.NeedsUnsignedMask ? $"({read} {info.MaskSuffix})" : read;
    }

    public static string GeneratePointerWrite(string segmentExpr, FixedPointerInfo info, string offsetExpr, string valueExpr)
    {
        string castValue = info.WriteCast.Length > 0 ? $"{info.WriteCast} {valueExpr}" : valueExpr;
        return $"{segmentExpr}.set(ValueLayout.{info.ValueLayoutName}, {offsetExpr}, {castValue})";
    }

    public static string GeneratePointerArithmetic(string segmentExpr, FixedPointerInfo info, string offsetExpr)
    {
        if (info.ElementSize == 1)
            return $"{segmentExpr}.asSlice({offsetExpr})";
        return $"{segmentExpr}.asSlice((long){offsetExpr} * {info.ElementSize})";
    }

    public static string GenerateMemorySegmentInit(string variableName, string initializerExpr, FixedPointerInfo info, bool isString, bool isNull)
    {
        if (isNull)
            return $"MemorySegment {variableName} = MemorySegment.NULL;";
        if (isString)
            return $"MemorySegment {variableName} = MemorySegment.ofArray({initializerExpr}.toCharArray());";
        return $"MemorySegment {variableName} = MemorySegment.ofArray({initializerExpr});";
    }

    public static string[] GetRequiredImports(bool usesArena)
    {
        if (usesArena)
            return ["java.lang.foreign.MemorySegment", "java.lang.foreign.ValueLayout", "java.lang.foreign.Arena"];
        return ["java.lang.foreign.MemorySegment", "java.lang.foreign.ValueLayout"];
    }
}
