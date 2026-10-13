using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Preprocessing;
using NB = CSharpToJava.Core.NumericBoundary.NumericBoundarySyntaxFacts;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.NumericBoundary.Rules.IncrementTargets;

/// <summary>
/// One accepted site's hoist plan (design §B): a marker-tagged placeholder in
/// the post-Pass1 tree plus the ordered statement sequence (receiver temps →
/// argument/index temps → [old-value temp] → write) that Pass2 splices in at
/// the site's anchor statement.
/// </summary>
internal sealed class IncrementHoistPlan
{
    public required int Sequence { get; init; }

    public required SyntaxAnnotation Marker { get; init; }

    /// <summary>Anchor statement in the ORIGINAL (pre-Pass1) tree.</summary>
    public required StatementSyntax OriginalAnchor { get; init; }

    /// <summary>True when the site is a standalone expression statement.</summary>
    public required bool Standalone { get; init; }

    /// <summary>Start span of the original unary node (multi-site source ordering).</summary>
    public required int SiteSpanStart { get; init; }

    /// <summary>
    /// The site is a postfix inside a WHILE condition: Pass2 restructures the
    /// loop to <c>while (true) { &lt;plans&gt;; if (!(C')) break; body; }</c>.
    /// </summary>
    public required bool WhileCondition { get; init; }

    /// <summary>
    /// The site IS a for-incrementor: Pass2 removes the placeholder incrementor
    /// and appends the write statements at the end of the loop body.
    /// </summary>
    public required bool ForIncrementor { get; init; }

    /// <summary>Hoisted declarations (and, for standalone sites, the write) in execution order.</summary>
    public required List<StatementSyntax> Statements { get; init; }
}

/// <summary>
/// C17 M1 Pass 1 (design §4.3/§B/C):
/// <list type="number">
/// <item>plan over the ORIGINAL tree — illegal targets (CS2J5332), anchor-less
/// sites (CS2J5339) and the decision-14 same-anchor safety gate;</item>
/// <item>bottom-up rewrite: each surviving owned ++/-- becomes a marker-tagged
/// placeholder plus a single-evaluation read-modify-write hoist sequence;</item>
/// </list>
/// Pass 2 (<see cref="IncrementTargetLifter"/>) splices the sequences in.
/// </summary>
internal sealed class IncrementTargetRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly NumericBoundaryOptions _options;
    private readonly IncrementTargetStatistics _stats;
    private readonly List<NumericBoundaryDiagnostic> _diagnostics;
    private readonly IncrementTargetSyntaxFacts.TempNameAllocator _names;

    private readonly Dictionary<ExpressionSyntax, IncrementTargetClassification> _accepted = new();
    private readonly List<IncrementHoistPlan> _plans = new();
    private int _sequence;

    public IncrementTargetRewriter(
        SemanticModel model,
        NumericBoundaryOptions options,
        IncrementTargetStatistics stats,
        List<NumericBoundaryDiagnostic> diagnostics,
        IReadOnlyList<(ExpressionSyntax Node, IncrementTargetClassification Classification)> allSites)
    {
        _model = model;
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
        _names = new IncrementTargetSyntaxFacts.TempNameAllocator(model.SyntaxTree.GetRoot());

        Plan(allSites);
    }

    /// <summary>True when no site error was diagnosed during planning.</summary>
    public bool HasErrorDiagnostic { get; private set; }

    public bool MadeChanges => _plans.Count > 0;

    public IReadOnlyList<IncrementHoistPlan> Plans => _plans;

    // ── planning (original tree; diagnostics + per-anchor safety gate) ──

    private void Plan(IReadOnlyList<(ExpressionSyntax Node, IncrementTargetClassification C)> allSites)
    {
        var candidates = new List<(ExpressionSyntax Node, IncrementTargetClassification C)>();

        foreach (var (node, classification) in allSites)
        {
            switch (classification.Target)
            {
                case TargetKind.Event:
                    _stats.EventSites++;
                    ReportIllegal(node, IncrementTargetDiagnostics.ReasonEvent, "event target");
                    continue;
                case TargetKind.Pointer:
                    ReportIllegal(node, IncrementTargetDiagnostics.ReasonPointer, "pointer-indirection target");
                    continue;
                case TargetKind.Conditional:
                    ReportNoAnchor(node, IncrementTargetDiagnostics.ReasonConditional);
                    continue;
            }

            // M2 owned-but-unsupported cores: one loud Error per site and the
            // whole file rolls back via NB-12 (Changed=false upstream).
            switch (classification.Core)
            {
                case ArithmeticCore.NativeInt:
                    ReportSiteError(node,
                        PreprocessDiagnosticCodes.IncrementTargetNativeInt,
                        IncrementTargetDiagnostics.ReasonNativeInt,
                        IncrementTargetDiagnostics.NativeInt(IncrementTargetSyntaxFacts.SiteText(node)));
                    continue;
                case ArithmeticCore.LiftedNullable:
                    // C22 NL-10/11 handoff (decisions 15-17): when the
                    // nullable-lift family rule is enabled it owns lifted
                    // ++/--/compound sites — simple targets get explicit
                    // HasValue guards, complex targets CS2J5355. C17 leaves
                    // the node untouched (no plan = no rewrite); CS2J5335
                    // only fires when C22 is switched off.
                    if (_options.EnableNullable)
                    {
                        continue;
                    }

                    ReportSiteError(node,
                        PreprocessDiagnosticCodes.IncrementTargetNullableLifted,
                        IncrementTargetDiagnostics.ReasonLifted,
                        IncrementTargetDiagnostics.NullableLifted(IncrementTargetSyntaxFacts.SiteText(node)));
                    continue;
                case ArithmeticCore.Dynamic:
                    ReportSiteError(node,
                        PreprocessDiagnosticCodes.IncrementTargetDynamic,
                        IncrementTargetDiagnostics.ReasonDynamic,
                        IncrementTargetDiagnostics.Dynamic(IncrementTargetSyntaxFacts.SiteText(node)));
                    continue;
                case ArithmeticCore.ExpressionTree:
                    // Defensive only: ++ inside an Expression<TDelegate> lambda
                    // is baseline CS0832, so compilable input never reaches here.
                    ReportSiteError(node,
                        PreprocessDiagnosticCodes.IncrementTargetExpressionTree,
                        IncrementTargetDiagnostics.ReasonExpressionTree,
                        IncrementTargetDiagnostics.ExpressionTree(IncrementTargetSyntaxFacts.SiteText(node)));
                    continue;
                case ArithmeticCore.CustomOp:
                    // Counted on the true-site hit even though the file rolls
                    // back (same convention as EventSites).
                    _stats.CustomOperatorSites++;
                    ReportSiteError(node,
                        PreprocessDiagnosticCodes.IncrementTargetCustomOperator,
                        IncrementTargetDiagnostics.ReasonCustomOperator,
                        IncrementTargetDiagnostics.CustomOperator(IncrementTargetSyntaxFacts.SiteText(node)));
                    continue;
            }

            // IT-9 ref-returning targets (decision 16).
            if (classification.Target == TargetKind.RefReturning)
            {
                if (classification.RefKind == RefKind.RefReadOnly)
                {
                    // ref readonly variable: ++ is baseline CS8331.
                    ReportIllegal(
                        node,
                        IncrementTargetDiagnostics.ReasonRefReadOnly,
                        "ref-returning property/indexer/method returns a readonly reference");
                    continue;
                }

                if (_options.Strict)
                {
                    // The Java half depends on the C01 holder: strict refuses.
                    ReportSiteError(node,
                        PreprocessDiagnosticCodes.IncrementTargetRefReturn,
                        IncrementTargetDiagnostics.ReasonRefReturn,
                        IncrementTargetDiagnostics.RefReturn(IncrementTargetSyntaxFacts.SiteText(node)));
                    continue;
                }
            }

            if (!IncrementTargetAnalyzer.IsOwnedByRule(classification))
            {
                // Direct-map / checked-simple / EnumFromValue-hook sites:
                // untouched and undiagnosed.
                continue;
            }

            if (classification.Lift == Liftability.NoAnchor)
            {
                ReportNoAnchor(node, IncrementTargetDiagnostics.ReasonNoAnchor);
                continue;
            }

            // Accessor checks apply only to real property/indexer symbols.
            // A field reached through an effectful/qualified receiver is also
            // classified Property but carries Member (IFieldSymbol) — fields
            // have no accessors and are always writable when reachable.
            // Ref-returning properties/indexers carry only a get accessor and
            // are written THROUGH the returned ref, so the set check is skipped.
            if (classification.Target != TargetKind.RefReturning
                && classification.Property is { } propertySymbol)
            {
                if (propertySymbol.GetMethod == null)
                {
                    ReportIllegal(node, "no-get-accessor", "property or indexer has no accessible get accessor");
                    continue;
                }

                if (propertySymbol.SetMethod == null)
                {
                    ReportIllegal(node, "read-only", "property or indexer has no accessible set accessor");
                    continue;
                }
            }

            if (classification.Target == TargetKind.RefReturning)
            {
                // Non-strict: the file is still rewritten via a ref local
                // alias; the C01 Java-half dependency stays loud (CS2J5340).
                ReportRefAliasWarning(node);
            }

            // Statement-free prefix lane: `(target = NEW)` splices no
            // statements, so it is exact in any expression position and needs
            // neither an anchor nor the same-anchor ordering gate.
            if (classification.Lift == Liftability.InlinePrefix
                && IncrementTargetAnalyzer.IsStatementFreePrefixShape(classification))
            {
                _accepted[node] = classification;
                continue;
            }

            // For-incrementor lane: the discarded write moves to the body end.
            if (classification.Lift == Liftability.ForIncrementor)
            {
                if (!IsForIncrementorRewritable(node))
                {
                    ReportNoAnchor(node, IncrementTargetDiagnostics.ReasonNoAnchor);
                    continue;
                }

                _accepted[node] = classification;
                continue;
            }

            candidates.Add((node, classification));
        }

        // Group accepted candidates by their (original-tree) anchor statement.
        var groups = new Dictionary<StatementSyntax, List<(ExpressionSyntax Node, IncrementTargetClassification C)>>();
        foreach (var candidate in candidates)
        {
            var anchor = IncrementTargetSyntaxFacts.FindAnchorStatement(candidate.Node)!;
            if (!groups.TryGetValue(anchor, out var list))
            {
                list = new List<(ExpressionSyntax, IncrementTargetClassification)>();
                groups[anchor] = list;
            }

            list.Add(candidate);
        }

        foreach (var (anchor, sites) in groups)
        {
            var safe = IsAnchorGroupRewritable(anchor, sites, out var reason);
            if (!safe)
            {
                foreach (var (node, _) in sites)
                {
                    ReportNoAnchor(node, reason);
                }

                continue;
            }

            foreach (var (node, classification) in sites)
            {
                _accepted[node] = classification;
            }
        }
    }

    /// <summary>
    /// Multi-site materialization gate. Beyond the single-site decision-14
    /// rules, two linearized shapes are accepted:
    /// <list type="bullet">
    /// <item>postfix sites in a WHILE condition (any count): the plans run at
    /// the top of the restructured loop body, before the test, per iteration;</item>
    /// <item>multiple value-consumed postfixes in one anchor: each old value is
    /// read and written once in source order, so the only ordering hazard is a
    /// disjoint side effect that a site textually follows.</item>
    /// </list>
    /// </summary>
    private bool IsAnchorGroupRewritable(
        StatementSyntax anchor,
        List<(ExpressionSyntax Node, IncrementTargetClassification C)> sites,
        out string reason)
    {
        reason = IncrementTargetDiagnostics.ReasonNoAnchor;

        if (anchor is WhileStatementSyntax whileStatement)
        {
            var allConditionPostfixes = sites.All(s =>
                s.C.Lift == Liftability.WhileConditionPostfix
                && whileStatement.Condition.Span.Contains(s.Node.Span));
            return allConditionPostfixes && IsMaterializationOrderSafe(anchor, sites);
        }

        if (sites.Count > 1)
        {
            var allPostfixes = sites.All(s => s.C.Lift == Liftability.LiftablePostfix);
            return allPostfixes && IsMaterializationOrderSafe(anchor, sites);
        }

        return !IsUnsafeSameAnchorGroup(sites, out reason);
    }

    /// <summary>
    /// Exact materialization ordering for multi-postfix/while-condition
    /// groups. The hoisted read/write sequences all run at ONE splice point in
    /// source order; that is exact iff no side-effect expression disjoint from
    /// every owned site subtree is textually FOLLOWED by an owned site (its
    /// write would otherwise move ahead of that effect).
    /// </summary>
    private bool IsMaterializationOrderSafe(
        StatementSyntax anchor,
        List<(ExpressionSyntax Node, IncrementTargetClassification C)> sites)
    {
        foreach (var candidate in anchor.DescendantNodes())
        {
            if (!IsPotentialEffect(candidate))
            {
                continue;
            }

            if (sites.Any(site => site.Node.Span.Contains(candidate.Span)))
            {
                continue;
            }

            if (sites.Any(site => site.Node.SpanStart >= candidate.Span.End))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A for-incrementor site is rewritable when the loop body has no direct
    /// <c>continue</c> (it would skip the write appended at body end) and no
    /// SIBLING incrementor carries a side effect (it stays in the incrementor
    /// list and would interleave wrongly with the moved writes).
    /// </summary>
    private bool IsForIncrementorRewritable(ExpressionSyntax node)
    {
        if (node.Parent is not ForStatementSyntax forStatement)
        {
            return false;
        }

        if (ForBodyContinueScanner.Scan(forStatement.Statement))
        {
            return false;
        }

        foreach (var incrementor in forStatement.Incrementors)
        {
            if (IncrementTargetSyntaxFacts.UnwrapParens(incrementor) == node)
            {
                continue;
            }

            if (incrementor.DescendantNodesAndSelf().Any(IsPotentialEffect))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Decision-14 conservative same-anchor gate:
    /// <list type="bullet">
    /// <item>any potential side-effect expression OUTSIDE the owned site
    /// subtrees (foreign invocation/assignment/++/await/throw/object-creation/
    /// property-getter) — hoisting would reorder it;</item>
    /// <item>more than one value-consumed postfix, or the single postfix not
    /// being the leftmost site (its old value must be read first);</item>
    /// <item>a non-leftmost site whose own hoisted receiver/argument
    /// initializers could observe a side effect.</item>
    /// </list>
    /// </summary>
    private bool IsUnsafeSameAnchorGroup(
        List<(ExpressionSyntax Node, IncrementTargetClassification C)> sites,
        out string reason)
    {
        reason = IncrementTargetDiagnostics.ReasonNoAnchor;
        var anchor = IncrementTargetSyntaxFacts.FindAnchorStatement(sites[0].Node)!;

        // ① Foreign side effects: only a disjoint effect that an owned site
        // TEXTUALLY FOLLOWS is a hazard — its read/write materialization would
        // move ahead of that effect. Effects after the site are harmless (the
        // site's write already precedes them in C# evaluation order, e.g. the
        // direct-map `j++` on the RHS of `dest[p.V++] = src[j++];`); ancestor
        // effects (enclosing invocation/assignment) and effects inside a site
        // subtree execute around/within the site as before (decision 14,
        // directional refinement for the UriHelper copy loops).
        if (!IsMaterializationOrderSafe(anchor, sites))
        {
            return true;
        }

        var ordered = sites.OrderBy(s => s.Node.SpanStart).ToList();
        var leftmostStart = ordered[0].Node.SpanStart;

        // ② postfix lift count/position
        var postfixes = ordered
            .Where(s => s.C.Lift == Liftability.LiftablePostfix)
            .ToList();
        if (postfixes.Count > 1 || (postfixes.Count == 1 && postfixes[0].Node.SpanStart != leftmostStart))
        {
            return true;
        }

        // ③ non-leftmost sites must hoist only pure receiver/argument temps
        foreach (var (node, classification) in ordered.Skip(1))
        {
            foreach (var initializer in HoistInitializerRoots(node, classification))
            {
                foreach (var candidate in initializer.DescendantNodesAndSelf())
                {
                    if (!IsPotentialEffect(candidate))
                    {
                        continue;
                    }

                    // Effects belonging to a NESTED owned site (strictly inside
                    // this site) run as their own hoist sequences and are
                    // mechanically ordered; the site's own effectful temps are
                    // what this rule rejects.
                    var insideNestedSite = sites.Any(s => s.Node != node
                        && s.Node.Span.Start <= candidate.SpanStart
                        && candidate.Span.End <= s.Node.Span.End);
                    if (!insideNestedSite)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Original-syntax expressions that would become hoisted temp initializers
    /// (receiver when it is not this/base/bare-name, every indexer argument,
    /// every array index). Static/implicit members and simple targets hoist
    /// nothing.
    /// </summary>
    private IEnumerable<ExpressionSyntax> HoistInitializerRoots(
        ExpressionSyntax node, IncrementTargetClassification classification)
    {
        var effective = classification.Operand;

        if (effective is MemberAccessExpressionSyntax memberAccess)
        {
            if (!classification.IsStaticMember && NeedsReceiverTemp(memberAccess.Expression))
            {
                yield return memberAccess.Expression;
            }
        }
        else if (effective is ElementAccessExpressionSyntax elementAccess)
        {
            if (NeedsReceiverTemp(elementAccess.Expression))
            {
                yield return elementAccess.Expression;
            }

            foreach (var argument in elementAccess.ArgumentList.Arguments)
            {
                yield return argument.Expression;
            }
        }
    }

    private static bool NeedsReceiverTemp(ExpressionSyntax receiver) =>
        IncrementTargetSyntaxFacts.UnwrapParens(receiver) is not
            (ThisExpressionSyntax or BaseExpressionSyntax or IdentifierNameSyntax);

    private bool IsPotentialEffect(SyntaxNode node)
    {
        switch (node)
        {
            case InvocationExpressionSyntax:
            case ObjectCreationExpressionSyntax:
            case ImplicitObjectCreationExpressionSyntax:
            case AssignmentExpressionSyntax:
            case AwaitExpressionSyntax:
            case ThrowExpressionSyntax:
                return true;
            case PrefixUnaryExpressionSyntax prefix:
                return prefix.Kind() is SyntaxKind.PreIncrementExpression
                    or SyntaxKind.PreDecrementExpression;
            case PostfixUnaryExpressionSyntax postfix:
                return postfix.Kind() is SyntaxKind.PostIncrementExpression
                    or SyntaxKind.PostDecrementExpression;
            case MemberAccessExpressionSyntax memberAccess:
                return _model.GetSymbolInfo(memberAccess).Symbol is IPropertySymbol;
            case ElementAccessExpressionSyntax elementAccess:
                return _model.GetSymbolInfo(elementAccess).Symbol is IPropertySymbol;
            default:
                return false;
        }
    }

    // ── Pass 1 visits ──

    public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
        => HandleUnary(node, (ExpressionSyntax)base.VisitPrefixUnaryExpression(node)!);

    public override SyntaxNode? VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
        => HandleUnary(node, (ExpressionSyntax)base.VisitPostfixUnaryExpression(node)!);

    private SyntaxNode HandleUnary(ExpressionSyntax original, ExpressionSyntax visited)
    {
        if (!_accepted.TryGetValue(original, out var classification))
        {
            return visited;
        }

        var site = ++_sequence;
        var decrement = classification.UnaryKind is SyntaxKind.PreDecrementExpression
            or SyntaxKind.PostDecrementExpression;
        var visitedEffective = GetVisitedEffectiveOperand(visited);
        var operandType = _model.GetTypeInfo(classification.Operand).Type
            ?? _model.GetTypeInfo(classification.Operand).ConvertedType;

        var (descriptor, tempDeclarations) = BuildTarget(
            site, classification, visitedEffective, operandType);
        var statements = new List<StatementSyntax>(tempDeclarations);

        var marker = new SyntaxAnnotation(IncrementTargetSyntaxFacts.HoistAnnotationKind);
        var standalone = classification.Lift == Liftability.Standalone;

        ExpressionSyntax replacement;
        if (classification.Lift is Liftability.LiftablePostfix
            or Liftability.WhileConditionPostfix)
        {
            // var __o = READ; WRITE = CORE(__o); site → __o
            var oldName = _names.Old(site);
            AddTempRole(IncrementTargetDiagnostics.TempRoleOldValue);
            statements.Add(IncrementTargetSyntaxFacts.VarDeclaration(oldName, descriptor.BuildAccess()));

            var core = IncrementTargetExpansion.BuildCore(
                classification, operandType, IdentifierName(oldName), decrement, _stats);
            statements.Add(IncrementTargetSyntaxFacts.WriteStatement(
                AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression, descriptor.BuildAccess(), core)));

            replacement = IdentifierName(oldName);
            _stats.PostfixLiftedSites++;
        }
        else if (classification.Lift == Liftability.ForIncrementor)
        {
            // Discarded incrementor: the single write statement moves to the
            // end of the loop body (Pass2 removes this placeholder incrementor).
            var read = descriptor.BuildAccess();
            var core = IncrementTargetExpansion.BuildCore(
                classification, operandType, read, decrement, _stats);
            statements.Add(IncrementTargetSyntaxFacts.WriteStatement(
                AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression, descriptor.BuildAccess(), core)));

            replacement = IdentifierName(_names.Discard(site));
            if (!classification.IsPrefix)
            {
                _stats.PostfixStandaloneSites++;
            }
        }
        else
        {
            var read = descriptor.BuildAccess();
            var core = IncrementTargetExpansion.BuildCore(
                classification, operandType, read, decrement, _stats);
            var assignment = AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression, descriptor.BuildAccess(), core);

            if (standalone)
            {
                statements.Add(IncrementTargetSyntaxFacts.WriteStatement(assignment));
                replacement = IdentifierName(_names.Discard(site));
                if (!classification.IsPrefix)
                {
                    _stats.PostfixStandaloneSites++;
                }
            }
            else
            {
                // Inline prefix (IT-10): (target = NEW)
                replacement = ParenthesizedExpression(assignment);
                _stats.PrefixInlineSites++;
            }
        }

        CountSite(classification);

        var anchor = IncrementTargetSyntaxFacts.FindAnchorStatement(original)!;
        _plans.Add(new IncrementHoistPlan
        {
            Sequence = site,
            Marker = marker,
            OriginalAnchor = anchor,
            Standalone = standalone,
            SiteSpanStart = original.SpanStart,
            WhileCondition = classification.Lift == Liftability.WhileConditionPostfix,
            ForIncrementor = classification.Lift == Liftability.ForIncrementor,
            Statements = statements,
        });

        return replacement
            .WithTriviaFrom(original)
            .WithAdditionalAnnotations(
                marker,
                new SyntaxAnnotation(IncrementTargetSyntaxFacts.HoistAnnotationKind),
                NB.RewriteAnnotation,
                new SyntaxAnnotation(IncrementTargetSyntaxFacts.RewriteAnnotationKind));
    }

    private static ExpressionSyntax GetVisitedEffectiveOperand(ExpressionSyntax visitedUnary)
    {
        var raw = visitedUnary switch
        {
            PrefixUnaryExpressionSyntax pre => pre.Operand,
            PostfixUnaryExpressionSyntax post => post.Operand,
            _ => throw new InvalidOperationException(),
        };

        return IncrementTargetSyntaxFacts.UnwrapParens(raw);
    }

    /// <summary>
    /// Build the storage descriptor plus receiver/argument/index temp
    /// declarations in source evaluation order.
    /// </summary>
    private (TargetDescriptor Descriptor, List<StatementSyntax> Temps) BuildTarget(
        int site,
        IncrementTargetClassification classification,
        ExpressionSyntax visitedEffective,
        ITypeSymbol? operandType)
    {
        var temps = new List<StatementSyntax>();

        switch (classification.Target)
        {
            case TargetKind.SimpleDirect:
            {
                var descriptor = BuildSimpleDescriptor(visitedEffective);
                return (descriptor, temps);
            }

            case TargetKind.RefReturning:
            {
                return BuildRefReturningTarget(
                    site, classification, visitedEffective, operandType, temps);
            }

            case TargetKind.Property:
            {
                var member = classification.Member!;
                if (visitedEffective is IdentifierNameSyntax)
                {
                    return (new TargetDescriptor.SimpleIdentifier { Name = member.Name }, temps);
                }

                var visitedMember = (MemberAccessExpressionSyntax)visitedEffective;
                ReceiverRef receiver;
                if (classification.IsStaticMember)
                {
                    receiver = new ReceiverRef.StaticType
                    {
                        TypeDisplay = member.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    };
                }
                else
                {
                    receiver = BuildReceiver(site, visitedMember.Expression, temps);
                }

                return (new TargetDescriptor.Member { Name = member.Name, Receiver = receiver }, temps);
            }

            case TargetKind.Indexer:
            {
                var visitedAccess = (ElementAccessExpressionSyntax)visitedEffective;
                var originalAccess = (ElementAccessExpressionSyntax)classification.Operand;
                var receiver = classification.IsStaticMember
                    ? (ReceiverRef)new ReceiverRef.StaticType
                    {
                        TypeDisplay = classification.Property!.ContainingType
                            .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    }
                    : BuildReceiver(site, visitedAccess.Expression, temps);

                var arguments = new List<IndexRef>();
                for (var i = 0; i < originalAccess.ArgumentList.Arguments.Count; i++)
                {
                    var originalArgument = originalAccess.ArgumentList.Arguments[i];
                    var visitedArgument = visitedAccess.ArgumentList.Arguments[i];
                    var name = _names.Argument(site, i);
                    AddTempRole(IncrementTargetDiagnostics.TempRoleIndexArg);
                    temps.Add(IncrementTargetSyntaxFacts.VarDeclaration(
                        name, visitedArgument.Expression));
                    arguments.Add(new IndexRef(
                        name,
                        originalArgument.NameColon?.Name.ToString(),
                        originalArgument.RefKindKeyword.Kind()));
                }

                return (new TargetDescriptor.Indexer
                {
                    Receiver = receiver,
                    Arguments = arguments,
                }, temps);
            }

            case TargetKind.ArrayElementSpecial:
            {
                var visitedAccess = (ElementAccessExpressionSyntax)visitedEffective;
                var originalAccess = (ElementAccessExpressionSyntax)classification.Operand;
                var receiver = BuildReceiver(site, visitedAccess.Expression, temps);

                var indices = new List<IndexRef>();
                for (var d = 0; d < originalAccess.ArgumentList.Arguments.Count; d++)
                {
                    var name = _names.Index(site, d);
                    AddTempRole(IncrementTargetDiagnostics.TempRoleIndexArg);
                    temps.Add(IncrementTargetSyntaxFacts.VarDeclaration(
                        name, visitedAccess.ArgumentList.Arguments[d].Expression));
                    indices.Add(new IndexRef(name, null, SyntaxKind.None));
                }

                return (new TargetDescriptor.ArrayElement
                {
                    Receiver = receiver,
                    Indices = indices,
                }, temps);
            }

            default:
                throw new InvalidOperationException(
                    $"target {classification.Target} is not rewritable by C17 M1");
        }
    }

    /// <summary>
    /// IT-9 (decision 16): hoist receiver/index args/method args in source
    /// order, evaluate the ref-returning READ exactly once into a ref local
    /// alias (<c>ref T __cs2j_inc_f{N} = ref READ;</c>), then let every
    /// subsequent read/write target the alias identifier. Statement order:
    /// receiver/arg temps → alias declaration → (caller appends old temp) →
    /// write.
    /// </summary>
    private (TargetDescriptor Descriptor, List<StatementSyntax> Temps) BuildRefReturningTarget(
        int site,
        IncrementTargetClassification classification,
        ExpressionSyntax visitedEffective,
        ITypeSymbol? operandType,
        List<StatementSyntax> temps)
    {
        ExpressionSyntax read;
        switch (visitedEffective)
        {
            // Implicit ref property: P++ (bound IPropertySymbol, no receiver).
            case IdentifierNameSyntax identifier:
                read = IdentifierName(identifier.Identifier.Text);
                break;

            // Ref property through an explicit/static receiver.
            case MemberAccessExpressionSyntax memberAccess:
            {
                var member = classification.Member!;
                ReceiverRef receiver;
                if (classification.IsStaticMember)
                {
                    receiver = new ReceiverRef.StaticType
                    {
                        TypeDisplay = member.ContainingType
                            .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    };
                }
                else
                {
                    receiver = BuildReceiver(site, memberAccess.Expression, temps);
                }

                read = new TargetDescriptor.Member
                {
                    Name = member.Name,
                    Receiver = receiver,
                }.BuildAccess();
                break;
            }

            // Ref indexer: receiver + index args hoisted exactly like IT-3.
            case ElementAccessExpressionSyntax:
            {
                var (indexerDescriptor, indexerTemps) = BuildTarget(
                    site,
                    classification with { Target = TargetKind.Indexer },
                    visitedEffective,
                    operandType);
                temps.AddRange(indexerTemps);
                read = indexerDescriptor.BuildAccess();
                break;
            }

            // Ref-returning method invocation: h.M(args)++ / M(args)++.
            case InvocationExpressionSyntax invocation:
                read = BuildRefMethodRead(
                    site,
                    classification,
                    invocation,
                    (InvocationExpressionSyntax)classification.Operand,
                    temps);
                break;

            default:
                throw new InvalidOperationException(
                    $"unexpected ref-returning target shape {visitedEffective.Kind()}");
        }

        var aliasName = _names.Ref(site);
        AddTempRole(IncrementTargetDiagnostics.TempRoleRefAlias);

        // ParseTypeName (not ParseName): the display string can be a keyword
        // type (int/decimal/…) or a generic namespace-qualified type.
        var aliasType = operandType != null
            ? ParseTypeName(operandType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
            : ParseTypeName("object");
        temps.Add(IncrementTargetSyntaxFacts.RefLocalDeclaration(aliasName, aliasType, read));

        return (new TargetDescriptor.SimpleIdentifier { Name = aliasName }, temps);
    }

    /// <summary>
    /// Rebuild a ref-returning method invocation once, with the receiver and
    /// by-value arguments replaced by hoisted temporaries. ref/out/in
    /// arguments are forwarded verbatim (a hoisted <c>var</c> temp would break
    /// the by-reference argument contract); such arguments are variables and
    /// cannot observe repeated evaluation.
    /// </summary>
    private ExpressionSyntax BuildRefMethodRead(
        int site,
        IncrementTargetClassification classification,
        InvocationExpressionSyntax visitedInvocation,
        InvocationExpressionSyntax originalInvocation,
        List<StatementSyntax> temps)
    {
        ExpressionSyntax callable;
        switch (visitedInvocation.Expression)
        {
            // Implicit instance/static call: M(…) / M<…>(…) keeps its name.
            case IdentifierNameSyntax or GenericNameSyntax:
                callable = visitedInvocation.Expression;
                break;
            case MemberAccessExpressionSyntax memberAccess:
            {
                ExpressionSyntax receiverExpression;
                if (classification.IsStaticMember)
                {
                    receiverExpression = ParseTypeName(
                        classification.Member!.ContainingType
                            .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                }
                else
                {
                    var receiver = BuildReceiver(site, memberAccess.Expression, temps);
                    receiverExpression = ReceiverExpression(receiver);
                }

                // ma.Name preserves a generic method name (GenericName).
                callable = MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    receiverExpression,
                    memberAccess.Name);
                break;
            }
            default:
                throw new InvalidOperationException(
                    $"unexpected ref-method receiver shape {visitedInvocation.Expression.Kind()}");
        }

        var rebuiltArguments = new List<ArgumentSyntax>();
        for (var i = 0; i < originalInvocation.ArgumentList.Arguments.Count; i++)
        {
            var originalArgument = originalInvocation.ArgumentList.Arguments[i];
            var visitedArgument = visitedInvocation.ArgumentList.Arguments[i];

            if (originalArgument.RefKindKeyword.Kind() != SyntaxKind.None)
            {
                rebuiltArguments.Add(visitedArgument);
                continue;
            }

            var name = _names.Argument(site, i);
            AddTempRole(IncrementTargetDiagnostics.TempRoleIndexArg);
            temps.Add(IncrementTargetSyntaxFacts.VarDeclaration(
                name, visitedArgument.Expression));
            rebuiltArguments.Add(Argument(
                originalArgument.NameColon != null
                    ? NameColon(IdentifierName(originalArgument.NameColon.Name.ToString()))
                    : null,
                Token(SyntaxKind.None),
                IdentifierName(name)));
        }

        return InvocationExpression(
            callable,
            ArgumentList(SeparatedList(rebuiltArguments)));
    }

    private static ExpressionSyntax ReceiverExpression(ReceiverRef receiver) => receiver switch
    {
        ReceiverRef.Temp temp => IdentifierName(temp.Name),
        ReceiverRef.This => ThisExpression(),
        ReceiverRef.Base => BaseExpression(),
        ReceiverRef.Identifier id => IdentifierName(id.Name),
        ReceiverRef.StaticType type => ParseTypeName(type.TypeDisplay),
        _ => throw new InvalidOperationException(),
    };

    private static TargetDescriptor BuildSimpleDescriptor(ExpressionSyntax visitedEffective)
    {
        switch (visitedEffective)
        {
            case IdentifierNameSyntax identifier:
                return new TargetDescriptor.SimpleIdentifier { Name = identifier.Identifier.Text };
            case MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax or BaseExpressionSyntax } member:
                return new TargetDescriptor.SimpleMember
                {
                    Name = member.Name.Identifier.Text,
                    IsThis = member.Expression.IsKind(SyntaxKind.ThisExpression),
                };
            default:
                throw new InvalidOperationException(
                    $"unexpected simple-target shape {visitedEffective.Kind()}");
        }
    }

    private ReceiverRef BuildReceiver(int site, ExpressionSyntax visitedReceiver, List<StatementSyntax> temps)
    {
        var effective = IncrementTargetSyntaxFacts.UnwrapParens(visitedReceiver);
        switch (effective)
        {
            case ThisExpressionSyntax:
                return new ReceiverRef.This();
            case BaseExpressionSyntax:
                return new ReceiverRef.Base();
            case IdentifierNameSyntax identifier:
                return new ReceiverRef.Identifier { Name = identifier.Identifier.Text };
            default:
            {
                var name = _names.Receiver(site);
                AddTempRole(IncrementTargetDiagnostics.TempRoleReceiver);
                temps.Add(IncrementTargetSyntaxFacts.VarDeclaration(name, visitedReceiver));
                return ReceiverRef.TempNamed(name);
            }
        }
    }

    private void CountSite(IncrementTargetClassification classification)
    {
        // Core buckets first: a site can contribute to both a core bucket
        // (DecimalSites) and its target-shape bucket below.
        switch (classification.Core)
        {
            case ArithmeticCore.Decimal:
                _stats.DecimalSites++;
                break;
        }

        if (classification.Target == TargetKind.RefReturning)
        {
            // Ref-alias sites keep their own bucket; the underlying
            // property/indexer/method shape is not double-counted.
            _stats.RefAliasSites++;
            return;
        }

        switch (classification.Target)
        {
            case TargetKind.Property:
                _stats.PropertySites++;
                break;
            case TargetKind.Indexer:
                _stats.IndexerSites++;
                break;
            case TargetKind.ArrayElementSpecial:
                _stats.SpecialArrayElementSites++;
                break;
            case TargetKind.SimpleDirect when classification.Core == ArithmeticCore.EnumCast:
                _stats.EnumCastSites++;
                break;
            case TargetKind.SimpleDirect when classification.Core == ArithmeticCore.UnsignedDomain:
                _stats.UnsignedResidualSites++;
                break;
        }
    }

    private void AddTempRole(string role)
    {
        _stats.TempsByRole[role] = _stats.TempsByRole.TryGetValue(role, out var count)
            ? count + 1
            : 1;
    }

    // ── diagnostics ──

    private void ReportIllegal(ExpressionSyntax site, string reason, string detail)
    {
        HasErrorDiagnostic = true;
        var (line, column) = GetLineColumn(site);
        _diagnostics.Add(new NumericBoundaryDiagnostic(
            NumericBoundarySeverity.Error,
            PreprocessDiagnosticCodes.IncrementTargetIllegalTarget,
            IncrementTargetDiagnostics.IllegalTarget(IncrementTargetSyntaxFacts.SiteText(site), detail),
            line, column));
        BumpDeferred($"{PreprocessDiagnosticCodes.IncrementTargetIllegalTarget}:{reason}");
    }

    private void ReportNoAnchor(ExpressionSyntax site, string reason)
    {
        HasErrorDiagnostic = true;
        var (line, column) = GetLineColumn(site);
        _diagnostics.Add(new NumericBoundaryDiagnostic(
            NumericBoundarySeverity.Error,
            PreprocessDiagnosticCodes.IncrementTargetNoAnchor,
            IncrementTargetDiagnostics.NoAnchor(IncrementTargetSyntaxFacts.SiteText(site)),
            line, column));
        BumpDeferred($"{PreprocessDiagnosticCodes.IncrementTargetNoAnchor}:{reason}");
    }

    /// <summary>
    /// M2 loud-fail core/site codes (CS2J5333/5335/5336/5337/5338 and strict
    /// CS2J5340): one Error per site; NB-12 rolls the whole file back.
    /// </summary>
    private void ReportSiteError(
        ExpressionSyntax site, string code, string reason, string message)
    {
        HasErrorDiagnostic = true;
        var (line, column) = GetLineColumn(site);
        _diagnostics.Add(new NumericBoundaryDiagnostic(
            NumericBoundarySeverity.Error, code, message, line, column));
        BumpDeferred($"{code}:{reason}");
    }

    /// <summary>
    /// Non-strict CS2J5340: the ref-alias rewrite is still emitted, but the
    /// Java half depends on the C01 holder so the dependency stays visible.
    /// Warnings do not bump DeferredByReason (the site is rewritten, not
    /// deferred) and never trigger NB-12.
    /// </summary>
    private void ReportRefAliasWarning(ExpressionSyntax site)
    {
        var (line, column) = GetLineColumn(site);
        _diagnostics.Add(new NumericBoundaryDiagnostic(
            NumericBoundarySeverity.Warning,
            PreprocessDiagnosticCodes.IncrementTargetRefReturn,
            IncrementTargetDiagnostics.RefReturn(IncrementTargetSyntaxFacts.SiteText(site)),
            line, column));
    }

    private void BumpDeferred(string key)
    {
        _stats.DeferredByReason[key] = _stats.DeferredByReason.TryGetValue(key, out var count)
            ? count + 1
            : 1;
    }

    private static (int Line, int Column) GetLineColumn(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        return (span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
    }
}

/// <summary>
/// C17 M1 Pass 2: splice every <see cref="IncrementHoistPlan"/> sequence into
/// its anchor statement — bare scoped block for standalone sites (mirrors the
/// C34 per-site scope), prelude-before-anchor for consumed sites, and an
/// embedded-block wrap for if/while/for/using/lock/labeled/global slots.
/// </summary>
internal static class IncrementTargetLifter
{
    public static CompilationUnitSyntax Apply(
        CompilationUnitSyntax root, IReadOnlyList<IncrementHoistPlan> plans)
    {
        if (plans.Count == 0)
        {
            return root;
        }

        var visibleMarkers = root.GetAnnotatedNodes(IncrementTargetSyntaxFacts.HoistAnnotationKind)
            .ToList();

        var groupsByOriginalAnchor = plans
            .GroupBy(plan => plan.OriginalAnchor)
            .Select(group => group.OrderBy(plan => plan.Sequence).ToList())
            .ToList();

        // Locate each group's anchor in the NEW tree through any visible
        // placeholder (nested plans' markers live inside an outer plan's
        // hoisted statements and are not visible).
        var anchorGroups = new Dictionary<StatementSyntax, List<IncrementHoistPlan>>();
        foreach (var groupPlans in groupsByOriginalAnchor)
        {
            StatementSyntax? newAnchor = null;
            foreach (var plan in groupPlans)
            {
                var placeholder = visibleMarkers.FirstOrDefault(node => node.HasAnnotation(plan.Marker));
                if (placeholder != null)
                {
                    newAnchor = placeholder.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
                    if (newAnchor != null)
                    {
                        break;
                    }
                }
            }

            if (newAnchor == null)
            {
                throw new InvalidOperationException(
                    "C17 hoist marker vanished from the rewritten tree; anchor not found");
            }

            anchorGroups[newAnchor] = groupPlans;
        }

        // ONE bottom-up ReplaceNodes over two disjoint node kinds:
        //   * ordinary anchor STATEMENTS (wrap/while/for paths), and
        //   * the enclosing Block/SwitchSection of LocalDeclarationStatement
        //     "escape" anchors, whose temps become sibling statements right
        //     before the declaration.
        // Replacing anchor statements directly (not their containers) is what
        // keeps nested groups intact when group B's anchor lives inside group
        // A's anchor subtree: ReplaceNodes visits bottom-up, so `updated`
        // already carries every nested group's result (a stale-instance second
        // ReplaceNodes pass silently no-ops and drops the inner splice —
        // observed as a dangling `__cs2j_inc_dN;` placeholder,
        // RegexNode.ReduceConcatenationWithAdjacentLoops, and as vanished
        // index temps, ContentValidator.RangeCursors).
        //
        // A LocalDeclarationStatement anchor declares a name that later
        // statements refer to (e.g. `decimal count = ++arr[i];` followed by
        // `if (count == …)`): wrapping it in a bare block scopes the name out
        // (operands then bind to the '?' error type and C34 reports CS2J5361),
        // so its temps splice into the enclosing statement list instead.
        var wrapAnchors = anchorGroups
            .Where(kvp => !IsDeclarationEscapeGroup(kvp.Key, kvp.Value))
            .Select(kvp => kvp.Key)
            .ToList();

        var escapeAnchors = anchorGroups
            .Where(kvp => IsDeclarationEscapeGroup(kvp.Key, kvp.Value))
            .ToList();

        // container (old-tree instance) -> escape declarations in source order
        var escapeByContainer =
            new Dictionary<SyntaxNode, List<(StatementSyntax Decl, List<IncrementHoistPlan> Plans)>>();

        var replacementNodes = new List<SyntaxNode>(wrapAnchors);
        foreach (var (decl, groupPlans) in escapeAnchors)
        {
            var container = decl.Parent;
            if (container is not BlockSyntax and not SwitchSectionSyntax)
            {
                throw new InvalidOperationException(
                    "C17 declaration-escape anchor sits outside a block or switch section statement list; anchor not found");
            }

            if (!escapeByContainer.TryGetValue(container, out var escapes))
            {
                escapes = new List<(StatementSyntax, List<IncrementHoistPlan>)>();
                escapeByContainer[container] = escapes;
                replacementNodes.Add(container);
            }

            escapes.Add((decl, groupPlans));
        }

        foreach (var escapes in escapeByContainer.Values)
        {
            escapes.Sort((a, b) => a.Decl.SpanStart.CompareTo(b.Decl.SpanStart));
        }

        if (replacementNodes.Count == 0)
        {
            return root;
        }

        return (CompilationUnitSyntax)root.ReplaceNodes(replacementNodes, (original, updated) =>
        {
            if (escapeByContainer.TryGetValue(original, out var escapes))
            {
                return SpliceDeclarationsBefore(updated, original, escapes);
            }

            var groupPlans = anchorGroups[(StatementSyntax)original];
            var anchor = (StatementSyntax)updated;

            if (groupPlans[0].WhileCondition)
            {
                return TransformWhile((WhileStatementSyntax)anchor, groupPlans);
            }

            if (groupPlans[0].ForIncrementor)
            {
                return TransformFor((ForStatementSyntax)anchor, groupPlans);
            }

            // Standalone sites consume the whole statement; all other groups
            // keep the anchor after the spliced plan statements, scoped in a
            // bare block (temps are unique, so the extra scope is neutral).
            var includeAnchor = !groupPlans.All(plan => plan.Standalone);
            return WrapInBlock(anchor, groupPlans, includeAnchor);
        });
    }

    /// <summary>
    /// True when the anchor is a local declaration that stays in the statement
    /// list after splicing (its declared names are visible to later statements,
    /// so the temps must be siblings of the declaration instead of wrapping it
    /// in a nested block.
    /// </summary>
    private static bool IsDeclarationEscapeGroup(
        StatementSyntax anchor, IReadOnlyList<IncrementHoistPlan> groupPlans)
    {
        if (anchor is not LocalDeclarationStatementSyntax)
        {
            return false;
        }

        // All-standalone groups replace the whole statement; the declaration
        // never survives, so the regular block path applies.
        return !groupPlans.All(plan => plan.Standalone);
    }

    /// <summary>
    /// Container callback for the single bottom-up ReplaceNodes: splice each
    /// escape declaration group's temps into the (updated) block or switch
    /// section's statement list, immediately before the declaration.
    /// <para>
    /// The declaration is located POSITIONALLY against the ORIGINAL container —
    /// nested replacements (a sibling anchor wrap, a descendant container
    /// splice) rebuild statement instances but never add/remove/reorder slots
    /// at this level, so old and updated lists zip one-to-one by index even
    /// though the declaration instance itself is new.
    /// </para>
    /// </summary>
    private static SyntaxNode SpliceDeclarationsBefore(
        SyntaxNode updated,
        SyntaxNode original,
        IReadOnlyList<(StatementSyntax Decl, List<IncrementHoistPlan> Plans)> escapes)
    {
        var oldStatements = original switch
        {
            BlockSyntax block => block.Statements,
            SwitchSectionSyntax section => section.Statements,
            _ => throw new InvalidOperationException(
                "C17 declaration-escape container is neither a block nor a switch section"),
        };
        var newStatements = updated switch
        {
            BlockSyntax block => block.Statements,
            SwitchSectionSyntax section => section.Statements,
            _ => throw new InvalidOperationException(
                "C17 declaration-escape container is neither a block nor a switch section"),
        };

        if (oldStatements.Count != newStatements.Count)
        {
            throw new InvalidOperationException(
                "C17 declaration-escape splice lost positional alignment " +
                $"({oldStatements.Count} old vs {newStatements.Count} updated statements)");
        }

        var plansByDecl = escapes.ToDictionary(item => item.Decl, item => item.Plans);
        var result = new List<StatementSyntax>();

        for (var i = 0; i < oldStatements.Count; i++)
        {
            var oldStatement = oldStatements[i];
            var newStatement = newStatements[i];

            if (!plansByDecl.TryGetValue(oldStatement, out var groupPlans))
            {
                result.Add(newStatement);
                continue;
            }

            var leading = newStatement.GetLeadingTrivia();
            var emitted = 0;
            foreach (var plan in groupPlans)
            {
                foreach (var hoisted in plan.Statements)
                {
                    var annotated = hoisted.WithAdditionalAnnotations(
                        NB.RewriteAnnotation,
                        new SyntaxAnnotation(IncrementTargetSyntaxFacts.RewriteAnnotationKind));
                    result.Add(emitted == 0
                        ? annotated.WithLeadingTrivia(leading)
                        : annotated);
                    emitted++;
                }
            }

            result.Add(emitted == 0
                ? newStatement
                : newStatement.WithoutLeadingTrivia());
        }

        return updated switch
        {
            BlockSyntax block => block.WithStatements(SyntaxFactory.List(result)),
            SwitchSectionSyntax section => section.WithStatements(SyntaxFactory.List(result)),
            _ => updated,
        };
    }

    private static BlockSyntax WrapInBlock(
        StatementSyntax anchor,
        IReadOnlyList<IncrementHoistPlan> groupPlans,
        bool includeAnchor)
    {
        var sequence = new List<StatementSyntax>();
        foreach (var plan in groupPlans)
        {
            sequence.AddRange(plan.Statements);
        }

        if (includeAnchor)
        {
            sequence.Add(anchor.WithoutLeadingTrivia());
        }

        return Block(sequence)
            .WithLeadingTrivia(anchor.GetLeadingTrivia())
            .WithAdditionalAnnotations(
                NB.RewriteAnnotation,
                new SyntaxAnnotation(IncrementTargetSyntaxFacts.RewriteAnnotationKind));
    }

    /// <summary>
    /// While-condition linearization:
    /// <c>while (C) B</c> → <c>while (true) { plans; if (!(C')) break; B }</c>
    /// where each plan (receiver/index temps → old read → write) runs once per
    /// condition evaluation in source order. A <c>continue</c> in B naturally
    /// re-enters at the plans and the retest; <c>break</c> leaves the loop
    /// without a retest, as in the original.
    /// </summary>
    private static WhileStatementSyntax TransformWhile(
        WhileStatementSyntax whileStatement,
        IReadOnlyList<IncrementHoistPlan> groupPlans)
    {
        var leading = whileStatement.GetLeadingTrivia();
        var sequence = new List<StatementSyntax>();
        var emittedAny = false;
        foreach (var plan in groupPlans)
        {
            foreach (var hoisted in plan.Statements)
            {
                sequence.Add(emittedAny
                    ? hoisted
                    : hoisted.WithLeadingTrivia(leading));
                emittedAny = true;
            }
        }

        var retest = IfStatement(
            PrefixUnaryExpression(
                SyntaxKind.LogicalNotExpression,
                ParenthesizedExpression(whileStatement.Condition)),
            BreakStatement());
        sequence.Add(retest);
        sequence.Add(whileStatement.Statement);

        var inner = Block(sequence);
        return WhileStatement(
                LiteralExpression(SyntaxKind.TrueLiteralExpression),
                inner)
            .WithLeadingTrivia(emittedAny ? TriviaList(ElasticMarker) : leading)
            .WithAdditionalAnnotations(
                NB.RewriteAnnotation,
                new SyntaxAnnotation(IncrementTargetSyntaxFacts.RewriteAnnotationKind));
    }

    /// <summary>
    /// For-incrementor linearization: every marker-bearing incrementor of the
    /// group is removed and its discarded write statement is appended at the
    /// end of the loop body (the planner refused bodies with a direct
    /// <c>continue</c> and effectful sibling incrementors).
    /// </summary>
    private static ForStatementSyntax TransformFor(
        ForStatementSyntax forStatement,
        IReadOnlyList<IncrementHoistPlan> groupPlans)
    {
        var markers = groupPlans.Select(plan => plan.Marker).ToHashSet();
        var keptIncrementors = forStatement.Incrementors
            .Where(incrementor => !incrementor
                .DescendantNodesAndSelf()
                .Any(node => markers.Any(node.HasAnnotation)))
            .ToList();

        var writes = groupPlans.SelectMany(plan => plan.Statements).ToList();
        var result = forStatement
            .WithIncrementors(SeparatedList(keptIncrementors))
            .WithStatement(AppendToBody(forStatement.Statement, writes));

        return result.WithAdditionalAnnotations(
            NB.RewriteAnnotation,
            new SyntaxAnnotation(IncrementTargetSyntaxFacts.RewriteAnnotationKind));
    }

    private static StatementSyntax AppendToBody(
        StatementSyntax body,
        IReadOnlyList<StatementSyntax> writes)
    {
        var annotations = new SyntaxAnnotation[]
        {
            NB.RewriteAnnotation,
            new(IncrementTargetSyntaxFacts.RewriteAnnotationKind),
        };

        if (body is BlockSyntax block)
        {
            return block
                .WithStatements(List(block.Statements.Concat(writes)))
                .WithAdditionalAnnotations(annotations);
        }

        var sequence = new List<StatementSyntax> { body.WithoutLeadingTrivia() };
        sequence.AddRange(writes);
        return Block(sequence)
            .WithLeadingTrivia(body.GetLeadingTrivia())
            .WithAdditionalAnnotations(annotations);
    }
}
