using System.Text;
using System.Text.RegularExpressions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.TypeScript.Rewriters;

/// <summary>
/// struct 发射 TS 化 rewriter（Task 27，M3）。C#→Java 共享管线为 struct 合成 Java
/// 专属支撑（implements Cloneable、clone()、equals(Object)/hashCode() 合成），TS
/// 目标下这些形态非法或语义偏离，统一在此收尾（Java 路径不进入本 pass）：
/// ① implements Cloneable → 移除（TS 无对应接口；值拷贝辅助 cloneStruct 归 M4）；
/// ② 合成 clone() 方法体：Java 局部声明 "Vector copy = new Vector();" → "const copy
///    = new Vector();"，new X&lt;&gt;() 菱形 → new X()（TS 泛型推断，<> 非法）；
/// ③ 合成 equals(Object o)（委托 valueEquals）→ 合法 TS：this === o / instanceof
///    无绑定 + const other = o as X / valueEquals 调用（Java 16 instanceof 模式为
///    非法 TS）；仅命中合成形态（this == o 开头 + instanceof X other 绑定 +
///    X.valueEquals 委托）时改写，用户自写 equals 直通（is 模式由模式匹配子任务处理）；
/// ④ 合成 hashCode()（Objects.hash(字段)）→ 字段哈希表达式：CsObject.getHashCode(字段)
///    折叠 * 31 + 后 | 0（spec §4.3.2：原始类型稳定哈希、对象调实例 GetHashCode——
///    对字段求值不递归自身），登记 compat 符号 CsObject 生成导入。
/// </summary>
public sealed class TsStructRewriter : JavaSyntaxRewriter
{
    private readonly ConversionContext? _context;

    public TsStructRewriter(ConversionContext? context = null)
    {
        _context = context;
    }

    public override JavaClassDeclaration VisitClassDeclaration(JavaClassDeclaration node)
    {
        var visited = (JavaClassDeclaration)base.VisitClassDeclaration(node);
        if (!visited.IsConvertedFromStruct)
        {
            return visited;
        }

        // ① implements Cloneable 无 TS 对应（其余 implements 由发射器 MapHeritageType 处置）
        visited.ImplementedTypes.RemoveAll(t => t == "Cloneable");

        foreach (var method in visited.Methods)
        {
            if (method.Name == "clone" && method.Parameters.Count == 0)
            {
                method.Body = RewriteCloneBody(method.Body);
            }
            else if (method.Name == "equals" && method.Parameters.Count == 1)
            {
                method.Body = RewriteEqualsBody(method.Body, visited.Name);
            }
            else if (method.Name == "hashCode" && method.Parameters.Count == 0)
            {
                method.Body = RewriteHashCodeBody(method.Body, visited);
            }
            else if (method.Name == "toString" && method.Parameters.Count == 0)
            {
                // 合成 toString() 体裸字段引用（Java 合法、TS 非法：模板字符串/拼接中
                // 裸字段名 TS2663）→ this. 前缀；仅命中合成形态（return "ClassName{ 开头，
                // 无局部变量遮蔽）时改写，用户自写 toString 直通。
                method.Body = RewriteToStringBody(method.Body, visited);
            }
        }

        return visited;
    }

    /// <summary>
    /// 合成 clone() 方法体：Java 类型声明 "Vector copy = new Vector();" → "const copy =
    /// new Vector();"；new Vector&lt;&gt;() 菱形 → new Vector()。其余形态（readonly 组合
    /// 构造拷贝 return new X(f1, f2);、空 struct return new X();）本身即合法 TS，直通。
    /// </summary>
    private static string? RewriteCloneBody(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return body;
        }
        var rewritten = Regex.Replace(
            body,
            @"\b([A-Za-z_$][\w$]*(?:<[^>\r\n]+>)?)\s+copy\s*=\s*new\s+([A-Za-z_$][\w$]*)(?:<>)?\s*\(([^()\r\n]*)\);",
            "const copy = new $2($3);");
        rewritten = Regex.Replace(rewritten, @"new\s+([A-Za-z_$][\w$]*)<>", "new $1");
        return rewritten;
    }

    /// <summary>
    /// 合成 equals(Object) 方法体 → 合法 TS。仅当命中合成形态时改写：以 "if (this == o)
    /// return true;" 开头、含 "instanceof {X} other" 绑定、含 "{X}.valueEquals(this,
    /// other)" 委托。用户自写 equals 直通（其 is 模式/强转由模式匹配子任务处理）。
    /// </summary>
    private static string? RewriteEqualsBody(string? body, string className)
    {
        if (string.IsNullOrEmpty(body))
        {
            return body;
        }
        if (!body.StartsWith("if (this == o)", StringComparison.Ordinal)
            || !body.Contains($"instanceof {className} other", StringComparison.Ordinal)
            || !body.Contains($"{className}.valueEquals(this, other)", StringComparison.Ordinal))
        {
            return body;
        }
        var sb = new StringBuilder();
        sb.Append("if (this === o) return true;").Append('\n');
        sb.Append("        if (!(o instanceof ").Append(className).Append(")) return false;").Append('\n');
        sb.Append("        const other = o as ").Append(className).Append(';').Append('\n');
        sb.Append("        return ").Append(className).Append(".valueEquals(this, other);");
        return sb.ToString();
    }

    /// <summary>
    /// 合成 toString() 方法体：跳过字符串字面量区域，把裸实例字段标识符替换为
    /// this.字段（TS 无 Java 的隐式 this 字段访问）。仅命中合成形态
    /// "return \"ClassName{" 开头时改写。
    /// </summary>
    private static string? RewriteToStringBody(string? body, JavaClassDeclaration owner)
    {
        if (string.IsNullOrEmpty(body))
        {
            return body;
        }
        var instanceFields = owner.Fields
            .Where(f => (f.Modifiers & JavaModifiers.Static) == 0)
            .Select(f => f.Name)
            .Where(f => f.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        if (instanceFields.Count == 0)
        {
            return body;
        }
        // 形态 A：Java 拼接体 "return \"Vector{" + "X=" + X + ... + "}";"（TsStringCallRewriter
        // 通常已转为模板字符串，此处兜底保留）。
        if (body.StartsWith("return \"" + owner.Name + "{", StringComparison.Ordinal))
        {
            var sb = new StringBuilder(body.Length);
            var i = 0;
            while (i < body.Length)
            {
                var ch = body[i];
                if (ch == '"')
                {
                    var start = i;
                    i++;
                    while (i < body.Length && body[i] != '"') i++;
                    if (i < body.Length) i++;
                    sb.Append(body, start, i - start);
                    continue;
                }
                if (char.IsLetter(ch) || ch == '_' || ch == '$')
                {
                    var start = i;
                    while (i < body.Length && (char.IsLetterOrDigit(body[i]) || body[i] == '_' || body[i] == '$')) i++;
                    var word = body.Substring(start, i - start);
                    if (instanceFields.Contains(word))
                    {
                        sb.Append("this.").Append(word);
                    }
                    else
                    {
                        sb.Append(body, start, i - start);
                    }
                    continue;
                }
                sb.Append(ch);
                i++;
            }
            return sb.ToString();
        }
        // 形态 B：模板字符串体 "return `Vector{X=${X}}`;"（TsStringCallRewriter 转换后）：
        // 插值段 ${字段} → ${this.字段}（TS 模板插值中裸字段名非法）。
        if (body.StartsWith("return `", StringComparison.Ordinal))
        {
            return Regex.Replace(
                body,
                @"\$\{([A-Za-z_$][\w$]*)\}",
                match =>
                {
                    var field = match.Groups[1].Value;
                    return instanceFields.Contains(field)
                        ? "${this." + field + "}"
                        : match.Value;
                });
        }
        return body;
    }

    /// <summary>
    /// 合成 hashCode()（Objects.hash(字段)）→ CsObject.getHashCode(字段) 折叠哈希。
    /// 非 Objects.hash 形态（用户自写）直通。
    /// </summary>
    private string? RewriteHashCodeBody(string? body, JavaClassDeclaration owner)
    {
        if (string.IsNullOrEmpty(body))
        {
            return body;
        }
        var m = Regex.Match(body, @"Objects\.hash\(([^)]*)\)");
        if (!m.Success)
        {
            return body;
        }
        var fields = m.Groups[1].Value
            .Split(',')
            .Select(f => f.Trim())
            .Where(f => f.Length > 0)
            .ToList();
        if (fields.Count == 0)
        {
            return body;
        }
        // 裸字段引用（Java 合法、TS 非法）→ this. 前缀（合成体无局部变量遮蔽）
        var parts = fields.Select(f => $"CsObject.getHashCode(this.{f})").ToList();
        var expr = parts[0];
        for (int i = 1; i < parts.Count; i++)
        {
            expr = $"({expr} * 31 + {parts[i]})";
        }
        if (_context != null)
        {
            _context.TsCompatSymbols.Add("CsObject");
        }
        return $"return ({expr}) | 0;";
    }
}