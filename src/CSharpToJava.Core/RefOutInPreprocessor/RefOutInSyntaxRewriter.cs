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

    // Value-typed held-lambda frames: holder name → shadow local name. A frame
    // is pushed only by the anonymous-function visitors; a null value is a
    // sentinel for a frame whose same-named holder stays a direct ByRef
    // (reference T), masking an outer frame's shadow of the same name.
    private readonly Stack<Dictionary<string, string?>> _shadowNameScopes = new();

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

    /// <summary>
    /// True when the holder name denotes a value-typed held-lambda parameter in
    /// the innermost frame that owns it: body references then bind to the
    /// shadow copy-in/copy-out local instead of <c>holder.Value</c> (CS1612).
    /// </summary>
    internal bool TryGetShadowName(string name, out string shadowName)
    {
        // Stack enumerates top (innermost) first.
        foreach (var frame in _shadowNameScopes)
        {
            if (frame.TryGetValue(name, out var mapped))
            {
                shadowName = mapped!;
                return mapped != null;
            }
        }
        shadowName = null!;
        return false;
    }

    internal IMethodSymbol? ResolveInvocation(InvocationExpressionSyntax inv)
    {
        try { return _semanticModel.GetSymbolInfo(inv).Symbol as IMethodSymbol; }
        catch (ArgumentException) { return null; }
    }

    /// <summary>
    /// Resolve the set of internal methods a byref call may bind to. Normally
    /// this is the unique <see cref="SymbolInfo.Symbol"/>; when overload
    /// resolution FAILS over ARGUMENTS THE REWRITE DOES NOT TOUCH, every
    /// reported candidate is still returned provided they agree on the byref
    /// shape. The pass binds one model for the WHOLE solution graph, so
    /// per-project source substitutions (e.g. test fakes that replace a
    /// production type) make unrelated arguments ambiguous (CS0229) even though
    /// each project compiles cleanly by itself; the ref/out arguments and
    /// their parameter mapping stay unambiguous in that case, and leaving the
    /// call untouched is what produces CS1615 against the lifted declaration.
    /// A third failure mode is a sibling project NAMESPACE shadowing the
    /// receiver TYPE in the enclosing-namespace lookup chain (Symbol=null,
    /// CandidateReason=None, zero candidates — e.g. a test-only namespace
    /// <c>dotnet.system.Net.Test.Uri</c> shadowing source type
    /// <c>dotnet.system.Uri</c>); that case is recovered by
    /// <see cref="TryGetNamespaceShadowedTypeLiftMethods"/>.
    /// </summary>
    internal bool TryGetLiftMethods(
        InvocationExpressionSyntax inv,
        out IReadOnlyList<IMethodSymbol> methods)
    {
        methods = System.Array.Empty<IMethodSymbol>();

        Microsoft.CodeAnalysis.SymbolInfo info;
        try { info = _semanticModel.GetSymbolInfo(inv); }
        catch (ArgumentException) { return false; }

        if (info.Symbol is IMethodSymbol single)
        {
            if (IsInternalCandidate(single))
            {
                methods = new[] { single };
                return true;
            }

            return false;
        }

        if (info.CandidateReason == CandidateReason.OverloadResolutionFailure
            && info.CandidateSymbols.Length > 0)
        {
            var candidates = new List<IMethodSymbol>();
            foreach (var candidate in info.CandidateSymbols)
            {
                if (candidate is not IMethodSymbol candidateMethod
                    || !IsInternalCandidate(candidateMethod))
                {
                    return false;
                }

                candidates.Add(candidateMethod);
            }

            if (!RefArgumentShapeAgrees(inv, candidates))
            {
                return false;
            }

            methods = candidates;
            return true;
        }

        return TryGetNamespaceShadowedTypeLiftMethods(inv, out methods);
    }

    /// <summary>
    /// Recovery for static calls <c>T.M(...)</c> whose simple-name receiver
    /// binds to a NAMESPACE instead of the intended TYPE in the merged
    /// solution-graph model. One model is bound for every project's sources, so
    /// a namespace declared by a SIBLING project (absent from the call site's
    /// own per-project build) can shadow a source type declared in an outer
    /// enclosing namespace: C# lookup takes the inner namespace member and
    /// never walks outward, yielding Symbol=null/CandidateReason=None with a
    /// CS0234 diagnostic. Each project compiles cleanly on its own, where the
    /// type (not the foreign namespace) is visible.
    ///
    /// Recovery replays just the enclosing-namespace part of name lookup,
    /// ignoring namespace symbols: the FIRST outer scope containing a type of
    /// the receiver's name wins (C# precedence; duplicate source declarations
    /// merge into one symbol). The call is liftable only when every
    /// same-arity static method of that name is an internal SOURCE candidate
    /// whose byref shape agrees — a metadata-only type (a real framework/BCL
    /// method) has no source location and therefore never matches, so genuine
    /// external out/ref calls are still left untouched.
    /// </summary>
    private bool TryGetNamespaceShadowedTypeLiftMethods(
        InvocationExpressionSyntax inv,
        out IReadOnlyList<IMethodSymbol> methods)
    {
        methods = System.Array.Empty<IMethodSymbol>();

        if (inv.Expression is not MemberAccessExpressionSyntax memberAccess
            || memberAccess.Expression is not IdentifierNameSyntax receiverName)
        {
            return false;
        }

        Microsoft.CodeAnalysis.SymbolInfo receiverInfo;
        try { receiverInfo = _semanticModel.GetSymbolInfo(receiverName); }
        catch (ArgumentException) { return false; }

        // Only the namespace-shadowing case: the bare receiver actually binds
        // to a namespace symbol. A metadata type, local or alias here is some
        // other failure mode the existing paths must decide.
        if (receiverInfo.Symbol is not INamespaceSymbol)
        {
            return false;
        }

        var receiverText = receiverName.Identifier.Text;
        INamedTypeSymbol? receiverType = null;
        for (var scope = _semanticModel.GetEnclosingSymbol(inv.SpanStart)?.ContainingNamespace;
             scope != null;
             scope = scope.ContainingNamespace)
        {
            List<INamedTypeSymbol>? scopeTypes = null;
            try
            {
                scopeTypes = _semanticModel
                    .LookupSymbols(inv.SpanStart, scope, receiverText)
                    .OfType<INamedTypeSymbol>()
                    .ToList();
            }
            catch (ArgumentException) { return false; }

            if (scopeTypes.Count == 0)
            {
                continue;
            }

            // First enclosing scope that declares a type of this name wins.
            // A metadata-only twin at the winning scope means the target is a
            // real external type: refuse rather than lifting against it.
            if (scopeTypes.Any(t => !t.Locations.Any(l => l.IsInSource)))
            {
                return false;
            }

            receiverType = scopeTypes[0];
            break;
        }

        if (receiverType == null)
        {
            return false;
        }

        var argumentCount = inv.ArgumentList.Arguments.Count;
        var candidates = receiverType
            .GetMembers(memberAccess.Name.Identifier.Text)
            .OfType<IMethodSymbol>()
            .Where(m => m.IsStatic
                && m.Parameters.Length == argumentCount
                && !m.Parameters.Any(p => p.IsParams))
            .ToList();

        if (candidates.Count == 0)
        {
            return false;
        }

        if (!candidates.All(IsInternalCandidate))
        {
            return false;
        }

        if (!RefArgumentShapeAgrees(inv, candidates))
        {
            return false;
        }

        methods = candidates;
        return true;
    }

    /// <summary>
    /// True when every candidate exposes the same ref/out parameter
    /// (RefKind and type) at every syntactically-byref argument position, all
    /// candidates have matching arity/void-ness and use no named/in/params
    /// arguments (which the fallback cannot map positionally).
    /// </summary>
    private static bool RefArgumentShapeAgrees(
        InvocationExpressionSyntax inv,
        IReadOnlyList<IMethodSymbol> candidates)
    {
        var arguments = inv.ArgumentList.Arguments;

        var returnsVoid = candidates[0].ReturnsVoid;
        foreach (var candidate in candidates)
        {
            if (candidate.ReturnsVoid != returnsVoid
                || candidate.Parameters.Length != arguments.Count
                || candidate.Parameters.Any(p => p.IsParams))
            {
                return false;
            }
        }

        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            var isRef = argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword);
            var isOut = argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword);
            if (!isRef && !isOut)
            {
                if (argument.RefKindKeyword.IsKind(SyntaxKind.InKeyword)
                    || argument.NameColon != null)
                {
                    return false;
                }

                continue;
            }

            var expectedKind = isOut ? RefKind.Out : RefKind.Ref;
            var expectedType = candidates[0].Parameters[i].Type;
            foreach (var candidate in candidates)
            {
                var parameter = candidate.Parameters[i];
                if (parameter.RefKind != expectedKind
                    || !SymbolEqualityComparer.Default.Equals(parameter.Type, expectedType))
                {
                    return false;
                }
            }
        }

        return true;
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
        Dictionary<string, string?>? shadowFrame = null;
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
            var holderParams = new List<(string Name, IParameterSymbol Symbol)>();
            foreach (var param in node.ParameterList.Parameters)
            {
                var paramSymbol = symbol.Parameters.FirstOrDefault(p => p.Name == param.Identifier.Text);
                if (paramSymbol != null && _annotator.HolderSymbols.Contains(paramSymbol))
                {
                    CurrentHolderNames.Add(param.Identifier.Text);
                    holderParams.Add((param.Identifier.Text, paramSymbol));
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

            // Value-typed holders get a shadow copy-in/copy-out frame inside
            // the body (same CS1612 lowering as for held lambda parameters).
            shadowFrame = PushHolderShadowFrame(holderParams,
                (SyntaxNode?)node.Body ?? node.ExpressionBody, true, out var shadows);
            bool shadowReturnsVoid = symbol.ReturnsVoid;
            string? shadowReturnType = symbol.ReturnsVoid
                ? null
                : (refReturn
                    ? "ByRef<" + TypeDisplay(symbol.ReturnType) + ">"
                    : TypeDisplay(symbol.ReturnType));

            var newParams = SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(newParamList));
            var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
            visited = visited.WithParameterList(newParams).WithReturnType(newReturnType);

            if (shadows.Count > 0)
                visited = ApplyMethodShadows(visited, shadows, shadowReturnsVoid, shadowReturnType);

            if (signatureChanged) UsedHolder = true;
            return visited;
        }
        finally
        {
            if (shadowFrame != null) _shadowNameScopes.Pop();
            _holderNameScopes.Pop();
        }
    }

    /// <summary>
    /// Wraps a rewritten method body (block or expression) with the holder
    /// shadow frame; expression-bodied methods are converted to block bodies.
    /// </summary>
    private MethodDeclarationSyntax ApplyMethodShadows(
        MethodDeclarationSyntax node,
        IReadOnlyList<(string Holder, string Shadow, string Type)> shadows,
        bool returnsVoid,
        string? returnTypeDisplay)
    {
        BlockSyntax block;
        if (node.Body != null)
        {
            block = node.Body;
        }
        else if (node.ExpressionBody != null)
        {
            block = BlockFromShadowExpression(node.ExpressionBody.Expression, returnsVoid);
        }
        else
        {
            return node;
        }

        return node
            .WithBody(BuildShadowBlock(block, shadows, returnsVoid, returnTypeDisplay))
            .WithExpressionBody(null)
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.None));
    }

    private static bool NeedsInStrip(ParameterSyntax param) =>
        param.Modifiers.Any(m => m.IsKind(SyntaxKind.InKeyword) || m.IsKind(SyntaxKind.ScopedKeyword));

    private MethodDeclarationSyntax ConvertExpressionBody(MethodDeclarationSyntax node)
    {
        var lifted = _lifter.SpliceExpression(node.ExpressionBody!.Expression, out var hoisted);
        var stmts = new List<StatementSyntax>();
        stmts.AddRange(hoisted);
        // A void expression body (`=> M(out x);`) must become an expression
        // statement; wrapping the lifted call in `return` is CS0127.
        if (node.ReturnType is PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.VoidKeyword })
        {
            stmts.Add(SyntaxFactory.ExpressionStatement(InjectSlotValues(lifted)));
        }
        else
        {
            stmts.Add(SyntaxFactory.ReturnStatement(InjectSlotValues(lifted)));
        }

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
        Dictionary<string, string?>? shadowFrame = null;
        try
        {
            if (node.ExpressionBody != null && _lifter.ContainsLiftable(node.ExpressionBody.Expression))
                Report3303(node.ExpressionBody.Expression,
                    "constructor expression body contains an internal ref/out call; convert to a block body");

            var newParamList = new List<ParameterSyntax>();
            var holderParams = new List<(string Name, IParameterSymbol Symbol)>();
            foreach (var param in node.ParameterList.Parameters)
            {
                var paramSymbol = symbol.Parameters.FirstOrDefault(p => p.Name == param.Identifier.Text);
                if (paramSymbol != null && _annotator.HolderSymbols.Contains(paramSymbol))
                {
                    CurrentHolderNames.Add(param.Identifier.Text);
                    holderParams.Add((param.Identifier.Text, paramSymbol));
                    newParamList.Add(RewriteParameter(param, paramSymbol));
                }
                else
                {
                    newParamList.Add(param);
                }
            }

            shadowFrame = PushHolderShadowFrame(holderParams,
                (SyntaxNode?)node.Body ?? node.ExpressionBody, true, out var shadows);
            var newParams = SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(newParamList));
            var visited = (ConstructorDeclarationSyntax)base.VisitConstructorDeclaration(node)!;
            visited = visited.WithParameterList(newParams);
            if (shadows.Count > 0)
            {
                if (visited.Body != null)
                {
                    visited = visited.WithBody(
                        BuildShadowBlock(visited.Body, shadows, returnsVoid: true, returnTypeDisplay: null));
                }
                else if (visited.ExpressionBody != null)
                {
                    var block = BlockFromShadowExpression(visited.ExpressionBody.Expression, returnsVoid: true);
                    visited = visited
                        .WithBody(BuildShadowBlock(block, shadows, true, null))
                        .WithExpressionBody(null)
                        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.None));
                }
            }
            return visited;
        }
        finally
        {
            if (shadowFrame != null) _shadowNameScopes.Pop();
            _holderNameScopes.Pop();
        }
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
        if (TryGetShadowName(name, out var shadowName))
        {
            // Value-typed held lambda: bind to the shadow local (a variable),
            // never to holder.Value (a property return value → CS1612).
            return SyntaxFactory.IdentifierName(shadowName)
                .WithAdditionalAnnotations(RefOutInStatementLifter.ValueInjectedAnnotation);
        }

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

    // ── CS0206: Interlocked.X(ref holder, …) → Cs2jInterlocked.X(holder, …) ──
    // A holder's Value is a ByRef<T> property, never a variable, so the BCL
    // Interlocked methods cannot take it by ref. The Cs2jByRef support class
    // provides lock-based equivalents with the same observable semantics.

    private static readonly HashSet<string> InterlockedHolderOps = new()
    {
        "Exchange", "CompareExchange", "Or", "And", "Add", "Increment", "Decrement",
    };

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        if (TryRewriteInterlockedHolderCall(node) is { } rewritten)
            return rewritten;
        return base.VisitInvocationExpression(node);
    }

    private InvocationExpressionSyntax? TryRewriteInterlockedHolderCall(InvocationExpressionSyntax node)
    {
        if (node.ArgumentList.Arguments.Count == 0)
            return null;

        var first = node.ArgumentList.Arguments[0];
        if (!first.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)
            && !first.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
            return null;

        if (first.Expression is not IdentifierNameSyntax holderId
            || !IsHolderName(holderId.Identifier.Text))
            return null;

        var sym = ResolveInvocation(node);
        if (sym == null
            || sym.ContainingType?.ToDisplayString() != "System.Threading.Interlocked"
            || !InterlockedHolderOps.Contains(sym.Name))
            return null;

        MarkHolderUsed();
        ExpressionSyntax holder;
        if (TryGetShadowName(holderId.Identifier.Text, out var shadowName))
        {
            // Inside a value-typed held-lambda frame the atomic op must target
            // the shadow local; forward through an accessor proxy so results
            // flow through copy-out.
            var holderSym = GetSymbol(holderId);
            var t = holderSym is IParameterSymbol ps ? TypeDisplay(ps.Type) : "object";
            holder = SyntaxFactory.ParseExpression(
                $"ByRef<{t}>.Create(() => {shadowName}, v => {shadowName} = v)");
        }
        else
        {
            holder = SyntaxFactory.IdentifierName(holderId.Identifier)
                .WithAdditionalAnnotations(RefOutInStatementLifter.DirectPassAnnotation);
        }

        // Remaining arguments still need ordinary holder .Value injection.
        var arguments = new List<ArgumentSyntax> { SyntaxFactory.Argument(holder) };
        foreach (var arg in node.ArgumentList.Arguments.Skip(1))
        {
            var visited = (ExpressionSyntax)Visit(arg.Expression)!;
            arguments.Add(SyntaxFactory.Argument(visited));
        }

        var target = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.ParseName("global::Cs2jByRef.Cs2jInterlocked"),
            SyntaxFactory.IdentifierName(sym.Name));
        return SyntaxFactory.InvocationExpression(
            target,
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments)));
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

        // Every child is walked exactly once. An untouched spliced condition
        // may still contain holder identifier reads (-> .Value), so it goes
        // through the normal visit; a spliced remainder is finalized by the
        // slot injector. Never re-dispatch base.VisitIfStatement(node) here:
        // the then/else subtrees were already visited above, and a second
        // dispatch walks them again — 2^d visits on nested if-else chains.
        var condChanged = !ReferenceEquals(cond, node.Condition);
        var newCondition = condChanged
            ? InjectSlotValues(cond)
            : (ExpressionSyntax)base.Visit(node.Condition)!;
        var visitedThen = (StatementSyntax)base.Visit(node.Statement)!;
        StatementSyntax? visitedElse = node.Else != null
            ? (StatementSyntax)base.Visit(node.Else.Statement)!
            : null;
        ElseClauseSyntax? elseClause = visitedElse != null
            ? SyntaxFactory.ElseClause(visitedElse)
            : null;

        if (hoisted.Count == 0)
        {
            if (!condChanged
                && ReferenceEquals(newCondition, node.Condition)
                && ReferenceEquals(visitedThen, node.Statement)
                && (node.Else == null || ReferenceEquals(visitedElse, node.Else.Statement)))
            {
                return node;
            }

            return SyntaxFactory.IfStatement(newCondition, visitedThen, elseClause);
        }

        var newIf = SyntaxFactory.IfStatement(newCondition, visitedThen, elseClause);
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
        var cond = _lifter.SpliceExpression(node.Condition, out var hoisted);
        var pending = _lifter.TakePendingReadbacks();
        bool condChanged = !ReferenceEquals(cond, node.Condition);

        if (hoisted.Count == 0 && pending.Count == 0)
        {
            if (!condChanged)
                return base.VisitDoStatement(node);
            var visitedBodyOnly = (StatementSyntax)base.Visit(node.Statement)!;
            return node.WithStatement(visitedBodyOnly).WithCondition(InjectSlotValues(cond));
        }

        // do B while (C) with lifted condition calls becomes:
        //   while (true) { <rootDecls>; B; <evalStmts>; if (!C') break; }
        // The body runs BEFORE the condition is evaluated, so only pure `out
        // var x` declarations (rootDecls) may precede it; every other hoisted
        // statement (proxy slots, the call, read-backs) stays after the body.
        var visitedBody = (StatementSyntax)base.Visit(node.Statement)!;

        var rootDecls = new List<StatementSyntax>();
        var evalStmts = new List<StatementSyntax>();
        foreach (var stmt in hoisted)
        {
            if (stmt.HasAnnotation(RefOutInStatementLifter.RootDeclarationAnnotation))
                rootDecls.Add(stmt);
            else
                evalStmts.Add(stmt);
        }
        evalStmts.AddRange(pending);

        var scanner = new DoBodyJumpScanner();
        scanner.Visit(visitedBody);

        var outerStmts = new List<StatementSyntax>();
        outerStmts.AddRange(rootDecls);

        if (!scanner.HasContinue)
        {
            // A direct `break` inside B binds to the enclosing while(true)
            // unchanged; nothing maps.
            outerStmts.Add(visitedBody);
        }
        else
        {
            // A direct `continue` must jump to condition evaluation, whereas a
            // plain continue in the while(true) form would re-enter B before C
            // runs. Wrap B in a do-once loop: continue -> inner break (falls
            // through to the condition code); a direct break needs an escape
            // flag to leave BOTH loops.
            string? brkVar = scanner.HasBreak ? $"__cs2jDoBrk{NextTemp()}" : null;
            if (brkVar != null)
                outerStmts.Add(BoolFalseDeclaration(brkVar));
            var mappedBody = (StatementSyntax)new DoBodyJumpMapper(brkVar).Visit(visitedBody)!;
            outerStmts.Add(SyntaxFactory.DoStatement(
                SyntaxFactory.Block(mappedBody),
                SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)));
            if (brkVar != null)
                outerStmts.Add(SyntaxFactory.IfStatement(
                    SyntaxFactory.IdentifierName(brkVar), SyntaxFactory.BreakStatement()));
        }

        outerStmts.AddRange(evalStmts);
        outerStmts.Add(SyntaxFactory.IfStatement(
            Negate(InjectSlotValues(cond)), SyntaxFactory.BreakStatement()));

        return SyntaxFactory.WhileStatement(
            SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression),
            SyntaxFactory.Block(outerStmts));
    }

    private static LocalDeclarationStatementSyntax BoolFalseDeclaration(string name) =>
        SyntaxFactory.LocalDeclarationStatement(SyntaxFactory.VariableDeclaration(
            SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword)),
            SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(
                SyntaxFactory.Identifier(name),
                null,
                SyntaxFactory.EqualsValueClause(
                    SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression))))));

    /// <summary>
    /// Finds <c>continue</c>/<c>break</c> statements inside a do-while body
    /// that bind to the do loop itself. Jumps nested inside another loop or
    /// switch (break) and anonymous functions / local functions are ignored.
    /// </summary>
    private sealed class DoBodyJumpScanner : CSharpSyntaxRewriter
    {
        private int _loopDepth;
        private int _switchDepth;
        public bool HasContinue;
        public bool HasBreak;

        public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) => node;
        public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node) => node;
        public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node) => node;
        public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node) => node;

        public override SyntaxNode? VisitDoStatement(DoStatementSyntax node) => EnterLoop(node, base.VisitDoStatement);
        public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node) => EnterLoop(node, base.VisitWhileStatement);
        public override SyntaxNode? VisitForStatement(ForStatementSyntax node) => EnterLoop(node, base.VisitForStatement);
        public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node) => EnterLoop(node, base.VisitForEachStatement);
        public override SyntaxNode? VisitForEachVariableStatement(ForEachVariableStatementSyntax node) => EnterLoop(node, base.VisitForEachVariableStatement);

        public override SyntaxNode? VisitSwitchStatement(SwitchStatementSyntax node)
        {
            _switchDepth++;
            var result = base.VisitSwitchStatement(node);
            _switchDepth--;
            return result;
        }

        public override SyntaxNode? VisitContinueStatement(ContinueStatementSyntax node)
        {
            if (_loopDepth == 0) HasContinue = true;
            return node;
        }

        public override SyntaxNode? VisitBreakStatement(BreakStatementSyntax node)
        {
            if (_loopDepth == 0 && _switchDepth == 0) HasBreak = true;
            return node;
        }

        private SyntaxNode? EnterLoop<T>(T node, Func<T, SyntaxNode?> visit) where T : SyntaxNode
        {
            _loopDepth++;
            var result = visit(node);
            _loopDepth--;
            return result;
        }
    }

    /// <summary>
    /// Maps do-loop-bound jumps for the do-once wrapped body:
    /// continue → inner break (runs the condition code next); break → set the
    /// escape flag and inner break (leaves the outer while).
    /// </summary>
    private sealed class DoBodyJumpMapper : CSharpSyntaxRewriter
    {
        private readonly string? _brkVar;
        private int _loopDepth;
        private int _switchDepth;

        public DoBodyJumpMapper(string? brkVar) => _brkVar = brkVar;

        public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) => node;
        public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node) => node;
        public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node) => node;
        public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node) => node;

        public override SyntaxNode? VisitDoStatement(DoStatementSyntax node) => EnterLoop(node, base.VisitDoStatement);
        public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node) => EnterLoop(node, base.VisitWhileStatement);
        public override SyntaxNode? VisitForStatement(ForStatementSyntax node) => EnterLoop(node, base.VisitForStatement);
        public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node) => EnterLoop(node, base.VisitForEachStatement);
        public override SyntaxNode? VisitForEachVariableStatement(ForEachVariableStatementSyntax node) => EnterLoop(node, base.VisitForEachVariableStatement);

        public override SyntaxNode? VisitSwitchStatement(SwitchStatementSyntax node)
        {
            _switchDepth++;
            var result = base.VisitSwitchStatement(node);
            _switchDepth--;
            return result;
        }

        public override SyntaxNode? VisitContinueStatement(ContinueStatementSyntax node)
        {
            if (_loopDepth > 0) return node;
            return SyntaxFactory.BreakStatement();
        }

        public override SyntaxNode? VisitBreakStatement(BreakStatementSyntax node)
        {
            if (_loopDepth > 0 || _switchDepth > 0 || _brkVar == null) return node;
            return SyntaxFactory.Block(
                SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName(_brkVar),
                    SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression))),
                SyntaxFactory.BreakStatement());
        }

        private SyntaxNode? EnterLoop<T>(T node, Func<T, SyntaxNode?> visit) where T : SyntaxNode
        {
            _loopDepth++;
            var result = visit(node);
            _loopDepth--;
            return result;
        }
    }

    public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
    {
        var preStmts = new List<StatementSyntax>();
        bool anyLift = false;

        // declarations / initializers: splice in evaluation order so hoist
        // allocation/order matches the original for; the initializer statements
        // themselves are rebuilt below (they run ONCE before the loop — dropping
        // them lost `int i = 0` / `i = start`, CS0103/CS0165).
        var declFragments = new List<(VariableDeclaratorSyntax Decl, ExpressionSyntax? LiftedInit,
            List<StatementSyntax> Hoisted, List<StatementSyntax> Readbacks)>();
        if (node.Declaration != null)
        {
            foreach (var d in node.Declaration.Variables)
            {
                if (d.Initializer == null)
                {
                    declFragments.Add((d, null, new List<StatementSyntax>(), new List<StatementSyntax>()));
                    continue;
                }
                var liftedInit = _lifter.SpliceExpression(d.Initializer.Value, out var hInit);
                var readbacks = _lifter.TakePendingReadbacks();
                if (hInit.Count > 0) anyLift = true;
                declFragments.Add((d, liftedInit, hInit, readbacks));
            }
        }

        var initFragments = new List<(ExpressionSyntax LiftedInit,
            List<StatementSyntax> Hoisted, List<StatementSyntax> Readbacks)>();
        foreach (var init in node.Initializers)
        {
            var liftedInit = _lifter.SpliceExpression(init, out var hInit);
            var readbacks = _lifter.TakePendingReadbacks();
            if (hInit.Count > 0) anyLift = true;
            initFragments.Add((liftedInit, hInit, readbacks));
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

        // Detect liftable incrementors with the non-mutating probe before
        // touching the body. On the unchanged path base.VisitForStatement does
        // the single regular walk; visiting the body here first would walk it
        // twice per nesting level (2^d on nested for loops).
        bool incrementsChanged = false;
        foreach (var inc in node.Incrementors)
        {
            if (_lifter.ContainsLiftable(inc))
            {
                anyLift = true;
                incrementsChanged = true;
            }
        }

        if (!anyLift && !incrementsChanged
            && ReferenceEquals(newCondition, node.Condition))
        {
            return base.VisitForStatement(node);
        }

        // Rebuild the initializer statements, in original order, before the
        // loop. Lifting the declaration out of the loop header is the standard
        // for→while lowering (same as the existing C17 anchor lifting); the
        // variable names are unchanged.
        if (node.Declaration != null)
        {
            var newDeclarators = new List<VariableDeclaratorSyntax>(declFragments.Count);
            foreach (var (d, liftedInit, hoisted, readbacks) in declFragments)
            {
                preStmts.AddRange(hoisted);
                preStmts.AddRange(readbacks);
                if (liftedInit == null)
                    newDeclarators.Add(d);   // uninitialized declarator keeps its declaration
                else
                    newDeclarators.Add(d.WithInitializer(
                        SyntaxFactory.EqualsValueClause(InjectSlotValues(liftedInit))));
            }
            preStmts.Add(SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(
                    node.Declaration.Type, SyntaxFactory.SeparatedList(newDeclarators))));
        }

        foreach (var (liftedInit, hoisted, readbacks) in initFragments)
        {
            preStmts.AddRange(hoisted);
            // A non-void lifted call is already materialized as `var __callN = …`
            // among the hoisted statements — the bare temp would be CS0201.
            if (!(liftedInit is IdentifierNameSyntax callTemp
                && callTemp.Identifier.ValueText.StartsWith("__call", StringComparison.Ordinal)))
            {
                preStmts.Add(SyntaxFactory.ExpressionStatement(InjectSlotValues(liftedInit)));
            }
            preStmts.AddRange(readbacks);
        }

        // body + increments (custom while(true) reconstruction path)
        var visitedBody = (StatementSyntax)base.Visit(node.Statement)!;
        var incStmts = new List<StatementSyntax>();
        var incExprs = new List<ExpressionSyntax>();
        foreach (var inc in node.Incrementors)
        {
            var liftedInc = _lifter.SpliceExpression(inc, out var hInc);
            if (hInc.Count > 0)
            {
                incStmts.AddRange(hInc);
                incStmts.AddRange(_lifter.TakePendingReadbacks());
            }
            incExprs.Add(InjectSlotValues(liftedInc));
        }

        // for (I; C; U) B  →  I; while (true) { C-splice; if (!C) break; B-wrapped; U-splice; }
        //
        // A direct `continue` inside B must execute U before re-evaluating C,
        // and a direct `break` inside B must skip U entirely. Both are handled
        // by wrapping B in a do/while(false) with a flag escape, the same
        // pattern used for VisitDoStatement:
        //   continue → inner break  (falls through to U)
        //   break     → set flag + inner break; if (flag) break;  (skips U)
        var forScanner = new DoBodyJumpScanner();
        forScanner.Visit(visitedBody);
        var loopBody = new List<StatementSyntax>();
        loopBody.AddRange(condStmts);
        if (newCondition != null)
            loopBody.Add(SyntaxFactory.IfStatement(
                Negate(newCondition), SyntaxFactory.BreakStatement()));

        if (forScanner.HasContinue || forScanner.HasBreak)
        {
            var brkVar = forScanner.HasBreak ? $"__cs2jForBrk{NextTemp()}" : null;
            if (brkVar != null)
                loopBody.Add(BoolFalseDeclaration(brkVar));
            var mappedBody = (StatementSyntax)new DoBodyJumpMapper(brkVar).Visit(visitedBody)!;
            loopBody.Add(SyntaxFactory.DoStatement(
                SyntaxFactory.Block(mappedBody),
                SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)));
            if (brkVar != null)
                loopBody.Add(SyntaxFactory.IfStatement(
                    SyntaxFactory.IdentifierName(brkVar), SyntaxFactory.BreakStatement()));
        }
        else
        {
            loopBody.Add(visitedBody);
        }
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

        // Walk the governing expression and every section exactly once (same
        // 2^d hazard as VisitIfStatement on nested switches).
        var exprChanged = !ReferenceEquals(lifted, node.Expression);
        var newExpression = exprChanged
            ? InjectSlotValues(lifted)
            : (ExpressionSyntax)base.Visit(node.Expression)!;

        var visitedSections = new List<SwitchSectionSyntax>();
        var sectionsUnchanged = !exprChanged;
        foreach (var section in node.Sections)
        {
            var visitedSection = (SwitchSectionSyntax)base.Visit(section)!;
            visitedSections.Add(visitedSection);
            if (!ReferenceEquals(visitedSection, section)) sectionsUnchanged = false;
        }

        var sections = SyntaxFactory.List(visitedSections);
        if (hoisted.Count == 0)
        {
            if (sectionsUnchanged && ReferenceEquals(newExpression, node.Expression))
                return node;
            return node.WithExpression(newExpression).WithSections(sections);
        }

        var newSwitch = node.WithExpression(newExpression).WithSections(sections);
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
        SyntaxFactory.Block(stmts)
            .WithAdditionalAnnotations(RefOutInStatementLifter.HoistedBlockAnnotation);

    /// <summary>
    /// C5 entry for lambdas retained inside a lifted invocation: the lifter
    /// rebuilds the enclosing statement from fragments and that replacement is
    /// never traversed again, so every lambda the splicer meets must be run
    /// through the normal rewrite HERE, while its node is still bound to the
    /// ORIGINAL semantic model — a held <c>ref</c> parameter becomes
    /// <c>ByRef&lt;T&gt;</c> and holder references inside the body become
    /// <c>.Value</c>. Non-held lambdas merely receive the ordinary
    /// holder-reference injection their normal traversal would have performed
    /// (e.g. a captured outer holder). Nested lambdas are converted by the
    /// ordinary descent inside Visit.
    /// </summary>
    internal SyntaxNode VisitLambdaFragment(AnonymousFunctionExpressionSyntax node)
    {
        // Isolate the lifter's per-statement scratch state: the owner visit
        // below re-enters the lifter from the lambda's own body statements.
        _lifter.EnterFragmentLambdaVisit();
        try
        {
            return Visit(node)!;
        }
        finally
        {
            _lifter.LeaveFragmentLambdaVisit();
        }
    }

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
        Dictionary<string, string?>? shadowFrame = null;
        try
        {
            if (holder)
                CurrentHolderNames.Add(node.Parameter.Identifier.Text);

            var holderParams = holder
                ? new[] { (node.Parameter.Identifier.Text, Symbol: sym!) }
                : Array.Empty<(string, IParameterSymbol)>();
            shadowFrame = PushHolderShadowFrame(holderParams, (SyntaxNode?)node.Body, false, out var shadows);
            var returnsVoid = DelegateReturnsVoid(node);

            // Expression-body conversion (and the forced block shape for shadow
            // copy-in/copy-out) runs on the ORIGINAL tree-owned node BEFORE any
            // parameter rebuild.
            var lifted = (SimpleLambdaExpressionSyntax)
                LiftExpressionLambdaBody(node, shadows.Count > 0);

            // Visit the body BEFORE rebuilding the parameter: WithParameter
            // detaches every body red-node from this syntax tree, so semantic
            // lookups from the body throw ArgumentException and the lifter
            // silently drops internal ref/out calls (naked `ref holder.Value.X`).
            var visited = (SimpleLambdaExpressionSyntax)base.VisitSimpleLambdaExpression(lifted);

            if (holder)
                visited = visited.WithParameter(RewriteParameter(node.Parameter, sym!));
            if (shadows.Count > 0)
                visited = (SimpleLambdaExpressionSyntax)
                    ApplyValueShadows(visited, shadows, returnsVoid,
                        DelegateReturnTypeDisplay(node));
            return visited;
        }
        finally
        {
            if (shadowFrame != null) _shadowNameScopes.Pop();
            _holderNameScopes.Pop();
        }
    }

    public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
    {
        if (!InCurrentTree(node))
            return node;   // fragment lambda: pre-injected, no further processing

        _holderNameScopes.Push(new HashSet<string>());
        Dictionary<string, string?>? shadowFrame = null;
        try
        {
            var newParams = new List<ParameterSyntax>();
            var holderParams = new List<(string Name, IParameterSymbol Symbol)>();
            foreach (var param in node.ParameterList.Parameters)
            {
                var sym = _semanticModel.GetDeclaredSymbol(param);
                if (sym != null && _annotator.HolderSymbols.Contains(sym))
                {
                    CurrentHolderNames.Add(param.Identifier.Text);
                    holderParams.Add((param.Identifier.Text, sym));
                    newParams.Add(RewriteParameter(param, sym));
                }
                else
                {
                    newParams.Add(param);
                }
            }
            bool anyHolder = holderParams.Count > 0;
            shadowFrame = PushHolderShadowFrame(holderParams, (SyntaxNode?)node.Body, false, out var shadows);
            var returnsVoid = DelegateReturnsVoid(node);

            // Lift the expression body on the ORIGINAL tree-owned node BEFORE
            // rebuilding the parameter list; shadow frames additionally force
            // the block shape needed for copy-in/copy-out.
            var lifted = (ParenthesizedLambdaExpressionSyntax)
                LiftExpressionLambdaBody(node, shadows.Count > 0);

            // Visit BEFORE replacing the parameter list: SeparatedList
            // regenerates separator trivia and a rebuilt list detaches every
            // body node from this syntax tree — body GetSymbolInfo calls then
            // throw ArgumentException ("Syntax node is not within syntax tree")
            // and the statement lifter silently skips internal ref/out calls.
            var visited = (ParenthesizedLambdaExpressionSyntax)
                base.VisitParenthesizedLambdaExpression(lifted);

            if (anyHolder)
                visited = visited.WithParameterList(
                    SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(newParams)));
            if (shadows.Count > 0)
                visited = (ParenthesizedLambdaExpressionSyntax)
                    ApplyValueShadows(visited, shadows, returnsVoid,
                        DelegateReturnTypeDisplay(node));
            return visited;
        }
        finally
        {
            if (shadowFrame != null) _shadowNameScopes.Pop();
            _holderNameScopes.Pop();
        }
    }

    /// <summary>
    /// Builds the shadow-rename frame for one anonymous-function or member
    /// visit: a value-typed (non-ref-like) held parameter whose OWN body has a
    /// CS1612-shaped mutation (member/element assignment, ++/--, a ref/out
    /// member-chain argument, or a mutating member call through the holder)
    /// gets a private mutable local; other value holders and reference-type /
    /// ref-like holders push a null sentinel so that the same name in this
    /// frame is not redirected to an OUTER shadow. A read-only body needs no
    /// frame at all.
    /// </summary>
    private Dictionary<string, string?>? PushHolderShadowFrame(
        IReadOnlyList<(string Name, IParameterSymbol Symbol)> holderParams,
        SyntaxNode? scanRoot,
        bool localFunctionsCapture,
        out List<(string Holder, string Shadow, string Type)> shadows)
    {
        shadows = new List<(string, string, string)>();
        if (holderParams.Count == 0 || scanRoot == null)
            return null;

        var frame = new Dictionary<string, string?>();
        foreach (var (name, symbol) in holderParams)
        {
            if (symbol.Type.IsValueType && !symbol.Type.IsRefLikeType
                && BodyMutatesHolder(scanRoot, name, localFunctionsCapture))
            {
                var shadow = "__shadow" + NextTemp();
                frame[name] = shadow;
                shadows.Add((name, shadow, TypeDisplay(symbol.Type)));
            }
            else
            {
                frame[name] = null;
            }
        }
        // Push even when every entry is a null sentinel (e.g. an inner lambda
        // has a reference-type holder with the same name as an outer value
        // shadow): the sentinel must still hide the outer mapping.
        _shadowNameScopes.Push(frame);
        return frame;
    }

    /// <summary>
    /// True when <paramref name="body"/> mutates (or passes onward by
    /// ref/out) a holder value through a member/element chain rooted at
    /// <paramref name="holderName"/> — the shapes that CS1612 rejects on a
    /// <c>ByRef&lt;T&gt;.Value</c> property return and that therefore require a
    /// shadow local. Bare <c>ref holder</c> arguments are already legal via the
    /// lifter's direct pass / Cs2jInterlocked mapping and do not force a frame.
    /// </summary>
    private static bool BodyMutatesHolder(
        SyntaxNode body, string holderName, bool descendIntoLocalFunctions)
    {
        bool Barrier(SyntaxNode n) =>
            n is AnonymousFunctionExpressionSyntax
            || (!descendIntoLocalFunctions && n is LocalFunctionStatementSyntax);

        foreach (var id in body.DescendantNodesAndSelf(n => !Barrier(n))
                     .OfType<IdentifierNameSyntax>())
        {
            if (id.Identifier.Text != holderName || IsInDeclarationContext(id))
                continue;

            // Climb member/element (and null-conditional) chains rooted at the
            // holder identifier; a mutation at any chain level needs the shadow.
            SyntaxNode chain = id;
            while (true)
            {
                var parent = chain.Parent;
                if (parent is AssignmentExpressionSyntax assign
                    && ReferenceEquals(assign.Left, chain))
                    return true;
                if (parent is PrefixUnaryExpressionSyntax pre
                    && (pre.IsKind(SyntaxKind.PreIncrementExpression)
                        || pre.IsKind(SyntaxKind.PreDecrementExpression)))
                    return true;
                if (parent is PostfixUnaryExpressionSyntax post
                    && (post.IsKind(SyntaxKind.PostIncrementExpression)
                        || post.IsKind(SyntaxKind.PostDecrementExpression)))
                    return true;
                if (parent is ArgumentSyntax arg
                    && (arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)
                        || arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
                    && ReferenceEquals(arg.Expression, chain)
                    && chain is not IdentifierNameSyntax)
                    return true;
                if (parent is InvocationExpressionSyntax call
                    && ReferenceEquals(call.Expression, chain)
                    && chain is not IdentifierNameSyntax)
                    return true;   // mutating member call (CS1612-conservative)

                if (parent is MemberAccessExpressionSyntax member
                    && ReferenceEquals(member.Expression, chain))
                {
                    chain = member;
                    continue;
                }
                if (parent is ElementAccessExpressionSyntax element
                    && ReferenceEquals(element.Expression, chain))
                {
                    chain = element;
                    continue;
                }
                if (parent is MemberBindingExpressionSyntax binding
                    && binding.Parent is ConditionalAccessExpressionSyntax conditional
                    && ReferenceEquals(conditional.WhenNotNull, binding))
                {
                    chain = conditional;
                    continue;
                }
                break;
            }
        }
        return false;
    }

    private bool DelegateReturnsVoid(AnonymousFunctionExpressionSyntax node)
        => DelegateInvoke(node)?.ReturnsVoid ?? false;

    /// <summary>Display name of the delegate return type (for the return temp).</summary>
    private string? DelegateReturnTypeDisplay(AnonymousFunctionExpressionSyntax node)
    {
        var invoke = DelegateInvoke(node);
        return invoke == null || invoke.ReturnsVoid ? null : TypeDisplay(invoke.ReturnType);
    }

    private IMethodSymbol? DelegateInvoke(AnonymousFunctionExpressionSyntax node)
        => (_semanticModel.GetTypeInfo(node).ConvertedType as INamedTypeSymbol)
            ?.DelegateInvokeMethod;

    /// <summary>
    /// Wraps a shadow-frame lambda body as
    /// <c>{ T __s = holder.Value; …body with holder names bound to __s…;
    /// holder.Value = __s; // before every return / at void fall-through }</c>.
    /// Exceptions skip copy-out, matching AccessorByRef/copy-in/copy-out
    /// semantics elsewhere in the pass.
    /// </summary>
    private AnonymousFunctionExpressionSyntax ApplyValueShadows(
        AnonymousFunctionExpressionSyntax node,
        IReadOnlyList<(string Holder, string Shadow, string Type)> shadows,
        bool returnsVoid,
        string? returnTypeDisplay)
    {
        if (node.Body is not BlockSyntax block)
            return node;   // defensive: forceBlock should have converted an expression body

        return node.WithBody(
            BuildShadowBlock(block, shadows, returnsVoid, returnTypeDisplay));
    }

    /// <summary>
    /// Builds a shadow frame block (shared by held lambdas and by method /
    /// constructor / local-function bodies whose value-typed ref parameters
    /// became <c>ByRef&lt;T&gt;</c> holders): copy-in declarations, the body
    /// with holder names bound to the shadows, and copy-out before every own
    /// return plus at void fall-through.
    /// </summary>
    private BlockSyntax BuildShadowBlock(
        BlockSyntax block,
        IReadOnlyList<(string Holder, string Shadow, string Type)> shadows,
        bool returnsVoid,
        string? returnTypeDisplay)
    {
        var statements = new List<StatementSyntax>();
        foreach (var shadow in shadows)
            statements.Add(BuildShadowCopyIn(shadow.Holder, shadow.Shadow, shadow.Type));

        var writebacks = shadows
            .Select(s => BuildShadowWriteback(s.Holder, s.Shadow))
            .ToList<StatementSyntax>();
        var rewritten = (BlockSyntax)new ShadowReturnWriter(
            writebacks, returnTypeDisplay, NextTemp).Visit(block)!;
        statements.AddRange(rewritten.Statements);
        if (returnsVoid)
            statements.AddRange(writebacks);

        return SyntaxFactory.Block(statements);
    }

    /// <summary>
    /// Wraps an expression-bodied member's visited expression as a block the
    /// shadow frame can rewrite: <c>=&gt; expr</c> → <c>{ return expr; }</c>
    /// (or <c>{ expr; }</c> for void members).
    /// </summary>
    private static BlockSyntax BlockFromShadowExpression(ExpressionSyntax expression, bool returnsVoid) =>
        returnsVoid
            ? SyntaxFactory.Block(SyntaxFactory.ExpressionStatement(expression))
            : SyntaxFactory.Block(SyntaxFactory.ReturnStatement(expression));

    private static MemberAccessExpressionSyntax HolderValueAccess(string holder) =>
        SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.IdentifierName(SyntaxFactory.Identifier(holder))
                .WithAdditionalAnnotations(RefOutInStatementLifter.DirectPassAnnotation),
            SyntaxFactory.IdentifierName("Value"));

    private static LocalDeclarationStatementSyntax BuildShadowCopyIn(
        string holder, string shadow, string type) =>
        SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.ParseTypeName(type),
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(shadow))
                        .WithInitializer(SyntaxFactory.EqualsValueClause(
                            HolderValueAccess(holder))))));

    private static ExpressionStatementSyntax BuildShadowWriteback(string holder, string shadow) =>
        SyntaxFactory.ExpressionStatement(
            SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                HolderValueAccess(holder),
                SyntaxFactory.IdentifierName(shadow)));

    /// <summary>
    /// Inserts the copy-out statements immediately before every return of the
    /// OWN lambda only; nested lambdas/local functions are barriers. A
    /// value-returning return first materializes its expression into a temp,
    /// so the shadow copy-out observes the expression's own mutations
    /// (<c>return --__s.X</c> must persist the decrement).
    /// </summary>
    private sealed class ShadowReturnWriter : CSharpSyntaxRewriter
    {
        private readonly List<StatementSyntax> _writebacks;
        private readonly string? _returnTypeDisplay;
        private readonly Func<int> _nextTemp;

        public ShadowReturnWriter(
            List<StatementSyntax> writebacks,
            string? returnTypeDisplay,
            Func<int> nextTemp)
        {
            _writebacks = writebacks;
            _returnTypeDisplay = returnTypeDisplay;
            _nextTemp = nextTemp;
        }

        public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) => node;
        public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node) => node;
        public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node) => node;
        public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node) => node;

        public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
        {
            var statements = new List<StatementSyntax>();
            if (node.Expression != null && _returnTypeDisplay != null)
            {
                var temp = "__ret" + _nextTemp();
                statements.Add(SyntaxFactory.LocalDeclarationStatement(
                    SyntaxFactory.VariableDeclaration(
                        SyntaxFactory.ParseTypeName(_returnTypeDisplay),
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(temp))
                                .WithInitializer(SyntaxFactory.EqualsValueClause(node.Expression))))));
                statements.AddRange(_writebacks);
                statements.Add(SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName(temp)));
            }
            else
            {
                statements.AddRange(_writebacks);
                statements.Add(node);
            }
            return SyntaxFactory.Block(statements);
        }
    }

    /// <summary>
    /// Expression-bodied lambdas (<c>() =&gt; Foo(ref x)</c>,
    /// <c>(out int r) =&gt; TryBar(out r)</c>) have no statement for the
    /// statement visitors to splice, so a liftable ref/out call in the body
    /// expression survived verbatim and produced CS1615 at the rewritten
    /// target. Convert such a body to a block body with the normal slot
    /// hoisting (fragment isolation keeps the hoisted statements inside the
    /// lambda), ending in <c>return</c> for non-void delegates.
    /// </summary>
    // Callers MUST pass the ORIGINAL, tree-owned lambda (before any
    // WithParameter/WithParameterList rebuild): the converted delegate type is
    // queried below and the lifter binds the body through this semantic model,
    // while a rebuilt parameter list produces a detached node whose span no
    // longer maps into the tree — GetTypeInfo throws "Syntax node is not
    // within syntax tree" and body invocations silently fail to resolve.
    private AnonymousFunctionExpressionSyntax LiftExpressionLambdaBody(
        AnonymousFunctionExpressionSyntax node, bool forceBlock = false)
    {
        if (node.Body is not ExpressionSyntax body)
        {
            return node;
        }

        var returnsVoid = (_semanticModel.GetTypeInfo(node).ConvertedType as INamedTypeSymbol)
            ?.DelegateInvokeMethod?.ReturnsVoid ?? false;

        _lifter.EnterFragmentLambdaVisit();
        try
        {
            // The liftability scan must run INSIDE fragment scope: reached via
            // the plain base re-walk (outer statement had no hoist) the owning
            // lambda would otherwise be treated as a barrier.
            if (!forceBlock && !_lifter.ContainsLiftable(body))
            {
                return node;
            }

            var remainder = _lifter.SpliceExpression(body, out var hoisted);
            var pending = _lifter.TakePendingReadbacks();
            var statements = new List<StatementSyntax>(hoisted);
            var finalExpression = InjectSlotValues(remainder);
            if (returnsVoid)
            {
                statements.Add(SyntaxFactory.ExpressionStatement(finalExpression));
                statements.AddRange(pending);
            }
            else
            {
                statements.AddRange(pending);
                statements.Add(SyntaxFactory.ReturnStatement(finalExpression));
            }

            var block = SyntaxFactory.Block(statements);
            return node.WithBody(block);
        }
        finally
        {
            _lifter.LeaveFragmentLambdaVisit();
        }
    }

    public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node)
    {
        if (!InCurrentTree(node))
            return node;   // fragment lambda: pre-injected, no further processing

        _holderNameScopes.Push(new HashSet<string>());
        Dictionary<string, string?>? shadowFrame = null;
        try
        {
            var newParams = new List<ParameterSyntax>();
            var holderParams = new List<(string Name, IParameterSymbol Symbol)>();
            if (node.ParameterList != null)
            {
                foreach (var param in node.ParameterList.Parameters)
                {
                    var sym = _semanticModel.GetDeclaredSymbol(param);
                    if (sym != null && _annotator.HolderSymbols.Contains(sym))
                    {
                        CurrentHolderNames.Add(param.Identifier.Text);
                        holderParams.Add((param.Identifier.Text, sym));
                        newParams.Add(RewriteParameter(param, sym));
                    }
                    else
                    {
                        newParams.Add(param);
                    }
                }
            }
            bool anyHolder = holderParams.Count > 0;
            shadowFrame = PushHolderShadowFrame(holderParams, (SyntaxNode?)node.Body, false, out var shadows);
            var returnsVoid = DelegateReturnsVoid(node);

            // Visit the original node first (body bound to this syntax tree),
            // then swap in the rewritten parameter list — see
            // VisitParenthesizedLambdaExpression for the detachment rationale.
            var visited = (AnonymousMethodExpressionSyntax)
                base.VisitAnonymousMethodExpression(node);

            if (anyHolder)
                visited = visited.WithParameterList(
                    node.ParameterList!.WithParameters(SyntaxFactory.SeparatedList(newParams)));
            if (shadows.Count > 0)
                visited = (AnonymousMethodExpressionSyntax)
                    ApplyValueShadows(visited, shadows, returnsVoid,
                        DelegateReturnTypeDisplay(node));
            return visited;
        }
        finally
        {
            if (shadowFrame != null) _shadowNameScopes.Pop();
            _holderNameScopes.Pop();
        }
    }

    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        if (!InCurrentTree(node))
            return node;   // fragment lambda: pre-injected, no further processing

        var symbol = _semanticModel.GetDeclaredSymbol(node);
        if (symbol == null)
            return base.VisitLocalFunctionStatement(node);

        _holderNameScopes.Push(new HashSet<string>());
        Dictionary<string, string?>? shadowFrame = null;
        try
        {
            var newParams = new List<ParameterSyntax>();
            var holderParams = new List<(string Name, IParameterSymbol Symbol)>();
            foreach (var param in node.ParameterList.Parameters)
            {
                var paramSymbol = symbol.Parameters.FirstOrDefault(p => p.Name == param.Identifier.Text);
                if (paramSymbol != null && _annotator.HolderSymbols.Contains(paramSymbol))
                {
                    CurrentHolderNames.Add(param.Identifier.Text);
                    holderParams.Add((param.Identifier.Text, paramSymbol));
                    newParams.Add(RewriteParameter(param, paramSymbol));
                }
                else
                {
                    newParams.Add(param);
                }
            }
            bool anyHolder = holderParams.Count > 0;
            shadowFrame = PushHolderShadowFrame(holderParams,
                (SyntaxNode?)node.Body ?? node.ExpressionBody, true, out var shadows);
            bool returnsVoid = symbol.ReturnsVoid;
            string? returnTypeDisplay = symbol.ReturnsVoid
                ? null
                : (node.ReturnType is RefTypeSyntax
                    ? "ByRef<" + TypeDisplay(symbol.ReturnType) + ">"
                    : TypeDisplay(symbol.ReturnType));

            // Visit the body BEFORE rebuilding the parameter list — a rebuilt
            // SeparatedList detaches every body node from the syntax tree and
            // body GetSymbolInfo calls throw ArgumentException, which makes the
            // statement lifter silently drop internal ref/out calls (same root
            // cause as for held lambdas).
            var visited = (LocalFunctionStatementSyntax)
                base.VisitLocalFunctionStatement(node);

            if (anyHolder)
            {
                visited = visited.WithParameterList(
                    SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(newParams)));
            }
            if (shadows.Count > 0)
            {
                BlockSyntax block;
                if (visited.Body != null)
                {
                    block = visited.Body;
                }
                else if (visited.ExpressionBody != null)
                {
                    block = BlockFromShadowExpression(visited.ExpressionBody.Expression, returnsVoid);
                    visited = visited
                        .WithExpressionBody(null)
                        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.None));
                }
                else
                {
                    return visited;
                }

                visited = visited.WithBody(
                    BuildShadowBlock(block, shadows, returnsVoid, returnTypeDisplay));
            }
            return visited;
        }
        finally
        {
            if (shadowFrame != null) _shadowNameScopes.Pop();
            _holderNameScopes.Pop();
        }
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
            if (_owner.TryGetShadowName(node.Identifier.Text, out var shadowName))
            {
                return SyntaxFactory.IdentifierName(shadowName)
                    .WithAdditionalAnnotations(RefOutInStatementLifter.ValueInjectedAnnotation);
            }
            return SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                node.WithAdditionalAnnotations(RefOutInStatementLifter.ValueInjectedAnnotation),
                SyntaxFactory.IdentifierName("Value"));
        }
    }
}
