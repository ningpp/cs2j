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

    public TsExpressionWriter(TsMemberMappingTable mappings, TsImportCollector imports)
    {
        _mappings = mappings;
        _imports = imports;
    }

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
            JavaRawExpression raw => raw.Code,
            _ => expr.ToInlineString(),
        };
    }

    /// <summary>
    /// Java 风味类型名 → TS 类型（供语句/成员发射器复用，如变量声明、字段）。
    /// </summary>
    public string MapType(string javaOrCsharpType)
    {
        return _mappings.TryMapType(javaOrCsharpType, out var ts, out var imports)
            ? WithImports(ts, imports)
            : javaOrCsharpType;
    }

    private string WithImports(string tsType, IReadOnlyList<string> imports)
    {
        foreach (var imp in imports)
        {
            // compat 包符号导入；模块名即 imp，符号名即 tsType
            _imports.AddNamedImport(tsType.Split('<')[0], imp);
        }
        return tsType;
    }

    private string EmitLiteral(JavaLiteralExpression lit)
    {
        var v = lit.Value;

        // long 字面量：100L -> 100n
        if (v.EndsWith("L") && long.TryParse(v[..^1], out _))
        {
            return v[..^1] + "n";
        }

        // char 字面量：'a' -> "a"；泛化支持转义字符（'\n' -> "\n"）。
        // 边界：内含双引号（如 '\"'）的转义在 M2 字符处理任务中细化。
        if (v.Length >= 3 && v.StartsWith("'") && v.EndsWith("'"))
        {
            var inner = v[1..^1];
            if (!inner.Contains('"'))
            {
                return "\"" + inner + "\"";
            }
        }

        return v;
    }

    private string EmitBinary(JavaBinaryExpression binary)
    {
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
        // 仅整数字面量（可解析为 long 且无 L 后缀）。bigint 字面量（L 后缀）
        // 不可包 Math.trunc：JS bigint 除法天然截断，且 Math.* 不接受 bigint（运行时 TypeError）。
        return expr is JavaLiteralExpression lit
            && long.TryParse(lit.Value, out _)
            && !lit.Value.EndsWith("L");
    }

    private string EmitUnary(JavaUnaryExpression unary)
    {
        return unary.IsPostfix
            ? $"{ToInlineString(unary.Operand)}{unary.Operator}"
            : $"{unary.Operator}{ToInlineString(unary.Operand)}";
    }

    private string EmitCast(JavaCastExpression cast)
    {
        // 引用类型 -> as；数值窄化 -> Number() 等（M2 细化）
        var tsType = MapType(cast.Type);   // 实际属性名是 Type（非 TargetType）
        if (tsType == "number" || tsType == "Number")
        {
            return $"Number({ToInlineString(cast.Expression)})";
        }
        return $"{ToInlineString(cast.Expression)} as {tsType}";
    }

    private string EmitNew(JavaNewExpression newExpr)
    {
        var args = string.Join(", ", newExpr.Arguments.Select(ToInlineString));
        return $"new {newExpr.Type}({args})";
    }

    private string EmitMethodCall(JavaMethodCallExpression call)
    {
        var target = call.Target != null ? ToInlineString(call.Target) + "." : "";

        // 尝试方法映射
        // 简化：根据 target 类型查映射。M1 用方法名直译 + 内置 System.out.println 等特殊处理
        var methodName = call.MethodName;

        // System.out.println -> console.log（特殊：target 是 System.out）
        if (call.Target is JavaMemberAccessExpression ma
            && ma.Target is JavaIdentifierExpression sysId
            && sysId.Name == "System"
            && ma.MemberName == "out"
            && (methodName == "println" || methodName == "print"))
        {
            var specialArgs = string.Join(", ", call.Arguments.Select(ToInlineString));
            return methodName == "println"
                ? $"console.log({specialArgs})"
                : $"process.stdout.write({specialArgs})";
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
        var member = TsNaming.SafeIdentifier(access.MemberName);
        return $"{target}.{member}";
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
        return $"{ToInlineString(inst.Expression)} instanceof {inst.Type}";
    }
}