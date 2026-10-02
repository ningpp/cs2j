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

    public CaptureSiteRewriter(
        IEnumerable<HolderGroupPlan> groups,
        ClosureLiftStatistics? statistics = null)
    {
        _statistics = statistics;

        foreach (var group in groups)
            RegisterGroup(group);

        BuildLocalDeclarationReplacements();
    }

    /// <summary>Number of identifier sites actually replaced during <see cref="Visit"/>.</summary>
    public int SitesRewritten { get; private set; }

    /// <summary>Step A: insert every holder class into its host type.</summary>
    public CompilationUnitSyntax InsertHolderClasses(CompilationUnitSyntax root)
    {
        if (_holderClasses.Count == 0)
            return root;

        return root.ReplaceNodes(
            _holderClasses.Keys,
            (original, _) => original.WithMembers(
                original.Members.AddRange(_holderClasses[original])));
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

    private BlockSyntax? RebuildStatementList(
        BlockSyntax original,
        SyntaxList<StatementSyntax> originalStatements,
        Func<BlockSyntax, BlockSyntax, BlockSyntax> attach)
    {
        var visitedStatements = VisitStatementList(originalStatements);
        if (!_blockPrepends.TryGetValue(original, out var prepend))
        {
            var rebuilt = attach(original, original.WithStatements(SyntaxFactory.List(visitedStatements)));
            return ReferenceEquals(rebuilt, original) ? null : rebuilt;
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
                ?? (group.HostMember as AccessorDeclarationSyntax)?.Body;
            // Planner (Task 11) guarantees a block body; expression-bodied
            // routines with mutable captures are rejected there (CS2J3204).
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

            if (body != null)
            {
                if (!_blockPrepends.TryGetValue(body, out var list))
                {
                    list = new List<StatementSyntax>();
                    _blockPrepends[body] = list;
                }
                list.AddRange(prepend);
            }
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
                if (EarliestLocalStatement(group) == statement)
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

    private LocalDeclarationStatementSyntax? EarliestLocalStatement(HolderGroupPlan group)
        => group.Symbols
            .Where(s => s.Declarator?.Parent?.Parent is LocalDeclarationStatementSyntax)
            .OrderBy(s => s.Declarator!.SpanStart)
            .Select(s => (LocalDeclarationStatementSyntax)s.Declarator!.Parent!.Parent!)
            .FirstOrDefault();

    private IEnumerable<HolderGroupPlan> GroupsWithLocalAnchor() => _groupBySymbol.Values.Distinct();
}
