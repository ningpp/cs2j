using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.PatternDesugar;

internal sealed class PatternDesugarRewriteContext
{
    public required IReadOnlyDictionary<SwitchStatementSyntax, SwitchLoweringPlan> SwitchPlans { get; init; }
    public required IReadOnlyDictionary<TryStatementSyntax, CatchLoweringPlan> CatchPlans { get; init; }
    public required PatternDesugarOptions Options { get; init; }
    public required PatternDesugarStatistics Statistics { get; init; }
    public required PatternDesugarPlanBuilder Builder { get; init; }
    public required SemanticModel? Model { get; init; }
    public required string? FilePath { get; init; }

    /// <summary>Back-reference set by the rewriter so components can recurse into nested nodes.</summary>
    public required PatternDesugarRewriter Rewriter { get; set; }

    public PatternReductionContext ReductionContext() =>
        new(Model, Options, Builder.Diagnostics, FilePath, Builder.Names.Next);
}

/// <summary>
/// Top-down rewriter over the ORIGINAL tree: plans are keyed by original nodes,
/// so ancestor walks for break ownership stay intact. Components intercept
/// planned nodes; everything else descends normally.
/// </summary>
internal sealed class PatternDesugarRewriter : CSharpSyntaxRewriter
{
    private readonly PatternDesugarRewriteContext _ctx;
    private readonly IReadOnlyList<IPatternDesugarComponent> _components;
    private readonly Stack<(SwitchStatementSyntax Owner, string EndLabel)> _breakOwners = new();

    public PatternDesugarRewriter(
        PatternDesugarRewriteContext ctx,
        IReadOnlyList<IPatternDesugarComponent> components)
    {
        _ctx = ctx;
        _components = components;
        _ctx.Rewriter = this;
    }

    public override SyntaxNode? Visit(SyntaxNode? node)
    {
        if (node is SwitchStatementSyntax sw && _ctx.SwitchPlans.TryGetValue(sw, out var plan))
        {
            // The pushed switch owns breaks in its section bodies (D12); nested
            // planned switches push their own frame while their sections descend.
            _breakOwners.Push((sw, plan.EndLabelName));
            try
            {
                var replacement = Dispatch(node);
                if (replacement != null)
                    return replacement;
            }
            finally
            {
                _breakOwners.Pop();
            }
        }
        else if (node is TryStatementSyntax tryNode && _ctx.CatchPlans.ContainsKey(tryNode))
        {
            var replacement = Dispatch(node);
            if (replacement != null)
                return replacement;
        }

        return base.Visit(node);
    }

    public override SyntaxNode? VisitBreakStatement(BreakStatementSyntax node)
    {
        // Only breaks whose nearest enclosing switch/loop is the lowered switch
        // being rewritten become gotos (D12) — nested constructs keep their own.
        if (_breakOwners.Count > 0)
        {
            var (owner, endLabel) = _breakOwners.Peek();
            if (PatternDesugarAnalysis.BreakBelongsToSwitch(node, owner))
                return SyntaxFactory.GotoStatement(
                    SyntaxKind.GotoStatement, SyntaxFactory.IdentifierName(endLabel));
        }
        return node;
    }

    private SyntaxNode? Dispatch(SyntaxNode node)
    {
        foreach (var component in _components)
        {
            var replacement = component.Rewrite(node, _ctx);
            if (replacement != null)
                return replacement;
        }
        return null;
    }
}
