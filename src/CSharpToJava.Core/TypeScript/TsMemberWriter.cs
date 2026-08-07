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

    /// <summary>
    /// Java→TS 类型映射转发（类/接口成员发射共用，保持映射一致）。
    /// </summary>
    public string MapType(string javaOrCsharpType) => _exprWriter.MapType(javaOrCsharpType);

    /// <summary>
    /// 非可空上下文映射（Task 20）：泛型实参/继承约束等位置的装箱名
    /// （Java 泛型装箱产物）不追加 `| null`，用于 MapHeritageType/FormatTypeParameters。
    /// </summary>
    public string MapTypeNonNull(string javaOrCsharpType) => _exprWriter.MapTypeNonNull(javaOrCsharpType);

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
        var hasInitializer = !string.IsNullOrEmpty(field.Initializer);

        // 无初始化器补默认零值（spec §2.5：tsc strictPropertyInitialization 要求）。
        // strictNullChecks 下 null 不可赋给非空类型（TS2322）：实际发射 `= null` 时
        // 类型放宽为 `T | null`（含无初始化器默认零值为 null、以及初始化器为 null
        // 字面量两种形态；Task 13 扩展后者使 `int? x = null` → `number | null` 成立，
        // 与既有约定一致；可空体系深度语义属 M2 Task 20）。
        var emitsNull = hasInitializer
            ? string.Equals(field.Initializer, "null", StringComparison.Ordinal)
            : DefaultValueForType(tsType) == "null";
        var emittedType = tsType;
        if (emitsNull
            && !tsType.Contains("| null") && !tsType.Contains("null |")
            && tsType is not ("null" or "any" or "undefined" or "void"))
        {
            // 函数类型（委托）后缀 | null 会并入返回类型（(a) => string | null），
            // 外层并集需显式括号 ((a) => string) | null（Task 27 委托）
            emittedType = tsType.Contains("=>") ? $"({tsType}) | null" : $"{tsType} | null";
        }
        sb.Append($"{name}: {emittedType}");

        if (hasInitializer)
        {
            // 初始化器为 Java 风味字面量（FieldTransformer.RenderConstantValue 产出
            // 100L/1.5f/0xFFL 等），需归一化为合法 TS 字面量再发射
            sb.Append($" = {NormalizeLiteral(field.Initializer)}");
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

        var name = TsNaming.SafeIdentifier(method.Name);
        sb.Append($"{name}{TsTypeWriter.FormatTypeParameters(method.TypeParameters, MapType)}(");

        // 参数/返回值类型需经 Java→TS 映射
        var parms = method.Parameters.Select(p =>
        {
            var pName = TsNaming.SafeIdentifier(p.Name);
            return $"{pName}: {_exprWriter.MapType(p.Type)}";
        });
        sb.Append(string.Join(", ", parms));

        sb.Append($"): {_exprWriter.MapType(method.ReturnType)}");

        AppendMethodBody(sb, method, ind, indentLevel);

        return sb.ToString();
    }

    /// <summary>
    /// 属性 getter 访问器发射（Task 16）：`get X(): T { ... }`。可见性 public 省略
    /// （TS 类成员默认），private/protected/static 保留；方法体复用 EmitMethod 三路发射。
    /// </summary>
    public string EmitPropertyAccessorGetter(JavaMethodDeclaration getter, string propertyName, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder(ind);

        if ((getter.Modifiers & JavaModifiers.Private) != 0) sb.Append("private ");
        else if ((getter.Modifiers & JavaModifiers.Protected) != 0) sb.Append("protected ");
        if ((getter.Modifiers & JavaModifiers.Static) != 0) sb.Append("static ");
        if ((getter.Modifiers & JavaModifiers.Abstract) != 0) sb.Append("abstract ");

        sb.Append($"get {TsNaming.SafeIdentifier(propertyName)}(): {_exprWriter.MapType(getter.ReturnType)}");
        AppendMethodBody(sb, getter, ind, indentLevel);
        return sb.ToString();
    }

    /// <summary>
    /// 属性 setter 访问器发射（Task 16）：`private set X(value: T) { ... }`。
    /// </summary>
    public string EmitPropertyAccessorSetter(JavaMethodDeclaration setter, string propertyName, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder(ind);

        if ((setter.Modifiers & JavaModifiers.Private) != 0) sb.Append("private ");
        else if ((setter.Modifiers & JavaModifiers.Protected) != 0) sb.Append("protected ");
        if ((setter.Modifiers & JavaModifiers.Static) != 0) sb.Append("static ");
        if ((setter.Modifiers & JavaModifiers.Abstract) != 0) sb.Append("abstract ");

        sb.Append($"set {TsNaming.SafeIdentifier(propertyName)}(");
        var parms = setter.Parameters.Select(p => $"{TsNaming.SafeIdentifier(p.Name)}: {_exprWriter.MapType(p.Type)}");
        sb.Append(string.Join(", ", parms));
        // TS setter 不允许返回类型注解（tsc TS1095），只输出参数列表
        sb.Append(')');
        AppendMethodBody(sb, setter, ind, indentLevel);
        return sb.ToString();
    }

    /// <summary>
    /// 方法体发射（结构化体 > 表达式体 > Body 字符串），EmitMethod 与访问器发射共用。
    /// </summary>
    private void AppendMethodBody(StringBuilder sb, JavaMethodDeclaration method, string ind, int indentLevel)
    {
        if ((method.Modifiers & JavaModifiers.Abstract) != 0
            || (method.Body == null && method.StructuredBody == null))
        {
            sb.Append(';');
            return;
        }
        if (method.StructuredBody != null)
        {
            // 结构化方法体优先于 Body 字符串（JavaMethodDeclaration 契约）：
            // 逐语句交给 TsStatementWriter 发射，避免丢失 body
            sb.AppendLine(" {");
            foreach (var stmt in method.StructuredBody.Statements)
            {
                sb.AppendLine(_stmtWriter.Emit(stmt, indentLevel + 2));
            }
            sb.Append(ind).Append(_indent).Append('}');
            return;
        }
        if (method.IsBodyExpression)
        {
            // 表达式体成员（方法/属性 getter/索引器/运算符）：类成员体不支持 => 语法
            // （TS1144），转块体。对齐 JavaMemberDeclaration 语义：非 void 加 return，
            // void 表达式即语句（无 return）。
            var isVoid = _exprWriter.MapType(method.ReturnType) == "void";
            sb.AppendLine(" {");
            sb.Append(ind).Append(_indent);
            if (!isVoid) sb.Append("return ");
            sb.AppendLine($"{method.Body};");
            sb.Append(ind).Append('}');
            return;
        }
        sb.AppendLine(" {");
        // body 已是字符串，按行缩进
        var bodyLines = method.Body!.Split('\n');
        foreach (var line in bodyLines)
        {
            sb.Append(ind).Append(_indent).AppendLine(line);
        }
        sb.Append(ind).Append('}');
    }

    public string EmitConstructor(JavaConstructorDeclaration ctor, string? extendedType, int indentLevel)
    {
        var ind = new string(_indent[0], indentLevel * _indent.Length);
        var sb = new StringBuilder(ind);

        // TS 支持 private/protected constructor（I4）；public 为默认可省略
        if ((ctor.Modifiers & JavaModifiers.Private) != 0) sb.Append("private ");
        else if ((ctor.Modifiers & JavaModifiers.Protected) != 0) sb.Append("protected ");

        sb.Append("constructor(");
        var parms = ctor.Parameters.Select(p => $"{TsNaming.SafeIdentifier(p.Name)}: {_exprWriter.MapType(p.Type)}");
        sb.Append(string.Join(", ", parms));
        sb.Append(')');

        sb.AppendLine(" {");

        // 基类构造调用：IR 中为 Java 风味字符串（实际属性名 Initializer，非 BaseConstructorCall），
        // 如 "super(a, b)"。TS 要求派生类构造器必须显式调用 super()（TS2377/TS17009）：
        // 类有 ExtendedType 时无条件发射 super(...)（有实参原样，无实参 super();）。
        // "this(...)" 重载委托无 TS 对应 → 诊断注释（重载构造器在 M3 转工厂）。
        if (!string.IsNullOrEmpty(ctor.Initializer))
        {
            if (ctor.Initializer.StartsWith("super("))
            {
                sb.Append(ind).Append(_indent).AppendLine($"{ctor.Initializer};");
            }
            else
            {
                // this(...) 委托：先满足基类 super() 要求，再注释说明委托无法直译
                if (!string.IsNullOrEmpty(extendedType))
                {
                    sb.Append(ind).Append(_indent).AppendLine("super();");
                }
                sb.Append(ind).Append(_indent).AppendLine($"/* cs2j: unsupported ctor delegation: {ctor.Initializer} */");
            }
        }
        else if (!string.IsNullOrEmpty(extendedType))
        {
            sb.Append(ind).Append(_indent).AppendLine("super();");
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

    /// <summary>
    /// 将 Java 风味字面量归一化为合法 TS 字面量（I2，等价于 TsExpressionWriter.EmitLiteral 的
    /// 字符串版）：long/ulong 后缀 L → bigint 后缀 n（100L→100n、0xFFL→0xFFn、1_000L→1_000n）；
    /// float/double 后缀 f/d → 去掉（1.5f→1.5）；char 字面量 'a' → "a"。
    /// 仅当整个字符串为字面量形态时才转换，避免误伤表达式/标识符。
    /// </summary>
    private static string NormalizeLiteral(string literal)
    {
        // Task 19 注记⑦：Java 风味数值 cast 前缀（(long)/(int)/(short)/(byte)/(double)/(float)）
        // const long = uint.MaxValue 等初始化经共享适配层包成 "(long) (4294967295L)"
        // 形态，TS 无 cast 语法，需剥除前缀并把 L 后缀归一化为 n
        // （递归处理嵌套/括号包裹形态；仅字面量整体匹配才转换）
        var stripped = StripNumericCastPrefix(literal);
        if (stripped != literal)
        {
            return NormalizeLiteral(stripped);
        }

        if (literal.Length >= 3 && literal[0] == '\'' && literal[^1] == '\'' && !literal[1..^1].Contains('"'))
        {
            return "\"" + literal[1..^1] + "\"";
        }

        if (literal.EndsWith("L", StringComparison.Ordinal) && IsNumericLiteralBody(literal[..^1]))
        {
            return literal[..^1] + "n";
        }

        if (literal.EndsWith("f", StringComparison.OrdinalIgnoreCase) && IsNumericLiteralBody(literal[..^1]))
        {
            return literal[..^1];
        }

        // Task 19 note (7): under the TS target, const initializers such as uint.MaxValue
        // reach this writer as BigInt(<Java literal>)/Number(<Java literal>) because the
        // shared adapt layer wraps them (AdaptExpressionToConvertedType TS branch). The
        // inner literal still carries Java suffixes, so normalize it: BigInt(4294967295L)
        // -> 4294967295n, Number(1.5f) -> 1.5, BigInt(5) -> 5n.
        if (literal.StartsWith("BigInt(", StringComparison.Ordinal) && literal.EndsWith(")", StringComparison.Ordinal))
        {
            var inner = literal["BigInt(".Length..^1].Trim();
            if (inner.EndsWith("L", StringComparison.Ordinal) && IsNumericLiteralBody(inner[..^1]))
            {
                return inner[..^1] + "n";
            }
            if (IsNumericLiteralBody(inner))
            {
                return inner + "n";
            }
            return literal;
        }

        if (literal.StartsWith("Number(", StringComparison.Ordinal) && literal.EndsWith(")", StringComparison.Ordinal))
        {
            var inner = literal["Number(".Length..^1].Trim();
            if ((inner.EndsWith("L", StringComparison.Ordinal) && IsNumericLiteralBody(inner[..^1]))
                || (inner.EndsWith("f", StringComparison.OrdinalIgnoreCase) && IsNumericLiteralBody(inner[..^1])))
            {
                return inner[..^1];
            }
            if (IsNumericLiteralBody(inner))
            {
                return inner;
            }
            return literal;
        }

        return literal;
    }

    /// <summary>剥除 "({type}) (literal)" 或 "({type})literal" 形态的 Java 数值 cast 前缀，
    /// 供 NormalizeLiteral 归一化 const 字段初始化器（TS 无 cast 语法）。</summary>
    private static string StripNumericCastPrefix(string literal)
    {
        var trimmed = literal.TrimStart();
        if (!trimmed.StartsWith('('))
        {
            return literal;
        }
        var closeIdx = trimmed.IndexOf(')');
        if (closeIdx < 2)
        {
            return literal;
        }
        var typeName = trimmed[1..closeIdx];
        if (typeName is not ("long" or "int" or "short" or "byte" or "sbyte" or "double" or "float"))
        {
            return literal;
        }
        var rest = trimmed[(closeIdx + 1)..].Trim();
        if (rest.Length >= 2 && rest[0] == '(' && rest[^1] == ')')
        {
            rest = rest[1..^1];
        }
        if (rest.Length == 0)
        {
            return literal;
        }
        if (rest.EndsWith("L", StringComparison.Ordinal) && IsNumericLiteralBody(rest[..^1]))
        {
            return rest;
        }
        if (IsNumericLiteralBody(rest))
        {
            return rest;
        }
        return literal;
    }

    private static bool IsNumericLiteralBody(string body)
    {
        if (body.Length == 0) return false;

        // 十六进制（C# uint/ulong 常量渲染为 0x... / 0x...L），可含下划线
        if (body.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return body.Length > 2 && body[2..].All(c => Uri.IsHexDigit(c) || c == '_');
        }

        // 十进制整数/浮点，可含下划线、小数点、指数（100、1_000、1.5、1e5、-100）
        var start = body[0] is '+' or '-' ? 1 : 0;
        var anyDigit = false;
        var anyFraction = false;
        var anyExponent = false;
        for (var i = start; i < body.Length; i++)
        {
            var c = body[i];
            if (char.IsAsciiDigit(c)) { anyDigit = true; continue; }
            if (c == '_') continue;
            if (c == '.' && !anyFraction && !anyExponent) { anyFraction = true; continue; }
            if ((c == 'e' || c == 'E') && anyDigit && !anyExponent) { anyExponent = true; continue; }
            if ((c == '+' || c == '-') && anyExponent && i > start) { continue; }
            return false;
        }
        return anyDigit;
    }
}
