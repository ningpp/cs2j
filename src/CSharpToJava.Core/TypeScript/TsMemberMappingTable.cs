using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CSharpToJava.Core.TypeScript;

/// <summary>
/// Java→TypeScript 二次映射表。
/// 查找顺序（发射期，spec §3.1.2，三者一致）：
/// ① 内置表优先（代码，覆盖最高频项：size→length、add→push 等；键为 Java 风味名）
/// ② 配置表兜底（MemberMappings.json / TypeMappings.json；键为 C# 全名）
/// ③ 未命中 -> 返回原值 + false，调用方输出 /* cs2j: unmapped */ 诊断注释
/// 说明：M1 发射期（IR 为 Java 风味名）暂不消费配置表；M4 Task 36 注入 Roslyn
/// 语义模型后用 C# 全名 + arity 感知分解消费配置。
/// </summary>
public sealed class TsMemberMappingTable
{
    private readonly Dictionary<string, (string TsType, List<string> Imports)> _typeMappings = new();
    private readonly Dictionary<(string Type, string Method), (string TsMethod, List<string> Imports)> _methodMappings = new();
    private readonly Dictionary<(string Type, string Member), string> _memberMappings = new();
    private readonly HashSet<string> _bclExceptionNames;

    // 与 TypeMappingRegistry 一致的 JSON 反序列化选项（属性名大小写不敏感）。
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // 内置成员映射（最高频项，spec §3.1.2 ①）
    // IR 中已是 Java 风味，这里覆盖 Java 化 API 调用。
    // 注意：本表只收录“方法名→方法名”可直接替换的项（逐字拼接后为合法 TS 成员访问）。
    // 整调用级替换（equals→===、isEmpty→length === 0、System.out.println 等）由
    // 发射器/rewriter 特殊分支处理（M3 TsEqualityRewriter、M4 Task 36），不放进本表，
    // 否则按方法名替换会产出非法 TS（如 a.===(b)）。
    private static readonly Dictionary<(string Type, string Method), string> BuiltinMethodMappings = new()
    {
        { ("java.util.List", "size"), "length" },
        { ("java.util.Collection", "size"), "length" },
        { ("java.lang.String", "length"), "length" },
        { ("java.util.List", "add"), "push" },
        { ("java.util.ArrayList", "add"), "push" },
        { ("java.util.List", "contains"), "includes" },
        { ("java.util.List", "indexOf"), "indexOf" },
        { ("java.lang.String", "toUpperCase"), "toUpperCase" },
        { ("java.lang.String", "toLowerCase"), "toLowerCase" },
        { ("java.lang.String", "trim"), "trim" },
        { ("java.lang.String", "split"), "split" },
        { ("java.lang.String", "replace"), "replace" },
        { ("java.lang.String", "substring"), "substring" },
        { ("java.lang.String", "startsWith"), "startsWith" },
        { ("java.lang.String", "endsWith"), "endsWith" },
        { ("java.lang.String", "contains"), "includes" },
        { ("java.lang.String", "indexOf"), "indexOf" },
        { ("java.lang.String", "charAt"), "charAt" },
        { ("java.lang.Math", "sqrt"), "Math.sqrt" },
        { ("java.lang.Math", "abs"), "Math.abs" },
        { ("java.lang.Math", "max"), "Math.max" },
        { ("java.lang.Math", "min"), "Math.min" },
        { ("java.lang.Math", "floor"), "Math.floor" },
        { ("java.lang.Math", "ceil"), "Math.ceil" },
        { ("java.lang.Math", "round"), "Math.round" },
        { ("java.lang.Math", "pow"), "Math.pow" },
    };

    // 内置 Java 风味类型 → TS 类型映射（IR 中字段/参数/返回值类型均为 Java 名，
    // 发射器必须先经此表转换；spec 公共契约“IR 为 Java 风味”）
    private static readonly Dictionary<string, string> BuiltinJavaTypeMappings = new()
    {
        { "int", "number" }, { "long", "bigint" }, { "short", "number" },
        { "byte", "number" }, { "char", "string" }, { "float", "number" },
        { "double", "number" }, { "boolean", "boolean" }, { "void", "void" },
        { "String", "string" }, { "java.lang.String", "string" },
        { "Object", "any" }, { "java.lang.Object", "any" },
        { "Integer", "number" }, { "Long", "bigint" }, { "Boolean", "boolean" },
        { "Double", "number" }, { "Float", "number" }, { "Character", "string" },
        // 泛型约束 bound（Task 14）：C# IComparable/IDisposable 在 IR 中为 Java 风味名，
        // TS 无对应内建类型，约束降级为最宽松对象类型 object（spec §1.4.3 约束仅部分表达）
        { "Comparable", "object" }, { "AutoCloseable", "object" },
    };

    // 内置属性/字段映射
    private static readonly Dictionary<(string Type, string Member), string> BuiltinMemberMappings = new()
    {
        { ("java.util.List", "size"), "length" },
        { ("java.util.Collection", "size"), "length" },
        { ("java.lang.String", "length"), "length" },
        { ("java.util.ArrayList", "size"), "length" },
    };

    // ─── 异常反向映射（Task 21，spec §4.3.9）────────────────────────────────
    // TS 路径复用 C#→Java 前端，IR 中异常类型为 Java 风味名（Java TypeMappings.json
    // 映射：InvalidOperationException→IllegalStateException、Exception→RuntimeException、
    // NullReferenceException→NullPointerException 等）。发射前做完整反向映射：
    // Java 异常名 → TS 兼容库异常名（spec §4.3.9 类层次，M4 Task 34 实现）。
    // 查找规则（TryMapException）：
    // ① Java 标准名覆盖表（多个 C# 异常共射同一 Java 标准异常时的歧义消解，
    //    取值来源见 Java config/TypeMappings.json 各条目）
    // ② spec §4.3.9 兼容库类名集合（同名恒等：ArgumentException/FormatException/…）
    // ③ 已知 BCL 异常名但兼容库无对应类 → CsException + 调用方输出
    //    /* cs2j: unmapped exception */ 诊断注释
    // “已知 BCL 异常名”集合运行时从 Java TypeMappings.json 全量提取（csharp/java
    // 两侧简单名，EndsWith Exception/Error），配置缺失时回退内置高频集合。
    private static readonly Dictionary<string, string> JavaExceptionOverrides = new(StringComparer.Ordinal)
    {
        ["IllegalArgumentException"] = "ArgumentException",            // ← ArgumentOutOfRangeException/RankException
        ["IllegalStateException"] = "InvalidOperationException",       // ← InvalidOperationException
        ["IndexOutOfBoundsException"] = "IndexOutOfRangeException",    // ← IndexOutOfRangeException
        ["RuntimeException"] = "CsException",                          // ← Exception/ApplicationException
        ["UnsupportedOperationException"] = "NotSupportedException",   // ← NotImplementedException/NotSupportedException
    };

    private static readonly HashSet<string> CompatExceptionNames = new(StringComparer.Ordinal)
    {
        "CsException",
        "ArgumentException", "ArgumentNullException", "ArgumentOutOfRangeException",
        "InvalidOperationException", "NotSupportedException", "NotImplementedException",
        "IndexOutOfRangeException", "KeyNotFoundException",
        "ArithmeticException", "DivideByZeroException", "OverflowException",
        "FormatException", "IOException",
    };

    private static readonly HashSet<string> BuiltinBclExceptionNames = new(StringComparer.Ordinal)
    {
        // Java 标准异常名（共享管线映射目标）
        "IllegalArgumentException", "IllegalStateException", "IndexOutOfBoundsException",
        "NullPointerException", "RuntimeException", "UnsupportedOperationException",
        "ArithmeticException", "ClassCastException", "ClassNotFoundException",
        "NumberFormatException", "IOException", "SecurityException", "TimeoutException",
        "FileNotFoundException", "CancellationException", "EOFException",
        "ExceptionInInitializerError", "ExecutionException", "IllegalAccessException",
        "NoSuchFieldException", "ArrayStoreException", "Exception", "SystemException",
        // 常见恒等兼容异常（Java 配置恒等映射源；其余全量条目运行时从配置提取）
        "ArgumentException", "ArgumentNullException", "ArgumentOutOfRangeException",
        "InvalidOperationException", "ObjectDisposedException", "InvalidDataException",
        "NotSupportedException", "NotImplementedException", "FormatException",
        "IndexOutOfRangeException", "KeyNotFoundException", "AggregateException",
        "SocketException", "StackOverflowException", "UnauthorizedAccessException",
        "NullReferenceException", "InvalidCastException", "TypeLoadException",
        "ApplicationException", "Win32Exception", "RankException",
        "EndOfStreamException", "OperationCanceledException", "TaskCanceledException",
        "TypeInitializationException", "MemberAccessException", "MissingFieldException",
        "ArrayTypeMismatchException", "DirectoryNotFoundException",
        "DivideByZeroException", "OverflowException", "CsException",
    };

    public TsMemberMappingTable(string? typeMappingPath, string? memberMappingPath, string? javaTypeMappingPath = null)
    {
        _bclExceptionNames = new HashSet<string>(BuiltinBclExceptionNames, StringComparer.Ordinal);
        LoadTypeMappings(typeMappingPath);
        LoadMemberMappings(memberMappingPath);
        LoadBclExceptionNames(javaTypeMappingPath);
    }

    public bool TryMapType(string csharpOrJavaType, out string tsType, out IReadOnlyList<string> imports)
    {
        // ① 异常名（Task 21）：IR 为 Java 风味异常名（IllegalStateException 等），
        //    映射到兼容库异常类（spec §4.3.9）。类型位置（字段/参数/返回值/局部变量）
        //    统一走此入口；未映射 BCL 异常降级 CsException（不附带诊断注释，
        //    需要注释的 new/instanceof/catch 位置走 TryMapException）。
        if (_bclExceptionNames.Contains(csharpOrJavaType))
        {
            tsType = ResolveCompatExceptionName(csharpOrJavaType);
            imports = new[] { "@csharptotypescript/compat" };
            return true;
        }
        // ① 内置 Java 风味类型表（IR 中实际出现的类型名，如 int/String）
        if (BuiltinJavaTypeMappings.TryGetValue(csharpOrJavaType, out var javaMapped))
        {
            tsType = javaMapped;
            imports = System.Array.Empty<string>();
            return true;
        }
        // ② 配置文件（C# 全名，如 System.Int32）
        if (_typeMappings.TryGetValue(csharpOrJavaType, out var entry))
        {
            tsType = entry.TsType;
            imports = entry.Imports;
            return true;
        }
        // ③ 数组后缀递归映射（int[] → number[]、String[] → string[]、int[][] → number[][]）：
        //    内置/配置表均不收录数组键，兜底返回原值前先映射元素类型并回拼 []；
        //    元素未命中时维持原行为（返回原值 + false），不破坏 ①② 查找顺序。
        if (csharpOrJavaType.EndsWith("[]", StringComparison.Ordinal)
            && TryMapType(csharpOrJavaType[..^2], out var elementTs, out var elementImports))
        {
            tsType = elementTs + "[]";
            imports = elementImports;
            return true;
        }
        tsType = csharpOrJavaType;
        imports = System.Array.Empty<string>();
        return false;
    }

    /// <summary>
    /// Java 风味异常名 → TS 兼容库异常名（Task 21，spec §4.3.9）。
    /// 返回 false = 非已知 BCL 异常（用户定义异常/普通类型），调用方原样发射。
    /// isFallback = true 表示无精确兼容类，降级 CsException（调用方应追加
    /// `/* cs2j: unmapped exception */` 诊断注释，见计划 Task 21“未映射异常”）。
    /// </summary>
    public bool TryMapException(string javaOrCsharpName, out string tsType, out IReadOnlyList<string> imports, out bool isFallback)
    {
        tsType = javaOrCsharpName;
        imports = System.Array.Empty<string>();
        isFallback = false;
        if (!_bclExceptionNames.Contains(javaOrCsharpName))
        {
            return false;
        }
        tsType = ResolveCompatExceptionName(javaOrCsharpName);
        // 仅“已知 BCL 异常但无精确兼容类”分支为降级（isFallback）；
        // 覆盖表（RuntimeException→CsException 等）与恒等兼容类均非降级。
        isFallback = !JavaExceptionOverrides.ContainsKey(javaOrCsharpName)
            && !CompatExceptionNames.Contains(javaOrCsharpName);
        imports = new[] { "@csharptotypescript/compat" };
        return true;
    }

    private static string ResolveCompatExceptionName(string javaOrCsharpName)
    {
        if (JavaExceptionOverrides.TryGetValue(javaOrCsharpName, out var overridden))
        {
            return overridden;
        }
        if (CompatExceptionNames.Contains(javaOrCsharpName))
        {
            return javaOrCsharpName;
        }
        return "CsException";
    }

    public bool TryMapMethod(string ownerType, string methodName, out string tsMethod, out IReadOnlyList<string> imports)
    {
        imports = System.Array.Empty<string>();
        // ① 内置表
        if (BuiltinMethodMappings.TryGetValue((ownerType, methodName), out var builtin))
        {
            tsMethod = builtin;
            return true;
        }
        // ② 配置表
        if (_methodMappings.TryGetValue((ownerType, methodName), out var entry))
        {
            tsMethod = entry.TsMethod;
            imports = entry.Imports;
            return true;
        }
        tsMethod = methodName;
        return false;
    }

    public bool TryMapMember(string ownerType, string memberName, out string tsMember)
    {
        // ① 内置表
        if (BuiltinMemberMappings.TryGetValue((ownerType, memberName), out var builtin))
        {
            tsMember = builtin;
            return true;
        }
        // ② 配置表（属性类成员映射复用 methodMappings，signature == "property"）
        if (_memberMappings.TryGetValue((ownerType, memberName), out var configured))
        {
            tsMember = configured;
            return true;
        }
        tsMember = memberName;
        return false;
    }

    private void LoadTypeMappings(string? path)
    {
        // 文件缺失保持静默（null 或不存在 = 纯内置表模式）。
        if (path == null || !File.Exists(path)) return;
        try
        {
            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize<TypeMappingFile>(json, JsonOptions);
            if (file?.TypeMappings == null) return;
            foreach (var item in file.TypeMappings)
            {
                if (item.Csharp == null || item.Typescript == null)
                {
                    throw new InvalidOperationException($"Invalid TypeScript mapping config: {path}");
                }
                _typeMappings[item.Csharp] = (item.Typescript, item.Imports ?? new List<string>());
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid TypeScript mapping config: {path}", ex);
        }
    }

    private void LoadMemberMappings(string? path)
    {
        // 文件缺失保持静默（null 或不存在 = 纯内置表模式）。
        if (path == null || !File.Exists(path)) return;
        try
        {
            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize<MemberMappingFile>(json, JsonOptions);
            if (file?.MethodMappings == null) return;
            foreach (var item in file.MethodMappings)
            {
                if (item.Type == null || item.Method == null || item.TsMethod == null)
                {
                    throw new InvalidOperationException($"Invalid TypeScript mapping config: {path}");
                }
                if (item.Signature == "property")
                {
                    _memberMappings[(item.Type, item.Method)] = item.TsMethod;
                }
                else
                {
                    _methodMappings[(item.Type, item.Method)] = (item.TsMethod, item.Imports ?? new List<string>());
                }
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid TypeScript mapping config: {path}", ex);
        }
    }

    /// <summary>
    /// 从 Java TypeMappings.json 全量提取 BCL 异常名（csharp/java 两侧简单名，
    /// 以 Exception/Error 结尾的条目），与内置高频集合合并为完整反向映射键集。
    /// 配置缺失或解析失败保持静默（纯内置表模式）。
    /// </summary>
    private void LoadBclExceptionNames(string? path)
    {
        if (path == null || !File.Exists(path)) return;
        try
        {
            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize<JavaTypeMappingFile>(json, JsonOptions);
            if (file?.TypeMappings == null) return;
            foreach (var item in file.TypeMappings)
            {
                if (item.Csharp == null || item.Java == null) continue;
                var csharpSimple = SimpleName(item.Csharp);
                var javaSimple = SimpleName(item.Java);
                if (IsExceptionName(csharpSimple) || IsExceptionName(javaSimple))
                {
                    _bclExceptionNames.Add(csharpSimple);
                    _bclExceptionNames.Add(javaSimple);
                }
            }
        }
        catch (JsonException)
        {
            // 配置解析失败不阻断发射（回退内置集；TS 配置表加载失败仍走独立抛错路径）
        }
    }

    private static string SimpleName(string fullName)
    {
        var idx = fullName.LastIndexOf('.');
        return idx >= 0 ? fullName[(idx + 1)..] : fullName;
    }

    private static bool IsExceptionName(string simpleName)
    {
        return simpleName.EndsWith("Exception", StringComparison.Ordinal)
            || simpleName.EndsWith("Error", StringComparison.Ordinal);
    }

    private sealed class TypeMappingFile
    {
        public List<TypeMappingItem>? TypeMappings { get; set; }
    }

    private sealed class TypeMappingItem
    {
        public string? Csharp { get; set; }
        public string? Typescript { get; set; }
        public List<string>? Imports { get; set; }
    }

    private sealed class MemberMappingFile
    {
        public List<MemberMappingItem>? MethodMappings { get; set; }
    }

    private sealed class MemberMappingItem
    {
        public string? Type { get; set; }
        public string? Method { get; set; }
        public string? TsMethod { get; set; }
        public string? Signature { get; set; }
        public List<string>? Imports { get; set; }
    }

    private sealed class JavaTypeMappingFile
    {
        public List<JavaTypeMappingItem>? TypeMappings { get; set; }
    }

    private sealed class JavaTypeMappingItem
    {
        public string? Csharp { get; set; }
        public string? Java { get; set; }
        public List<string>? Imports { get; set; }
    }
}
