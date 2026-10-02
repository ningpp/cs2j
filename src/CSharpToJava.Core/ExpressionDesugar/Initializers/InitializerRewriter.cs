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
    private readonly Dictionary<BaseObjectCreationExpressionSyntax, InitializerPlan> _byNode = new();
    private readonly Dictionary<SyntaxNode, InitializerPlan> _ownedSiteByNode = new();
    private readonly HashSet<SyntaxNode> _ownerNodes = new();
    private readonly Dictionary<SyntaxNode, int> _ownerOrder = new();
    private readonly Dictionary<ArgumentListSyntax, Dictionary<int, string>> _hoistByList = new();
    private readonly IReadOnlyDictionary<SyntaxNode, string> _ownerTemps;

    public InitializerRewriter(InitializerAnalysis analysis, SemanticModel model)
    {
        _model = model;
        _ownerTemps = analysis.OwnerTemps;

        foreach (var plan in analysis.Plans)
        {
            if (plan.Outcome == InitializerOutcome.DeferredForCoexistence)
                continue;

            _byNode[plan.Node] = plan;

            if (plan.IsOwnerHandled && plan.OwnerChain.Count > 0)
            {
                foreach (var owner in plan.OwnerChain)
                    _ownerNodes.Add(owner);
                _ownerOrder.TryAdd(plan.OwnerChain[0], plan.LiftOrder);
                _ownedSiteByNode[plan.Node] = plan;
            }

            if (plan.ArgumentList != null)
            {
                if (!_hoistByList.TryGetValue(plan.ArgumentList, out var map))
                {
                    map = new Dictionary<int, string>();
                    _hoistByList[plan.ArgumentList] = map;
                }

                foreach (var hoist in plan.HoistedArguments)
                    map[hoist.Index] = hoist.TempName;
            }
        }
    }

    public List<ExpressionDesugarTempLifter.PendingTemp> Pending { get; } = new();

    public Dictionary<SyntaxNode, SyntaxAnnotation> AnchorAnnotations { get; } = new();

    public int RewriteCount { get; private set; }

    // ------------------------------------------------------------------
    // Creation sites.
    // ------------------------------------------------------------------

    public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        var visited = (ObjectCreationExpressionSyntax)base.VisitObjectCreationExpression(node)!;
        return Apply(node, visited);
    }

    public override SyntaxNode? VisitImplicitObjectCreationExpression(
        ImplicitObjectCreationExpressionSyntax node)
    {
        var visited = (ImplicitObjectCreationExpressionSyntax)
            base.VisitImplicitObjectCreationExpression(node)!;
        return Apply(node, visited);
    }

    private SyntaxNode Apply(
        BaseObjectCreationExpressionSyntax original,
        BaseObjectCreationExpressionSyntax visited)
    {
        if (!_byNode.TryGetValue(original, out var plan))
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

        // Hoist preceding side-effecting arguments left-to-right (argument only).
        foreach (var hoist in plan.HoistedArguments)
        {
            if (!hoist.Declare || plan.ArgumentList == null)
                continue;

            statements.Add(ExpressionDesugarTempLifter.DeclareVar(
                hoist.TempName, plan.ArgumentList.Arguments[hoist.Index].Expression));
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

    private SyntaxNode LowerOwner(SyntaxNode original, SyntaxNode visited)
    {
        if (!_ownerNodes.Contains(original) || HasOwnerAncestor(original))
            return visited;

        var statements = new List<StatementSyntax>();
        var resultTemp = EmitOwnerInto(original, visited, statements);
        Register(original, statements, _ownerOrder.TryGetValue(original, out var order) ? order : 0);
        RewriteCount++;
        return SyntaxFactory.IdentifierName(resultTemp);
    }

    private string EmitOwnerInto(
        SyntaxNode owner, SyntaxNode visitedOwner, List<StatementSyntax> statements)
    {
        var resultTemp = _ownerTemps[owner];

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
            && _ownerNodes.Contains(innerConditional)
            && branchVisited is ConditionalExpressionSyntax visitedInner)
        {
            var innerResult = EmitOwnerInto(innerConditional, visitedInner, blockStatements);
            blockStatements.Add(Assign(resultTemp, SyntaxFactory.IdentifierName(innerResult)));
        }
        else if (branchOriginal is BinaryExpressionSyntax innerCoalesce
            && innerCoalesce.IsKind(SyntaxKind.CoalesceExpression)
            && _ownerNodes.Contains(innerCoalesce)
            && branchVisited is BinaryExpressionSyntax visitedInnerCoalesce)
        {
            var innerResult = EmitOwnerInto(innerCoalesce, visitedInnerCoalesce, blockStatements);
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

    private bool HasOwnerAncestor(SyntaxNode node)
    {
        for (var current = node.Parent; current != null; current = current.Parent)
        {
            if (_ownerNodes.Contains(current))
                return true;
            if (current is StatementSyntax or ArrowExpressionClauseSyntax)
                return false;
        }

        return false;
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
        if (visited is BaseObjectCreationExpressionSyntax creation
            && _byNode.TryGetValue(creation, out var plan)
            && plan.IsNested)
        {
            if (plan.Outcome is InitializerOutcome.ObjectSequence
                or InitializerOutcome.CollectionSequence)
            {
                statements.AddRange(EmitSequence(plan, creation));
                return SyntaxFactory.IdentifierName(plan.TempName!);
            }

            return ObjectInitRewriter.BuildConstruction(plan, creation);
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

        if (!_hoistByList.TryGetValue(node, out var hoists) || hoists.Count == 0)
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

        Pending.Add(new ExpressionDesugarTempLifter.PendingTemp(statements, anchor, order));
        if (!AnchorAnnotations.ContainsKey(anchor))
        {
            AnchorAnnotations[anchor] =
                new SyntaxAnnotation(ExpressionDesugarTempLifter.AnchorAnnotationKind);
        }
    }

    /// <summary>
    /// Keeps anchor nodes locatable after their descendants are rewritten
    /// (same attachment discipline as the C36/C47 rewriters).
    /// </summary>
    public override SyntaxNode? Visit(SyntaxNode? node)
    {
        var result = base.Visit(node);
        if (node != null
            && result != null
            && AnchorAnnotations.TryGetValue(node, out var annotation))
        {
            result = result.WithAdditionalAnnotations(annotation);
        }

        return result;
    }
}
