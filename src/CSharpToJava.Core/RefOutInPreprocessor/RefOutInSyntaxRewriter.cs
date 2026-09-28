using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.RefOutInPreprocessor;

/// <summary>
/// Pass 2+3: CSharpSyntaxRewriter that rewrites declarations (ref/out params,
/// ref returns, ref locals, ref fields, delegates) and lifts call sites
/// (ref/out arguments → ByRef proxies).
/// </summary>
internal sealed class RefOutInSyntaxRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _semanticModel;
    private readonly RefOutInAliasAnnotator _annotator;
    private readonly RefOutInRewriterStatistics _stats;
    private readonly List<RefOutInRewriterDiagnostic> _diagnostics;
    private int _tempCounter;

    // Names of parameters/locals that are holder slots in the current method
    private readonly HashSet<string> _currentMethodHolderNames = new();

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
    }

    // --- Pass 2: Declaration rewriting ---

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var symbol = _semanticModel.GetDeclaredSymbol(node);
        if (symbol == null)
            return base.VisitMethodDeclaration(node);

        _currentMethodHolderNames.Clear();
        bool isHolder = false;

        // Pre-scan for ref local names in this method body (do this BEFORE
        // any rewriting so the semantic model can still resolve symbols)
        if (node.Body != null)
        {
            var refLocals = node.Body.DescendantNodes()
                .OfType<VariableDeclaratorSyntax>()
                .Where(v => v.Parent is VariableDeclarationSyntax vd && vd.Type is RefTypeSyntax);
            foreach (var rl in refLocals)
            {
                _currentMethodHolderNames.Add(rl.Identifier.Text);
            }
        }

        // Determine which parameters are holders and populate _currentMethodHolderNames
        // BEFORE visiting the body, so VisitIdentifierName can use them.
        var newParamList = new List<ParameterSyntax>();
        foreach (var param in node.ParameterList.Parameters)
        {
            var paramSymbol = symbol.Parameters.FirstOrDefault(p => p.Name == param.Identifier.Text);
            if (paramSymbol != null && _annotator.HolderSymbols.Contains(paramSymbol))
            {
                isHolder = true;
                _currentMethodHolderNames.Add(param.Identifier.Text);
                newParamList.Add(RewriteParameter(param, paramSymbol));
            }
            else
            {
                var needsStrip = param.Modifiers.Any(m =>
                    m.IsKind(SyntaxKind.InKeyword)
                    || m.IsKind(SyntaxKind.ScopedKeyword));
                if (needsStrip)
                {
                    isHolder = true;
                    newParamList.Add(StripModifiers(param));
                }
                else
                {
                    newParamList.Add(param);
                }
            }
        }

        // Ref return → ByRef return
        TypeSyntax newReturnType = node.ReturnType;
        if ((symbol.RefKind == RefKind.Ref || symbol.RefKind == RefKind.RefReadOnly)
            && node.ReturnType is RefTypeSyntax refType)
        {
            newReturnType = refType.Type;
            isHolder = true;
            UsedHolder = true;
        }

        // Visit the body on the ORIGINAL node first (so SemanticModel works),
        // then swap in the rewritten parameter list and return type.
        var newParams = SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(newParamList));
        var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
        // Now apply the new parameter list and return type to the visited result
        visited = visited.WithParameterList(newParams).WithReturnType(newReturnType);

        if (isHolder) UsedHolder = true;
        return visited;
    }

    private ParameterSyntax RewriteParameter(ParameterSyntax param, IParameterSymbol symbol)
    {
        var type = symbol.Type;
        TypeSyntax newType;

        if (type.IsRefLikeType)
        {
            var holderName = RefOutInMangling.HolderName(type);
            newType = SyntaxFactory.ParseTypeName(holderName);
        }
        else
        {
            // Wrap the underlying type in ByRef<T>
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

        _currentMethodHolderNames.Clear();

        var newParamList = new List<ParameterSyntax>();
        foreach (var param in node.ParameterList.Parameters)
        {
            var paramSymbol = symbol.Parameters.FirstOrDefault(p => p.Name == param.Identifier.Text);
            if (paramSymbol != null && _annotator.HolderSymbols.Contains(paramSymbol))
            {
                _currentMethodHolderNames.Add(param.Identifier.Text);
                newParamList.Add(RewriteParameter(param, paramSymbol));
            }
            else
            {
                newParamList.Add(param);
            }
        }

        var newParams = SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(newParamList));
        var visited = (ConstructorDeclarationSyntax)base.VisitConstructorDeclaration(node)!;
        visited = visited.WithParameterList(newParams);
        return visited;
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

        // Ref return on delegate
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

    // --- Body rewriting: s → s.Value for holder symbols ---

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        var name = node.Identifier.Text;

        if (IsInDeclarationContext(node))
            return base.VisitIdentifierName(node);

        if (!_currentMethodHolderNames.Contains(name))
            return base.VisitIdentifierName(node);

        if (IsHolderSlotDirectPass(node))
            return base.VisitIdentifierName(node);

        if (IsExternalRefOutArgument(node))
            return base.VisitIdentifierName(node);

        _stats.HoldersInjected++;
        return SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            node,
            SyntaxFactory.IdentifierName("Value"));
    }

    private bool IsInDeclarationContext(IdentifierNameSyntax node)
    {
        return node.Parent is ParameterSyntax
            || node.Parent is VariableDeclaratorSyntax
            || node.Parent is SimpleBaseTypeSyntax;
    }

    private bool IsHolderSlotDirectPass(IdentifierNameSyntax node)
    {
        if (node.Parent is ArgumentSyntax arg)
        {
            var invocation = arg.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault();
            if (invocation != null)
            {
                IMethodSymbol? method;
                try
                {
                    method = _semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                }
                catch (ArgumentException)
                {
                    return false;
                }
                if (method != null)
                {
                    var isExternal = !method.Locations.Any(l => l.IsInSource);
                    if (!isExternal && (arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)
                        || arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)))
                        return true;
                }
            }
        }
        return false;
    }

    private bool IsExternalRefOutArgument(IdentifierNameSyntax node)
    {
        if (node.Parent is ArgumentSyntax arg)
        {
            var invocation = arg.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault();
            if (invocation != null)
            {
                IMethodSymbol? method;
                try
                {
                    method = _semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                }
                catch (ArgumentException)
                {
                    return false;
                }
                if (method != null)
                {
                    var isExternal = !method.Locations.Any(l => l.IsInSource);
                    if (isExternal && (arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)
                        || arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)))
                        return true;
                }
            }
        }
        return false;
    }

    // --- Pass 3: Call-site lifting ---

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        var (lifted, newStatement) = TryLiftExpressionStatement(node);
        if (lifted)
        {
            _stats.CallSitesLifted++;
            return newStatement!;
        }
        return base.VisitExpressionStatement(node);
    }

    private (bool, StatementSyntax?) TryLiftExpressionStatement(ExpressionStatementSyntax node)
    {
        var expr = node.Expression;

        if (expr is not InvocationExpressionSyntax invocation)
            return (false, null);

        // The node may have been modified by parent rewriting (e.g. method signature
        // changes), so it may not be in the original syntax tree anymore.
        // If it's not in the tree, GetSymbolInfo will throw. Skip in that case.
        IMethodSymbol? method;
        try
        {
            method = _semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        }
        catch (ArgumentException)
        {
            // Node not in original syntax tree — skip semantic lookup
            return (false, null);
        }
        if (method == null)
            return (false, null);

        var isExternal = !method.Locations.Any(l => l.IsInSource);

        var leadingStatements = new List<StatementSyntax>();
        var trailingStatements = new List<StatementSyntax>();
        var newArgs = new List<ArgumentSyntax>();
        bool anyLifted = false;

        for (int i = 0; i < invocation.ArgumentList.Arguments.Count; i++)
        {
            var arg = invocation.ArgumentList.Arguments[i];
            var hasRef = arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword);
            var hasOut = arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword);

            if (!hasRef && !hasOut)
            {
                newArgs.Add(arg);
                continue;
            }

            if (isExternal)
            {
                newArgs.Add(arg);
                _stats.ExternalRefArgumentsKept++;
                continue;
            }

            anyLifted = true;
            var argExpr = arg.Expression;

            // Case 1: Holder slot direct pass
            if (argExpr is IdentifierNameSyntax idn && _currentMethodHolderNames.Contains(idn.Identifier.Text))
            {
                newArgs.Add(SyntaxFactory.Argument(argExpr));
                continue;
            }

            // Case 2: Array element
            if (argExpr is ElementAccessExpressionSyntax elemAccess)
            {
                var (arrExpr, indices) = DecomposeElementAccess(elemAccess);
                if (arrExpr != null)
                {
                    var paramType = method.Parameters[i].Type;
                    var typeName = paramType.ToDisplayString();
                    var indexArgs = string.Join(", ", indices);
                    var proxyExpr = SyntaxFactory.ParseExpression(
                        $"ByRef<{typeName}>.Create({arrExpr}, {indexArgs})");
                    var tempName = $"__ref{++_tempCounter}";
                    leadingStatements.Add(SyntaxFactory.LocalDeclarationStatement(
                        SyntaxFactory.VariableDeclaration(
                            SyntaxFactory.IdentifierName("var"),
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.VariableDeclarator(
                                    SyntaxFactory.Identifier(tempName))
                                    .WithInitializer(SyntaxFactory.EqualsValueClause(proxyExpr))))));
                    if (hasOut)
                    {
                        trailingStatements.Insert(0, SyntaxFactory.ExpressionStatement(
                            SyntaxFactory.AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression,
                                argExpr,
                                SyntaxFactory.MemberAccessExpression(
                                    SyntaxKind.SimpleMemberAccessExpression,
                                    SyntaxFactory.IdentifierName(tempName),
                                    SyntaxFactory.IdentifierName("Value")))));
                    }
                    newArgs.Add(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(tempName)));
                    _stats.ArrayElementProxies++;
                    continue;
                }
            }

            // Case 3: Identifier (local or field)
            if (argExpr is IdentifierNameSyntax id)
            {
                var argSymbol = _semanticModel.GetSymbolInfo(id).Symbol;
                var argType = argSymbol switch
                {
                    ILocalSymbol local => local.Type,
                    IFieldSymbol field => field.Type,
                    IParameterSymbol p => p.Type,
                    _ => method.Parameters[i].Type,
                };

                var typeName = argType.ToDisplayString();
                var varName = id.Identifier.Text;

                if (hasRef)
                {
                    var tempName = $"__ref{++_tempCounter}";
                    var proxyExpr = SyntaxFactory.ParseExpression(
                        $"ByRef<{typeName}>.Create(() => {varName}, v => {varName} = v)");
                    leadingStatements.Add(SyntaxFactory.LocalDeclarationStatement(
                        SyntaxFactory.VariableDeclaration(
                            SyntaxFactory.IdentifierName("var"),
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.VariableDeclarator(
                                    SyntaxFactory.Identifier(tempName))
                                    .WithInitializer(SyntaxFactory.EqualsValueClause(proxyExpr))))));
                    newArgs.Add(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(tempName)));
                    _stats.AccessorProxies++;
                }
                else // out
                {
                    var tempName = $"__out{++_tempCounter}";
                    var proxyExpr = SyntaxFactory.ParseExpression(
                        $"ByRef<{typeName}>.Create(() => {varName}, v => {varName} = v)");
                    leadingStatements.Add(SyntaxFactory.LocalDeclarationStatement(
                        SyntaxFactory.VariableDeclaration(
                            SyntaxFactory.IdentifierName("var"),
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.VariableDeclarator(
                                    SyntaxFactory.Identifier(tempName))
                                    .WithInitializer(SyntaxFactory.EqualsValueClause(proxyExpr))))));
                    newArgs.Add(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(tempName)));
                    trailingStatements.Insert(0, SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            SyntaxFactory.IdentifierName(varName),
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName(tempName),
                                SyntaxFactory.IdentifierName("Value")))));
                    _stats.AccessorProxies++;
                }
                continue;
            }

            newArgs.Add(arg);
        }

        if (!anyLifted)
            return (false, null);

        var newInvocation = invocation.WithArgumentList(
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(newArgs)));
        var newExpr = node.WithExpression(newInvocation);

        var allStatements = leadingStatements
            .Append(newExpr)
            .Concat(trailingStatements)
            .ToArray();

        if (allStatements.Length == 1)
            return (true, allStatements[0]);
        return (true, SyntaxFactory.Block(allStatements));
    }

    private (string?, List<string>) DecomposeElementAccess(ElementAccessExpressionSyntax elem)
    {
        var arrExpr = elem.Expression.ToString();
        var indices = elem.ArgumentList.Arguments.Select(a => a.Expression.ToString()).ToList();
        return (arrExpr, indices);
    }

    // Handle ref local declarations: `ref T r = ref e;` → `ByRef<T> r = <proxy(e)>;`
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
                    if (expr is IdentifierNameSyntax id && _currentMethodHolderNames.Contains(id.Identifier.Text))
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

        return base.VisitLocalDeclarationStatement(node);
    }

    // Handle ref field declarations
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
}
