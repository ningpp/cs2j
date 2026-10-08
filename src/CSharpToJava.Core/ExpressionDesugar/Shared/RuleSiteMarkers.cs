using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar;

/// <summary>
/// Stable annotation registry for one rule's plan sites. Once an earlier
/// in-family rule rewrites two or more subtrees, Roslyn hands every later
/// traversal fresh node instances, so plans keyed by ORIGINAL node references
/// cannot be re-found on the progressive tree. Each site instead carries a
/// rule-specific annotation whose <see cref="SyntaxAnnotation.Data"/> is the
/// registry id; annotations survive every subsequent subtree replacement.
/// </summary>
internal sealed class RuleSiteMarkers<TPlan>
    where TPlan : class
{
    private readonly string _kind;
    private readonly Dictionary<SyntaxNode, SyntaxAnnotation> _annotations = new();
    private int _sequence;

    public RuleSiteMarkers(string kind)
    {
        _kind = kind;
    }

    /// <summary>Marker id -> plan (resolved on current nodes during rewrite).</summary>
    public Dictionary<string, TPlan> ById { get; } = new();

    /// <summary>Registers one original site node and its plan (idempotent).</summary>
    public void Add(SyntaxNode original, TPlan plan)
    {
        if (_annotations.ContainsKey(original))
            return;

        var id = _kind + "-" + _sequence++;
        _annotations[original] = new SyntaxAnnotation(_kind, id);
        ById[id] = plan;
    }

    /// <summary>Contributes the stamps into the family-wide single pass.</summary>
    public void ContributeTo(Dictionary<SyntaxNode, List<SyntaxAnnotation>> stamps)
    {
        foreach (var pair in _annotations)
        {
            if (!stamps.TryGetValue(pair.Key, out var list))
            {
                list = new List<SyntaxAnnotation>();
                stamps[pair.Key] = list;
            }

            list.Add(pair.Value);
        }
    }

    /// <summary>Resolves the plan stamped on a current node; null when absent.</summary>
    public TPlan? TryResolve(SyntaxNode current)
    {
        foreach (var annotation in current.GetAnnotations(_kind))
        {
            if (annotation.Data != null && ById.TryGetValue(annotation.Data, out var plan))
                return plan;
        }

        return null;
    }
}
