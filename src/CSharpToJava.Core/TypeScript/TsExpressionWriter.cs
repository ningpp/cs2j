using System.Text;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.TypeScript;

/// <summary>
/// 表达式发射器：遍历 JavaExpression IR 节点，输出 TS 表达式文本。
/// 应用 TsMemberMappingTable 进行 Java→TS 成员/方法映射（spec §2.5 表达式部分）。
/// </summary>
public sealed class TsExpressionWriter
{
    private readonly TsMemberMappingTable _mappings;
    private readonly TsImportCollector _imports;

    /// <summary>Round 16：类成员上下文（裸实例/静态成员名补 this./类名. 前缀）。</summary>
    public TsMemberContext? MemberContext { get; set; }

    /// <summary>Round 16：当前方法参数/结构化局部遮蔽名（由 TsMemberWriter 按方法设置）。</summary>
    public IReadOnlySet<string>? MethodLocals { get; set; }

    public TsExpressionWriter(TsMemberMappingTable mappings, TsImportCollector imports)
    {
        _mappings = mappings;
        _imports = imports;
    }

    /// <summary>
    /// Raw Java-flavor expression text (e.g. initializer strings extracted by the
    /// transformer) is normalized at emission time like raw statement bodies, so
    /// float suffixes, Java array literals, reference casts and FQCN new-expressions
    /// become valid TypeScript.
    /// </summary>
    private string NormalizeRaw(string code)
        => TsRawStatementNormalizer.Normalize(code, MapType, MapExceptionName, normalizeChars: false, memberContext: MemberContext, extraLocals: MethodLocals);

    public string ToInlineString(JavaExpression expr)
    {
        return expr switch
        {
            JavaLiteralExpression lit => EmitLiteral(lit),
            JavaIdentifierExpression id => TsNaming.SafeIdentifier(id.Name),
            JavaThisExpression thisExpr => thisExpr.IsSuper ? "super" : "this",
            JavaBinaryExpression binary => EmitBinary(binary),
            JavaUnaryExpression unary => EmitUnary(unary),
            JavaConditionalExpression cond => $"{ToInlineString(cond.Condition)} ? {ToInlineString(cond.WhenTrue)} : {ToInlineString(cond.WhenFalse)}",
            JavaCastExpression cast => EmitCast(cast),
            JavaNewExpression newExpr => EmitNew(newExpr),
            JavaMethodCallExpression call => EmitMethodCall(call),
            JavaMemberAccessExpression access => EmitMemberAccess(access),
            JavaLambdaExpression lambda => EmitLambda(lambda),
            JavaArrayAccessExpression arr => $"{ToInlineString(arr.Target)}[{ToInlineString(arr.Index)}]",
            JavaAssignmentExpression assign => $"{ToInlineString(assign.Target)} {assign.Operator} {ToInlineString(assign.Value)}",
            JavaParenthesizedExpression paren => $"({ToInlineString(paren.InnerExpression)})",
            JavaInstanceOfExpression inst => EmitInstanceOf(inst),
            JavaRawExpression raw => NormalizeRaw(raw.Code),
            _ => expr.ToInlineString(),
        };
    }

    /// <summary>
    /// Java 风味类型名 → TS 类型（供语句/成员发射器复用，如变量声明、字段）。
    /// 可空值类型（Task 20）：IR 中整串类型名 Integer/Long/Boolean/…
    /// 仅来自 C# 可空值类型（int?/long?/…），映射后追加 `| null`；
    /// 泛型实参/继承约束等非可空位置的装箱名走 MapTypeNonNull 不宽化。
    /// </summary>
    public string MapType(string javaOrCsharpType)
        => MapTypeCore(javaOrCsharpType, widenNullableBoxed: true);

    /// <summary>
    /// 非可空上下文映射（Task 20）：泛型实参/继承约束等位置的装箱名
    /// （如 IRepo&lt;Integer&gt; 来自 C# IRepo&lt;int&gt;）不追加 `| null`。
    /// </summary>
    public string MapTypeNonNull(string javaOrCsharpType)
        => MapTypeCore(javaOrCsharpType, widenNullableBoxed: false);

    private static readonly System.Collections.Generic.HashSet<string> NullableBoxedTypeNames =
        new(System.StringComparer.Ordinal)
        {
            "Integer", "Long", "Boolean", "Double", "Float", "Character", "Short", "Byte",
        };

    private string MapTypeCore(string javaOrCsharpType, bool widenNullableBoxed)
    {
        var mapped = _mappings.TryMapType(javaOrCsharpType, out var ts, out var imports)
            ? RegisterImports(ts, imports)
            : javaOrCsharpType;
        if (widenNullableBoxed && NullableBoxedTypeNames.Contains(javaOrCsharpType))
        {
            mapped += " | null";
        }
        return mapped;
    }

    /// <summary>
    /// 异常名反向映射（Task 21）：委托 TsMemberMappingTable.TryMapException，
    /// 供语句发射器（catch instanceof）复用。
    /// </summary>
    public bool TryMapException(string javaOrCsharpName, out string tsType, out IReadOnlyList<string> imports, out bool isFallback)
        => _mappings.TryMapException(javaOrCsharpName, out tsType, out imports, out isFallback);

    /// <summary>
    /// 异常名 → 兼容库名的 raw 文本映射回调（Task 21）：非 BCL 异常返回 null（直通）；
    /// 无精确兼容类时返回带 /* cs2j: unmapped exception */ 注释的降级名。
    /// </summary>
    public string? MapExceptionName(string javaOrCsharpName)
    {
        if (!_mappings.TryMapException(javaOrCsharpName, out var tsType, out var imports, out var isFallback))
        {
            return null;
        }
        RegisterImports(tsType, imports);
        return isFallback ? $"{tsType} /* cs2j: unmapped exception */" : tsType;
    }

    /// <summary>
    /// 登记 compat 包具名导入并返回类型名（Task 18 起；Task 21 异常类复用）。
    /// </summary>
    public string RegisterImports(string tsType, IReadOnlyList<string> imports)
    {
        foreach (var imp in imports)
        {
            // compat package symbol import; module path is imp, symbol comes from
            // tsType. Nested compat symbols (e.g. the inner CsKeyValuePair of
            // CSharpList<CsKeyValuePair<...>>) are encoded as Symbol|module because
            // the symbol cannot be derived from the outer tsType shell.
            var sep = imp.IndexOf('|');
            if (sep > 0)
            {
                _imports.AddNamedImport(imp[..sep], imp[(sep + 1)..]);
            }
            else
            {
                _imports.AddNamedImport(tsType.Split('<')[0], imp);
            }
        }
        return tsType;
    }

    private string EmitLiteral(JavaLiteralExpression lit)
    {
        var v = lit.Value;

        // long 字面量：100L -> 100n、0xFFL -> 0xFFn、1_000_000L -> 1_000_000n。
        // IR 中 long 字面量统一 L 后缀，直接按后缀判定，无需 TryParse
        //（TryParse 不识别十六进制/下划线分隔形式，会漏判而输出无效 TS 语法）。
        if (v.EndsWith("L"))
        {
            return v[..^1] + "n";
        }

        // char 字面量：'a' -> "a"；泛化支持转义字符（'\n' -> "\n"）。
        // 边界：内含双引号（如 '\"'）的转义在 M2 字符处理任务中细化。
        // float literals: 1.5f/1.5F -> 1.5 (TS has no f suffix; matches NormalizeLiteral).
        if ((v.EndsWith("f", StringComparison.Ordinal) || v.EndsWith("F", StringComparison.Ordinal))
            && IsFloatLiteralBody(v[..^1]))
        {
            return v[..^1];
        }

        if (v.Length >= 3 && v.StartsWith("'") && v.EndsWith("'"))
        {
            var inner = v[1..^1];
            if (!inner.Contains('"'))
            {
                // 转义去转义：IR 中 '\'' 表示单引号字符，双引号字符串中直接写 ' 即可
                return "\"" + inner.Replace("\\'", "'") + "\"";
            }
        }

        return v;
    }

    /// <summary>float literal body (after removing the f suffix): decimal int/float,
    /// optionally with underscores, fraction and exponent.</summary>
    private static bool IsFloatLiteralBody(string body)
    {
        if (body.Length == 0) return false;
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

    private string EmitBinary(JavaBinaryExpression binary)
    {
        // Task 18：字符串拼接链（含 C# 插值降级的 + 链）→ 模板字符串。仅当链首或
        // 第二段为字符串字面量时整链才是字符串拼接（首个 + 即拼接，C# 与 JS 规则一致），
        // 重建语义等价；x + y + "a"（首个 + 可能为数值相加）与纯表达式链（$"{n}{m}"
        // 降级的 n + m）无类型信息，保守保持 + 拼接（JS 求值本身与 C# 一致）。
        if (binary.Operator == "+" && TryEmitTemplateString(binary, out var template))
        {
            return template;
        }

        var left = ToInlineString(binary.Left);
        var right = ToInlineString(binary.Right);

        // 整数除法补偿（spec §2.5：两操作数均为 number 整型时才包 Math.trunc）。
        // IR 不携带类型信息：M1 仅对“两个整数字面量”这一确定场景包装；
        // 标识符/表达式操作数的类型感知判定由 M2 Task 19 的数值语义 rewriter
        //（持有 Roslyn 语义模型回调，模式同 §2.6.1 重载消歧）处理。
        if (binary.Operator == "/"
            && IsIntegerLiteralOperand(binary.Left)
            && IsIntegerLiteralOperand(binary.Right))
        {
            return $"Math.trunc({left} / {right})";
        }

        // 相等比较：原始类型 === / !==（record/struct 场景由 M3 TsEqualityRewriter 预处理）
        var op = binary.Operator switch
        {
            "==" => "===",
            "!=" => "!==",
            _ => binary.Operator,
        };

        return $"{left} {op} {right}";
    }

    private static bool IsIntegerLiteralOperand(JavaExpression expr)
    {
        // 纯文本判定（IR 字面量为文本、无类型信息；TryParse 不识别 0xFF/1_000 会漏判）：
        // 剥离下划线后——先排除 L 后缀 bigint（不包装：JS bigint 除法天然截断，
        // 且 Math.trunc 不接受 bigint，运行时 TypeError；此检查必须在 0x/0b 前缀
        // 之前，否则 0xFFL 会命中 hex 分支被误判为 number 整数）；
        // 再判 0x/0X、0b/0B 前缀 -> 整数字面量；含 . / e / E -> 浮点（不包装）；
        // 其余纯数字 -> 整数字面量。
        // 混合 bigint/number 操作数（如 7L / 2）的混算转换留给 M2 Task 19 数值语义 rewriter。
        if (expr is not JavaLiteralExpression lit) return false;
        var v = lit.Value.Replace("_", "");
        if (v.EndsWith("L")) return false;
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return true;
        if (v.StartsWith("0b", StringComparison.OrdinalIgnoreCase)) return true;
        if (v.Contains('.') || v.Contains('e') || v.Contains('E')) return false;
        return v.Length > 0 && v.All(char.IsDigit);
    }

    /// <summary>
    /// 左结合 + 链 → 模板字符串。条件：至少一段字符串字面量 + 至少一段表达式
    /// （任一段为字符串字面量即整链为字符串拼接，C# 与 JS 语义一致）。
    /// </summary>
    private bool TryEmitTemplateString(JavaBinaryExpression binary, out string result)
    {
        result = "";
        var parts = new List<JavaExpression>();
        var current = (JavaExpression)binary;
        while (current is JavaBinaryExpression b && b.Operator == "+")
        {
            parts.Add(b.Right);
            current = b.Left;
        }
        parts.Add(current);
        parts.Reverse();

        // 仅链首或第二段为字符串字面量时才整链重建（首个 + 已确定为字符串拼接）；
        // x + y + "a" 形态首个 + 可能为数值相加（C#/JS 均如此），不重建
        var firstIsLiteral = parts.Count > 0 && parts[0] is JavaLiteralExpression l0 && IsStringLiteral(l0);
        var secondIsLiteral = parts.Count > 1 && parts[1] is JavaLiteralExpression l1 && IsStringLiteral(l1);
        if (!firstIsLiteral && !secondIsLiteral) return false;
        var anyExpression = false;
        foreach (var part in parts)
        {
            if (!(part is JavaLiteralExpression lit && IsStringLiteral(lit))) anyExpression = true;
        }
        if (!anyExpression) return false;

        var sb = new StringBuilder();
        sb.Append('`');
        foreach (var part in parts)
        {
            if (part is JavaLiteralExpression lit && IsStringLiteral(lit))
            {
                AppendTemplateText(sb, lit.Value[1..^1]);
            }
            else
            {
                sb.Append("${").Append(ToInlineString(part)).Append('}');
            }
        }
        sb.Append('`');
        result = sb.ToString();
        return true;
    }

    private static bool IsStringLiteral(JavaLiteralExpression lit)
    {
        return lit.Value.Length >= 2 && lit.Value[0] == '"' && lit.Value[^1] == '"';
    }

    /// <summary>模板文本段转义：反引号与 ${ 需转义；既有转义序列（\、\" 等）语义不变。</summary>
    private static void AppendTemplateText(StringBuilder sb, string inner)
    {
        for (var i = 0; i < inner.Length; i++)
        {
            var ch = inner[i];
            if (ch == '`')
            {
                sb.Append("\\`");
                continue;
            }
            if (ch == '$' && i + 1 < inner.Length && inner[i + 1] == '{')
            {
                sb.Append("\\$");
                continue;
            }
            sb.Append(ch);
        }
    }

    private string EmitUnary(JavaUnaryExpression unary)
    {
        return unary.IsPostfix
            ? $"{ToInlineString(unary.Operand)}{unary.Operator}"
            : $"{unary.Operator}{ToInlineString(unary.Operand)}";
    }

    private string EmitCast(JavaCastExpression cast)
    {
        // 引用类型 -> as；数值窄化 -> Number() 等（M2 细化）。
        // 占位说明：int->long 提升（BigInt(...)）与窄化截断（Math.trunc）由
        // M2 Task 19 数值语义 rewriter 处理，当前 `as bigint`/`Number()` 仅为占位。
        var tsType = MapType(cast.Type);   // 实际属性名是 Type（非 TargetType）
        if (tsType == "number" || tsType == "Number")
        {
            return $"Number({ToInlineString(cast.Expression)})";
        }
        return $"{ToInlineString(cast.Expression)} as {tsType}";
    }

    private string EmitNew(JavaNewExpression newExpr)
    {
        // Task 21：IR 中异常类型为 Java 风味名（IllegalArgumentException/IllegalStateException/
        // RuntimeException 等），反向映射到兼容库异常类（spec §4.3.9）；无精确兼容类时
        // 降级 CsException 并追加 /* cs2j: unmapped exception */ 诊断注释。
        var typeName = newExpr.Type;
        if (_mappings.TryMapException(typeName, out var tsException, out var exceptionImports, out var isFallback))
        {
            RegisterImports(tsException, exceptionImports);
            if (isFallback)
            {
                tsException += " /* cs2j: unmapped exception */";
            }
            typeName = tsException;
        }
        else if (_mappings.TryMapType(typeName, out var mappedNewType, out var newTypeImports))
        {
            // M5 / MSAGL: creation types map like other type positions (project
            // FQCNs shorten, Map.Entry/Class<?> etc. map to compat forms).
            RegisterImports(mappedNewType, newTypeImports);
            typeName = mappedNewType;
        }
        // Round 19：CSharpList/CSharpStack 是 global.d.ts 类型别名（Array），值位
        // "new CSharpList<X>(...)" 无运行时值（TS2693）——与 raw 文本
        // TsRawStatementNormalizer.NormalizeCompatListConstructors 同规改写：
        // 无参 → []；单实参 → Array.from(expr)（数字字面量 → new Array(n)）。
        if ((typeName.StartsWith("CSharpList<", StringComparison.Ordinal)
             || typeName.StartsWith("CSharpStack<", StringComparison.Ordinal))
            && (newExpr.Arguments.Count == 0 || newExpr.Arguments.Count == 1))
        {
            if (newExpr.Arguments.Count == 0)
            {
                return "[]";
            }
            var arg = ToInlineString(newExpr.Arguments[0]);
            // 数字字面量 → new Array(n)（ArrayList(int) 容量语义）；其余单实参 →
            // Array.from(expr)（ArrayList(Collection) 拷贝语义；容量表达式非字面量
            // 时 Array.from 触发 TS2769，属下轮候选，与 raw 文本路径行为一致）
            return IsNumericLiteral(arg) ? $"new Array({arg})" : $"Array.from({arg})";
        }
        var args = string.Join(", ", newExpr.Arguments.Select(ToInlineString));
        // Round 20（TS2673）：结构化 new 表达式携带构造器工厂名（ObjectCreationTransformer.
        // TransformToIR 按语义模型解析目标构造器重算 Create$N 序号）——发射端改发
        // Type.Create$N(args)；工厂化类声明端只剩私有无参构造器，直发 new 形态
        // 会报 TS2673（Constructor of class 'X' is private）。接收者取映射后类型名的
        // 裸名（泛型类静态工厂按类名访问，与 raw 路径 TryBuildTsCtorFactoryCall 同规）。
        if (newExpr.FactoryName != null)
        {
            var bareType = typeName.Contains('<') ? typeName[..typeName.IndexOf('<')] : typeName;
            return $"{bareType}.{newExpr.FactoryName}({args})";
        }
        return $"new {typeName}({args})";
    }

    private static bool IsNumericLiteral(string text)
        => text.Length > 0 && text.All(c => char.IsDigit(c) || c == '.' || c == '-')
           && text.Any(char.IsDigit);

    private string EmitMethodCall(JavaMethodCallExpression call)
    {
        var target = call.Target != null ? ToInlineString(call.Target) + "." : "";

        // 尝试方法映射
        // 简化：根据 target 类型查映射。M1 用方法名直译 + 内置 System.out/err 等特殊处理
        var methodName = call.MethodName;

        // System.out/err 特殊处理：println/print/printf 三类（target 是 System.out 或 System.err）
        if (call.Target is JavaMemberAccessExpression ma
            && ma.Target is JavaIdentifierExpression sysId
            && sysId.Name == "System"
            && (ma.MemberName == "out" || ma.MemberName == "err")
            && (methodName == "println" || methodName == "print" || methodName == "printf"))
        {
            var specialArgs = string.Join(", ", call.Arguments.Select(ToInlineString));

            // printf 无直接对应，降级为 console.log，M4 可配置细化
            if (methodName == "printf")
            {
                return $"console.log({specialArgs})";
            }

            // err 统一 console.error；out 保持 console.log / process.stdout.write
            if (ma.MemberName == "err")
            {
                return $"console.error({specialArgs})";
            }

            return methodName == "println"
                ? $"console.log({specialArgs})"
                : $"process.stdout.write({specialArgs})";
        }

        // Task 18：String.valueOf(x) → String(x)（对象插值包装的结构化形态）；
        // String.format(...) → CsString.format(...)（格式串逆翻译 + compat 导入）。
        if (call.Target is JavaIdentifierExpression stringTarget && stringTarget.Name == "String")
        {
            if (call.MethodName == "valueOf" && call.Arguments.Count == 1)
            {
                return $"String({ToInlineString(call.Arguments[0])})";
            }
            if (call.MethodName == "format")
            {
                _imports.AddNamedImport("CsString", "@csharptotypescript/compat");
                if (call.Arguments.Count == 0)
                {
                    return "CsString.format()";
                }
                var formatArg = ToInlineString(call.Arguments[0]);
                if (call.Arguments[0] is JavaLiteralExpression formatLit && IsStringLiteral(formatLit))
                {
                    formatArg = "\"" + JavaToCSharpFormatTranslator.Translate(
                        formatLit.Value[1..^1], out _, out _) + "\"";
                }
                var restArgs = string.Join(", ", call.Arguments.Skip(1).Select(ToInlineString));
                return string.IsNullOrEmpty(restArgs)
                    ? $"CsString.format({formatArg})"
                    : $"CsString.format({formatArg}, {restArgs})";
            }
        }

        // ─── Compat 库方法映射（M6 / MSAGL 修复）──────────────────────────────
        // 发射器在发射阶段没有目标类型信息，按方法名推断是否为 compat 库方法：
        // ① getter 模式 getXxx() → target.Xxx（属性访问）
        // ② boolean getter isXxx() → target.Xxx（属性访问）
        // ③ 已知 compat 方法名（add/remove/clear/size/iterator 等）→ PascalCase
        if (call.Target != null)
        {
            // getter 模式：getXxx() → .Xxx（无参时才可能是属性 getter）
            if (call.Arguments.Count == 0 && TsMemberMappingTable.TryParseGetter(methodName, out var getterProp))
            {
                var targetStr = ToInlineString(call.Target);
                return $"{targetStr}.{getterProp}";
            }
            // boolean getter 模式：isXxx() → .Xxx
            if (call.Arguments.Count == 0 && TsMemberMappingTable.TryParseBooleanGetter(methodName, out var boolProp))
            {
                var targetStr = ToInlineString(call.Target);
                return $"{targetStr}.{boolProp}";
            }
            // setter 模式：setXxx(v) → .Xxx = v
            if (call.Arguments.Count == 1 && TsMemberMappingTable.TryParseSetter(methodName, out var setterProp))
            {
                var targetStr = ToInlineString(call.Target);
                return $"{targetStr}.{setterProp} = {ToInlineString(call.Arguments[0])}";
            }

            // compat 方法名映射：camelCase → PascalCase
            if (_mappings.TryMapCompatMethod(methodName, out var compatMethod))
            {
                var compatArgs = string.Join(", ", call.Arguments.Select(ToInlineString));
                return $"{target}{compatMethod}({compatArgs})";
            }
        }

        var args = string.Join(", ", call.Arguments.Select(ToInlineString));

        // 类型参数（泛型方法调用）TS 不需要运行时类型参数，丢弃
        return $"{target}{methodName}({args})";
    }

    private string EmitMemberAccess(JavaMemberAccessExpression access)
    {
        var target = ToInlineString(access.Target);
        // 特殊：System.out / System.err 直译
        if (target == "System")
        {
            return access.MemberName switch
            {
                "out" => "console",
                "err" => "console",
                _ => $"{target}.{access.MemberName}",
            };
        }
        // 成员名直接透传：TS 点号后的属性名不需要保留字转义（foo.type 合法）。
        // 声明处的属性/字段名仍须 SafeIdentifier（Task 9 语句/成员发射器处理）。
        return $"{target}.{access.MemberName}";
    }

    private string EmitLambda(JavaLambdaExpression lambda)
    {
        // JavaLambdaExpression.Parameters 是 List<string>（纯参数名，无类型）；
        // 参数类型注解在 M2/M3 委托转换时由语义模型补充。
        var parms = string.Join(", ", lambda.Parameters.Select(TsNaming.SafeIdentifier));
        if (lambda.ExpressionBody != null)
        {
            return $"({parms}) => {ToInlineString(lambda.ExpressionBody)}";
        }
        if (lambda.BlockBody != null)
        {
            // 块体交给语句发射器（构造时注入；此处简化为 IR 自带 Java 文本，
            // 完整版在 TypeScriptEmitter 组装时用 TsStatementWriter 渲染 BlockBody）
            return $"({parms}) => {lambda.BlockBody.ToString("")}";
        }
        return $"({parms}) => {{}}";
    }

    private string EmitInstanceOf(JavaInstanceOfExpression inst)
    {
        // class 直译 instanceof；interface 由 TsInterfaceProbeRewriter 预处理（M3）
        // 实际属性名是 Type（非 TypeName）；Java 16 模式变量（PatternVariable）在 M3 模式匹配任务处理
        // Task 21：异常类型名反向映射到兼容库类（C# `x is Exception` 等类型模式）。
        var tsType = inst.Type;
        if (_mappings.TryMapException(tsType, out var mappedException, out var exceptionImports, out var isFallback))
        {
            RegisterImports(mappedException, exceptionImports);
            if (isFallback)
            {
                mappedException += " /* cs2j: unmapped exception */";
            }
            tsType = mappedException;
        }
        return $"{ToInlineString(inst.Expression)} instanceof {tsType}";
    }
}
