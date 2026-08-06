using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CSharpToJava.Core.TypeScript;

/// <summary>
/// Java→TypeScript 二次映射表。
/// 解析顺序（发射期，spec §3.1.2）：
/// ① 内置表（代码，覆盖最高频项：size→length、add→push 等）
/// ② MemberMappings.json（配置，可扩展）
/// ③ TypeMappings.json 的 csharp→typescript 直查（用于类型注解）
/// ④ 未命中 -> 调用方输出 /* cs2j: unmapped */ 诊断注释
/// </summary>
public sealed class TsMemberMappingTable
{
    private readonly Dictionary<string, (string TsType, List<string> Imports)> _typeMappings = new();
    private readonly Dictionary<(string Type, string Method), (string TsMethod, List<string> Imports)> _methodMappings = new();
    private readonly Dictionary<(string Type, string Member), string> _memberMappings = new();

    // 内置成员映射（最高频项，spec §3.1.2 ①）
    // IR 中已是 Java 风味，这里覆盖 Java 化 API 调用。
    // 注意：只收录“方法名→方法名”可直接替换的项；需要参数感知/语句级改写的
    // （如 remove(int) 返回值语义、equalsIgnoreCase 两侧调用、Clear → length = 0）
    // 不放进本表，由发射器特殊分支或 M3 rewriter 处理。
    private static readonly Dictionary<(string Type, string Method), string> BuiltinMethodMappings = new()
    {
        { ("java.util.List", "size"), "length" },
        { ("java.util.Collection", "size"), "length" },
        { ("java.lang.String", "length"), "length" },
        { ("java.util.List", "add"), "push" },
        { ("java.util.ArrayList", "add"), "push" },
        { ("java.util.List", "isEmpty"), "length === 0" },
        { ("java.util.Collection", "isEmpty"), "length === 0" },
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
        { ("java.lang.String", "equals"), "===" },
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
        // ① 配置文件（C# 全名，如 System.Int32）
        if (_typeMappings.TryGetValue(csharpOrJavaType, out var entry))
        {
            tsType = entry.TsType;
            imports = entry.Imports;
            return true;
        }
        // ② 内置 Java 风味类型表（IR 中实际出现的类型名，如 int/String）
        if (BuiltinJavaTypeMappings.TryGetValue(csharpOrJavaType, out var javaMapped))
        {
            tsType = javaMapped;
            imports = System.Array.Empty<string>();
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
        if (path == null || !File.Exists(path)) return;
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("typeMappings", out var arr)) return;
        foreach (var item in arr.EnumerateArray())
        {
            var csharp = item.GetProperty("csharp").GetString()!;
            var ts = item.GetProperty("typescript").GetString()!;
            var imports = new List<string>();
            if (item.TryGetProperty("imports", out var impArr))
            {
                foreach (var imp in impArr.EnumerateArray())
                {
                    imports.Add(imp.GetString()!);
                }
            }
            _typeMappings[csharp] = (ts, imports);
        }
    }

    private void LoadMemberMappings(string? path)
    {
        if (path == null || !File.Exists(path)) return;
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("methodMappings", out var arr)) return;
        foreach (var item in arr.EnumerateArray())
        {
            var type = item.GetProperty("type").GetString()!;
            var method = item.GetProperty("method").GetString()!;
            var tsMethod = item.GetProperty("tsMethod").GetString()!;
            var signature = item.TryGetProperty("signature", out var sig) ? sig.GetString() ?? "" : "";

            var imports = new List<string>();
            if (item.TryGetProperty("imports", out var impArr))
            {
                foreach (var imp in impArr.EnumerateArray())
                {
                    imports.Add(imp.GetString()!);
                }
            }

            if (signature == "property")
            {
                _memberMappings[(type, method)] = tsMethod;
            }
            else
            {
                _methodMappings[(type, method)] = (tsMethod, imports);
            }
        }
    }
}