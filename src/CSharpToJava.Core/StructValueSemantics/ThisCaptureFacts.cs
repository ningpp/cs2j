using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.StructValueSemantics;

/// <summary>
/// 作用域内对实例 this 的精确引用分类（SV040-c/d 根因修复的判定基础）。
/// 旧实现只做“任意非静态成员简单名”粗扫，会把 <c>builder.AsSpan()</c> 这类
/// 显式其它接收者调用误判成 this 引用；本分类只承认经隐式 this / 显式 this/base
/// 路由的引用，并区分读 / 写。
/// </summary>
internal enum ThisCaptureKind
{
    /// <summary>作用域不引用本 struct 实例态。</summary>
    None,

    /// <summary>仅经 this 读取实例态。</summary>
    Reads,

    /// <summary>经 this 写入实例态（赋值/复合赋值/自增减/ref-out 实参/变异接收者调用）。</summary>
    Writes,
}

internal static class ThisCaptureFacts
{
    /// <summary>
    /// 遍历 <paramref name="scope"/>（含其全部嵌套匿名函数/局部函数），判定实例态
    /// 引用的最强级别；<paramref name="mutatingMethods"/> 给出本 struct 已确认的
    /// 变异实例方法集（隐式 this 调用命中即写），不可证者按写计（宁可保守）。
    /// </summary>
    public static ThisCaptureKind Classify(
        SyntaxNode scope,
        INamedTypeSymbol symbol,
        SemanticModel model,
        IReadOnlyCollection<IMethodSymbol>? mutatingMethods = null)
    {
        var result = ThisCaptureKind.None;

        foreach (var node in scope.DescendantNodesAndSelf())
        {
            var isThis = node switch
            {
                ThisExpressionSyntax => true,
                BaseExpressionSyntax => true,
                SimpleNameSyntax simple => IsImplicitThisMember(simple, symbol, model),
                _ => false,
            };
            if (!isThis) continue;

            result = ThisCaptureKind.Reads;
            if (IsWriteUsage(node, model, mutatingMethods))
                return ThisCaptureKind.Writes;
        }

        return result;
    }

    /// <summary>
    /// 简单名是否经“隐式 this”绑定到本 struct（含其基链 System.ValueType/Object）
    /// 的非静态成员。显式接收者（<c>builder.AsSpan()</c>）、命名实参、nameof、
    /// 对象初始化器名等值不属此列；局部函数/匿名函数符号亦排除。
    /// </summary>
    public static bool IsImplicitThisMember(
        SimpleNameSyntax name,
        INamedTypeSymbol symbol,
        SemanticModel model)
    {
        // 显式接收者的成员名：x.M、x?.M —— 接收者另有所属（站点 1 假阳根因）。
        if (name.Parent is MemberAccessExpressionSyntax { Name: { } accessed } && accessed == name)
            return false;
        if (name.Parent is MemberBindingExpressionSyntax { Name: { } boundName } && boundName == name)
            return false;
        // 命名实参 start: / 形参别名。
        if (name.Parent is NameColonSyntax) return false;
        // new { X } / new C { X = ... } / 匿名对象成员名不解析为接收者成员。
        if (name.Parent is NameEqualsSyntax) return false;
        // 对象/with 初始化器的成员声明符（new C { M = ... } / x with { M = ... }）：
        // 左侧名字绑定的是新对象（或克隆体）的成员，不是隐式 this；右值中的同名
        // 引用仍按外层作用域解析，故只排声明符本身。
        if (name.Parent is AssignmentExpressionSyntax
                {
                    Left: { } declarator,
                    Parent: InitializerExpressionSyntax initializer,
                }
            && declarator == name
            && (initializer.IsKind(SyntaxKind.ObjectInitializerExpression)
                || initializer.IsKind(SyntaxKind.WithInitializerExpression)))
        {
            return false;
        }
        // 特性实参只能是常量，不可能是实例态。
        if (name.Ancestors().OfType<AttributeSyntax>().Any()) return false;
        // nameof(X) 是纯语法引用，不构成捕获。
        if (IsInsideNameOf(name)) return false;

        var member = model.GetSymbolInfo(name).Symbol;
        if (member is not { IsStatic: false })
        {
            member = model.GetSymbolInfo(name).CandidateSymbols.FirstOrDefault();
            if (member is not { IsStatic: false }) return false;
        }

        // 局部/参数/范围变量的 ContainingType 会沿 ContainingSymbol 链解析到本 struct，
        // 必须在类型归属判断前显式排除（它们经局部环境绑定，不经隐式 this）。
        if (member is ILocalSymbol or IParameterSymbol or IRangeVariableSymbol)
            return false;
        // 类型形参 T 的 ContainingType 同样是本 struct，但它是类型不是实例态成员。
        if (member is ITypeParameterSymbol)
            return false;

        if (member is IMethodSymbol method
            && method.MethodKind is MethodKind.LocalFunction
                or MethodKind.AnonymousFunction
                or MethodKind.LambdaMethod)
        {
            return false;
        }

        var containing = member.ContainingType?.OriginalDefinition;
        if (containing == null) return false;
        if (SymbolEqualityComparer.Default.Equals(containing, symbol.OriginalDefinition))
            return true;

        for (var baseType = symbol.BaseType; baseType != null; baseType = baseType.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(
                    baseType.OriginalDefinition, containing))
                return true;
        }

        return false;
    }

    /// <summary>
    /// this 路由节点在所在位置是否构成对实例态的写入（或保守按写处理的变异调用）。
    /// </summary>
    private static bool IsWriteUsage(
        SyntaxNode node,
        SemanticModel model,
        IReadOnlyCollection<IMethodSymbol>? mutatingMethods)
    {
        // ref/out 实参（in 为只读别名，按读）。
        if (node is ExpressionSyntax expression
            && node.Parent is ArgumentSyntax argument
            && argument.Expression == RootOf(expression)
            && (argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)
                || argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)))
        {
            return true;
        }

        // 赋值（含复合赋值）左根，或自增/自减操作数（均含成员链形态：pos.X++）。
        if (FindAssignmentTarget(node) != null) return true;
        if (IsOperandOfIncrement(node)) return true;

        // 隐式 this 调用本类型变异方法：Mutate();（非变异/查询方法按读）。
        if (node is SimpleNameSyntax
            && node.Parent is InvocationExpressionSyntax invocationSelf
            && invocationSelf.Expression == node
            && model.GetSymbolInfo(node).Symbol is IMethodSymbol selfMethod
            && mutatingMethods != null
            && ContainsMethod(mutatingMethods, selfMethod))
        {
            return true;
        }

        // 以实例态为接收者的成员调用（x.M()）：是否变异取决于接收字段/属性的类型
        // 分析，此处保守按写——多读一次出口回写无语义差异，漏写则副本不可见。
        if (IsReceiverOfMemberInvocation(node)) return true;

        return false;
    }

    /// <summary>沿成员/元素链找该节点作为左根的赋值表达式（任何赋值含复合赋值）。</summary>
    private static AssignmentExpressionSyntax? FindAssignmentTarget(SyntaxNode node)
    {
        var current = node;
        while (true)
        {
            if (current.Parent is AssignmentExpressionSyntax assignment && assignment.Left == current)
                return assignment;
            if (current.Parent is MemberAccessExpressionSyntax { Expression: { } maChild } && maChild == current)
            {
                current = current.Parent;
                continue;
            }
            if (current.Parent is ElementAccessExpressionSyntax { Expression: { } eaChild } && eaChild == current)
            {
                current = current.Parent;
                continue;
            }
            if (current.Parent is ParenthesizedExpressionSyntax { Expression: { } pChild } && pChild == current)
            {
                current = current.Parent;
                continue;
            }
            return null;
        }
    }

    private static bool IsOperandOfIncrement(SyntaxNode node)
    {
        var current = node;
        while (true)
        {
            if (current.Parent is PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax)
            {
                var op = current.Parent;
                if (op.IsKind(SyntaxKind.PreIncrementExpression)
                    || op.IsKind(SyntaxKind.PostIncrementExpression)
                    || op.IsKind(SyntaxKind.PreDecrementExpression)
                    || op.IsKind(SyntaxKind.PostDecrementExpression))
                    return true;
            }
            if (current.Parent is MemberAccessExpressionSyntax { Expression: { } maChild } && maChild == current)
            {
                current = current.Parent;
                continue;
            }
            if (current.Parent is ElementAccessExpressionSyntax { Expression: { } eaChild } && eaChild == current)
            {
                current = current.Parent;
                continue;
            }
            if (current.Parent is ParenthesizedExpressionSyntax { Expression: { } pChild } && pChild == current)
            {
                current = current.Parent;
                continue;
            }
            return false;
        }
    }

    private static bool IsReceiverOfMemberInvocation(SyntaxNode node)
    {
        var current = node;
        while (true)
        {
            if (current.Parent is InvocationExpressionSyntax)
                return true;
            if (current.Parent is MemberAccessExpressionSyntax { Expression: { } maChild } && maChild == current)
            {
                current = current.Parent;
                continue;
            }
            if (current.Parent is ElementAccessExpressionSyntax { Expression: { } eaChild } && eaChild == current)
            {
                current = current.Parent;
                continue;
            }
            if (current.Parent is ParenthesizedExpressionSyntax { Expression: { } pChild } && pChild == current)
            {
                current = current.Parent;
                continue;
            }
            // 条件接收 x?.M() 会把调用结果变为 null 传播，实例态作接收者同样保守按写。
            if (current.Parent is ConditionalAccessExpressionSyntax { Expression: { } caChild } && caChild == current)
                return true;
            return false;
        }
    }

    private static ExpressionSyntax RootOf(ExpressionSyntax expression)
    {
        var current = expression;
        while (true)
        {
            switch (current.Parent)
            {
                case MemberAccessExpressionSyntax { Expression: { } ma } when ma == current:
                    current = ma; continue;
                case ElementAccessExpressionSyntax { Expression: { } ea } when ea == current:
                    current = ea; continue;
                case ParenthesizedExpressionSyntax { Expression: { } pe } when pe == current:
                    current = pe; continue;
                default:
                    return current;
            }
        }
    }

    private static bool IsInsideNameOf(SyntaxNode node)
    {
        foreach (var invocation in node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" }
                && invocation.ArgumentList.Arguments.Any(a =>
                    a.Expression == node || a.Expression.DescendantNodesAndSelf().Contains(node)))
                return true;
        }
        return false;
    }

    private static bool ContainsMethod(
        IReadOnlyCollection<IMethodSymbol> methods,
        IMethodSymbol candidate)
    {
        foreach (var method in methods)
        {
            if (SymbolEqualityComparer.Default.Equals(method, candidate)
                || SymbolEqualityComparer.Default.Equals(method.OriginalDefinition, candidate))
                return true;
        }
        return false;
    }
}
