using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.DefaultParameterStripper.ParamsCollections;

/// <summary>
/// Result object of the C48 params-collections Collect pass. It plays the same
/// role between the two strip-default passes as <see
/// cref="CSharpToJava.Core.DefaultParameterStripper.DefaultParameterCatalog"/>:
/// the stripper holds one instance, Collect fills it across the whole
/// compilation, and Strip reads it per file.
///
/// This is the entry point the declaration materializer (R-a hook) and the
/// call-site rewriter (R-b hook) consume in the following milestone.
/// </summary>
public sealed class ParamsCollectionsResult
{
    private readonly Dictionary<IMethodSymbol, ParamsDeclPlan> _declarations =
        new(SymbolEqualityComparer.Default);

    /// <summary>Registered declaration plans keyed by the declaring method-like symbol.</summary>
    public IReadOnlyDictionary<IMethodSymbol, ParamsDeclPlan> Declarations => _declarations;

    public ParamsStatistics Statistics { get; } = new();

    public List<DefaultParameterStripDiagnostic> Diagnostics { get; } = new();

    public void AddDeclaration(ParamsDeclPlan plan)
        => _declarations[plan.Symbol] = plan;

    /// <summary>
    /// Resolves a call-site symbol to its declaration plan, walking the same
    /// fallback chain the default-parameter catalog uses: the symbol itself,
    /// its original/constructed definitions, its reduced-from definition
    /// (extension methods) and the override chain.
    /// </summary>
    public bool TryGetDeclaration(IMethodSymbol? symbol, out ParamsDeclPlan? plan)
    {
        plan = null;
        var current = symbol;
        var depth = 0;
        while (current != null && depth++ < 8)
        {
            if (_declarations.TryGetValue(current, out plan))
            {
                return true;
            }

            if (_declarations.TryGetValue(current.OriginalDefinition, out plan))
            {
                return true;
            }

            if (current.ConstructedFrom != null
                && _declarations.TryGetValue(current.ConstructedFrom.OriginalDefinition, out plan))
            {
                return true;
            }

            if (current.ReducedFrom != null
                && _declarations.TryGetValue(current.ReducedFrom.OriginalDefinition, out plan))
            {
                return true;
            }

            current = current.OverriddenMethod;
        }

        return false;
    }
}
