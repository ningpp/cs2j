using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LocalFunctionLift;

/// <summary>
/// Pure analysis pass: locates host types, determines static, collects captures,
/// runs the fixpoint propagation, and evaluates all skip gates.
/// Produces a list of LiftPlan objects for the rewriter.
/// </summary>
internal sealed class LocalFunctionLiftAnalyzer
{
    private readonly SemanticModel _model;
    private readonly LocalFunctionLiftOptions _options;
    private readonly LocalFunctionLiftStatistics _stats;
    private readonly List<LocalFunctionLiftDiagnostic> _diagnostics;
    private readonly Dictionary<string, HashSet<string>> _crossFileNameTable;

    public LocalFunctionLiftAnalyzer(
        SemanticModel model,
        LocalFunctionLiftOptions options,
        LocalFunctionLiftStatistics stats,
        List<LocalFunctionLiftDiagnostic> diagnostics,
        Dictionary<string, HashSet<string>> crossFileNameTable)
    {
        _model = model;
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
        _crossFileNameTable = crossFileNameTable;
    }

    /// <summary>
    /// Analyzes all local functions in the compilation unit and returns LiftPlans.
    /// </summary>
    public List<LiftPlan> Analyze(CompilationUnitSyntax root)
    {
        var localFns = root.DescendantNodes().OfType<LocalFunctionStatementSyntax>().ToList();
        var plans = new List<LiftPlan>();

        foreach (var localFn in localFns)
        {
            var plan = AnalyzeOne(root, localFn);
            plans.Add(plan);
        }

        PropagateCaptures(plans);

        foreach (var plan in plans)
        {
            if (plan.Skipped)
                continue;
            CheckPostPropagationGates(plan);
        }

        return plans;
    }

    private LiftPlan AnalyzeOne(CompilationUnitSyntax root, LocalFunctionStatementSyntax localFn)
    {
        var symbol = _model.GetDeclaredSymbol(localFn) as IMethodSymbol;
        var plan = new LiftPlan
        {
            Node = localFn,
            Symbol = symbol!,
            NewName = "",
            IsStatic = false,
            Skipped = false,
            SkipReason = LocalFunctionSkipReason.None,
        };

        // Gate #2: No body
        if (localFn.Body == null && localFn.ExpressionBody == null)
        {
            Skip(plan, LocalFunctionSkipReason.NoBody, "local function has no body");
            return plan;
        }

        // Gate #1: Iterator (yield return / yield break)
        var bodyNode = (SyntaxNode?)localFn.Body ?? localFn.ExpressionBody!;
        if (bodyNode.DescendantNodes().Any(n =>
            n.IsKind(SyntaxKind.YieldReturnStatement) || n.IsKind(SyntaxKind.YieldBreakStatement)))
        {
            Skip(plan, LocalFunctionSkipReason.Iterator, "local function is an iterator (contains yield)");
            return plan;
        }

        // Gate #3: No host type
        var hostType = FindHostType(localFn);
        if (hostType == null)
        {
            Skip(plan, LocalFunctionSkipReason.NoHostType, "no enclosing type declaration found");
            return plan;
        }
        if (IsInsideLambdaOrInitializer(localFn))
        {
            Skip(plan, LocalFunctionSkipReason.NoHostType, "local function is inside a lambda or field/property initializer");
            return plan;
        }
        plan.HostType = hostType;

        // Gate #4: Interface host
        if (hostType is InterfaceDeclarationSyntax && !_options.AllowInterfaceHost)
        {
            Skip(plan, LocalFunctionSkipReason.InterfaceHost, "host type is an interface");
            return plan;
        }

        // Gate #6: checked/unchecked context
        if (IsInsideCheckedContext(localFn))
        {
            Skip(plan, LocalFunctionSkipReason.CheckedContext, "local function is inside a checked/unchecked block");
            return plan;
        }

        plan.IsStatic = DetermineStatic(localFn);

        // Gate #5: Method group / delegate conversion reference
        if (HasMethodGroupReference(root, symbol!))
        {
            Skip(plan, LocalFunctionSkipReason.MethodGroupConversion,
                "local function is referenced as a method group or delegate conversion");
            return plan;
        }

        // Collect captures
        plan.Captures = CollectCaptures(localFn, symbol!, plan.IsStatic);

        // Gate #7: Unsupported capture type
        foreach (var cap in plan.Captures)
        {
            if (IsUnsupportedCaptureType(cap))
            {
                Skip(plan, LocalFunctionSkipReason.UnsupportedCaptureType,
                    $"capture variable '{cap.OriginalName}' has unsupported type: {cap.TypeDisplay}");
                return plan;
            }
        }

        // Assign name
        plan.NewName = AssignName(localFn, hostType, symbol!);

        // Gate #10: CapturesAtEnd with params/optional
        if (_options.CapturesAtEnd)
        {
            if (HasParamsOrOptional(localFn))
            {
                Skip(plan, LocalFunctionSkipReason.ParamsOrOptionalConflict,
                    "CapturesAtEnd=true but original parameters contain params or default values");
                return plan;
            }
        }

        // Gate #11: Named argument at call sites
        if (HasNamedArgumentCallSite(root, symbol!))
        {
            Skip(plan, LocalFunctionSkipReason.NamedArgumentCallSite,
                "a call site uses named arguments");
            return plan;
        }

        // Gate #9: Enclosing type parameter conflict
        var enclosingTypeParams = GetEnclosingMethodTypeParameters(localFn);
        if (enclosingTypeParams.Count > 0)
        {
            var localTypeParams = localFn.TypeParameterList?.Parameters
                .Select(p => p.Identifier.Text).ToList() ?? new List<string>();
            var conflicts = enclosingTypeParams.Where(tp => localTypeParams.Contains(tp)).ToList();
            if (conflicts.Count > 0)
            {
                Skip(plan, LocalFunctionSkipReason.EnclosingTypeParameterConflict,
                    $"enclosing method type parameter(s) conflict: {string.Join(", ", conflicts)}");
                return plan;
            }
        }

        return plan;
    }

    private void Skip(LiftPlan plan, LocalFunctionSkipReason reason, string message)
    {
        plan.Skipped = true;
        plan.SkipReason = reason;
        _stats.LocalFunctionsSkipped++;
        if (!_stats.SkippedByReason.TryAdd(reason, 1))
            _stats.SkippedByReason[reason]++;

        var lineSpan = plan.Node.GetLocation().GetLineSpan();
        var line = lineSpan.IsValid ? lineSpan.StartLinePosition.Line + 1 : 0;
        _diagnostics.Add(new LocalFunctionLiftDiagnostic(
            LocalFunctionLiftSeverity.Warning,
            $"Skipped local function '{plan.Node.Identifier.Text}': {message}",
            null, line, reason));
    }

    private static TypeDeclarationSyntax? FindHostType(LocalFunctionStatementSyntax localFn)
    {
        SyntaxNode? current = localFn.Parent;
        while (current != null)
        {
            if (current is TypeDeclarationSyntax typeDecl)
                return typeDecl;
            current = current.Parent;
        }
        return null;
    }

    private static bool IsInsideLambdaOrInitializer(LocalFunctionStatementSyntax localFn)
    {
        SyntaxNode? current = localFn.Parent;
        while (current != null)
        {
            if (current is AnonymousFunctionExpressionSyntax
                or SimpleLambdaExpressionSyntax
                or ParenthesizedLambdaExpressionSyntax)
                return true;
            if (current is VariableDeclaratorSyntax vd && vd.Initializer != null)
                return true;
            if (current is PropertyDeclarationSyntax pd
                && (pd.ExpressionBody != null || pd.Initializer != null))
                return true;
            if (current is TypeDeclarationSyntax)
                return false;
            current = current.Parent;
        }
        return false;
    }

    private static bool IsInsideCheckedContext(LocalFunctionStatementSyntax localFn)
    {
        SyntaxNode? current = localFn.Parent;
        while (current != null)
        {
            if (current is CheckedExpressionSyntax || current is CheckedStatementSyntax)
                return true;
            if (current is TypeDeclarationSyntax)
                return false;
            current = current.Parent;
        }
        return false;
    }

    private static bool DetermineStatic(LocalFunctionStatementSyntax localFn)
    {
        if (localFn.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
            return true;

        SyntaxNode? current = localFn.Parent;
        while (current != null)
        {
            if (current is LocalFunctionStatementSyntax)
            {
                current = current.Parent;
                continue;
            }
            if (current is MemberDeclarationSyntax member)
            {
                return member.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
            }
            current = current.Parent;
        }
        return false;
    }

    private List<CapturePlan> CollectCaptures(
        LocalFunctionStatementSyntax localFn,
        IMethodSymbol localFnSymbol,
        bool isStatic)
    {
        if (isStatic)
            return new List<CapturePlan>();

        var captures = new List<CapturePlan>();
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var body = (SyntaxNode?)localFn.Body ?? localFn.ExpressionBody;
        if (body == null)
            return captures;

        var capParamNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var identifier in body
            .DescendantNodes(d => d is not LocalFunctionStatementSyntax)
            .OfType<IdentifierNameSyntax>())
        {
            if (identifier.Parent is NameColonSyntax)
                continue;

            var symbol = _model.GetSymbolInfo(identifier).Symbol;
            switch (symbol)
            {
                case ILocalSymbol local when local.HasConstantValue:
                    break;
                case ILocalSymbol or IParameterSymbol or IRangeVariableSymbol:
                    if (SymbolEqualityComparer.Default.Equals(symbol.ContainingSymbol, localFnSymbol))
                        break;
                    if (!seen.Add(symbol))
                        break;

                    var name = symbol.Name;
                    var symbolType = symbol switch
                    {
                        ILocalSymbol ls => ls.Type,
                        IParameterSymbol ps => ps.Type,
                        _ => null
                    };

                    if (symbolType == null || symbolType.TypeKind == TypeKind.Error)
                        break;

                    var typeDisplay = symbolType.ToDisplayString(
                        SymbolDisplayFormat.FullyQualifiedFormat);
                    var isWritten = IsCapturedVariableWritten(localFn, symbol);
                    var capName = LocalFunctionLiftNaming.GetCaptureParameterName(
                        name, capParamNames);

                    captures.Add(new CapturePlan(
                        symbol, name, capName, typeDisplay, isWritten, isWritten));
                    break;
            }
        }

        return captures;
    }

    private bool IsCapturedVariableWritten(
        LocalFunctionStatementSyntax localFn,
        ISymbol symbol)
    {
        var body = (SyntaxNode?)localFn.Body ?? localFn.ExpressionBody;
        if (body == null)
            return false;

        foreach (var identifier in body
            .DescendantNodes(d => d is not LocalFunctionStatementSyntax)
            .OfType<IdentifierNameSyntax>())
        {
            if (identifier.Parent is NameColonSyntax)
                continue;
            var resolved = _model.GetSymbolInfo(identifier).Symbol;
            if (!SymbolEqualityComparer.Default.Equals(resolved, symbol))
                continue;

            var parent = identifier.Parent;
            if (parent is AssignmentExpressionSyntax assignment
                && assignment.Left == identifier)
                return true;
            if (parent is PrefixUnaryExpressionSyntax prefix
                && prefix.Operand == identifier
                && (prefix.IsKind(SyntaxKind.PreIncrementExpression)
                    || prefix.IsKind(SyntaxKind.PreDecrementExpression)))
                return true;
            if (parent is PostfixUnaryExpressionSyntax postfix
                && postfix.Operand == identifier
                && (postfix.IsKind(SyntaxKind.PostIncrementExpression)
                    || postfix.IsKind(SyntaxKind.PostDecrementExpression)))
                return true;
            if (parent is ArgumentSyntax argument
                && (argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)
                    || argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)))
                return true;
        }
        return false;
    }

    private void PropagateCaptures(List<LiftPlan> plans)
    {
        var bySymbol = plans.Where(p => p.Symbol != null)
            .ToDictionary(p => p.Symbol!, p => p, SymbolEqualityComparer.Default);

        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var plan in plans)
            {
                if (plan.Skipped)
                    continue;

                var body = (SyntaxNode?)plan.Node.Body ?? plan.Node.ExpressionBody;
                if (body == null)
                    continue;

                foreach (var identifier in body
                    .DescendantNodes(d => d is not LocalFunctionStatementSyntax)
                    .OfType<IdentifierNameSyntax>())
                {
                    if (identifier.Parent is NameColonSyntax)
                        continue;

                    var calleeSymbol = _model.GetSymbolInfo(identifier).Symbol;
                    if (calleeSymbol is not IMethodSymbol { MethodKind: MethodKind.LocalFunction } callee)
                        continue;
                    if (!bySymbol.TryGetValue(callee, out var calleePlan))
                        continue;
                    if (calleePlan.Skipped)
                        continue;

                    foreach (var capture in calleePlan.Captures)
                    {
                        var existing = plan.Captures.FirstOrDefault(c =>
                            SymbolEqualityComparer.Default.Equals(c.Symbol, capture.Symbol));
                        if (existing == null)
                        {
                            plan.Captures.Add(capture with { });
                            changed = true;
                        }
                        else if (capture.IsWritten && !existing.IsWritten)
                        {
                            plan.Captures[plan.Captures.IndexOf(existing)] =
                                existing with { IsWritten = true, ByRef = true };
                            changed = true;
                        }
                    }
                }
            }
        }
    }

    private void CheckPostPropagationGates(LiftPlan plan)
    {
        foreach (var cap in plan.Captures)
        {
            if (!cap.IsWritten)
                continue;

            if (!CanUseRefCapture(plan, cap))
            {
                Skip(plan, LocalFunctionSkipReason.RefCaptureUnavailable,
                    $"write capture '{cap.OriginalName}' cannot use ref: " +
                    GetRefCaptureUnavailableReason(plan, cap));
                return;
            }
        }
    }

    private bool CanUseRefCapture(LiftPlan plan, CapturePlan cap)
    {
        if (plan.Node.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)))
            return false;

        var sym = cap.Symbol;
        if (sym is ILocalSymbol)
        {
            var decl = FindVariableDeclarator(plan.Node, sym);
            if (decl != null)
            {
                if (IsForeachOrUsingOrFixedVariable(decl))
                    return false;
                if (decl.Initializer == null)
                    return false;
            }
        }
        if (sym is IParameterSymbol param)
        {
            if (param.RefKind is RefKind.In or RefKind.Out)
                return false;
        }

        return true;
    }

    private static string GetRefCaptureUnavailableReason(LiftPlan plan, CapturePlan cap)
    {
        if (plan.Node.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)))
            return "local function is async";
        if (cap.Symbol is ILocalSymbol)
        {
            var decl = FindVariableDeclarator(plan.Node, cap.Symbol);
            if (decl != null)
            {
                if (IsForeachOrUsingOrFixedVariable(decl))
                    return $"variable '{cap.OriginalName}' is a foreach/using/fixed variable";
                if (decl.Initializer == null)
                    return $"variable '{cap.OriginalName}' has no initializer";
            }
        }
        if (cap.Symbol is IParameterSymbol param)
        {
            if (param.RefKind is RefKind.In)
                return $"parameter '{cap.OriginalName}' is 'in'";
            if (param.RefKind is RefKind.Out)
                return $"parameter '{cap.OriginalName}' is 'out'";
        }
        return "ref not available";
    }

    private static VariableDeclaratorSyntax? FindVariableDeclarator(
        SyntaxNode scope, ISymbol symbol)
    {
        foreach (var decl in scope.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (decl.Identifier.Text == symbol.Name)
                return decl;
        }
        return null;
    }

    private static bool IsForeachOrUsingOrFixedVariable(VariableDeclaratorSyntax decl)
    {
        SyntaxNode? current = decl.Parent;
        while (current != null)
        {
            if (current is ForEachStatementSyntax
                or UsingStatementSyntax
                or FixedStatementSyntax)
                return true;
            if (current is MemberDeclarationSyntax)
                return false;
            current = current.Parent;
        }
        return false;
    }

    private bool HasMethodGroupReference(
        CompilationUnitSyntax root,
        IMethodSymbol symbol)
    {
        foreach (var id in root.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (id.Parent is NameColonSyntax)
                continue;
            var sym = _model.GetSymbolInfo(id).Symbol;
            if (!SymbolEqualityComparer.Default.Equals(sym, symbol))
                continue;
            if (id.Parent is InvocationExpressionSyntax invocation
                && invocation.Expression == id)
                continue;
            if (IsInsideNameOf(id))
                continue;
            return true;
        }
        return false;
    }

    private static bool IsInsideNameOf(SyntaxNode node)
    {
        SyntaxNode? current = node.Parent;
        while (current != null)
        {
            if (current is InvocationExpressionSyntax invocation
                && invocation.Expression is IdentifierNameSyntax id
                && id.Identifier.Text == "nameof")
                return true;
            current = current.Parent;
        }
        return false;
    }

    private bool HasNamedArgumentCallSite(
        CompilationUnitSyntax root,
        IMethodSymbol symbol)
    {
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not IdentifierNameSyntax
                && invocation.Expression is not GenericNameSyntax)
                continue;

            var sym = _model.GetSymbolInfo(invocation).Symbol;
            if (!SymbolEqualityComparer.Default.Equals(sym, symbol))
                continue;

            foreach (var arg in invocation.ArgumentList.Arguments)
            {
                if (arg.NameColon != null)
                    return true;
            }
        }
        return false;
    }

    private static bool IsUnsupportedCaptureType(CapturePlan cap)
    {
        var typeDisplay = cap.TypeDisplay;
        if (typeDisplay.Contains("<anonymous"))
            return true;
        if (typeDisplay.Contains("*"))
            return true;
        if (typeDisplay.StartsWith("ref "))
            return true;
        return false;
    }

    private static bool HasParamsOrOptional(LocalFunctionStatementSyntax localFn)
    {
        if (localFn.ParameterList == null)
            return false;
        foreach (var param in localFn.ParameterList.Parameters)
        {
            if (param.Default != null)
                return true;
            if (param.Modifiers.Any(m => m.IsKind(SyntaxKind.ParamsKeyword)))
                return true;
        }
        return false;
    }

    private static List<string> GetEnclosingMethodTypeParameters(
        LocalFunctionStatementSyntax localFn)
    {
        var result = new List<string>();
        SyntaxNode? current = localFn.Parent;
        while (current != null)
        {
            if (current is MethodDeclarationSyntax method
                && method.TypeParameterList != null)
            {
                foreach (var tp in method.TypeParameterList.Parameters)
                    result.Add(tp.Identifier.Text);
            }
            if (current is TypeDeclarationSyntax)
                break;
            current = current.Parent;
        }
        return result;
    }

    private string AssignName(
        LocalFunctionStatementSyntax localFn,
        TypeDeclarationSyntax hostType,
        IMethodSymbol symbol)
    {
        var baseName = LocalFunctionLiftNaming.GetBaseName(localFn.Identifier.Text);

        var typeSymbol = _model.GetDeclaredSymbol(hostType) as INamedTypeSymbol;
        var memberNames = new HashSet<string>(StringComparer.Ordinal);
        if (typeSymbol != null)
        {
            foreach (var member in typeSymbol.GetMembers())
                memberNames.Add(member.Name);
        }

        var typeKey = typeSymbol?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            ?? hostType.Identifier.Text;
        if (!_crossFileNameTable.TryGetValue(typeKey, out var usedNames))
        {
            usedNames = new HashSet<string>(StringComparer.Ordinal);
            _crossFileNameTable[typeKey] = usedNames;
        }

        var combined = new HashSet<string>(memberNames, StringComparer.Ordinal);
        foreach (var n in usedNames)
            combined.Add(n);

        var finalName = LocalFunctionLiftNaming.ResolveUniqueName(baseName, combined);
        usedNames.Add(finalName);

        return finalName;
    }
}
