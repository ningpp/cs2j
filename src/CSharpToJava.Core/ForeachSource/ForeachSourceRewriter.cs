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
    private readonly IReadOnlyList<IForeachSourceRule> _refRules;
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
        // Ordered dispatch (design §3.2): C16 rules first, then C40 FA rules;
        // EnumeratorAdapterRule explicitly excludes Span sources so the
        // following SpanIndexLoopRule owns them. M3 adds the FR tail for
        // ref/ref-readonly sites (ref sites never reach the plain rules).
        _rules = new IForeachSourceRule[]
        {
            new StringSourceRule(),
            new TupleResidualRule(),
            new DeconstructResidualRule(),
            new OtherResidualRule(),
            new EnumeratorAdapterRule(),
            new SpanIndexLoopRule(),
        };
        // M3 FR chain: indexable sources (arrays/Span/custom) win over the
        // ref-Current enumerator form — RefIndexLoopRule also terminates
        // unbindable ref sites with CS2J5485, deferring only FR-3-eligible
        // sites to RefCurrentEnumeratorRule.
        _refRules = new IForeachSourceRule[]
        {
            new RefIndexLoopRule(),
            new RefCurrentEnumeratorRule(),
        };
    }

    public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
    {
        var visited = (ForEachStatementSyntax?)base.VisitForEachStatement(node);
        if (visited == null)
            return visited;

        // FS-4 syntactic guards (C40/C38 surfaces): await sites are counted
        // and left untouched. Ref/ref-readonly foreach is M3 FR territory —
        // it flows through the semantic FR chain below instead of the old
        // CS2J5441 early return. RefKeyword only exists on the plain form;
        // ref deconstruction foreach is not legal.
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

        // Semantic classification always goes through the ORIGINAL node:
        // children were rewritten first, so `visited` may be a rebuilt,
        // detached node on which model lookups return empty bindings (nested
        // foreach, e.g. outer duck / inner span). Code generation in the rules
        // uses the visited node so already-lowered children are kept.
        var expressionType = ForeachSourceAnalyzer.GetExpressionType(_model, node.Expression);
        if (expressionType == null
            || ForeachSourceAnalyzer.IsErrorType(expressionType))
        {
            _stats.SitesPassedThrough++;
            return visited;
        }

        // C40: dynamic sites are no longer filtered here — EnumeratorAdapterRule
        // emits CS2J5456 and keeps the node (decision 11). Only error types and
        // missing bindings pass through early.
        var binding = ForeachBindingResolver.Resolve(node, _model);

        var site = new ForeachSourceSite
        {
            Node = visited,
            OriginalNode = node,
            Model = _model,
            Options = _options,
            ExpressionType = expressionType,
            RefKind = binding?.RefKind ?? Microsoft.CodeAnalysis.RefKind.None,
            Binding = binding,
        };

        // M3: ref/ref-readonly sites dispatch through the FR chain only —
        // string/deconstruction/FA rules must never see them.
        if (site.RefKind != Microsoft.CodeAnalysis.RefKind.None)
        {
            foreach (var rule in _refRules)
            {
                var outcome = rule.TryHandle(site, _context, visited);
                if (outcome == null)
                {
                    continue;
                }

                if (!outcome.Changed || outcome.Output == null)
                {
                    // Diagnose-only termination (CS2J5485 Error, CS2J5456
                    // dynamic): the node intentionally survives; the terminal
                    // diagnostic already recorded the site, so no 5495 double
                    // count.
                    return visited;
                }

                // CS2J5486 conservative pre-scan on the ORIGINAL body: a
                // lambda/anonymous method/local function referencing the
                // iteration variable cannot survive the lowering (the
                // per-iteration local becomes ref/ref-struct-scoped).
                ReportRefIterationCaptureIfAny(node);
                return outcome.Output;
            }

            // Defensive escape (every unbindable shape is normally terminated
            // by CS2J5485): keeps the FS-5 ref gate honest.
            _stats.ResidualRefSites++;
            return visited;
        }

        foreach (var rule in _rules)
        {
            var outcome = rule.TryHandle(site, _context, visited);
            if (outcome == null)
                continue;

            if (!outcome.Changed || outcome.Output == null)
            {
                // Matched but intentionally kept (diagnose-only rules land here:
                // dynamic CS2J5456, unresolved CS2J5457, C16 residuals).
                RecordPlainResidual(visited, expressionType);
                return visited;
            }

            return outcome.Output;
        }

        // No rule claimed the site. FS-5 CS2J5470 escape bucket: a
        // non-array/non-string/non-IEnumerable plain foreach with a valid
        // pattern-enumerator binding that still went unlowered. Counting here
        // (same discipline as C16 FS-5 — the output tree is never parsed a
        // second time) keeps the gate honest even if a future rule gap lets a
        // valid site through; ordinary converter-owned sources (arrays,
        // List/Dictionary, …) stay plain pass-throughs.
        if (ForeachSourceAnalyzer.IsResidualEnumeratorCandidate(
                expressionType, binding, _model.Compilation))
        {
            _stats.ResidualEnumeratorSites++;
        }
        else
        {
            _stats.SitesPassedThrough++;
        }
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

    /// <summary>
    /// CS2J5486 conservative capture scan (design FR notes): after an FR
    /// lowering, the iteration variable is a per-iteration ref/ref-struct
    /// local, which cannot be captured by a lambda, anonymous method or a
    /// local function that gets converted to a delegate. The scan runs on the
    /// ORIGINAL body (semantic queries need the model-bound tree) and matches
    /// identifier references that bind to the foreach iteration local symbol,
    /// so shadowed names inside lambdas do not false-positive. One diagnostic
    /// per site; Warning by default, Error in strict mode.
    /// </summary>
    private void ReportRefIterationCaptureIfAny(ForEachStatementSyntax originalNode)
    {
        if (_model == null)
        {
            return;
        }

        var iterationLocal = _model.GetDeclaredSymbol(originalNode) as Microsoft.CodeAnalysis.ILocalSymbol;
        if (iterationLocal == null)
        {
            return;
        }

        foreach (var root in originalNode.Statement.DescendantNodesAndSelf())
        {
            if (root is not (LambdaExpressionSyntax
                or AnonymousMethodExpressionSyntax
                or LocalFunctionStatementSyntax))
            {
                continue;
            }

            foreach (var id in root.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                Microsoft.CodeAnalysis.ISymbol? referenced;
                try
                {
                    referenced = _model.GetSymbolInfo(id).Symbol;
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (referenced != null
                    && Microsoft.CodeAnalysis.SymbolEqualityComparer.Default.Equals(
                        referenced, iterationLocal))
                {
                    var severity = _options.Strict
                        ? ForeachSourceSeverity.Error
                        : ForeachSourceSeverity.Warning;
                    _context.Report(
                        severity,
                        PreprocessDiagnosticCodes.ForeachSourceRefIterationCapture,
                        "[FR] lowered ref/ref-readonly foreach body references the iteration variable " +
                        $"'{iterationLocal.Name}' inside a lambda/anonymous method/local function; the lowered " +
                        "per-iteration ref/ref-struct local cannot cross that boundary.",
                        root);
                    return;
                }
            }
        }
    }
}
