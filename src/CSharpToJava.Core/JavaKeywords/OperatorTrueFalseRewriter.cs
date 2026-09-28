using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.JavaKeywords;

/// <summary>
/// Phase 3b：operator 声明→方法 + 布尔上下文改写。
/// <para>JK-5：operator true/false → IsTrue/IsFalse 声明降级</para>
/// <para>JK-6a：if/while/do/for 条件包裹 IsTrue(expr)</para>
/// <para>JK-6b：conditional 条件包裹 IsTrue(expr)</para>
/// <para>JK-6c：when 守卫条件包裹 IsTrue(expr)</para>
/// <para>JK-6d/e：&& / || 同构展开——标注为后续迭代</para>
/// <para>JK-8：不可行 plan 跳过（不执行任何改写）</para>
/// </summary>
internal sealed partial class OperatorTrueFalseRewriter : CSharpSyntaxRewriter
{
    private readonly RewriteContext _context;
    private readonly HashSet<INamedTypeSymbol> _feasibleTypes;

    public OperatorTrueFalseRewriter(RewriteContext context) : base(visitIntoStructuredTrivia: true)
    {
        _context = context;
        _feasibleTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var plan in context.ActivePlans)
        {
            if (plan.Kind != JavaKeywordPlanKind.OperatorTrueFalse) continue;
            if (plan.Outcome != PlanOutcome.Converted) continue;
            if (plan.OperatorType != null)
                _feasibleTypes.Add(plan.OperatorType);
        }
    }

    // ---- JK-5: 声明降级 ----

    public override SyntaxNode? VisitOperatorDeclaration(OperatorDeclarationSyntax node)
    {
        var visited = (OperatorDeclarationSyntax?)base.VisitOperatorDeclaration(node);
        if (visited == null) return null;

        var symbol = _context.Model.GetDeclaredSymbol(node);
        if (symbol == null) return visited;

        // 判断是 op_True 还是 op_False
        var isOpTrue = symbol.Name == "op_True";
        var isOpFalse = symbol.Name == "op_False";
        if (!isOpTrue && !isOpFalse) return visited;

        // 检查所属类型是否在可行集合中
        if (!_feasibleTypes.Contains(symbol.ContainingType)) return visited;

        var newName = isOpTrue ? "IsTrue" : "IsFalse";

        // 构造 MethodDeclarationSyntax 替换 OperatorDeclarationSyntax
        // 运算符声明没有 ConstraintClauses、ExplicitInterfaceSpecifier
        var methodDecl = SyntaxFactory.MethodDeclaration(
            visited.ReturnType,
            SyntaxFactory.Identifier(newName).WithTriviaFrom(visited.OperatorToken))
            .WithModifiers(visited.Modifiers)
            .WithParameterList(visited.ParameterList)
            .WithBody(visited.Body)
            .WithExpressionBody(visited.ExpressionBody)
            .WithSemicolonToken(visited.SemicolonToken)
            .WithAttributeLists(visited.AttributeLists)
            .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);

        _context.Statistics.OperatorTypesDowngraded++;
        return methodDecl;
    }

    // ---- JK-6a: if/while/do/for 条件包裹 ----

    public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
    {
        var visited = (IfStatementSyntax?)base.VisitIfStatement(node);
        if (visited == null) return null;
        return WrapIfBooleanSite(visited, visited.Condition,
            (stmt, newCondition) => stmt.WithCondition(newCondition));
    }

    public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
    {
        var visited = (WhileStatementSyntax?)base.VisitWhileStatement(node);
        if (visited == null) return null;
        return WrapIfBooleanSite(visited, visited.Condition,
            (stmt, newCondition) => stmt.WithCondition(newCondition));
    }

    public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
    {
        var visited = (DoStatementSyntax?)base.VisitDoStatement(node);
        if (visited == null) return null;
        return WrapIfBooleanSite(visited, visited.Condition,
            (stmt, newCondition) => stmt.WithCondition(newCondition));
    }

    public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
    {
        var visited = (ForStatementSyntax?)base.VisitForStatement(node);
        if (visited == null) return null;
        if (visited.Condition == null) return visited;
        return WrapIfBooleanSite(visited, visited.Condition,
            (stmt, newCondition) => stmt.WithCondition(newCondition));
    }

    // ---- JK-6b: conditional 条件包裹 ----

    public override SyntaxNode? VisitConditionalExpression(ConditionalExpressionSyntax node)
    {
        var visited = (ConditionalExpressionSyntax?)base.VisitConditionalExpression(node);
        if (visited == null) return null;
        return WrapIfBooleanSite(visited, visited.Condition,
            (expr, newCondition) => expr.WithCondition(newCondition));
    }

    // ---- JK-6c: when 守卫条件包裹 ----

    public override SyntaxNode? VisitWhenClause(WhenClauseSyntax node)
    {
        var visited = (WhenClauseSyntax?)base.VisitWhenClause(node);
        if (visited == null) return null;
        if (visited.Condition == null) return visited;
        return WrapIfBooleanSite(visited, visited.Condition,
            (clause, newCondition) => clause.WithCondition(newCondition));
    }

    // ---- 辅助 ----

    /// <summary>
    /// 检查条件表达式是否绑定了用户定义的 op_True，
    /// 若是则包裹为 <c>Type.IsTrue(expr)</c>。
    /// </summary>
    private TNode? WrapIfBooleanSite<TNode>(
        TNode visited,
        ExpressionSyntax condition,
        Func<TNode, ExpressionSyntax, TNode> withCondition)
        where TNode : SyntaxNode
    {
        // 先递归处理条件表达式内部（已由 base.Visit 完成）
        // 检查条件到 bool 的隐式转换是否绑定了 op_True
        var opType = TryGetOperatorTypeForCondition(condition);
        if (opType == null) return visited;

        if (!_feasibleTypes.Contains(opType)) return visited;

        var wrapped = WrapWithIsTrue(condition, opType);
        if (wrapped == null) return visited;

        var result = withCondition(visited, wrapped)
            .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);

        _context.Statistics.BooleanSitesRewritten++;
        return result;
    }

    /// <summary>
    /// 检查条件表达式到 bool 的隐式转换是否绑定了 op_True，
    /// 返回运算符所属类型。
    /// </summary>
    private INamedTypeSymbol? TryGetOperatorTypeForCondition(ExpressionSyntax condition)
    {
        var conversion = _context.Model.GetConversion(condition);
        if (conversion.IsUserDefined)
        {
            var method = conversion.MethodSymbol;
            if (method != null && method.Name == "op_True")
                return method.ContainingType;
        }

        // 对于 if/while 等，如果条件类型本身就是运算符类型
        var condType = _context.Model.GetTypeInfo(condition).Type as INamedTypeSymbol;
        if (condType != null && _feasibleTypes.Contains(condType))
            return condType;

        // 检查 ConvertedType
        var convertedType = _context.Model.GetTypeInfo(condition).ConvertedType as INamedTypeSymbol;
        if (convertedType != null && _feasibleTypes.Contains(convertedType))
            return convertedType;

        return null;
    }

    /// <summary>
    /// 将条件表达式包裹为 <c>Type.IsTrue(expr)</c>。
    /// 类型限定名使用 <c>ToMinimalDisplayString</c> 以尽量短。
    /// </summary>
    private ExpressionSyntax? WrapWithIsTrue(
        ExpressionSyntax condition,
        INamedTypeSymbol operatorType)
    {
        // 获取类型限定名（尽量短）
        var typeName = operatorType.ToMinimalDisplayString(
            _context.Model,
            condition.SpanStart);

        // 构造 Type.IsTrue(condition) 调用
        var typeExpr = SyntaxFactory.ParseName(typeName);
        var invocation = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                typeExpr,
                SyntaxFactory.IdentifierName("IsTrue")),
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(condition))));

        return invocation.WithTriviaFrom(condition)
            .WithAdditionalAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation);
    }
}
