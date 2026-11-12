using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.ConditionEffect;

/// <summary>
/// Condition-effect rule (J-04 / G-36 / JE-04): linearizes property/indexer
/// side effects out of <c>while</c>/<c>if</c> conditions into the canonical
/// statement form, so the Java side no longer has to rebuild the condition's
/// control flow at text-emission time (the
/// <c>while (++start.Value &lt; _length)</c> infinite loop, a2d3a6f0).
///
/// <para>
/// Canonical forms:
/// <code>
/// while (C) BODY          => while (true) { bool n = C; if (!n) break; BODY }
/// if (C) THEN [else E]    => { bool n = C; if (n) THEN [else E] }
/// for (I; C; U) BODY      => { I; while (true) { bool n = C; if (!n) break;
///                              BODY; U } }            // continue => { U; continue; }
/// </code>
/// An <c>if</c> already directly in a block/switch-section statement list only
/// receives the declaration before it (no extra block). Only <c>while</c>,
/// <c>if</c> and <c>for</c> are handled; <c>do</c>/<c>?:</c> and short-circuit
/// sub-expressions are later slices. Conditions with declaration expressions,
/// pattern variables or <c>await</c> are preserved with a Warning.
/// </para>
///
/// <para>
/// Registered LAST in the family rule order: earlier rules may rewrite the
/// condition or body, and this rule must run against the final tree.
/// </para>
/// </summary>
internal sealed class ConditionEffectRule : IExpressionDesugarRule
{
    public string Id => "ConditionEffect";

    public bool IsPotentiallyDirty(
        ExpressionDesugarSyntaxProfile profile,
        ExpressionDesugarOptions options)
        => options.ConditionEffect is { Enabled: true }
           && profile.HasConditionSideEffectCandidate;

    public ExpressionDesugarRulePlan Analyze(RuleAnalysisContext context)
    {
        if (context.Options.ConditionEffect is not { Enabled: true }
            || context.SemanticModel is not { } model)
        {
            return new ConditionEffectRulePlan(
                context, Array.Empty<ConditionEffectPlan>());
        }

        var plans = new List<ConditionEffectPlan>();
        var allocator = new ConditionEffectNameAllocator(context.Root);

        foreach (var node in context.Root.DescendantNodes())
        {
            ExpressionSyntax? condition = node switch
            {
                WhileStatementSyntax whileStatement => whileStatement.Condition,
                IfStatementSyntax ifStatement => ifStatement.Condition,
                ForStatementSyntax forLoop => forLoop.Condition,
                _ => null,
            };

            if (condition == null
                || !ConditionEffectSyntaxFacts.HasCandidateSyntax(condition)
                || ConditionEffectSyntaxFacts.IsLiteralCondition(condition))
            {
                continue;
            }

            if (ConditionEffectSyntaxFacts.ContainsUnsupportedShape(condition)
                || ConditionEffectSyntaxFacts.ContainsOutVariableArgument(condition))
            {
                ReportSkipped(context, condition,
                    "declaration expression, pattern variable, await or a " +
                    "non-discard out argument (definite assignment would be lost)");
                continue;
            }

            if (node is ForStatementSyntax { Incrementors.Count: > 0 } forStatement
                && ConditionEffectForLoopFacts.ContainsContinueInCapturedContext(
                    forStatement.Statement))
            {
                // Moving the incrementors in front of a `continue` that runs
                // through try/using/lock/checked would reorder them relative to
                // the finally/dispose/exit; preserve the site (Warning).
                ReportSkipped(context, condition,
                    "a continue nested in try/using/lock/checked would run the " +
                    "for incrementors in a different order");
                continue;
            }

            var isCandidate = false;
            try
            {
                isCandidate = ConditionEffectSyntaxFacts.HasSemanticCandidate(
                    condition, model);
            }
            catch (ArgumentException)
            {
                ReportSkipped(context, condition, "semantic binding failed");
                continue;
            }

            if (!isCandidate)
                continue;

            plans.Add(new ConditionEffectPlan
            {
                Node = (StatementSyntax)node,
                TempName = allocator.Allocate(),
            });
        }

        return new ConditionEffectRulePlan(context, plans);
    }

    private static void ReportSkipped(
        RuleAnalysisContext context, ExpressionSyntax condition, string reason)
    {
        context.Report(
            ExpressionDesugarSeverity.Warning,
            ConditionEffectDiagnosticCodes.CS2J0590,
            string.Format(
                ConditionEffectDiagnostics.SkippedMessage,
                condition.ToString(), reason),
            condition);
        context.Statistics.ConditionEffectSkipped++;
    }
}

/// <summary>Analyze product for one triggering statement.</summary>
internal sealed class ConditionEffectPlan
{
    public required StatementSyntax Node { get; init; }

    public required string TempName { get; init; }
}

/// <summary>Plan: owns the site markers and runs the statement-level rewrite.</summary>
internal sealed class ConditionEffectRulePlan : ExpressionDesugarRulePlan
{
    private const string MarkerKind = "ed-condition-effect-site";

    private readonly RuleSiteMarkers<ConditionEffectPlan> _markers =
        new(MarkerKind);

    private readonly ExpressionDesugarStatistics _statistics;

    private ConditionEffectRewriter? _rewriter;

    public ConditionEffectRulePlan(
        RuleAnalysisContext context, IReadOnlyList<ConditionEffectPlan> plans)
    {
        _statistics = context.Statistics;
        foreach (var plan in plans)
            _markers.Add(plan.Node, plan);
    }

    public override void ContributeStamps(
        Dictionary<SyntaxNode, List<SyntaxAnnotation>> stamps)
        => _markers.ContributeTo(stamps);

    public override CompilationUnitSyntax Rewrite(CompilationUnitSyntax root)
    {
        _rewriter = new ConditionEffectRewriter(_markers, _statistics);
        return (CompilationUnitSyntax)_rewriter.Visit(root)!;
    }

    public override IReadOnlyList<ExpressionDesugarTempLifter.PendingTemp>
        PendingTemps
        => Array.Empty<ExpressionDesugarTempLifter.PendingTemp>();

    public override IReadOnlyDictionary<SyntaxNode, SyntaxAnnotation>
        AnchorAnnotations
        => new Dictionary<SyntaxNode, SyntaxAnnotation>();

    public override int RewriteCount => _rewriter?.RewriteCount ?? 0;

    /// <summary>Skipped conditions are Warning-paired, not residual triggers.</summary>
    public override int CheckResiduals(
        RuleAnalysisContext context, CompilationUnitSyntax root)
        => 0;
}

/// <summary>
/// Statement-level rewriter. <c>while</c> is replaced in one step; an
/// <c>if</c> directly in a block/switch-section list receives its declaration
/// from <see cref="VisitBlock"/>/<see cref="VisitSwitchSection"/>, while an
/// embedded <c>if</c> is wrapped in a dedicated block.
/// </summary>
internal sealed class ConditionEffectRewriter : CSharpSyntaxRewriter
{
    private readonly RuleSiteMarkers<ConditionEffectPlan> _markers;
    private readonly ExpressionDesugarStatistics _statistics;

    public ConditionEffectRewriter(
        RuleSiteMarkers<ConditionEffectPlan> markers,
        ExpressionDesugarStatistics statistics)
    {
        _markers = markers;
        _statistics = statistics;
    }

    public int RewriteCount { get; private set; }

    public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
    {
        var visited = (WhileStatementSyntax)base.VisitWhileStatement(node)!;
        if (_markers.TryResolve(visited) is not { } plan
            || !IsStillCandidate(visited.Condition))
        {
            return visited;
        }

        var statements = new List<StatementSyntax>
        {
            DeclareCondition(plan.TempName, visited.Condition),
            SyntaxFactory.IfStatement(
                SyntaxFactory.PrefixUnaryExpression(
                    SyntaxKind.LogicalNotExpression,
                    SyntaxFactory.IdentifierName(plan.TempName)),
                SyntaxFactory.BreakStatement()),
            // The original body stays ONE nested statement: flattening a block
            // body into the new loop block would move its local declarations
            // into the same declaration space as the condition temp (a body
            // local shadowing a field used by the condition would then make the
            // temp's initializer bind to the local: CS0841).
            visited.Statement,
        };

        var rewritten = SyntaxFactory.WhileStatement(
            SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression),
            SyntaxFactory.Block(SyntaxFactory.List(statements)));

        RewriteCount++;
        _statistics.ConditionEffectRewrites++;
        return rewritten.WithLeadingTrivia(visited.GetLeadingTrivia());
    }

    /// <summary>
    /// <c>for (I; C; U) BODY</c> => <c>{ I; while (true) { bool n = C;
    /// if (!n) break; BODY; U } }</c> with every <c>continue</c> bound to this
    /// loop prefixed by <c>U</c>. The original body stays ONE nested statement
    /// so its locals cannot capture the condition temp's initializer.
    /// </summary>
    public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
    {
        var visited = (ForStatementSyntax)base.VisitForStatement(node)!;
        if (visited.Condition is not { } condition
            || _markers.TryResolve(visited) is not { } plan
            || !IsStillCandidate(condition))
        {
            return visited;
        }

        var increments = ConditionEffectForLoopFacts.BuildIncrementStatements(visited);
        var body = increments.Count > 0
            ? ConditionEffectForLoopFacts.RewriteContinues(visited.Statement, increments)
            : visited.Statement;

        var loopBody = new List<StatementSyntax>
        {
            DeclareCondition(plan.TempName, condition),
            SyntaxFactory.IfStatement(
                SyntaxFactory.PrefixUnaryExpression(
                    SyntaxKind.LogicalNotExpression,
                    SyntaxFactory.IdentifierName(plan.TempName)),
                SyntaxFactory.BreakStatement()),
            body,
        };
        if (increments.Count > 0
            && !ConditionEffectForLoopFacts.EndsWithUnconditionalTransfer(body))
        {
            loopBody.AddRange(increments);
        }

        var statements = ConditionEffectForLoopFacts.BuildInitializerStatements(visited);
        statements.Add(SyntaxFactory.WhileStatement(
            SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression),
            SyntaxFactory.Block(SyntaxFactory.List(loopBody))));

        RewriteCount++;
        _statistics.ConditionEffectRewrites++;
        return SyntaxFactory.Block(SyntaxFactory.List(statements))
            .WithLeadingTrivia(visited.GetLeadingTrivia());
    }

    public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
    {
        var visited = (IfStatementSyntax)base.VisitIfStatement(node)!;

        // Direct block/switch-section statements are handled by the list pass,
        // which can insert the declaration without wrapping. The ORIGINAL
        // node's parent is checked because `base.VisitIfStatement` may return a
        // rebuilt node whose Parent is not attached yet.
        if (node.Parent is BlockSyntax or SwitchSectionSyntax)
            return visited;

        if (_markers.TryResolve(visited) is not { } plan
            || !IsStillCandidate(visited.Condition))
        {
            return visited;
        }

        var lifted = BuildLiftedIf(plan, visited).WithoutLeadingTrivia();
        var block = SyntaxFactory.Block(
            DeclareCondition(plan.TempName, visited.Condition),
            lifted);

        RewriteCount++;
        _statistics.ConditionEffectRewrites++;
        return block.WithLeadingTrivia(visited.GetLeadingTrivia());
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        var visited = (BlockSyntax)base.VisitBlock(node)!;
        var rewritten = RewriteStatementList(visited.Statements);
        return rewritten == null
            ? visited
            : visited.WithStatements(SyntaxFactory.List(rewritten));
    }

    public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
    {
        var visited = (SwitchSectionSyntax)base.VisitSwitchSection(node)!;
        var rewritten = RewriteStatementList(visited.Statements);
        return rewritten == null
            ? visited
            : visited.WithStatements(SyntaxFactory.List(rewritten));
    }

    private List<StatementSyntax>? RewriteStatementList(
        SyntaxList<StatementSyntax> statements)
    {
        List<StatementSyntax>? rewritten = null;

        for (var i = 0; i < statements.Count; i++)
        {
            var statement = statements[i];

            if (statement is IfStatementSyntax ifStatement
                && _markers.TryResolve(ifStatement) is { } plan
                && IsStillCandidate(ifStatement.Condition))
            {
                if (rewritten == null)
                {
                    rewritten = new List<StatementSyntax>(statements.Count + 1);
                    for (var j = 0; j < i; j++)
                        rewritten.Add(statements[j]);
                }

                rewritten.Add(DeclareCondition(plan.TempName, ifStatement.Condition));
                rewritten.Add(BuildLiftedIf(plan, ifStatement));
                RewriteCount++;
                _statistics.ConditionEffectRewrites++;
                continue;
            }

            rewritten?.Add(statement);
        }

        return rewritten;
    }

    private static IfStatementSyntax BuildLiftedIf(
        ConditionEffectPlan plan, IfStatementSyntax node)
        => node.WithCondition(SyntaxFactory.IdentifierName(plan.TempName));

    /// <summary>
    /// <c>bool __ed_cond_N = C;</c>. The condition must still be a plain bool
    /// expression (declaration/pattern/await conditions are skipped).
    /// </summary>
    private static LocalDeclarationStatementSyntax DeclareCondition(
        string name, ExpressionSyntax condition)
        => SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.PredefinedType(
                    SyntaxFactory.Token(SyntaxKind.BoolKeyword)),
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(
                        SyntaxFactory.Identifier(name),
                        null,
                        SyntaxFactory.EqualsValueClause(condition)))));

    /// <summary>
    /// Re-checks the CURRENT condition (earlier rules may have rewritten it).
    /// <c>__ed_cond_N</c> re-runs are pure identifiers and therefore inert.
    /// </summary>
    private static bool IsStillCandidate(ExpressionSyntax condition)
        => !ConditionEffectSyntaxFacts.IsLiteralCondition(condition)
           && !ConditionEffectSyntaxFacts.ContainsUnsupportedShape(condition)
           && !ConditionEffectSyntaxFacts.ContainsOutVariableArgument(condition)
           && ConditionEffectSyntaxFacts.HasCandidateSyntax(condition);
}

/// <summary>Allocates file-unique <c>__ed_cond_N</c> names.</summary>
internal sealed class ConditionEffectNameAllocator
{
    private readonly HashSet<string> _used;
    private int _counter;

    public ConditionEffectNameAllocator(CompilationUnitSyntax root)
    {
        _used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in root.DescendantTokens())
            _used.Add(token.ValueText);
    }

    public string Allocate()
    {
        while (true)
        {
            var name = $"__ed_cond_{_counter}";
            _counter++;
            if (_used.Add(name))
                return name;
        }
    }
}
