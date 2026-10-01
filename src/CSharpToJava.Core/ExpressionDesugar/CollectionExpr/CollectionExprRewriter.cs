using Microsoft.CodeAnalysis.CSharp;
using CSharpToJava.Core.ExpressionDesugar;

namespace CSharpToJava.Core.ExpressionDesugar.CollectionExpr;

/// <summary>
/// Bottom-up rewriter for C36 (design §4.1 step 2): inner collection
/// expressions are expanded first; zero-temp forms replace the node directly,
/// statement-sequence forms register <see cref="ExpressionDesugarTempLifter.PendingTemp"/>
/// entries at the anchor found by the shared lifter.
///
/// M0: identity rewriter skeleton — no Visit* overrides, so running it returns
/// the tree unchanged. M1 adds VisitCollectionExpression.
/// </summary>
internal sealed class CollectionExprRewriter : CSharpSyntaxRewriter
{
    private readonly RuleAnalysisContext _context;

    public CollectionExprRewriter(RuleAnalysisContext context)
    {
        _context = context;
    }

    /// <summary>Statement sequences collected for the family TempLifter (M1).</summary>
    public List<ExpressionDesugarTempLifter.PendingTemp> Pending { get; } = new();

    /// <summary>Anchor node -> annotation map consumed by TempLifter.Apply (M1).</summary>
    public Dictionary<Microsoft.CodeAnalysis.SyntaxNode, Microsoft.CodeAnalysis.SyntaxAnnotation>
        AnchorAnnotations { get; } = new();

    /// <summary>Number of sites actually rewritten (drives the family dirty/no-rewrite gate).</summary>
    public int RewriteCount { get; private set; }

    internal void IncrementRewriteCount() => RewriteCount++;
}
