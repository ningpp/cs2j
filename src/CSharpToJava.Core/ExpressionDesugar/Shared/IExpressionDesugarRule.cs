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
/// Minimal contract of an expression-desugar family rule. M0 rules only
/// analyze (count/classify); later milestones attach plans and a rewriter
/// while keeping this interface append-only.
/// </summary>
internal interface IExpressionDesugarRule
{
    /// <summary>Stable rule id (matches the rule directory/section name).</summary>
    string Id { get; }

    /// <summary>
    /// Cheap syntax-only dirty gate. When no rule reports a dirty file the
    /// family entry performs a byte-level clean copy without semantic work.
    /// </summary>
    bool IsPotentiallyDirty(CompilationUnitSyntax root);

    /// <summary>
    /// File-level analysis: classify sites, accumulate statistics, emit
    /// diagnostics. M0 does not rewrite; later phases consume the plans
    /// produced here in a single bottom-up rewrite pass.
    /// </summary>
    void Analyze(RuleAnalysisContext context);
}
