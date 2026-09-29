using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CSharpToJava.Core.MemberDesugar.Operators;

/// <summary>决策 5 六类提升公式的语法构造（OC-6）。分析期保证运算元为简单形态
/// （<see cref="OperatorFacts.IsSimpleLiftedOperand"/>：局部/参数/字段标识符），
/// 故运算元可安全地引用两次（`.HasValue` 守卫 + `.Value` 取值）。
///
/// 公式（R = 运算符返回类型的可空形态，D = 去糖后的静态方法）：
/// ① 提升转换  `s.HasValue ? D.ToR(s.GetValueOrDefault()) : (R?)null`
/// ② 提升一元  `a.HasValue ? D.Negate(a.Value) : (R?)null`
/// ③ 提升算术  `(a.HasValue &amp; b.HasValue) ? D.Add(a.Value, b.Value) : (R?)null`
/// ④ 提升关系  `(a.HasValue &amp; b.HasValue) ? D.GreaterThan(a.Value, b.Value) : (bool?)null`
/// ⑤ 提升相等  三值表：双 null→true（`==`）/false（`!=`）、一 null→false/true、否则调用
/// ⑥ 提升复合  `x = x.HasValue ? D.Add(x.Value, v) : (R?)null`</summary>
internal static partial class LiftedOperatorExpansion
{
    public static ExpressionSyntax ExpandBinaryOrUnary(
        OperatorPlan plan, IMethodSymbol boundOperator, SemanticModel model,
        ExpressionSyntax site, params ExpressionSyntax[] args)
    {
        var qualifier = boundOperator.ContainingType!
            .ToMinimalDisplayString(model, site.SpanStart);
        var op = model.GetOperation(site)!;
        var nullValue = NullOf(op.Type!.ToMinimalDisplayString(model, site.SpanStart));

        if (op is IUnaryOperation)
            return Conditional(HasValue(args[0]),
                Call(qualifier, plan.GeneratedMethodName, Value(args[0])),
                nullValue);

        if (op is IBinaryOperation binary)
        {
            // 可空性取**语法**类型（模型 GetTypeInfo）：提升操作里运算元的
            // IOperation.Type 会被抬成 T?，而语法类型为非可空时不该生成守卫
            //（否则产出 `v.HasValue`——非可空 T 无此成员，且语义上也无 null 可言）。
            var leftNullable = IsNullableSyntax(args[0], model);
            var rightNullable = IsNullableSyntax(args[1], model);
            var call = Call(qualifier, plan.GeneratedMethodName,
                leftNullable ? Value(args[0]) : args[0],
                rightNullable ? Value(args[1]) : args[1]);

            if (binary.OperatorKind == BinaryOperatorKind.Equals)
                return Conditional(AllMissing(leftNullable, rightNullable, args[0], args[1]), True(),
                    Conditional(AllPresent(leftNullable, rightNullable, args[0], args[1]), call, False()));
            if (binary.OperatorKind == BinaryOperatorKind.NotEquals)
                return Conditional(AllMissing(leftNullable, rightNullable, args[0], args[1]), False(),
                    Conditional(AllPresent(leftNullable, rightNullable, args[0], args[1]), call, True()));

            return Conditional(
                Guard(leftNullable, rightNullable, args[0], args[1], SyntaxKind.BitwiseAndExpression),
                call, nullValue);
        }
        throw new System.NotSupportedException($"unsupported lifted operation {op.Kind}");
    }

    /// <summary>公式 ①：提升转换（S? → T?）。</summary>
    public static ExpressionSyntax ExpandLiftedConversion(
        string qualifier, string generatedMethodName, string nullableResultDisplay,
        ExpressionSyntax operand)
        => Conditional(HasValue(operand),
            Call(qualifier, generatedMethodName, GetValueOrDefault(operand)),
            NullOf(nullableResultDisplay));

    /// <summary>公式 ⑥：提升复合（左值可空时守卫 + 空回写）。</summary>
    public static ExpressionSyntax ExpandLiftedCompound(
        string qualifier, string generatedMethodName, ExpressionSyntax left,
        ExpressionSyntax right, ITypeSymbol resultType,
        SemanticModel model, int spanStart)
    {
        var leftNullable = IsNullableSyntax(left, model);
        var rightNullable = IsNullableSyntax(right, model);
        var call = Call(qualifier, generatedMethodName,
            leftNullable ? Value(left) : left,
            rightNullable ? Value(right) : right);
        var nullValue = NullOf(resultType.ToMinimalDisplayString(model, spanStart));
        return Conditional(
            Guard(leftNullable, rightNullable, left, right, SyntaxKind.BitwiseAndExpression),
            call, nullValue);
    }

    // ---- 守卫与构造工具 ----

    /// <summary>语法层面的可空性（模型 GetTypeInfo.Type，非转换后类型）。</summary>
    private static bool IsNullableSyntax(ExpressionSyntax e, SemanticModel model)
        => model.GetTypeInfo(e).Type is INamedTypeSymbol
        { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };

    /// <summary>存在性守卫：`a.HasValue &amp; b.HasValue`（单侧可空时退化为单元）。</summary>
    private static ExpressionSyntax Guard(
        bool leftNullable, bool rightNullable,
        ExpressionSyntax left, ExpressionSyntax right, SyntaxKind op)
    {
        var present = new List<ExpressionSyntax>();
        if (leftNullable) present.Add(HasValue(left));
        if (rightNullable) present.Add(HasValue(right));
        return present.Aggregate((x, y) => SyntaxFactory.BinaryExpression(op, x, y));
    }

    /// <summary>双 null 判定：`!a.HasValue &amp;&amp; !b.HasValue`（单元时退化为单侧）。</summary>
    private static ExpressionSyntax AllMissing(
        bool leftNullable, bool rightNullable, ExpressionSyntax left, ExpressionSyntax right)
    {
        var missing = new List<ExpressionSyntax>();
        if (leftNullable) missing.Add(Not(HasValue(left)));
        if (rightNullable) missing.Add(Not(HasValue(right)));
        return missing.Aggregate((x, y) =>
            SyntaxFactory.BinaryExpression(SyntaxKind.LogicalAndExpression, x, y));
    }

    /// <summary>双非 null 判定：`a.HasValue &amp;&amp; b.HasValue`（单元时退化为单侧）。</summary>
    private static ExpressionSyntax AllPresent(
        bool leftNullable, bool rightNullable, ExpressionSyntax left, ExpressionSyntax right)
    {
        var present = new List<ExpressionSyntax>();
        if (leftNullable) present.Add(HasValue(left));
        if (rightNullable) present.Add(HasValue(right));
        return present.Aggregate((x, y) =>
            SyntaxFactory.BinaryExpression(SyntaxKind.LogicalAndExpression, x, y));
    }

    private static ExpressionSyntax HasValue(ExpressionSyntax e)
        => Retarget(e, "HasValue");

    private static ExpressionSyntax Value(ExpressionSyntax e)
        => Retarget(e, "Value");

    /// <summary>在运算元上追加成员访问。分析期已把运算元约束为简单形态
    /// （局部/参数/字段标识符：`a`、`this.F`、`h.F`），皆为高优先级表达式，无需括号。</summary>
    private static ExpressionSyntax Retarget(ExpressionSyntax e, string member)
        => SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression, e,
            SyntaxFactory.IdentifierName(member));

    private static ExpressionSyntax GetValueOrDefault(ExpressionSyntax e)
        => SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression, e,
                SyntaxFactory.IdentifierName("GetValueOrDefault")));

    private static ExpressionSyntax Not(ExpressionSyntax e)
        => SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, e);

    private static ExpressionSyntax Conditional(
        ExpressionSyntax condition, ExpressionSyntax whenTrue, ExpressionSyntax whenFalse)
        => SyntaxFactory.ConditionalExpression(condition, whenTrue, whenFalse);

    private static ExpressionSyntax Call(string qualifier, string name, params ExpressionSyntax[] args)
        => SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.ParseTypeName(qualifier),
                SyntaxFactory.IdentifierName(name)),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
                args.Select(SyntaxFactory.Argument))));

    private static ExpressionSyntax NullOf(string typeDisplay)
        => SyntaxFactory.CastExpression(
            SyntaxFactory.ParseTypeName(
                typeDisplay.EndsWith("?", StringComparison.Ordinal) ? typeDisplay : typeDisplay + "?"),
            SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));

    private static ExpressionSyntax True()
        => SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression);

    private static ExpressionSyntax False()
        => SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression);
}
