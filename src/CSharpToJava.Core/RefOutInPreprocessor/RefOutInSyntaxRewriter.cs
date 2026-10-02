using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.RefOutInPreprocessor;

/// <summary>
/// Pass 2+3: CSharpSyntaxRewriter that rewrites declarations (ref/out params,
/// ref returns, ref locals, ref fields, delegates) and lifts call sites
/// (ref/out arguments → ByRef proxies) through RefOutInStatementLifter.
/// </summary>
internal sealed class RefOutInSyntaxRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _semanticModel;
    private readonly RefOutInAliasAnnotator _annotator;
    private readonly RefOutInRewriterStatistics _stats;
    private readonly List<RefOutInRewriterDiagnostic> _diagnostics;
    private readonly RefOutInStatementLifter _lifter;
    private int _tempCounter;

    // Holder-name scopes: one entry per active method/constructor frame.
    private readonly Stack<HashSet<string>> _holderNameScopes = new();

    public bool UsedHolder { get; private set; }

    public RefOutInSyntaxRewriter(
        SemanticModel semanticModel,
        RefOutInAliasAnnotator annotator,
        RefOutInRewriterStatistics stats,
        List<RefOutInRewriterDiagnostic> diagnostics)
    {
        _semanticModel = semanticModel;
        _annotator = annotator;
        _stats = stats;
        _diagnostics = diagnostics;
        _lifter = new RefOutInStatementLifter(this);
    }

    // ── surfaces used by RefOutInStatementLifter ────────────────────────────

    internal RefOutInRewriterStatistics Stats => _stats;
    internal RefOutInAliasAnnotator Annotator => _annotator;
    internal int NextTemp() => _tempCounter++;

    internal bool IsHolderName(string name) => _holderNameScopes.Any(s => s.Contains(name));

    internal IMethodSymbol? ResolveInvocation(InvocationExpressionSyntax inv)
    {
        try { return _semanticModel.GetSymbolInfo(inv).Symbol as IMethodSymbol; }
        catch (ArgumentException) { return null; }
    }

    internal ISymbol? GetSymbol(ExpressionSyntax e)
    {
        try { return _semanticModel.GetSymbolInfo(e).Symbol; }
        catch (ArgumentException) { return null; }
    }

    internal ITypeSymbol? GetTypeOf(ExpressionSyntax e)
    {
        try
        {
            var ti = _semanticModel.GetTypeInfo(e);
            return ti.Type ?? ti.ConvertedType;
        }
        catch (ArgumentException) { return null; }
    }

    internal string TypeDisplay(ITypeSymbol t) => t.ToDisplayString();

    internal bool IsInternalCandidate(IMethodSymbol m) =>
        m.Locations.Any(l => l.IsInSource)
        && Annotator.GetSymbolClass(m) != RefOutInAliasAnnotator.SymbolClass.Locked;

    /// <summary>CD8 wiring (C9): true when the parameter keeps its <c>in</c> modifier.</summary>
    internal bool IsInKept(IMethodSymbol method, int index)
    {
        var param = method.Parameters[index];
        return param.RefKind == RefKind.In && Annotator.InKeptParameters.Contains(param);
    }

    internal void MarkHolderUsed() => UsedHolder = true;

    internal void Report3304(ExpressionSyntax node, string reason) =>
        _diagnostics.Add(new RefOutInRewriterDiagnostic(
            RefOutInDiagnosticCodes.RefStructSlotForwarded,
            reason,
            RefOutInSeverity.Error, node.GetLocation()));

    internal void Report3303(ExpressionSyntax node, string reason) =>
        _diagnostics.Add(new RefOutInRewriterDiagnostic(
            RefOutInDiagnosticCodes.UnproxyablePosition,
            $"cannot construct a legal ByRef proxy here: {reason}",
            RefOutInSeverity.Error, node.GetLocation()));

    internal ExpressionSyntax InjectSlotValues(ExpressionSyntax expr)
    {
        if (expr == null) return expr!;
        return (ExpressionSyntax)new SlotInjector(this).Visit(expr)!;
    }

    // ── Pass 2: Declaration rewriting ───────────────────────────────────────

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var symbol = _semanticModel.GetDeclaredSymbol(node);
        if (symbol == null)
            return base.VisitMethodDeclaration(node);

        _holderNameScopes.Push(new HashSet<string>());
        try
        {
            // Pre-scan for ref local names in this method body (BEFORE any
            // rewriting so the semantic model can still resolve symbols).
            if (node.Body != null)
            {
                var refLocals = node.Body.DescendantNodes()
                    .OfType<VariableDeclaratorSyntax>()
                    .Where(v => v.Parent is VariableDeclarationSyntax vd && vd.Type is RefTypeSyntax);
                foreach (var rl in refLocals)
                    CurrentHolderNames.Add(rl.Identifier.Text);
            }

            var newParamList = new List<ParameterSyntax>();
            foreach (var param in node.ParameterList.Parameters)
            {
                var paramSymbol = symbol.Parameters.FirstOrDefault(p => p.Name == param.Identifier.Text);
                if (paramSymbol != null && _annotator.HolderSymbols.Contains(paramSymbol))
                {
                    CurrentHolderNames.Add(param.Identifier.Text);
                    newParamList.Add(RewriteParameter(param, paramSymbol));
                }
                else if (NeedsInStrip(param) && _annotator.IsInParameterKept(paramSymbol))
                {
                    // CD8: overload collision — keep `in`
                    newParamList.Add(param);
                }
                else if (NeedsInStrip(param))
                {
                    newParamList.Add(StripModifiers(param));
                }
                else
                {
                    newParamList.Add(param);
                }
            }

            // Ref return → ByRef<T> return
            TypeSyntax newReturnType = node.ReturnType;
            bool refReturn = (symbol.RefKind == RefKind.Ref || symbol.RefKind == RefKind.RefReadOnly)
                && node.ReturnType is RefTypeSyntax;
            if (refReturn && node.ReturnType is RefTypeSyntax refReturnType)
            {
                newReturnType = SyntaxFactory.GenericName(
                    SyntaxFactory.Identifier("ByRef"),
                    SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList(refReturnType.Type)));
                UsedHolder = true;
            }

            bool signatureChanged = newParamList.Count != node.ParameterList.Parameters.Count
                || node.ParameterList.Parameters
                    .Where((p, i) => !ReferenceEquals(newParamList[i], p)).Any()
                || refReturn;

            // C4: expression body `=> ref e` → `=> <proxy(e)>`
            if (node.ExpressionBody?.Expression is RefExpressionSyntax refArrowMethod)
            {
                var innerM = refArrowMethod.Expression;
                while (innerM is ParenthesizedExpressionSyntax p) innerM = p.Expression;
                if (!_lifter.ContainsRefStructCapture(innerM))
                {
                    node = node.WithExpressionBody(
                        node.ExpressionBody.WithExpression(BuildReturnProxy(innerM)));
                    UsedHolder = true;
                }
            }

            // C1: expression body containing liftable calls → block body
            if (node.ExpressionBody != null && _lifter.ContainsLiftable(node.ExpressionBody.Expression))
            {
                node = ConvertExpressionBody(node);
                signatureChanged = true;
            }

            var newParams = SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(newParamList));
            var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
            visited = visited.WithParameterList(newParams).WithReturnType(newReturnType);

            if (signatureChanged) UsedHolder = true;
            return visited;
        }
        finally { _holderNameScopes.Pop(); }
    }

    private static bool NeedsInStrip(ParameterSyntax param) =>
        param.Modifiers.Any(m => m.IsKind(SyntaxKind.InKeyword) || m.IsKind(SyntaxKind.ScopedKeyword));

    private MethodDeclarationSyntax ConvertExpressionBody(MethodDeclarationSyntax node)
    {
        var lifted = _lifter.SpliceExpression(node.ExpressionBody!.Expression, out var hoisted);
        var stmts = new List<StatementSyntax>();
        stmts.AddRange(hoisted);
        stmts.Add(SyntaxFactory.ReturnStatement(InjectSlotValues(lifted)));
        stmts.AddRange(_lifter.TakePendingReadbacks());
        UsedHolder = true;
        return node
            .WithBody(SyntaxFactory.Block(stmts))
            .WithExpressionBody(null)
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.None));
    }

    private HashSet<string> CurrentHolderNames => _holderNameScopes.Peek();

    private ParameterSyntax RewriteParameter(ParameterSyntax param, IParameterSymbol symbol)
    {
        var type = symbol.Type;
        TypeSyntax newType;

        if (type.IsRefLikeType)
        {
            newType = SyntaxFactory.ParseTypeName(
                $"Cs2jByRef.RefStructByRef<{TypeDisplay(type)}>");
        }
        else
        {
            var innerType = param.Type;
            if (innerType is RefTypeSyntax rt)
                innerType = rt.Type;
            if (innerType == null)
                innerType = SyntaxFactory.ParseTypeName(type.ToDisplayString());
            newType = SyntaxFactory.GenericName(
                SyntaxFactory.Identifier("ByRef"),
                SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList(innerType)));
        }

        var newModifiers = SyntaxFactory.TokenList();
        foreach (var mod in param.Modifiers)
        {
            if (mod.IsKind(SyntaxKind.ScopedKeyword))
            {
                _stats.ScopedRemoved++;
                continue;
            }
            if (mod.IsKind(SyntaxKind.RefKeyword) || mod.IsKind(SyntaxKind.OutKeyword)
                || mod.IsKind(SyntaxKind.InKeyword))
                continue;
            newModifiers = newModifiers.Add(mod);
        }

        UsedHolder = true;
        return param.WithModifiers(newModifiers).WithType(newType);
    }

    private ParameterSyntax StripModifiers(ParameterSyntax param)
    {
        var newModifiers = SyntaxFactory.TokenList();
        foreach (var mod in param.Modifiers)
        {
            if (mod.IsKind(SyntaxKind.ScopedKeyword))
            {
                _stats.ScopedRemoved++;
                continue;
            }
            if (mod.IsKind(SyntaxKind.InKeyword))
                continue;
            newModifiers = newModifiers.Add(mod);
        }

        TypeSyntax newType = param.Type;
        if (param.Type is RefTypeSyntax rt)
            newType = rt.Type;

        UsedHolder = true;
        return param.WithModifiers(newModifiers).WithType(newType);
    }

    public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        var symbol = _semanticModel.GetDeclaredSymbol(node);
        if (symbol == null)
            return base.VisitConstructorDeclaration(node);

        _holderNameScopes.Push(new HashSet<string>());
        try
        {
            if (node.ExpressionBody != null && _lifter.ContainsLiftable(node.ExpressionBody.Expression))
                Report3303(node.ExpressionBody.Expression,
                    "constructor expression body contains an internal ref/out call; convert to a block body");

            var newParamList = new List<ParameterSyntax>();
            foreach (var param in node.ParameterList.Parameters)
            {
                var paramSymbol = symbol.Parameters.FirstOrDefault(p => p.Name == param.Identifier.Text);
                if (paramSymbol != null && _annotator.HolderSymbols.Contains(paramSymbol))
                {
                    CurrentHolderNames.Add(param.Identifier.Text);
                    newParamList.Add(RewriteParameter(param, paramSymbol));
                }
                else
                {
                    newParamList.Add(param);
                }
            }

            var newParams = SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(newParamList));
            var visited = (ConstructorDeclarationSyntax)base.VisitConstructorDeclaration(node)!;
            return visited.WithParameterList(newParams);
        }
        finally { _holderNameScopes.Pop(); }
    }

    public override SyntaxNode? VisitDelegateDeclaration(DelegateDeclarationSyntax node)
    {
        var symbol = _semanticModel.GetDeclaredSymbol(node);
        if (symbol == null)
            return base.VisitDelegateDeclaration(node);

        var invoke = symbol.DelegateInvokeMethod;
        if (invoke == null)
            return base.VisitDelegateDeclaration(node);

        bool hasHolder = false;
        var newParamList = new List<ParameterSyntax>();
        foreach (var param in node.ParameterList.Parameters)
        {
            var paramSymbol = invoke.Parameters.FirstOrDefault(p => p.Name == param.Identifier.Text);
            if (paramSymbol != null && _annotator.HolderSymbols.Contains(paramSymbol))
            {
                hasHolder = true;
                newParamList.Add(RewriteParameter(param, paramSymbol));
            }
            else
            {
                var needsStrip = param.Modifiers.Any(m =>
                    m.IsKind(SyntaxKind.InKeyword)
                    || m.IsKind(SyntaxKind.ScopedKeyword));
                if (needsStrip)
                {
                    hasHolder = true;
                    newParamList.Add(StripModifiers(param));
                }
                else
                {
                    newParamList.Add(param);
                }
            }
        }

        var newParams = SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(newParamList));

        TypeSyntax newReturnType = node.ReturnType;
        if ((invoke.RefKind == RefKind.Ref || invoke.RefKind == RefKind.RefReadOnly)
            && node.ReturnType is RefTypeSyntax refType)
        {
            newReturnType = SyntaxFactory.GenericName(
                SyntaxFactory.Identifier("ByRef"),
                SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList(refType.Type)));
            hasHolder = true;
        }

        var result = base.VisitDelegateDeclaration(
            node.WithParameterList(newParams).WithReturnType(newReturnType));
        if (hasHolder) UsedHolder = true;
        return result;
    }

    // ── Body rewriting: s → s.Value for holder symbols ─────────────────────

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        if (node.HasAnnotation(RefOutInStatementLifter.ValueInjectedAnnotation)
            || node.HasAnnotation(RefOutInStatementLifter.DirectPassAnnotation))
            return base.VisitIdentifierName(node);

        var name = node.Identifier.Text;
        if (IsInDeclarationContext(node) || !IsHolderName(name))
            return base.VisitIdentifierName(node);

        _stats.HoldersInjected++;
        return SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            node.WithAdditionalAnnotations(RefOutInStatementLifter.ValueInjectedAnnotation),
            SyntaxFactory.IdentifierName("Value"));
    }

    internal static bool IsInDeclarationContext(IdentifierNameSyntax node)
    {
        return node.Parent is ParameterSyntax
            || node.Parent is VariableDeclaratorSyntax
            || node.Parent is SimpleBaseTypeSyntax
            || node.Parent is GenericNameSyntax
            || node.Parent is QualifiedNameSyntax
            || node.Parent is AliasQualifiedNameSyntax
            || node.Parent is NameColonSyntax
            || (node.Parent is MemberAccessExpressionSyntax mae && ReferenceEquals(mae.Name, node))
            || (node.Parent is MemberBindingExpressionSyntax mbe && ReferenceEquals(mbe.Name, node));
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        var newStatements = new List<StatementSyntax>();
        bool changed = false;
        foreach (var stmt in node.Statements)
        {
            var visited = (StatementSyntax)base.Visit(stmt)!;
            if (visited is BlockSyntax inner
                && inner.HasAnnotation(RefOutInStatementLifter.HoistedBlockAnnotation))
            {
                newStatements.AddRange(inner.Statements);
                changed = true;
            }
            else
            {
                newStatements.Add(visited);
                changed |= !ReferenceEquals(visited, stmt);
            }
        }
        if (!changed) return node;
        return node.WithStatements(SyntaxFactory.List(newStatements));
    }

    /// <summary>
    /// Top-level statements live outside any method declaration, so the
    /// method-scoped holder registry never sees their ref locals. Push a
    /// compilation-level scope seeded with the annotator's ref-local names.
    /// </summary>
    public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
    {
        if (!node.Members.OfType<GlobalStatementSyntax>().Any())
            return base.VisitCompilationUnit(node);

        _holderNameScopes.Push(new HashSet<string>(Annotator.RefLocalNames));
        try
        {
            return base.VisitCompilationUnit(node);
        }
        finally { _holderNameScopes.Pop(); }
    }

    // ── Pass 3: Statement-level call-site lifting ───────────────────────────

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        var lifted = _lifter.SpliceExpression(node.Expression, out var hoisted);
        var pending = _lifter.TakePendingReadbacks();
        if (hoisted.Count == 0 && pending.Count == 0)
        {
            if (ReferenceEquals(lifted, node.Expression))
                return base.VisitExpressionStatement(node);
            return node.WithExpression(InjectSlotValues(lifted));   // direct-pass / in-keyword drops only
        }

        var stmts = new List<StatementSyntax>();
        stmts.AddRange(hoisted);
        // A non-void lifted call is already materialized as 
        // in the hoisted statements — a bare temp statement would be CS0201.
        if (!(lifted is IdentifierNameSyntax callTemp
            && callTemp.Identifier.ValueText.StartsWith("__call", StringComparison.Ordinal)))
        {
            stmts.Add(SyntaxFactory.ExpressionStatement(InjectSlotValues(lifted)));
        }
        stmts.AddRange(pending);
        return HoistedBlock(stmts);
    }

    public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
    {
        if (node.Expression == null)
            return base.VisitReturnStatement(node);

        // C4: `return ref e` -> `return <proxy(e)>`
        if (node.Expression is RefExpressionSyntax refExpr)
        {
            var inner = refExpr.Expression;
            while (inner is ParenthesizedExpressionSyntax p) inner = p.Expression;
            if (_lifter.ContainsRefStructCapture(inner))
            {
                Report3303(inner, "ref return of a ref-struct member cannot be proxied");
                return base.VisitReturnStatement(node);
            }
            UsedHolder = true;
            var proxy = BuildReturnProxy(inner);
            return node.WithExpression(proxy);
        }

        var lifted = _lifter.SpliceExpression(node.Expression, out var hoisted);
        var pending = _lifter.TakePendingReadbacks();
        if (hoisted.Count == 0 && pending.Count == 0)
        {
            if (ReferenceEquals(lifted, node.Expression))
                return base.VisitReturnStatement(node);
            return node.WithExpression(InjectSlotValues(lifted));
        }

        var stmts = new List<StatementSyntax>();
        stmts.AddRange(hoisted);
        stmts.Add(node.WithExpression(InjectSlotValues(lifted)));
        stmts.AddRange(pending);
        return HoistedBlock(stmts);
    }

    public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
    {
        var cond = _lifter.SpliceExpression(node.Condition, out var hoisted);
        var visitedThen = (StatementSyntax)base.Visit(node.Statement)!;
        ElseClauseSyntax? elseClause = node.Else != null
            ? SyntaxFactory.ElseClause((StatementSyntax)base.Visit(node.Else.Statement)!)
            : null;
        if (hoisted.Count == 0)
        {
            if (ReferenceEquals(cond, node.Condition))
                return base.VisitIfStatement(node);
            return SyntaxFactory.IfStatement(InjectSlotValues(cond), visitedThen, elseClause);
        }

        var newIf = SyntaxFactory.IfStatement(InjectSlotValues(cond), visitedThen, elseClause);
        return HoistedBlock(hoisted.Append(newIf));
    }

    public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
    {
        var cond = _lifter.SpliceExpression(node.Condition, out var hoisted);
        if (hoisted.Count == 0)
        {
            if (ReferenceEquals(cond, node.Condition))
                return base.VisitWhileStatement(node);
            var visitedBodyOnly = (StatementSyntax)base.Visit(node.Statement)!;
            return node.WithStatement(visitedBodyOnly).WithCondition(InjectSlotValues(cond));
        }

        // while (C) B → while (true) { hoisted; if (!C) break; B; }
        // continue inside B re-evaluates the spliced condition — semantics kept.
        var visitedBody = (StatementSyntax)base.Visit(node.Statement)!;
        var loopBody = new List<StatementSyntax>();
        loopBody.AddRange(hoisted);
        loopBody.Add(SyntaxFactory.IfStatement(
            Negate(InjectSlotValues(cond)), SyntaxFactory.BreakStatement()));
        loopBody.Add(visitedBody);
        return SyntaxFactory.WhileStatement(
            SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression),
            SyntaxFactory.Block(loopBody));
    }

    public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
    {
        if (_lifter.ContainsLiftable(node.Condition))
            Report3303(node.Condition,
                "do-while condition contains an internal ref/out call; continue semantics cannot be preserved — restructure the loop");
        return base.VisitDoStatement(node);
    }

    public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
    {
        var preStmts = new List<StatementSyntax>();
        bool anyLift = false;

        // declarations / initializers: hoist only (the while(true) form discards them)
        if (node.Declaration != null)
        {
            foreach (var d in node.Declaration.Variables)
            {
                if (d.Initializer == null) continue;
                var liftedInit = _lifter.SpliceExpression(d.Initializer.Value, out var hInit);
                if (hInit.Count > 0)
                {
                    anyLift = true;
                    preStmts.AddRange(hInit);
                    preStmts.AddRange(_lifter.TakePendingReadbacks());
                }
            }
        }

        foreach (var init in node.Initializers)
        {
            var liftedInit = _lifter.SpliceExpression(init, out var hInit);
            if (hInit.Count > 0)
            {
                anyLift = true;
                preStmts.AddRange(hInit);
                preStmts.AddRange(_lifter.TakePendingReadbacks());
            }
        }

        // condition
        ExpressionSyntax? newCondition = node.Condition;
        var condStmts = new List<StatementSyntax>();
        if (node.Condition != null)
        {
            var liftedCond = _lifter.SpliceExpression(node.Condition, out var hCond);
            if (hCond.Count > 0) anyLift = true;
            condStmts.AddRange(hCond);
            condStmts.AddRange(_lifter.TakePendingReadbacks());
            newCondition = InjectSlotValues(liftedCond);
        }

        // body + increments
        var visitedBody = (StatementSyntax)base.Visit(node.Statement)!;
        var incStmts = new List<StatementSyntax>();
        var incExprs = new List<ExpressionSyntax>();
        bool incrementsChanged = false;
        foreach (var inc in node.Incrementors)
        {
            var liftedInc = _lifter.SpliceExpression(inc, out var hInc);
            if (hInc.Count > 0)
            {
                anyLift = true;
                incrementsChanged = true;
                incStmts.AddRange(hInc);
                incStmts.AddRange(_lifter.TakePendingReadbacks());
            }
            incExprs.Add(InjectSlotValues(liftedInc));
        }

        if (!anyLift && !incrementsChanged
            && ReferenceEquals(newCondition, node.Condition))
        {
            return base.VisitForStatement(node);
        }

        // for (I; C; U) B → I; while (true) { C-splice; if (!C) break; B; U-splice; }
        // (continue runs U then re-evaluates C — for semantics kept)
        var loopBody = new List<StatementSyntax>();
        loopBody.AddRange(condStmts);
        if (newCondition != null)
            loopBody.Add(SyntaxFactory.IfStatement(
                Negate(newCondition), SyntaxFactory.BreakStatement()));
        loopBody.Add(visitedBody);
        loopBody.AddRange(incStmts);
        foreach (var incExpr in incExprs)
            loopBody.Add(SyntaxFactory.ExpressionStatement(incExpr));

        preStmts.Add(SyntaxFactory.WhileStatement(
            SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression),
            SyntaxFactory.Block(loopBody)));
        return HoistedBlock(preStmts);
    }

    public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
        => LiftForEach(node, node.Expression, (e, body) => node.WithExpression(e).WithStatement(body));

    public override SyntaxNode? VisitForEachVariableStatement(ForEachVariableStatementSyntax node)
        => LiftForEach(node, node.Expression, (e, body) => node.WithExpression(e).WithStatement(body));

    private SyntaxNode? LiftForEach(
        StatementSyntax node, ExpressionSyntax expression,
        Func<ExpressionSyntax, StatementSyntax, StatementSyntax> rebuild)
    {
        var lifted = _lifter.SpliceExpression(expression, out var hoisted);
        var visitedBody = (StatementSyntax)base.Visit(((CommonForEachStatementSyntax)node).Statement)!;
        if (hoisted.Count == 0)
        {
            // 快路径：表达式无需提升。不能调 base.Visit(node)——Visit 经 node.Accept
            // 虚分发会重入本 override 造成无限递归（SO）；body 已手动访问过，直接重建。
            if (ReferenceEquals(lifted, expression))
                return rebuild(expression, visitedBody);
            return rebuild(InjectSlotValues(lifted), visitedBody);
        }
        var newForeach = rebuild(InjectSlotValues(lifted), visitedBody);
        return HoistedBlock(hoisted.Append(newForeach));
    }

    public override SyntaxNode? VisitLockStatement(LockStatementSyntax node)
    {
        if (_lifter.ContainsLiftable(node.Expression))
            Report3303(node.Expression,
                "lock expression contains an internal ref/out call; hoisting would move code outside the lock — restructure");
        return base.VisitLockStatement(node);
    }

    public override SyntaxNode? VisitFixedStatement(FixedStatementSyntax node)
    {
        var initializer = node.Declaration.Variables
            .Select(v => v.Initializer?.Value).OfType<ExpressionSyntax>().FirstOrDefault();
        if (initializer != null && _lifter.ContainsLiftable(initializer))
            Report3303(initializer,
                "fixed statement initializer contains an internal ref/out call; the unsafe pointer domain (C06) does not compose with ByRef proxies");
        return base.VisitFixedStatement(node);
    }

    public override SyntaxNode? VisitUsingStatement(UsingStatementSyntax node)
    {
        var hoisted = new List<StatementSyntax>();
        ExpressionSyntax? newExpr = null;
        VariableDeclarationSyntax? newDecl = null;

        if (node.Expression != null)
        {
            var lifted = _lifter.SpliceExpression(node.Expression, out hoisted);
            if (hoisted.Count == 0 && ReferenceEquals(lifted, node.Expression))
                return base.VisitUsingStatement(node);
            newExpr = InjectSlotValues(lifted);
        }
        else if (node.Declaration != null)
        {
            var vars = new List<VariableDeclaratorSyntax>();
            foreach (var d in node.Declaration.Variables)
            {
                if (d.Initializer != null)
                {
                    var lifted = _lifter.SpliceExpression(d.Initializer.Value, out var h);
                    if (h.Count > 0)
                    {
                        hoisted.AddRange(h);
                        hoisted.AddRange(_lifter.TakePendingReadbacks());
                        vars.Add(d.WithInitializer(SyntaxFactory.EqualsValueClause(InjectSlotValues(lifted))));
                        continue;
                    }
                }
                vars.Add(d);
            }
            if (hoisted.Count == 0)
                return base.VisitUsingStatement(node);
            newDecl = SyntaxFactory.VariableDeclaration(node.Declaration.Type, SyntaxFactory.SeparatedList(vars));
        }
        else
        {
            return base.VisitUsingStatement(node);
        }

        var visitedBody = (StatementSyntax)base.Visit(node.Statement)!;
        var newUsing = node.WithStatement(visitedBody);
        if (newExpr != null)
            newUsing = (UsingStatementSyntax)newUsing.WithExpression(newExpr).WithDeclaration(null);
        else
            newUsing = (UsingStatementSyntax)newUsing.WithDeclaration(newDecl!).WithExpression(null);

        return SyntaxFactory.Block(hoisted.Append(newUsing));
    }

    public override SyntaxNode? VisitSwitchStatement(SwitchStatementSyntax node)
    {
        var lifted = _lifter.SpliceExpression(node.Expression, out var hoisted);
        var sections = SyntaxFactory.List(node.Sections.Select(s => (SwitchSectionSyntax)base.Visit(s)!));
        if (hoisted.Count == 0)
        {
            if (ReferenceEquals(lifted, node.Expression))
                return base.VisitSwitchStatement(node);
            return node.WithExpression(InjectSlotValues(lifted)).WithSections(sections);
        }
        var newSwitch = node.WithExpression(InjectSlotValues(lifted)).WithSections(sections);
        return HoistedBlock(hoisted.Append(newSwitch));
    }

    // ── Local declarations ──────────────────────────────────────────────────

    public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
    {
        if (node.Declaration.Type is RefTypeSyntax refType)
        {
            var innerType = refType.Type;
            var isReadOnly = refType.ReadOnlyKeyword.IsKind(SyntaxKind.ReadOnlyKeyword);

            // One entry per declarator: the proxy type can differ per
            // initializer shape (ByRef / ReadOnlyByRef / ScopedByRef /
            // ReadOnlyScopedByRef), so each declarator becomes its own
            // declaration statement.
            var proxiedDeclarators =
                new List<(string ProxyTypeName, string TypeArg, VariableDeclaratorSyntax Declarator)>();

            foreach (var declarator in node.Declaration.Variables)
            {
                if (declarator.Initializer is EqualsValueClauseSyntax init)
                {
                    var expr = init.Value;
                    if (expr is RefExpressionSyntax refExpr)
                        expr = refExpr.Expression;
                    var typeArgText = ResolveTypeDisplayName(innerType, expr);

                    ExpressionSyntax proxyExpr;
                    string proxyTypeName;

                    if (expr is IdentifierNameSyntax id && IsHolderName(id.Identifier.Text))
                    {
                        // holder-to-holder flow: keep the proxy reference itself
                        proxyExpr = expr;
                        proxyTypeName = "ByRef";
                    }
                    else if (expr is InvocationExpressionSyntax)
                    {
                        // ref-returning call: the callee already returns ByRef<T>
                        var liftedCall = _lifter.SpliceExpression(expr, out var hCall);
                        if (hCall.Count > 0)
                        {
                            var declStatement = SyntaxFactory.LocalDeclarationStatement(
                                SyntaxFactory.VariableDeclaration(
                                    SyntaxFactory.ParseTypeName($"ByRef<{typeArgText}>"),
                                    SyntaxFactory.SingletonSeparatedList(
                                        declarator.WithInitializer(SyntaxFactory.EqualsValueClause(InjectSlotValues(liftedCall))))));
                            return SyntaxFactory.Block(
                                hCall.Append(declStatement));
                        }
                        proxyExpr = liftedCall;
                        proxyTypeName = "ByRef";
                    }
                    else
                    {
                        (proxyTypeName, proxyExpr) =
                            BuildRefLocalAlias(expr, innerType, isReadOnly);
                    }

                    proxiedDeclarators.Add((proxyTypeName, typeArgText,
                        declarator.WithInitializer(SyntaxFactory.EqualsValueClause(proxyExpr))));
                }
                else
                {
                    proxiedDeclarators.Add(("ByRef", innerType.ToString(), declarator));
                }
            }

            UsedHolder = true;

            if (proxiedDeclarators.Count == 1)
            {
                return BuildProxyLocalDeclaration(
                    proxiedDeclarators[0].ProxyTypeName, proxiedDeclarators[0].TypeArg,
                    SyntaxFactory.SingletonSeparatedList(proxiedDeclarators[0].Declarator));
            }

            return SyntaxFactory.Block(proxiedDeclarators.Select(pair =>
                BuildProxyLocalDeclaration(
                    pair.ProxyTypeName, pair.TypeArg,
                    SyntaxFactory.SingletonSeparatedList(pair.Declarator))));
        }

        // C1: non-ref local initializers containing internal ref/out calls
        var hoisted = new List<StatementSyntax>();
        var newVariables = new List<VariableDeclaratorSyntax>();
        bool anyLift = false;
        foreach (var d in node.Declaration.Variables)
        {
            if (d.Initializer != null)
            {
                var lifted = _lifter.SpliceExpression(d.Initializer.Value, out var h);
                if (h.Count > 0)
                {
                    anyLift = true;
                    hoisted.AddRange(h);
                    hoisted.AddRange(_lifter.TakePendingReadbacks());
                    newVariables.Add(d.WithInitializer(
                        SyntaxFactory.EqualsValueClause(InjectSlotValues(lifted))));
                    continue;
                }
            }
            newVariables.Add(d);
        }
        if (!anyLift)
            return base.VisitLocalDeclarationStatement(node);

        var newDeclaration = SyntaxFactory.VariableDeclaration(
            node.Declaration.Type, SyntaxFactory.SeparatedList(newVariables));
        var newStatement = node.WithDeclaration(newDeclaration);
        return HoistedBlock(hoisted.Append(newStatement));
    }

    // ── Ref fields (C12 flips this to fail-fast in its own task) ────────────

    public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node)
    {
        if (node.Declaration.Type is RefTypeSyntax)
        {
            // C12/Q5: the csharp-sdk scan found zero ref-field usages, so per
            // spec the half-broken declaration rewrite becomes an explicit error.
            _diagnostics.Add(new RefOutInRewriterDiagnostic(
                RefOutInDiagnosticCodes.UnproxyablePosition,
                "ref fields are not supported by the ref/out/in preprocessor (no known usage); " +
                "declare the field as a normal field or wrap it explicitly.",
                RefOutInSeverity.Error, node.GetLocation()));
            return base.VisitFieldDeclaration(node);
        }

        return base.VisitFieldDeclaration(node);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static BlockSyntax HoistedBlock(IEnumerable<StatementSyntax> stmts) =>
        SyntaxFactory.Block(stmts).WithAdditionalAnnotations(RefOutInStatementLifter.HoistedBlockAnnotation);

    // ── C5: lambda / local-function ref-out params ──────────────────────────

    /// <summary>Fragment nodes are not in the annotated tree — skip symbol lookups for them.</summary>
    private bool InCurrentTree(SyntaxNode node) =>
        ReferenceEquals(node.SyntaxTree, _semanticModel.SyntaxTree);



    public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
    {
        if (!InCurrentTree(node))
            return node;   // fragment lambda: pre-injected, no further processing

        var sym = _semanticModel.GetDeclaredSymbol(node.Parameter);
        bool holder = sym != null && _annotator.HolderSymbols.Contains(sym);
        _holderNameScopes.Push(new HashSet<string>());
        try
        {
            if (holder)
            {
                CurrentHolderNames.Add(node.Parameter.Identifier.Text);
                var newParam = RewriteParameter(node.Parameter, sym!);
                return base.VisitSimpleLambdaExpression(node.WithParameter(newParam));
            }
            return base.VisitSimpleLambdaExpression(node);
        }
        finally { _holderNameScopes.Pop(); }
    }

    public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
    {
        if (!InCurrentTree(node))
            return node;   // fragment lambda: pre-injected, no further processing

        _holderNameScopes.Push(new HashSet<string>());
        try
        {
            var newParams = new List<ParameterSyntax>();
            foreach (var param in node.ParameterList.Parameters)
            {
                var sym = _semanticModel.GetDeclaredSymbol(param);
                if (sym != null && _annotator.HolderSymbols.Contains(sym))
                {
                    CurrentHolderNames.Add(param.Identifier.Text);
                    newParams.Add(RewriteParameter(param, sym));
                }
                else
                {
                    newParams.Add(param);
                }
            }
            var newNode = node.WithParameterList(node.ParameterList.WithParameters(
                SyntaxFactory.SeparatedList(newParams)));
            return base.VisitParenthesizedLambdaExpression(newNode);
        }
        finally { _holderNameScopes.Pop(); }
    }

    public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node)
    {
        if (!InCurrentTree(node))
            return node;   // fragment lambda: pre-injected, no further processing

        if (node.ParameterList == null)
            return base.VisitAnonymousMethodExpression(node);
        _holderNameScopes.Push(new HashSet<string>());
        try
        {
            var newParams = new List<ParameterSyntax>();
            foreach (var param in node.ParameterList.Parameters)
            {
                var sym = _semanticModel.GetDeclaredSymbol(param);
                if (sym != null && _annotator.HolderSymbols.Contains(sym))
                {
                    CurrentHolderNames.Add(param.Identifier.Text);
                    newParams.Add(RewriteParameter(param, sym));
                }
                else
                {
                    newParams.Add(param);
                }
            }
            var newNode = node.WithParameterList(node.ParameterList.WithParameters(
                SyntaxFactory.SeparatedList(newParams)));
            return base.VisitAnonymousMethodExpression(newNode);
        }
        finally { _holderNameScopes.Pop(); }
    }

    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        if (!InCurrentTree(node))
            return node;   // fragment lambda: pre-injected, no further processing

        var symbol = _semanticModel.GetDeclaredSymbol(node);
        if (symbol == null)
            return base.VisitLocalFunctionStatement(node);

        _holderNameScopes.Push(new HashSet<string>());
        try
        {
            var newParams = new List<ParameterSyntax>();
            foreach (var param in node.ParameterList.Parameters)
            {
                var paramSymbol = symbol.Parameters.FirstOrDefault(p => p.Name == param.Identifier.Text);
                if (paramSymbol != null && _annotator.HolderSymbols.Contains(paramSymbol))
                {
                    CurrentHolderNames.Add(param.Identifier.Text);
                    newParams.Add(RewriteParameter(param, paramSymbol));
                }
                else
                {
                    newParams.Add(param);
                }
            }
            var newNode = node.WithParameterList(node.ParameterList.WithParameters(
                SyntaxFactory.SeparatedList(newParams)));
            return base.VisitLocalFunctionStatement(newNode);
        }
        finally { _holderNameScopes.Pop(); }
    }

    // ── C11: CS2J3304 — ref struct holder forwarded to an external ref API ──

    public override SyntaxNode? VisitArgument(ArgumentSyntax node)
    {
        if (node.Expression is IdentifierNameSyntax id
            && (node.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)
                || node.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
            && IsHolderName(id.Identifier.Text))
        {
            var invocation = node.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault();
            var target = invocation != null ? ResolveInvocation(invocation) : null;
            if (target != null && !IsInternalCandidate(target))
            {
                var sym = GetSymbol(id);
                var type = sym switch
                {
                    ILocalSymbol l => l.Type,
                    IParameterSymbol p => p.Type,
                    IFieldSymbol f => f.Type,
                    _ => null,
                };
                if (type != null && type.IsRefLikeType)
                    Report3304(id,
                        $"ref struct holder '{id.Identifier.Text}' forwarded to external ref API " +
                        $"'{target.ToDisplayString()}' — C# cannot express this through a holder (D3 §9.4)");
            }
        }
        return base.VisitArgument(node);
    }

    // ── C4: ref-return proxies ──────────────────────────────────────────────

    private ExpressionSyntax BuildReturnProxy(ExpressionSyntax inner)
    {
        var type = GetTypeOf(inner);
        var t = type != null ? TypeDisplay(type) : "int";
        if (inner is ElementAccessExpressionSyntax elem)
        {
            var (arr, indices) = DecomposeElementAccess(elem);
            _stats.ArrayElementProxies++;
            return SyntaxFactory.ParseExpression(
                $"ByRef<{t}>.Create({arr}, {string.Join(", ", indices)})");
        }
        var name = inner.ToString();
        _stats.AccessorProxies++;
        return SyntaxFactory.ParseExpression(
            $"ByRef<{t}>.Create(() => {name}, v => {name} = v)");
    }

    public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        var symbol = _semanticModel.GetDeclaredSymbol(node);
        if (symbol == null || (symbol.RefKind != RefKind.Ref && symbol.RefKind != RefKind.RefReadOnly)
            || node.Type is not RefTypeSyntax refPropType)
            return base.VisitPropertyDeclaration(node);

        UsedHolder = true;
        if (node.ExpressionBody?.Expression is RefExpressionSyntax refArrow)
        {
            var inner = refArrow.Expression;
            while (inner is ParenthesizedExpressionSyntax p) inner = p.Expression;
            node = node.WithExpressionBody(node.ExpressionBody.WithExpression(BuildReturnProxy(inner)));
        }
        var byRefPropType = SyntaxFactory.GenericName(
            SyntaxFactory.Identifier("ByRef"),
            SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList(refPropType.Type)));
        return base.VisitPropertyDeclaration(node.WithType(byRefPropType));
    }

    public override SyntaxNode? VisitIndexerDeclaration(IndexerDeclarationSyntax node)
    {
        var symbol = _semanticModel.GetDeclaredSymbol(node);
        if (symbol == null || (symbol.RefKind != RefKind.Ref && symbol.RefKind != RefKind.RefReadOnly)
            || node.Type is not RefTypeSyntax refIndexerType)
            return base.VisitIndexerDeclaration(node);

        UsedHolder = true;
        var byRefIndexerType = SyntaxFactory.GenericName(
            SyntaxFactory.Identifier("ByRef"),
            SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList(refIndexerType.Type)));
        return base.VisitIndexerDeclaration(node.WithType(byRefIndexerType));
    }

    // C7: ref-local rebinding `r = ref e2` -> `r = <proxy(e2)>`
    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        if (node.Left is IdentifierNameSyntax leftId && IsHolderName(leftId.Identifier.Text)
            && node.Right is RefExpressionSyntax refRebind)
        {
            var inner = refRebind.Expression;
            while (inner is ParenthesizedExpressionSyntax p) inner = p.Expression;
            if (_lifter.ContainsRefStructCapture(inner))
            {
                Report3303(inner, "ref struct member cannot be re-bound through a proxy");
                return base.VisitAssignmentExpression(node);
            }
            UsedHolder = true;
            return node
                .WithLeft(leftId.WithAdditionalAnnotations(RefOutInStatementLifter.DirectPassAnnotation))
                .WithRight(BuildReturnProxy(inner));
        }
        return base.VisitAssignmentExpression(node);
    }

    private static LocalDeclarationStatementSyntax BuildProxyLocalDeclaration(
        string proxyTypeName,
        string typeArgumentDisplay,
        SeparatedSyntaxList<VariableDeclaratorSyntax> declarators) =>
        SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.ParseTypeName($"{proxyTypeName}<{typeArgumentDisplay}>"),
                declarators));

    // ── ref-local declaration-site proxies (C40 M3, decisions 1/3) ───────────

    private string ResolveTypeDisplayName(TypeSyntax declaredInnerType, ExpressionSyntax value)
    {
        // `ref var x = ref location` — the element type comes from the bound
        // initializer; the proxy generic argument must never render as `var`.
        // Holder/call initializers are themselves proxies (ByRef<T>), so peel
        // one proxy layer.
        if (declaredInnerType is IdentifierNameSyntax id && id.IsVar)
        {
            var resolved = GetTypeOf(value);
            if (resolved is INamedTypeSymbol namedProxy
                && namedProxy.ContainingNamespace?.ToDisplayString() == "Cs2jByRef"
                && namedProxy.TypeArguments is [{ } proxyArg])
            {
                resolved = proxyArg;
            }
            if (resolved != null)
                return TypeDisplay(resolved);
        }
        return declaredInnerType.ToString();
    }

    private static bool IsSpanLikeType(ITypeSymbol? type)
        => type is INamedTypeSymbol named
           && named.TypeArguments.Length == 1
           && named.ContainingNamespace?.ToDisplayString() == "System"
           && (named.MetadataName == "Span`1" || named.MetadataName == "ReadOnlySpan`1");

    /// <summary>
    /// True when the bound member of an element/member access is a
    /// ref/ref-readonly-returning property or indexer DECLARED IN SOURCE.
    /// This pass rewrites those very declarations to ByRef&lt;T&gt;-returning
    /// members (VisitPropertyDeclaration/VisitIndexerDeclaration), so a
    /// ref-local initializer over one of them is holder-to-holder flow:
    /// <c>ByRef&lt;T&gt; local = access;</c>. Framework/metadata members
    /// (arrays, Span&lt;T&gt; indexers) keep their ref form and are classified
    /// via the array/ScopedByRef/accessor shapes below instead.
    /// </summary>
    private bool IsSourceDefinedRefReturningMember(ExpressionSyntax expr)
    {
        return GetSymbol(expr) is IPropertySymbol property
            && (property.RefKind == RefKind.Ref || property.RefKind == RefKind.RefReadOnly)
            && property.Locations.Any(static l => l.IsInSource);
    }

    /// <summary>
    /// True when the expression cannot be textually embedded in a lambda:
    /// uses a ref struct local/parameter/field (CS8175/CS9108), a ref/out/in
    /// parameter or a ref local (CS8175/CS1628), or <c>this</c> of a ref struct
    /// (CS9108). Holder names are evaluated against their *rewritten* storage —
    /// ByRef holders are heap classes (safe to capture), RefStructByRef holders
    /// stay ref-like (still blocking).
    /// </summary>
    private bool HasLambdaBlockingCapture(ExpressionSyntax expr)
    {
        var enclosingMethod = expr.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>();
        var enclosingType = enclosingMethod != null
            ? _semanticModel.GetDeclaredSymbol(enclosingMethod)?.ContainingType
            : null;
        if (enclosingType?.IsRefLikeType == true
            && expr.DescendantNodesAndSelf().OfType<ThisExpressionSyntax>().Any())
            return true;

        foreach (var id in expr.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            var sym = GetSymbol(id);
            ITypeSymbol? type;
            bool byRef;
            bool isHolder;
            switch (sym)
            {
                case ILocalSymbol l:
                    type = l.Type;
                    byRef = l.RefKind != RefKind.None;
                    isHolder = IsHolderName(id.Identifier.Text);
                    break;
                case IParameterSymbol p:
                    type = p.Type;
                    byRef = p.RefKind != RefKind.None;
                    isHolder = _annotator.HolderSymbols.Contains(p);
                    break;
                case IFieldSymbol f:
                    type = f.Type;
                    byRef = false;
                    isHolder = false;
                    break;
                default:
                    continue;
            }

            if (type != null && type.IsRefLikeType)
                return true;
            // A holder is rewritten to a heap ByRef unless its carried type is
            // itself ref-like (handled above) — raw ref-ness is irrelevant
            // for holders.
            if (byRef && !isHolder)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Builds the proxy factory for a <c>ref</c>/<c>ref readonly</c> local
    /// initializer. Receiver classification (C40 M3 hard prerequisite;
    /// decision 1's lambda form is refined because ref struct receivers make
    /// closures uncompilable — CS8175/CS9108, probe-pinned):
    ///
    /// rank-1 array → ByRef.Create(receiver, index) (unchanged);
    /// Span/ReadOnlySpan receiver → ScopedByRef/ReadOnlyScopedByRef
    /// .Create(receiver, index) (ref field alias, no closure; Java maps to
    /// existing ByRef/ReadOnlyByRef create(span, i) factories);
    /// other ref-like receiver or lambda-blocking capture →
    /// ScopedByRef/ReadOnlyScopedByRef.Create(ref/in location);
    /// heap receivers → accessor closures ByRef/ReadOnlyByRef.Create(getter…).
    /// Read-only forms expose a get-only Value, so a write fails at compile
    /// time (CS0200) in the intermediate tree.
    /// </summary>
    private (string ProxyTypeName, ExpressionSyntax Proxy) BuildRefLocalAlias(
        ExpressionSyntax expr,
        TypeSyntax declaredInnerType,
        bool isReadOnly)
    {
        var typeDisplay = ResolveTypeDisplayName(declaredInnerType, expr);
        var blocking = HasLambdaBlockingCapture(expr);

        // Source-defined ref-returning property/indexer: the declaration
        // itself is rewritten in this same pass to a ByRef<T>-returning
        // member, so the access expression already yields a holder.
        if (IsSourceDefinedRefReturningMember(expr))
        {
            return ("ByRef", InjectSlotValues(expr));
        }

        if (expr is ElementAccessExpressionSyntax elem)
        {
            var receiverType = GetTypeOf(elem.Expression);
            var injected = (ElementAccessExpressionSyntax)InjectSlotValues(elem);
            var elemText = injected.ToString();
            var receiverText = injected.Expression.ToString();
            var indices = injected.ArgumentList.Arguments.Select(a => a.Expression).ToList();
            var singleIndex = indices.Count == 1 ? indices[0].ToString() : null;

            if (receiverType is IArrayTypeSymbol arrayType)
            {
                if (arrayType.Rank != 1)
                {
                    Report3303(elem,
                        $"multi-dimensional array element ref (rank {arrayType.Rank}); " +
                        "only rank-1 array proxies are supported");
                    _stats.ArrayElementProxies++;
                    return ("ByRef", SyntaxFactory.ParseExpression(
                        $"ByRef<{typeDisplay}>.Create({receiverText}, {string.Join(", ", indices)})"));
                }

                if (isReadOnly)
                {
                    if (blocking)
                    {
                        _stats.ScopedElementProxies++;
                        return ("ReadOnlyScopedByRef", SyntaxFactory.ParseExpression(
                            $"ReadOnlyScopedByRef<{typeDisplay}>.Create(in {elemText})"));
                    }
                    _stats.ReadOnlyProxies++;
                    return ("ReadOnlyByRef", SyntaxFactory.ParseExpression(
                        $"ReadOnlyByRef<{typeDisplay}>.Create(() => {elemText})"));
                }

                _stats.ArrayElementProxies++;
                return ("ByRef", SyntaxFactory.ParseExpression(
                    $"ByRef<{typeDisplay}>.Create({receiverText}, {singleIndex})"));
            }

            if (singleIndex != null && IsSpanLikeType(receiverType))
            {
                if (isReadOnly)
                {
                    _stats.ScopedElementProxies++;
                    return ("ReadOnlyScopedByRef", SyntaxFactory.ParseExpression(
                        $"ReadOnlyScopedByRef<{typeDisplay}>.Create({receiverText}, {singleIndex})"));
                }
                _stats.ScopedElementProxies++;
                return ("ScopedByRef", SyntaxFactory.ParseExpression(
                    $"ScopedByRef<{typeDisplay}>.Create({receiverText}, {singleIndex})"));
            }

            if (receiverType?.IsRefLikeType == true || blocking)
            {
                if (isReadOnly)
                {
                    _stats.ScopedElementProxies++;
                    return ("ReadOnlyScopedByRef", SyntaxFactory.ParseExpression(
                        $"ReadOnlyScopedByRef<{typeDisplay}>.Create(in {elemText})"));
                }
                _stats.ScopedElementProxies++;
                return ("ScopedByRef", SyntaxFactory.ParseExpression(
                    $"ScopedByRef<{typeDisplay}>.Create(ref {elemText})"));
            }

            if (isReadOnly)
            {
                _stats.ReadOnlyProxies++;
                return ("ReadOnlyByRef", SyntaxFactory.ParseExpression(
                    $"ReadOnlyByRef<{typeDisplay}>.Create(() => {elemText})"));
            }
            _stats.AccessorProxies++;
            return ("ByRef", SyntaxFactory.ParseExpression(
                $"ByRef<{typeDisplay}>.Create(() => {elemText}, v => {elemText} = v)"));
        }

        // identifier / member chain / other lvalue shapes
        var nameText = InjectSlotValues(expr).ToString();
        if (isReadOnly)
        {
            if (blocking)
            {
                _stats.ScopedElementProxies++;
                return ("ReadOnlyScopedByRef", SyntaxFactory.ParseExpression(
                    $"ReadOnlyScopedByRef<{typeDisplay}>.Create(in {nameText})"));
            }
            _stats.ReadOnlyProxies++;
            return ("ReadOnlyByRef", SyntaxFactory.ParseExpression(
                $"ReadOnlyByRef<{typeDisplay}>.Create(() => {nameText})"));
        }
        if (blocking)
        {
            _stats.ScopedElementProxies++;
            return ("ScopedByRef", SyntaxFactory.ParseExpression(
                $"ScopedByRef<{typeDisplay}>.Create(ref {nameText})"));
        }
        _stats.AccessorProxies++;
        return ("ByRef", SyntaxFactory.ParseExpression(
            $"ByRef<{typeDisplay}>.Create(() => {nameText}, v => {nameText} = v)"));
    }

    private static ExpressionSyntax Negate(ExpressionSyntax e) =>
        SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression,
            SyntaxFactory.ParenthesizedExpression(e));

    private (string?, List<string>) DecomposeElementAccess(ElementAccessExpressionSyntax elem)
    {
        var arrExpr = elem.Expression.ToString();
        var indices = elem.ArgumentList.Arguments.Select(a => a.Expression.ToString()).ToList();
        return (arrExpr, indices);
    }

    /// <summary>
    /// Pre-injects .Value for holder-slot identifiers inside generated fragments.
    /// Idempotent via ValueInjectedAnnotation; respects DirectPassAnnotation.
    /// </summary>
    private sealed class SlotInjector : CSharpSyntaxRewriter
    {
        private readonly RefOutInSyntaxRewriter _owner;

        public SlotInjector(RefOutInSyntaxRewriter owner) => _owner = owner;

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (node.HasAnnotation(RefOutInStatementLifter.ValueInjectedAnnotation)
                || node.HasAnnotation(RefOutInStatementLifter.DirectPassAnnotation))
                return node;
            if (IsInDeclarationContext(node) || !_owner.IsHolderName(node.Identifier.Text))
                return base.VisitIdentifierName(node);
            _owner._stats.HoldersInjected++;
            return SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                node.WithAdditionalAnnotations(RefOutInStatementLifter.ValueInjectedAnnotation),
                SyntaxFactory.IdentifierName("Value"));
        }
    }
}
