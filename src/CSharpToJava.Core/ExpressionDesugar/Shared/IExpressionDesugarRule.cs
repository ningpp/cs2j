using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar;

/// <summary>
/// File-level context handed to every <see cref="IExpressionDesugarRule"/>.
/// Wraps the semantic model (the single source of truth), family options,
/// accumulating family statistics and the diagnostic sink (design §4.1
/// data flow: Analyze runs once per file before any rewrite).
/// </summary>
internal sealed class RuleAnalysisContext
{
    public required CompilationUnitSyntax Root { get; init; }

    /// <summary>Null when the host runs without semantic information; rules must degrade safely.</summary>
    public required SemanticModel? SemanticModel { get; init; }

    public required ExpressionDesugarOptions Options { get; init; }
    public required ExpressionDesugarStatistics Statistics { get; init; }
    public required List<ExpressionDesugarDiagnostic> Diagnostics { get; init; }

    /// <summary>Reports a diagnostic pinned to <paramref name="node"/>'s mapped location.</summary>
    public void Report(
        ExpressionDesugarSeverity severity,
        string code,
        string message,
        SyntaxNode? node)
    {
        string? filePath = null;
        var line = 0;
        var column = 0;

        if (node != null)
        {
            var span = node.GetLocation().GetMappedLineSpan();
            filePath = node.SyntaxTree?.FilePath;
            line = span.StartLinePosition.Line + 1;
            column = span.StartLinePosition.Character + 1;
        }

        Diagnostics.Add(new ExpressionDesugarDiagnostic(
            severity, code, message, filePath, line, column));
    }
}

/// <summary>
/// Per-file product of a rule's Analyze pass (design §4.1): owns the plan data
/// and can run the single bottom-up rewrite, expose collected
/// <c>__ed_</c> pending statements for the family TempLifter and run the
/// post-lift residual gate. The family entry calls every rule in fixed order;
/// TempLifter.Apply runs exactly once after all rules have rewritten.
/// </summary>
internal abstract class ExpressionDesugarRulePlan
{
    /// <summary>
    /// Runs the rule's bottom-up rewriter against <paramref name="root"/>
    /// (semantic decisions are taken on original nodes; rendering uses the
    /// already-visited children). Returns the rewritten root.
    /// </summary>
    public abstract CompilationUnitSyntax Rewrite(CompilationUnitSyntax root);

    /// <summary>
    /// Optional stable-marker contribution applied to the shared root in ONE
    /// family-wide stamping pass BEFORE the first rule's <see cref="Rewrite"/>
    /// runs. Rules whose plans are keyed by original node references attach
    /// <see cref="SyntaxAnnotation"/> markers here: once two or more sites are
    /// rewritten anywhere in the tree Roslyn hands every traversed sibling a
    /// fresh node instance, so identity-keyed lookups across rules miss while
    /// annotations survive every subsequent rewrite.
    /// </summary>
    /// <param name="stamps">
    /// Original key node -> annotations to attach; multiple rules may target
    /// the same node. The family applies all contributions in a single walk.
    /// </param>
    public virtual void ContributeStamps(
        Dictionary<SyntaxNode, List<SyntaxAnnotation>> stamps)
    {
    }

    /// <summary>Statement sequences collected during <see cref="Rewrite"/>.</summary>
    public abstract IReadOnlyList<ExpressionDesugarTempLifter.PendingTemp> PendingTemps { get; }

    /// <summary>Original anchor node -> annotation map used by TempLifter.Apply.</summary>
    public abstract IReadOnlyDictionary<SyntaxNode, SyntaxAnnotation> AnchorAnnotations { get; }

    /// <summary>Number of sites actually rewritten (0 keeps the file byte-identical).</summary>
    public abstract int RewriteCount { get; }

    /// <summary>
    /// True when the plan carries sites intentionally left untouched by the
    /// primary rewrite pending the post-lift late pass (anchor-deferred sites).
    /// Keeps the family pipeline past the zero-rewrite early return so the late
    /// pass can lower them or emit their diagnostic.
    /// </summary>
    public virtual bool HasDeferredSites => false;

    /// <summary>
    /// Optional SECOND rewrite run against the tree AFTER the family-wide
    /// <see cref="ExpressionDesugarTempLifter.Apply"/>. Sites that traveled
    /// inside another rule's pending statement sequences are invisible to the
    /// primary <see cref="Rewrite"/> (the sequences are still in memory then)
    /// and become reachable only once they are spliced into the tree. Returns
    /// null when the rule has no late contribution. Pending sequences produced
    /// here are exposed via <see cref="LatePendingTemps"/> and lifted by one
    /// additional family-wide lift pass.
    /// </summary>
    public virtual CompilationUnitSyntax? LateRewriteAfterLift(
        CompilationUnitSyntax root)
        => null;

    /// <summary>Statement sequences collected during <see cref="LateRewriteAfterLift"/>.</summary>
    public virtual IReadOnlyList<ExpressionDesugarTempLifter.PendingTemp> LatePendingTemps
        => Array.Empty<ExpressionDesugarTempLifter.PendingTemp>();

    /// <summary>
    /// CURRENT post-lift anchor node -> annotation map for the late pending
    /// sequences (late anchors cannot reuse analyze-time anchors, which may
    /// precede the inserted sequences or have been replaced).
    /// </summary>
    public virtual IReadOnlyDictionary<SyntaxNode, SyntaxAnnotation> LateAnchorAnnotations
        => new Dictionary<SyntaxNode, SyntaxAnnotation>();

    /// <summary>
    /// Post-lift residual gate: every trigger node left in the tree must be
    /// paired with an explicit skip (explained annotation/node-diagnostic
    /// mapping). Returns the number of unpaired residuals and reports the
    /// gate diagnostic (strict Error / non-strict Warning) when non-zero.
    /// </summary>
    public abstract int CheckResiduals(RuleAnalysisContext context, CompilationUnitSyntax root);
}

/// <summary>
/// Minimal contract of an expression-desugar family rule. Analyze runs first
/// for every rule (plans + diagnostics; strict errors abort the file atomically
/// before any rewrite); the returned plan then drives one bottom-up rewrite.
/// The interface stays append-only for the future C23/C24/C35 rules.
/// </summary>
internal interface IExpressionDesugarRule
{
    /// <summary>Stable rule id (matches the rule directory/section name).</summary>
    string Id { get; }

    /// <summary>
    /// Cheap syntax-only dirty gate over the shared per-file
    /// <see cref="ExpressionDesugarSyntaxProfile"/>. When no rule reports a
    /// dirty file the family entry performs a byte-level clean copy without
    /// semantic work. The profile is produced by ONE
    /// <see cref="ExpressionDesugarTriggerScan.Scan"/> traversal per file (the
    /// gate must not walk the tree itself).
    /// <paramref name="options"/> lets rule-level gray-release switches (C23
    /// is opt-in inside the default-enabled family) report clean while staying
    /// syntax-only.
    /// </summary>
    bool IsPotentiallyDirty(
        ExpressionDesugarSyntaxProfile profile,
        ExpressionDesugarOptions options);

    /// <summary>
    /// File-level analysis: classify sites, accumulate statistics, emit
    /// diagnostics and produce the rewrite plan. Under strict options any
    /// Error diagnostic makes the family entry return the file unchanged.
    /// </summary>
    ExpressionDesugarRulePlan Analyze(RuleAnalysisContext context);
}
