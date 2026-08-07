using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.TypeScript.Rewriters;

/// <summary>
/// 索引器语法层 rewriter（Task 27，M3，spec §1.4.3）：用户类型索引器
/// `b[i]` → `b.get_Item(i)`、`b[i] = v` → `b.set_Item(i, v)`、复合赋值
/// `b[i] += v` → `b.set_Item(i, b.get_Item(i) + v)`、`b[i]++` → 赋值调用。
/// 声明端由 IndexerTransformer 的 TS 分叉同步命名 get_Item/set_Item（Java 目标
/// 保持 get/set/put，零回归）。判定条件：语义符号为索引器属性且包含类型在当前
/// 编译单元有源码位置（用户类型）——BCL 集合（List/Dictionary/string/数组）的
/// 索引器保持原路径（get/charAt/下标），不在此改写。
/// 已知限制：`b[i]++` 处于嵌套表达式（非语句）时返回值语义（旧值）不保真，
/// 登记为 M4 语义模型细化项；ref/out 索引器参数、??= 索引器左值按 set_Item 形式
/// 展开（??= → set_Item(i, get_Item(i) ?? v)）。
/// </summary>
public sealed class TsIndexerRewriter : CSharpSyntaxRewriter
{
    private readonly ConversionContext _context;
    private readonly SemanticModel _semanticModel;

    public TsIndexerRewriter(ConversionContext context)
    {
        _context = context;
        _semanticModel = context.SemanticModel!;
    }

    /// <summary>按节点所在树解析语义模型（Task 38：项目模式注入 Compilation/
    /// SemanticModel，多文件各树模型不同）；单文件模式回退到构造时捕获的模型。</summary>
    private SemanticModel? SemanticModelFor(SyntaxNode node)
    {
        if (node.SyntaxTree == null)
        {
            return _semanticModel;
        }
        return _context.GetSemanticModelForNode(node) ?? _semanticModel;
    }

    public override SyntaxNode? VisitElementAccessExpression(ElementAccessExpressionSyntax node)
    {
        // 赋值/自增等左值场景由上层 Visit 特判，不在此改写（否则产生非法 C# 输出
        // 如 b.get_Item(i) = v，重建编译失败）。
        if (IsUserIndexer(node))
        {
            var target = (ExpressionSyntax)base.Visit(node.Expression)!;
            var args = VisitArguments(node);
            return BuildInvocation("get_Item", target, args, node);
        }
        return base.VisitElementAccessExpression(node);
    }

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        if (node.Left is ElementAccessExpressionSyntax left && IsUserIndexer(left))
        {
            var target = (ExpressionSyntax)Visit(left.Expression)!;
            var args = VisitArguments(left);
            var right = (ExpressionSyntax)Visit(node.Right)!;
            var getCall = BuildInvocation("get_Item", target, args, left);
            ExpressionSyntax valueExpr;
            if (node.IsKind(SyntaxKind.SimpleAssignmentExpression))
            {
                valueExpr = right;
            }
            else if (node.IsKind(SyntaxKind.CoalesceAssignmentExpression))
            {
                valueExpr = SyntaxFactory.BinaryExpression(
                    SyntaxKind.CoalesceExpression,
                    getCall,
                    SyntaxFactory.ParenthesizedExpression(right));
            }
            else
            {
                valueExpr = SyntaxFactory.BinaryExpression(
                    ToBinaryKind(node.Kind()),
                    getCall,
                    SyntaxFactory.ParenthesizedExpression(right));
            }
            return BuildInvocation("set_Item", target, args, valueExpr, node);
        }
        return base.VisitAssignmentExpression(node);
    }

    public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
    {
        if (node.Operand is ElementAccessExpressionSyntax operand && IsUserIndexer(operand))
        {
            return RewriteIncDec(operand, node.IsKind(SyntaxKind.PreIncrementExpression), node);
        }
        return base.VisitPrefixUnaryExpression(node);
    }

    public override SyntaxNode? VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
    {
        if (node.Operand is ElementAccessExpressionSyntax operand && IsUserIndexer(operand))
        {
            // 语句位置语义等价（旧值/新值均被丢弃）；嵌套表达式返回值差异登记为限制
            return RewriteIncDec(operand, node.IsKind(SyntaxKind.PostIncrementExpression), node);
        }
        return base.VisitPostfixUnaryExpression(node);
    }

    private SyntaxNode RewriteIncDec(ElementAccessExpressionSyntax operand, bool isIncrement, SyntaxNode original)
    {
        var target = (ExpressionSyntax)Visit(operand.Expression)!;
        var args = VisitArguments(operand);
        var getCall = BuildInvocation("get_Item", target, args, operand);
        var one = SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(1));
        var binary = SyntaxFactory.BinaryExpression(
            isIncrement ? SyntaxKind.AddExpression : SyntaxKind.SubtractExpression,
            getCall,
            one);
        return BuildInvocation("set_Item", target, args, binary, original);
    }

    /// <summary>复合赋值语法种类 → 二元表达式种类（SyntaxFacts.GetBinaryExpression 仅接受
    /// 二元种类，不接受赋值种类，需自行映射）。</summary>
    private static SyntaxKind ToBinaryKind(SyntaxKind assignmentKind) => assignmentKind switch
    {
        SyntaxKind.AddAssignmentExpression => SyntaxKind.AddExpression,
        SyntaxKind.SubtractAssignmentExpression => SyntaxKind.SubtractExpression,
        SyntaxKind.MultiplyAssignmentExpression => SyntaxKind.MultiplyExpression,
        SyntaxKind.DivideAssignmentExpression => SyntaxKind.DivideExpression,
        SyntaxKind.ModuloAssignmentExpression => SyntaxKind.ModuloExpression,
        SyntaxKind.AndAssignmentExpression => SyntaxKind.BitwiseAndExpression,
        SyntaxKind.OrAssignmentExpression => SyntaxKind.BitwiseOrExpression,
        SyntaxKind.ExclusiveOrAssignmentExpression => SyntaxKind.ExclusiveOrExpression,
        SyntaxKind.LeftShiftAssignmentExpression => SyntaxKind.LeftShiftExpression,
        SyntaxKind.RightShiftAssignmentExpression => SyntaxKind.RightShiftExpression,
        SyntaxKind.UnsignedRightShiftAssignmentExpression => SyntaxKind.UnsignedRightShiftExpression,
        _ => SyntaxKind.None,
    };

    private List<ArgumentSyntax> VisitArguments(ElementAccessExpressionSyntax node)
    {
        return node.ArgumentList.Arguments
            .Select(a => (ArgumentSyntax)Visit(a)!)
            .ToList();
    }

    private InvocationExpressionSyntax BuildInvocation(
        string methodName, ExpressionSyntax target, List<ArgumentSyntax> args, SyntaxNode original)
    {
        return BuildInvocation(methodName, target, args, null, original);
    }

    private InvocationExpressionSyntax BuildInvocation(
        string methodName, ExpressionSyntax target, List<ArgumentSyntax> args,
        ExpressionSyntax? valueExpr, SyntaxNode original)
    {
        var allArgs = new List<ArgumentSyntax>(args);
        if (valueExpr != null)
        {
            allArgs.Add(SyntaxFactory.Argument(valueExpr));
        }
        var invocation = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                target,
                SyntaxFactory.IdentifierName(methodName)),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(allArgs)));
        return invocation
            .WithLeadingTrivia(original.GetLeadingTrivia())
            .WithTrailingTrivia(original.GetTrailingTrivia());
    }

    private bool IsUserIndexer(ElementAccessExpressionSyntax node)
    {
        return SemanticModelFor(node)?.GetSymbolInfo(node).Symbol is IPropertySymbol { IsIndexer: true } prop
            && prop.ContainingType?.Locations.Any(l => l.IsInSource) == true;
    }
}