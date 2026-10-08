using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.ExpressionDesugar;

namespace CSharpToJava.Core.ExpressionDesugar.Initializers;

/// <summary>
/// Bottom-up rewriter for C24 (design §4.1 step 2 / IN-4/5/7/8/11). Object and
/// collection initializer sites in a supported context become a
/// <see cref="ExpressionDesugarTempLifter.PendingTemp"/> statement sequence
/// registered at the shared anchor and the site is replaced by the temp
/// reference. Nested creations and nested member initializers are emitted
/// inline (inner receiver before the outer member assignment; one getter temp
/// per member access chain). Ternary/coalesce owners synthesize an if/else with
/// a definite-assignment result temp, so the initializer side effects stay in
/// the taken branch (S6). Argument positions hoist preceding side-effecting
/// arguments left-to-right. Bare target-typed <c>new()</c> and empty
/// initializers are explicitized in place with no temp (work in any context).
///
/// All semantic decisions come from Analyze-built plans keyed by ORIGINAL
/// nodes; rendering uses already-visited children where the node is still
/// present in the traversal.
/// </summary>
internal sealed class InitializerRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly InitializerStableMarkers _markers;
    private readonly Dictionary<BaseObjectCreationExpressionSyntax, InitializerPlan> _byNode = new();
    private readonly Dictionary<SyntaxNode, InitializerPlan> _ownedSiteByNode = new();

    public InitializerRewriter(
        InitializerAnalysis analysis,
        SemanticModel model,
        InitializerStableMarkers markers)
    {
        _model = model;
        _markers = markers;

        foreach (var plan in analysis.Plans)
        {
            if (plan.Outcome == InitializerOutcome.DeferredForCoexistence)
                continue;

            _byNode[plan.Node] = plan;

            if (plan.IsOwnerHandled && plan.OwnerChain.Count > 0)
                _ownedSiteByNode[plan.Node] = plan;
        }
    }

    public List<ExpressionDesugarTempLifter.PendingTemp> Pending { get; } = new();

    public IReadOnlyDictionary<SyntaxNode, SyntaxAnnotation> AnchorAnnotations
        => _markers.AnchorAnnotations;

    public int RewriteCount { get; private set; }

    /// <summary>
    /// Resolves the plan for a creation node in the CURRENT (possibly
    /// post-C23) tree via its stamped site annotation. Identity lookup on
    /// original nodes misses once an earlier rule refreshes sibling
    /// instances, so the annotation is the source of truth.
    /// </summary>
    private static string? MarkerId(SyntaxNode current, string kind)
        => current.GetAnnotations(kind)
            .Select(a => a.Data)
            .FirstOrDefault(static data => data != null);

    private InitializerPlan? PlanForCurrent(SyntaxNode current)
        => MarkerId(current, InitializerStableMarkers.SiteKind) is { } id
           && _markers.SiteById.TryGetValue(id, out var plan)
            ? plan
            : null;

    private InitializerStableMarkers.OwnerInfo? OwnerForCurrent(SyntaxNode current)
        => MarkerId(current, InitializerStableMarkers.OwnerKind) is { } id
           && _markers.OwnerById.TryGetValue(id, out var info)
            ? info
            : null;

    private bool CurrentNodeHasOwnerAncestor(SyntaxNode current)
    {
        for (var parent = current.Parent; parent != null; parent = parent.Parent)
        {
            if (MarkerId(parent, InitializerStableMarkers.OwnerKind) != null)
                return true;
            if (parent is StatementSyntax or ArrowExpressionClauseSyntax)
                return false;
        }

        return false;
    }

    private Dictionary<int, string>? HoistsForCurrent(SyntaxNode current)
        => MarkerId(current, InitializerStableMarkers.ArgsKind) is { } id
           && _markers.HoistsById.TryGetValue(id, out var map)
            ? map
            : null;

    /// <summary>
    /// Resolves the CURRENT argument list carrying this plan's hoist map.
    /// It is either the creation's own constructor argument list or an
    /// ENCLOSING call's list (preceding side-effect arguments are hoisted
    /// across the enclosing invocation, IN-11).
    /// </summary>
    private ArgumentListSyntax? HoistListForPlan(
        InitializerPlan plan, BaseObjectCreationExpressionSyntax visited)
    {
        if (_markers.ArgsIdByPlan.TryGetValue(plan, out var argsId))
        {
            if (visited.ArgumentList is { } own
                && MarkerId(own, InitializerStableMarkers.ArgsKind) == argsId)
            {
                return own;
            }

            for (var current = visited.Parent; current != null; current = current.Parent)
            {
                if (current is ArgumentListSyntax candidate
                    && MarkerId(candidate, InitializerStableMarkers.ArgsKind) == argsId)
                {
                    return candidate;
                }

                if (current is StatementSyntax or ArrowExpressionClauseSyntax)
                    break;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Creation sites.
    // ------------------------------------------------------------------

    public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        var visited = (ObjectCreationExpressionSyntax)base.VisitObjectCreationExpression(node)!;
        return Apply(visited);
    }

    public override SyntaxNode? VisitImplicitObjectCreationExpression(
        ImplicitObjectCreationExpressionSyntax node)
    {
        var visited = (ImplicitObjectCreationExpressionSyntax)
            base.VisitImplicitObjectCreationExpression(node)!;
        return Apply(visited);
    }

    private SyntaxNode Apply(BaseObjectCreationExpressionSyntax visited)
    {
        var plan = PlanForCurrent(visited);
        if (plan == null)
            return visited;

        if (plan.IsNested)
            return visited; // consumed inline by the enclosing plan

        switch (plan.Outcome)
        {
            case InitializerOutcome.TargetTypedNewExplicit:
            case InitializerOutcome.ZeroTempExplicit:
                RewriteCount++;
                return ObjectInitRewriter.BuildConstruction(plan, visited);

            case InitializerOutcome.ObjectSequence:
            case InitializerOutcome.CollectionSequence:
                if (plan.IsOwnerHandled)
                    return visited; // the conditional/coalesce owner lowers the whole expression
                return LowerSequence(plan, visited);

            default:
                return visited.WithAdditionalAnnotations(
                    ExpressionDesugarSyntaxFacts.ExplainedAnnotation);
        }
    }

    private SyntaxNode LowerSequence(
        InitializerPlan plan, BaseObjectCreationExpressionSyntax visited)
    {
        var statements = new List<StatementSyntax>();

        // Hoist preceding side-effecting arguments left-to-right (argument
        // only). The list may be the creation's own constructor list or an
        // ENCLOSING invocation's list (IN-11); resolve it on the current tree
        // via its stamped marker so post-C23 argument expressions are used.
        var hoistList = HoistListForPlan(plan, visited);
        foreach (var hoist in plan.HoistedArguments)
        {
            if (!hoist.Declare || hoistList == null)
                continue;

            statements.Add(ExpressionDesugarTempLifter.DeclareVar(
                hoist.TempName, hoistList.Arguments[hoist.Index].Expression));
        }

        statements.AddRange(EmitSequence(plan, visited));

        Register(plan.Node, statements, plan.LiftOrder);
        RewriteCount++;
        return SyntaxFactory.IdentifierName(plan.TempName!);
    }

    // ------------------------------------------------------------------
    // Conditional / coalescing owners.
    // ------------------------------------------------------------------

    public override SyntaxNode? VisitConditionalExpression(ConditionalExpressionSyntax node)
    {
        var visited = (ConditionalExpressionSyntax)base.VisitConditionalExpression(node)!;
        return LowerOwner(node, visited);
    }

    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        var visited = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;
        return node.IsKind(SyntaxKind.CoalesceExpression)
            ? LowerOwner(node, visited)
            : visited;
    }

    private SyntaxNode LowerOwner(SyntaxNode current, SyntaxNode visited)
    {
        var info = OwnerForCurrent(current);
        if (info == null || CurrentNodeHasOwnerAncestor(current))
            return visited;

        var statements = new List<StatementSyntax>();
        var resultTemp = EmitOwnerInto(info, visited, statements);
        Register(info.Original, statements, info.Order);
        RewriteCount++;
        return SyntaxFactory.IdentifierName(resultTemp);
    }

    private string EmitOwnerInto(
        InitializerStableMarkers.OwnerInfo info, SyntaxNode visitedOwner,
        List<StatementSyntax> statements)
    {
        var resultTemp = info.TempName;
        var owner = info.Original;

        if (owner is ConditionalExpressionSyntax conditional
            && visitedOwner is ConditionalExpressionSyntax visitedConditional)
        {
            statements.Add(DeclareNoInit(
                ConditionalTypeSyntax(conditional, resultTemp), resultTemp));

            var trueBlock = EmitBranch(
                conditional.WhenTrue, visitedConditional.WhenTrue, resultTemp);
            var falseBlock = EmitBranch(
                conditional.WhenFalse, visitedConditional.WhenFalse, resultTemp);

            statements.Add(SyntaxFactory.IfStatement(
                    visitedConditional.Condition, trueBlock)
                .WithElse(SyntaxFactory.ElseClause(falseBlock)));

            return resultTemp;
        }

        if (owner is BinaryExpressionSyntax coalesce
            && visitedOwner is BinaryExpressionSyntax visitedCoalesce)
        {
            statements.Add(ExpressionDesugarTempLifter.DeclareVar(
                resultTemp, visitedCoalesce.Left));

            var rightBlock = EmitBranch(
                coalesce.Right, visitedCoalesce.Right, resultTemp);

            var isNull = SyntaxFactory.BinaryExpression(
                SyntaxKind.EqualsExpression,
                SyntaxFactory.IdentifierName(resultTemp),
                SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));

            statements.Add(SyntaxFactory.IfStatement(isNull, rightBlock));
            return resultTemp;
        }

        // Defensive: owner shape mismatch; assign the visited value unchanged.
        statements.Add(ExpressionDesugarTempLifter.DeclareVar(
            resultTemp, (ExpressionSyntax)visitedOwner));
        return resultTemp;
    }

    private BlockSyntax EmitBranch(
        ExpressionSyntax branchOriginal, ExpressionSyntax branchVisited, string resultTemp)
    {
        var blockStatements = new List<StatementSyntax>();

        if (branchOriginal is ConditionalExpressionSyntax innerConditional
            && _markers.InfoByOriginal.TryGetValue(innerConditional, out var innerCondInfo)
            && branchVisited is ConditionalExpressionSyntax visitedInner)
        {
            var innerResult = EmitOwnerInto(innerCondInfo, visitedInner, blockStatements);
            blockStatements.Add(Assign(resultTemp, SyntaxFactory.IdentifierName(innerResult)));
        }
        else if (branchOriginal is BinaryExpressionSyntax innerCoalesce
            && innerCoalesce.IsKind(SyntaxKind.CoalesceExpression)
            && _markers.InfoByOriginal.TryGetValue(innerCoalesce, out var innerCoalInfo)
            && branchVisited is BinaryExpressionSyntax visitedInnerCoalesce)
        {
            var innerResult = EmitOwnerInto(innerCoalInfo, visitedInnerCoalesce, blockStatements);
            blockStatements.Add(Assign(resultTemp, SyntaxFactory.IdentifierName(innerResult)));
        }
        else if (_ownedSiteByNode.TryGetValue(branchOriginal, out var plan)
            && branchVisited is BaseObjectCreationExpressionSyntax visitedCreation)
        {
            blockStatements.AddRange(EmitSequence(plan, visitedCreation));
            blockStatements.Add(Assign(
                resultTemp, SyntaxFactory.IdentifierName(plan.TempName!)));
        }
        else
        {
            blockStatements.Add(Assign(resultTemp, branchVisited));
        }

        return SyntaxFactory.Block(SyntaxFactory.List(blockStatements));
    }

    private TypeSyntax ConditionalTypeSyntax(ConditionalExpressionSyntax node, string resultTemp)
    {
        var type = _model.GetTypeInfo(node).Type;
        if (type == null && _byNode.Values.FirstOrDefault(
                p => p.ContextOwner == node) is { } plan)
        {
            type = plan.CreatedType;
        }

        if (type == null)
            type = _byNode.Values.FirstOrDefault(
                p => p.OwnerChain.Count > 0 && p.OwnerChain[^1] == node)?.CreatedType;

        return TargetTypedNewRewriter.BuildTypeSyntax(type);
    }

    private static StatementSyntax Assign(string target, ExpressionSyntax value)
        => SyntaxFactory.ExpressionStatement(
            SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                SyntaxFactory.IdentifierName(target),
                value));

    private static LocalDeclarationStatementSyntax DeclareNoInit(TypeSyntax type, string name)
        => SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                type,
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(name)))));

    // ------------------------------------------------------------------
    // Statement-sequence emission (recursive for nesting).
    // ------------------------------------------------------------------

    private List<StatementSyntax> EmitSequence(
        InitializerPlan plan, BaseObjectCreationExpressionSyntax visited)
    {
        var statements = new List<StatementSyntax>
        {
            ExpressionDesugarTempLifter.DeclareVar(
                plan.TempName!, ObjectInitRewriter.BuildConstruction(plan, visited)),
        };

        if (visited.Initializer != null)
            EmitMembers(plan.TempName!, plan.Members, visited.Initializer, statements);

        return statements;
    }

    private void EmitMembers(
        string receiver,
        IReadOnlyList<InitializerMemberPlan> members,
        InitializerExpressionSyntax visitedInitializer,
        List<StatementSyntax> statements)
    {
        var index = 0;
        foreach (var member in members)
        {
            if (index >= visitedInitializer.Expressions.Count)
                break;

            var element = visitedInitializer.Expressions[index++];
            EmitMember(receiver, member, element, statements);
        }
    }

    private void EmitMember(
        string receiver,
        InitializerMemberPlan member,
        ExpressionSyntax element,
        List<StatementSyntax> statements)
    {
        switch (member)
        {
            case PropertySetPlan property:
                statements.Add(ObjectInitRewriter.BuildMemberAssignment(
                    receiver, property.Property?.Name ?? "_", ValueOf(element)));
                break;

            case FieldSetPlan field:
                statements.Add(ObjectInitRewriter.BuildMemberAssignment(
                    receiver, field.Field?.Name ?? "_", ValueOf(element)));
                break;

            case IndexSetPlan:
                statements.Add(ObjectInitRewriter.BuildIndexAssignment(
                    receiver, LeftOf(element), ValueOf(element)));
                break;

            case NestedCreatePlan nested:
                EmitNestedCreate(receiver, nested, element, statements);
                break;

            case NestedMemberInitPlan nestedMember:
                EmitNestedMemberInit(receiver, nestedMember, element, statements);
                break;

            case CollectionAddPlan:
                var arguments = element is InitializerExpressionSyntax complex
                    && complex.IsKind(SyntaxKind.ComplexElementInitializerExpression)
                    ? complex.Expressions.Select(e => EmitValue(e, statements)).ToList()
                    : new List<ExpressionSyntax> { EmitValue(element, statements) };
                statements.Add(CollectionInitRewriter.BuildAddStatement(receiver, arguments));
                break;
        }
    }

    private void EmitNestedCreate(
        string receiver,
        NestedCreatePlan nested,
        ExpressionSyntax element,
        List<StatementSyntax> statements)
    {
        var visitedCreation = (BaseObjectCreationExpressionSyntax)ValueOf(element);
        var inner = nested.Nested;

        ExpressionSyntax value;
        if (inner.Outcome is InitializerOutcome.ObjectSequence
            or InitializerOutcome.CollectionSequence)
        {
            statements.AddRange(EmitSequence(inner, visitedCreation));
            value = SyntaxFactory.IdentifierName(inner.TempName!);
        }
        else
        {
            value = ObjectInitRewriter.BuildConstruction(inner, visitedCreation);
        }

        statements.Add(ObjectInitRewriter.BuildMemberAssignment(
            receiver, nested.MemberName ?? "_", value));
    }

    private void EmitNestedMemberInit(
        string receiver,
        NestedMemberInitPlan nestedMember,
        ExpressionSyntax element,
        List<StatementSyntax> statements)
    {
        var visitedInner = (InitializerExpressionSyntax)ValueOf(element);
        var getter = nestedMember.GetterTempName!;

        statements.Add(ExpressionDesugarTempLifter.DeclareVar(
            getter,
            ObjectInitRewriter.MemberAccess(receiver, nestedMember.MemberName ?? "_")));

        EmitMembers(getter, nestedMember.Members, visitedInner, statements);
    }

    /// <summary>Emits any nested-creation statements for <paramref name="visited"/>
    /// and returns the value expression to use in its place.</summary>
    private ExpressionSyntax EmitValue(ExpressionSyntax visited, List<StatementSyntax> statements)
    {
        if (visited is BaseObjectCreationExpressionSyntax creation)
        {
            var plan = PlanForCurrent(creation);
            if (plan is { IsNested: true })
            {
                if (plan.Outcome is InitializerOutcome.ObjectSequence
                    or InitializerOutcome.CollectionSequence)
                {
                    statements.AddRange(EmitSequence(plan, creation));
                    return SyntaxFactory.IdentifierName(plan.TempName!);
                }

                return ObjectInitRewriter.BuildConstruction(plan, creation);
            }
        }

        return visited;
    }

    private static ExpressionSyntax ValueOf(ExpressionSyntax element)
        => element is AssignmentExpressionSyntax assignment ? assignment.Right : element;

    private static ExpressionSyntax LeftOf(ExpressionSyntax element)
        => element is AssignmentExpressionSyntax assignment ? assignment.Left : element;

    // ------------------------------------------------------------------
    // Argument interleaving.
    // ------------------------------------------------------------------

    public override SyntaxNode? VisitArgumentList(ArgumentListSyntax node)
    {
        var visited = (ArgumentListSyntax)base.VisitArgumentList(node)!;

        var hoists = HoistsForCurrent(visited);
        if (hoists is not { Count: > 0 })
            return visited;

        var changed = false;
        var arguments = new List<ArgumentSyntax>(visited.Arguments.Count);
        for (var i = 0; i < visited.Arguments.Count; i++)
        {
            if (hoists.TryGetValue(i, out var temp))
            {
                arguments.Add(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(temp)));
                changed = true;
            }
            else
            {
                arguments.Add(visited.Arguments[i]);
            }
        }

        return changed
            ? visited.WithArguments(SyntaxFactory.SeparatedList(arguments))
            : visited;
    }

    // ------------------------------------------------------------------
    // Anchor registration.
    // ------------------------------------------------------------------

    private void Register(SyntaxNode source, List<StatementSyntax> statements, int order)
    {
        var anchor = ExpressionDesugarTempLifter.FindAnchor(source);
        if (anchor == null)
            return;

        // The anchor annotation was stamped on the shared tree before Pass 2
        // (see InitializerStableMarkers), so TempLifter resolves the current
        // anchor after both C23's and C24's subtree replacements.
        Pending.Add(new ExpressionDesugarTempLifter.PendingTemp(statements, anchor, order));
    }
}

/// <summary>
/// Stable annotation keys for C24. Plans are semantic and bound to the
/// ORIGINAL analysis tree, but an earlier in-family rule (C23) replaces
/// subtrees on the shared progressive tree, after which every traversed
/// sibling gets a fresh node instance (empirically verified; the C36 face
/// works around it with whole-file deferral). Annotations survive those
/// replacements, so each plan site/owner/argument-list/anchor is located via
/// its stamped marker during C24's rewrite. Markers are applied once before
/// Pass 2 (<see cref="Apply"/>).
/// </summary>
internal sealed class InitializerStableMarkers
{
    public const string SiteKind = "ed-init-site";
    public const string OwnerKind = "ed-init-owner";
    public const string ArgsKind = "ed-init-args";

    public sealed record OwnerInfo(SyntaxNode Original, string TempName, int Order);

    public Dictionary<string, InitializerPlan> SiteById { get; } = new();
    public Dictionary<string, OwnerInfo> OwnerById { get; } = new();
    public Dictionary<string, Dictionary<int, string>> HoistsById { get; } = new();
    public Dictionary<InitializerPlan, string> ArgsIdByPlan { get; } = new();
    public Dictionary<SyntaxNode, OwnerInfo> InfoByOriginal { get; } = new();
    public Dictionary<SyntaxNode, SyntaxAnnotation> AnchorAnnotations { get; } = new();

    private readonly Dictionary<SyntaxNode, List<SyntaxAnnotation>> _byNode = new();
    private int _sequence;

    private string Stamp(SyntaxNode node, string kind)
    {
        var id = "ed-init-" + _sequence++;
        var annotation = new SyntaxAnnotation(kind, id);
        if (!_byNode.TryGetValue(node, out var list))
        {
            list = new List<SyntaxAnnotation>();
            _byNode[node] = list;
        }

        list.Add(annotation);
        return id;
    }

    /// <summary>Contributes all site/owner/args/anchor annotations to the
    /// family-wide single stamping pass (keyed by ORIGINAL nodes).</summary>
    public void ContributeTo(Dictionary<SyntaxNode, List<SyntaxAnnotation>> stamps)
    {
        foreach (var pair in _byNode)
        {
            if (!stamps.TryGetValue(pair.Key, out var list))
            {
                list = new List<SyntaxAnnotation>();
                stamps[pair.Key] = list;
            }

            list.AddRange(pair.Value);
        }
    }

    public static InitializerStableMarkers Build(InitializerAnalysis analysis)
    {
        var markers = new InitializerStableMarkers();

        foreach (var plan in analysis.Plans)
        {
            if (plan.Outcome == InitializerOutcome.DeferredForCoexistence)
                continue;

            var siteId = markers.Stamp(plan.Node, SiteKind);
            markers.SiteById[siteId] = plan;

            if (plan.IsOwnerHandled)
            {
                foreach (var owner in plan.OwnerChain)
                {
                    if (!analysis.OwnerTemps.TryGetValue(owner, out var temp))
                        continue;

                    var ownerId = markers.Stamp(owner, OwnerKind);
                    var info = new OwnerInfo(owner, temp, plan.LiftOrder);
                    markers.OwnerById[ownerId] = info;
                    markers.InfoByOriginal[owner] = info;
                }
            }

            if (plan.ArgumentList != null && plan.HoistedArguments.Count > 0)
            {
                var hoistMap = new Dictionary<int, string>();
                foreach (var hoist in plan.HoistedArguments)
                    hoistMap[hoist.Index] = hoist.TempName;

                var argsId = markers.Stamp(plan.ArgumentList, ArgsKind);
                markers.HoistsById[argsId] = hoistMap;
                markers.ArgsIdByPlan[plan] = argsId;
            }

            // Only top-level sequence sites register Pending temps (nested
            // sequences are emitted inline by their enclosing site). Owner
            // sites register against the outermost owner expression.
            if (!plan.IsNested
                && plan.Outcome is InitializerOutcome.ObjectSequence
                    or InitializerOutcome.CollectionSequence)
            {
                var anchorSource = plan.IsOwnerHandled && plan.OwnerChain.Count > 0
                    ? plan.OwnerChain[^1]
                    : plan.Node;
                var anchor = ExpressionDesugarTempLifter.FindAnchor(anchorSource);
                if (anchor != null && !markers.AnchorAnnotations.ContainsKey(anchor))
                {
                    var annotation = new SyntaxAnnotation(
                        ExpressionDesugarTempLifter.AnchorAnnotationKind);
                    markers.AnchorAnnotations[anchor] = annotation;
                    markers._byNode.TryGetValue(anchor, out var existing);
                    var list = existing ?? new List<SyntaxAnnotation>();
                    list.Add(annotation);
                    markers._byNode[anchor] = list;
                }
            }
        }

        return markers;
    }
}
