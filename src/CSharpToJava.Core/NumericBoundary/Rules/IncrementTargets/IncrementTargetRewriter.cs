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

            if (!IncrementTargetAnalyzer.IsOwnedByRule(classification))
            {
                // M2 categories / ref-returning / direct-map / checked-simple:
                // untouched and undiagnosed by M1.
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
            if (classification.Property is { } propertySymbol)
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

        foreach (var (_, sites) in groups)
        {
            if (IsUnsafeSameAnchorGroup(sites, out var reason))
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
        var siteNodes = sites.Select(s => s.Node).ToList();

        // ① Foreign side effects: potential-effect nodes that are DISJOINT from
        // every owned site. Ancestor effects (the enclosing invocation F(...),
        // an assignment LHS/RHS wrapper, the return statement) execute after
        // the site's own read/write in C# evaluation order and stay in place,
        // so hoisting does not reorder them; effects inside a site subtree are
        // captured by its own hoist sequence. A disjoint sibling effect
        // (F(G(), p.P++), p.P++ + Other(), …) could be observed in a different
        // order once the writes move before the anchor → refuse (decision 14).
        foreach (var candidate in anchor.DescendantNodes())
        {
            if (!IsPotentialEffect(candidate))
            {
                continue;
            }

            var relatedToSite = siteNodes.Any(site =>
                site.Span.Contains(candidate.Span) || candidate.Span.Contains(site.Span));
            if (!relatedToSite)
            {
                return true;
            }
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

        var (descriptor, tempDeclarations) = BuildTarget(site, classification, visitedEffective);
        var statements = new List<StatementSyntax>(tempDeclarations);

        var marker = new SyntaxAnnotation(IncrementTargetSyntaxFacts.HoistAnnotationKind);
        var standalone = classification.Lift == Liftability.Standalone;

        ExpressionSyntax replacement;
        if (classification.Lift == Liftability.LiftablePostfix)
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
        int site, IncrementTargetClassification classification, ExpressionSyntax visitedEffective)
    {
        var temps = new List<StatementSyntax>();

        switch (classification.Target)
        {
            case TargetKind.SimpleDirect:
            {
                var descriptor = BuildSimpleDescriptor(visitedEffective);
                return (descriptor, temps);
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

        var parents = anchorGroups.Keys
            .Select(anchor => anchor.Parent!)
            .Distinct()
            .ToList();

        return (CompilationUnitSyntax)root.ReplaceNodes(parents, (parent, _) =>
        {
            if (parent is BlockSyntax block)
            {
                return block.WithStatements(List(ExpandStatements(block.Statements, anchorGroups)));
            }

            if (parent is SwitchSectionSyntax section)
            {
                return section.WithStatements(List(ExpandStatements(section.Statements, anchorGroups)));
            }

            // Embedded statement slot (if/while/do/for/foreach/using/lock/
            // fixed/else/labeled/global statement): exactly one anchor group.
            var (anchor, groupPlans) = anchorGroups.Single(pair => pair.Key.Parent == parent);
            return parent.ReplaceNode(anchor, WrapInBlock(anchor, groupPlans, includeAnchor: true));
        });
    }

    private static IEnumerable<StatementSyntax> ExpandStatements(
        SyntaxList<StatementSyntax> statements,
        IReadOnlyDictionary<StatementSyntax, List<IncrementHoistPlan>> anchorGroups)
    {
        foreach (var statement in statements)
        {
            if (!anchorGroups.TryGetValue(statement, out var groupPlans))
            {
                yield return statement;
                continue;
            }

            if (groupPlans.All(plan => plan.Standalone))
            {
                // Standalone sites replace the whole statement; the bare block
                // scopes the per-site temps (a group has a single standalone
                // plan, possibly enclosing nested consumed sites).
                var scoped = new List<StatementSyntax>();
                foreach (var plan in groupPlans)
                {
                    scoped.AddRange(plan.Statements);
                }

                yield return Block(scoped)
                    .WithLeadingTrivia(statement.GetLeadingTrivia())
                    .WithAdditionalAnnotations(
                        NB.RewriteAnnotation,
                        new SyntaxAnnotation(IncrementTargetSyntaxFacts.RewriteAnnotationKind));
                continue;
            }

            // Consumed sites: every plan sequence (receiver → args → old-read
            // → write, in source/visit order) precedes the anchor statement.
            var leading = statement.GetLeadingTrivia();
            var emittedAny = false;
            foreach (var plan in groupPlans)
            {
                foreach (var hoisted in plan.Statements)
                {
                    yield return emittedAny
                        ? hoisted
                        : hoisted.WithLeadingTrivia(leading);
                    emittedAny = true;
                }
            }

            yield return statement.WithoutLeadingTrivia()
                .WithLeadingTrivia(ElasticMarker);
        }
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
}
