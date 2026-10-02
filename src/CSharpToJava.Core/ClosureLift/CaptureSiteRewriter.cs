using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// The single site rewriter (design §4.5). Input is the FROZEN plan set:
/// identifiers are mapped by original node identity (never by name), so
/// shadowing same-named locals cannot be hit by mistake. Quoted-lambda and
/// nameof sites never enter the plan (CaptureAnalyzer excluded them).
///
/// Two structural steps, mirroring E02:
///   1. InsertHolderClasses(root) — nested holder types (ReplaceNodes).
///   2. Visit(root)                 — statement splices + identifier sites in
///                                    one bottom-up traversal.
/// Moved initializer expressions keep their original node identity, so the
/// site table still resolves them after relocation.
/// Task 10 extends statement registration for for/foreach (D9).
/// </summary>
internal sealed class CaptureSiteRewriter : CSharpSyntaxRewriter
{
    private readonly HolderSyntaxGenerator _generator = new();
    private readonly ClosureLiftStatistics? _statistics;

    // Identifier node (original tree) → replacement expression.
    private readonly Dictionary<IdentifierNameSyntax, ExpressionSyntax> _siteTargets = new();

    // Lifted declarator → its plan (for statement rebuilds).
    private readonly Dictionary<VariableDeclaratorSyntax, CapturedSymbolPlan> _liftedDeclarators = new();

    // Original statement → statements it becomes (empty list = remove).
    private readonly Dictionary<StatementSyntax, List<StatementSyntax>> _statementReplacements = new();

    // Original block → statements inserted at its top (parameter groups, D4).
    private readonly Dictionary<BlockSyntax, List<StatementSyntax>> _blockPrepends = new();

    // Original host type → holder classes appended to its members.
    private readonly Dictionary<TypeDeclarationSyntax, List<ClassDeclarationSyntax>> _holderClasses = new();

    // for-statement (original node) → lifted induction symbols, declarator order.
    private readonly Dictionary<ForStatementSyntax, List<CapturedSymbolPlan>> _liftedForDeclarators = new();

    // foreach loops whose embedded statement is NOT a block: the loop is rebuilt
    // with a block opening with the per-iteration instance + snapshot (D9).
    private readonly Dictionary<ForEachStatementSyntax, List<StatementSyntax>> _foreachWraps = new();

    public CaptureSiteRewriter(
        IEnumerable<HolderGroupPlan> groups,
        ClosureLiftStatistics? statistics = null)
    {
        _statistics = statistics;

        foreach (var group in groups)
            RegisterGroup(group);

        BuildLocalDeclarationReplacements();
        BuildForLoopExpansions();
    }

    /// <summary>Number of identifier sites actually replaced during <see cref="Visit"/>.</summary>
    public int SitesRewritten { get; private set; }

    // Holder classes are appended DURING the single traversal (not via a prior
    // ReplaceNodes step): replacing the host type first would allocate new
    // wrappers for the whole host subtree and invalidate the frozen identity
    // tables (mirrors E02's one-Visit shape). The three host kinds are exactly
    // the insertable host types the analyzer accepts (class/struct/record).
    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
        => AppendHolderClasses(node, (TypeDeclarationSyntax?)base.VisitClassDeclaration(node));

    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
        => AppendHolderClasses(node, (TypeDeclarationSyntax?)base.VisitStructDeclaration(node));

    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node)
        => AppendHolderClasses(node, (TypeDeclarationSyntax?)base.VisitRecordDeclaration(node));

    private TypeDeclarationSyntax? AppendHolderClasses(
        TypeDeclarationSyntax original, TypeDeclarationSyntax? visited)
    {
        if (visited is null || !_holderClasses.TryGetValue(original, out var added))
            return visited;
        return visited.WithMembers(visited.Members.AddRange(added));
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        if (_siteTargets.TryGetValue(node, out var replacement))
        {
            SitesRewritten += 1;
            if (_statistics != null)
                _statistics.SitesRewritten += 1;
            // Assignment LHS / ++-- operand / ref-out argument / lock expression
            // all stay legal with a field or array-element expression in place.
            return replacement.WithTriviaFrom(node);
        }

        return base.VisitIdentifierName(node);
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
        => RebuildStatementList(node, node.Statements, (_, rebuilt) => rebuilt);

    public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
    {
        var statements = VisitStatementList(node.Statements);
        return node.WithStatements(SyntaxFactory.List(statements));
    }

    public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
    {
        if (!_foreachWraps.TryGetValue(node, out var prepend))
            return base.VisitForEachStatement(node);

        // Bottom-up: visit the original loop (embedded statement + all site
        // hits) first, then wrap the VISITED embedded statement in a block that
        // opens with the per-iteration instance + snapshot.
        var visited = (ForEachStatementSyntax)base.VisitForEachStatement(node)!;

        var wrapped = new List<StatementSyntax>();
        foreach (var statement in prepend)
        {
            if (Visit(statement) is StatementSyntax visitedPrepend)
                wrapped.Add(visitedPrepend);
        }
        wrapped.Add(visited.Statement);

        // Trivia/indentation is canonicalized by the Formatter pass (Task 11,
        // same AdhocWorkspace shape as E02).
        return visited.WithStatement(SyntaxFactory.Block(SyntaxFactory.List(wrapped)));
    }

    private BlockSyntax? RebuildStatementList(
        BlockSyntax original,
        SyntaxList<StatementSyntax> originalStatements,
        Func<BlockSyntax, BlockSyntax, BlockSyntax> attach)
    {
        var visitedStatements = VisitStatementList(originalStatements);
        if (!_blockPrepends.TryGetValue(original, out var prepend))
        {
            var rebuilt = attach(original, original.WithStatements(SyntaxFactory.List(visitedStatements)));
            // Returning null from a Visit override would DELETE the block; an
            // unchanged block must come back as its original node.
            return ReferenceEquals(rebuilt, original) ? original : rebuilt;
        }

        var all = new List<StatementSyntax>(prepend);
        all.AddRange(visitedStatements);
        return attach(original, original.WithStatements(SyntaxFactory.List(all)));
    }

    private List<StatementSyntax> VisitStatementList(SyntaxList<StatementSyntax> statements)
    {
        var result = new List<StatementSyntax>();
        foreach (var statement in statements)
        {
            if (_statementReplacements.TryGetValue(statement, out var replaced))
            {
                foreach (var candidate in replaced)
                {
                    if (Visit(candidate) is StatementSyntax visited)
                        result.Add(visited);
                }
                continue;
            }

            if (Visit(statement) is StatementSyntax visitedStatement)
                result.Add(visitedStatement);
        }

        return result;
    }

    private void RegisterGroup(HolderGroupPlan group)
    {
        if (group.PerIteration && _statistics != null)
            _statistics.ForeachPerIterationGroups += 1;

        if (!group.ArrayForm && !_holderClasses.ContainsKey(group.HostType))
            _holderClasses[group.HostType] = new List<ClassDeclarationSyntax>();

        foreach (var symbol in group.Symbols)
            RegisterSites(group, symbol);

        if (!group.ArrayForm)
            _holderClasses[group.HostType].Add(_generator.BuildHolderClass(group));

        var parameters = group.Symbols.Where(s => s.Parameter != null).ToList();
        if (parameters.Count > 0)
        {
            // D4: instance at body first statement, then per-parameter snapshot
            // assignments; the RHS identifier is a FRESH node, so it reads the
            // original parameter once and is never itself site-rewritten.
            var body = (group.HostMember as BaseMethodDeclarationSyntax)?.Body
                ?? (group.HostMember as AccessorDeclarationSyntax)?.Body
                ?? throw new InvalidOperationException(
                    "C32: parameter capture in a routine without a block body must be " +
                    "rejected with CS2J3204 before rewriting (expression-bodied/D16 boundary).");

            var prepend = new List<StatementSyntax>();
            if (!group.ArrayForm)
                prepend.Add(_generator.BuildHolderInstanceDeclaration(group));
            foreach (var parameter in parameters)
            {
                prepend.Add(BuildInitialization(
                    group,
                    parameter,
                    SyntaxFactory.IdentifierName(parameter.OriginalName)));
            }

            if (!_blockPrepends.TryGetValue(body, out var list))
            {
                list = new List<StatementSyntax>();
                _blockPrepends[body] = list;
            }
            list.AddRange(prepend);
        }

        if (group.PerIteration && group.PerIterationLoop is { } foreachLoop)
            RegisterForeachSnapshot(group, foreachLoop);
    }

    /// <summary>
    /// D9 foreach side: every iteration opens with a fresh holder instance (or
    /// fresh array storage) snapshotted from THIS iteration's value. The RHS
    /// identifier is a FRESH SyntaxFactory node — identity-based site lookup can
    /// never match it, so it reads the original iteration variable exactly once;
    /// every body/lambda reference of the symbol is still in the frozen site
    /// table and rewrites to the field/element.
    /// </summary>
    private void RegisterForeachSnapshot(HolderGroupPlan group, ForEachStatementSyntax loop)
    {
        var prepend = new List<StatementSyntax>();

        if (group.ArrayForm)
        {
            foreach (var symbol in group.Symbols)
            {
                prepend.Add(_generator.BuildArrayStorageDeclaration(symbol));
                prepend.Add(_generator.BuildArrayAssignment(
                    symbol.StorageName,
                    SyntaxFactory.IdentifierName(symbol.OriginalName)));
            }
        }
        else
        {
            prepend.Add(_generator.BuildHolderInstanceDeclaration(group));
            foreach (var symbol in group.Symbols)
            {
                prepend.Add(BuildInitialization(
                    group,
                    symbol,
                    SyntaxFactory.IdentifierName(symbol.OriginalName)));
            }
        }

        if (loop.Statement is BlockSyntax block)
        {
            // Block body: reuse the Task 9 prepend channel; VisitBlock splices.
            if (!_blockPrepends.TryGetValue(block, out var list))
            {
                list = new List<StatementSyntax>();
                _blockPrepends[block] = list;
            }
            list.AddRange(prepend);
        }
        else
        {
            // Embedded non-block body: VisitForEachStatement wraps it in a block.
            _foreachWraps[loop] = prepend;
        }
    }

    private void RegisterSites(HolderGroupPlan group, CapturedSymbolPlan symbol)
    {
        _groupBySymbol[symbol] = group;

        foreach (var site in symbol.Sites)
        {
            if (site.Reference.GetSyntax() is not IdentifierNameSyntax identifier)
                continue; // Defensive: stale reference never silently rewrites.

            var target = group.ArrayForm
                ? (ExpressionSyntax)_generator.BuildArrayElementAccess(symbol.StorageName)
                : _generator.BuildFieldAccess(group.InstanceName, symbol.FieldName);
            _siteTargets[identifier] = target;
        }

        if (symbol.Declarator != null)
            _liftedDeclarators[symbol.Declarator] = symbol;

        if (symbol.ForLoop is { } forLoop)
        {
            if (!_liftedForDeclarators.TryGetValue(forLoop, out var forList))
            {
                forList = new List<CapturedSymbolPlan>();
                _liftedForDeclarators[forLoop] = forList;
            }
            forList.Add(symbol);
        }
    }

    /// <summary>
    /// Materialize per-declaration splices. Within one declaration statement
    /// declarators are walked in original order: lifted declarators become
    /// storage/assignment statements (array form: storage + assignment),
    /// consecutive un-lifted declarators stay as one declaration. The group
    /// instance declaration (holder form, non-parameter groups) is emitted at
    /// the earliest lifted declaration statement.
    /// </summary>
    private void BuildLocalDeclarationReplacements()
    {
        // statement → lifted symbols in declarator order; populated first so
        // the anchor decision can observe every statement.
        var liftedByStatement =
            new Dictionary<LocalDeclarationStatementSyntax, List<CapturedSymbolPlan>>();
        foreach (var symbol in _liftedDeclarators.Values)
        {
            if (symbol.Declarator!.Parent is not VariableDeclarationSyntax declaration
                || declaration.Parent is not LocalDeclarationStatementSyntax statement)
                continue;

            if (!liftedByStatement.TryGetValue(statement, out var list))
            {
                list = new List<CapturedSymbolPlan>();
                liftedByStatement[statement] = list;
            }
            list.Add(symbol);
        }

        // Anchor: holder instance goes before the earliest lifted local
        // declaration of each non-parameter group (Task 10 adds for-loops).
        var anchoredGroups = new HashSet<HolderGroupPlan>(GroupsWithLocalAnchor());

        foreach (var (statement, lifted) in liftedByStatement)
        {
            var generated = new List<StatementSyntax>();

            // One instance at most per statement (the group's earliest anchor).
            foreach (var group in anchoredGroups)
            {
                if (group.ArrayForm || GroupHasParameters(group))
                    continue;
                if (EarliestAnchorStatement(group) == statement)
                    generated.Add(_generator.BuildHolderInstanceDeclaration(group));
            }

            var liftedSet = new HashSet<VariableDeclaratorSyntax>(
                lifted.Select(s => s.Declarator)!);

            // Walk every declarator in source order, interleaving generated
            // statements with the rebuilt declaration of un-lifted runs.
            var run = new List<VariableDeclaratorSyntax>();
            foreach (var declarator in statement.Declaration.Variables)
            {
                if (!liftedSet.Contains(declarator))
                {
                    run.Add(declarator);
                    continue;
                }

                EmitRunDeclaration();

                var symbol = lifted.Single(s => s.Declarator == declarator);
                var group = GroupOf(symbol);
                if (group.ArrayForm)
                {
                    generated.Add(_generator.BuildArrayStorageDeclaration(symbol));
                    if (declarator.Initializer is { Value: var init })
                        generated.Add(_generator.BuildArrayAssignment(symbol.StorageName, init));
                }
                else if (declarator.Initializer is { Value: var init })
                {
                    generated.Add(BuildInitialization(group, symbol, init));
                }
                // No initializer: nothing emitted — field zero-init matches the
                // definite-assignment rules every legal input already obeys.
            }

            EmitRunDeclaration();

            _statementReplacements[statement] = generated;

            void EmitRunDeclaration()
            {
                if (run.Count == 0)
                    return;
                var kept = statement.Declaration.WithVariables(SyntaxFactory.SeparatedList(run));
                generated.Add(statement.WithDeclaration(kept));
                run.Clear();
            }
        }
    }

    private StatementSyntax BuildInitialization(
        HolderGroupPlan group,
        CapturedSymbolPlan symbol,
        ExpressionSyntax value)
        => group.ArrayForm
            ? _generator.BuildArrayAssignment(symbol.StorageName, value)
            : _generator.BuildFieldAssignment(group.InstanceName, symbol.FieldName, value);

    // Group lookup helpers. Registration order is the plan order; a symbol
    // belongs to exactly one group.
    private readonly Dictionary<CapturedSymbolPlan, HolderGroupPlan> _groupBySymbol = new();

    private HolderGroupPlan GroupOf(CapturedSymbolPlan symbol) => _groupBySymbol[symbol];

    private static bool GroupHasParameters(HolderGroupPlan group)
        => group.Symbols.Any(s => s.Parameter != null);

    /// <summary>
    /// Instance anchor of a non-parameter group: the earliest source statement
    /// among lifted local declarations (Task 9) and enclosing for-loops (D9).
    /// Parameter groups anchor at routine body top and never query this.
    /// </summary>
    private StatementSyntax? EarliestAnchorStatement(HolderGroupPlan group)
    {
        StatementSyntax? earliest = group.Symbols
            .Where(s => s.Declarator?.Parent?.Parent is LocalDeclarationStatementSyntax)
            .OrderBy(s => s.Declarator!.SpanStart)
            .Select(s => (StatementSyntax)s.Declarator!.Parent!.Parent!)
            .FirstOrDefault();

        foreach (var loop in group.Symbols
                     .Where(s => s.ForLoop != null)
                     .Select(s => s.ForLoop!)
                     .Distinct())
        {
            if (earliest is null || loop.SpanStart < earliest.SpanStart)
                earliest = loop;
        }

        return earliest;
    }

    /// <summary>
    /// D9 for side. Builds the statement-list expansion per lifted for-loop:
    /// [group instance?][field/array initializers…][rebuilt for]. The rebuilt
    /// for keeps its ORIGINAL condition/incrementors/body nodes, so their site
    /// identifiers rewrite in place when the expansion is visited; the loop
    /// declaration only loses lifted declarators.
    /// </summary>
    private void BuildForLoopExpansions()
    {
        foreach (var (loop, lifted) in _liftedForDeclarators)
        {
            // Precondition contract #1: planner must diagnose CS2J3204 earlier.
            // Fail loud — dropping the instance here would silently split state.
            if (loop.Parent is not (BlockSyntax or SwitchSectionSyntax))
                throw new InvalidOperationException(
                    "C32: a for-loop with a lifted induction variable anchored " +
                    "outside a block/switch section must be rejected with " +
                    "CS2J3204 before rewriting (D9 boundary).");

            var ordered = lifted
                .OrderBy(s => s.Declarator!.SpanStart)
                .ToList();

            var generated = new List<StatementSyntax>();

            // Instance before the loop ONLY when this loop is the group's earliest
            // anchor. Parameter groups anchor at body top; local-anchor groups are
            // handled by BuildLocalDeclarationReplacements; array groups have no
            // holder instance.
            foreach (var group in ordered.Select(GroupOf).Distinct())
            {
                if (group.ArrayForm || GroupHasParameters(group))
                    continue;
                if (EarliestAnchorStatement(group) == loop)
                    generated.Add(_generator.BuildHolderInstanceDeclaration(group));
            }

            // Initializers relocate verbatim (original node identity → sites in
            // the table still rewrite after the move). No initializer: the field
            // zero-inits, matching a declarator-less for iteration variable.
            foreach (var symbol in ordered)
            {
                if (symbol.Declarator!.Initializer is { Value: var init })
                    generated.Add(BuildInitialization(GroupOf(symbol), symbol, init));
            }

            generated.Add(StripForDeclaration(loop, ordered));

            _statementReplacements[loop] = generated;
        }
    }

    /// <summary>
    /// Remove lifted declarators from the for declaration. All declarators in one
    /// for declaration share its type token: surviving declarators keep it; when
    /// none survive the declaration becomes null (for (; cond; incr)).
    /// </summary>
    private static ForStatementSyntax StripForDeclaration(
        ForStatementSyntax loop,
        IReadOnlyCollection<CapturedSymbolPlan> lifted)
    {
        if (loop.Declaration is not { } declaration)
            return loop; // Expression-form initializers carry no declarator.

        var liftedSet = new HashSet<VariableDeclaratorSyntax>(
            lifted.Select(s => s.Declarator!));

        var remaining = declaration.Variables
            .Where(d => !liftedSet.Contains(d))
            .ToList();

        if (remaining.Count == declaration.Variables.Count)
            return loop; // Defensive: nothing to strip.

        VariableDeclarationSyntax? rebuiltDeclaration = remaining.Count == 0
            ? null
            : declaration.WithVariables(SyntaxFactory.SeparatedList(remaining));

        return loop.WithDeclaration(rebuiltDeclaration);
    }

    private IEnumerable<HolderGroupPlan> GroupsWithLocalAnchor() => _groupBySymbol.Values.Distinct();
}
