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
        return ExpandCore(qualifier, plan.GeneratedMethodName, model, site, args);
    }

    /// <summary>
    /// C49 块运算符变体（M2）：同一提升公式，但 qualifier/方法名显式传入——
    /// 块运算符物化到块所在父 host（而非合成 extension 容器），无法从被绑定
    /// 运算符的 ContainingType 推导 qualifier。
    /// </summary>
    public static ExpressionSyntax ExpandBlockBinaryOrUnary(
        string qualifier, string generatedMethodName,
        IMethodSymbol boundOperator, SemanticModel model,
        ExpressionSyntax site, params ExpressionSyntax[] args)
        => ExpandCore(qualifier, generatedMethodName, model, site, args);

    private static ExpressionSyntax ExpandCore(
        string qualifier, string generatedMethodName,
        SemanticModel model, ExpressionSyntax site, ExpressionSyntax[] args)
    {
        var op = model.GetOperation(site)!;
        var resultDisplay = op.Type!.ToMinimalDisplayString(model, site.SpanStart);

        if (op is IUnaryOperation)
            return BuildBinaryOrUnary(
                qualifier, generatedMethodName, isUnary: true,
                IsNullableSyntax(args[0], model), rightNullable: false,
                operatorKind: default, resultDisplay, args);

        var binary = (IBinaryOperation)op;
        return BuildBinaryOrUnary(
            qualifier, generatedMethodName, isUnary: false,
            IsNullableSyntax(args[0], model), IsNullableSyntax(args[1], model),
            binary.OperatorKind, resultDisplay, args);
    }

    /// <summary>
    /// C49 块提升二元（M2）：nullability/结果类型由分析期（原树模型）钉死，
    /// args 为当前树已访问节点（两阶段重建后不能再对其调 GetTypeInfo）。
    /// </summary>
    public static ExpressionSyntax ExpandBlockBinary(
        string qualifier, string name,
        bool leftNullable, bool rightNullable,
        BinaryOperatorKind operatorKind, string resultTypeDisplay,
        ExpressionSyntax left, ExpressionSyntax right)
        => BuildBinaryOrUnary(
            qualifier, name, isUnary: false,
            leftNullable, rightNullable, operatorKind, resultTypeDisplay,
            new[] { left, right });

    /// <summary>C49 块提升一元（M2）。</summary>
    public static ExpressionSyntax ExpandBlockUnary(
        string qualifier, string name,
        bool operandNullable, string resultTypeDisplay,
        ExpressionSyntax operand)
        => BuildBinaryOrUnary(
            qualifier, name, isUnary: true,
            operandNullable, rightNullable: false,
            operatorKind: default, resultTypeDisplay,
            new[] { operand });

    private static ExpressionSyntax BuildBinaryOrUnary(
        string qualifier, string name,
        bool isUnary, bool leftNullable, bool rightNullable,
        BinaryOperatorKind operatorKind, string resultTypeDisplay,
        ExpressionSyntax[] args)
    {
        var nullValue = NullOf(resultTypeDisplay);

        if (isUnary)
            return Conditional(HasValue(args[0]),
                Call(qualifier, name, Value(args[0])),
                nullValue);

        var call = Call(qualifier, name,
            leftNullable ? Value(args[0]) : args[0],
            rightNullable ? Value(args[1]) : args[1]);

        if (operatorKind == BinaryOperatorKind.Equals)
            return Conditional(AllMissing(leftNullable, rightNullable, args[0], args[1]), True(),
                Conditional(AllPresent(leftNullable, rightNullable, args[0], args[1]), call, False()));
        if (operatorKind == BinaryOperatorKind.NotEquals)
            return Conditional(AllMissing(leftNullable, rightNullable, args[0], args[1]), False(),
                Conditional(AllPresent(leftNullable, rightNullable, args[0], args[1]), call, True()));

        return Conditional(
            Guard(leftNullable, rightNullable, args[0], args[1], SyntaxKind.BitwiseAndExpression),
            call, nullValue);
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
        => ExpandBlockLiftedCompound(
            qualifier, generatedMethodName,
            IsNullableSyntax(left, model), IsNullableSyntax(right, model),
            resultType.ToMinimalDisplayString(model, spanStart),
            left, right);

    /// <summary>
    /// C49 块提升复合（M2）：事实（每侧可空性/结果类型显示）由分析期钉死，
    /// left/right 为当前树已访问节点。
    /// </summary>
    public static ExpressionSyntax ExpandBlockLiftedCompound(
        string qualifier, string generatedMethodName,
        bool leftNullable, bool rightNullable,
        string resultTypeDisplay,
        ExpressionSyntax left, ExpressionSyntax right)
    {
        var call = Call(qualifier, generatedMethodName,
            leftNullable ? Value(left) : left,
            rightNullable ? Value(right) : right);
        var nullValue = NullOf(resultTypeDisplay);
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
