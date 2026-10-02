using CSharpToJava.Core.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Linq;

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

        // Gate #12: References skipped local functions.
        // If a non-skipped local function calls a skipped local function,
        // it cannot be lifted because the skipped function remains local.
        CheckReferencesToSkippedLocalFunctions(plans);

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

        // Gate #1 (disabled): Iterator (yield return / yield break).
        // Iterator local functions can be lifted to class-level methods
        // that also use yield return. The hoisted method will be a valid
        // iterator method.
        // Note: iterator methods cannot have ref/out parameters, so if
        // captures require ref, the post-propagation gate will catch it.

        // Gate #3: No host type
        var hostType = FindHostType(localFn);
        if (hostType == null)
        {
            Skip(plan, LocalFunctionSkipReason.NoHostType, "no enclosing type declaration found");
            return plan;
        }
        if (IsInsideFieldOrPropertyInitializer(localFn))
        {
            Skip(plan, LocalFunctionSkipReason.NoHostType, "local function is inside a field/property initializer");
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
        var isExplicitStatic = IsExplicitStaticLocalFunction(localFn);

        // Gate #5a: Static local functions with the 'static' modifier cannot
        // capture enclosing locals. However, instead of skipping them, we
        // treat them as non-static so captures are collected properly.
        // The hoisted method will be static (since the enclosing method is
        // typically static) and captures will be passed as parameters.
        // Only skip if the enclosing method is an instance method — in that
        // case, the hoisted method would need 'this' which a static local
        // function doesn't have.
        if (isExplicitStatic && ReferencesEnclosingLocals(localFn, symbol!))
        {
            // Check if enclosing method is static. If so, we can safely
            // treat this as non-static for capture purposes.
            if (plan.IsStatic)
            {
                // Enclosing method is static, so the hoisted method will be
                // static too. Treat as non-static for capture collection.
                isExplicitStatic = false;
            }
            else
            {
                // Enclosing method is an instance method. A static local
                // function inside an instance method that references
                // enclosing locals cannot be safely lifted.
                Skip(plan, LocalFunctionSkipReason.UnsupportedCaptureType,
                    "static local function references enclosing local variables/parameters inside instance method");
                return plan;
            }
        }

        // Gate #5 (disabled): Method group / delegate conversion reference.
        // We now support this case — the rewriter will rename the method group
        // reference to the new method name. The delegate conversion still works
        // because the hoisted method has the same signature.

        // Collect captures
        plan.Captures = CollectCaptures(localFn, symbol!, isExplicitStatic);

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

        // Gate #11 (removed): Named argument at call sites.
        // Named arguments still work after lifting because capture parameters
        // are prepended as positional arguments, and named arguments reference
        // the original parameter names which remain unchanged. C# allows
        // mixing positional and named arguments as long as positionals come first.

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

        // Gate #9b (disabled): Local function uses enclosing method type parameters.
        // We now support this — the rewriter will add explicit type arguments
        // at call sites to avoid type inference failures.
        if (enclosingTypeParams.Count > 0)
        {
            var ownTypeParams = localFn.TypeParameterList?.Parameters
                .Select(p => p.Identifier.Text)
                .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);

            // Collect all identifier names used in the local function
            var typeIdentifiers = new HashSet<string>(StringComparer.Ordinal);
            if (localFn.ParameterList != null)
            {
                foreach (var param in localFn.ParameterList.Parameters)
                    CollectIdentifierNames(param.Type, typeIdentifiers);
            }
            CollectIdentifierNames(localFn.ReturnType, typeIdentifiers);
            var body = (SyntaxNode?)localFn.Body ?? localFn.ExpressionBody;
            if (body != null)
            {
                foreach (var id in body.DescendantNodes().OfType<IdentifierNameSyntax>())
                    typeIdentifiers.Add(id.Identifier.Text);
            }

            // Determine which enclosing type params are used by the local function
            // but not in its own type parameter list.
            var usedEnclosingParams = new List<string>();
            foreach (var tp in enclosingTypeParams)
            {
                if (!ownTypeParams.Contains(tp) && typeIdentifiers.Contains(tp))
                    usedEnclosingParams.Add(tp);
            }

            if (usedEnclosingParams.Count > 0)
            {
                plan.EnclosingTypeParams = usedEnclosingParams;
                plan.NeedsExplicitTypeArguments = true;
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

    /// <summary>
    /// Checks if the local function is inside a field or property initializer.
    /// Local functions inside lambdas are OK (they can be lifted to the class level),
    /// but local functions inside field/property initializers cannot be lifted
    /// because the initializer context is not a method body.
    /// </summary>
    private static bool IsInsideFieldOrPropertyInitializer(
        LocalFunctionStatementSyntax localFn)
    {
        SyntaxNode? current = localFn.Parent;
        while (current != null)
        {
            // Field initializer: variable declarator with initializer,
            // whose parent is a FieldDeclarationSyntax
            if (current is VariableDeclaratorSyntax vd
                && vd.Initializer != null
                && vd.Parent?.Parent is FieldDeclarationSyntax)
                return true;
            // Property initializer
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

    /// <summary>
    /// Returns true if the local function itself has the 'static' modifier
    /// (as opposed to merely being inside a static method).
    /// </summary>
    private static bool IsExplicitStaticLocalFunction(LocalFunctionStatementSyntax localFn)
    {
        return localFn.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
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

    /// <summary>
    /// Checks if a static local function references local variables or
    /// parameters from the enclosing scope (which would require captures
    /// that a static hoisted method cannot have).
    /// </summary>
    private bool ReferencesEnclosingLocals(
        LocalFunctionStatementSyntax localFn,
        IMethodSymbol localFnSymbol)
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

            var symbol = _model.GetSymbolInfo(identifier).Symbol;
            if (symbol is ILocalSymbol or IParameterSymbol or IRangeVariableSymbol)
            {
                // Skip references to the local function's own parameters
                if (SymbolEqualityComparer.Default.Equals(symbol.ContainingSymbol, localFnSymbol))
                    continue;
                return true;
            }
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

        var body = (SyntaxNode?)localFn.Body ?? localFn.ExpressionBody;
        if (body == null)
            return new List<CapturePlan>();

        // C32 §4.2: capture discovery is the single shared primitive.
        var capParamNames = new HashSet<string>(StringComparer.Ordinal);
        return CaptureClassification.CollectCapturedSymbols(
                _model,
                body,
                localFnSymbol,
                d => d is not LocalFunctionStatementSyntax)
            .Select(info =>
            {
                var isWritten = IsCapturedVariableWritten(localFn, info.Symbol);
                var capName = LocalFunctionLiftNaming.GetCaptureParameterName(
                    info.Name, capParamNames);
                return new CapturePlan(
                    info.Symbol,
                    info.Name,
                    capName,
                    info.TypeDisplayFullyQualified,
                    isWritten,
                    isWritten);
            })
            .ToList();
    }

    private bool IsCapturedVariableWritten(
        LocalFunctionStatementSyntax localFn,
        ISymbol symbol)
    {
        var body = (SyntaxNode?)localFn.Body ?? localFn.ExpressionBody;
        if (body == null)
            return false;

        // C32 §4.2: write-site discovery is the single shared primitive.
        return CaptureClassification.FindWriteSites(
                _model,
                symbol,
                body,
                d => d is not LocalFunctionStatementSyntax)
            .Count > 0;
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

    /// <summary>
    /// Gate #12: If a non-skipped local function references a skipped local
    /// function, it must also be skipped because the skipped function remains
    /// local and cannot be accessed from the class-level method.
    /// Uses iterative fixpoint propagation because skipping a function may
    /// cause other functions that reference it to also need skipping.
    /// </summary>
    private void CheckReferencesToSkippedLocalFunctions(List<LiftPlan> plans)
    {
        var skippedSymbols = new HashSet<ISymbol>(
            plans.Where(p => p.Skipped && p.Symbol != null)
                 .Select(p => p.Symbol!),
            SymbolEqualityComparer.Default);

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

                    var symbol = _model.GetSymbolInfo(identifier).Symbol;
                    if (symbol != null && skippedSymbols.Contains(symbol))
                    {
                        Skip(plan, LocalFunctionSkipReason.ReferencesSkippedLocalFunction,
                            $"references skipped local function '{identifier.Identifier.Text}'");
                        if (plan.Symbol != null)
                            skippedSymbols.Add(plan.Symbol);
                        changed = true;
                        break;
                    }
                }
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
            if (CaptureClassification.IsInsideNameOf(id))
                continue;
            return true;
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
        // C32 §4.2: unsupported-kind classification is the single shared primitive.
        => CaptureClassification.IsUnsupportedCaptureKind(cap.TypeDisplay);

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

    /// <summary>
    /// Gate #9b (strict): Checks if the local function uses ANY enclosing
    /// method type parameters in its body, parameter types, or return type.
    /// After lifting, the hoisted method becomes a generic method and type
    /// inference at call sites may fail (e.g. when arguments are default,
    /// null, or lambda expressions). To be safe, we skip all such functions.
    /// </summary>
    private static bool UsesEnclosingTypeParameters(
        LocalFunctionStatementSyntax localFn)
    {
        var enclosingParams = GetEnclosingMethodTypeParameters(localFn);
        if (enclosingParams.Count == 0)
            return false;

        // Type parameters declared on the local function itself
        var ownTypeParams = localFn.TypeParameterList?.Parameters
            .Select(p => p.Identifier.Text)
            .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);

        // Collect all identifier names used in parameter types and return type
        var typeIdentifiers = new HashSet<string>(StringComparer.Ordinal);
        if (localFn.ParameterList != null)
        {
            foreach (var param in localFn.ParameterList.Parameters)
                CollectIdentifierNames(param.Type, typeIdentifiers);
        }
        CollectIdentifierNames(localFn.ReturnType, typeIdentifiers);

        // Collect all identifier names used in the body
        var body = (SyntaxNode?)localFn.Body ?? localFn.ExpressionBody;
        if (body != null)
        {
            foreach (var id in body.DescendantNodes().OfType<IdentifierNameSyntax>())
                typeIdentifiers.Add(id.Identifier.Text);
        }

        // Check if any enclosing type parameter (not in own type params) is used
        foreach (var tp in enclosingParams)
        {
            if (ownTypeParams.Contains(tp))
                continue;
            if (typeIdentifiers.Contains(tp))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Gate #9b (legacy): Checks if the local function references enclosing method
    /// type parameters that are NOT in its own type parameter list or
    /// parameter list. After lifting, call sites would need to explicitly
    /// specify these type parameters, which they don't.
    /// </summary>
    private static bool UsesEnclosingTypeParametersNotInParameters(
        LocalFunctionStatementSyntax localFn)
    {
        var enclosingParams = GetEnclosingMethodTypeParameters(localFn);
        if (enclosingParams.Count == 0)
            return false;

        // Type parameters declared on the local function itself
        var ownTypeParams = localFn.TypeParameterList?.Parameters
            .Select(p => p.Identifier.Text)
            .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);

        // Type parameters that appear in the local function's parameter types
        // and return type. Note: we must check the type node itself, not just
        // its descendants, because a simple type like "TDict" is itself an
        // IdentifierNameSyntax with no descendant nodes.
        var paramsInParameters = new HashSet<string>(StringComparer.Ordinal);
        if (localFn.ParameterList != null)
        {
            foreach (var param in localFn.ParameterList.Parameters)
            {
                CollectIdentifierNames(param.Type, paramsInParameters);
            }
        }

        // Also check the return type
        CollectIdentifierNames(localFn.ReturnType, paramsInParameters);

        // For each enclosing type parameter, check if it's used in the
        // local function body but not in its own type params or parameter types.
        var body = (SyntaxNode?)localFn.Body ?? localFn.ExpressionBody;
        if (body == null)
            return false;

        var bodyIdentifiers = body.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Select(id => id.Identifier.Text)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var tp in enclosingParams)
        {
            if (ownTypeParams.Contains(tp))
                continue;
            // If the type parameter is used in the body but not in parameters
            if (bodyIdentifiers.Contains(tp) && !paramsInParameters.Contains(tp))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Collects all IdentifierName text from a type syntax node, including
    /// the node itself if it is an IdentifierNameSyntax. This is needed
    /// because DescendantNodes() does not include the node itself.
    /// </summary>
    private static void CollectIdentifierNames(
        TypeSyntax? type, HashSet<string> names)
    {
        if (type == null)
            return;
        if (type is IdentifierNameSyntax id)
            names.Add(id.Identifier.Text);
        foreach (var descendant in type.DescendantNodes().OfType<IdentifierNameSyntax>())
            names.Add(descendant.Identifier.Text);
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

        // Collect local variable names in the enclosing method to avoid
        // naming conflicts when the hoisted method name shadows a local.
        foreach (var localName in GetEnclosingMethodLocalVariableNames(localFn))
            combined.Add(localName);

        var finalName = LocalFunctionLiftNaming.ResolveUniqueName(baseName, combined);
        usedNames.Add(finalName);

        return finalName;
    }

    /// <summary>
    /// Collects all local variable and local constant names declared in the
    /// enclosing method (between the method start and the local function),
    /// so that the hoisted method name doesn't shadow them.
    /// </summary>
    private static HashSet<string> GetEnclosingMethodLocalVariableNames(
        LocalFunctionStatementSyntax localFn)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        SyntaxNode? current = localFn.Parent;
        while (current != null)
        {
            if (current is TypeDeclarationSyntax)
                break;
            if (current is BaseMethodDeclarationSyntax method)
            {
                CollectLocalVariableNames(method, names);
                break;
            }
            current = current.Parent;
        }
        return names;
    }

    private static void CollectLocalVariableNames(
        SyntaxNode node, HashSet<string> names)
    {
        foreach (var decl in node.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>())
        {
            names.Add(decl.Identifier.Text);
        }
        foreach (var param in node.DescendantNodes()
            .OfType<ParameterSyntax>())
        {
            names.Add(param.Identifier.Text);
        }
    }
}
