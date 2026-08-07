using System.Collections.Generic;

namespace CSharpToJava.Core.TypeScript;

/// <summary>
/// TypeScript 标识符安全化与命名规则。
/// 处理 TS 保留字冲突、C# @ 转义前缀去除、重载后缀生成、构造器工厂命名。
/// </summary>
public static class TsNaming
{
    /// <summary>
    /// TypeScript 保留字与上下文关键字集合（与 C# 交集部分需后缀化）。
    /// </summary>
    private static readonly HashSet<string> TsReservedWords = new()
    {
        "break", "case", "catch", "class", "const", "continue", "debugger",
        "default", "delete", "do", "else", "enum", "export", "extends",
        "false", "finally", "for", "function", "if", "implements", "import", "in",
        "instanceof", "interface", "let", "new", "null", "return", "super", "switch",
        "this", "throw", "true", "try", "typeof", "var", "void", "while",
        "with", "yield", "any", "as", "async", "await", "bigint", "boolean",
        "constructor", "declare", "get", "infer", "is", "keyof", "module",
        "namespace", "never", "number", "object", "of", "package", "private",
        "protected", "public", "readonly", "require", "satisfies", "set",
        "static", "string", "symbol", "type", "undefined", "unknown",
    };

    /// <summary>
    /// 将 C# 标识符安全化为合法 TS 标识符。
    /// 规则：① 去除 C# @ 前缀；② 若结果为 TS 保留字，追加 $ 后缀。
    /// </summary>
    public static string SafeIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name)) return "_";

        // 去除 C# @ 转义前缀
        if (name.StartsWith("@", StringComparison.Ordinal))
        {
            name = name[1..];
        }

        // 剥离 @ 后为空（如输入 "@"）-> 下划线占位
        if (name.Length == 0) return "_";

        // TS 保留字冲突 -> 追加 $
        if (TsReservedWords.Contains(name))
        {
            return name + "$";
        }

        return name;
    }

    /// <summary>
    /// 类名安全化（同 SafeIdentifier，但保留 PascalCase）。
    /// </summary>
    public static string SafeClassName(string name)
    {
        return SafeIdentifier(name);
    }

    /// <summary>
    /// 字符串字面量内容转义（测试显示名等）：\ 与 " 转义为 TS 双引号字符串安全形态。
    /// </summary>
    public static string EscapeStringLiteral(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"");
    }

    /// <summary>
    /// 生成重载方法后缀。规则：_type1_type2（TS 映射类型名小写连接）。
    /// 示例：["string", "number"] -> "_string_number"
    /// 前置条件：调用方必须传入已小写、已消毒的简单 TS 类型名（如 "string"/"number"）；
    /// 复杂类型（数组/泛型/联合）由调用方（Task 23 重载 rewriter）先行消毒。
    /// </summary>
    public static string OverloadSuffix(IReadOnlyList<string> paramTsTypes)
    {
        if (paramTsTypes.Count == 0) return "";
        return "_" + string.Join("_", paramTsTypes);
    }

    /// <summary>
    /// 构造器重载转静态工厂的命名。规则：Create$N（N 按参数列表序号）。
    /// 与方法重载的类型后缀规则刻意区分（构造器无名称，类型后缀易产生超长难读名）。
    /// </summary>
    public static string FactoryName(int ordinal)
    {
        return $"Create${ordinal}";
    }
}