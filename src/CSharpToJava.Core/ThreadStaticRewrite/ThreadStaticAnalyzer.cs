using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using CSharpToJava.Core.Analysis;

namespace CSharpToJava.Core.ThreadStaticRewrite;

/// <summary>Whole-compilation analysis result: sorted plans and analysis-phase diagnostics.</summary>
internal sealed class ThreadStaticAnalysis
{
    public required IReadOnlyList<ThreadStaticFieldPlan> Plans { get; init; }
    public required IReadOnlyList<ThreadStaticDiagnostic> Diagnostics { get; init; }
    public INamedTypeSymbol? ThreadStaticAttributeSymbol { get; init; }

    public static ThreadStaticAnalysis Empty { get; } = new()
    {
        Plans = Array.Empty<ThreadStaticFieldPlan>(),
        Diagnostics = Array.Empty<ThreadStaticDiagnostic>(),
    };
}

/// <summary>
/// Whole-tree analyzer for the thread-static pass (C09). Field identity is decided
/// solely by <see cref="IFieldSymbol.GetAttributes"/> symbol identity (never string matching);
/// every reference site is resolved back to its <see cref="IFieldSymbol"/> via the semantic model.
/// </summary>
internal sealed class ThreadStaticAnalyzer
{
    private const string ThreadStaticAttributeMetadataName = "System.ThreadStaticAttribute";

    private readonly Compilation _compilation;
    private readonly ThreadStaticOptions _options;
    private readonly List<ThreadStaticDiagnostic> _diagnostics = new();

    public ThreadStaticAnalyzer(Compilation compilation, ThreadStaticOptions options)
    {
        _compilation = compilation;
        _options = options;
    }

    private sealed class Candidate
    {
        public required IFieldSymbol Field { get; init; }
        public required FieldDeclarationSyntax Declaration { get; init; }
        public required VariableDeclaratorSyntax Declarator { get; init; }
        public ThreadStaticFieldKind Kind { get; init; }
        public ThreadStaticInitializerKind InitializerKind { get; init; }
        public bool IsConst { get; init; }
        public bool IsVolatile { get; init; }
        public bool InGenericContainer { get; init; }
        public List<ReferenceSite> Sites { get; } = new();
        /// <summary>(site, code, rule) for every site that makes the field infeasible.</summary>
        public List<(ReferenceSite Site, string Code, ThreadStaticRule Rule)> FailedSites { get; } = new();
    }

    public ThreadStaticAnalysis Analyze()
    {
        var attributeSymbol = _compilation.GetTypeByMetadataName(ThreadStaticAttributeMetadataName);
        if (attributeSymbol == null)
        {
            return ThreadStaticAnalysis.Empty;
        }

        var candidates = new Dictionary<IFieldSymbol, Candidate>(SymbolEqualityComparer.Default);
        CollectCandidates(attributeSymbol, candidates);
        if (candidates.Count == 0)
        {
            return new ThreadStaticAnalysis
            {
                Plans = Array.Empty<ThreadStaticFieldPlan>(),
                Diagnostics = Array.Empty<ThreadStaticDiagnostic>(),
                ThreadStaticAttributeSymbol = attributeSymbol,
            };
        }

        CollectReferenceSites(candidates);

        var plans = new List<ThreadStaticFieldPlan>();
        foreach (var candidate in candidates.Values)
        {
            var plan = BuildPlan(candidate);
            plans.Add(plan);
            EmitPlanDiagnostics(plan, candidate);
        }

        plans.Sort();
        return new ThreadStaticAnalysis
        {
            Plans = plans,
            Diagnostics = _diagnostics
                .OrderBy(d => d.FilePath, StringComparer.Ordinal)
                .ThenBy(d => d.Line ?? int.MaxValue)
                .ToList(),
            ThreadStaticAttributeSymbol = attributeSymbol,
        };
    }

    // ---- phase A: candidate fields ----

    private void CollectCandidates(
        INamedTypeSymbol attributeSymbol,
        Dictionary<IFieldSymbol, Candidate> candidates)
    {
        foreach (var tree in _compilation.SyntaxTrees)
        {
            var model = _compilation.GetSemanticModel(tree);
            foreach (var field in tree.GetRoot().DescendantNodes().OfType<FieldDeclarationSyntax>())
            {
                foreach (var declarator in field.Declaration.Variables)
                {
                    if (model.GetDeclaredSymbol(declarator) is not IFieldSymbol fieldSymbol) continue;
                    if (!HasThreadStaticAttribute(fieldSymbol, attributeSymbol)) continue;

                    var candidate = new Candidate
                    {
                        Field = fieldSymbol,
                        Declaration = field,
                        Declarator = declarator,
                        Kind = fieldSymbol.IsStatic ? ThreadStaticFieldKind.Static : ThreadStaticFieldKind.Instance,
                        InitializerKind = declarator.Initializer != null
                            ? ThreadStaticInitializerKind.Inline
                            : ThreadStaticInitializerKind.None,
                        IsConst = fieldSymbol.IsConst
                            || field.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword)),
                        IsVolatile = field.Modifiers.Any(m => m.IsKind(SyntaxKind.VolatileKeyword)),
                        InGenericContainer = fieldSymbol.ContainingType?.IsGenericType == true,
                    };
                    candidates[fieldSymbol.OriginalDefinition] = candidate;
                }
            }
        }
    }

    private static bool HasThreadStaticAttribute(IFieldSymbol fieldSymbol, ISymbol attributeSymbol)
        => fieldSymbol.GetAttributes()
            .Any(a => a.AttributeClass != null
                && SymbolEqualityComparer.Default.Equals(a.AttributeClass, attributeSymbol));

    // ---- phase B: reference sites ----

    private void CollectReferenceSites(Dictionary<IFieldSymbol, Candidate> candidates)
    {
        foreach (var tree in _compilation.SyntaxTrees)
        {
            var model = _compilation.GetSemanticModel(tree);
            foreach (var identifier in tree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                var field = ResolveField(model, identifier, candidates);
                if (field == null) continue;

                var candidate = field;
                var access = FieldAccessExpression(identifier);
                ClassifySite(model, candidate, access);
            }
        }
    }

    private static Candidate? ResolveField(
        SemanticModel model,
        IdentifierNameSyntax identifier,
        Dictionary<IFieldSymbol, Candidate> candidates)
    {
        // The name segment of a member/qualified access binds on the enclosing access node.
        SyntaxNode bindNode = identifier.Parent switch
        {
            MemberAccessExpressionSyntax member when member.Name == identifier => member,
            QualifiedNameSyntax qualified when qualified.Right == identifier => qualified,
            AliasQualifiedNameSyntax alias when alias.Name == identifier => alias,
            _ => identifier,
        };

        var info = model.GetSymbolInfo(bindNode);
        var symbol = info.Symbol ?? (info.CandidateSymbols.IsDefaultOrEmpty ? null : info.CandidateSymbols[0]);
        if (symbol is not IFieldSymbol fieldSymbol) return null;
        return candidates.TryGetValue(fieldSymbol.OriginalDefinition, out var candidate) ? candidate : null;
    }

    /// <summary>
    /// The field-access expression for a bound name segment: the enclosing member/qualified
    /// access for <c>C.f</c> / <c>global::Ns.C.f</c>, or the identifier itself for bare <c>f</c>.
    /// </summary>
    private static ExpressionSyntax FieldAccessExpression(IdentifierNameSyntax identifier)
    {
        SyntaxNode node = identifier;
        while (node.Parent is MemberAccessExpressionSyntax member && member.Name == node)
        {
            node = member;
        }

        if (node.Parent is QualifiedNameSyntax qualified && qualified.Right == node)
        {
            node = qualified;
        }

        return (ExpressionSyntax)node;
    }

    private void ClassifySite(SemanticModel model, Candidate candidate, ExpressionSyntax access)
    {
        var filePath = access.SyntaxTree.FilePath;

        // nameof(f) / nameof(C.f): the name is a string argument, not a variable access.
        if (IsNameofArgument(access))
        {
            return;
        }

        var (underlyingKind, liftAnchor) = ClassifyLValue(candidate, access);
        var site = new ReferenceSite(access, underlyingKind, filePath, access.Span);
        candidate.Sites.Add(site);

        if (IsInsideExpressionTree(model, access))
        {
            candidate.FailedSites.Add((site, ThreadStaticCodes.ExpressionTreeSite, ThreadStaticRule.ExpressionTree));
        }

        if (IsPassedByReference(access))
        {
            candidate.FailedSites.Add((site, ThreadStaticCodes.InfeasibleSite, ThreadStaticRule.RefOut));
        }

        if (IsDynamicallyBound(model, access))
        {
            candidate.FailedSites.Add((site, ThreadStaticCodes.InfeasibleSite, ThreadStaticRule.Dynamic));
        }

        if (underlyingKind == ThreadStaticSiteKind.StructMutation)
        {
            if (liftAnchor != null)
            {
                site.LiftAnchorSpan = liftAnchor.Span;
            }
            else
            {
                candidate.FailedSites.Add((
                    site,
                    ThreadStaticCodes.StructMutationNotLiftable,
                    ThreadStaticRule.StructMutation));
            }
        }
    }

    private (ThreadStaticSiteKind Kind, ExpressionStatementSyntax? LiftAnchor) ClassifyLValue(
        Candidate candidate, ExpressionSyntax access)
    {
        var lvalueTarget = UnwrapParenthesizedUp(access);

        if (lvalueTarget.Parent is AssignmentExpressionSyntax assignment && assignment.Left == lvalueTarget)
        {
            return (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                ? ThreadStaticSiteKind.Write
                : ThreadStaticSiteKind.Compound, null);
        }

        if (IsIncDecOperand(lvalueTarget))
        {
            return (ThreadStaticSiteKind.Increment, null);
        }

        // Mutable struct member mutation (TS-5): when statement-level temp lifting
        // is feasible the site carries its lift anchor; otherwise CS2J1203 skips the plan.
        if (IsMutableValueType(candidate.Field.Type)
            && TryDetectMutableStructMutation(access, out var liftAnchor))
        {
            return (ThreadStaticSiteKind.StructMutation, liftAnchor);
        }

        return (ThreadStaticSiteKind.Read, null);
    }

    private static bool IsIncDecOperand(SyntaxNode target)
    {
        if (target.Parent is PrefixUnaryExpressionSyntax prefix
            && prefix.Operand == target
            && (prefix.IsKind(SyntaxKind.PreIncrementExpression)
                || prefix.IsKind(SyntaxKind.PreDecrementExpression)))
        {
            return true;
        }

        return target.Parent is PostfixUnaryExpressionSyntax postfix
            && postfix.Operand == target
            && (postfix.IsKind(SyntaxKind.PostIncrementExpression)
                || postfix.IsKind(SyntaxKind.PostDecrementExpression));
    }

    /// <summary>
    /// A struct type is mutable when it is a value type without the readonly modifier
    /// (<see cref="ITypeSymbol.IsReadOnly"/> covers the <c>readonly struct</c> form;
    /// TS-5 leaves its read sites on plain <c>.Value</c> with no write-back).
    /// </summary>
    private static bool IsMutableValueType(ITypeSymbol type)
        => type.IsValueType && !type.IsReadOnly;

    /// <summary>
    /// Walks the receiver chain <c>f.X(.Y...)</c> looking for a mutation-shaped use
    /// (member assignment, ++/--, method receiver). When one exists, climbs outward
    /// to find the statement-level anchor that TS-5 lifts into; returns null anchor
    /// when the mutation sits in a non-liftable context (initializer, attribute
    /// argument, query clause, argument/embedded expression such as <c>var x=f.Mut()</c>).
    /// </summary>
    private static bool TryDetectMutableStructMutation(
        ExpressionSyntax access,
        out ExpressionStatementSyntax? anchor)
    {
        anchor = null;
        SyntaxNode node = UnwrapParenthesizedUp(access);

        SyntaxNode? mutation = null;
        while (true)
        {
            var current = (ExpressionSyntax)UnwrapParenthesizedUp(node);
            var parent = current.Parent;

            if (parent is AssignmentExpressionSyntax assignment && assignment.Left == current)
            {
                mutation = assignment;
                break;
            }

            if (IsIncDecOperand(current))
            {
                mutation = parent!;
                break;
            }

            // Method receiver: cannot prove the method does not mutate, so TS-5
            // temp-lifts and writes back unconditionally.
            if (parent is InvocationExpressionSyntax invocation && invocation.Expression == current)
            {
                mutation = invocation;
                break;
            }

            if (parent is MemberAccessExpressionSyntax nextMember && nextMember.Expression == current)
            {
                node = nextMember;
                continue;
            }

            break;
        }

        if (mutation == null)
        {
            return false;
        }

        anchor = FindLiftAnchor(mutation);
        return true;
    }

    /// <summary>
    /// Climbs from the mutation expression outward. Only receiver-chain positions
    /// (member access / invocation result) and parentheses may be crossed; the
    /// first expression statement encountered is the lift anchor. Anything else
    /// (argument list, initializer, attribute, query, lambda body, ...) is a
    /// non-liftable context.
    /// </summary>
    private static ExpressionStatementSyntax? FindLiftAnchor(SyntaxNode mutation)
    {
        var current = mutation;
        while (true)
        {
            var parent = current.Parent;
            if (parent == null) return null;

            if (parent is ExpressionStatementSyntax expressionStatement
                && expressionStatement.Expression == current)
            {
                return expressionStatement;
            }

            if (parent is ParenthesizedExpressionSyntax parenthesized
                && parenthesized.Expression == current)
            {
                current = parenthesized;
                continue;
            }

            if (parent is MemberAccessExpressionSyntax outerMember
                && outerMember.Expression == current)
            {
                current = outerMember;
                continue;
            }

            if (parent is InvocationExpressionSyntax outerInvocation
                && outerInvocation.Expression == current)
            {
                current = outerInvocation;
                continue;
            }

            return null;
        }
    }

    private static SyntaxNode UnwrapParenthesizedUp(SyntaxNode node)
    {
        while (node.Parent is ParenthesizedExpressionSyntax parenthesized && parenthesized.Expression == node)
        {
            node = parenthesized;
        }

        return node;
    }

    private static bool IsNameofArgument(SyntaxNode access)
    {
        for (var node = access; node != null; node = node.Parent)
        {
            if (node is not ArgumentSyntax argument) continue;
            if (!ContainsNode(argument.Expression, access)) continue;
            if (argument.Parent is ArgumentListSyntax { Parent: InvocationExpressionSyntax invocation }
                && invocation.Expression is IdentifierNameSyntax name
                && name.Identifier.ValueText == "nameof")
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInsideExpressionTree(SemanticModel model, SyntaxNode access)
    {
        foreach (var lambda in access.Ancestors().OfType<LambdaExpressionSyntax>())
        {
            if (ExpressionTreeFacts.IsExpressionTreeLambda(model, lambda))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPassedByReference(SyntaxNode access)
    {
        for (var node = access; node != null; node = node.Parent)
        {
            switch (node)
            {
                case ArgumentSyntax argument when ContainsNode(argument.Expression, access)
                        && argument.RefKindKeyword.RawKind != (int)SyntaxKind.None:
                case PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.AddressOfExpression } address
                        when address.Operand != null && ContainsNode(address.Operand, access):
                    return true;
                case FixedStatementSyntax fixedStatement when fixedStatement.Declaration.Variables
                        .Any(v => v.Initializer != null && ContainsNode(v.Initializer!.Value, access)):
                    return true;
            }
        }

        return false;
    }

    private static bool IsDynamicallyBound(SemanticModel model, SyntaxNode access)
    {
        var typeInfo = model.GetTypeInfo(access);
        return typeInfo.Type?.TypeKind == TypeKind.Dynamic
            || typeInfo.ConvertedType?.TypeKind == TypeKind.Dynamic;
    }

    private static bool ContainsNode(SyntaxNode? container, SyntaxNode node)
        => container != null
            && (container == node || container.DescendantNodes().Contains(node));

    // ---- plan finalization ----

    private ThreadStaticFieldPlan BuildPlan(Candidate candidate)
    {
        var sites = candidate.Sites
            .OrderBy(s => s.FilePath, StringComparer.Ordinal)
            .ThenBy(s => s.SpanStart)
            .ToList();

        var touched = new HashSet<string>(StringComparer.Ordinal) { candidate.Declaration.SyntaxTree.FilePath };
        foreach (var site in sites)
        {
            touched.Add(site.FilePath);
        }

        var plan = new ThreadStaticFieldPlan
        {
            Field = candidate.Field,
            Declaration = candidate.Declaration,
            Declarator = candidate.Declarator,
            Kind = candidate.Kind,
            InitializerKind = candidate.InitializerKind,
            Sites = sites,
            Active = true,
            TouchedFiles = touched,
        };

        // Declaration-level gates first (deterministic, structural).
        if (candidate.IsConst || candidate.IsVolatile)
        {
            var what = candidate.IsConst ? "const" : "volatile";
            Deactivate(plan, ThreadStaticCodes.UnsupportedAttributePosition,
                $"[ThreadStatic] is meaningless on a {what} field ('{candidate.Field.Name}'); the field is not rewritten.");
            return plan;
        }

        if (candidate.Kind == ThreadStaticFieldKind.Instance)
        {
            Deactivate(plan, ThreadStaticCodes.UnsupportedAttributePosition,
                $"[ThreadStatic] on instance field '{candidate.Field.Name}' is ignored by the CLR; the field is not rewritten.");
            return plan;
        }

        if (candidate.InGenericContainer && !_options.RewriteGenericContainingTypes)
        {
            Deactivate(plan, ThreadStaticCodes.GenericContainerUnsupported,
                $"[ThreadStatic] static field '{candidate.Field.Name}' lives in generic container "
                + $"'{candidate.Field.ContainingType?.ToDisplayString()}'; per-closed-type sharing cannot be expressed in v1, the field is not rewritten.");
            return plan;
        }

        // Site-level gates by precedence: ref/dynamic out > expression tree > struct liftability.
        var failed = candidate.FailedSites
            .OrderBy(f => f.Site.FilePath, StringComparer.Ordinal)
            .ThenBy(f => f.Site.SpanStart)
            .ToList();

        if (failed.Count > 0)
        {
            var precedence = new (string Code, int Rank)[]
            {
                (ThreadStaticCodes.InfeasibleSite, 0),
                (ThreadStaticCodes.ExpressionTreeSite, 1),
                (ThreadStaticCodes.StructMutationNotLiftable, 2),
            };

            var ordered = failed
                .OrderBy(f => precedence.FirstOrDefault(p => p.Code == f.Code).Rank)
                .ThenBy(f => f.Site.FilePath, StringComparer.Ordinal)
                .ThenBy(f => f.Site.SpanStart)
                .ToList();
            var first = ordered[0];
            var failCode = first.Code;
            var failReason = failCode switch
            {
                ThreadStaticCodes.InfeasibleSite when first.Rule == ThreadStaticRule.RefOut =>
                    $"[ThreadStatic] field '{candidate.Field.Name}' is passed by reference (ref/out/&/fixed); "
                    + "ThreadLocal<T>.Value is a property and cannot be passed by reference, the field is not rewritten.",
                ThreadStaticCodes.InfeasibleSite =>
                    $"[ThreadStatic] field '{candidate.Field.Name}' is used through dynamic binding at an unrewritable site; the field is not rewritten.",
                ThreadStaticCodes.ExpressionTreeSite =>
                    $"[ThreadStatic] field '{candidate.Field.Name}' is read or written inside an expression-tree lambda; rewriting would change the expression tree shape, the field is not rewritten.",
                ThreadStaticCodes.StructMutationNotLiftable =>
                    $"[ThreadStatic] field '{candidate.Field.Name}' has a mutable value type and a member mutation "
                    + "(member assignment, compound assignment, ++/-- or method receiver) sits in a context that "
                    + "cannot be statement-level temp-lifted (another field's initializer, an attribute argument, "
                    + "a query clause, or an embedded expression such as an argument or 'var x = f.Mut()'); the field is not rewritten.",
                _ => $"[ThreadStatic] field '{candidate.Field.Name}' has an infeasible site; the field is not rewritten.",
            };
            Deactivate(plan, failCode, failReason);
        }

        return plan;
    }

    private static void Deactivate(ThreadStaticFieldPlan plan, string code, string reason)
    {
        plan.Active = false;
        plan.FailCode = code;
        plan.FailReason = reason;
    }

    private void EmitPlanDiagnostics(ThreadStaticFieldPlan plan, Candidate candidate)
    {
        if (!plan.Active)
        {
            // Prefer the declaration as anchor for structural gates; anchor the first offending site otherwise.
            var (filePath, line) = DiagnosticAnchor(plan, candidate);
            var rule = RuleForCode(plan.FailCode!, candidate);
            _diagnostics.Add(new ThreadStaticDiagnostic(
                SeverityForSkip(plan.FailCode!), rule, plan.FailCode!, plan.FailReason!,
                filePath, line, plan.Id));
            return;
        }

        if (!_options.ReportInfo) return;

        var reads = candidate.Sites.Count(s => s.Kind == ThreadStaticSiteKind.Read);
        var writes = candidate.Sites.Count(s => s.Kind == ThreadStaticSiteKind.Write);
        var compounds = candidate.Sites.Count(s => s.Kind == ThreadStaticSiteKind.Compound);
        var increments = candidate.Sites.Count(s => s.Kind == ThreadStaticSiteKind.Increment);
        var structMutations = candidate.Sites.Count(s => s.Kind == ThreadStaticSiteKind.StructMutation);
        var declFile = plan.Declaration.SyntaxTree.FilePath;
        var declLine = plan.Declaration.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        _diagnostics.Add(new ThreadStaticDiagnostic(
            ThreadStaticSeverity.Info, ThreadStaticRule.Declaration, ThreadStaticCodes.FieldRewritten,
            $"[ThreadStatic] field '{plan.Field.ToDisplayString()}' rewritten: declaration plus "
            + $"{reads} read site(s), {writes} write site(s), {compounds} compound site(s), "
            + $"{increments} increment site(s), {structMutations} struct mutation site(s).",
            declFile, declLine, plan.Id));

        if (plan.InitializerKind == ThreadStaticInitializerKind.Inline)
        {
            var (code, message) = _options.InitializerPolicy == ThreadStaticInitializerPolicy.EveryThread
                ? (ThreadStaticCodes.InitializerNormalized,
                    $"Inline initializer of [ThreadStatic] field '{plan.Field.Name}' normalized to a per-thread "
                    + "ThreadLocal<T> first-access factory (EveryThread policy, explicit opt-in); CLR "
                    + "first-thread-only semantics are not preserved.")
                : (ThreadStaticCodes.InitializerNormalized,
                    $"Inline initializer of [ThreadStatic] field '{plan.Field.Name}' moved into the static "
                    + "constructor (FirstThreadFaithful policy, the CLR-faithful default): only the first thread "
                    + "to run the static constructor observes the initial value, every other thread observes "
                    + "default(T).");
            _diagnostics.Add(new ThreadStaticDiagnostic(
                ThreadStaticSeverity.Info, ThreadStaticRule.Declaration, code, message,
                declFile, declLine, plan.Id));
        }
    }

    private (string FilePath, int? Line) DiagnosticAnchor(ThreadStaticFieldPlan plan, Candidate candidate)
    {
        var declarationLevel = plan.FailCode is ThreadStaticCodes.UnsupportedAttributePosition
            or ThreadStaticCodes.GenericContainerUnsupported;
        if (declarationLevel)
        {
            return (plan.Declaration.SyntaxTree.FilePath,
                plan.Declaration.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
        }

        var failure = candidate.FailedSites.FirstOrDefault(f => f.Code == plan.FailCode);
        var anchorSite = failure.Site
            ?? candidate.Sites.FirstOrDefault(s => s.Kind == ThreadStaticSiteKind.StructMutation);
        if (anchorSite != null)
        {
            return (anchorSite.FilePath,
                anchorSite.Node.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
        }

        return (plan.Declaration.SyntaxTree.FilePath,
            plan.Declaration.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
    }

    private ThreadStaticRule RuleForCode(string code, Candidate candidate)
        => code switch
        {
            ThreadStaticCodes.GenericContainerUnsupported => ThreadStaticRule.GenericContainer,
            ThreadStaticCodes.StructMutationNotLiftable => ThreadStaticRule.StructMutation,
            ThreadStaticCodes.ExpressionTreeSite => ThreadStaticRule.ExpressionTree,
            ThreadStaticCodes.InfeasibleSite => candidate.FailedSites.Any(f => f.Rule == ThreadStaticRule.RefOut)
                ? ThreadStaticRule.RefOut
                : ThreadStaticRule.Dynamic,
            _ => candidate.IsConst || candidate.IsVolatile || candidate.Kind == ThreadStaticFieldKind.Static
                ? ThreadStaticRule.Declaration
                : ThreadStaticRule.Instance,
        };

    private ThreadStaticSeverity SeverityForSkip(string code)
    {
        if (code == ThreadStaticCodes.InfeasibleSite)
        {
            return ThreadStaticSeverity.Error;
        }

        if (code == ThreadStaticCodes.ExpressionTreeSite
            && _options.ExpressionTreePolicy == ThreadStaticExpressionTreePolicy.Error)
        {
            return ThreadStaticSeverity.Error;
        }

        return _options.Strict ? ThreadStaticSeverity.Error : ThreadStaticSeverity.Warning;
    }
}
