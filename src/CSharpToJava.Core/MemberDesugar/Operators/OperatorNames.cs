using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.MemberDesugar.Operators;

/// <summary>命名层（设计决策 1/1b/3/8）：C# 半区方法名 = 转换器 Java 名的 PascalCase 形态，
/// 经 MethodTransformer.JavaName（首字母小写）派生后与 OperatorTransformer 今发名逐名相等（零漂移）。</summary>
internal static class OperatorNames
{
    /// <summary>op 元数据名 → C# 方法基名（OpSymbolToJavaName 的 PascalCase 镜像，决策 1）。</summary>
    private static readonly IReadOnlyDictionary<string, string> OpBaseNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "op_Addition", "Add" },
            { "op_Subtraction", "Subtract" },
            { "op_Multiply", "Multiply" },
            { "op_Division", "Divide" },
            { "op_Modulus", "Mod" },
            { "op_Equality", "ValueEquals" },
            { "op_Inequality", "NotEquals" },
            { "op_GreaterThan", "GreaterThan" },
            { "op_LessThan", "LessThan" },
            { "op_GreaterThanOrEqual", "GreaterThanOrEqual" },
            { "op_LessThanOrEqual", "LessThanOrEqual" },
            { "op_BitwiseAnd", "And" },
            { "op_BitwiseOr", "Or" },
            { "op_ExclusiveOr", "Xor" },
            { "op_LogicalNot", "Not" },
            { "op_OnesComplement", "OnesComplement" },
            { "op_LeftShift", "LeftShift" },
            { "op_RightShift", "RightShift" },
            { "op_UnsignedRightShift", "UnsignedRightShift" },
            { "op_UnaryNegation", "Negate" },
            { "op_UnaryPlus", "Plus" },
        };

    /// <summary>决策 1b：checked 对 = 基名 + Checked（与 OperatorTransformer.cs:96-98 对齐）。</summary>
    public static string ConvertOperatorName(string metadataName)
    {
        if (metadataName.StartsWith("op_Checked", StringComparison.Ordinal))
            return ConvertOperatorName("op_" + metadataName["op_Checked".Length..]) + "Checked";
        return OpBaseNames.TryGetValue(metadataName, out var name) ? name : metadataName;
    }

    public static bool IsSupportedOperatorName(string metadataName)
        => OpBaseNames.ContainsKey(metadataName)
           || (metadataName.StartsWith("op_Checked", StringComparison.Ordinal)
               && OpBaseNames.ContainsKey("op_" + metadataName["op_Checked".Length..]));

    // ---- 决策 3：转换命名镜像表（Java 类型字符串 → 后缀；算法复刻 BuildConversionMethodSuffix）----

    private static readonly IReadOnlyDictionary<string, string> JavaTypeToSuffix =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "double", "Double" }, { "int", "Int" }, { "long", "Long" }, { "float", "Float" },
            { "boolean", "Boolean" }, { "String", "String" }, { "BigDecimal", "Decimal" },
            { "byte", "Byte" },
        };

    /// <summary>C# 目标类型 → 转换方法基名（不含消歧）；表外 → false（CS2J5177）。</summary>
    public static bool TryGetConversionBaseName(ITypeSymbol targetType, bool isExplicitOperator,
        out string baseName)
    {
        baseName = "";
        if (!TryGetJavaTypeName(targetType, out var javaType)) return false;
        if (!TryGetConversionBaseSuffix(javaType, out var suffix)) return false;
        baseName = "To" + suffix + (isExplicitOperator ? "Explicit" : "");
        return true;
    }

    /// <summary>镜像表判定与后缀（供单测直接核对）。</summary>
    public static bool TryGetConversionBaseSuffix(string javaType, out string suffix)
    {
        suffix = "";
        if (string.IsNullOrEmpty(javaType)) return false;
        var isArray = javaType.EndsWith("[]", StringComparison.Ordinal);
        var core = isArray ? javaType[..^2] : javaType;
        if (!JavaTypeToSuffix.TryGetValue(core, out var coreSuffix)) return false;
        suffix = isArray ? coreSuffix + "Array" : coreSuffix;
        return true;
    }

    /// <summary>C# 目标类型 → 镜像表 Java 类型名。Nullable/泛型/tuple/指针 → false。</summary>
    private static bool TryGetJavaTypeName(ITypeSymbol t, out string javaType)
    {
        switch (t.SpecialType)
        {
            case SpecialType.System_Double: javaType = "double"; return true;
            case SpecialType.System_Int32: javaType = "int"; return true;
            case SpecialType.System_Int64: javaType = "long"; return true;
            case SpecialType.System_Single: javaType = "float"; return true;
            case SpecialType.System_Boolean: javaType = "boolean"; return true;
            case SpecialType.System_String: javaType = "String"; return true;
            case SpecialType.System_Decimal: javaType = "BigDecimal"; return true;
            case SpecialType.System_Byte: javaType = "byte"; return true;
        }
        if (t is IArrayTypeSymbol arr && arr.Rank == 1)
        {
            if (!TryGetJavaTypeName(arr.ElementType, out var elem)) { javaType = ""; return false; }
            javaType = elem + "[]";
            return true;
        }
        javaType = "";
        return false;
    }

    // ---- 决策 8：确定性碰撞升级（Add → AddOperator → AddOperator2…）----

    public static string Disambiguate(string baseName, IReadOnlyCollection<string> taken)
    {
        if (!taken.Contains(baseName)) return baseName;
        var candidate = baseName + "Operator";
        if (!taken.Contains(candidate)) return candidate;
        for (var i = 2; i < 1000; i++)
        {
            candidate = baseName + "Operator"
                + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!taken.Contains(candidate)) return candidate;
        }
        return baseName; // 耗尽由调用方 CS2J5178 兜底
    }
}
