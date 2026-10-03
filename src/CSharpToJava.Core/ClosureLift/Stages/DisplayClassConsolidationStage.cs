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
        // The lifter substitutes nested static sites inside RECEIVED-tree
        // bodies: key on the received span (SiteSpanStart is the model anchor
        // consumed by the planner and the delegate-site stage).
        var staticPlansBySpan = bag.StaticLambdas.ToDictionary(
            static p => (p.ReceivedSpanStart, p.SiteRawKind), static p => p);
        var methodsByClassName = new Dictionary<string, List<MethodDeclarationSyntax>>(StringComparer.Ordinal);
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

                var method = LambdaMethodLifter.BuildMethod(
                    methodPlan, pending.Signature, receivedLambda,
                    pending.ReadOnlyFieldByOriginalName, pending.InstanceMemberNames,
                    pending.CapturesThis, group.UsedFieldNames, staticPlansBySpan);

                if (!methodsByClassName.TryGetValue(group.ClassName, out var list))
                {
                    list = new List<MethodDeclarationSyntax>();
                    methodsByClassName[group.ClassName] = list;
                }

                list.Add(method);
                methodCount += 1;
            }
        }

        statistics.LambdaMethodsLifted += methodCount;
        statistics.DisplayClassesGenerated += plan.NewGroups.Count;
        var emitter = new DisplayClassSyntaxEmitter();

        // 2. Class members: additive edits on emitted holder classes + fresh
        //    display classes on their hosts (member-spine append, M1 shape).
        //    This runs BEFORE the statement edits: the spine append rebuilds type
        //    wrappers (so anchor/creation lookups must run against ITS nodes),
        //    while method bodies keep node identity and absolute positions.
        var classEdits = new Dictionary<string, List<MemberDeclarationSyntax>>(StringComparer.Ordinal);
        foreach (var group in plan.EvolvedGroups)
        {
            var edits = new List<MemberDeclarationSyntax>();
            edits.AddRange(group.Symbols
                .Where(static s => s.Reason == TriggerReason.ReadOnlyValue)
                .Select(emitter.BuildCaptureField));
            if (group.CapturesThis)
            {
                edits.Add(emitter.BuildThisField(HostTypeDisplay(group.HostType)));
                edits.Add(emitter.BuildThisConstructor(group.ClassName, HostTypeDisplay(group.HostType)));
            }

            if (methodsByClassName.TryGetValue(group.ClassName, out var methods))
                edits.AddRange(methods);

            if (edits.Count > 0)
                classEdits[group.ClassName] = edits;
        }

        var newClassesByHost = new Dictionary<TypeDeclarationSyntax, List<ClassDeclarationSyntax>>();
        foreach (var group in plan.NewGroups)
        {
            if (!receivedTypeByModelType.TryGetValue(group.HostType, out var receivedHost))
                return new ClosureStageResult(root, false);

            methodsByClassName.TryGetValue(group.ClassName, out var methods);
            var declaration = emitter.BuildDisplayClass(
                group, HostTypeDisplay(group.HostType), methods ?? Enumerable.Empty<MethodDeclarationSyntax>());

            if (!newClassesByHost.TryGetValue(receivedHost, out var classes))
            {
                classes = new List<ClassDeclarationSyntax>();
                newClassesByHost[receivedHost] = classes;
            }

            classes.Add(declaration);
        }

        var withClasses = root.WithMembers(AppendDisplayMembers(
            root.Members, newClassesByHost, classEdits));

        // 3. Ctor-argument edit for evolved groups that captured `this` (D7): the
        //    C32-emitted `new Class()` creations gain the `this` argument.
        var creationReplacements = new Dictionary<ObjectCreationExpressionSyntax, ObjectCreationExpressionSyntax>();
        var thisGroupNames = plan.EvolvedGroups
            .Where(static g => g.CapturesThis)
            .Select(static g => g.InstanceName)
            .ToHashSet(StringComparer.Ordinal);
        if (thisGroupNames.Count > 0)
        {
            foreach (var declaration in withClasses.DescendantNodes()
                         .OfType<LocalDeclarationStatementSyntax>())
            {
                if (!ClosureLiftAnnotations.IsAnnotated(declaration)
                    || declaration.Declaration.Variables.Count != 1)
                {
                    continue;
                }

                var name = declaration.GetAnnotations(ClosureLiftAnnotations.Kind)
                    .FirstOrDefault()?.Data is { } data && data.StartsWith("instance:", StringComparison.Ordinal)
                    ? data["instance:".Length..]
                    : null;
                if (name == null || !thisGroupNames.Contains(name))
                    continue;

                if (declaration.Declaration.Variables[0].Initializer?.Value
                    is ObjectCreationExpressionSyntax creation)
                {
                    creationReplacements[creation] = creation.WithArgumentList(
                        SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(SyntaxFactory.ThisExpression()))));
                }
            }
        }

        // 4. Statement insertions (D14②): new-group instance declarations and
        //    read-only snapshots, each at the nearest statement boundary before
        //    its anchor lambda site (after it when that statement declares the
        //    captured symbol). Anchors are RECEIVED-tree spans — model spans
        //    shift once C32 inserted its instance statements — re-anchored here
        //    through the pairing map, and looked up against `withClasses` so the
        //    rewriter's node identities match exactly.
        StatementSyntax? AnchorStatementOf(int modelSpan)
        {
            if (!receivedLambdaBySpanStart.TryGetValue(modelSpan, out var receivedLambda))
                return null;
            return FindStatementInStatementList(withClasses, receivedLambda.SpanStart);
        }

        var insertBefore = new Dictionary<SyntaxNode, List<StatementSyntax>>();
        var insertAfter = new Dictionary<SyntaxNode, List<StatementSyntax>>();

        foreach (var group in plan.NewGroups)
        {
            var anchorSpan = group.Methods.Count > 0 ? group.Methods[0].SiteSpanStart : -1;
            if (anchorSpan < 0 || AnchorStatementOf(anchorSpan) is not { } anchor)
            {
                return new ClosureStageResult(root, false);
            }

            AddInsertion(insertBefore, anchor, emitter.BuildInstanceDeclaration(group));

            foreach (var symbol in group.Symbols.Where(static s => s.Reason == TriggerReason.ReadOnlyValue))
            {
                if (!plan.SnapshotAnchors.TryGetValue(symbol, out var snapshotSpan)
                    || AnchorStatementOf(snapshotSpan) is not { } snapshotAnchor)
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
                    || AnchorStatementOf(snapshotSpan) is not { } snapshotAnchor)
                {
                    return new ClosureStageResult(root, false);
                }

                AddSnapshot(insertBefore, insertAfter, snapshotAnchor, symbol,
                    SyntaxFactory.IdentifierName(symbol.OriginalName),
                    value => emitter.BuildSnapshotAssignment(group, symbol, value));
            }
        }

        var insertionRewriter = new StatementInsertionRewriter(insertBefore, insertAfter, creationReplacements);
        var rewritten = (CompilationUnitSyntax)insertionRewriter.Visit(withClasses)!;

        // 5. Freeze the site plans for the delegate-site stage (D8). Site plans
        //    carry MODEL spans on purpose: DelegateSiteRewriteStage re-anchors
        //    them onto the final tree by order-based pairing, because the
        //    statement edits above shift absolute positions.
        foreach (var group in plan.EvolvedGroups.Concat(plan.NewGroups))
            bag.InstanceLambdas.AddRange(group.Sites);

        statistics.HolderInstancesGenerated += plan.NewGroups.Count;

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

    /// <summary>
    /// The innermost statement whose span contains <paramref name="spanStart"/>,
    /// lifted to its enclosing statement-list membership (embedded statements of
    /// if/for/while/… hoist to the loop/selection statement — the nearest valid
    /// boundary before the site, D14②). Lambda bodies never contain anchors
    /// (nested capturing lambdas stay residual), so the walk excludes them.
    /// </summary>
    private static StatementSyntax? FindStatementInStatementList(
        CompilationUnitSyntax root,
        int spanStart)
    {
        var innermost = root.DescendantNodes(static n => n is not LambdaExpressionSyntax)
            .OfType<StatementSyntax>()
            .Where(s => s.Span.Contains(spanStart))
            .OrderBy(static s => s.Span.Length)
            .FirstOrDefault();

        while (innermost?.Parent is not (BlockSyntax or SwitchSectionSyntax or null))
        {
            if (innermost.Parent is StatementSyntax parent)
                innermost = parent;
            else
                break;
        }

        return innermost;
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
    /// additive edits; untouched positions keep identity, so
    /// DelegateSiteRewriteStage's SpanStart anchors resolve as frozen.
    /// </summary>
    private static SyntaxList<MemberDeclarationSyntax> AppendDisplayMembers(
        SyntaxList<MemberDeclarationSyntax> members,
        Dictionary<TypeDeclarationSyntax, List<ClassDeclarationSyntax>> newClassesByHost,
        Dictionary<string, List<MemberDeclarationSyntax>> classEdits)
    {
        List<MemberDeclarationSyntax>? buffer = null;

        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];
            MemberDeclarationSyntax replacement = member;

            if (member is TypeDeclarationSyntax type)
            {
                var newType = type;

                var className = GetHolderClassName(type);
                if (className != null && classEdits.TryGetValue(className, out var edits))
                    newType = newType.WithMembers(newType.Members.AddRange(edits));

                if (newClassesByHost.TryGetValue(type, out var classes))
                    newType = newType.WithMembers(newType.Members.AddRange(classes));

                var nestedMembers = AppendDisplayMembers(newType.Members, newClassesByHost, classEdits);
                if (nestedMembers != newType.Members)
                    newType = newType.WithMembers(nestedMembers);

                if (!ReferenceEquals(newType, type))
                    replacement = newType;
            }
            else if (member is BaseNamespaceDeclarationSyntax namespaceDeclaration)
            {
                var namespaceMembers = AppendDisplayMembers(
                    namespaceDeclaration.Members, newClassesByHost, classEdits);
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
