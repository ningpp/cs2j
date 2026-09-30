using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.EnumDeclaration;

/// <summary>
/// Phase 2b：整树改写被归一 enum 的使用点。分类口径必须与
/// <see cref="EnumDeclarationAnalyzer.UseSiteWalker"/> 一一对应——Analyzer 判可行
/// 的每一类站点这里都有落地改写；Analyzer 判 ED020 的 plan 不会进入 active 集。
/// </summary>
internal sealed class EnumUseSiteRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly Dictionary<INamedTypeSymbol, EnumDeclarationPlan> _active;

    public EnumUseSiteRewriter(SemanticModel model, IEnumerable<EnumDeclarationPlan> activePlans)
    {
        _model = model;
        _active = new(SymbolEqualityComparer.Default);
        foreach (var p in activePlans)
            if (p.Feasible && p.Names != null) _active[p.Symbol] = p;
    }

    // ── 基础工具 ──

    private EnumDeclarationPlan? PlanOf(ITypeSymbol? type)
    {
        var inner = EnumDeclarationSyntaxFacts.UnwrapNullableEnum(type);
        if (inner == null) return null;
        return _active.TryGetValue(inner, out var plan) ? plan : null;
    }

    /// <summary>仅精确（非 Nullable&lt;T&gt;）命中的活动 plan。</summary>
    private EnumDeclarationPlan? PlanExact(ITypeSymbol? type)
        => type is INamedTypeSymbol nt && _active.TryGetValue(nt, out var p) ? p : null;

    /// <summary>
    /// E04 形状判定：表达式是 (integral)enumExpr 强转，且 enumExpr 为活动 plan。
    /// </summary>
    private EnumDeclarationPlan? IntegralCastPlan(ExpressionSyntax? expr)
    {
        if (expr is not CastExpressionSyntax cast) return null;
        var castType = _model.GetTypeInfo(cast.Type).Type;
        if (castType?.SpecialType is not (SpecialType.System_Int32
            or SpecialType.System_UInt32 or SpecialType.System_Int64
            or SpecialType.System_UInt64 or SpecialType.System_Byte
            or SpecialType.System_SByte or SpecialType.System_Int16
            or SpecialType.System_UInt16))
            return null;
        return PlanExact(_model.GetTypeInfo(cast.Expression).Type);
    }

    private static bool IsAnyEnum(ITypeSymbol? type)
        => EnumDeclarationSyntaxFacts.UnwrapNullableEnum(type) != null;

    private static string TypeName(EnumDeclarationPlan plan)
        => "global::" + plan.EnumQualifiedName.Replace('+', '.');

    private static string U(EnumDeclarationPlan plan)
        => EnumDeclarationSyntaxFacts.GetTypeKeyword(plan.Underlying);

    private ExpressionSyntax DefaultOf(EnumDeclarationPlan plan)
        => Annotate(ParseExpression($"{TypeName(plan)}.{plan.Names!.DefaultInstance}"));

    private static ExpressionSyntax Annotate(ExpressionSyntax node)
        => node.WithAdditionalAnnotations(EnumDeclarationSyntaxFacts.RewriteAnnotation);

    private static StatementSyntax Annotate(StatementSyntax node)
        => node.WithAdditionalAnnotations(EnumDeclarationSyntaxFacts.RewriteAnnotation);

    private static ExpressionSyntax Paren(ExpressionSyntax e)
        => e is ParenthesizedExpressionSyntax ? e : ParenthesizedExpression(e);

    private string LiteralFor(EnumDeclarationPlan plan, object value)
        => EnumDeclarationSyntaxFacts.RenderIntegerLiteral(value, plan.Underlying);

    /// <summary>常量模式/标签表达式 → U 字面量（成员引用取 plan 内成员位值）。</summary>
    private bool TryPatternLiteral(ExpressionSyntax expression, EnumDeclarationPlan plan,
        out string literal)
    {
        literal = "0";
        var symbol = _model.GetSymbolInfo(expression).Symbol;
        if (symbol is IFieldSymbol f && SymbolEqualityComparer.Default.Equals(f.ContainingType, plan.Symbol))
        {
            var member = plan.Members.FirstOrDefault(m => m.Name == f.Name);
            if (member == null) return false;
            literal = LiteralFor(plan, member.ConstantValue);
            return true;
        }
        var constant = _model.GetConstantValue(expression);
        if (constant.HasValue && constant.Value != null)
        {
            literal = EnumDeclarationSyntaxFacts.RenderIntegerLiteral(constant.Value, plan.Underlying);
            return true;
        }
        return false;
    }

    // ── 1) 强转 ──

    public override SyntaxNode? VisitEnumDeclaration(EnumDeclarationSyntax node)
    {
        // 归一目标 enum 内部（成员 EqualsValue 等）整体由 Synthesizer 用编译期常量重建，
        // 不做使用点改写；其余 enum 成员初值只能是整数常量，也无改写点。
        // 跳过子树同时保证 EnumDeclarationSyntax 节点引用身份不变（声明替换靠引用匹配）。
        return node;
    }

    public override SyntaxNode? VisitCastExpression(CastExpressionSyntax node)
    {
        // 语义判定必须在 base.Visit 前（新节点尚未进语义树）。
        var targetType = _model.GetTypeInfo(node.Type).Type;
        var sourceType = _model.GetTypeInfo(node.Expression).Type;
        var targetPlan = PlanOf(targetType);
        var sourcePlan = PlanOf(sourceType);

        var visited = (CastExpressionSyntax)base.VisitCastExpression(node)!;
        node = visited;

        if (sourceType?.TypeKind == TypeKind.Dynamic && targetPlan != null) return node;
        if (sourcePlan != null && targetType is INamedTypeSymbol tn
            && tn.ToDisplayString() is "System.IFormattable" or "System.IConvertible"
                or "System.Enum" or "System.ValueType")
        {
            return node;
        }

        if (targetPlan != null && sourcePlan != null)
        {
            // 双问题 enum 互转（同 plan 冗余转换也安全）。
            return Annotate(ParseExpression(
                $"{TypeName(targetPlan)}.{targetPlan.Names!.FromValue}" +
                $"(({U(targetPlan)})({node.Expression.ToFullString().Trim()}.{sourcePlan.Names!.GetValue}()))"));
        }

        if (targetPlan != null
            && sourceType is { SpecialType: var st }
            && EnumDeclarationSyntaxFacts.IntegerSpecialTypes.Contains(st))
        {
            // 整数 → E：FromValue（D14）。
            return Annotate(ParseExpression(
                $"{TypeName(targetPlan)}.{targetPlan.Names!.FromValue}" +
                $"(({U(targetPlan)})({node.Expression.ToFullString().Trim()}))"));
        }

        if (targetPlan != null && IsAnyEnum(sourceType) && sourcePlan == null)
        {
            // 普通（未改写）enum → class 形态：整数桥 (int)(object)。
            return Annotate(ParseExpression(
                $"{TypeName(targetPlan)}.{targetPlan.Names!.FromValue}" +
                $"(({U(targetPlan)})(int)(object)({node.Expression.ToFullString().Trim()}))"));
        }

        if (targetPlan != null && sourceType is { SpecialType:
                SpecialType.System_Object or SpecialType.System_Enum
                or SpecialType.System_ValueType })
        {
            // 拆箱 (E)(object)：class 形态引用转换，零改写。
            return node;
        }

        if (sourcePlan != null && targetType != null
            && EnumDeclarationSyntaxFacts.IntegerSpecialTypes.Contains(targetType.SpecialType))
        {
            // E → 整数：GetValue 后按目标类型再转一层。
            var inner = node.Expression.ToFullString().Trim();
            var call = $"{inner}.{sourcePlan.Names!.GetValue}()";
            var targetText = node.Type.ToFullString().Trim();
            return Annotate(ParseExpression(
                targetText == U(sourcePlan) ? call : $"({targetText})({call})"));
        }

        if (sourcePlan != null && IsAnyEnum(targetType) && targetPlan == null)
        {
            // class 形态 → 普通 enum：装箱桥。
            var targetText = node.Type.ToFullString().Trim();
            return Annotate(ParseExpression(
                $"({targetText})(object)(int)({node.Expression.ToFullString().Trim()}" +
                $".{sourcePlan.Names!.GetValue}())"));
        }

        return node;
    }

    // ── 2) default(E) / default / new E() ──

    public override SyntaxNode? VisitDefaultExpression(DefaultExpressionSyntax node)
    {
        var plan = PlanOf(_model.GetTypeInfo(node).Type)
            ?? PlanOf(_model.GetTypeInfo(node).ConvertedType);
        return plan != null ? DefaultOf(plan) : base.VisitDefaultExpression(node);
    }

    public override SyntaxNode? VisitLiteralExpression(LiteralExpressionSyntax node)
    {
        if (node.IsKind(SyntaxKind.DefaultLiteralExpression))
        {
            var plan = PlanOf(_model.GetTypeInfo(node).ConvertedType)
                ?? PlanOf(_model.GetTypeInfo(node).Type);
            if (plan != null) return DefaultOf(plan);
        }
        return base.VisitLiteralExpression(node);
    }

    public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        var plan = PlanOf(_model.GetTypeInfo(node).Type);
        if (plan != null && node.ArgumentList?.Arguments.Count == 0)
            return DefaultOf(plan);
        return base.VisitObjectCreationExpression(node);
    }

    // ── 3) switch 语句降级为 if 链 ──

    public override SyntaxNode? VisitSwitchStatement(SwitchStatementSyntax node)
    {
        var plan = PlanOf(_model.GetTypeInfo(node.Expression).Type)
            ?? PlanOf(_model.GetTypeInfo(node.Expression).ConvertedType);
        var visited = (SwitchStatementSyntax)base.VisitSwitchStatement(node)!;
        if (plan == null) return visited;

        var gov = visited.Expression.ToFullString().Trim();
        var tmp = UniqueTemp(node, "__cs2jEv");
        var tempDecl = ParseStatement(
            $"{U(plan)}? {tmp} = {gov}?.{plan.Names!.GetValue}();");

        var stmts = new List<StatementSyntax> { Annotate(tempDecl) };
        IfStatementSyntax? chain = null;
        BlockSyntax? fallback = null;

        foreach (var section in visited.Sections)
        {
            var preds = new List<ExpressionSyntax>();
            var isDefault = false;
            ExpressionSyntax? whenClause = null;
            foreach (var label in section.Labels)
            {
                switch (label)
                {
                    case DefaultSwitchLabelSyntax:
                        isDefault = true;
                        break;
                    case CaseSwitchLabelSyntax cs:
                        if (TryPatternLiteral(cs.Value, plan, out var lit1))
                            preds.Add(ParseExpression($"{tmp} == {lit1}"));
                        break;
                    case CasePatternSwitchLabelSyntax cp:
                        if (cp.Pattern is DiscardPatternSyntax) isDefault = true;
                        else preds.Add(PatternPredicate(cp.Pattern, plan, tmp));
                        if (cp.WhenClause != null)
                            whenClause = cp.WhenClause.Condition;
                        break;
                }
            }

            var predicate = preds.Count == 0 ? ParseExpression("false")
                : preds.Count == 1 ? preds[0]
                : preds.Aggregate((a, b) => BinaryExpression(SyntaxKind.LogicalOrExpression,
                    Paren(a), Paren(b)));
            if (whenClause != null)
                predicate = BinaryExpression(SyntaxKind.LogicalAndExpression,
                    Paren(predicate), Paren(whenClause.WithoutTrivia()));

            var body = StripTrailingBreak(section.Statements);
            if (isDefault)
                fallback = body;
            else
                chain = chain == null
                    ? IfStatement(predicate, body)
                    : AddBranch(chain, IfStatement(predicate, body));
        }

        if (fallback != null && chain != null)
            chain = AttachFallback(chain, fallback);
        else if (fallback != null)
            stmts.Add(fallback);
        if (chain != null) stmts.Add(chain);
        return Annotate(Block(stmts));
    }

    /// <summary>把 default 段挂到 else-if 链最深层（不能覆盖已有的 else-if）。</summary>
    private static IfStatementSyntax AttachFallback(IfStatementSyntax chain, BlockSyntax fallback)
    {
        if (chain.Else == null) return chain.WithElse(ElseClause(fallback));
        if (chain.Else.Statement is IfStatementSyntax nested)
            return chain.WithElse(ElseClause(AttachFallback(nested, fallback)));
        return chain;
    }

    private static IfStatementSyntax AddBranch(IfStatementSyntax chain, IfStatementSyntax branch)
    {
        // else 与其嵌套 if 之间必须有空白，否则文本粘连成标识符 elseif。
        var spaced = branch.WithLeadingTrivia(SyntaxFactory.Space);
        if (chain.Else == null) return chain.WithElse(ElseClause(spaced));
        if (chain.Else.Statement is IfStatementSyntax nested)
            return chain.WithElse(ElseClause(AddBranch(nested, branch)));
        return chain;
    }

    private static BlockSyntax StripTrailingBreak(IReadOnlyList<StatementSyntax> statements)
    {
        var list = statements.ToList();
        if (list.Count == 1 && list[0] is BlockSyntax block)
        {
            var inner = block.Statements.ToList();
            if (inner.Count > 0 && inner[^1] is BreakStatementSyntax) inner.RemoveAt(inner.Count - 1);
            return Block(inner);
        }
        if (list.Count > 0 && list[^1] is BreakStatementSyntax) list.RemoveAt(list.Count - 1);
        return Block(list);
    }

    private string UniqueTemp(SyntaxNode anchor, string preferred)
    {
        var pos = anchor.SpanStart;
        var name = preferred;
        for (var i = 0; ; i++)
        {
            var candidate = i == 0 ? name : name + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (_model.LookupSymbols(pos, name: candidate).Length == 0) return candidate;
        }
    }

    // ── 4) switch 表达式降级为条件链 ──

    public override SyntaxNode? VisitSwitchExpression(SwitchExpressionSyntax node)
    {
        var plan = PlanOf(_model.GetTypeInfo(node.GoverningExpression).Type)
            ?? PlanOf(_model.GetTypeInfo(node.GoverningExpression).ConvertedType);
        var visited = (SwitchExpressionSyntax)base.VisitSwitchExpression(node)!;
        if (plan == null) return visited;

        var gov = $"({visited.GoverningExpression.ToFullString().Trim()}?.{plan.Names!.GetValue}())";
        ExpressionSyntax? fallback = null;
        for (var i = visited.Arms.Count - 1; i >= 0; i--)
        {
            var arm = visited.Arms[i];
            if (arm.Pattern is DiscardPatternSyntax)
            {
                fallback = arm.Expression;
                continue;
            }
            var pred = PatternPredicate(arm.Pattern, plan, gov);
            if (arm.WhenClause != null)
                pred = BinaryExpression(SyntaxKind.LogicalAndExpression,
                    Paren(pred), Paren(arm.WhenClause.Condition.WithoutTrivia()));
            var innerFallback = fallback
                ?? ParseExpression("throw new System.Runtime.CompilerServices.SwitchExpressionException()");
            fallback = ConditionalExpression(Paren(pred),
                Paren(arm.Expression.WithoutTrivia()), Paren(innerFallback));
        }

        return Annotate(Paren(fallback ?? ParseExpression("default")));
    }

    /// <summary>
    /// 模式 → 对 U? 值表达式的谓词。const 走 lifted ==（null 自动不匹配）；
    /// NullPattern 仅 switch 临时变量路径用到（is 路径在 <see cref="VisitIsPatternExpression"/>）。
    /// </summary>
    private ExpressionSyntax PatternPredicate(PatternSyntax pattern, EnumDeclarationPlan plan,
        string valueExpression)
    {
        switch (pattern)
        {
            case ParenthesizedPatternSyntax pp:
                return PatternPredicate(pp.Pattern, plan, valueExpression);
            case ConstantPatternSyntax cp when cp.Expression.IsKind(SyntaxKind.NullLiteralExpression):
                return ParseExpression($"(!{valueExpression}.HasValue)");
            case ConstantPatternSyntax cp:
                if (TryPatternLiteral(cp.Expression, plan, out var lit))
                    return ParseExpression($"{valueExpression} == {lit}");
                return ParseExpression("false");
            case DiscardPatternSyntax:
                return ParseExpression("true");
            case BinaryPatternSyntax bp when bp.IsKind(SyntaxKind.OrPattern):
                return BinaryExpression(SyntaxKind.LogicalOrExpression,
                    Paren(PatternPredicate(bp.Left, plan, valueExpression)),
                    Paren(PatternPredicate(bp.Right, plan, valueExpression)));
            case BinaryPatternSyntax bp when bp.IsKind(SyntaxKind.AndPattern):
                return BinaryExpression(SyntaxKind.LogicalAndExpression,
                    Paren(PatternPredicate(bp.Left, plan, valueExpression)),
                    Paren(PatternPredicate(bp.Right, plan, valueExpression)));
            case UnaryPatternSyntax up when up.IsKind(SyntaxKind.NotPattern):
                if (up.Pattern is ConstantPatternSyntax ncp
                    && ncp.Expression.IsKind(SyntaxKind.NullLiteralExpression))
                {
                    return ParseExpression($"({valueExpression}.HasValue)");
                }
                return PrefixUnaryExpression(SyntaxKind.LogicalNotExpression,
                    Paren(PatternPredicate(up.Pattern, plan, valueExpression)));
            default:
                // 声明/递归模式已被 Analyzer 判 ED020（plan 不会在 active 集）。
                return ParseExpression("false");
        }
    }

    // ── 5) is 模式 ──

    public override SyntaxNode? VisitIsPatternExpression(IsPatternExpressionSyntax node)
    {
        var plan = PlanOf(_model.GetTypeInfo(node.Expression).Type);
        var visited = (IsPatternExpressionSyntax)base.VisitIsPatternExpression(node)!;
        if (plan == null) return visited;

        var recv = visited.Expression.ToFullString().Trim();
        var valueExpression = $"({recv}?.{plan.Names!.GetValue}())";
        return Annotate(Paren(IsPatternPredicate(visited.Pattern, plan, recv, valueExpression)));
    }

    private ExpressionSyntax IsPatternPredicate(
        PatternSyntax pattern, EnumDeclarationPlan plan, string recv, string valueExpression)
    {
        switch (pattern)
        {
            case ParenthesizedPatternSyntax pp:
                return IsPatternPredicate(pp.Pattern, plan, recv, valueExpression);
            case ConstantPatternSyntax cp when cp.Expression.IsKind(SyntaxKind.NullLiteralExpression):
                return ParseExpression($"({recv} is null)");
            case ConstantPatternSyntax cp:
                if (TryPatternLiteral(cp.Expression, plan, out var lit))
                    return ParseExpression($"{valueExpression} == {lit}");
                return ParseExpression("false");
            case DiscardPatternSyntax:
                return ParseExpression("true");
            case BinaryPatternSyntax bp when bp.IsKind(SyntaxKind.OrPattern):
                return BinaryExpression(SyntaxKind.LogicalOrExpression,
                    Paren(IsPatternPredicate(bp.Left, plan, recv, valueExpression)),
                    Paren(IsPatternPredicate(bp.Right, plan, recv, valueExpression)));
            case BinaryPatternSyntax bp when bp.IsKind(SyntaxKind.AndPattern):
                return BinaryExpression(SyntaxKind.LogicalAndExpression,
                    Paren(IsPatternPredicate(bp.Left, plan, recv, valueExpression)),
                    Paren(IsPatternPredicate(bp.Right, plan, recv, valueExpression)));
            case UnaryPatternSyntax up when up.IsKind(SyntaxKind.NotPattern):
                if (up.Pattern is ConstantPatternSyntax ncp
                    && ncp.Expression.IsKind(SyntaxKind.NullLiteralExpression))
                {
                    return ParseExpression($"({recv} is not null)");
                }
                return PrefixUnaryExpression(SyntaxKind.LogicalNotExpression,
                    Paren(IsPatternPredicate(up.Pattern, plan, recv, valueExpression)));
            default:
                return ParseExpression("false");
        }
    }

    // ── 6) == / != / &lt; &gt; 比较 ──

    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        var kind = node.Kind();

        // 经典 is 表达式 p is E.M（BinaryExpression/IsExpression）→  lifted 值比较。
        if (kind == SyntaxKind.IsExpression)
        {
            var isPlan = PlanOf(_model.GetTypeInfo(node.Left).Type)
                ?? PlanOf(_model.GetTypeInfo(node.Left).ConvertedType);
            var field = _model.GetSymbolInfo(node.Right).Symbol as IFieldSymbol;
            var isVisited = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;
            if (isPlan == null || field == null
                || !SymbolEqualityComparer.Default.Equals(field.ContainingType, isPlan.Symbol))
            {
                return isVisited;
            }
            var member = isPlan.Members.FirstOrDefault(m => m.Name == field.Name);
            if (member == null) return isVisited;
            var lit = LiteralFor(isPlan, member.ConstantValue);
            var isLeft = isVisited.Left.ToFullString().Trim();
            return Annotate(ParseExpression(
                $"(({isLeft}?.{isPlan.Names!.GetValue}()) == {lit})"));
        }

        if (kind is SyntaxKind.BitwiseAndExpression
            && node.Right is BinaryExpressionSyntax innerEq
            && innerEq.IsKind(SyntaxKind.EqualsExpression)
            && IntegralCastPlan(node.Left) is { } hp
            && IntegralCastPlan(innerEq.Left) == hp
            && IntegralCastPlan(innerEq.Right) == hp)
        {
            // E04 (legacy shape) lowers x.HasFlag(y) to (int)x & (int)y == (int)y
            // without parens (== binds tighter than &); recover the intended
            // grouping and route the cast chain through GetValue().
            var hv = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;
            var heq = (BinaryExpressionSyntax)hv.Right;
            return Annotate(ParseExpression(
                $"(({hv.Left.ToFullString().Trim()} & {heq.Left.ToFullString().Trim()}) " +
                $"== {heq.Right.ToFullString().Trim()})"));
        }

        if (kind is SyntaxKind.EqualsExpression
            && node.Left is ParenthesizedExpressionSyntax andParen
            && andParen.Expression is BinaryExpressionSyntax correctAnd
            && correctAnd.IsKind(SyntaxKind.BitwiseAndExpression)
            && IntegralCastPlan(node.Right) is { } hp2
            && IntegralCastPlan(correctAnd.Left) == hp2
            && IntegralCastPlan(correctAnd.Right) == hp2)
        {
            // E04 (fixed shape) lowers x.HasFlag(y) to (((U)x & (U)y) == (U)y).
            // Route the three cast sites through GetValue() while keeping the
            // now-correct precedence grouping.
            var eqVisited = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;
            var eqAnd = (BinaryExpressionSyntax)
                ((ParenthesizedExpressionSyntax)eqVisited.Left).Expression;
            return Annotate(ParseExpression(
                $"(({eqAnd.Left.ToFullString().Trim()} " +
                $"& {eqAnd.Right.ToFullString().Trim()}) " +
                $"== {eqVisited.Right.ToFullString().Trim()})"));
        }

        if (kind is SyntaxKind.BitwiseAndExpression or SyntaxKind.BitwiseOrExpression
            or SyntaxKind.ExclusiveOrExpression)
        {
            var lp = PlanExact(_model.GetTypeInfo(node.Left).Type);
            var rp = PlanExact(_model.GetTypeInfo(node.Right).Type);
            var bitVisited = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;
            if (lp != null && lp == rp)
            {
                var bop = kind == SyntaxKind.BitwiseAndExpression ? "&"
                    : kind == SyntaxKind.BitwiseOrExpression ? "|" : "^";
                var bl = bitVisited.Left.ToFullString().Trim();
                var br = bitVisited.Right.ToFullString().Trim();
                return Annotate(ParseExpression(
                    $"{TypeName(lp)}.{lp.Names!.FromValue}(({U(lp)})" +
                    $"({bl}.{lp.Names.GetValue}() {bop} {br}.{lp.Names.GetValue}()))"));
            }
            return bitVisited;
        }

        if (kind is SyntaxKind.AddExpression)
        {
            var lp = PlanExact(_model.GetTypeInfo(node.Left).Type);
            var rp = PlanExact(_model.GetTypeInfo(node.Right).Type);
            var addVisited = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;
            if (lp != null && rp != null)
            {
                // 两个 enum 直接相加在 C# 中本就非法；出现在输出链（Console.WriteLine 等）
                // 的实际意图是字符串拼接，归一为 string.Concat(object, object)。
                return Annotate(ParseExpression(
                    $"string.Concat((object){addVisited.Left.ToFullString().Trim()}, " +
                    $"(object){addVisited.Right.ToFullString().Trim()})"));
            }
            return addVisited;
        }

        if (kind is not (SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression
            or SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression
            or SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression))
        {
            return base.VisitBinaryExpression(node);
        }

        var plan = PlanOf(_model.GetTypeInfo(node.Left).Type)
            ?? PlanOf(_model.GetTypeInfo(node.Left).ConvertedType)
            ?? PlanOf(_model.GetTypeInfo(node.Right).Type)
            ?? PlanOf(_model.GetTypeInfo(node.Right).ConvertedType);
        var visited = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;
        if (plan == null) return visited;

        var left = visited.Left.ToFullString().Trim();
        var right = visited.Right.ToFullString().Trim();

        if (kind is SyntaxKind.EqualsExpression)
            return Annotate(ParseExpression($"object.Equals({left}, {right})"));
        if (kind is SyntaxKind.NotEqualsExpression)
            return Annotate(ParseExpression($"!object.Equals({left}, {right})"));

        var op = kind switch
        {
            SyntaxKind.LessThanExpression => "<",
            SyntaxKind.LessThanOrEqualExpression => "<=",
            SyntaxKind.GreaterThanExpression => ">",
            _ => ">=",
        };
        var compareTo = plan.Names!.Method(EnumOptionalMembers.CompareTo, "CompareTo");
        return Annotate(ParseExpression(
            $"({left} is null ? false : {left}.{compareTo}({right}) {op} 0)"));
    }

    // ── 7) System.Enum 静态 API / GetValueOrDefault / Nullable 成员 ──

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
        if (_model.GetSymbolInfo(node).Symbol is not IMethodSymbol method) return visited;

        if (method.ContainingType.ToDisplayString() == "System.Enum" && method.IsStatic)
        {
            var redirected = TryRedirectEnumApi(node, visited, method);
            if (redirected != null) return redirected;
            return visited;
        }

        if (method.Name == "GetValueOrDefault"
            && method.ContainingType.ConstructedFrom.SpecialType
                == SpecialType.System_Nullable_T)
        {
            var p = PlanOf(method.ReceiverType
                ?? method.ContainingType.TypeArguments.FirstOrDefault());
            if (p != null)
            {
                var recv = visited.Expression is MemberAccessExpressionSyntax ma0
                    ? ma0.Expression.ToFullString().Trim()
                    : node.ToString();
                return Annotate(ParseExpression($"({recv} ?? {DefaultText(p)})"));
            }
        }

        return visited;
    }

    private static string DefaultText(EnumDeclarationPlan plan)
        => $"{TypeName(plan)}.{plan.Names!.DefaultInstance}";

    private ExpressionSyntax? TryRedirectEnumApi(
        InvocationExpressionSyntax original, InvocationExpressionSyntax visited,
        IMethodSymbol method)
    {
        var args = original.ArgumentList.Arguments;
        EnumDeclarationPlan? plan = null;
        if (method.TypeArguments.Length > 0)
            plan = PlanOf(method.TypeArguments[0]);
        if (plan == null && args.Count > 0)
        {
            if (args[0].Expression is TypeOfExpressionSyntax tos)
                plan = PlanOf(_model.GetTypeInfo(tos.Type).Type);
        }
        if (plan == null) return null;

        var tn = TypeName(plan);
        var n = plan.Names!;
        // typeof(E) 形式才有可跳过的首参；泛型 Enum.M<E>(...) 首参就是业务参数。
        var skip = args.Count > 0 && args[0].Expression is TypeOfExpressionSyntax ? 1 : 0;
        ExpressionSyntax Call(string methodName, params int[] relativeIndices)
        {
            var argText = string.Join(", ",
                relativeIndices.Select(i => visited.ArgumentList.Arguments[i + skip].ToFullString().Trim()));
            return Annotate(ParseExpression($"{tn}.{methodName}({argText})"));
        }

        switch (method.Name)
        {
            case "GetValues":
                return ParseExpression($"{tn}.{n.Method(EnumOptionalMembers.GetValues, "GetValues")}()");
            case "GetNames":
                return ParseExpression($"{tn}.{n.Method(EnumOptionalMembers.GetNames, "GetNames")}()");
            case "GetName":
                return Call(n.Method(EnumOptionalMembers.GetName, "GetName"), 0);
            case "Parse":
                return Call(n.Method(EnumOptionalMembers.Parse, "Parse"),
                    Enumerable.Range(0, args.Count - skip).ToArray());
            case "TryParse":
                return Call(n.Method(EnumOptionalMembers.TryParse, "TryParse"),
                    Enumerable.Range(0, args.Count - skip).ToArray());
            case "IsDefined":
                return Call(n.Method(EnumOptionalMembers.IsDefinedValue, "IsDefined"), 0);
            case "Format":
                return Call(n.Method(EnumOptionalMembers.Format, "Format"), 0, 1);
            case "ToObject":
                return Call(n.Method(EnumOptionalMembers.ToObject, "ToObject"), 0);
            case "GetUnderlyingType":
                return Annotate(ParseExpression($"typeof({U(plan)})"));
            default:
                return null;
        }
    }

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var visited = (MemberAccessExpressionSyntax)base.VisitMemberAccessExpression(node)!;
        if (_model.GetSymbolInfo(node).Symbol is IPropertySymbol prop
            && prop.ContainingType.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T)
        {
            var inner = prop.ContainingType.TypeArguments.FirstOrDefault() as INamedTypeSymbol;
            if (inner != null && _active.ContainsKey(inner))
            {
                var recv = visited.Expression.ToFullString().Trim();
                if (prop.Name == "HasValue")
                    return Annotate(ParseExpression($"({recv} != null)"));
                if (prop.Name == "Value")
                    return Annotate(ParseExpression($"({recv})"));
            }
        }
        return visited;
    }

    // ── 8) 插值格式槽 {e:G} → {e.ToString("G")} ──

    public override SyntaxNode? VisitInterpolation(InterpolationSyntax node)
    {
        var visited = (InterpolationSyntax)base.VisitInterpolation(node)!;
        if (node.FormatClause == null) return visited;
        var plan = PlanOf(_model.GetTypeInfo(node.Expression).Type);
        if (plan == null) return visited;

        var formatText = node.FormatClause.FormatStringToken.Text;
        return visited.WithExpression(Annotate(ParseExpression(
                $"{visited.Expression.ToFullString().Trim()}.ToString(" +
                $"\"{formatText.Replace("\"", "\\\"")}\")")))
            .WithFormatClause(null);
    }

    // ── 9) const 字段/局部降级（成员引用本身零改写） ──

    public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node)
    {
        var visited = (FieldDeclarationSyntax)base.VisitFieldDeclaration(node)!;
        if (!node.Modifiers.Any(SyntaxKind.ConstKeyword)) return visited;
        if (!DeclarationReferencesActiveEnum(node.Declaration)) return visited;

        var modifiers = visited.Modifiers
            .Where(m => !m.IsKind(SyntaxKind.ConstKeyword))
            .ToList();
        if (!modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
            modifiers.Add(Token(SyntaxKind.StaticKeyword).WithTrailingTrivia(ElasticSpace));
        if (!modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword)))
            modifiers.Add(Token(SyntaxKind.ReadOnlyKeyword).WithTrailingTrivia(ElasticSpace));
        return visited.WithModifiers(TokenList(modifiers))
            .WithAdditionalAnnotations(EnumDeclarationSyntaxFacts.RewriteAnnotation);
    }

    public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
    {
        var visited = (LocalDeclarationStatementSyntax)base.VisitLocalDeclarationStatement(node)!;
        if (!node.Modifiers.Any(SyntaxKind.ConstKeyword)) return visited;
        if (!DeclarationReferencesActiveEnum(node.Declaration)) return visited;

        var modifiers = visited.Modifiers
            .Where(m => !m.IsKind(SyntaxKind.ConstKeyword));
        return visited.WithModifiers(TokenList(modifiers))
            .WithAdditionalAnnotations(EnumDeclarationSyntaxFacts.RewriteAnnotation);
    }

    private bool DeclarationReferencesActiveEnum(VariableDeclarationSyntax declaration)
    {
        foreach (var id in declaration.DescendantNodes().OfType<ExpressionSyntax>())
        {
            if (_model.GetSymbolInfo(id).Symbol is IFieldSymbol f
                && f.HasConstantValue && _active.ContainsKey(f.ContainingType))
            {
                return true;
            }
        }
        return false;
    }
}
