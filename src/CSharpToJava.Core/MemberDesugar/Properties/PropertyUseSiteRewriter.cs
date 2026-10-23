using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Properties;

/// <summary>
/// PD-6/PD-7/PD-8/PD-9：使用点改写 + 声明展开插入（单遍，绑定一律取原始 SemanticModel）。
/// 读/写/复合/++--/方法组/base/?.、构造器直写后备字段、对象初始化器临时提升、nameof 字面量化。
/// </summary>
internal sealed class PropertyUseSiteRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    // 键为 PropertyPlan.SiteKey（跨编译稳定）：本改写器可能在 finalizer B 档删除后的
    // 重解析模型上运行，不能直接用 ISymbol 引用相等（与 IndexerUseSiteRewriter 同因）。
    private readonly Dictionary<string, PropertyPlan> _active;
    private readonly HashSet<string> _knownKeys;
    private readonly HashSet<string> _candidateNames;
    // C27 M2：reshape 后位置属性在重解析模型里已不存在，其残余使用点靠
    // 「名 + 接收者类型显示串」回退认领（仅对 IsPositionalRecordMember plan）。
    private readonly Dictionary<string, List<PropertyPlan>> _positionalByName;
    private readonly MemberDesugarOptions _options;
    private readonly MemberDesugarStatistics _stats;
    private readonly List<MemberDesugarDiagnostic> _diagnostics;
    private readonly string _filePath;
    private int _tempCounter;

    public PropertyUseSiteRewriter(
        SemanticModel model,
        IReadOnlyList<PropertyPlan> plannedPlans,
        IReadOnlyList<PropertyPlan> allPlans,
        MemberDesugarOptions options,
        MemberDesugarStatistics stats,
        List<MemberDesugarDiagnostic> diagnostics,
        string filePath)
    {
        _model = model;
        _active = new Dictionary<string, PropertyPlan>(StringComparer.Ordinal);
        foreach (var p in plannedPlans) _active[PropertyPlan.SiteKey(p.Property)] = p;
        _knownKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in allPlans) _knownKeys.Add(PropertyPlan.SiteKey(p.Property));
        _candidateNames = plannedPlans.Select(p => p.DeclName).ToHashSet(StringComparer.Ordinal);
        _positionalByName = plannedPlans
            .Where(p => p.IsPositionalRecordMember)
            .GroupBy(p => p.DeclName)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
        _filePath = filePath;
    }

    // ---- 声明展开（PD-1..PD-5/PD-10）+ residual 闸门（PD-12）----

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node) => VisitTypeLike(node);
    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node) => VisitTypeLike(node);
    public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) => VisitTypeLike(node);
    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node) => VisitTypeLike(node);

    // ---- 语句拼接：提升产物块拆入外层作用域（`var x = new C { .. }` 的 x 必须对后续语句可见）----

    public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
    {
        var members = new List<MemberDeclarationSyntax>();
        foreach (var member in node.Members)
        {
            if (member is GlobalStatementSyntax global)
            {
                if (Visit(global.Statement) is not StatementSyntax visited) continue;
                if (visited is BlockSyntax spliced
                    && spliced.HasAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation.Kind))
                {
                    var parts = Splice(spliced).ToList();
                    parts[0] = parts[0].WithLeadingTrivia(
                        global.GetLeadingTrivia().Concat(parts[0].GetLeadingTrivia()));
                    for (var i = 0; i < parts.Count; i++)
                        members.Add(SyntaxFactory.GlobalStatement(parts[i]));
                    continue;
                }
                members.Add(global.WithStatement(visited));
                continue;
            }
            if (Visit(member) is MemberDeclarationSyntax visitedMember)
                members.Add(visitedMember);
        }
        return node.WithMembers(SyntaxFactory.List(members));
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
        => node.WithStatements(SyntaxFactory.List(SpliceStatements(node.Statements)));

    public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
        => node.WithStatements(SyntaxFactory.List(SpliceStatements(node.Statements)));

    private IEnumerable<StatementSyntax> SpliceStatements(SyntaxList<StatementSyntax> statements)
    {
        foreach (var stmt in statements)
        {
            if (Visit(stmt) is not StatementSyntax visited) continue;
            if (visited is BlockSyntax spliced
                && spliced.HasAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation.Kind))
            {
                foreach (var s in Splice(spliced)) yield return s;
                continue;
            }
            yield return visited;
        }
    }

    /// <summary>拆开提升块并转移首尾 trivia，保持声明在外层作用域可见。</summary>
    private static IEnumerable<StatementSyntax> Splice(BlockSyntax block)
    {
        var statements = block.Statements;
        if (statements.Count == 0) yield break;
        var list = statements.ToList();
        list[0] = list[0].WithLeadingTrivia(
            block.GetLeadingTrivia().Concat(list[0].GetLeadingTrivia().Where(t => t.IsDirective)));
        list[^1] = list[^1].WithTrailingTrivia(block.GetTrailingTrivia());
        foreach (var s in list) yield return s;
    }

    private SyntaxNode? VisitTypeLike(TypeDeclarationSyntax node)
    {
        var newMembers = new List<MemberDeclarationSyntax>();
        foreach (var member in node.Members)
        {
            if (member is PropertyDeclarationSyntax propDecl
                && _model.GetDeclaredSymbol(propDecl) is { } prop
                && _active.TryGetValue(PropertyPlan.SiteKey(prop), out var plan))
            {
                // 先在使用点改写器内改写访问器体（计算属性可能引用同族去糖属性，
                // 如 FullName => FirstName + ..；语义绑定全部走原始树节点）。
                var visitedDecl = (PropertyDeclarationSyntax)Visit(propDecl)!;

                // C50：Expand 之前再把 field 形态归一为「显式字段 + 普通 full 属性」。
                // 归一在 Visit 之后：未改写节点（含 FieldExpression）仍是原始树引用，
                // 结构化替换可命中；字段节点预制，Expander 复用不重复发。
                // 失败（跨阶段漂移）已发跳过诊断，原样保留该成员（整树由编译回滚闭环兜底）。
                FieldDeclarationSyntax? prebuiltField = null;
                if (plan.FieldKeyword != null)
                {
                    if (!FieldKeywordNormalizer.TryNormalize(
                            visitedDecl, propDecl, plan, _model, _options, _stats,
                            _diagnostics, _filePath,
                            out var normalized, out var synthesized))
                    {
                        newMembers.Add(visitedDecl);
                        continue;
                    }
                    visitedDecl = normalized;
                    prebuiltField = synthesized;
                }

                // 展开产物是脱离语法树的拷贝，无法再绑定 —— 特性路由一律走原始声明。
                var expander = new PropertyDeclarationExpander(
                    _model, _options, _stats, _diagnostics, _filePath);
                foreach (var expanded in expander.Expand(visitedDecl, plan, propDecl, prebuiltField))
                    newMembers.Add(expanded);
                continue;
            }

            if (member is PropertyDeclarationSyntax residual
                && _model.GetDeclaredSymbol(residual) is { } residualProp
                && !_knownKeys.Contains(PropertyPlan.SiteKey(residualProp)))
            {
                _diagnostics.Add(new MemberDesugarDiagnostic(
                    _options.Strict ? MemberDesugarSeverity.Error : MemberDesugarSeverity.Warning,
                    MemberDesugarDiagnosticCode.ResidualProperty,
                    $"property '{residualProp.Name}' has neither a plan nor a skip diagnostic " +
                    "(residual gate, PD-12)",
                    _filePath,
                    residual.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    residual.GetLocation().GetLineSpan().StartLinePosition.Character + 1,
                    residualProp.Name));
                // residual 闸门命中是「缺陷信号」（无 plan 的残留属性），绝不等同于
                // 真实跳过：计入 residual 专属桶（摘要行的 residual 字段），不再污染
                // PlansSkipped（曾经的 46 skipped/0 residual 就是此处错记所致）。
                _stats.SkippedByReason.TryGetValue(
                    MemberDesugarDiagnosticCode.ResidualProperty, out var residualProperties);
                _stats.SkippedByReason[MemberDesugarDiagnosticCode.ResidualProperty] =
                    residualProperties + 1;
            }

            if (Visit(member) is MemberDeclarationSyntax visited)
                newMembers.Add(visited);
        }

        var rewritten = node.WithMembers(SyntaxFactory.List(newMembers));

        // C50 FK-9（仅开关开启）：输出树中仍残留 FieldExpression 且归属属性没有任何
        // plan/skip 记录 → CS2J5122。扫描必须走原始（在树）节点：rewritten 是
        // WithMembers 产生的离树红根，其后代节点无法再做语义绑定。
        if (_options.FieldKeyword.Enabled)
            EmitFieldKeywordResiduals(node);

        return rewritten;
    }

    /// <summary>FK-9 residual 闸门：枚举当前类型直接属性成员中的 FieldExpressionSyntax。
    /// 归一/跳过的属性在分析期都留有 plan key（_knownKeys），命中即排除；嵌套类型
    /// 归各自 VisitTypeLike 扫描。</summary>
    private void EmitFieldKeywordResiduals(TypeDeclarationSyntax originalNode)
    {
        foreach (var propSyntax in originalNode.Members
                     .OfType<PropertyDeclarationSyntax>()
                     .Where(p => p.DescendantNodes().OfType<FieldExpressionSyntax>().Any()))
        {
            var residualProp = _model.GetDeclaredSymbol(propSyntax);
            if (residualProp != null
                && _knownKeys.Contains(PropertyPlan.SiteKey(residualProp)))
            {
                // 有 plan 或 infeasible 记录：成功归一，或漂移失败路径已发 5126/5127
                // 等 skip 诊断。
                continue;
            }

            var fe = propSyntax.DescendantNodes().OfType<FieldExpressionSyntax>().First();
            var lineSpan = fe.GetLocation().GetLineSpan();
            _diagnostics.Add(new MemberDesugarDiagnostic(
                _options.Strict ? MemberDesugarSeverity.Error : MemberDesugarSeverity.Warning,
                MemberDesugarDiagnosticCode.FieldKeywordAccessor,
                FieldKeywordDiagnostics.Residual(residualProp?.Name),
                _filePath,
                lineSpan.StartLinePosition.Line + 1,
                lineSpan.StartLinePosition.Character + 1,
                residualProp?.Name));
            _stats.FieldKeywordResidualTokens++;
            _stats.PlansSkipped++;
            _stats.SkippedByReason.TryGetValue(
                MemberDesugarDiagnosticCode.FieldKeywordAccessor, out var n);
            _stats.SkippedByReason[MemberDesugarDiagnosticCode.FieldKeywordAccessor] = n + 1;
        }
    }

    // ---- 语句级：复杂接收者复合赋值/自增自减 + 对象初始化器提升（PD-8/决策 7）----

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        if (node.Expression is AssignmentExpressionSyntax ca
            && !ca.IsKind(SyntaxKind.SimpleAssignmentExpression)
            && TryGetPlan(ca.Left, out var compoundPlan)
            && ca.Left is MemberAccessExpressionSyntax compoundAccess
            && !IsSimpleReceiver(compoundAccess.Expression))
        {
            _stats.CompoundSitesRewritten++;
            var right = (ExpressionSyntax)Visit(ca.Right)!;
            var temp = TempName("recv");
            var tempIdent = Ident(temp);
            var read = Invoke(MemberAccess(tempIdent, compoundPlan.GetterMethodName));
            var bin = Binary(ca.Kind(), ca.OperatorToken.Kind(), read, right);
            var set = Invoke(MemberAccess(tempIdent, compoundPlan.SetterMethodName!), bin);
            return Block(
                DeclVar(temp, (ExpressionSyntax)Visit(compoundAccess.Expression)!),
                SyntaxFactory.ExpressionStatement(set))
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia())
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        }

        if (node.Expression is PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax)
        {
            var operand = node.Expression switch
            {
                PrefixUnaryExpressionSyntax p => p.Operand,
                PostfixUnaryExpressionSyntax p => p.Operand,
                _ => null!,
            };
            if (operand != null && TryGetPlan(operand, out var incPlan)
                && operand is MemberAccessExpressionSyntax incAccess
                && !IsSimpleReceiver(incAccess.Expression))
            {
                _stats.CompoundSitesRewritten++;
                var increment = node.Expression.IsKind(SyntaxKind.PreIncrementExpression)
                    || node.Expression.IsKind(SyntaxKind.PostIncrementExpression);
                var temp = TempName("recv");
                var tempIdent = Ident(temp);
                var read = Invoke(MemberAccess(tempIdent, incPlan.GetterMethodName));
                var bin = Binary(
                    increment ? SyntaxKind.AddAssignmentExpression : SyntaxKind.SubtractAssignmentExpression,
                    increment ? SyntaxKind.PlusToken : SyntaxKind.MinusToken,
                    read, One());
                var set = Invoke(MemberAccess(tempIdent, incPlan.SetterMethodName!), bin);
                return Block(
                    DeclVar(temp, (ExpressionSyntax)Visit(incAccess.Expression)!),
                    SyntaxFactory.ExpressionStatement(set))
                    .WithLeadingTrivia(node.GetLeadingTrivia())
                    .WithTrailingTrivia(node.GetTrailingTrivia())
                    .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
            }
        }

        if (node.Expression is ObjectCreationExpressionSyntax oc && LiftCreation(oc, out var stmts, out _))
            return Block(stmts)
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia())
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);

        // PD-6 链式赋值提升：`a.P = b.Q = v;` / `local = a.P = v;`。setter 返回
        // void，内层赋值改写产物不能充当外层实参（CS1503）。rhs 先存临时（单次
        // 求值），再按 C# 求值顺序（由内向外）逐个 setter；最外层非属性目标保留
        // 赋值形态读回临时。仅拦截 RHS 为赋值链的语句位，`a.P = v;` 常规路径不变。
        if (UnwrapParentheses(node.Expression) is AssignmentExpressionSyntax headAssign
            && headAssign.IsKind(SyntaxKind.SimpleAssignmentExpression)
            && UnwrapParentheses(headAssign.Right) is AssignmentExpressionSyntax
            && LiftChainedPropertyAssignment(node, headAssign, out var chainBlock))
        {
            return chainBlock;
        }

        return base.VisitExpressionStatement(node);
    }

    private bool LiftChainedPropertyAssignment(
        ExpressionStatementSyntax node,
        AssignmentExpressionSyntax head,
        out BlockSyntax block)
    {
        block = null!;
        var sites = new List<(AssignmentExpressionSyntax Assign, PropertyPlan Plan)>();

        // 最外层非属性目标（`local = a.P = v`）保留赋值形态，从其右侧进入链。
        AssignmentExpressionSyntax? outerNonSite = null;
        if (!(TryGetPlan(head.Left, out var headPlan) && headPlan.HasSetter))
        {
            outerNonSite = head;
            head = (AssignmentExpressionSyntax)UnwrapParentheses(head.Right)!;
        }

        var current = (ExpressionSyntax?)head;
        ExpressionSyntax value = current!;
        while (UnwrapParentheses(current) is AssignmentExpressionSyntax assign
            && assign.IsKind(SyntaxKind.SimpleAssignmentExpression))
        {
            if (TryGetPlan(assign.Left, out var plan) && plan.HasSetter)
            {
                sites.Add((assign, plan));
                current = assign.Right;
                value = current;
                continue;
            }

            // 非属性目标：整段赋值保留为值表达式（最内层，合法 C# 赋值表达式）。
            value = assign;
            current = null;
            break;
        }

        if (sites.Count == 0)
            return false;

        var temp = TempName("pval");
        var tempIdent = Ident(temp);
        var visitedValue = (ExpressionSyntax)Visit(value)!;
        // null/default 字面量无法用 var 推断（CS0815）：退回最内层属性类型显式声明。
        StatementSyntax tempDecl;
        if (UnwrapParentheses(value) is LiteralExpressionSyntax literal
            && (literal.IsKind(SyntaxKind.NullLiteralExpression)
                || literal.IsKind(SyntaxKind.DefaultLiteralExpression)))
        {
            tempDecl = DeclVarTyped(NullTempTypeDisplay(sites[^1]), temp, visitedValue);
        }
        else
        {
            tempDecl = DeclVar(temp, visitedValue);
        }

        var lifted = new List<StatementSyntax>
        {
            tempDecl,
        };
        foreach (var (assign, plan) in sites.AsEnumerable().Reverse())
            lifted.Add(SyntaxFactory.ExpressionStatement(BuildSetterCall(assign.Left, plan, tempIdent)));

        if (outerNonSite != null)
            lifted.Add(SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    (ExpressionSyntax)Visit(outerNonSite.Left)!,
                    tempIdent.WithLeadingTrivia(SyntaxFactory.Space))));

        block = Block(lifted)
            .WithLeadingTrivia(node.GetLeadingTrivia())
            .WithTrailingTrivia(node.GetTrailingTrivia())
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        return true;
    }

    public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
    {
        if (CanLiftLocalDeclaration(node)
            && node.Declaration.Variables[0].Initializer?.Value is ObjectCreationExpressionSyntax oc
            && LiftCreation(oc, out var stmts, out var result))
        {
            var declarator = node.Declaration.Variables[0]
                .WithInitializer(SyntaxFactory.EqualsValueClause(result));
            var final = node.WithDeclaration(node.Declaration
                .WithVariables(SyntaxFactory.SeparatedList(new[] { declarator })));
            return Block(stmts.Append(final))
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia())
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        }
        return base.VisitLocalDeclarationStatement(node);
    }

    public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
    {
        // 属性简单赋值作为返回值（numeric-boundary 前缀 ++ 消费形态，如
        // `return (X = X + 1);`）：setter 返回 void，必须提升为
        // `{ var __v = right; set_X(__v); return __v; }`（PD-6 值上下文）。
        if (node.Parent is BlockSyntax or SwitchSectionSyntax
            && UnwrapParentheses(node.Expression) is AssignmentExpressionSyntax pa
            && pa.IsKind(SyntaxKind.SimpleAssignmentExpression)
            && TryGetPlan(pa.Left, out var assignPlan))
        {
            _stats.WriteSitesRewritten++;
            var value = (ExpressionSyntax)Visit(pa.Right)!;
            var temp = TempName("pval");
            var tempIdent = Ident(temp);
            var lifted = new List<StatementSyntax>
            {
                DeclVar(temp, value),
                SyntaxFactory.ExpressionStatement(BuildSetterCall(pa.Left, assignPlan, tempIdent)),
                SyntaxFactory.ReturnStatement(tempIdent.WithLeadingTrivia(SyntaxFactory.Space)),
            };
            return Block(lifted)
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia())
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        }

        if (node.Expression is ObjectCreationExpressionSyntax oc
            && (node.Parent is BlockSyntax || node.Parent is SwitchSectionSyntax)
            && LiftCreation(oc, out var stmts, out var result))
        {
            // return 关键字与表达式之间必须显式留白（return__x 会拼接成单个标识符）。
            var @return = SyntaxFactory.ReturnStatement(
                result.WithLeadingTrivia(SyntaxFactory.Space));
            return Block(stmts.Append(@return))
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia())
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        }
        return base.VisitReturnStatement(node);
    }

    // ---- 箭头体提升（PD-6 值上下文延伸）----
    // 表达式体成员/访问器/lambda 的体直接是属性简单赋值时，setter 返回 void，
    // 不能充当箭头体的值（CS0029）。块化为
    // `{ var __v = rhs; set_X(__v); return __v; }`：赋值表达式的值即赋入值，
    // rhs 只求值一次（先于 setter 调用）。void 宿主的 `=> set_X(rhs)` 本就合法，
    // 保持默认游历。识别与语义绑定一律走原始在树节点，产物块覆盖到已访问节点上。

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
        if (!TryGetArrowAssignment(node.ExpressionBody, node.Body,
                _model.GetDeclaredSymbol(node) is IMethodSymbol { ReturnsVoid: false },
                out var assign, out var plan))
            return visited;
        return visited.WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithBody(BuildArrowAssignmentBlock(assign, plan))
            .WithTrailingTrivia(visited.GetTrailingTrivia())
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
    }

    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        var visited = (LocalFunctionStatementSyntax)base.VisitLocalFunctionStatement(node)!;
        if (!TryGetArrowAssignment(node.ExpressionBody, node.Body,
                _model.GetDeclaredSymbol(node) is IMethodSymbol { ReturnsVoid: false },
                out var assign, out var plan))
            return visited;
        return visited.WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithBody(BuildArrowAssignmentBlock(assign, plan))
            .WithTrailingTrivia(visited.GetTrailingTrivia())
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
    }

    public override SyntaxNode? VisitAccessorDeclaration(AccessorDeclarationSyntax node)
    {
        var visited = (AccessorDeclarationSyntax)base.VisitAccessorDeclaration(node)!;
        if (!TryGetArrowAssignment(node.ExpressionBody, node.Body,
                _model.GetDeclaredSymbol(node) is IMethodSymbol { ReturnsVoid: false },
                out var assign, out var plan))
            return visited;
        return visited.WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithBody(BuildArrowAssignmentBlock(assign, plan))
            .WithTrailingTrivia(visited.GetTrailingTrivia())
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
    }

    public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
    {
        var visited = (SimpleLambdaExpressionSyntax)base.VisitSimpleLambdaExpression(node)!;
        if (!TryGetLambdaAssignment(node, out var assign, out var plan))
            return visited;
        return visited.WithExpressionBody(null)
            .WithBlock(BuildArrowAssignmentBlock(assign, plan))
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
    }

    public override SyntaxNode? VisitParenthesizedLambdaExpression(
        ParenthesizedLambdaExpressionSyntax node)
    {
        var visited =
            (ParenthesizedLambdaExpressionSyntax)base.VisitParenthesizedLambdaExpression(node)!;
        if (!TryGetLambdaAssignment(node, out var assign, out var plan))
            return visited;
        return visited.WithExpressionBody(null)
            .WithBlock(BuildArrowAssignmentBlock(assign, plan))
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
    }

    /// <summary>箭头体站点识别：宿主无块体且（值返回时）箭头表达式剥括号后恰为
    /// 被规划属性的简单赋值。只认直接表达式，嵌套形态不受理。</summary>
    private bool TryGetArrowAssignment(
        ArrowExpressionClauseSyntax? arrow,
        BlockSyntax? blockBody,
        bool returnsValue,
        out AssignmentExpressionSyntax assign,
        out PropertyPlan plan)
    {
        assign = null!;
        plan = null!;
        if (!returnsValue || arrow == null || blockBody != null) return false;
        return TryGetAssignment(arrow.Expression, out assign, out plan);
    }

    /// <summary>表达式 lambda 站点识别：返回类型由转换后的委托 Invoke 决定，
    /// 仅值返回委托需要提升（Action 的裸 setter 调用合法）。</summary>
    private bool TryGetLambdaAssignment(
        LambdaExpressionSyntax node,
        out AssignmentExpressionSyntax assign,
        out PropertyPlan plan)
    {
        assign = null!;
        plan = null!;
        return node.Block == null
               && node.ExpressionBody is { } body
               && _model.GetTypeInfo(node).ConvertedType
                   is INamedTypeSymbol { DelegateInvokeMethod: { ReturnsVoid: false } }
               && TryGetAssignment(body, out assign, out plan);
    }

    private bool TryGetAssignment(
        ExpressionSyntax expression,
        out AssignmentExpressionSyntax assign,
        out PropertyPlan plan)
    {
        assign = null!;
        plan = null!;
        if (UnwrapParentheses(expression) is not AssignmentExpressionSyntax candidate
            || !candidate.IsKind(SyntaxKind.SimpleAssignmentExpression)
            || !TryGetPlan(candidate.Left, out plan)
            || !plan.HasSetter)
            return false;
        assign = candidate;
        return true;
    }

    /// <summary>提升块：rhs 先存临时（单次求值），setter 消费临时，返回赋入值。</summary>
    private BlockSyntax BuildArrowAssignmentBlock(
        AssignmentExpressionSyntax assign, PropertyPlan plan)
    {
        var value = (ExpressionSyntax)Visit(assign.Right)!;
        var temp = TempName("pval");
        var tempIdent = Ident(temp);
        return Block(
            DeclVar(temp, value),
            SyntaxFactory.ExpressionStatement(BuildSetterCall(assign.Left, plan, tempIdent)),
            SyntaxFactory.ReturnStatement(tempIdent.WithLeadingTrivia(SyntaxFactory.Space)));
    }

    // ---- 赋值（PD-6/PD-7）----

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        if (node.Parent is InitializerExpressionSyntax)
            return base.VisitAssignmentExpression(node); // 由提升逻辑接管

        if (!TryGetPlan(node.Left, out var plan))
            return base.VisitAssignmentExpression(node);

        var right = (ExpressionSyntax)Visit(node.Right)!;

        if (node.IsKind(SyntaxKind.SimpleAssignmentExpression))
        {
            // PD-7：无 setter 且写入点位于声明类型构造器内 → 直写后备字段。
            if (!plan.HasSetter && plan.BackingFieldName != null
                && IsConstructorFieldWrite(node, plan))
            {
                _stats.WriteSitesRewritten++;
                return BuildFieldStore(node.Left, plan.BackingFieldName, right);
            }
            _stats.WriteSitesRewritten++;
            return BuildSetterCall(node.Left, plan, right);
        }

        // 复合赋值：仅语句位 + 简单接收者在此处理；复杂接收者在语句级提升。
        if (node.Parent is not ExpressionStatementSyntax)
            return base.VisitAssignmentExpression(node); // 分析期已闸门（5119），保险路径
        if (node.Left is MemberAccessExpressionSyntax sma && !IsSimpleReceiver(sma.Expression))
            return base.VisitAssignmentExpression(node);
        if (node.Left is not (IdentifierNameSyntax or MemberAccessExpressionSyntax))
            return base.VisitAssignmentExpression(node);

        _stats.CompoundSitesRewritten++;
        var read = BuildGetterExpression(node.Left, plan);
        return BuildSetterCall(node.Left, plan,
            Binary(node.Kind(), node.OperatorToken.Kind(), read, right));
    }

    public override SyntaxNode? VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
    {
        if ((node.IsKind(SyntaxKind.PostIncrementExpression) || node.IsKind(SyntaxKind.PostDecrementExpression))
            && TryGetPlan(node.Operand, out var plan)
            && node.Parent is ExpressionStatementSyntax
            && IsSimpleOperand(node.Operand))
        {
            _stats.CompoundSitesRewritten++;
            var increment = node.IsKind(SyntaxKind.PostIncrementExpression);
            var read = BuildGetterExpression(node.Operand, plan);
            return BuildSetterCall(node.Operand, plan,
                Binary(
                    increment ? SyntaxKind.AddAssignmentExpression : SyntaxKind.SubtractAssignmentExpression,
                    increment ? SyntaxKind.PlusToken : SyntaxKind.MinusToken,
                    read, One()));
        }
        return base.VisitPostfixUnaryExpression(node);
    }

    public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
    {
        if ((node.IsKind(SyntaxKind.PreIncrementExpression) || node.IsKind(SyntaxKind.PreDecrementExpression))
            && TryGetPlan(node.Operand, out var plan)
            && node.Parent is ExpressionStatementSyntax
            && IsSimpleOperand(node.Operand))
        {
            _stats.CompoundSitesRewritten++;
            var increment = node.IsKind(SyntaxKind.PreIncrementExpression);
            var read = BuildGetterExpression(node.Operand, plan);
            return BuildSetterCall(node.Operand, plan,
                Binary(
                    increment ? SyntaxKind.AddAssignmentExpression : SyntaxKind.SubtractAssignmentExpression,
                    increment ? SyntaxKind.PlusToken : SyntaxKind.MinusToken,
                    read, One()));
        }
        return base.VisitPrefixUnaryExpression(node);
    }

    // ---- 读站点（PD-6）----

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        // 条件访问接收者形态：a?.X.M() → a?.get_X().M()
        if (node.Expression is MemberBindingExpressionSyntax binding
            && TryGetPlan(binding, out var bindingPlan))
        {
            _stats.ReadSitesRewritten++;
            var invoked = Invoke(SyntaxFactory.MemberBindingExpression(
                SyntaxFactory.Token(SyntaxKind.DotToken),
                SyntaxFactory.IdentifierName(bindingPlan.GetterMethodName)));
            return node.WithExpression(invoked)
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        }

        if (TryGetPlan(node, out var plan)
            && !IsDirectAssignmentLeft(node)
            && !IsInsideDeconstructionLeft(node))
        {
            var recv = (ExpressionSyntax)Visit(node.Expression)!;
            var access = SyntaxFactory.MemberAccessExpression(
                node.Kind(), recv, node.OperatorToken,
                SyntaxFactory.IdentifierName(plan.GetterMethodName));
            if (IsMethodGroupContext(node))
                return access.WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
            _stats.ReadSitesRewritten++;
            return Invoke(access)
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        }
        return base.VisitMemberAccessExpression(node);
    }

    public override SyntaxNode? VisitConditionalAccessExpression(ConditionalAccessExpressionSyntax node)
        => RewriteConditionalAccess(node);

    private SyntaxNode? RewriteConditionalAccess(ConditionalAccessExpressionSyntax node)
    {
        // a?.X → a?.get_X()
        PropertyPlan? tailPlan = null;
        var tailPlanned = node.WhenNotNull is MemberBindingExpressionSyntax tail
            && TryGetPlan(tail, out tailPlan);
        // 链式 a?.X?.Y：内层 ConditionalAccess.Expression 是裸 MemberBinding。
        // 其 Name 槽只能放 SimpleName，基类 VisitMemberBindingExpression 会把
        // Visit(Name) 的结果无条件强转——下放给 VisitIdentifierName 换成
        // Invocation 必抛 InvalidCastException，故中间槽必须在本层整体替换。
        PropertyPlan? headPlan = null;
        var head = node.Expression as MemberBindingExpressionSyntax;
        var headPlanned = head != null && TryGetPlan(head, out headPlan);

        if (!tailPlanned && !headPlanned)
            return base.VisitConditionalAccessExpression(node);

        var headGetter = headPlanned
            ? Invoke(SyntaxFactory.MemberBindingExpression(
                SyntaxFactory.Token(SyntaxKind.DotToken),
                SyntaxFactory.IdentifierName(headPlan!.GetterMethodName)))
            : null;

        // CS0023 折叠：链式 a?.X?.rest 的中间槽 X 是「非空值类型」属性时，
        // 去糖后的 get_X() 返回不可空 struct，其后再挂 ?. 非法（且多余——
        // 外层首个 ?. 已短路整条尾链）。把本层降为普通点接尾链并入外层：
        // a?.get_X()?.rest → a?.get_X().rest。引用类型/可空值类型槽保留 ?.。
        if (headPlanned && BindingTypeIsNonNullableValue(head!))
        {
            var tailForMerge = tailPlanned
                ? Invoke(SyntaxFactory.MemberBindingExpression(
                    SyntaxFactory.Token(SyntaxKind.DotToken),
                    SyntaxFactory.IdentifierName(tailPlan!.GetterMethodName)))
                : (ExpressionSyntax)Visit(node.WhenNotNull)!;
            var dotted = AppendBindingAsDotted(headGetter!, tailForMerge);
            if (dotted != null)
            {
                _stats.ReadSitesRewritten++;
                if (tailPlanned) _stats.ReadSitesRewritten++;
                return dotted.WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
            }
        }

        var receiver = headPlanned
            ? headGetter!
            : (ExpressionSyntax)Visit(node.Expression)!;

        var whenNotNull = tailPlanned
            ? Invoke(SyntaxFactory.MemberBindingExpression(
                SyntaxFactory.Token(SyntaxKind.DotToken),
                SyntaxFactory.IdentifierName(tailPlan!.GetterMethodName)))
            : (ExpressionSyntax)Visit(node.WhenNotNull)!;

        if (headPlanned) _stats.ReadSitesRewritten++;
        if (tailPlanned) _stats.ReadSitesRewritten++;
        return SyntaxFactory.ConditionalAccessExpression(receiver, whenNotNull)
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
    }

    /// <summary>MemberBinding 槽绑定的成员类型是否为非空值类型（原始模型）。</summary>
    private bool BindingTypeIsNonNullableValue(MemberBindingExpressionSyntax binding)
    {
        var type = _model.GetTypeInfo(binding).Type;
        return type is { IsValueType: true } t
               && t.NullableAnnotation != NullableAnnotation.Annotated;
    }

    /// <summary>
    /// 把外层 ?. 的绑定槽以普通点接方式挂到内层尾表达式：
    /// .Name → tail.Name；.M(args) → tail.M(args)；[args] → tail[args]。
    /// 无法识别的形态返回 null（调用方保留原 ?.)。
    /// </summary>
    private static ExpressionSyntax? AppendBindingAsDotted(
        ExpressionSyntax innerTail, ExpressionSyntax outerWhenNotNull)
    {
        switch (outerWhenNotNull)
        {
            case MemberBindingExpressionSyntax binding:
                return SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression, innerTail,
                    SyntaxFactory.Token(SyntaxKind.DotToken), binding.Name);

            case InvocationExpressionSyntax invocation
                when invocation.Expression is MemberBindingExpressionSyntax invokedBinding:
                return invocation.WithExpression(SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression, innerTail,
                    SyntaxFactory.Token(SyntaxKind.DotToken), invokedBinding.Name));

            case ElementBindingExpressionSyntax elementBinding:
                return SyntaxFactory.ElementAccessExpression(
                    innerTail, elementBinding.ArgumentList);

            default:
                return null;
        }
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        // 特性命名实参 (Foo = x) / 调用命名实参 (foo: x) 左侧的标识符不是属性
        // 读站点：它绑定到特性字段/属性或方法参数，绝不能改写为 get_ 调用——
        // 替换会把 NameEquals 的 Name 槽塞成 InvocationExpression，默认
        // VisitNameEquals 的无条件强转随即抛 InvalidCastException。
        if (node.Parent is NameEqualsSyntax or NameColonSyntax)
            return base.VisitIdentifierName(node);

        // 成员访问的「名称槽」（a.Property 中的 Property）由
        // VisitMemberAccessExpression 统一改写；但隐式 this 属性作接收者时
        // （Property.ToCharArray() 中的 Property）外层访问不是被规划属性、
        // 访问层不会兜底，此处必须照常改写，否则声明已去属性化而读站点残留，
        // 整树验证报 CS0103。
        if (node.Parent is MemberAccessExpressionSyntax parentAccess
            && parentAccess.Name == node)
        {
            return base.VisitIdentifierName(node);
        }

        if (!_candidateNames.Contains(node.Identifier.ValueText))
            return base.VisitIdentifierName(node);

        if (TryGetPlan(node, out var plan)
            && !IsDirectAssignmentLeft(node)
            && !IsInsideDeconstructionLeft(node))
        {
            if (IsMethodGroupContext(node))
                return SyntaxFactory.IdentifierName(plan.GetterMethodName)
                    .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
            _stats.ReadSitesRewritten++;
            return Invoke(SyntaxFactory.IdentifierName(plan.GetterMethodName))
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        }
        return base.VisitIdentifierName(node);
    }

    // ---- nameof（PD-9 / 决策 11）：nameof(X)/nameof(C.X) → "X" ----
    // 例外：xUnit 的 [MemberData(nameof(X))] 会按名字在编译期/运行期解析成员，
    // 属性降形后原名已不存在（展开为 get_X 方法），此时必须字面量化到 get_X，
    // 否则 xUnit1015「MemberData must reference an existing member」编译失败。
    // （MemberData 的方法形态本就被 xUnit 支持，运行期可正常取数。）

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        if (node.Expression is IdentifierNameSyntax nameSyntax
            && nameSyntax.Identifier.ValueText == "nameof"
            && node.ArgumentList.Arguments.Count == 1
            && TryGetPlan(node.ArgumentList.Arguments[0].Expression, out var plan))
        {
            _stats.NameofLiteralized++;
            var literal = IsMemberDataArgument(node) ? plan.GetterMethodName : plan.DeclName;
            return SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal(literal))
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        }
        return base.VisitInvocationExpression(node);
    }

    /// <summary>
    /// True when the <c>nameof</c> invocation is an argument of a
    /// <c>[MemberData(...)]</c> attribute — the one consumer that resolves the
    /// literal against the post-desugar member set.
    /// </summary>
    private static bool IsMemberDataArgument(InvocationExpressionSyntax invocation)
    {
        for (SyntaxNode? current = invocation.Parent; current != null; current = current.Parent)
        {
            if (current is AttributeArgumentSyntax argument)
            {
                // AttributeSyntax → AttributeArgumentListSyntax → AttributeArgumentSyntax
                return argument.Parent?.Parent is AttributeSyntax attribute
                    && AttributeName(attribute.Name) is "MemberData" or "MemberDataAttribute";
            }

            // 离开表达式区域仍未命中属性参数 → 普通 nameof 用法。
            if (current is StatementSyntax or ExpressionSyntax)
                return false;
        }
        return false;
    }

    private static string AttributeName(NameSyntax name) => name switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
        _ => string.Empty,
    };

    // ---- 对象初始化器提升（PD-8 / 决策 7）----

    private bool LiftCreation(
        ObjectCreationExpressionSyntax oc, out List<StatementSyntax> stmts, out ExpressionSyntax result)
    {
        stmts = new List<StatementSyntax>();
        result = oc;
        if (oc.Initializer is not { } init || !init.IsKind(SyntaxKind.ObjectInitializerExpression))
            return false;
        var assigns = init.Expressions.OfType<AssignmentExpressionSyntax>().ToList();
        if (!assigns.Any(a => TryGetPlan(a.Left, out _)))
            return false;

        var temp = TempName("init");
        var kept = init.Expressions
            .Where(e => e is not AssignmentExpressionSyntax a || !TryGetPlan(a.Left, out _))
            .ToList();
        // PD-8：只在仍属于语法树的原始节点上做 Visit（TryGetPlan 的语义查询要求
        // 节点在树内，离树克隆会抛 "Syntax node is not within syntax tree"）；
        // 离树的 new 表达式用已访问的部件组装，绝不整体 Visit 克隆。
        var visitedKept = kept
            .Select(e => (ExpressionSyntax)Visit(e)!)
            .ToList();
        var visitedArgs = oc.ArgumentList is { } argList
            ? argList.WithArguments(SyntaxFactory.SeparatedList(
                argList.Arguments.Select(a => (ArgumentSyntax)Visit(a)!)))
            : null;
        var keptCreation = oc
            .WithArgumentList(visitedArgs)
            .WithInitializer(visitedKept.Count > 0
                ? SyntaxFactory.InitializerExpression(
                    SyntaxKind.ObjectInitializerExpression,
                    SyntaxFactory.SeparatedList(visitedKept))
                : null);
        // `new C { .. }` 去掉初始化器后必须补空参数表，否则 `new C` 不是合法表达式。
        if (visitedKept.Count == 0 && keptCreation.ArgumentList == null)
            keptCreation = keptCreation.WithArgumentList(SyntaxFactory.ArgumentList());
        stmts.Add(DeclVar(temp, keptCreation));

        var tempIdent = Ident(temp);
        foreach (var a in assigns)
        {
            if (!TryGetPlan(a.Left, out var plan)) continue;
            AppendLifted(stmts, tempIdent, a, plan);
        }
        result = tempIdent;
        _stats.InitializersRewritten++;
        return true;
    }

    private void AppendLifted(
        List<StatementSyntax> stmts, ExpressionSyntax target,
        AssignmentExpressionSyntax assign, PropertyPlan plan)
    {
        switch (assign.Right)
        {
            case ObjectCreationExpressionSyntax nested when NeedsLift(nested):
            {
                LiftCreation(nested, out var nestedStmts, out var nestedResult);
                stmts.AddRange(nestedStmts);
                stmts.Add(ExprStmt(BuildSetterOn(target, plan, nestedResult)));
                return;
            }

            case InitializerExpressionSyntax nestedInit
                when nestedInit.IsKind(SyntaxKind.ComplexElementInitializerExpression):
            {
                if (nestedInit.Expressions.All(e => e is AssignmentExpressionSyntax))
                {
                    // 成员初始化器 B = { X = 1 }：get_B() 求值一次存临时。
                    var mem = TempName("mem");
                    stmts.Add(DeclVar(mem, BuildGetterOn(target, plan)));
                    var memIdent = Ident(mem);
                    foreach (var e in nestedInit.Expressions.OfType<AssignmentExpressionSyntax>())
                    {
                        if (TryGetPlan(e.Left, out var inner))
                        {
                            stmts.Add(ExprStmt(BuildSetterOn(memIdent, inner,
                                (ExpressionSyntax)Visit(e.Right)!)));
                        }
                        else
                        {
                            stmts.Add(ExprStmt(SyntaxFactory.AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression,
                                PrefixReceiver(e.Left, memIdent),
                                (ExpressionSyntax)Visit(e.Right)!)));
                        }
                    }
                }
                else
                {
                    // 集合初始化器 L = { 1, 2 } / { ["k"] = v }：get_L() 求值一次存临时。
                    var col = TempName("col");
                    stmts.Add(DeclVar(col, BuildGetterOn(target, plan)));
                    var colIdent = Ident(col);
                    foreach (var e in nestedInit.Expressions)
                    {
                        if (e is AssignmentExpressionSyntax ia
                            && ia.Left is ImplicitElementAccessSyntax iei)
                        {
                            stmts.Add(ExprStmt(SyntaxFactory.AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression,
                                SyntaxFactory.ElementAccessExpression(colIdent, iei.ArgumentList),
                                (ExpressionSyntax)Visit(ia.Right)!)));
                        }
                        else
                        {
                            stmts.Add(ExprStmt(Invoke(
                                MemberAccess(colIdent, "Add"),
                                (ExpressionSyntax)Visit(e)!)));
                        }
                    }
                }
                return;
            }

            default:
                stmts.Add(ExprStmt(BuildSetterOn(target, plan, (ExpressionSyntax)Visit(assign.Right)!)));
                return;
        }
    }

    private bool NeedsLift(ObjectCreationExpressionSyntax oc)
        => oc.Initializer is { } init
           && init.IsKind(SyntaxKind.ObjectInitializerExpression)
           && init.Expressions.OfType<AssignmentExpressionSyntax>().Any(a => TryGetPlan(a.Left, out _));

    private static bool CanLiftLocalDeclaration(LocalDeclarationStatementSyntax node)
        => node.Declaration.Variables.Count == 1
           && node.UsingKeyword.IsKind(SyntaxKind.None)
           && node.AwaitKeyword.IsKind(SyntaxKind.None)
           && !node.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword)
               || m.IsKind(SyntaxKind.StaticKeyword));

    // ---- 符号/上下文判定 ----

    private bool TryGetPlan(ExpressionSyntax node, out PropertyPlan plan)
    {
        plan = null!;
        if (node is IdentifierNameSyntax id)
        {
            if (!_candidateNames.Contains(id.Identifier.ValueText)) return false;
        }
        else if (node is MemberAccessExpressionSyntax ma)
        {
            if (!_candidateNames.Contains(ma.Name.Identifier.ValueText)) return false;
        }
        else if (node is MemberBindingExpressionSyntax mb)
        {
            if (!_candidateNames.Contains(mb.Name.Identifier.ValueText)) return false;
        }
        else
        {
            return false;
        }

        var symbolInfo = _model.GetSymbolInfo(node);
        if (symbolInfo.Symbol is IPropertySymbol p)
        {
            // 构造泛型（Box<string>.Value）归一到定义符号（Box<T>.Value）命中计划。
            if (!p.IsDefinition) p = p.OriginalDefinition;
            return _active.TryGetValue(PropertyPlan.SiteKey(p), out plan);
        }

        // C27 M2 回退：reshape 终形里位置属性已降形为字段/get_X/set_X，重解析模型对
        // 残余 X/p.X 不再给出属性符号。仅当该处「什么都没绑定到」（参数/局部/方法等
        // 真实绑定一律不拦——含新发主 ctor 的 : base(X)）时，按名 + 接收者类型链回退。
        if (symbolInfo.Symbol != null || symbolInfo.CandidateSymbols.Length > 0)
            return false;
        return TryPositionalFallback(node, out plan);
    }

    /// <summary>
    /// 位置属性残余使用点回退认领：IdentifierName 取其所在类型为接收者；
    /// MemberAccess 取左接收者静态类型；MemberBinding 取条件接收表达式类型。
    /// 类型比较走 OriginalDefinition 显示串（跨编译稳定，构造泛型归一），
    /// 沿基类链匹配 plan 属性所属类型。
    /// </summary>
    private bool TryPositionalFallback(ExpressionSyntax node, out PropertyPlan plan)
    {
        plan = null!;
        var name = node switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax mb => mb.Name.Identifier.ValueText,
            _ => null,
        };
        if (name == null || !_positionalByName.TryGetValue(name, out var candidates))
            return false;

        INamedTypeSymbol? receiverType = null;
        switch (node)
        {
            case IdentifierNameSyntax:
                receiverType = _model.GetEnclosingSymbol(node.SpanStart)?.ContainingType;
                break;
            case MemberAccessExpressionSyntax ma:
                receiverType = _model.GetTypeInfo(ma.Expression).Type as INamedTypeSymbol;
                break;
            case MemberBindingExpressionSyntax:
                var cond = node.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault();
                if (cond != null)
                    receiverType = _model.GetTypeInfo(cond.Expression).Type as INamedTypeSymbol;
                break;
        }
        if (receiverType == null) return false;

        foreach (var candidate in candidates)
        {
            var ownerFqn = candidate.Property.ContainingType?.OriginalDefinition.ToDisplayString();
            if (ownerFqn == null) continue;
            for (var b = receiverType.OriginalDefinition; b != null; b = b.BaseType)
            {
                if (b.ToDisplayString() == ownerFqn)
                {
                    plan = candidate;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>方法组上下文：转换目标是委托而表达式本身类型不是委托（如 Func&lt;int&gt; f = c.X）。</summary>
    private bool IsMethodGroupContext(ExpressionSyntax node)
    {
        var info = _model.GetTypeInfo(node);
        if (info.ConvertedType is not { TypeKind: TypeKind.Delegate }) return false;
        return info.Type is not { TypeKind: TypeKind.Delegate };
    }

    private static bool IsDirectAssignmentLeft(SyntaxNode node)
        => node.Parent is AssignmentExpressionSyntax a && ReferenceEquals(a.Left, node);

    private static ExpressionSyntax? UnwrapParentheses(ExpressionSyntax? expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;
        return expression;
    }

    private static bool IsInsideDeconstructionLeft(ExpressionSyntax node)
        => node.Ancestors().OfType<TupleExpressionSyntax>()
               .Any(t => t.Parent is AssignmentExpressionSyntax a && ReferenceEquals(a.Left, t))
           || node.Ancestors().OfType<ParenthesizedExpressionSyntax>()
               .Any(p => p.Parent is AssignmentExpressionSyntax a && ReferenceEquals(a.Left, p));

    /// <summary>副作用无关接收者：标识符/this/base/预定义类型链（复合赋值内联安全）。</summary>
    private static bool IsSimpleReceiver(ExpressionSyntax receiver) => receiver switch
    {
        IdentifierNameSyntax or ThisExpressionSyntax or BaseExpressionSyntax or PredefinedTypeSyntax => true,
        MemberAccessExpressionSyntax ma => IsSimpleReceiver(ma.Expression),
        ParenthesizedExpressionSyntax p => IsSimpleReceiver(p.Expression),
        _ => false,
    };

    private static bool IsSimpleOperand(ExpressionSyntax operand) => operand switch
    {
        IdentifierNameSyntax => true,
        MemberAccessExpressionSyntax ma => IsSimpleReceiver(ma.Expression),
        _ => false,
    };

    /// <summary>PD-7：写入点位于声明类型的（静态）构造器内且接收者是 this/隐式。</summary>
    private bool IsConstructorFieldWrite(AssignmentExpressionSyntax node, PropertyPlan plan)
    {
        if (_model.GetEnclosingSymbol(node.SpanStart) is not IMethodSymbol m
            || m.MethodKind != MethodKind.Constructor
            // 含入类型按显示串比较：finalizer 重解析后两侧符号来自不同编译，
            // SymbolEqualityComparer 不相等（构造泛型归一 OriginalDefinition）。
            || m.ContainingType?.OriginalDefinition.ToDisplayString()
                != plan.Property.ContainingType?.OriginalDefinition.ToDisplayString()
            || m.IsStatic != plan.Property.IsStatic)
            return false;
        return node.Left switch
        {
            IdentifierNameSyntax => !plan.Property.IsStatic,
            MemberAccessExpressionSyntax ma => ma.Expression is ThisExpressionSyntax,
            _ => false,
        };
    }

    // ---- 表达式构造 ----

    private string TempName(string hint) => MemberDesugarSyntaxFacts.TempName(ref _tempCounter, hint);

    private static IdentifierNameSyntax Ident(string name)
        => SyntaxFactory.IdentifierName(name);

    /// <summary>实例后备字段访问：显式 this.field，规避与形参/局部变量重名。</summary>
    private static MemberAccessExpressionSyntax ThisField(string fieldName)
        => SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.ThisExpression(),
            SyntaxFactory.Token(SyntaxKind.DotToken),
            SyntaxFactory.IdentifierName(fieldName));

    private static LiteralExpressionSyntax One()
        => SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(1));

    private static BinaryExpressionSyntax Binary(
        SyntaxKind assignmentKind, SyntaxKind operatorKind,
        ExpressionSyntax left, ExpressionSyntax right)
    {
        // 复合赋值的 RHS 原样作为二元右操作数时，优先级低于算术/位运算的表达式
        // （条件、合并）必须补括号：否则 `a += c ? x : y` 降形后会重解析为
        // `(a + c) ? x : y`（CS0029）。
        if (right.IsKind(SyntaxKind.ConditionalExpression)
            || right.IsKind(SyntaxKind.CoalesceExpression))
        {
            right = SyntaxFactory.ParenthesizedExpression(right);
        }

        var binaryKind = ToBinaryKind(assignmentKind);
        return SyntaxFactory.BinaryExpression(binaryKind, left,
            SyntaxFactory.Token(ToBinaryToken(binaryKind)), right);
    }

    private static SyntaxKind ToBinaryToken(SyntaxKind binaryKind) => binaryKind switch
    {
        SyntaxKind.AddExpression => SyntaxKind.PlusToken,
        SyntaxKind.SubtractExpression => SyntaxKind.MinusToken,
        SyntaxKind.MultiplyExpression => SyntaxKind.AsteriskToken,
        SyntaxKind.DivideExpression => SyntaxKind.SlashToken,
        SyntaxKind.ModuloExpression => SyntaxKind.PercentToken,
        SyntaxKind.BitwiseAndExpression => SyntaxKind.AmpersandToken,
        SyntaxKind.BitwiseOrExpression => SyntaxKind.BarToken,
        SyntaxKind.ExclusiveOrExpression => SyntaxKind.CaretToken,
        SyntaxKind.LeftShiftExpression => SyntaxKind.LessThanLessThanToken,
        SyntaxKind.RightShiftExpression => SyntaxKind.GreaterThanGreaterThanToken,
        SyntaxKind.UnsignedRightShiftExpression => SyntaxKind.GreaterThanGreaterThanGreaterThanToken,
        SyntaxKind.CoalesceExpression => SyntaxKind.QuestionQuestionToken,
        _ => SyntaxKind.PlusToken,
    };

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
        SyntaxKind.CoalesceAssignmentExpression => SyntaxKind.CoalesceExpression,
        _ => SyntaxKind.AddExpression,
    };

    private static InvocationExpressionSyntax Invoke(ExpressionSyntax e, ExpressionSyntax? arg = null)
        => SyntaxFactory.InvocationExpression(e, arg == null
            ? SyntaxFactory.ArgumentList()
            : SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] { SyntaxFactory.Argument(arg) })));

    private static MemberAccessExpressionSyntax MemberAccess(ExpressionSyntax recv, string name)
        => SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression, recv,
            SyntaxFactory.Token(SyntaxKind.DotToken), SyntaxFactory.IdentifierName(name));

    private static BlockSyntax Block(params StatementSyntax[] stmts)
        => SyntaxFactory.Block(stmts);

    private static BlockSyntax Block(IEnumerable<StatementSyntax> stmts)
        => SyntaxFactory.Block(stmts);

    private static ExpressionStatementSyntax ExprStmt(ExpressionSyntax e)
        => SyntaxFactory.ExpressionStatement(e);

    private static LocalDeclarationStatementSyntax DeclVar(string name, ExpressionSyntax value)
        => DeclVarTyped("var", name, value);

    private static LocalDeclarationStatementSyntax DeclVarTyped(
        string typeDisplay, string name, ExpressionSyntax value)
        => SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                // "var" 与标识符之间必须显式留白：无 trivia 的相邻标识符会拼接成
                // 单个 token（var__cs2j_init0），重解析后语义破坏。
                SyntaxFactory.ParseTypeName(typeDisplay).WithTrailingTrivia(SyntaxFactory.Space),
                SyntaxFactory.SeparatedList(new[]
                {
                    SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(name))
                        .WithInitializer(SyntaxFactory.EqualsValueClause(value))
                })));

    /// <summary>null/default 临时声明类型：站点绑定类型优先（泛型属性声明的
    /// TypeDisplay 可能是裸类型参数名 T，不能作显式类型），退回符号类型显示。</summary>
    private string NullTempTypeDisplay((AssignmentExpressionSyntax Assign, PropertyPlan Plan) innermost)
    {
        if (_model.GetTypeInfo(innermost.Assign.Left).Type is { } bound
            && bound.TypeKind != TypeKind.Error)
            return bound.ToDisplayString();
        var declared = innermost.Plan.Property.Type;
        if (declared.TypeKind == TypeKind.TypeParameter) return "object";
        return declared.ToDisplayString();
    }

    /// <summary>左值 → set 调用：X=v / recv.X=v / base.X=v → set_X(v) / recv.set_X(v) / base.set_X(v)。</summary>
    private ExpressionSyntax BuildSetterCall(
        ExpressionSyntax left, PropertyPlan plan, ExpressionSyntax value)
    {
        switch (left)
        {
            case IdentifierNameSyntax:
                return Invoke(Ident(plan.SetterMethodName!), value)
                    .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
            case MemberAccessExpressionSyntax ma:
            {
                var recv = (ExpressionSyntax)Visit(ma.Expression)!;
                return Invoke(SyntaxFactory.MemberAccessExpression(
                        ma.Kind(), recv, SyntaxFactory.Token(SyntaxKind.DotToken),
                        Ident(plan.SetterMethodName!)), value)
                    .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
            }
            default:
                return Invoke(Ident(plan.SetterMethodName!), value);
        }
    }

    private ExpressionSyntax BuildGetterExpression(ExpressionSyntax left, PropertyPlan plan)
    {
        switch (left)
        {
            case IdentifierNameSyntax:
                return Invoke(Ident(plan.GetterMethodName));
            case MemberAccessExpressionSyntax ma:
            {
                var recv = (ExpressionSyntax)Visit(ma.Expression)!;
                return Invoke(SyntaxFactory.MemberAccessExpression(
                    ma.Kind(), recv, SyntaxFactory.Token(SyntaxKind.DotToken),
                    Ident(plan.GetterMethodName)));
            }
            default:
                return Invoke(Ident(plan.GetterMethodName));
        }
    }

    private static ExpressionSyntax BuildSetterOn(
        ExpressionSyntax target, PropertyPlan plan, ExpressionSyntax value)
        => Invoke(MemberAccess(target, plan.SetterMethodName!), value)
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);

    private static ExpressionSyntax BuildGetterOn(ExpressionSyntax target, PropertyPlan plan)
        => Invoke(MemberAccess(target, plan.GetterMethodName))
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);

    /// <summary>PD-7：构造器内直写后备字段。X=v → x=v；this.X=v → this.x=v。</summary>
    private ExpressionSyntax BuildFieldStore(
        ExpressionSyntax left, string fieldName, ExpressionSyntax value)
    {
        var store = left switch
        {
            // 仅实例构造器直写会走到 IdentifierName 臂（见 IsConstructorFieldWrite）；
            // 显式 this. 限定避免字段名与构造器形参/局部变量重名时自赋值
            // （get-only 自动属性 Limit 的后备字段 limit vs 形参 limit）。
            IdentifierNameSyntax => SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression, ThisField(fieldName), value),
            MemberAccessExpressionSyntax ma => SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    (ExpressionSyntax)Visit(ma.Expression)!,
                    SyntaxFactory.Token(SyntaxKind.DotToken),
                    Ident(fieldName)),
                value),
            _ => SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression, Ident(fieldName), value),
        };
        return store.WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
    }

    /// <summary>嵌套成员初始化器的左值改挂在临时接收者上：X → __mem.X；X.Y → __mem.X.Y。</summary>
    private static ExpressionSyntax PrefixReceiver(ExpressionSyntax left, ExpressionSyntax prefix)
        => left switch
        {
            IdentifierNameSyntax id => SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression, prefix,
                SyntaxFactory.Token(SyntaxKind.DotToken), id),
            MemberAccessExpressionSyntax ma => SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                PrefixReceiver(ma.Expression, prefix),
                SyntaxFactory.Token(SyntaxKind.DotToken), ma.Name),
            _ => left,
        };
}

