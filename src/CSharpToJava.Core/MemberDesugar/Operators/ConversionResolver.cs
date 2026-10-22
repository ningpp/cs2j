using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CSharpToJava.Core.MemberDesugar.Operators;

/// <summary>决策 4/OC-4/OC-5：转换站点解析。构造期对整树扫描一次建
/// 「站点表达式节点 → 改写配方」表，重写期由 <see cref="OperatorUseSiteRewriter"/> 的
/// Visit 漏斗按节点引用命中。
///
/// 站点绑定形态（实测 Roslyn 语义，设计 §4.6-4/§13.9）：
/// ① 隐式站点（`int x = w;`、实参、coalesce/条件臂）：绑定的用户转换操作的
///    Syntax == 运算元节点，而 GetOperation(运算元) 返回其**内层**操作、
///    用户转换在 `op.Parent`（同语法）；
/// ② 简单 cast（`(int)w`，转换即整节点）：GetOperation(cast) 即用户转换；
/// ③ 复合 cast（`(byte)s`，用户转换 + 外层标准转换）：GetOperation(cast) 返回 null，
///    用户转换嵌在**最近可绑定祖先**的操作树内（内层为显式 int、外层标准转换 int→byte）；
///    此时保留外层显式 cast（`(byte)Src.ToInt(s)`）。</summary>
internal sealed class ConversionResolver
{
    public sealed record Recipe(
        OperatorPlan Plan,
        bool IsLifted,
        string Qualifier,
        bool NeedsOuterCast,
        string OuterCastType);

    public sealed record Site(
        IConversionOperation Conversion,
        string TargetType,
        bool NeedsOuterCast,
        string OuterCastType);

    private readonly SemanticModel _model;
    private readonly Dictionary<ExpressionSyntax, Recipe> _recipes = new();

    public ConversionResolver(CompilationUnitSyntax root, SemanticModel model,
        IReadOnlyList<OperatorPlan> planned)
    {
        _model = model;
        var byKey = new Dictionary<string, OperatorPlan>(StringComparer.Ordinal);
        foreach (var p in planned)
            byKey[OperatorUseSiteRewriter.PlanKey(p.Operator)] = p;

        foreach (var expr in root.DescendantNodes().OfType<ExpressionSyntax>())
        {
            if (!TryResolveSite(expr, model, out var site)) continue;
            var method = site.Conversion.OperatorMethod!;
            var def = method.IsDefinition ? method : method.OriginalDefinition;
            if (!byKey.TryGetValue(OperatorUseSiteRewriter.PlanKey(def), out var plan)) continue;

            var operand = site.Conversion.Operand;
            var isLifted = operand.Type is INamedTypeSymbol
                {
                    OriginalDefinition.SpecialType: SpecialType.System_Nullable_T
                }
                && !SymbolEqualityComparer.Default.Equals(
                    method.Parameters.FirstOrDefault()?.Type, operand.Type);

            _recipes[expr] = new Recipe(
                plan, isLifted,
                method.ContainingType.ToMinimalDisplayString(model, expr.SpanStart),
                site.NeedsOuterCast, site.OuterCastType);
        }
    }

    public IReadOnlyDictionary<ExpressionSyntax, Recipe> Recipes => _recipes;

    /// <summary>表达式节点是否为用户转换站点（并给出目标类型/外层显式 cast 判定）。</summary>
    internal static bool TryResolveSite(
        ExpressionSyntax expr, SemanticModel model, out Site site)
    {
        site = null!;
        if (!TryFindUserConversion(expr, model, out var conv)) return false;
        var methodReturn = conv.OperatorMethod!.ReturnType;

        var needsCast = false;
        var castType = "";
        var target = FindOuterTarget(expr, model, conv) ?? methodReturn;
        if (!SymbolEqualityComparer.Default.Equals(methodReturn, target))
        {
            var classify = model.Compilation.ClassifyConversion(methodReturn, target);
            if (classify.IsExplicit)
            {
                needsCast = true;
                castType = target.ToMinimalDisplayString(model, expr.SpanStart);
            }
        }
        site = new Site(conv, target.ToDisplayString(), needsCast, castType);
        return true;
    }

    /// <summary>三种绑定形态的用户转换查找（见类注释）。</summary>
    internal static bool TryFindUserConversion(
        ExpressionSyntax expr, SemanticModel model, out IConversionOperation conv)
    {
        conv = null!;
        var op = model.GetOperation(expr);
        if (op is IConversionOperation own && own.OperatorMethod != null)
        {
            conv = own;                                   // ② 简单 cast
            return true;
        }
        if (op?.Parent is IConversionOperation parent && parent.Syntax == expr
            && parent.OperatorMethod != null)
        {
            conv = parent;                                // ① 隐式站点
            return true;
        }
        // ③ 复合 cast（GetOperation 对该节点为 null）：下钻最近可绑定祖先的操作树。
        //    复合形态只在 cast 语法下出现（隐式上下文中外层标准转换不进操作树）。
        if (expr is not CastExpressionSyntax) return false;
        foreach (var ancestor in expr.Ancestors())
        {
            if (model.GetOperation(ancestor) is not { } ancestorOp) continue;
            foreach (var candidate in ancestorOp.DescendantsAndSelf().OfType<IConversionOperation>())
            {
                if (candidate.OperatorMethod != null && candidate.Syntax == expr)
                {
                    conv = candidate;
                    return true;
                }
            }
            return false;
        }
        return false;
    }

    /// <summary>用户转换之上的外层标准转换目标类型（复合 cast）；无则 null。</summary>
    private static ITypeSymbol? FindOuterTarget(
        ExpressionSyntax expr, SemanticModel model, IConversionOperation userConv)
    {
        if (expr is not CastExpressionSyntax) return null;
        foreach (var ancestor in expr.Ancestors())
        {
            if (model.GetOperation(ancestor) is not { } ancestorOp) return null;
            var outer = ancestorOp.DescendantsAndSelf().OfType<IConversionOperation>()
                .FirstOrDefault(c => c.Syntax == expr && c.OperatorMethod == null
                                     && ReferenceEquals(c.Operand, userConv));
            return outer?.Type;
        }
        return null;
    }

    /// <summary>包装：`[cast] Qual.Name(visited)`；提升站点走公式 ①（Task 8）。</summary>
    public ExpressionSyntax Wrap(
        Recipe recipe, ExpressionSyntax visited, int spanStart, MemberDesugarStatistics stats)
    {
        if (recipe.IsLifted)
        {
            stats.OperatorLiftedSitesExpanded++;
            var nullableDisplay = recipe.Plan.Operator.ReturnType.ToDisplayString() + "?";
            return LiftedOperatorExpansion.ExpandLiftedConversion(
                recipe.Qualifier, recipe.Plan.GeneratedMethodName, nullableDisplay, visited);
        }
        stats.OperatorConversionSitesRewritten++;
        var call = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.ParseTypeName(recipe.Qualifier),
                SyntaxFactory.IdentifierName(recipe.Plan.GeneratedMethodName)),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
                new[] { SyntaxFactory.Argument(visited) })));
        return recipe.NeedsOuterCast
            ? SyntaxFactory.CastExpression(
                SyntaxFactory.ParseTypeName(recipe.OuterCastType), call)
            : call;
    }
}
