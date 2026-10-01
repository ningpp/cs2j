using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.Core.ForeachSource;

/// <summary>
/// C# syntax rewriter for the foreach-source pass (C16). Holds the ordered
/// rule list and applies the FS-4 guards (ref/await keywords, missing model,
/// error/dynamic binding) before dispatch. Children are visited first so
/// nested foreach sites are lowered bottom-up (same discipline as E07).
/// </summary>
internal sealed class ForeachSourceRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel? _model;
    private readonly ForeachSourceOptions _options;
    private readonly ForeachSourceStatistics _stats;
    private readonly List<ForeachSourceDiagnostic> _diagnostics;
    private readonly IReadOnlyList<IForeachSourceRule> _rules;
    private readonly ForeachSourceRewriteContext _context;

    public bool Changed => _context.Changed;
    public IReadOnlyList<ForeachSourceDiagnostic> Diagnostics => _diagnostics;

    public ForeachSourceRewriter(
        SemanticModel? model,
        ForeachSourceOptions options,
        ForeachSourceStatistics stats,
        List<ForeachSourceDiagnostic> diagnostics)
    {
        _model = model;
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
        _context = new ForeachSourceRewriteContext
        {
            Options = options,
            Statistics = stats,
            Diagnostics = diagnostics,
        };
        // Ordered dispatch. M2 rules for deconstruction residuals; C40 will
        // append its EnumeratorAdapter/RefForeach rules at the reserved slots.
        _rules = new IForeachSourceRule[]
        {
            new StringSourceRule(),
            new TupleResidualRule(),
            new DeconstructResidualRule(),
            new OtherResidualRule(),
        };
    }

    public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
    {
        var visited = (ForEachStatementSyntax?)base.VisitForEachStatement(node);
        if (visited == null)
            return visited;

        // FS-4 syntactic guards (C40/C38 surfaces): count and leave untouched.
        // RefKeyword only exists on the plain form; ref deconstruction foreach is not legal.
        if (ForeachSourceAnalyzer.HasRefKeyword(visited))
        {
            _stats.RefForeachSeen++;
            _context.Report(
                ForeachSourceSeverity.Info,
                PreprocessDiagnosticCodes.ForeachSourceRefForeach,
                "[FS-4] ref/ref-readonly foreach is not lowered by foreach-source (reserved for C40).",
                visited);
            return visited;
        }
        if (ForeachSourceAnalyzer.HasAwaitKeyword(visited))
        {
            _stats.AwaitForeachSeen++;
            _context.Report(
                ForeachSourceSeverity.Info,
                PreprocessDiagnosticCodes.ForeachSourceAwaitForeach,
                "[FS-4] await foreach is not lowered by foreach-source (belongs to the C38 async-normalize family).",
                visited);
            return visited;
        }

        if (_model == null)
        {
            _stats.SitesPassedThrough++;
            return visited;
        }

        var expressionType = ForeachSourceAnalyzer.GetExpressionType(_model, visited.Expression);
        if (expressionType == null
            || ForeachSourceAnalyzer.IsErrorType(expressionType)
            || ForeachSourceAnalyzer.IsDynamic(expressionType))
        {
            _stats.SitesPassedThrough++;
            return visited;
        }

        var site = new ForeachSourceSite
        {
            Node = visited,
            Model = _model,
            Options = _options,
            ExpressionType = expressionType,
        };

        foreach (var rule in _rules)
        {
            var outcome = rule.TryHandle(site, _context, visited);
            if (outcome == null)
                continue;

            if (!outcome.Changed || outcome.Output == null)
            {
                // Matched but intentionally kept (diagnose-only rules land here).
                RecordPlainResidual(visited, expressionType);
                return visited;
            }

            return outcome.Output;
        }

        // No rule claimed the site: ordinary non-string source — out of scope.
        _stats.SitesPassedThrough++;
        return visited;
    }

    public override SyntaxNode? VisitForEachVariableStatement(ForEachVariableStatementSyntax node)
    {
        var visited = (ForEachVariableStatementSyntax?)base.VisitForEachVariableStatement(node);
        if (visited == null)
            return visited;

        // FS-4 await guard (ref deconstruction foreach is not legal C#).
        if (ForeachSourceAnalyzer.HasAwaitKeyword(visited))
        {
            _stats.AwaitForeachSeen++;
            _context.Report(
                ForeachSourceSeverity.Info,
                PreprocessDiagnosticCodes.ForeachSourceAwaitForeach,
                "[FS-4] await deconstruction foreach is not lowered by foreach-source (belongs to the C38 async-normalize family).",
                visited);
            return visited;
        }

        if (_model == null)
        {
            _stats.SitesPassedThrough++;
            return visited;
        }

        var expressionType = ForeachSourceAnalyzer.GetExpressionType(_model, visited.Expression);
        if (expressionType == null
            || ForeachSourceAnalyzer.IsErrorType(expressionType)
            || ForeachSourceAnalyzer.IsDynamic(expressionType))
        {
            _stats.SitesPassedThrough++;
            return visited;
        }

        var elementType = ForeachSourceAnalyzer.TryGetForEachElementType(_model, visited);
        if (elementType == null)
        {
            _stats.SitesPassedThrough++;
            return visited;
        }

        var family = ForeachSourceAnalyzer.ClassifyFamily(elementType);
        var site = new ForeachSourceSite
        {
            Node = visited,
            Model = _model,
            Options = _options,
            ExpressionType = expressionType,
            ElementType = elementType,
            Family = family,
        };

        foreach (var rule in _rules)
        {
            var outcome = rule.TryHandle(site, _context, visited);
            if (outcome == null)
                continue;

            // Deconstruction rules either rewrite (instance Deconstruct) or
            // terminate dispatch with the node kept (residual diagnostics).
            return outcome.Output ?? visited;
        }

        _stats.SitesPassedThrough++;
        return visited;
    }

    /// <summary>
    /// Defensive residual bookkeeping on a visited plain foreach. A string
    /// typed site that a rule matched without rewriting would survive in the
    /// output tree; FS-1 always rewrites, so this counter stays zero today.
    /// </summary>
    private void RecordPlainResidual(ForEachStatementSyntax visited, ITypeSymbol expressionType)
    {
        if (ForeachSourceAnalyzer.IsString(expressionType))
            _stats.ResidualStringSites++;
    }
}
