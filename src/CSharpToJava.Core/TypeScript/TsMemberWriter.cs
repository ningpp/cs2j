using System.Text;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.TypeScript;

/// <summary>
/// 成员发射器：字段/方法/构造器/属性发射为 TS（spec §2.5 成员部分）。
/// </summary>
public sealed class TsMemberWriter
{
    private readonly TsStatementWriter _stmtWriter;
    private readonly TsExpressionWriter _exprWriter;   // 用于 Java→TS 类型映射
    private readonly string _indent;

    public TsMemberWriter(TsStatementWriter stmtWriter, TsExpressionWriter exprWriter, string indent = "    ")
    {
        _stmtWriter = stmtWriter;
        _exprWriter = exprWriter;
        _indent = indent;
    }

    public string EmitField(JavaFieldDeclaration field, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder(ind);

        // 修饰符
        if ((field.Modifiers & JavaModifiers.Public) != 0) sb.Append("public ");
        else if ((field.Modifiers & JavaModifiers.Protected) != 0) sb.Append("protected ");
        else if ((field.Modifiers & JavaModifiers.Private) != 0) sb.Append("private ");
        if ((field.Modifiers & JavaModifiers.Static) != 0) sb.Append("static ");
        if ((field.Modifiers & JavaModifiers.Final) != 0) sb.Append("readonly ");

        // IR 字段类型为 Java 风味（int/String/...），先经映射转 TS
        var tsType = _exprWriter.MapType(field.Type);
        var name = TsNaming.SafeIdentifier(field.Name);
        sb.Append($"{name}: {tsType}");

        // 初始化器：有则用，无则补默认零值（spec §2.5：tsc strictPropertyInitialization 要求）
        if (!string.IsNullOrEmpty(field.Initializer))
        {
            sb.Append($" = {field.Initializer}");
        }
        else
        {
            sb.Append($" = {DefaultValueForType(tsType)}");
        }

        sb.Append(';');
        return sb.ToString();
    }

    public string EmitMethod(JavaMethodDeclaration method, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder(ind);

        if ((method.Modifiers & JavaModifiers.Public) != 0) sb.Append("public ");
        else if ((method.Modifiers & JavaModifiers.Protected) != 0) sb.Append("protected ");
        else if ((method.Modifiers & JavaModifiers.Private) != 0) sb.Append("private ");
        if ((method.Modifiers & JavaModifiers.Static) != 0) sb.Append("static ");
        if ((method.Modifiers & JavaModifiers.Abstract) != 0) sb.Append("abstract ");

        // 类型参数
        if (method.TypeParameters.Count > 0)
        {
            sb.Append('<');
            sb.Append(string.Join(", ", method.TypeParameters.Select(tp => tp.ToString())));
            sb.Append('>');
        }

        var name = TsNaming.SafeIdentifier(method.Name);
        sb.Append($"{name}(");

        // 参数/返回值类型需经 Java→TS 映射
        var parms = method.Parameters.Select(p =>
        {
            var pName = TsNaming.SafeIdentifier(p.Name);
            return $"{pName}: {_exprWriter.MapType(p.Type)}";
        });
        sb.Append(string.Join(", ", parms));

        sb.Append($"): {_exprWriter.MapType(method.ReturnType)}");

        if ((method.Modifiers & JavaModifiers.Abstract) != 0
            || (method.Body == null && method.StructuredBody == null))
        {
            sb.Append(';');
        }
        else if (method.StructuredBody != null)
        {
            // 结构化方法体优先于 Body 字符串（JavaMethodDeclaration 契约）：
            // 逐语句交给 TsStatementWriter 发射，避免丢失 body
            sb.AppendLine(" {");
            foreach (var stmt in method.StructuredBody.Statements)
            {
                sb.AppendLine(_stmtWriter.Emit(stmt, indentLevel + 2));
            }
            sb.Append(ind).Append(_indent).Append('}');
        }
        else if (method.IsBodyExpression)
        {
            sb.Append($" => {method.Body};");
        }
        else
        {
            sb.AppendLine(" {");
            // body 已是字符串，按行缩进
            var bodyLines = method.Body!.Split('\n');
            foreach (var line in bodyLines)
            {
                sb.Append(ind).Append(_indent).AppendLine(line);
            }
            sb.Append(ind).Append('}');
        }

        return sb.ToString();
    }

    public string EmitConstructor(JavaConstructorDeclaration ctor, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder(ind);

        // TS 构造器不支持可见性修饰符以外的前缀；可见性仅用于诊断参考，不发射
        sb.Append("constructor(");
        var parms = ctor.Parameters.Select(p => $"{TsNaming.SafeIdentifier(p.Name)}: {_exprWriter.MapType(p.Type)}");
        sb.Append(string.Join(", ", parms));
        sb.Append(')');

        sb.AppendLine(" {");

        // 基类构造调用：IR 中为 Java 风味字符串（实际属性名 Initializer，非 BaseConstructorCall），
        // 如 "super(a, b)"。TS 要求 super() 必须是构造体第一条语句；
        // "this(...)" 重载委托无 TS 对应 → 诊断注释（重载构造器在 M3 转工厂）。
        if (!string.IsNullOrEmpty(ctor.Initializer))
        {
            if (ctor.Initializer.StartsWith("super("))
            {
                sb.Append(ind).Append(_indent).AppendLine($"{ctor.Initializer};");
            }
            else
            {
                sb.Append(ind).Append(_indent).AppendLine($"/* cs2j: unsupported ctor delegation: {ctor.Initializer} */");
            }
        }

        if (ctor.StructuredBody != null)
        {
            foreach (var stmt in ctor.StructuredBody.Statements)
            {
                sb.AppendLine(_stmtWriter.Emit(stmt, indentLevel + 1));
            }
        }
        else if (!string.IsNullOrEmpty(ctor.Body))
        {
            var bodyLines = ctor.Body.Split('\n');
            foreach (var line in bodyLines)
            {
                sb.Append(ind).Append(_indent).AppendLine(line);
            }
        }
        sb.Append(ind).Append('}');
        return sb.ToString();
    }

    /// <summary>
    /// 返回 TS 类型的默认零值（spec §2.5 字段默认零值规则）。
    /// </summary>
    public static string DefaultValueForType(string tsType)
    {
        if (string.IsNullOrEmpty(tsType)) return "null";

        // 可空类型 X | null -> null
        if (tsType.Contains("| null") || tsType.Contains("null |")) return "null";

        // 数组 -> []
        if (tsType.EndsWith("[]")) return "[]";

        return tsType switch
        {
            "number" => "0",
            "bigint" => "0n",
            "boolean" => "false",
            "string" => "null",  // C# string 默认 null，不用 ""
            "void" => "undefined",
            "any" => "null",
            "null" => "null",
            "undefined" => "undefined",
            _ => "null",  // 引用类型默认 null
        };
    }
}