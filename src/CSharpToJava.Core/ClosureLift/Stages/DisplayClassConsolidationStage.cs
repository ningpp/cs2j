using CSharpToJava.Core.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// C45 M2 stage (design §4.1/§9): consolidates the C32 holder products into full
/// display classes and lifts every capturing statement lambda.
///
/// Analysis/rewrite split (design §4.1): <see cref="DisplayClassPlanner"/> runs on
/// the MODEL tree; the edits below consume the RECEIVED tree (post C32 holder
/// lift and M1 named-method lift) through the shared span/type pairing
/// discipline. The stage
///   1. builds the `Lambda_&lt;k&gt;` instance methods from the received bodies
///      (field-access rewrites baked in, <see cref="LambdaMethodLifter"/>),
///   2. emits instance declarations + read-only snapshot assignments at the
///      D14② statement boundaries (before the first lambda site of each group /
///      each read-only symbol, per-iteration inside loop bodies naturally),
///   3. appends read-only fields, `_cs2jThis` + constructor (D7) and methods to
///      the emitted C32 holder classes (additive member edits only — D2), and
///      fresh display classes for pure read-only/this groups,
///   4. freezes the delegate-site plans for <see cref="DelegateSiteRewriteStage"/>.
/// Any blocking row keeps the file verbatim (C32 plan-level rollback discipline;
/// the CS2J4510 gate freezes unaccounted residuals, §7.3).
/// </summary>
internal sealed class DisplayClassConsolidationStage : IClosureLiftStage
{
    public string Id => "display-class-consolidation";

    public ClosureStageResult Apply(ClosureStageInput input)
    {
        var (root, model, options, naming, statistics, diagnostics) = input;

        if (input.PlanBag is not { } bag
            || options.AuditOnly
            // A frozen file stays verbatim end to end (design §4.1 rollback).
            || diagnostics.Any(d => d.Code != ClosureLiftCodes.AuditRow))
        {
            return new ClosureStageResult(root, false);
        }

        var analysisRoot = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();

        // Non-default array comparator (design §4.4 tail): no display class exists
        // to host methods, so M2 does not migrate in this mode; the census keeps
        // the residual accounting visible instead of silently skipping it.
        if (options.HolderForm == HolderForm.SingleElementArray)
        {
            AccountArrayFormCensus(analysisRoot, model, bag, statistics);
            return new ClosureStageResult(root, false);
        }

        var plan = DisplayClassPlanner.Build(
            analysisRoot, model, options, naming, statistics, diagnostics,
            bag.HolderGroups, bag.StaticLambdas);
        if (plan == null)
            return new ClosureStageResult(root, false); // blocking rows / gate: frozen.

        if (plan.Lambdas.Count == 0)
            return new ClosureStageResult(root, false);

        // D13: recursive delegate groups ride the ordinary field lowering, but
        // only when the assignment-before-entry ordering is provable; an
        // unprovable group trips CS2J4508 and the facade keeps the file
        // verbatim (the gate runs before any edit of this stage).
        if (!RecursiveLambdaHandler.Validate(
                analysisRoot, model, plan, options, diagnostics, statistics))
        {
            return new ClosureStageResult(root, false);
        }

        // Model-tree → received-tree pairing (M1 discipline; the C32+M1 edit
        // vocabulary preserves lambda order and member-spine structure).
        if (!NamedMethodLiftStage.TryPairTrees(
                analysisRoot, root,
                out var receivedLambdaBySpanStart, out var receivedTypeByModelType))
        {
            return new ClosureStageResult(root, false);
        }

        // 1. Build the instance methods (received bodies, rewrites applied).
        var pendingBySpan = plan.Lambdas.ToDictionary(
            static p => (p.SpanStart, p.RawKind), static p => p);
        // Nested PLANNED-STATIC sites are substituted while building received
        // bodies: key on the received span (SiteSpanStart is the model anchor).
        var staticPlansByReceivedSpan = bag.StaticLambdas.ToDictionary(
            static p => (p.ReceivedSpanStart, p.SiteRawKind), static p => p);
        var methodsByGroup = new Dictionary<DisplayClassPlan, List<MethodDeclarationSyntax>>();
        var methodCount = 0;

        foreach (var group in plan.EvolvedGroups.Concat(plan.NewGroups))
        {
            foreach (var methodPlan in group.Methods)
            {
                if (!pendingBySpan.TryGetValue((methodPlan.SiteSpanStart, methodPlan.SiteRawKind),
                        out var pending))
                {
                    return new ClosureStageResult(root, false);
                }

                if (!receivedLambdaBySpanStart.TryGetValue(pending.SpanStart, out var receivedLambda)
                    || receivedLambda.RawKind != pending.RawKind)
                {
                    return new ClosureStageResult(root, false);
                }

                // D4: nested child sites move with the parent's received body;
                // re-anchor their model span onto the received tree.
                var childSites = new Dictionary<(int Start, int Kind), NestedDelegateSite>();
                foreach (var nested in pending.NestedSites)
                {
                    if (!receivedLambdaBySpanStart.TryGetValue(nested.SpanStart, out var nestedReceived)
                        || nestedReceived.RawKind != nested.RawKind)
                    {
                        return new ClosureStageResult(root, false);
                    }

                    childSites[(nestedReceived.SpanStart, nestedReceived.RawKind)] = nested;
                }

                // D12: frozen slot names (planner model-tree decision);
                // attribute lists are read from the received lambda here.
                var parameterSlots = pending.MethodParameterNames
                    .Select(static name => new ParameterSlot(
                        name,
                        name.StartsWith(ClosureLiftNaming.SynthesizedArgPrefix, StringComparison.Ordinal)))
                    .ToArray();

                var context = new LambdaMigrationContext
                {
                    Plan = methodPlan,
                    Signature = pending.Signature,
                    ReceivedLambda = receivedLambda,
                    FieldAccessByName = pending.FieldAccessByName,
                    CollapseByInstanceField = pending.CollapseByInstanceField,
                    InstanceMemberNames = pending.InstanceMemberNames,
                    CapturesThis = pending.CapturesThis,
                    HasHostThis = pending.HasHostThis,
                    HostThisPath = pending.HostThisPath,
                    PromotedParameterNames = pending.PromotedParameterNames,
                    PromotedLocalNames = pending.PromotedLocalNames,
                    ChildSites = childSites,
                    ChildTypeByInstance = pending.ChildTypeByInstance,
                    StaticPlans = staticPlansByReceivedSpan,
                    ParameterSlots = parameterSlots,
                };

                var method = LambdaMethodLifter.BuildMethod(context);

                if (!methodsByGroup.TryGetValue(group, out var list))
                {
                    list = new List<MethodDeclarationSyntax>();
                    methodsByGroup[group] = list;
                }

                list.Add(method);
                methodCount += 1;
            }
        }

        statistics.LambdaMethodsLifted += methodCount;
        var emitter = new DisplayClassSyntaxEmitter();

        // 2. Class members: additive edits on emitted holder classes + fresh
        //    display classes on their hosts (member-spine append, M1 shape).
        //    D4 nested classes are appended into their parent display classes.
        //    This runs BEFORE the statement edits: the spine append rebuilds type
        //    wrappers (so anchor/creation lookups must run against ITS nodes),
        //    while method bodies keep node identity and absolute positions.
        //
        //    Holder class names derive from the routine name, so two sibling
        //    host types with an identically named routine legitimately emit
        //    holders with the same local class name. Edits are therefore keyed
        //    by (received host type, nested-holder chain), never by bare class
        //    name — a bare-name map cross-wires every holder of the same name.
        var classEdits = new Dictionary<(string HostTypeKey, string Chain), List<MemberDeclarationSyntax>>();
        foreach (var group in plan.EvolvedGroups)
        {
            if (!receivedTypeByModelType.TryGetValue(group.HostType, out _))
                return new ClosureStageResult(root, false);

            var edits = new List<MemberDeclarationSyntax>();
            edits.AddRange(group.Symbols
                .Where(static s => s.Reason is TriggerReason.ReadOnlyValue
                    or TriggerReason.PromotedLambdaParameter
                    or TriggerReason.LambdaLocalMutable)
                .Select(emitter.BuildCaptureField));
            if (group.Parent != null)
            {
                edits.Add(emitter.BuildParentField(group.Parent.QualifiedClassName));
                edits.Add(emitter.BuildParentConstructor(group.ClassName, group.Parent.QualifiedClassName));
            }
            else if (group.CapturesThis)
            {
                edits.Add(emitter.BuildThisField(HostTypeDisplay(group.HostType)));
                edits.Add(emitter.BuildThisConstructor(group.ClassName, HostTypeDisplay(group.HostType)));
            }

            if (methodsByGroup.TryGetValue(group, out var methods))
                edits.AddRange(methods);

            if (edits.Count > 0)
                classEdits[(group.HostTypeKey, SelfChain(group))] = edits;
        }

        var newClassesByHost = new Dictionary<TypeDeclarationSyntax, List<ClassDeclarationSyntax>>();
        var nestedClassesByParent = new Dictionary<(string HostTypeKey, string Chain), List<ClassDeclarationSyntax>>();
        var topLevelNewGroupCount = 0;
        foreach (var group in plan.NewGroups)
        {
            methodsByGroup.TryGetValue(group, out var methods);
            var declaration = emitter.BuildDisplayClass(
                group, HostTypeDisplay(group.HostType), methods ?? Enumerable.Empty<MethodDeclarationSyntax>());

            if (group.Parent != null)
            {
                // Attach into the parent's member list: keyed by the parent's
                // own chain (host + path including its class name).
                var key = (group.HostTypeKey, SelfChain(group.Parent));
                if (!nestedClassesByParent.TryGetValue(key, out var nested))
                {
                    nested = new List<ClassDeclarationSyntax>();
                    nestedClassesByParent[key] = nested;
                }

                nested.Add(declaration);
                continue;
            }

            if (!receivedTypeByModelType.TryGetValue(group.HostType, out var receivedHost))
                return new ClosureStageResult(root, false);

            if (!newClassesByHost.TryGetValue(receivedHost, out var classes))
            {
                classes = new List<ClassDeclarationSyntax>();
                newClassesByHost[receivedHost] = classes;
            }

            classes.Add(declaration);
            topLevelNewGroupCount += 1;
        }

        statistics.DisplayClassesGenerated += topLevelNewGroupCount;
        statistics.HolderInstancesGenerated += topLevelNewGroupCount;
        statistics.NestedDisplayClasses += plan.NewGroups.Count - topLevelNewGroupCount;

        // Reverse the model-node → received-node host map so the spine walk
        // can key (host, chain) edits while visiting received-tree nodes.
        var modelHostByReceivedHost =
            new Dictionary<TypeDeclarationSyntax, TypeDeclarationSyntax>(
                System.Collections.Generic.ReferenceEqualityComparer.Instance);
        foreach (var pair in receivedTypeByModelType)
            modelHostByReceivedHost[pair.Value] = pair.Key;
        var withClasses = root.WithMembers(AppendDisplayMembers(
            root.Members, model, modelHostByReceivedHost, newClassesByHost,
            nestedClassesByParent, classEdits, enclosingHost: null, chain: string.Empty));

        var receivedLambdas = root
            .DescendantNodes()
            .OfType<LambdaExpressionSyntax>()
            .ToList();
        StatementSyntax? AnchorStatementOf(
            IReadOnlyList<LambdaExpressionSyntax> liveLambdas,
            int modelSpan)
        {
            if (!receivedLambdaBySpanStart.TryGetValue(modelSpan, out var receivedLambda))
                return null;

            var ordinal = receivedLambdas.FindIndex(l => ReferenceEquals(l, receivedLambda));
            if (ordinal < 0 || ordinal >= liveLambdas.Count
                || liveLambdas[ordinal].RawKind != receivedLambda.RawKind)
            {
                return null;
            }

            var current = liveLambdas[ordinal].Parent;
            while (current is not null and not StatementSyntax)
                current = current.Parent;
            if (current is not StatementSyntax statement)
                return null;

            while (statement.Parent is not (BlockSyntax or SwitchSectionSyntax or null))
            {
                if (statement.Parent is StatementSyntax parentStatement)
                    statement = parentStatement;
                else
                    break;
            }

            return statement;
        }

        // 3. Evolved groups: C32 anchored the instance declaration before the
        //    earliest LIFTED LOCAL declaration, but an M2 lambda merged into that
        //    class may sit in an EARLIER statement (typically a local-declaration
        //    initializer), so its migrated delegate site would reference the
        //    instance before its declaration (CS0841). Hoist the C32 declaration
        //    in front of the earliest such sibling site. The move re-parents that
        //    statement, and Roslyn hands out fresh red nodes for re-parented
        //    subtrees, so EVERY later identity-keyed map below is built on the
        //    hoisted tree (never on `withClasses`).
        var withClassesLambdas = LambdasOutsideDisplayClasses(withClasses);
        var withClassesInstances = CollectInstanceDeclarations(withClasses);
        var hoistBefore = new Dictionary<StatementSyntax, StatementSyntax>();
        foreach (var group in plan.EvolvedGroups.Where(static g => g.Methods.Count > 0))
        {
            var instance = withClassesInstances
                .Where(d => d.Name == group.InstanceName && d.ClassName == group.ClassName)
                .Select(static d => d.Statement)
                .FirstOrDefault();
            if (instance?.Parent is not (BlockSyntax or SwitchSectionSyntax))
                continue;

            var statements = instance.Parent is BlockSyntax block
                ? block.Statements
                : ((SwitchSectionSyntax)instance.Parent).Statements;
            var instanceIndex = OrdinalOf(statements, instance!);
            if (instanceIndex < 0)
                continue;

            var earliest = -1;
            foreach (var method in group.Methods)
            {
                if (AnchorStatementOf(withClassesLambdas, method.SiteSpanStart) is not { } anchor
                    || !ReferenceEquals(anchor.Parent, instance.Parent))
                {
                    continue;
                }

                var index = OrdinalOf(statements, anchor);
                if (index >= 0 && index < instanceIndex && (earliest < 0 || index < earliest))
                    earliest = index;
            }

            if (earliest >= 0)
                hoistBefore[instance] = statements[earliest];
        }

        var hoisted = hoistBefore.Count == 0
            ? withClasses
            : (CompilationUnitSyntax)new InstanceHoistRewriter(hoistBefore).Visit(withClasses)!;

        // 4. Ctor-argument edit for evolved groups that captured `this` (D7): the
        //    C32-emitted `new Class()` creations gain the `this` argument.
        //    Matching uses BOTH (instanceName, className) because instance names are
        //    PER-ROUTINE — the same `__cs2jClosure<N>` name legitimately appears in
        //    multiple methods, but only an evolved group whose class name matches the
        //    `new` target should receive the `this` argument (otherwise unmatched
        //    holders would produce CS1729: 'Class' has no 1-parameter constructor).
        var instanceDeclarations = CollectInstanceDeclarations(hoisted);
        var creationReplacements = new Dictionary<ObjectCreationExpressionSyntax, ObjectCreationExpressionSyntax>();
        var thisNameClassPairs = plan.EvolvedGroups
            .Where(static g => g.CapturesThis)
            .Select(static g => (g.InstanceName, g.ClassName))
            .ToHashSet();
        if (thisNameClassPairs.Count > 0)
        {
            foreach (var (declaration, name, className) in instanceDeclarations)
            {
                if (!thisNameClassPairs.Contains((name, className)))
                    continue;

                var creation = (ObjectCreationExpressionSyntax)
                    declaration.Declaration.Variables[0].Initializer!.Value;
                creationReplacements[creation] = creation.WithArgumentList(
                    SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(SyntaxFactory.ThisExpression()))));
            }
        }

        // 5. Statement insertions (D14②): new-group instance declarations and
        //    read-only snapshots, each at the nearest statement boundary before
        //    its anchor lambda site (after it when that statement declares the
        //    captured symbol). Appending members along the type spine hands out
        //    fresh red wrappers (so reference identity from `root` does NOT
        //    survive) and, for a later sibling type, absolute spans shift by the
        //    inserted members. Anchors therefore re-pair the RECEIVED lambdas with
        //    the current tree's lambdas by descendant ORDER (same ordinal
        //    discipline as the model↔received pairing; generated display classes
        //    are excluded) and then climb that live node's parents to its
        //    statement-list membership.
        var liveLambdas = hoisted == withClasses
            ? withClassesLambdas
            : LambdasOutsideDisplayClasses(hoisted);

        var insertBefore = new Dictionary<SyntaxNode, List<StatementSyntax>>();
        var insertAfter = new Dictionary<SyntaxNode, List<StatementSyntax>>();

        // D4: child groups instantiate inside migrated parent methods (the
        // lifter spliced those declarations); only chain-top fresh groups take
        // routine-scope instance declarations + read-only snapshots here.
        foreach (var group in plan.NewGroups.Where(static g => g.Parent == null))
        {
            var anchorSpan = group.Methods.Count > 0 ? group.Methods[0].SiteSpanStart : -1;
            if (anchorSpan < 0 || AnchorStatementOf(liveLambdas, anchorSpan) is not { } anchor)
                return new ClosureStageResult(root, false);

            AddInsertion(insertBefore, anchor, emitter.BuildInstanceDeclaration(group));

            foreach (var symbol in group.Symbols.Where(static s => s.Reason == TriggerReason.ReadOnlyValue))
            {
                if (!plan.SnapshotAnchors.TryGetValue(symbol, out var snapshotSpan)
                    || AnchorStatementOf(liveLambdas, snapshotSpan) is not { } snapshotAnchor)
                {
                    return new ClosureStageResult(root, false);
                }

                AddSnapshot(insertBefore, insertAfter, snapshotAnchor, symbol,
                    SyntaxFactory.IdentifierName(symbol.OriginalName),
                    value => emitter.BuildSnapshotAssignment(group, symbol, value));
            }
        }

        // Evolved groups only gain snapshot statements (their C32 instance exists).
        foreach (var group in plan.EvolvedGroups)
        {
            foreach (var symbol in group.Symbols.Where(static s => s.Reason == TriggerReason.ReadOnlyValue))
            {
                if (!plan.SnapshotAnchors.TryGetValue(symbol, out var snapshotSpan)
                    || AnchorStatementOf(liveLambdas, snapshotSpan) is not { } snapshotAnchor)
                {
                    return new ClosureStageResult(root, false);
                }

                AddSnapshot(insertBefore, insertAfter, snapshotAnchor, symbol,
                    SyntaxFactory.IdentifierName(symbol.OriginalName),
                    value => emitter.BuildSnapshotAssignment(group, symbol, value));
            }
        }

        var insertionRewriter = new StatementInsertionRewriter(insertBefore, insertAfter, creationReplacements);
        var rewritten = (CompilationUnitSyntax)insertionRewriter.Visit(hoisted)!;

        // 5. Freeze the site plans for the delegate-site stage (D8). Site plans
        //    carry MODEL spans on purpose: DelegateSiteRewriteStage re-anchors
        //    them onto the final tree by order-based pairing, because the
        //    statement edits above shift absolute positions.
        //    D4 child sites are included deliberately: the received main tree
        //    still physically contains the nested lambda nodes (BuildMethod
        //    only produced migrated COPIES), so the model/main-tree lambda
        //    counts stay equal. The stage replaces each chain-top site as a
        //    whole subtree, which drops the nested child nodes before their
        //    own entries are ever visited — child plans are inert there.
        foreach (var group in plan.EvolvedGroups.Concat(plan.NewGroups))
            bag.InstanceLambdas.AddRange(group.Sites);

        return new ClosureStageResult(rewritten, true);
    }

    private static void AddSnapshot(
        Dictionary<SyntaxNode, List<StatementSyntax>> insertBefore,
        Dictionary<SyntaxNode, List<StatementSyntax>> insertAfter,
        StatementSyntax anchor,
        CapturedSymbolPlan symbol,
        ExpressionSyntax value,
        Func<ExpressionSyntax, StatementSyntax> build)
    {
        // The snapshot reads the original variable once; a fresh RHS identifier is
        // never itself rewritten (C32 snapshot discipline). When the anchor
        // statement is the symbol's own declaration statement, the snapshot must
        // follow it instead (use before declaration would not compile).
        var declaresHere = symbol.DeclaringNode.Span.Start >= anchor.SpanStart
                           && symbol.DeclaringNode.Span.End <= anchor.Span.End;
        if (declaresHere)
            AddInsertion(insertAfter, anchor, build(value));
        else
            AddInsertion(insertBefore, anchor, build(value));
    }

    /// <summary>Main-tree lambdas of <paramref name="tree"/>; display-class interiors are excluded.</summary>
    private static List<LambdaExpressionSyntax> LambdasOutsideDisplayClasses(CompilationUnitSyntax tree)
        => tree.DescendantNodes(static n =>
                n is not TypeDeclarationSyntax type || !ClosureLiftAnnotations.IsAnnotated(type))
            .OfType<LambdaExpressionSyntax>()
            .ToList();

    /// <summary>
    /// Every C32 `instance:`-annotated declaration still present in the tree:
    /// (statement, instance name, created class name). Instance names are
    /// per-routine, so all consumers pair them with the class name.
    /// </summary>
    private static List<(LocalDeclarationStatementSyntax Statement, string Name, string ClassName)>
        CollectInstanceDeclarations(CompilationUnitSyntax root)
    {
        var declarations = new List<(LocalDeclarationStatementSyntax, string, string)>();
        foreach (var declaration in root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
        {
            if (!ClosureLiftAnnotations.IsAnnotated(declaration)
                || declaration.Declaration.Variables.Count != 1
                || declaration.Declaration.Variables[0].Initializer?.Value
                    is not ObjectCreationExpressionSyntax creation)
            {
                continue;
            }

            var name = declaration.GetAnnotations(ClosureLiftAnnotations.Kind)
                .FirstOrDefault()?.Data is { } data && data.StartsWith("instance:", StringComparison.Ordinal)
                ? data["instance:".Length..]
                : null;
            var className = creation.Type switch
            {
                IdentifierNameSyntax id => id.Identifier.ValueText,
                GenericNameSyntax generic => generic.Identifier.ValueText,
                _ => null,
            };
            if (name == null || className == null)
                continue;

            declarations.Add((declaration, name, className));
        }

        return declarations;
    }

    /// <summary>Reference-identity ordinal of <paramref name="statement"/> in its own list.</summary>
    private static int OrdinalOf(SyntaxList<StatementSyntax> statements, StatementSyntax statement)
    {
        for (var i = 0; i < statements.Count; i++)
        {
            if (ReferenceEquals(statements[i], statement))
                return i;
        }

        return -1;
    }

    private static void AddInsertion(
        Dictionary<SyntaxNode, List<StatementSyntax>> map,
        StatementSyntax anchor,
        StatementSyntax inserted)
    {
        if (!map.TryGetValue(anchor, out var list))
        {
            list = new List<StatementSyntax>();
            map[anchor] = list;
        }

        list.Add(inserted);
    }

    /// <summary>`C` / `C&lt;T, U&gt;` — the display class's receiver type for `_cs2jThis` (D7).</summary>
    private static string HostTypeDisplay(TypeDeclarationSyntax hostType)
    {
        if (hostType.TypeParameterList == null || hostType.TypeParameterList.Parameters.Count == 0)
            return hostType.Identifier.Text;

        var args = string.Join(", ", hostType.TypeParameterList.Parameters
            .Select(static p => p.Identifier.Text));
        return $"{hostType.Identifier.Text}<{args}>";
    }

    private static void AccountArrayFormCensus(
        CompilationUnitSyntax analysisRoot,
        SemanticModel model,
        ClosureLiftPlanBag bag,
        ClosureLiftStatistics statistics)
    {
        var statementLambdas = 0;
        foreach (var lambda in analysisRoot.DescendantNodes().OfType<LambdaExpressionSyntax>())
        {
            if (ClosureLiftAnnotations.IsAnnotated(lambda))
                continue;
            if (ExpressionTreeFacts.IsExpressionTreeLambda(model, lambda))
            {
                statistics.ExpressionTreeLambdasSkipped += 1;
                continue;
            }

            statementLambdas += 1;
        }

        var residual = statementLambdas - bag.StaticLambdas.Count;
        if (residual > 0)
        {
            statistics.ResidualStatementLambdas += residual;
            statistics.AccountedResidualLambdas += residual;
        }
    }

    /// <summary>
    /// Member-spine append (M1 <c>AppendMethods</c> shape): only the member lists
    /// that gain members are rebuilt; annotated holder classes gain their
    /// additive edits; D4 nested display classes are appended to the annotated
    /// parent class; untouched positions keep identity, so
    /// DelegateSiteRewriteStage's SpanStart anchors resolve as frozen.
    /// <para>
    /// The recursive descent ALWAYS walks the ORIGINAL member list of each type,
    /// never a list this walk just extended: <see cref="WithMembers"/> rebuilds
    /// the whole member list and hands every untouched nested type a FRESH red
    /// node instance, so the identity-keyed lookups below
    /// (<c>newClassesByHost</c>, <c>modelHostByReceivedHost</c>) would silently
    /// miss for nested hosts of any host that gained members first (declaration
    /// dropped while the site rewrite still lands). Appended members are applied
    /// to the recursion result afterwards; freshly appended display classes are
    /// walked in a scoped pass with the same (host, chain) context — their edits
    /// key on annotations/chains, never on node identity.
    /// </para>
    /// <para>
    /// Edit dictionaries are scoped by <c>(host type, holder chain)</c>: the
    /// same local holder name is legal in different host types (and at
    /// different nesting depths), so the walk tracks the enclosing non-holder
    /// host type plus the <c>/a/b</c> chain of annotated holder names leading
    /// to the current member list.
    /// </para>
    /// </summary>
    private static SyntaxList<MemberDeclarationSyntax> AppendDisplayMembers(
        SyntaxList<MemberDeclarationSyntax> members,
        SemanticModel model,
        Dictionary<TypeDeclarationSyntax, TypeDeclarationSyntax> modelHostByReceivedHost,
        Dictionary<TypeDeclarationSyntax, List<ClassDeclarationSyntax>> newClassesByHost,
        Dictionary<(string HostTypeKey, string Chain), List<ClassDeclarationSyntax>> nestedClassesByParent,
        Dictionary<(string HostTypeKey, string Chain), List<MemberDeclarationSyntax>> classEdits,
        TypeDeclarationSyntax? enclosingHost,
        string chain)
    {
        List<MemberDeclarationSyntax>? buffer = null;

        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];
            MemberDeclarationSyntax replacement = member;

            if (member is TypeDeclarationSyntax type)
            {
                var newType = type;
                var holderName = GetHolderClassName(type);
                // Descend into the ORIGINAL members with the context they live
                // in: holder interiors keep the enclosing host + holder chain,
                // a non-annotated type is itself a host boundary for its members.
                var nestedMembers = AppendDisplayMembers(
                    type.Members, model, modelHostByReceivedHost, newClassesByHost,
                    nestedClassesByParent, classEdits,
                    holderName != null ? enclosingHost : HostNodeOf(type),
                    holderName != null ? chain + "/" + holderName : string.Empty);

                if (holderName != null)
                {
                    // Annotated holder (or freshly attached nested holder):
                    // edits and children are scoped by the enclosing host and
                    // the holder chain leading HERE.
                    if (enclosingHost != null)
                    {
                        // Edits target THIS holder (keyed by the chain including
                        // its own name); children attach into THIS holder (keyed
                        // by the chain leading to it). Edits for THIS holder and
                        // children attaching INTO it share the same key: host +
                        // this holder's self chain.
                        var selfChain = chain.Length == 0
                            ? "/" + holderName
                            : chain + "/" + holderName;
                        var hostTypeKey = HolderPlanner.HostTypeKey(model, enclosingHost);
                        if (classEdits.TryGetValue((hostTypeKey, selfChain), out var edits))
                            nestedMembers = nestedMembers.AddRange(edits);

                        // Fresh children are walked in a scoped pass so
                        // grandchildren still find their freshly attached parent
                        // nodes (annotation-keyed edits only, no identity keys).
                        if (nestedClassesByParent.TryGetValue((hostTypeKey, selfChain), out var nested))
                            nestedMembers = nestedMembers.AddRange(WalkFreshClasses(
                                nested, model, modelHostByReceivedHost, newClassesByHost,
                                nestedClassesByParent, classEdits, enclosingHost, selfChain));
                    }
                }
                else
                {
                    // Non-annotated type = host boundary. Fresh top-level
                    // display classes key off the received node; its model
                    // symbol scopes holder edits encountered while descending.
                    if (newClassesByHost.TryGetValue(type, out var classes))
                        nestedMembers = nestedMembers.AddRange(WalkFreshClasses(
                            classes, model, modelHostByReceivedHost, newClassesByHost,
                            nestedClassesByParent, classEdits, HostNodeOf(type), string.Empty));
                }

                if (nestedMembers != type.Members)
                    newType = type.WithMembers(nestedMembers);

                if (!ReferenceEquals(newType, type))
                    replacement = newType;
            }
            else if (member is BaseNamespaceDeclarationSyntax namespaceDeclaration)
            {
                var namespaceMembers = AppendDisplayMembers(
                    namespaceDeclaration.Members, model, modelHostByReceivedHost, newClassesByHost,
                    nestedClassesByParent, classEdits, enclosingHost, chain);
                if (namespaceMembers != namespaceDeclaration.Members)
                    replacement = namespaceDeclaration.WithMembers(namespaceMembers);
            }

            if (!ReferenceEquals(replacement, member) && buffer == null)
            {
                buffer = new List<MemberDeclarationSyntax>();
                for (var j = 0; j < i; j++)
                    buffer.Add(members[j]);
            }

            buffer?.Add(replacement);
        }

        return buffer == null ? members : SyntaxFactory.List(buffer);

        // Model host of a received host node (null when unpaired).
        TypeDeclarationSyntax? HostNodeOf(TypeDeclarationSyntax receivedType)
            => modelHostByReceivedHost.TryGetValue(receivedType, out var host) ? host : null;
    }

    /// <summary>
    /// Scoped walk over freshly appended display classes: attaches THEIR nested
    /// children (grandchildren of the host, D4) in the same
    /// <c>(enclosing host, chain)</c> context the spine walk would have used.
    /// The spine walk cannot visit them through its own recursion without
    /// rebuilding the host member list first, which would destroy the red-node
    /// identities its identity-keyed lookups depend on.
    /// </summary>
    private static SyntaxList<MemberDeclarationSyntax> WalkFreshClasses(
        List<ClassDeclarationSyntax> classes,
        SemanticModel model,
        Dictionary<TypeDeclarationSyntax, TypeDeclarationSyntax> modelHostByReceivedHost,
        Dictionary<TypeDeclarationSyntax, List<ClassDeclarationSyntax>> newClassesByHost,
        Dictionary<(string HostTypeKey, string Chain), List<ClassDeclarationSyntax>> nestedClassesByParent,
        Dictionary<(string HostTypeKey, string Chain), List<MemberDeclarationSyntax>> classEdits,
        TypeDeclarationSyntax? enclosingHost,
        string chain)
        => AppendDisplayMembers(
            SyntaxFactory.List<MemberDeclarationSyntax>(classes),
            model, modelHostByReceivedHost, newClassesByHost,
            nestedClassesByParent, classEdits, enclosingHost, chain);

    /// <summary>
    /// Chain identifying the holder itself (its parent path + its own class
    /// name), e.g. <c>/Cs2jDisplayClass_M_0/Cs2jDisplayClass_M_1</c>.
    /// </summary>
    private static string SelfChain(DisplayClassPlan group)
    {
        var parentChain = ParentChain(group);
        return parentChain.Length == 0
            ? "/" + group.ClassName
            : parentChain + "/" + group.ClassName;
    }

    /// <summary>
    /// <c>/Outer/.../DirectParent</c> chain of holder class names above
    /// <paramref name="group"/> (<c>""</c> for a top-level group).
    /// </summary>
    private static string ParentChain(DisplayClassPlan group)
    {
        var current = group.Parent;
        if (current == null)
            return string.Empty;

        var depth = 0;
        for (var node = current; node != null; node = node.Parent)
            depth += 1;

        var parts = new string[depth];
        var cursor = current;
        for (var i = depth - 1; i >= 0; i--)
        {
            parts[i] = cursor!.ClassName;
            cursor = cursor.Parent;
        }

        return "/" + string.Join("/", parts);
    }

    private static string? GetHolderClassName(TypeDeclarationSyntax type)
    {
        foreach (var annotation in type.GetAnnotations(ClosureLiftAnnotations.Kind))
        {
            if (annotation.Data is { } data && data.StartsWith("class:", StringComparison.Ordinal))
                return data["class:".Length..];
        }

        return null;
    }

    /// <summary>
    /// Moves one statement to the front of another inside the SAME statement
    /// list (block / switch section). Only list ORDER changes: every statement
    /// node keeps its identity, so later identity-keyed insertion/creation maps
    /// still resolve.
    /// </summary>
    private sealed class InstanceHoistRewriter : CSharpSyntaxRewriter
    {
        private readonly Dictionary<StatementSyntax, StatementSyntax> _hoistBefore;

        public InstanceHoistRewriter(Dictionary<StatementSyntax, StatementSyntax> hoistBefore)
        {
            _hoistBefore = hoistBefore;
        }

        public override SyntaxNode? VisitBlock(BlockSyntax node)
        {
            var visited = (BlockSyntax?)base.VisitBlock(node);
            return visited == null
                ? null
                : visited.WithStatements(Hoist(node.Statements, visited.Statements));
        }

        public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
        {
            var visited = (SwitchSectionSyntax?)base.VisitSwitchSection(node);
            if (visited == null)
                return null;

            var statements = visited.Statements;
            var hoisted = Hoist(node.Statements, statements);
            return hoisted == statements ? visited : visited.WithStatements(hoisted);
        }

        private SyntaxList<StatementSyntax> Hoist(
            SyntaxList<StatementSyntax> originalStatements,
            SyntaxList<StatementSyntax> visitedStatements)
        {
            if (_hoistBefore.Count == 0 || originalStatements.Count != visitedStatements.Count)
                return visitedStatements;

            var removed = new HashSet<int>();
            var movedBefore = new Dictionary<int, List<int>>();
            foreach (var (instance, anchor) in _hoistBefore)
            {
                var from = OrdinalOf(originalStatements, instance);
                var to = OrdinalOf(originalStatements, anchor);
                if (from < 0 || to < 0 || to >= from)
                    continue;

                removed.Add(from);
                if (!movedBefore.TryGetValue(to, out var sources))
                {
                    sources = new List<int>();
                    movedBefore[to] = sources;
                }

                sources.Add(from);
            }

            if (removed.Count == 0)
                return visitedStatements;

            var result = new List<StatementSyntax>(visitedStatements.Count);
            for (var i = 0; i < visitedStatements.Count; i++)
            {
                if (movedBefore.TryGetValue(i, out var sources))
                    result.AddRange(sources.OrderBy(static s => s).Select(s => visitedStatements[s]));

                if (!removed.Contains(i))
                    result.Add(visitedStatements[i]);
            }

            return SyntaxFactory.List(result);
        }
    }

    /// <summary>
    /// One bottom-up traversal applying the frozen insertions (by received-statement
    /// identity) and the `new Class()` → `new Class(this)` replacements. Mirrors the
    /// C32 <c>CaptureSiteRewriter</c> list-rebuild pattern (Block/SwitchSection).
    /// </summary>
    private sealed class StatementInsertionRewriter : CSharpSyntaxRewriter
    {
        private readonly Dictionary<SyntaxNode, List<StatementSyntax>> _insertBefore;
        private readonly Dictionary<SyntaxNode, List<StatementSyntax>> _insertAfter;
        private readonly Dictionary<ObjectCreationExpressionSyntax, ObjectCreationExpressionSyntax> _creationReplacements;

        public StatementInsertionRewriter(
            Dictionary<SyntaxNode, List<StatementSyntax>> insertBefore,
            Dictionary<SyntaxNode, List<StatementSyntax>> insertAfter,
            Dictionary<ObjectCreationExpressionSyntax, ObjectCreationExpressionSyntax> creationReplacements)
        {
            _insertBefore = insertBefore;
            _insertAfter = insertAfter;
            _creationReplacements = creationReplacements;
        }

        public override SyntaxNode? VisitBlock(BlockSyntax node)
        {
            var visited = (BlockSyntax?)base.VisitBlock(node);
            if (visited == null)
                return null;

            var statements = ApplyInsertions(node.Statements, visited.Statements);
            return statements == visited.Statements ? visited : visited.WithStatements(statements);
        }

        public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
        {
            var visited = (SwitchSectionSyntax?)base.VisitSwitchSection(node);
            if (visited == null)
                return null;

            var statements = ApplyInsertions(node.Statements, visited.Statements);
            return statements == visited.Statements ? visited : visited.WithStatements(statements);
        }

        public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            if (_creationReplacements.TryGetValue(node, out var replacement))
                return replacement;
            return base.VisitObjectCreationExpression(node);
        }

        /// <summary>
        /// Positional pairing (C32 ExpandForLoop discipline): insertions are keyed
        /// on ORIGINAL statement identity and splice around the VISITED statement
        /// at the same list position.
        /// </summary>
        private SyntaxList<StatementSyntax> ApplyInsertions(
            SyntaxList<StatementSyntax> originalStatements,
            SyntaxList<StatementSyntax> visitedStatements)
        {
            if (_insertBefore.Count == 0 && _insertAfter.Count == 0)
                return visitedStatements;
            if (originalStatements.Count != visitedStatements.Count)
                return visitedStatements; // defensive: never splice blind.

            var result = new List<StatementSyntax>();
            var changed = false;
            for (var i = 0; i < originalStatements.Count; i++)
            {
                if (_insertBefore.TryGetValue(originalStatements[i], out var before))
                {
                    result.AddRange(before);
                    changed = true;
                }

                result.Add(visitedStatements[i]);

                if (_insertAfter.TryGetValue(originalStatements[i], out var after))
                {
                    result.AddRange(after);
                    changed = true;
                }
            }

            return changed ? SyntaxFactory.List(result) : visitedStatements;
        }
    }
}
