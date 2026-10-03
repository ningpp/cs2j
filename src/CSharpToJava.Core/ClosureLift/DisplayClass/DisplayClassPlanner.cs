using CSharpToJava.Core.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// One capturing lambda the M2/M3 consolidation plans to migrate (design §9).
/// Model-tree facts only; the stage pairs <see cref="SpanStart"/> to the
/// received tree and builds the method via <see cref="LambdaMethodLifter"/>.
/// </summary>
/// <param name="SpanStart">Absolute model-tree SpanStart of the lambda node.</param>
/// <param name="RawKind">RawKind (defensive pairing).</param>
/// <param name="Group">Home display class (evolved C32 group or new pure/nested group).</param>
/// <param name="IsNewGroup">True when <see cref="Group"/> must be emitted fresh.</param>
/// <param name="Signature">Delegate-derived explicit signature (D12/D23).</param>
/// <param name="IsAsync">D17.</param>
/// <param name="DelegateTypeDisplay">Symbol-rendered target delegate (D8).</param>
/// <param name="FieldAccessByName">Original capture/own-state name → the field
/// access expression relative to the migrated method: a bare field for the
/// home class, `_cs2jParent._cs2jCap_x` (possibly chained) for ancestor
/// classes (D4/D5).</param>
/// <param name="CollapseByInstanceField">Received-tree C32 accesses
/// `__cs2jClosure&lt;n&gt;._cs2jCap_x` → the same relative field expression.</param>
/// <param name="InstanceMemberNames">Host instance-member names referenced by
/// the body; reads rewrite to `&lt;hostThisPath&gt;_cs2jThis.&lt;name&gt;` (D7/D4).</param>
/// <param name="CapturesThis">Explicit `this` in the lambda's own body.</param>
/// <param name="HasHostThis">The chain-top class carries the `_cs2jThis` field.</param>
/// <param name="HostThisPath">`_cs2jParent.` repetitions from the home class
/// to the chain top ("" at chain top).</param>
/// <param name="PromotedParameterNames">Own parameters promoted to home-class
/// fields for nested lambdas (entry-assigned, D4).</param>
/// <param name="PromotedLocalNames">Own-body locals promoted to home-class
/// fields for nested lambdas (declaration site becomes an assignment, D4).</param>
/// <param name="NestedSites">Direct child-group lambda sites substituted while
/// the enclosing method body is built (model-span coordinates).</param>
/// <param name="ChildTypeByInstance">Direct child group instance name →
/// chain-qualified child type display (per-block instantiation).</param>
internal sealed record PendingInstanceLambda(
    int SpanStart,
    int RawKind,
    DisplayClassPlan Group,
    bool IsNewGroup,
    ResolvedLambdaSignature Signature,
    bool IsAsync,
    string DelegateTypeDisplay,
    IReadOnlyDictionary<string, string> FieldAccessByName,
    IReadOnlyDictionary<(string Instance, string Field), string> CollapseByInstanceField,
    IReadOnlyCollection<string> InstanceMemberNames,
    bool CapturesThis,
    bool HasHostThis,
    string HostThisPath,
    IReadOnlySet<string> PromotedParameterNames,
    IReadOnlySet<string> PromotedLocalNames,
    IReadOnlyList<NestedDelegateSite> NestedSites,
    IReadOnlyDictionary<string, string> ChildTypeByInstance);

/// <summary>D4: one nested instance-lambda site replaced inside a migrated parent method.</summary>
internal sealed record NestedDelegateSite(
    int SpanStart,
    int RawKind,
    string InstanceName,
    string MethodName,
    string DelegateTypeDisplay);

/// <summary>
/// Frozen M2/M3 plan for one CompilationUnit (design §4.1 Plan step).
/// </summary>
/// <param name="EvolvedGroups">C32 groups that gain methods/fields/this; the
/// emitted holder classes receive additive member edits only (D2).</param>
/// <param name="NewGroups">Pure read-only/this groups and D4 nested child
/// groups emitted as fresh classes (D3/D4/D5/D7).</param>
/// <param name="Lambdas">Capturing lambdas to migrate, SpanStart order (D9 k sequence).</param>
/// <param name="SnapshotAnchors">Per read-only symbol plan → SpanStart of its earliest
/// capturing lambda site (D14② snapshot placement).</param>
/// <param name="AccountedSkips">Deliberately-kept statement lambdas (documented buckets).</param>
internal sealed record ConsolidationPlan(
    IReadOnlyList<DisplayClassPlan> EvolvedGroups,
    IReadOnlyList<DisplayClassPlan> NewGroups,
    IReadOnlyList<PendingInstanceLambda> Lambdas,
    IReadOnlyDictionary<CapturedSymbolPlan, int> SnapshotAnchors,
    int AccountedSkips);

/// <summary>
/// C45 M2/M3 planner (design §4: DisplayClass/DisplayClassPlanner; D3/D4/D5/D7/D19).
/// Pure (never edits trees). Consumes the C32 bag groups as the single mutable
/// classification (C32 裁决 4: bag membership IS the shared FindWriteSites
/// verdict — no second口径), merges every capturing statement lambda of one
/// (routine, scope block) into one class + one instance even when capture sets
/// are disjoint (D3/§4.2), and freezes: group membership, `Lambda_&lt;k&gt;` names,
/// read-only fields, this capture, body rewrite maps, the D4 nested
/// parent-chain groups and the accounted-residual census. Blocking rows
/// (CS2J4501/4502/4505/4509) freeze the file via the stage.
/// </summary>
internal static class DisplayClassPlanner
{
    private static readonly Func<SyntaxNode, bool> DescendNotIntoLocalFunctions
        = static d => d is not LocalFunctionStatementSyntax;

    /// <summary>
    /// Test-only injection hook (InternalsVisibleTo); production callers always
    /// leave this null. A positive value adds to the §7.3 unaccounted count so
    /// the CS2J4510 fail-loud/rollback path can be exercised despite the
    /// planner's exhaustive planned/pending/accounted partition.
    /// </summary>
    internal static int? ForcedUnaccountedForTests;

    public static ConsolidationPlan? Build(
        CompilationUnitSyntax analysisRoot,
        SemanticModel model,
        ClosureLiftOptions options,
        ClosureLiftNaming naming,
        ClosureLiftStatistics statistics,
        List<ClosureLiftDiagnostic> diagnostics,
        IReadOnlyList<DisplayClassPlan> holderGroups,
        IReadOnlyList<StaticLambdaPlan> staticPlans)
    {
        var quoted = new HashSet<LambdaExpressionSyntax>();
        var statementLambdas = new List<LambdaExpressionSyntax>();
        foreach (var lambda in analysisRoot.DescendantNodes().OfType<LambdaExpressionSyntax>())
        {
            if (ClosureLiftAnnotations.IsAnnotated(lambda))
                continue;
            if (ExpressionTreeFacts.IsExpressionTreeLambda(model, lambda))
            {
                quoted.Add(lambda);
                continue;
            }
            statementLambdas.Add(lambda);
        }

        statistics.ExpressionTreeLambdasSkipped += quoted.Count;

        // Mutable classification: bag membership IS the C32 FindWriteSites verdict.
        // C32 no longer owns lambda-scoped symbols (M3 D4), so bag entries here
        // are routine-scoped mutable cells only.
        var groupBySymbol = new Dictionary<ISymbol, DisplayClassPlan>(SymbolEqualityComparer.Default);
        foreach (var group in holderGroups)
        {
            foreach (var symbol in group.Symbols)
                groupBySymbol.TryAdd(symbol.Symbol, group);
        }

        // Pass 1: classify every statement lambda.
        var facts = new Dictionary<LambdaExpressionSyntax, LambdaFacts>();
        foreach (var lambda in statementLambdas)
        {
            var f = CollectFacts(model, lambda, groupBySymbol, diagnostics);
            if (f == null)
                return null; // CS2J4502/4505/4509: the file stays verbatim.
            facts[lambda] = f;
        }

        var statementSet = statementLambdas.ToHashSet();

        // Pass 2 (D4): nearest enclosing statement lambda + promotion map. An
        // outer lambda's own parameter/local captured by a nested lambda is
        // promoted to a field of the declaring lambda's home group (the value
        // only exists during that invocation). Capture transitivity guarantees
        // every promoted symbol is seen at each chain level (each ancestor's
        // subtree walk includes the use site).
        foreach (var lambda in statementLambdas)
        {
            facts[lambda].OwnerLambda = lambda.Ancestors()
                .OfType<LambdaExpressionSyntax>()
                .FirstOrDefault(statementSet.Contains);
        }

        foreach (var lambda in statementLambdas)
        {
            foreach (var capture in facts[lambda].AllCaptures)
            {
                var declaring = capture.Symbol.DeclaringSyntaxReferences
                    .Select(static r => r.GetSyntax())
                    .FirstOrDefault();
                var declaredOwner = declaring?.Ancestors()
                    .OfType<LambdaExpressionSyntax>()
                    .FirstOrDefault(statementSet.Contains);
                if (declaredOwner != null)
                    facts[declaredOwner].PromotedCaptures.TryAdd(capture.Symbol, capture);
            }
        }

        foreach (var lambda in statementLambdas)
        {
            var f = facts[lambda];
            if (f.PromotedCaptures.Count == 0)
                continue;

            f.IsCapturing = true;
            if (f.PromotedCaptures.Values.Any(info => IsLoopScopedWithin(lambda, info.Symbol)))
                f.PromotionBlocked = true;
        }

        // Pass 3: freeze groups, methods and sites (SpanStart order ⇒ Lambda_<k>).
        var registry = new PureGroupRegistry();
        var groupOfLambda = new Dictionary<LambdaExpressionSyntax, DisplayClassPlan>();
        var isNewByLambda = new Dictionary<LambdaExpressionSyntax, bool>();
        var signatureByLambda = new Dictionary<LambdaExpressionSyntax, ResolvedLambdaSignature>();
        var snapshotAnchors = new Dictionary<CapturedSymbolPlan, int>();
        var accountedSkips = 0;
        var plannedStaticSpans = staticPlans.Select(static p => p.SiteSpanStart).ToHashSet();

        foreach (var lambda in statementLambdas.OrderBy(static l => l.SpanStart))
        {
            var f = facts[lambda];

            if (!f.IsCapturing)
            {
                // Zero-capture lambdas belong to the M1 static path: planned spans
                // were lifted; everything else is an accounted residual (no edit).
                if (!plannedStaticSpans.Contains(lambda.SpanStart))
                    accountedSkips += 1;
                continue;
            }

            if (f.SkipReason != null || f.PromotionBlocked)
            {
                accountedSkips += 1;
                continue;
            }

            var owner = f.OwnerLambda;
            DisplayClassPlan? ownerGroup = null;
            if (owner != null
                && (!groupOfLambda.TryGetValue(owner, out ownerGroup) || ownerGroup == null))
            {
                // Nested lambda whose enclosing lambda stayed residual
                // (signature/host/quoted bucket): cannot plan it alone.
                accountedSkips += 1;
                continue;
            }

            var before = diagnostics.Count;
            var signature = LambdaSignatureResolver.Resolve(model, lambda, options, f.RoutineName, diagnostics);
            if (signature == null)
            {
                if (diagnostics.Count > before)
                    return null; // CS2J4501/4509: file stays verbatim (D12 fail-loud).
                accountedSkips += 1; // ConvertedType null: generic-inference position (D20/E16).
                continue;
            }

            signatureByLambda[lambda] = signature;

            if (!NamedMethodLiftStage.HasUsableParameterNames(lambda, signature))
            {
                accountedSkips += 1; // discard/unnamed/attribute parameters: D12 (feat③).
                continue;
            }

            DisplayClassPlan? group;
            bool isNewGroup;
            if (owner != null)
            {
                // D4: every nested lambda gets its own child group keyed at its
                // site block; inherited cells (routine C32 groups, read-only
                // outer captures, the enclosing lambda's promoted state) are
                // reached through the single `_cs2jParent` chain — never copied.
                group = ResolveChildGroup(
                    f, ownerGroup!, model, naming, holderGroups, registry);
                isNewGroup = true;
            }
            else
            {
                // Multi-group routine captures (cross-block C32 holders) keep
                // their documented narrow bucket until a multi-parent shape lands.
                if (f.MutableGroups.Count > 1)
                {
                    accountedSkips += 1;
                    continue;
                }

                var (resolved, resolvedNew) = ResolveHomeGroup(
                    f, model, naming, holderGroups, registry);
                if (resolved == null)
                {
                    accountedSkips += 1; // array comparator form: no host class (D1).
                    continue;
                }

                group = resolved;
                isNewGroup = resolvedNew;
            }

            // D7: host `this` is always carried by the CHAIN TOP (a child class
            // reaches it through `_cs2jParent`). The top lambda's fact scan
            // covers its whole subtree, so every descendant host-member use is
            // seen here.
            if (owner == null && (f.HasExplicitThis || f.InstanceMemberNames.Count > 0))
                group.CapturesThis = true;

            // D4: own parameters/locals captured by nested lambdas become fields
            // of THIS group (entry-assigned for parameters; declaration-site
            // assigned for locals).
            foreach (var capture in f.PromotedCaptures.Values)
            {
                var reason = capture.Symbol.DeclaringSyntaxReferences
                    .Select(static r => r.GetSyntax())
                    .FirstOrDefault() is ParameterSyntax
                    ? TriggerReason.PromotedLambdaParameter
                    : TriggerReason.LambdaLocalMutable;
                GetOrAddPromotedPlan(group, capture, reason);
            }

            // D5 (chain top only): routine-scope read-only captures become
            // fields of the home class. Nested lambdas route through parents.
            if (owner == null)
            {
                foreach (var capture in f.ReadOnlyCaptures)
                {
                    var plan = GetOrAddReadOnlyPlan(group, capture);
                    if (!snapshotAnchors.TryGetValue(plan, out var anchor) || lambda.SpanStart < anchor)
                        snapshotAnchors[plan] = lambda.SpanStart;
                }
            }

            var methodPlan = new LambdaMethodPlan(
                lambda.SpanStart, lambda.RawKind,
                group.ClassName, group.InstanceName,
                $"Lambda_{group.Methods.Count}",
                lambda.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword),
                signature.DelegateTypeDisplay);
            group.Methods.Add(methodPlan);
            group.Sites.Add(new DelegateSitePlan(
                lambda.SpanStart, lambda.RawKind,
                signature.DelegateTypeDisplay, group.InstanceName,
                methodPlan.MethodName, IsStatic: false));

            if (f.IsThisOnly)
                statistics.ThisOnlyLambdas += 1;

            groupOfLambda[lambda] = group;
            isNewByLambda[lambda] = isNewGroup;
        }

        // Freeze per-lambda rewrite maps from the complete group chain.
        var depthByGroup = new Dictionary<DisplayClassPlan, int>();
        foreach (var group in registry.Evolved.Concat(registry.Fresh))
            depthByGroup[group] = ChainDepth(group);

        var planBySymbol = new Dictionary<ISymbol, (CapturedSymbolPlan Plan, DisplayClassPlan Group)>(
            SymbolEqualityComparer.Default);
        foreach (var group in registry.Evolved.Concat(registry.Fresh))
        {
            foreach (var symbol in group.Symbols)
                planBySymbol[symbol.Symbol] = (symbol, group);
        }

        var pending = new List<PendingInstanceLambda>();
        foreach (var lambda in statementLambdas.OrderBy(static l => l.SpanStart))
        {
            if (!groupOfLambda.TryGetValue(lambda, out var group))
                continue;

            var f = facts[lambda];
            var depth = depthByGroup[group];

            var fieldAccessByName = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var capture in f.AllCaptures)
            {
                if (!planBySymbol.TryGetValue(capture.Symbol, out var routed))
                    return null; // structural inconsistency: fail-loud, no half lift.

                var hops = depth - depthByGroup[routed.Group];
                if (hops < 0)
                    return null;
                fieldAccessByName[capture.Name] = ParentPath(hops) + routed.Plan.FieldName;
            }

            var promotedParameters = new HashSet<string>(StringComparer.Ordinal);
            var promotedLocals = new HashSet<string>(StringComparer.Ordinal);
            foreach (var capture in f.PromotedCaptures.Values)
            {
                if (!group.Symbols.Any(s => SymbolEqualityComparer.Default.Equals(s.Symbol, capture.Symbol)))
                    return null;
                var fieldName = group.Symbols.First(s =>
                        SymbolEqualityComparer.Default.Equals(s.Symbol, capture.Symbol))
                    .FieldName;
                fieldAccessByName[capture.Name] = fieldName;

                var declaring = capture.Symbol.DeclaringSyntaxReferences
                    .Select(static r => r.GetSyntax())
                    .FirstOrDefault();
                if (declaring is ParameterSyntax)
                    promotedParameters.Add(capture.Name);
                else
                    promotedLocals.Add(capture.Name);
            }

            var collapse = new Dictionary<(string, string), string>();
            for (var ancestor = group; ancestor != null; ancestor = ancestor.Parent)
            {
                var ancestorPath = ParentPath(depth - depthByGroup[ancestor]);
                foreach (var symbol in ancestor.Symbols)
                    collapse[(ancestor.InstanceName, symbol.FieldName)] = ancestorPath + symbol.FieldName;
            }

            var chainTop = group;
            while (chainTop.Parent != null)
                chainTop = chainTop.Parent;
            var hostThisPath = ParentPath(depth);

            var nestedSites = new List<NestedDelegateSite>();
            var childTypes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in groupOfLambda)
            {
                if (!ReferenceEquals(pair.Value.Parent, group))
                    continue;

                var child = pair.Value;
                childTypes[child.InstanceName] = child.QualifiedClassName;
                foreach (var site in child.Sites)
                {
                    nestedSites.Add(new NestedDelegateSite(
                        site.SiteSpanStart, site.SiteRawKind,
                        child.InstanceName, site.MethodName, site.DelegateTypeDisplay));
                }
            }

            var resolvedSignature = signatureByLambda[lambda];

            pending.Add(new PendingInstanceLambda(
                lambda.SpanStart, lambda.RawKind, group, isNewByLambda[lambda],
                resolvedSignature, lambda.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword),
                resolvedSignature.DelegateTypeDisplay,
                fieldAccessByName,
                collapse,
                f.InstanceMemberNames.ToArray(),
                f.HasExplicitThis,
                chainTop.CapturesThis,
                hostThisPath,
                promotedParameters,
                promotedLocals,
                nestedSites,
                childTypes));
        }

        // §7.3 residual census: every statement lambda is either planned (static or
        // instance), an accounted documented-bucket skip, or an unexpected residual
        // that trips the CS2J4510 gate (fail-loud instead of silent half-lifting).
        var unaccounted = statementLambdas.Count
                          - plannedStaticSpans.Count
                          - pending.Count
                          - accountedSkips;
        if (ForcedUnaccountedForTests is { } forcedUnaccounted && forcedUnaccounted > 0)
            unaccounted += forcedUnaccounted;
        if (unaccounted > 0)
        {
            diagnostics.Add(new ClosureLiftDiagnostic(
                options.Strict ? ClosureLiftSeverity.Error : ClosureLiftSeverity.Warning,
                ClosureLiftCodes.ResidualStatementGate,
                $"C45 residual gate: {unaccounted} statement lambda(s) could not be accounted for by the lift plan " +
                "(design §7.3); the file stays verbatim.",
                null,
                statementLambdas.Count > 0
                    ? statementLambdas[0].GetLocation().GetLineSpan().StartLinePosition.Line + 1
                    : 0));
            return null;
        }

        statistics.ResidualStatementLambdas += accountedSkips;
        statistics.AccountedResidualLambdas += accountedSkips;

        return new ConsolidationPlan(
            registry.Evolved, registry.Fresh, pending, snapshotAnchors, accountedSkips);
    }

    private static string ParentPath(int hops)
        => hops <= 0 ? string.Empty : string.Concat(Enumerable.Repeat(ClosureLiftNaming.ParentFieldName + ".", hops));

    private static int ChainDepth(DisplayClassPlan group)
    {
        var depth = 0;
        for (var current = group.Parent; current != null; current = current.Parent)
            depth += 1;
        return depth;
    }

    /// <summary>
    /// True when the symbol's declaration lives inside a loop body of the
    /// owner lambda (foreach/for/while/do): a single promoted field cannot
    /// preserve the per-iteration cell identity C# gives such locals.
    /// </summary>
    private static bool IsLoopScopedWithin(LambdaExpressionSyntax owner, ISymbol symbol)
    {
        var declaring = symbol.DeclaringSyntaxReferences
            .Select(static r => r.GetSyntax())
            .FirstOrDefault();
        if (declaring == null)
            return false;
        if (declaring is ForEachStatementSyntax or ForStatementSyntax)
            return true;

        for (var current = declaring.Parent; current != null; current = current.Parent)
        {
            if (ReferenceEquals(current, owner))
                return false;
            if (current is ForStatementSyntax
                or ForEachStatementSyntax
                or WhileStatementSyntax
                or DoStatementSyntax)
            return true;
        }

        return false;
    }

    /// <summary>
    /// D5 field plan for a read-only capture inside one home class. Field names
    /// continue the C32 per-class reservation set (D2); the first lambda that
    /// references the symbol freezes the plan.
    /// </summary>
    private static CapturedSymbolPlan GetOrAddReadOnlyPlan(DisplayClassPlan group, CapturedSymbolInfo capture)
    {
        var existing = group.Symbols.FirstOrDefault(s =>
            s.Reason == TriggerReason.ReadOnlyValue
            && SymbolEqualityComparer.Default.Equals(s.Symbol, capture.Symbol));
        if (existing != null)
            return existing;

        var fieldName = ClosureLiftNaming.GetFieldName(capture.Name, group.UsedFieldNames);
        var declaringNode = capture.Symbol.DeclaringSyntaxReferences
            .Select(static r => r.GetSyntax())
            .First();

        var plan = new CapturedSymbolPlan(
            capture.Symbol,
            capture.Name,
            fieldName,
            fieldName,
            capture.TypeDisplayFullyQualified,
            declaringNode,
            TriggerReason.ReadOnlyValue,
            IsForeachIteration: false,
            ForeachLoop: null,
            ForLoop: null,
            Declarator: declaringNode as VariableDeclaratorSyntax,
            Parameter: declaringNode as ParameterSyntax,
            Sites: Array.Empty<ClosureSiteRef>());
        group.Symbols.Add(plan);
        return plan;
    }

    /// <summary>
    /// D4 field plan for an outer lambda's own parameter/local captured by a
    /// nested lambda. The field is an ordinary public mutable field (parameter
    /// values are entry-assigned; locals at their declaration site).
    /// </summary>
    private static CapturedSymbolPlan GetOrAddPromotedPlan(
        DisplayClassPlan group,
        CapturedSymbolInfo capture,
        TriggerReason reason)
    {
        var existing = group.Symbols.FirstOrDefault(s =>
            (s.Reason == TriggerReason.PromotedLambdaParameter || s.Reason == TriggerReason.LambdaLocalMutable)
            && SymbolEqualityComparer.Default.Equals(s.Symbol, capture.Symbol));
        if (existing != null)
            return existing;

        var fieldName = ClosureLiftNaming.GetFieldName(capture.Name, group.UsedFieldNames);
        var declaringNode = capture.Symbol.DeclaringSyntaxReferences
            .Select(static r => r.GetSyntax())
            .First();

        var plan = new CapturedSymbolPlan(
            capture.Symbol,
            capture.Name,
            fieldName,
            fieldName,
            capture.TypeDisplayFullyQualified,
            declaringNode,
            reason,
            IsForeachIteration: false,
            ForeachLoop: null,
            ForLoop: null,
            Declarator: declaringNode as VariableDeclaratorSyntax,
            Parameter: declaringNode as ParameterSyntax,
            Sites: Array.Empty<ClosureSiteRef>());
        group.Symbols.Add(plan);
        return plan;
    }

    private static (DisplayClassPlan? Group, bool IsNewGroup) ResolveHomeGroup(
        LambdaFacts f,
        SemanticModel model,
        ClosureLiftNaming naming,
        IReadOnlyList<DisplayClassPlan> holderGroups,
        PureGroupRegistry registry)
    {
        // D3①: a lambda with mutable captures joins the C32 group (one class, one
        // instance) — all its mutable symbols live in that single group here.
        if (f.MutableGroups.Count == 1)
        {
            var original = f.MutableGroups[0];
            if (original.ArrayForm)
                return (null, false); // array comparator mode: no migration.

            return (registry.GetOrAddEvolved(original), false);
        }

        // D3②: pure read-only/this lambdas merge per (routine, scope block) — a
        // same-scope C32 group absorbs them (§4.2 mixed shape), sibling scopes
        // get their own class+instance (D14② inserts at the first lambda site).
        var block = f.ScopeBlock!;
        var key = (f.Routine!, (SyntaxNode)block);
        if (registry.ByKey.TryGetValue(key, out var known))
            return (known.Group, known.IsNew);

        foreach (var holder in holderGroups)
        {
            if (holder.ArrayForm
                || !ReferenceEquals(holder.HostMember, f.Routine)
                || !ReferenceEquals(HolderScopeBlock(holder), block))
            {
                continue;
            }

            var absorbed = registry.GetOrAddEvolved(holder);
            registry.ByKey[key] = (absorbed, false);
            return (absorbed, false);
        }

        var group = CreateFreshGroup(f, model, naming, holderGroups, registry);
        registry.ByKey[key] = (group, true);
        return (group, true);
    }

    /// <summary>D4: the fresh child display group of one nested-lambda block.</summary>
    private static DisplayClassPlan ResolveChildGroup(
        LambdaFacts f,
        DisplayClassPlan parent,
        SemanticModel model,
        ClosureLiftNaming naming,
        IReadOnlyList<DisplayClassPlan> holderGroups,
        PureGroupRegistry registry)
    {
        // Sibling lambdas in the same block of the same parent lambda share one
        // class + one instance (D3). Expression-bodied owner lambdas use the
        // owner lambda node itself as the scope anchor.
        var anchor = (SyntaxNode?)f.ScopeBlock ?? f.OwnerLambda!;
        var key = (f.Routine!, anchor, parent);
        if (registry.ChildByKey.TryGetValue(key, out var existing))
            return existing;

        var group = CreateFreshGroup(f, model, naming, holderGroups, registry);
        group.Parent = parent;
        group.ParentFieldName = ClosureLiftNaming.ParentFieldName;
        group.ScopeAnchor = anchor;
        registry.ChildByKey[key] = group;
        return group;
    }

    /// <summary>Reserves a fresh group (class + instance names) without keying it.</summary>
    private static DisplayClassPlan CreateFreshGroup(
        LambdaFacts f,
        SemanticModel model,
        ClosureLiftNaming naming,
        IReadOnlyList<DisplayClassPlan> holderGroups,
        PureGroupRegistry registry)
    {
        var nextIndex = NextInstanceIndex(
            holderGroups, f.Routine!,
            registry.Evolved.Concat(registry.Fresh));
        var routineNames = HolderPlanner.CollectRoutineLocalNames(f.Routine!);
        foreach (var holder in holderGroups.Where(h => ReferenceEquals(h.HostMember, f.Routine!)))
            routineNames.Add(holder.InstanceName);
        foreach (var created in registry.Evolved.Concat(registry.Fresh)
                     .Where(g => ReferenceEquals(g.HostMember, f.Routine!)))
        {
            routineNames.Add(created.InstanceName);
        }

        var methodTypeParameters = HolderPlanner.CollectMethodTypeParameters(f.Routine!);
        var group = new DisplayClassPlan
        {
            HostType = f.HostType!,
            HostMember = f.Routine!,
            HostTypeKey = HolderPlanner.HostTypeKey(model, f.HostType!),
            ClassName = naming.ReserveClassName(HolderPlanner.HostTypeKey(model, f.HostType!), f.RoutineName),
            InstanceName = ClosureLiftNaming.ReserveInstanceName(nextIndex, routineNames),
            PerIteration = false,
            ArrayForm = false,
            MethodTypeParameters = methodTypeParameters,
            ClassTypeParameters = CollectClassTypeParameters(f.HostType!, methodTypeParameters),
            ScopeAnchor = f.ScopeBlock,
        };
        registry.Fresh.Add(group);
        return group;
    }

    /// <summary>
    /// Per-file home-group registry: evolved copies of C32 groups and fresh pure
    /// groups stay in DISJOINT lists (the stage iterates both), while the key
    /// maps unify the merge lookups.
    /// </summary>
    private sealed class PureGroupRegistry
    {
        public Dictionary<(SyntaxNode Routine, SyntaxNode Block), (DisplayClassPlan Group, bool IsNew)> ByKey { get; }
            = new();
        public Dictionary<(SyntaxNode Routine, SyntaxNode Anchor, DisplayClassPlan Parent), DisplayClassPlan> ChildByKey { get; }
            = new();
        public List<DisplayClassPlan> Fresh { get; } = new();

        public DisplayClassPlan GetOrAddEvolved(DisplayClassPlan original)
        {
            if (ByKeyEvolved.TryGetValue(original, out var copy))
                return copy;

            copy = new DisplayClassPlan
            {
                HostType = original.HostType,
                HostMember = original.HostMember,
                HostTypeKey = original.HostTypeKey,
                ClassName = original.ClassName,
                InstanceName = original.InstanceName,
                PerIteration = original.PerIteration,
                ArrayForm = original.ArrayForm,
                PerIterationLoop = original.PerIterationLoop,
                MethodTypeParameters = original.MethodTypeParameters,
                ClassTypeParameters = CollectClassTypeParameters(original.HostType, original.MethodTypeParameters),
            };
            copy.UsedFieldNames.UnionWith(original.Symbols.Select(static s => s.FieldName));
            foreach (var symbol in original.Symbols)
                copy.Symbols.Add(symbol);
            Evolved.Add(copy);
            ByKeyEvolved[original] = copy;
            return copy;
        }

        public Dictionary<DisplayClassPlan, DisplayClassPlan> ByKeyEvolved { get; } = new();
        public List<DisplayClassPlan> Evolved { get; } = new();
    }

    /// <summary>
    /// The scope block whose top hosts a C32 group's instance declaration
    /// (mirrors the M1 analyzer mapping); null when it cannot be derived.
    /// Pure-lambda groups only join holders whose instance block matches theirs.
    /// </summary>
    private static BlockSyntax? HolderScopeBlock(DisplayClassPlan holder)
    {
        if (holder.PerIteration && holder.PerIterationLoop?.Statement is BlockSyntax loopBody)
            return loopBody;

        var parameter = holder.Symbols.FirstOrDefault(static s => s.Parameter != null);
        if (parameter != null)
        {
            return (holder.HostMember as BaseMethodDeclarationSyntax)?.Body
                   ?? (holder.HostMember as AccessorDeclarationSyntax)?.Body;
        }

        foreach (var symbol in holder.Symbols)
        {
            if (symbol.DeclaringNode.Ancestors().OfType<BlockSyntax>().FirstOrDefault() is { } block)
                return block;
        }

        return null;
    }

    private static int NextInstanceIndex(
        IReadOnlyList<DisplayClassPlan> holderGroups,
        SyntaxNode routine,
        IEnumerable<DisplayClassPlan> created)
    {
        var max = -1;
        foreach (var group in holderGroups.Concat(created))
        {
            if (!ReferenceEquals(group.HostMember, routine))
                continue;
            var name = group.InstanceName;
            if (!name.StartsWith("__cs2jClosure", StringComparison.Ordinal))
                continue;
            var digits = name["__cs2jClosure".Length..].TakeWhile(char.IsDigit).ToArray();
            if (digits.Length > 0 && int.TryParse(new string(digits), out var index))
                max = Math.Max(max, index);
        }

        return max + 1;
    }

    private static IReadOnlyList<string> CollectClassTypeParameters(
        TypeDeclarationSyntax hostType,
        IReadOnlyList<string> methodTypeParameters)
    {
        var names = new List<string>();
        if (hostType.TypeParameterList != null)
        {
            names.AddRange(hostType.TypeParameterList.Parameters
                .Select(static p => p.Identifier.Text)
                .Where(static t => t.Length > 0));
        }

        names.AddRange(methodTypeParameters);
        return names;
    }

    private static SyntaxNode? GetBodyRoot(LambdaExpressionSyntax lambda) => lambda switch
    {
        ParenthesizedLambdaExpressionSyntax parenthesized
            => (SyntaxNode?)parenthesized.Block ?? parenthesized.ExpressionBody,
        SimpleLambdaExpressionSyntax simple
            => (SyntaxNode?)simple.Block ?? simple.ExpressionBody,
        _ => null,
    };

    private static SyntaxNode? ResolveRoutine(LambdaExpressionSyntax lambda)
    {
        for (var current = lambda.Parent; current != null; current = current.Parent)
        {
            switch (current)
            {
                case AccessorDeclarationSyntax:
                case BaseMethodDeclarationSyntax:
                    return current;
                case LocalFunctionStatementSyntax:
                case PropertyDeclarationSyntax:
                case IndexerDeclarationSyntax:
                case BaseFieldDeclarationSyntax:
                    return null;
            }
        }

        return null;
    }

    /// <summary>The lambda's nearest enclosing statement-list block (D3② group key input).</summary>
    private static BlockSyntax? NearestBlock(LambdaExpressionSyntax lambda)
        => lambda.Ancestors().OfType<BlockSyntax>().FirstOrDefault();

    private sealed class LambdaFacts
    {
        public LambdaFacts(
            bool isCapturing,
            string? skipReason,
            SyntaxNode? routine,
            string routineName,
            TypeDeclarationSyntax? hostType,
            BlockSyntax? scopeBlock,
            List<DisplayClassPlan> mutableGroups,
            List<CapturedSymbolInfo> allCaptures,
            List<CapturedSymbolInfo> readOnlyCaptures,
            HashSet<string> instanceMemberNames,
            bool hasExplicitThis)
        {
            IsCapturing = isCapturing;
            SkipReason = skipReason;
            Routine = routine;
            RoutineName = routineName;
            HostType = hostType;
            ScopeBlock = scopeBlock;
            MutableGroups = mutableGroups;
            AllCaptures = allCaptures;
            ReadOnlyCaptures = readOnlyCaptures;
            InstanceMemberNames = instanceMemberNames;
            HasExplicitThis = hasExplicitThis;
        }

        public bool IsCapturing { get; set; }
        public bool IsThisOnly => ReadOnlyCaptures.Count == 0
                                  && MutableGroups.Count == 0
                                  && PromotedCaptures.Count == 0
                                  && (InstanceMemberNames.Count > 0 || HasExplicitThis);
        public string? SkipReason { get; }
        public SyntaxNode? Routine { get; }
        public string RoutineName { get; }
        public TypeDeclarationSyntax? HostType { get; }
        public BlockSyntax? ScopeBlock { get; }
        public List<DisplayClassPlan> MutableGroups { get; }
        public List<CapturedSymbolInfo> AllCaptures { get; }
        public List<CapturedSymbolInfo> ReadOnlyCaptures { get; }
        public HashSet<string> InstanceMemberNames { get; }
        public bool HasExplicitThis { get; }

        /// <summary>D4: nearest enclosing statement lambda (pass 2).</summary>
        public LambdaExpressionSyntax? OwnerLambda { get; set; }

        /// <summary>D4: own parameters/locals captured by nested lambdas (pass 2).</summary>
        public Dictionary<ISymbol, CapturedSymbolInfo> PromotedCaptures { get; }
            = new(SymbolEqualityComparer.Default);

        /// <summary>D4: promotion blocked by per-iteration loop scoping (documented bucket).</summary>
        public bool PromotionBlocked { get; set; }

        public static LambdaFacts Skipped(
            string reason,
            SyntaxNode? routine,
            string routineName,
            TypeDeclarationSyntax? hostType,
            BlockSyntax? scopeBlock)
            => new(false, reason, routine, routineName, hostType, scopeBlock,
                new List<DisplayClassPlan>(),
                new List<CapturedSymbolInfo>(),
                new List<CapturedSymbolInfo>(),
                new HashSet<string>(StringComparer.Ordinal),
                hasExplicitThis: false);
    }

    /// <summary>
    /// Collects the model facts of one statement lambda. Returns null only when a
    /// blocking CS2J45xx row was emitted (the whole file stays verbatim).
    /// </summary>
    private static LambdaFacts? CollectFacts(
        SemanticModel model,
        LambdaExpressionSyntax lambda,
        Dictionary<ISymbol, DisplayClassPlan> groupBySymbol,
        List<ClosureLiftDiagnostic> diagnostics)
    {
        var routine = ResolveRoutine(lambda);
        var hostType = routine?.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();

        if (routine == null || hostType is not (ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax))
            return LambdaFacts.Skipped("unsupported host", routine, "?", null, null);

        var routineName = LambdaSiteIndex.GetRoutineName(routine, model, hostType);
        var bodyRoot = GetBodyRoot(lambda);
        if (bodyRoot == null)
            return LambdaFacts.Skipped("no body", routine, routineName, hostType, null);

        var lambdaSymbol = model.GetSymbolInfo(lambda).Symbol as IMethodSymbol;
        // The LAMBDA node is the walk root: a bare expression body (`() => x`)
        // would hide its own root identifier from DescendantNodes() (M1 note).
        List<CapturedSymbolInfo> captures = CaptureClassification.CollectCapturedSymbols(
            model, lambda, lambdaSymbol, DescendNotIntoLocalFunctions).ToList();

        // Idempotency (C32 §4.1 mirror): already-lifted instance/array locals are
        // never capture candidates for M2/M3 either.
        captures = captures
            .Where(static c => !c.Name.StartsWith("__cs2jClosure", StringComparison.Ordinal)
                               && !c.Name.StartsWith("__cs2jArr_", StringComparison.Ordinal))
            .ToList();

        // D5 split: bag members are mutable (C32-lifted); the rest are read-only
        // candidates — a supported declarator is required (query range variables
        // and friends stay residual).
        var mutableGroups = new List<DisplayClassPlan>();
        var readOnlyCaptures = new List<CapturedSymbolInfo>();
        foreach (var capture in captures)
        {
            if (groupBySymbol.TryGetValue(capture.Symbol, out var mutableGroup))
            {
                if (!mutableGroups.Contains(mutableGroup))
                    mutableGroups.Add(mutableGroup);
                continue;
            }

            var declaringNode = capture.Symbol.DeclaringSyntaxReferences
                .Select(static r => r.GetSyntax())
                .FirstOrDefault();
            if (declaringNode is not (ParameterSyntax or VariableDeclaratorSyntax or ForEachStatementSyntax))
                return LambdaFacts.Skipped(
                    "unsupported capture declarator", routine, routineName, hostType, NearestBlock(lambda));

            readOnlyCaptures.Add(capture);
        }

        var readOnlyBySymbol = captures.ToDictionary(
            static c => c.Symbol, static c => c, SymbolEqualityComparer.Default);

        var quotedSet = new HashSet<LambdaExpressionSyntax>(lambda
            .DescendantNodes()
            .OfType<LambdaExpressionSyntax>()
            .Where(q => ExpressionTreeFacts.IsExpressionTreeLambda(model, q)));

        var instanceMemberNames = new HashSet<string>(StringComparer.Ordinal);
        var hasExplicitThis = false;
        var quotedExternalReference = false;

        foreach (var node in lambda.DescendantNodes(DescendNotIntoLocalFunctions))
        {
            var insideQuoted = node.Ancestors().OfType<LambdaExpressionSyntax>().Any(quotedSet.Contains);

            if (node is BaseExpressionSyntax)
            {
                if (!insideQuoted)
                {
                    diagnostics.Add(new ClosureLiftDiagnostic(
                        ClosureLiftSeverity.Error,
                        ClosureLiftCodes.UnresolvableDelegateType,
                        $"base access inside a capturing lambda in '{routineName}' cannot be expressed through " +
                        "_cs2jThis; the migrated instance method has no base-call structure (D21/§4.3-②).",
                        null,
                        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        routineName));
                    return null;
                }

                continue;
            }

            if (node is ThisExpressionSyntax)
            {
                if (insideQuoted)
                    quotedExternalReference = true;
                else
                    hasExplicitThis = true;
                continue;
            }

            if (node is not IdentifierNameSyntax identifier)
                continue;
            if (identifier.Parent is NameColonSyntax || CaptureClassification.IsInsideNameOf(identifier))
                continue;
            if (identifier.Parent is MemberAccessExpressionSyntax member && member.Name == identifier)
                continue;
            if (identifier.Parent is MemberBindingExpressionSyntax binding && binding.Name == identifier)
                continue;
            if (identifier.Parent is NameEqualsSyntax)
                continue;
            if (identifier.Parent is AssignmentExpressionSyntax targetAssignment
                && targetAssignment.Left == identifier
                && targetAssignment.Parent is InitializerExpressionSyntax)
            {
                continue;
            }

            var symbol = model.GetSymbolInfo(identifier).Symbol;
            switch (symbol)
            {
                case IMethodSymbol { MethodKind: MethodKind.LocalFunction }:
                    return LambdaFacts.Skipped("local function reference", routine, routineName, hostType,
                        NearestBlock(lambda));

                case IFieldSymbol { IsStatic: false }:
                case IPropertySymbol { IsStatic: false }:
                case IEventSymbol { IsStatic: false }:
                case IMethodSymbol { IsStatic: false, IsExtensionMethod: false, MethodKind: not MethodKind.Constructor }:
                    if (insideQuoted)
                        quotedExternalReference = true;
                    else
                        instanceMemberNames.Add(identifier.Identifier.Text);
                    break;

                default:
                    if (insideQuoted && symbol != null && readOnlyBySymbol.ContainsKey(symbol))
                        quotedExternalReference = true; // the quoted subtree would dangle after the move (D18②).
                    break;
            }
        }

        // D18②: a quoted lambda referencing external state (host instance members
        // or read-only captures) cannot move — inside the display class neither
        // the implicit receiver nor the original local is reachable.
        if (quotedExternalReference)
            return LambdaFacts.Skipped("quoted external reference", routine, routineName, hostType, NearestBlock(lambda));

        var isCapturing = captures.Count > 0 || instanceMemberNames.Count > 0 || hasExplicitThis;

        return new LambdaFacts(
            isCapturing,
            routine is BaseMethodDeclarationSyntax { Body: null } or AccessorDeclarationSyntax { Body: null }
                ? "no statement boundary"
                : null,
            routine,
            routineName,
            hostType,
            NearestBlock(lambda),
            mutableGroups,
            captures,
            readOnlyCaptures,
            instanceMemberNames,
            hasExplicitThis);
    }
}
