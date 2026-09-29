using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Operators;

/// <summary>决策 5 六类提升公式（Task 8 完成实现）。分析期（OperatorRule 阶段 2）已把
/// 不可展开的提升站点升级为 Infeasible（CS2J5176），简单运算元形态在此占位——
/// 本占位在 Task 6..7 期间对测试矩阵不可达。</summary>
internal static partial class LiftedOperatorExpansion
{
    public static ExpressionSyntax ExpandBinaryOrUnary(
        OperatorPlan plan, IMethodSymbol boundOperator, SemanticModel model,
        ExpressionSyntax site, params ExpressionSyntax[] args)
        => throw new System.NotSupportedException(
            "lifted binary/unary sites are gated to Infeasible (CS2J5176) before rewrite");

    public static ExpressionSyntax ExpandLiftedConversion(
        ConversionResolver.Recipe recipe, ExpressionSyntax visited, int spanStart)
        => throw new System.NotSupportedException(
            "lifted conversion sites are gated to Infeasible (CS2J5176) before rewrite");
}
