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

            // Ref return → ByRef return
            TypeSyntax newReturnType = node.ReturnType;
            bool refReturn = (symbol.RefKind == RefKind.Ref || symbol.RefKind == RefKind.RefReadOnly)
                && node.ReturnType is RefTypeSyntax;
            if (refReturn && node.ReturnType is RefTypeSyntax refReturnType)
            {
                newReturnType = refReturnType.Type;
                UsedHolder = true;
            }

            bool signatureChanged = newParamList.Count != node.ParameterList.Parameters.Count
                || node.ParameterList.Parameters
                    .Where((p, i) => !ReferenceEquals(newParamList[i], p)).Any()
                || refReturn;

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
            newReturnType = refType.Type;
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
        stmts.Add(SyntaxFactory.ExpressionStatement(InjectSlotValues(lifted)));
        stmts.AddRange(pending);
        return HoistedBlock(stmts);
    }

    public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
    {
        if (node.Expression == null)
            return base.VisitReturnStatement(node);

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
            if (ReferenceEquals(lifted, expression))
                return base.Visit(node)!;
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
            var newDeclarators = new List<VariableDeclaratorSyntax>();
            foreach (var declarator in node.Declaration.Variables)
            {
                if (declarator.Initializer is EqualsValueClauseSyntax init)
                {
                    var expr = init.Value;
                    if (expr is RefExpressionSyntax refExpr)
                        expr = refExpr.Expression;

                    var typeName = innerType.ToString();

                    ExpressionSyntax proxyExpr;
                    if (expr is IdentifierNameSyntax id && IsHolderName(id.Identifier.Text))
                    {
                        proxyExpr = expr;
                    }
                    else if (expr is ElementAccessExpressionSyntax elem)
                    {
                        var (arr, indices) = DecomposeElementAccess(elem);
                        proxyExpr = SyntaxFactory.ParseExpression(
                            $"ByRef<{typeName}>.Create({arr}, {string.Join(", ", indices)})");
                        _stats.ArrayElementProxies++;
                    }
                    else
                    {
                        var varName = expr.ToString();
                        proxyExpr = SyntaxFactory.ParseExpression(
                            $"ByRef<{typeName}>.Create(() => {varName}, v => {varName} = v)");
                        _stats.AccessorProxies++;
                    }

                    newDeclarators.Add(declarator.WithInitializer(
                        SyntaxFactory.EqualsValueClause(proxyExpr)));
                }
                else
                {
                    newDeclarators.Add(declarator);
                }
            }

            var newDecl = SyntaxFactory.VariableDeclaration(
                SyntaxFactory.GenericName(
                    SyntaxFactory.Identifier("ByRef"),
                    SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList(innerType))),
                SyntaxFactory.SeparatedList(newDeclarators));

            UsedHolder = true;
            return SyntaxFactory.LocalDeclarationStatement(newDecl);
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
        if (node.Declaration.Type is RefTypeSyntax refType)
        {
            var innerType = refType.Type;
            var newDecl = SyntaxFactory.VariableDeclaration(
                SyntaxFactory.GenericName(
                    SyntaxFactory.Identifier("ByRef"),
                    SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList(innerType))),
                node.Declaration.Variables);
            UsedHolder = true;
            return node.WithDeclaration(newDecl);
        }

        return base.VisitFieldDeclaration(node);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static BlockSyntax HoistedBlock(IEnumerable<StatementSyntax> stmts) =>
        SyntaxFactory.Block(stmts).WithAdditionalAnnotations(RefOutInStatementLifter.HoistedBlockAnnotation);

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
