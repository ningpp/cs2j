using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Analysis;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>How a non-quoted reference site is treated by the rewriter.</summary>
internal enum ClosureSiteKind
{
    Read,
    Write,
    Lock,
}

/// <summary>
/// One reference site of a captured symbol. Rewrite-eligible sites never
/// include nameof (a string literal) or quoted expression-tree lambdas (D11).
/// </summary>
internal sealed record ClosureSiteRef(
    SyntaxReference Reference,
    ClosureSiteKind Kind,
    CaptureWriteKind? WriteKind,
    bool InsideLambda,
    bool InsideQuotedLambda);

/// <summary>Analysis record for one candidate symbol inside one routine.</summary>
/// <remarks>
/// A record so the frozen skip rows can use <c>with</c>. The routine is a
/// <see cref="SyntaxNode"/> (not MemberDeclarationSyntax) because accessors
/// (property/indexer/event) are not member declarations in Roslyn's model.
/// </remarks>
internal sealed record CapturedSymbolAnalysis
{
    public required ISymbol Symbol { get; init; }
    public required string Name { get; init; }
    public required string TypeDisplay { get; init; }
    public required SyntaxReference DeclaringReference { get; init; }
    public required SyntaxNode EnclosingMember { get; init; }
    public required TypeDeclarationSyntax HostType { get; init; }
    public required string RoutineName { get; init; }

    /// <summary>Block the instance declaration belongs to (null for non-block foreach bodies wrapped in M2).</summary>
    public BlockSyntax? ScopeBlock { get; init; }

    public required TriggerReason Reason { get; init; }
    public bool IsForeachIteration { get; init; }
    public ForEachStatementSyntax? ForeachLoop { get; init; }
    /// <summary>Set when the symbol is a for-loop induction variable (instance goes right before the loop).</summary>
    public ForStatementSyntax? ForLoop { get; init; }
    public VariableDeclaratorSyntax? Declarator { get; init; }
    public ParameterSyntax? Parameter { get; init; }

    /// <summary>True when a CS2J3201-3206 row fired; the file must stay verbatim for this symbol's routine.</summary>
    public bool Skipped { get; init; }
    public string? SkipCode { get; init; }

    public List<ClosureSiteRef> Sites { get; init; } = new();
    public List<ClosureSiteRef> QuotedSites { get; init; } = new();
}

internal sealed record MemberCaptureAnalysis(
    TypeDeclarationSyntax HostType,
    SyntaxNode Routine,
    string RoutineName,
    List<CapturedSymbolAnalysis> Symbols);

internal sealed record ClosureAnalysisResult(
    List<MemberCaptureAnalysis> Members,
    List<CapturedSymbolAnalysis> AllSymbols);

/// <summary>
/// First pass of design §4.1: whole CompilationUnit, read-only.
/// Residual anonymous methods/local-function boundaries and initializer
/// lambdas are diagnosed before any rewrite (skip/error set is frozen first).
/// </summary>
internal sealed class CaptureAnalyzer
{
    private static readonly Func<SyntaxNode, bool> DescendNotIntoLocalFunctions
        = static d => d is not LocalFunctionStatementSyntax;

    private readonly SemanticModel _model;
    private readonly ClosureLiftOptions _options;
    private readonly List<ClosureLiftDiagnostic> _diagnostics;

    public CaptureAnalyzer(
        SemanticModel model,
        ClosureLiftOptions options,
        List<ClosureLiftDiagnostic> diagnostics)
    {
        _model = model;
        _options = options;
        _diagnostics = diagnostics;
    }

    public ClosureAnalysisResult Analyze(CompilationUnitSyntax root)
    {
        var all = new List<CapturedSymbolAnalysis>();
        var byRoutine = new Dictionary<SyntaxNode, MemberCaptureWork>();

        // D14 residual: anonymous methods should have been lowered by E05.
        foreach (var anon in root.DescendantNodes().OfType<AnonymousMethodExpressionSyntax>())
        {
            Emit(ClosureLiftCodes.ResidualAnonymousMethod,
                "Residual anonymous method cannot be capture-lifted (delegate pass output expected).",
                anon.GetLocation());
        }

        var lambdas = root.DescendantNodes().OfType<LambdaExpressionSyntax>().ToList();
        var quoted = new HashSet<LambdaExpressionSyntax>();
        foreach (var lambda in lambdas)
        {
            if (ExpressionTreeFacts.IsExpressionTreeLambda(_model, lambda))
                quoted.Add(lambda);
        }

        // Candidate symbols discovered from statement lambdas, keyed by routine
        // (SyntaxNode: methods/accessors share no closer common declared type).
        var candidateByRoutine = new Dictionary<SyntaxNode, Dictionary<ISymbol, LambdaExpressionSyntax>>();

        foreach (var lambda in lambdas)
        {
            var routine = ResolveRoutine(lambda, out var boundaryCode, out var boundaryNode);
            if (routine == null)
            {
                if (boundaryCode != null)
                    Emit(boundaryCode, BoundaryMessage(boundaryCode), boundaryNode!.GetLocation());
                continue;
            }

            // D11: quoted lambdas contribute no mutable candidates of their own.
            if (quoted.Contains(lambda))
                continue;

            // D12: static lambdas capture nothing.
            if (lambda.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword)))
                continue;

            var lambdaBody = lambda is ParenthesizedLambdaExpressionSyntax parenthesized
                ? (SyntaxNode?)parenthesized.Block ?? parenthesized.ExpressionBody
                : ((SimpleLambdaExpressionSyntax)lambda).Block is { } block2
                    ? block2
                    : ((SimpleLambdaExpressionSyntax)lambda).ExpressionBody;
            if (lambdaBody == null)
                continue;

            // The synthesized anonymous-function method symbol (its parameter
            // symbols own the lambda's own parameters) is exposed via
            // GetSymbolInfo in Roslyn 5; GetDeclaredSymbol does not accept
            // anonymous-function syntax.
            var lambdaSymbol = _model.GetSymbolInfo(lambda).Symbol as IMethodSymbol;

            foreach (var info in CaptureClassification.CollectCapturedSymbols(
                _model, lambdaBody, lambdaSymbol, DescendNotIntoLocalFunctions))
            {
                if (!candidateByRoutine.TryGetValue(routine, out var map))
                {
                    map = new Dictionary<ISymbol, LambdaExpressionSyntax>(SymbolEqualityComparer.Default);
                    candidateByRoutine[routine] = map;
                }

                map.TryAdd(info.Symbol, lambda);
            }
        }

        foreach (var (routineNode, candidateMap) in candidateByRoutine)
        {
            var hostType = routineNode.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();

            var bodyRoot = GetBodyRoot(routineNode);
            if (bodyRoot == null)
            {
                // No statement body to place a holder instance (Task 10 contract #2;
                // expression-bodied routines and the D16 boundary family).
                Emit(ClosureLiftCodes.UnsupportedBoundary,
                    "Mutably captured variable in a routine without a block body cannot be closure-lifted in v1.",
                    routineNode.GetLocation(),
                    hostType is null ? null : GetRoutineName(routineNode, hostType),
                    candidateMap.Keys.FirstOrDefault()?.Name);
                continue;
            }

            if (hostType is not (ClassDeclarationSyntax or StructDeclarationSyntax
                or RecordDeclarationSyntax))
            {
                // No insertable host type (interface default method / top-level statements).
                foreach (var symbol in candidateMap.Keys)
                {
                    Emit(ClosureLiftCodes.UnsupportedBoundary,
                        $"Captured variable '{symbol.Name}' has no holder host type (interface/top-level boundary is unsupported in v1).",
                        symbol.Locations.FirstOrDefault());
                }
                continue;
            }

            var routineName = GetRoutineName(routineNode, hostType);
            if (!byRoutine.TryGetValue(routineNode, out var work))
            {
                work = new MemberCaptureWork(hostType, routineNode, routineName);
                byRoutine[routineNode] = work;
            }

            foreach (var symbol in candidateMap.Keys)
            {
                // Idempotency: never re-lift our own generated instances (D8/§4.1).
                if (symbol.Name.StartsWith("__cs2jClosure", StringComparison.Ordinal)
                    || symbol.Name.StartsWith("__cs2jArr_", StringComparison.Ordinal))
                    continue;

                var analysis = AnalyzeSymbol(symbol, routineNode, bodyRoot, hostType, routineName, quoted);
                if (analysis == null)
                    continue;

                work.Symbols.Add(analysis);
                all.Add(analysis);
            }
        }

        // Deterministic order: routines by first symbol span; symbols by declaring span (§4.3).
        var members = byRoutine.Values
            .Select(w => new MemberCaptureAnalysis(
                w.HostType, w.Routine, w.RoutineName,
                w.Symbols.OrderBy(s => s.DeclaringReference.Span.Start).ToList()))
            .OrderBy(m => m.Symbols.First().DeclaringReference.Span.Start)
            .ToList();

        return new ClosureAnalysisResult(members, all);
    }

    private CapturedSymbolAnalysis? AnalyzeSymbol(
        ISymbol symbol,
        SyntaxNode routine,
        SyntaxNode bodyRoot,
        TypeDeclarationSyntax hostType,
        string routineName,
        ISet<LambdaExpressionSyntax> quoted)
    {
        var declaringRef = symbol.DeclaringSyntaxReferences.FirstOrDefault(r => r.SyntaxTree == bodyRoot.SyntaxTree);
        if (declaringRef == null)
        {
            Emit(ClosureLiftCodes.UnresolvableSymbol,
                $"Captured symbol '{symbol.Name}' has no declarator in this tree; the complete site set cannot be guaranteed.",
                symbol.Locations.FirstOrDefault());
            return null;
        }

        var declaringNode = declaringRef.GetSyntax();

        ParameterSyntax? parameter = declaringNode as ParameterSyntax;
        VariableDeclaratorSyntax? declarator = declaringNode as VariableDeclaratorSyntax;
        // Classic foreach iteration variables expose the ForEachStatementSyntax
        // itself as their declaring syntax (no VariableDeclarator exists in the
        // tree). Deconstructing foreach (ForEachVariableStatementSyntax) is a
        // different node type and stays unrecognized in M1 (plan Task 10 note).
        ForEachStatementSyntax? foreachDirect = declaringNode as ForEachStatementSyntax;
        if (parameter == null && declarator == null && foreachDirect == null)
            return null;

        // D6: by-ref parameter (RefKind != None; C01 ByRef<T> has RefKind.None and is ordinary).
        if (parameter != null)
        {
            var parameterSymbol = (IParameterSymbol)symbol;
            if (parameterSymbol.RefKind != RefKind.None)
            {
                return Skip(symbol, routine, bodyRoot, hostType, routineName, declaringRef,
                    ClosureLiftCodes.RefCapture,
                    $"By-ref parameter '{symbol.Name}' captured by a lambda cannot be field-backed before C01 normalization (D6).",
                    parameter, null);
            }
        }

        ForEachStatementSyntax? foreachLoop = null;
        ForStatementSyntax? forLoop = null;
        BlockSyntax? scopeBlock = null;

        if (declarator != null && declarator.Parent is VariableDeclarationSyntax declaration)
        {
            switch (declaration.Parent)
            {
                case ForEachStatementSyntax fe when fe.DescendantNodes().Contains(declarator):
                    foreachLoop = fe;
                    scopeBlock = fe.Statement as BlockSyntax;
                    break;
                case ForStatementSyntax fs when fs.DescendantNodes().Contains(declarator):
                    forLoop = fs;
                    scopeBlock = fs.Ancestors().OfType<BlockSyntax>().FirstOrDefault();
                    break;
                case LocalDeclarationStatementSyntax local:
                    // D6: ref local.
                    if (local.Declaration.Type is Microsoft.CodeAnalysis.CSharp.Syntax.RefTypeSyntax)
                    {
                        return Skip(symbol, routine, bodyRoot, hostType, routineName, declaringRef,
                            ClosureLiftCodes.RefCapture,
                            $"ref local '{symbol.Name}' captured by a lambda cannot be field-backed (D6).",
                            null, declarator);
                    }
                    scopeBlock = local.Ancestors().OfType<BlockSyntax>().FirstOrDefault();
                    break;
                default:
                    // catch/using/argument-declaration locals are never mutable-captured
                    // in baseline-valid code; treat conservatively as no trigger below.
                    break;
            }
        }

        if (foreachDirect != null)
        {
            foreachLoop = foreachDirect;
            scopeBlock = foreachDirect.Statement as BlockSyntax;
        }

        if (parameter != null)
            scopeBlock = (routine as BaseMethodDeclarationSyntax)?.Body
                ?? (routine as AccessorDeclarationSyntax)?.Body;

        // D15: ErrorType / pointer / anonymous kinds that cannot be spelled.
        var typeDisplay = symbol switch
        {
            ILocalSymbol ls => ls.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            IParameterSymbol ps => ps.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            _ => null,
        };

        if (typeDisplay == null
            || (symbol is ILocalSymbol { Type.TypeKind: TypeKind.Error })
            || (symbol is IParameterSymbol { Type.TypeKind: TypeKind.Error }))
        {
            return Skip(symbol, routine, bodyRoot, hostType, routineName, declaringRef,
                ClosureLiftCodes.UnresolvableSymbol,
                $"Captured symbol '{symbol.Name}' has an unresolvable type (D15).",
                parameter, declarator);
        }

        if (CaptureClassification.IsUnsupportedCaptureKind(typeDisplay))
        {
            return Skip(symbol, routine, bodyRoot, hostType, routineName, declaringRef,
                ClosureLiftCodes.UnsupportedCaptureKind,
                $"Captured symbol '{symbol.Name}' has pointer/anonymous kind '{typeDisplay}' that cannot be field-backed in source (D15).",
                parameter, declarator);
        }

        // Site enumeration across the whole routine (external + lambda sites).
        var writeSpans = CaptureClassification
            .FindWriteSites(_model, symbol, bodyRoot, DescendNotIntoLocalFunctions)
            .ToDictionary(s => s.Identifier.Span.Start, s => (CaptureWriteKind?)s.Kind);

        var sites = new List<ClosureSiteRef>();
        var quotedSites = new List<ClosureSiteRef>();

        foreach (var identifier in bodyRoot
            .DescendantNodes(DescendNotIntoLocalFunctions)
            .OfType<IdentifierNameSyntax>())
        {
            if (identifier.Parent is NameColonSyntax)
                continue;
            if (!SymbolEqualityComparer.Default.Equals(_model.GetSymbolInfo(identifier).Symbol, symbol))
                continue;
            if (CaptureClassification.IsInsideNameOf(identifier))
                continue;

            var enclosingLambda = identifier.Ancestors().OfType<LambdaExpressionSyntax>().FirstOrDefault();
            var insideQuoted = enclosingLambda != null && quoted.Contains(enclosingLambda);
            writeSpans.TryGetValue(identifier.Span.Start, out var writeKind);

            var isLock = identifier.Parent is LockStatementSyntax lockStatement
                && lockStatement.Expression == identifier;

            var kind = writeKind.HasValue ? ClosureSiteKind.Write
                : isLock ? ClosureSiteKind.Lock
                : ClosureSiteKind.Read;

            var siteRef = new ClosureSiteRef(
                identifier.GetReference(),
                kind,
                writeKind,
                enclosingLambda != null,
                insideQuoted);

            if (insideQuoted)
                quotedSites.Add(siteRef);
            else
                sites.Add(siteRef);
        }

        // D2 trigger predicate.
        TriggerReason? reason = null;
        if (foreachLoop != null)
        {
            // D9 conservative: every foreach iteration variable captured by a lambda lifts.
            reason = TriggerReason.ForeachIteration;
        }
        else
        {
            var writes = sites.Where(s => s.WriteKind.HasValue).ToList();
            if (writes.Count > 0)
            {
                if (writes.Any(s => s.WriteKind == CaptureWriteKind.RefOutArgument))
                    reason = TriggerReason.RefOutArgument;
                else if (writes.Any(s => s.InsideLambda))
                    reason = TriggerReason.InsideLambdaWrite;
                else
                    reason = TriggerReason.ExternalWrite;
            }
        }

        if (reason == null)
        {
            // D3: read-only capture — effectively final, no holder in C32.
            return null;
        }

        var analysis = new CapturedSymbolAnalysis
        {
            Symbol = symbol,
            Name = symbol.Name,
            TypeDisplay = typeDisplay!,
            DeclaringReference = declaringRef,
            EnclosingMember = routine,
            HostType = hostType,
            RoutineName = routineName,
            ScopeBlock = scopeBlock,
            Reason = reason.Value,
            IsForeachIteration = foreachLoop != null,
            ForeachLoop = foreachLoop,
            ForLoop = forLoop,
            Declarator = declarator,
            Parameter = parameter,
            Sites = sites,
            QuotedSites = quotedSites,
        };

        // D11: a mutable symbol also referenced inside a quoted lambda — rewrite would
        // dangle the in-tree reference; fail the symbol and keep the file verbatim.
        if (quotedSites.Count > 0)
        {
            Emit(ClosureLiftCodes.ExpressionTreeMix,
                $"Mutable captured symbol '{symbol.Name}' is also referenced inside an expression-tree lambda (D11).",
                symbol.Locations.FirstOrDefault(), routineName, symbol.Name, reason);
            return analysis with { Skipped = true, SkipCode = ClosureLiftCodes.ExpressionTreeMix };
        }

        return analysis;
    }

    private CapturedSymbolAnalysis Skip(
        ISymbol symbol,
        SyntaxNode routine,
        SyntaxNode bodyRoot,
        TypeDeclarationSyntax hostType,
        string routineName,
        SyntaxReference declaringRef,
        string code,
        string message,
        ParameterSyntax? parameter,
        VariableDeclaratorSyntax? declarator)
    {
        Emit(code, message, symbol.Locations.FirstOrDefault(), routineName, symbol.Name, null);
        return new CapturedSymbolAnalysis
        {
            Symbol = symbol,
            Name = symbol.Name,
            TypeDisplay = symbol switch
            {
                ILocalSymbol ls => ls.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                IParameterSymbol ps => ps.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                _ => "?",
            },
            DeclaringReference = declaringRef,
            EnclosingMember = routine,
            HostType = hostType,
            RoutineName = routineName,
            Reason = TriggerReason.ExternalWrite,
            Parameter = parameter,
            Declarator = declarator,
            Skipped = true,
            SkipCode = code,
        };
    }

    private SyntaxNode? ResolveRoutine(
        LambdaExpressionSyntax lambda,
        out string? boundaryCode,
        out SyntaxNode? boundaryNode)
    {
        boundaryCode = null;
        boundaryNode = null;

        for (var current = lambda.Parent; current != null; current = current.Parent)
        {
            switch (current)
            {
                case AccessorDeclarationSyntax accessor:
                    return accessor;
                case BaseMethodDeclarationSyntax method:
                    return method;
                case LocalFunctionStatementSyntax:
                    boundaryCode = ClosureLiftCodes.UnsupportedBoundary;
                    boundaryNode = current;
                    return null;
                case PropertyDeclarationSyntax:
                case IndexerDeclarationSyntax:
                case BaseFieldDeclarationSyntax:
                    boundaryCode = ClosureLiftCodes.UnsupportedBoundary;
                    boundaryNode = current;
                    return null;
            }
        }

        return null;
    }

    private static string BoundaryMessage(string code) => code switch
    {
        ClosureLiftCodes.UnsupportedBoundary =>
            "Lambda crosses a residual local-function boundary or lives in a field/property initializer; unsupported in v1 (D14/D16).",
        _ => "Unsupported lambda boundary.",
    };

    private static SyntaxNode? GetBodyRoot(SyntaxNode member) => member switch
    {
        BaseMethodDeclarationSyntax bm => (SyntaxNode?)bm.Body ?? bm.ExpressionBody,
        AccessorDeclarationSyntax accessor => accessor.Body ?? (SyntaxNode?)accessor.ExpressionBody,
        _ => null,
    };

    private string GetRoutineName(SyntaxNode routine, TypeDeclarationSyntax hostType)
    {
        if (routine is AccessorDeclarationSyntax accessor)
        {
            string containerName;
            if (accessor.Ancestors().OfType<IndexerDeclarationSyntax>().FirstOrDefault() != null)
                containerName = "Indexer";
            else
            {
                containerName = accessor.Ancestors().OfType<PropertyDeclarationSyntax>().FirstOrDefault()?.Identifier.Text
                    ?? accessor.Ancestors().OfType<EventDeclarationSyntax>().FirstOrDefault()?.Identifier.Text
                    ?? "Accessor";
            }

            return $"{containerName}_{accessor.Keyword.Text}";
        }

        var declared = routine is BaseMethodDeclarationSyntax bm ? _model.GetDeclaredSymbol(bm) : null;
        return declared?.Name ?? routine switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text,
            ConstructorDeclarationSyntax c => c.Identifier.Text,
            DestructorDeclarationSyntax d => d.Identifier.Text,
            _ => hostType.Identifier.Text,
        };
    }

    private void Emit(
        string code,
        string message,
        Location? location,
        string? memberName = null,
        string? symbolName = null,
        TriggerReason? reason = null)
    {
        var severity = code switch
        {
            ClosureLiftCodes.RefCapture or ClosureLiftCodes.UnsupportedBoundary
                => _options.Strict ? ClosureLiftSeverity.Error : ClosureLiftSeverity.Warning,
            ClosureLiftCodes.AuditRow => ClosureLiftSeverity.Info,
            _ => ClosureLiftSeverity.Error,
        };

        var line = location?.GetLineSpan().StartLinePosition.Line + 1 ?? 0;
        _diagnostics.Add(new ClosureLiftDiagnostic(
            severity, code, message, null, line, memberName, symbolName, reason));
    }

    private sealed record MemberCaptureWork(
        TypeDeclarationSyntax HostType,
        SyntaxNode Routine,
        string RoutineName)
    {
        public List<CapturedSymbolAnalysis> Symbols { get; } = new();
    }
}
