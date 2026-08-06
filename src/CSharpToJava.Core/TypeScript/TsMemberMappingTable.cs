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
    };

    // 内置属性/字段映射
    private static readonly Dictionary<(string Type, string Member), string> BuiltinMemberMappings = new()
    {
        { ("java.util.List", "size"), "length" },
        { ("java.util.Collection", "size"), "length" },
        { ("java.lang.String", "length"), "length" },
        { ("java.util.ArrayList", "size"), "length" },
    };

    public TsMemberMappingTable(string? typeMappingPath, string? memberMappingPath)
    {
        LoadTypeMappings(typeMappingPath);
        LoadMemberMappings(memberMappingPath);
    }

    public bool TryMapType(string csharpOrJavaType, out string tsType, out IReadOnlyList<string> imports)
    {
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
        tsType = csharpOrJavaType;
        imports = System.Array.Empty<string>();
        return false;
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
}