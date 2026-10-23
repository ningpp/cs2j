using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Indexers;

/// <summary>IX-6..IX-12 使用点改写 + 声明展开插入（逐文件单遍，绑定一律取原始
/// SemanticModel，镜像 C11 PropertyUseSiteRewriter）。语句拼接基建与 C11 同构：
/// 提升产物以带 RewriteAnnotation 的 Block 返回，由块/节/编译单元访问器拆入外层作用域。</summary>
internal sealed class IndexerUseSiteRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    // 属性 pass 先行改写后本 pass 在重解析树上绑定：符号来自不同 Compilation，
    // SymbolEqualityComparer 判不相等 → 必须用稳定字符串键（类型全名::元数据名+签名）。
    private readonly Dictionary<string, IndexerPlan> _active;
    private readonly HashSet<string> _knownIndexers;
    private readonly MemberDesugarOptions _options;
    private readonly MemberDesugarStatistics _stats;
    private readonly List<MemberDesugarDiagnostic> _diagnostics;
    private readonly string _filePath;
    private readonly HashSet<string> _externalReported = new(StringComparer.Ordinal);
    private int _tempCounter;

    public IndexerUseSiteRewriter(
        SemanticModel model,
        IReadOnlyList<IndexerPlan> plannedPlans,
        IReadOnlyList<IndexerPlan> allPlans,
        MemberDesugarOptions options,
        MemberDesugarStatistics stats,
        List<MemberDesugarDiagnostic> diagnostics,
        string filePath)
    {
        _model = model;
        _active = new Dictionary<string, IndexerPlan>(StringComparer.Ordinal);
        foreach (var p in plannedPlans) _active[IndexerPlan.SiteKey(p.Indexer)] = p;
        _knownIndexers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in allPlans) _knownIndexers.Add(IndexerPlan.SiteKey(p.Indexer));
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
        _filePath = filePath;
    }

    // ---- 声明展开 + residual 闸门（CS2J5141）----

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node) => VisitTypeLike(node);
    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node) => VisitTypeLike(node);
    public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) => VisitTypeLike(node);
    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node) => VisitTypeLike(node);

    private SyntaxNode? VisitTypeLike(TypeDeclarationSyntax node)
    {
        var newMembers = new List<MemberDeclarationSyntax>();
        foreach (var member in node.Members)
        {
            if (member is IndexerDeclarationSyntax idxDecl
                && _model.GetDeclaredSymbol(idxDecl) is { IsIndexer: true } idx)
            {
                if (_active.TryGetValue(IndexerPlan.SiteKey(idx), out var plan))
                {
                    // 先改写访问器体内的使用点（this[i] 递归等），再展开；
                    // 语义绑定（特性路由）一律走原始声明。
                    var visitedDecl = (IndexerDeclarationSyntax)Visit(idxDecl)!;
                    var expander = new IndexerDeclarationExpander(
                        _model, _options, _stats, _diagnostics, _filePath);
                    foreach (var expanded in expander.Expand(visitedDecl, plan, idxDecl))
                        newMembers.Add(expanded);
                    continue;
                }
                if (!_knownIndexers.Contains(IndexerPlan.SiteKey(idx)))
                {
                    var span = idxDecl.GetLocation().GetLineSpan().StartLinePosition;
                    _diagnostics.Add(new MemberDesugarDiagnostic(
                        _options.Strict ? MemberDesugarSeverity.Error : MemberDesugarSeverity.Warning,
                        MemberDesugarDiagnosticCode.ResidualIndexer,
                        IndexerDiagnostics.Residual(idx.MetadataName),
                        _filePath, span.Line + 1, span.Character + 1, idx.MetadataName));
                    _stats.PlansSkipped++;
                    _stats.SkippedByReason.TryGetValue(
                        MemberDesugarDiagnosticCode.ResidualIndexer, out var n);
                    _stats.SkippedByReason[MemberDesugarDiagnosticCode.ResidualIndexer] = n + 1;
                }
            }

            if (Visit(member) is MemberDeclarationSyntax visitedMember)
                newMembers.Add(visitedMember);
        }
        return node.WithMembers(SyntaxFactory.List(newMembers));
    }

    // ---- 语句拼接（镜像 C11）：提升块拆入外层作用域 ----

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

    // ---- 读站点（IX-6/IX-9/IX-11）----

    public override SyntaxNode? VisitElementAccessExpression(ElementAccessExpressionSyntax node)
    {
        if (!TryGetPlan(node, out var plan))
        {
            CountExternalIfIndexer(node);
            return base.VisitElementAccessExpression(node);
        }
        if (IsDirectAssignmentTarget(node)) return base.VisitElementAccessExpression(node);
        _stats.IndexerReadSitesRewritten++;
        return BuildGetterInvocation(node, plan);
    }

    public override SyntaxNode? VisitElementBindingExpression(ElementBindingExpressionSyntax node)
    {
        if (!TryGetPlan(node, out var plan))
        {
            if (_model.GetSymbolInfo(node).Symbol is IPropertySymbol { IsIndexer: true } ext
                && IndexerFacts.IsExternal(ext))
                CountExternal(ext);
            return base.VisitElementBindingExpression(node);
        }
        // r?[i] → r?.get_Item(i)（IX-9）
        _stats.IndexerReadSitesRewritten++;
        var binding = SyntaxFactory.MemberBindingExpression(
            SyntaxFactory.Token(SyntaxKind.DotToken),
            SyntaxFactory.IdentifierName(plan.GetterMethodName));
        var args = ToPlainArgs((BaseArgumentListSyntax)Visit(node.ArgumentList)!);
        return SyntaxFactory.InvocationExpression(binding, args)
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
    }

    // ---- 写站点（IX-7：简单写语句 / ref 返回变量位置）----

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        // 简单写：c[i] = v → c.set_Item(i, v)
        if (node.Expression is AssignmentExpressionSyntax assign
            && assign.IsKind(SyntaxKind.SimpleAssignmentExpression)
            && assign.Left is ElementAccessExpressionSyntax ea
            && TryGetPlan(ea, out var writePlan)
            && !writePlan.ReturnsByRef)
        {
            _stats.IndexerWriteSitesRewritten++;
            var value = (ExpressionSyntax)Visit(assign.Right)!;
            return node.WithExpression(BuildSetterInvocation(ea, writePlan, value));
        }

        // 复合：c[k] op= v → c.set_Item(k, c.get_Item(k) op v)（副作用实参提升）
        if (node.Expression is AssignmentExpressionSyntax compound
            && !compound.IsKind(SyntaxKind.SimpleAssignmentExpression)
            && compound.Left is ElementAccessExpressionSyntax cea
            && TryGetPlan(cea, out var cplan)
            && !cplan.ReturnsByRef)
        {
            _stats.IndexerCompoundSitesExpanded++;
            return ExpandCompoundStatement(node, compound, cea, cplan);
        }

        // ++/--：结果未消费，按 get→算→set 展开（旧/新值等价）
        if (node.Expression is PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax)
        {
            var operand = node.Expression switch
            {
                PrefixUnaryExpressionSyntax p => p.Operand,
                PostfixUnaryExpressionSyntax p => p.Operand,
                _ => null!,
            };
            var increment = node.Expression.IsKind(SyntaxKind.PreIncrementExpression)
                            || node.Expression.IsKind(SyntaxKind.PostIncrementExpression);
            var decrement = node.Expression.IsKind(SyntaxKind.PreDecrementExpression)
                            || node.Expression.IsKind(SyntaxKind.PostDecrementExpression);
            if ((increment || decrement)
                && operand is ElementAccessExpressionSyntax iea
                && TryGetPlan(iea, out var iplan))
            {
                _stats.IndexerCompoundSitesExpanded++;
                if (iplan.ReturnsByRef)
                {
                    // ref 索引器：get_Item(...) 返回变量，++/-- 直接作用于调用结果。
                    var getter = BuildGetterInvocation(iea, iplan);
                    var unary = node.Expression switch
                    {
                        PrefixUnaryExpressionSyntax p => (ExpressionSyntax)p.WithOperand(getter),
                        PostfixUnaryExpressionSyntax po => po.WithOperand(getter),
                        _ => throw new InvalidOperationException("unreachable increment shape"),
                    };
                    return node.WithExpression(unary);
                }
                return ExpandCompoundStatement(
                    node,
                    SyntaxFactory.AssignmentExpression(
                        increment
                            ? SyntaxKind.AddAssignmentExpression
                            : SyntaxKind.SubtractAssignmentExpression,
                        iea,
                        SyntaxFactory.Token(increment
                            ? SyntaxKind.PlusEqualsToken
                            : SyntaxKind.MinusEqualsToken),
                        One()),
                    iea,
                    iplan);
            }
        }

        // 集合初始化器提升（IX-10）：new C { [k] = v } → 临时 + set_Item
        if (node.Expression is ObjectCreationExpressionSyntax oc
            && LiftIndexerCreation(oc, out var stmts, out _))
            return Block(stmts)
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia())
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);

        return base.VisitExpressionStatement(node);
    }

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        if (node.Parent is InitializerExpressionSyntax)
            return base.VisitAssignmentExpression(node); // 由提升逻辑接管

        // ref 返回索引器：写/复合/自增一律经 getter 变量（D9）。
        if (node.Left is ElementAccessExpressionSyntax left
            && TryGetPlan(left, out var plan)
            && plan.ReturnsByRef)
        {
            var newLeft = BuildGetterInvocation(left, plan);
            var right = (ExpressionSyntax)Visit(node.Right)!;
            return SyntaxFactory.AssignmentExpression(node.Kind(), newLeft, node.OperatorToken, right)
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        }
        return base.VisitAssignmentExpression(node);
    }

    // ---- 集合初始化器提升（IX-10 / D13，镜像 C11 LiftCreation）----

    public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
    {
        if (CanLiftLocalDeclaration(node)
            && node.Declaration.Variables[0].Initializer?.Value is ObjectCreationExpressionSyntax oc
            && LiftIndexerCreation(oc, out var stmts, out var result))
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
        // 索引器简单赋值作为返回值（numeric-boundary 前缀 ++ 消费形态，如
        // `return (c[i] = c[i] + 1);`）：setter 返回 void，提升为
        // `{ var __v = right; c.set_Item(i, __v); return __v; }`（IX-7 值上下文）。
        if (node.Parent is BlockSyntax or SwitchSectionSyntax
            && UnwrapParentheses(node.Expression) is AssignmentExpressionSyntax ia
            && ia.IsKind(SyntaxKind.SimpleAssignmentExpression)
            && ia.Left is ElementAccessExpressionSyntax ea
            && TryGetPlan(ea, out var writePlan)
            && !writePlan.ReturnsByRef)
        {
            _stats.IndexerWriteSitesRewritten++;
            var value = (ExpressionSyntax)Visit(ia.Right)!;
            var temp = TempName("ival");
            var tempIdent = Ident(temp);
            var lifted = new List<StatementSyntax>
            {
                DeclVar(temp, value),
                ExprStmt(BuildSetterInvocation(ea, writePlan, tempIdent)),
                SyntaxFactory.ReturnStatement(tempIdent.WithLeadingTrivia(SyntaxFactory.Space)),
            };
            return Block(lifted)
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia())
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        }

        if (node.Expression is ObjectCreationExpressionSyntax oc
            && node.Parent is BlockSyntax or SwitchSectionSyntax
            && LiftIndexerCreation(oc, out var stmts, out var result))
        {
            // return 与表达式之间必须显式留白（return__x 会拼接成单个标识符）。
            var @return = SyntaxFactory.ReturnStatement(result.WithLeadingTrivia(SyntaxFactory.Space));
            return Block(stmts.Append(@return))
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia())
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        }
        return base.VisitReturnStatement(node);
    }

    private bool LiftIndexerCreation(
        ObjectCreationExpressionSyntax oc, out List<StatementSyntax> stmts, out ExpressionSyntax result)
    {
        stmts = new List<StatementSyntax>();
        result = oc;
        if (oc.Initializer is not { } init || !init.IsKind(SyntaxKind.ObjectInitializerExpression))
            return false;
        var assigns = init.Expressions.OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is ImplicitElementAccessSyntax iea && TryGetPlan(iea, out _))
            .ToList();
        if (assigns.Count == 0) return false;

        var temp = TempName("init");
        var kept = init.Expressions
            .Where(e => e is not AssignmentExpressionSyntax a
                || a.Left is not ImplicitElementAccessSyntax i
                || !TryGetPlan(i, out _))
            .ToList();
        var keptCreation = oc.WithInitializer(kept.Count > 0
            ? SyntaxFactory.InitializerExpression(
                SyntaxKind.ObjectInitializerExpression, SyntaxFactory.SeparatedList(kept))
            : null);
        // `new C { .. }` 去掉初始化器后必须补空参数表，否则 `new C` 不是合法表达式。
        if (kept.Count == 0 && keptCreation.ArgumentList == null)
            keptCreation = keptCreation.WithArgumentList(SyntaxFactory.ArgumentList());
        stmts.Add(DeclVar(temp, (ExpressionSyntax)Visit(keptCreation)!));

        var tempIdent = Ident(temp);
        foreach (var a in assigns)
        {
            var iea = (ImplicitElementAccessSyntax)a.Left;
            TryGetPlan(iea, out var plan);
            var args = ToPlainArgs((BaseArgumentListSyntax)Visit(iea.ArgumentList)!);
            var withValue = SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
                args.Arguments.Append(SyntaxFactory.Argument((ExpressionSyntax)Visit(a.Right)!))));
            stmts.Add(ExprStmt(Invoke(
                MemberAccess(tempIdent, plan.SetterMethodName), withValue)));
        }
        result = tempIdent;
        _stats.IndexerInitializerSitesRewritten++;
        return true;
    }

    private static bool CanLiftLocalDeclaration(LocalDeclarationStatementSyntax node)
        => node.Declaration.Variables.Count == 1
           && node.UsingKeyword.IsKind(SyntaxKind.None)
           && node.AwaitKeyword.IsKind(SyntaxKind.None)
           && !node.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword)
               || m.IsKind(SyntaxKind.StaticKeyword));

    // ---- 复合/自增展开（D11：索引实参单次求值）----

    /// <summary>c[k] op= v →（简单实参）c.set_Item(k, c.get_Item(k) op v)；
    /// 带副作用实参 → 提升临时变量块（语句位必有可拼接块上下文）。</summary>
    private SyntaxNode ExpandCompoundStatement(
        ExpressionStatementSyntax node, AssignmentExpressionSyntax compound,
        ElementAccessExpressionSyntax ea, IndexerPlan plan)
    {
        var right = (ExpressionSyntax)Visit(compound.Right)!;
        var allSimple = ea.ArgumentList.Arguments.All(a => IndexerFacts.IsSimpleArgument(a.Expression));

        if (allSimple)
        {
            var read = BuildGetterInvocation(ea, plan);
            var bin = Binary(compound.Kind(), compound.OperatorToken.Kind(), read, right);
            return node.WithExpression(BuildSetterInvocation(ea, plan, bin));
        }

        var stmts = new List<StatementSyntax>();
        var argIdents = new List<ExpressionSyntax>();
        foreach (var arg in ea.ArgumentList.Arguments)
        {
            if (IndexerFacts.IsSimpleArgument(arg.Expression))
            {
                argIdents.Add(arg.Expression);
                continue;
            }
            var t = TempName("idx");
            stmts.Add(DeclVar(t, (ExpressionSyntax)Visit(arg.Expression)!));
            argIdents.Add(Ident(t));
        }
        var tempArgs = SyntaxFactory.ArgumentList(
            SyntaxFactory.SeparatedList(argIdents.Select(SyntaxFactory.Argument)));
        var recv = (ExpressionSyntax)Visit(ea.Expression)!;
        var read2 = Invoke(MemberAccess(recv, plan.GetterMethodName), tempArgs);
        var bin2 = Binary(compound.Kind(), compound.OperatorToken.Kind(), read2, right);
        var setArgs = SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(
            tempArgs.Arguments.Append(SyntaxFactory.Argument(bin2))));
        stmts.Add(ExprStmt(Invoke(MemberAccess(recv, plan.SetterMethodName), setArgs)));
        return Block(stmts)
            .WithLeadingTrivia(node.GetLeadingTrivia())
            .WithTrailingTrivia(node.GetTrailingTrivia())
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
    }

    // ---- 符号/上下文判定 ----

    private bool TryGetPlan(ExpressionSyntax node, out IndexerPlan plan)
    {
        plan = null!;
        if (node is not (ElementAccessExpressionSyntax or ElementBindingExpressionSyntax
            or ImplicitElementAccessSyntax)) return false;
        var sym = _model.GetSymbolInfo(node).Symbol;
        if (sym is not IPropertySymbol { IsIndexer: true } p)
        {
            // IX 跨文件兜底：索引实参依赖兄弟文件刚被属性 pass 展开的合成访问器时
            // （属性 pass 先行改写、逐文件绑定原始编译），GetSymbolInfo 对整个元素
            // 访问绑定失败。此时按接收者静态类型匹配活跃 plan——plan 只含源码声明
            // 索引器，外部索引器（D5 闸门）天然不会被劫持；歧义（同类型多个活跃
            // 索引器）时保守放弃。
            p = ResolveIndexerByReceiver(node);
            if (p == null) return false;
        }
        if (!p.IsDefinition) p = p.OriginalDefinition;
        // D5 闸门：非源码声明的（BCL/外部程序集）索引器使用点保留原生 []
        // 语法，否则 Dictionary<string,string> 的 d["k"] 会变成 d.get_Item("k")，
        // 在方法组/调用位置触发 CS0571。
        if (!p.Locations.Any(l => l.IsInSource)) return false;
        return _active.TryGetValue(IndexerPlan.SiteKey(p), out plan);
    }

    /// <summary>接收者静态类型兜底：沿基类链匹配唯一活跃索引器 plan；0 个或多个
    /// 匹配都返回 null（不猜）。数组接收者的 GetSymbolInfo 本就无符号，同样安全。</summary>
    private IPropertySymbol? ResolveIndexerByReceiver(ExpressionSyntax node)
    {
        if (node is not ElementAccessExpressionSyntax ea) return null;
        var receiverType = _model.GetTypeInfo(ea.Expression).Type;
        if (receiverType is IErrorTypeSymbol or null) return null;

        IndexerPlan? match = null;
        foreach (var candidate in _active.Values)
        {
            var containing = candidate.Indexer.ContainingType;
            if (containing == null) continue;
            // 接收者类型绑自重解析树、plan 符号绑自原始编译——跨 Compilation 的
            // 符号 SymbolEqualityComparer 判不相等（本文件头部注释），按显示串比较。
            var containingDisplay = containing.ToDisplayString();
            for (var t = (ITypeSymbol?)receiverType; t != null; t = t.BaseType)
            {
                if (!StringComparer.Ordinal.Equals(t.ToDisplayString(), containingDisplay)) continue;
                if (match != null) return null; // 歧义：同接收者类型多个活跃索引器。
                match = candidate;
                break;
            }
        }

        return match?.Indexer;
    }

    /// <summary>c[i] 是纯左值目标（赋值左侧 / ++/-- 操作数）时读路径让位：简单写由
    /// <see cref="VisitExpressionStatement"/> 的 setter 路径接管、自增/自减由 get→算→set
    /// 展开接管。注意一元父节点仅在 ++/-- 时才算写目标——`!`/`~`/`+`/`-` 是纯读，必须照常
    /// 改写（回归：xml-tiny ContentValidator 的 `!context.CurPos[i][j]` 曾被本闸门整体
    /// 跳过，降糖后该站点残留 [] 索引，最终阶段 '!' 非 bool 操作数 fail-fast）。</summary>
    private static bool IsDirectAssignmentTarget(SyntaxNode node)
        => (node.Parent is AssignmentExpressionSyntax a && ReferenceEquals(a.Left, node))
           || (node.Parent is PrefixUnaryExpressionSyntax pre
               && (pre.IsKind(SyntaxKind.PreIncrementExpression)
                   || pre.IsKind(SyntaxKind.PreDecrementExpression)))
           || (node.Parent is PostfixUnaryExpressionSyntax post
               && (post.IsKind(SyntaxKind.PostIncrementExpression)
                   || post.IsKind(SyntaxKind.PostDecrementExpression)));

    private static ExpressionSyntax? UnwrapParentheses(ExpressionSyntax? expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;
        return expression;
    }

    // ---- 表达式构造 ----

    /// <summary>索引实参表是 BracketedArgumentListSyntax；方法调用需要普通 ArgumentList。</summary>
    private static ArgumentListSyntax ToPlainArgs(BaseArgumentListSyntax bracketed)
        => SyntaxFactory.ArgumentList(bracketed.Arguments);

    private InvocationExpressionSyntax BuildGetterInvocation(
        ElementAccessExpressionSyntax node, IndexerPlan plan)
    {
        var recv = (ExpressionSyntax)Visit(node.Expression)!;
        recv = ApplyExplicitCast(recv, plan, node);
        var args = ToPlainArgs((BaseArgumentListSyntax)Visit(node.ArgumentList)!);
        return Invoke(MemberAccess(recv, plan.GetterMethodName), args);
    }

    /// <summary>显式接口实现的接收者静态类型不是该接口时插入最小限定强转（D6；
    /// 合法输入中站点总是经接口类型绑定到接口 plan，此处为防御路径）。</summary>
    private ExpressionSyntax ApplyExplicitCast(
        ExpressionSyntax recv, IndexerPlan plan, ExpressionSyntax site)
    {
        if (!plan.IsExplicitImplementation) return recv;
        var iface = plan.Indexer.ExplicitInterfaceImplementations.FirstOrDefault()
            ?.ContainingType;
        if (iface == null) return recv;
        if (_model.GetTypeInfo(site).Type is { } t
            && SymbolEqualityComparer.Default.Equals(t, iface)) return recv;
        if (recv is ParenthesizedExpressionSyntax) return recv;
        var ifaceType = SyntaxFactory.ParseTypeName(
            iface.ToMinimalDisplayString(_model, site.SpanStart));
        return SyntaxFactory.ParenthesizedExpression(
            SyntaxFactory.CastExpression(ifaceType, recv));
    }

    private InvocationExpressionSyntax BuildSetterInvocation(
        ElementAccessExpressionSyntax node, IndexerPlan plan, ExpressionSyntax value)
    {
        var recv = (ExpressionSyntax)Visit(node.Expression)!;
        var args = ToPlainArgs((BaseArgumentListSyntax)Visit(node.ArgumentList)!);
        var withValue = SyntaxFactory.ArgumentList(
            SyntaxFactory.SeparatedList(args.Arguments.Append(SyntaxFactory.Argument(value))));
        return Invoke(MemberAccess(recv, plan.SetterMethodName), withValue);
    }

    private void CountExternalIfIndexer(ElementAccessExpressionSyntax node)
    {
        if (_model.GetSymbolInfo(node).Symbol is IPropertySymbol { IsIndexer: true } ext
            && IndexerFacts.IsExternal(ext))
            CountExternal(ext);
    }

    private void CountExternal(IPropertySymbol ext)
    {
        _stats.ExternalIndexerSites++;
        var typeName = ext.ContainingType?.ToDisplayString() ?? "?";
        if (_externalReported.Add(typeName))
            _diagnostics.Add(new MemberDesugarDiagnostic(
                MemberDesugarSeverity.Info, MemberDesugarDiagnosticCode.ExternalIndexerSite,
                $"indexer access on external type '{typeName}' was left untouched (D5 gate)",
                _filePath, 0, 0, typeName));
    }

    private string TempName(string hint) => MemberDesugarSyntaxFacts.TempName(ref _tempCounter, hint);

    private static IdentifierNameSyntax Ident(string name) => SyntaxFactory.IdentifierName(name);

    private static LiteralExpressionSyntax One()
        => SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(1));

    private static InvocationExpressionSyntax Invoke(ExpressionSyntax e, ArgumentListSyntax args)
        => SyntaxFactory.InvocationExpression(e, args)
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);

    private static MemberAccessExpressionSyntax MemberAccess(ExpressionSyntax recv, string name)
        => SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression, recv,
            SyntaxFactory.Token(SyntaxKind.DotToken), SyntaxFactory.IdentifierName(name));

    private static BlockSyntax Block(IEnumerable<StatementSyntax> stmts) => SyntaxFactory.Block(stmts);

    private static ExpressionStatementSyntax ExprStmt(ExpressionSyntax e)
        => SyntaxFactory.ExpressionStatement(e);

    internal static LocalDeclarationStatementSyntax DeclVar(string name, ExpressionSyntax value)
        => SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                // "var" 与标识符之间必须显式留白：无 trivia 的相邻标识符会拼接成
                // 单个 token（var__cs2j_init0），重解析后语义破坏。
                SyntaxFactory.IdentifierName("var").WithTrailingTrivia(SyntaxFactory.Space),
                SyntaxFactory.SeparatedList(new[]
                {
                    SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(name))
                        .WithInitializer(SyntaxFactory.EqualsValueClause(value))
                })));

    private static BinaryExpressionSyntax Binary(
        SyntaxKind assignmentKind, SyntaxKind operatorKind,
        ExpressionSyntax left, ExpressionSyntax right)
    {
        var binaryKind = ToBinaryKind(assignmentKind);
        return SyntaxFactory.BinaryExpression(binaryKind, left,
            SyntaxFactory.Token(ToBinaryToken(binaryKind)), right);
    }

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
}
