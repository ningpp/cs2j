using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ForeachSource;

/// <summary>Deconstruction element family classification (ported from E07, design decision 5).</summary>
internal enum ForeachSourceFamily
{
    KeyValuePair,
    Tuple,
    Deconstruct,
    Other,
}

/// <summary>
/// Per-site context gathered by <see cref="ForeachSourceAnalyzer"/> for one
/// <see cref="CommonForEachStatementSyntax"/> node. Guards (ref/await, no
/// model, error/dynamic types) are evaluated before any rule sees the site.
/// </summary>
internal sealed class ForeachSourceSite
{
    public required CommonForEachStatementSyntax Node { get; init; }
    public required SemanticModel Model { get; init; }
    public required ForeachSourceOptions Options { get; init; }

    /// <summary>Statically bound type of the iteration expression (null when unclassified).</summary>
    public ITypeSymbol? ExpressionType { get; init; }

    /// <summary><c>GetForEachStatementInfo(...).ElementType</c> for variable-form foreach (M2).</summary>
    public INamedTypeSymbol? ElementType { get; init; }

    /// <summary>Element family for <see cref="ForEachVariableStatementSyntax"/> sites (M2); null for plain foreach.</summary>
    public ForeachSourceFamily? Family { get; init; }

    /// <summary>C40: RefKind of the iteration local (None/Ref/RefReadOnly; authoritative, design §4.1/decision 1).</summary>
    public RefKind RefKind { get; init; }

    /// <summary>C40: normalized foreach binding from <see cref="ForeachBindingResolver"/> (null when unresolved).</summary>
    public ForeachBinding? Binding { get; init; }

    /// <summary>
    /// The node as it existed in the original model tree, before child sites
    /// were rewritten. Children are visited first (nested foreach lower
    /// bottom-up), so the dispatched node can be a detached rebuilt node on
    /// which semantic-model lookups return empty; all symbol queries must use
    /// this original node, while generated syntax uses the visited node.
    /// </summary>
    public CommonForEachStatementSyntax? OriginalNode { get; init; }
}

/// <summary>
/// Mutable per-file accumulator shared by the rewriter and the ordered rule
/// list: synthetic-name counters, statistics, diagnostics and the global
/// Changed flag.
/// </summary>
internal sealed class ForeachSourceRewriteContext
{
    public required ForeachSourceOptions Options { get; init; }
    public required ForeachSourceStatistics Statistics { get; init; }
    public required List<ForeachSourceDiagnostic> Diagnostics { get; init; }

    private readonly Dictionary<string, int> _nameCounters = new(StringComparer.Ordinal);

    public bool Changed { get; set; }

    public string FreshName(string baseName, SyntaxNode scopeNode)
        => ForeachSourceSyntaxFacts.FreshName(baseName, scopeNode, _nameCounters);

    public void Report(
        ForeachSourceSeverity severity,
        string code,
        string message,
        SyntaxNode? node = null,
        int? line = null,
        int? column = null)
    {
        if (severity == ForeachSourceSeverity.Info && !Options.ReportInfo)
        {
            return;
        }

        if (node != null && line == null)
        {
            var span = node.GetLocation().GetLineSpan().StartLinePosition;
            line = span.Line + 1;
            column = span.Character + 1;
        }

        Diagnostics.Add(new ForeachSourceDiagnostic(severity, code, message, null, line, column));
        Statistics.ErrorsByCode[code] =
            Statistics.ErrorsByCode.GetValueOrDefault(code) + 1;
    }
}

/// <summary>
/// Outcome of one rule attempt. A null result means "rule does not match this
/// site" (the next rule runs). A non-null result terminates rule dispatch:
/// <see cref="Changed"/> false with a null <see cref="Output"/> means the node
/// stays as-is (diagnose-only / intentional pass-through), while a non-null
/// <see cref="Output"/> replaces the (already child-visited) node.
/// </summary>
internal sealed record ForeachSourceRuleOutcome(SyntaxNode? Output, bool Changed)
{
    public static ForeachSourceRuleOutcome KeepUnchanged { get; } = new(null, false);
}

internal interface IForeachSourceRule
{
    /// <summary>null = not matched; non-null = handled (rewritten or diagnosed without rewrite).</summary>
    ForeachSourceRuleOutcome? TryHandle(
        ForeachSourceSite site,
        ForeachSourceRewriteContext context,
        CommonForEachStatementSyntax visitedNode);
}
