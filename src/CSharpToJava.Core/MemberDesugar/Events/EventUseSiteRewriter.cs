using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Events;

/// <summary>
/// EV-10..EV-15 使用点改写 + EV-1..EV-9 声明展开编排（单遍）：
/// 类内 field-like 组合 → 字段 = CsPrelude.Combine/Remove；类外/custom → add_X/remove_X 调用；
/// 触发/判空/重置/类内 nameof 零改写（同名字段自动绑定，D3）；nameof 字面量化（EV-14）；
/// residual 闸门（CS2J5158，模式同 C11 PD-12）。
/// 本改写器在「先属性后事件」的第二阶段经重解析树运行（设计 §3.2）：属性改写产物是脱离
/// 原树的红色节点，语义查询必须绑定到自有模型；plan 归属键 = 「含类型::事件名」显示串
/// ——符号实例跨编译不通用，显示串键稳定。
/// </summary>
internal sealed class EventUseSiteRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly Dictionary<string, EventPlan> _active;
    private readonly HashSet<string> _knownKeys;
    private readonly MemberDesugarOptions _options;
    private readonly MemberDesugarStatistics _stats;
    private readonly List<MemberDesugarDiagnostic> _diagnostics;
    private readonly string _filePath;

    public EventUseSiteRewriter(
        SemanticModel model,
        IReadOnlyList<EventPlan> plannedPlans,
        IReadOnlyList<EventPlan> allPlans,
        MemberDesugarOptions options,
        MemberDesugarStatistics stats,
        List<MemberDesugarDiagnostic> diagnostics,
        string filePath)
    {
        _model = model;
        _active = new Dictionary<string, EventPlan>(StringComparer.Ordinal);
        foreach (var p in plannedPlans)
            _active.TryAdd(KeyOf(p.Event), p); // 防御：异常输入下重复键不致命
        _knownKeys = allPlans.Select(p => KeyOf(p.Event)).ToHashSet(StringComparer.Ordinal);
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
        _filePath = filePath;
    }

    /// <summary>plan 归属键：含类型完全限定显示串 + 事件符号名（显式接口实现的 Name 是
    /// "IFoo.X" 形态，两端一致即可）。符号实例跨 ReplaceSyntaxTree 编译不通用，显示串稳定。</summary>
    private static string KeyOf(IEventSymbol evt)
        => EventRule.KeyOf(evt);

    // ---- 声明展开编排（EV-1..EV-9）+ residual 闸门（CS2J5158）----

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node) => VisitTypeLike(node);
    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node) => VisitTypeLike(node);
    public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) => VisitTypeLike(node);
    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node) => VisitTypeLike(node);

    private SyntaxNode? VisitTypeLike(TypeDeclarationSyntax node)
    {
        var newMembers = new List<MemberDeclarationSyntax>();
        var expander = new EventDeclarationExpander(_model, _options, _stats, _diagnostics, _filePath);

        foreach (var member in node.Members)
        {
            switch (member)
            {
                case EventFieldDeclarationSyntax fieldDecl:
                    HandleEventFieldDeclaration(fieldDecl, expander, newMembers);
                    continue;

                case EventDeclarationSyntax eventDecl:
                {
                    if (_model.GetDeclaredSymbol(eventDecl) is not IEventSymbol evt) break;
                    var plan = FindPlan(evt);

                    if (plan != null)
                    {
                        // 先在使用点改写器内改写 accessor 体（体内可能引用同族去糖事件，
                        // 如转发到 private field-style 事件），再展开。展开产物是脱离语法树的
                        // 拷贝，无法再绑定 —— 语义绑定（特性路由）一律走重解析树上的原始声明。
                        var visited = (EventDeclarationSyntax)Visit(eventDecl)!;
                        foreach (var expanded in expander.ExpandEventDeclaration(visited, plan, eventDecl))
                            newMembers.Add(expanded);
                        continue;
                    }
                    if (_knownKeys.Contains(KeyOf(evt)))
                        break; // skip/failed plan：声明原样保留
                    EmitResidual(evt.Name, eventDecl.GetLocation());
                    newMembers.Add(eventDecl);
                    continue;
                }
            }

            if (Visit(member) is MemberDeclarationSyntax visitedMember)
                newMembers.Add(visitedMember);
        }
        return node.WithMembers(SyntaxFactory.List(newMembers));
    }

    private void HandleEventFieldDeclaration(
        EventFieldDeclarationSyntax decl, EventDeclarationExpander expander,
        List<MemberDeclarationSyntax> output)
    {
        var planned = new List<(VariableDeclaratorSyntax Visited, EventPlan Plan)>();
        var keptVariables = new List<VariableDeclaratorSyntax>();

        foreach (var variable in decl.Declaration.Variables)
        {
            if (_model.GetDeclaredSymbol(variable) is not IEventSymbol evt)
            {
                keptVariables.Add(variable);
                continue;
            }
            var plan = FindPlan(evt);

            if (plan != null)
            {
                planned.Add(((VariableDeclaratorSyntax)Visit(variable)!, plan));
                continue;
            }
            if (!_knownKeys.Contains(KeyOf(evt)))
                EmitResidual(evt.Name, variable.GetLocation());
            keptVariables.Add(variable); // skip/failed plan：变量原样保留
        }

        var leading = decl.GetLeadingTrivia();
        var first = true;
        foreach (var (visitedVar, plan) in planned)
        {
            foreach (var expanded in plan.HasField
                         ? expander.ExpandFieldVariable(decl, visitedVar, plan, first ? leading : default)
                         : expander.ExpandAbstractSignatures(decl, plan, first ? leading : default))
                output.Add(expanded);
            first = false;
        }

        if (keptVariables.Count == 0) return;

        var keptDecl = decl.WithDeclaration(
            decl.Declaration.WithVariables(SyntaxFactory.SeparatedList(keptVariables)));
        if (planned.Count > 0)
            keptDecl = keptDecl.WithLeadingTrivia(default(SyntaxTriviaList)); // trivia 已随首个生成成员走
        output.Add((MemberDeclarationSyntax)Visit(keptDecl)!);
    }

    private void EmitResidual(string name, Location location)
    {
        var span = location.GetLineSpan();
        _diagnostics.Add(new MemberDesugarDiagnostic(
            _options.Strict ? MemberDesugarSeverity.Error : MemberDesugarSeverity.Warning,
            MemberDesugarDiagnosticCode.ResidualEvent,
            EventDiagnostics.Residual(name),
            _filePath,
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            name));
        _stats.PlansSkipped++;
    }

    // ---- 订阅/退订站点（EV-10/EV-11）----

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        if (!node.IsKind(SyntaxKind.AddAssignmentExpression)
            && !node.IsKind(SyntaxKind.SubtractAssignmentExpression))
            return base.VisitAssignmentExpression(node);
        if (!TryGetPlan(node.Left, out var plan))
            return base.VisitAssignmentExpression(node);

        var right = (ExpressionSyntax)Visit(node.Right)!;
        var isAdd = node.IsKind(SyntaxKind.AddAssignmentExpression);
        var inDeclaringType = InDeclaringType(node, plan);

        if (plan.HasField && inDeclaringType)
        {
            // EV-11：类内 field-like 组合 —— 直接字段组合，复刻绕过虚访问器语义。
            // 事件名即字段名：左值文本不变（Click / this.Click / _c.Click）。
            _stats.EventInClassCombineSitesRewritten++;
            var combine = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.ParseName("Cs2jPrelude.CsPrelude"),
                    SyntaxFactory.IdentifierName(isAdd ? "Combine" : "Remove")),
                SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[]
                {
                    SyntaxFactory.Argument(LeftClone(node.Left)),
                    SyntaxFactory.Argument(right),
                })));
            return SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                LeftClone(node.Left),
                SyntaxFactory.Token(SyntaxKind.EqualsToken).WithTriviaFrom(node.OperatorToken),
                combine)
                .WithTriviaFrom(node)
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        }

        // EV-10：custom 类内 + 一切类外站点 → receiver.add_X/remove_X(right)。
        if (inDeclaringType)
            _stats.EventInClassCombineSitesRewritten++;
        else if (isAdd)
            _stats.EventSubscribeSitesRewritten++;
        else
            _stats.EventUnsubscribeSitesRewritten++;

        return BuildAccessorCall(node.Left, plan, isAdd, right)
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
    }

    /// <summary>左值 → add/remove 调用：E / c.E / this.E / base.不可达（CS0070）/ ((IFoo)x).E。
    /// 接收者表达式原样保留、单次求值（接收者不复制，无副作用面）。</summary>
    private ExpressionSyntax BuildAccessorCall(
        ExpressionSyntax left, EventPlan plan, bool isAdd, ExpressionSyntax right)
    {
        var name = isAdd ? plan.AdderName : plan.RemoverName;
        ExpressionSyntax target;
        switch (left)
        {
            case IdentifierNameSyntax:
                // 类内无接收者（custom 事件）：裸方法调用。
                target = SyntaxFactory.IdentifierName(name);
                break;
            case MemberAccessExpressionSyntax ma:
                target = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    (ExpressionSyntax)Visit(ma.Expression)!,
                    SyntaxFactory.Token(SyntaxKind.DotToken),
                    SyntaxFactory.IdentifierName(name));
                break;
            default:
                target = SyntaxFactory.IdentifierName(name);
                break;
        }
        return SyntaxFactory.InvocationExpression(
            target,
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] { SyntaxFactory.Argument(right) })));
    }

    // ---- nameof（EV-14）：nameof(X)/nameof(C.X)/nameof(IFoo.X) → "X" ----

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        if (node.Expression is IdentifierNameSyntax nameSyntax
            && nameSyntax.Identifier.ValueText == "nameof"
            && node.ArgumentList.Arguments.Count == 1
            && TryGetPlan(node.ArgumentList.Arguments[0].Expression, out var plan))
        {
            _stats.EventNameofLiteralized++;
            return SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal(plan.DeclName))
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        }
        return base.VisitInvocationExpression(node);
    }

    // ---- 符号/上下文判定 ----

    private EventPlan? FindPlan(IEventSymbol evt)
    {
        if (!evt.IsDefinition) evt = (IEventSymbol)evt.OriginalDefinition;
        return _active.TryGetValue(KeyOf(evt), out var plan) ? plan : null;
    }

    private bool TryGetPlan(ExpressionSyntax node, out EventPlan plan)
    {
        plan = null!;
        var name = node switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
            _ => null,
        };
        if (name == null || !_active.Values.Any(p => p.DeclName == name)) return false;
        if (_model.GetSymbolInfo(node).Symbol is not IEventSymbol evt) return false;
        // 构造泛型（Box<string>.E）归一到定义符号（Box<T>.E）才能命中候选。
        if (!evt.IsDefinition) evt = (IEventSymbol)evt.OriginalDefinition;
        var found = FindPlan(evt);
        if (found == null) return false;
        plan = found;
        return true;
    }

    private bool InDeclaringType(SyntaxNode node, EventPlan plan)
    {
        var enclosing = EventFacts.EnclosingType(_model, node);
        return enclosing != null && string.Equals(
            enclosing.ToDisplayString(), plan.Event.ContainingType?.ToDisplayString(), StringComparison.Ordinal);
    }

    /// <summary>事件名即字段名：左值两处出现（赋值目标 + Combine 实参）需两个独立节点实例。</summary>
    private static ExpressionSyntax LeftClone(ExpressionSyntax left)
        => (ExpressionSyntax)SyntaxFactory.ParseExpression(left.ToString());
}
