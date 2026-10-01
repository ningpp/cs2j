using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.Core.ForeachSource;

/// <summary>
/// C40 FR-1/FR-2/FR-4 (design §4.3): <c>ref</c>/<c>ref readonly</c> foreach
/// over an indexable source lowers to a cached-source index loop whose
/// per-iteration local is <c>ref [readonly] T item = ref __cs2jFrSrc[i]</c>:
/// <list type="bullet">
/// <item>FR-1 — rank-1 array (built-in <c>.Length</c>, no bound symbols);</item>
/// <item>FR-2 — <c>System.Span&lt;T&gt;</c>/<c>ReadOnlySpan&lt;T&gt;</c> and
/// custom sources with a public Length/Count field or property plus a
/// single-int indexer whose RefKind is compatible with the foreach
/// (Ref for <c>ref</c>; Ref/RefReadOnly for <c>ref readonly</c>);</item>
/// <item>FR-4 — anything else that is not FR-3-eligible: the node stays and
/// CS2J5485 Error is reported (multi-dimensional arrays, by-value indexers,
/// no Length/Count and by-value Current). A dynamic source gets CS2J5456
/// Warning instead (strict: Error).</item>
/// </list>
/// Registered before <see cref="RefCurrentEnumeratorRule"/> so an indexable
/// source wins even when its enumerator Current also returns by ref
/// (Span.Enumerator does).
/// </summary>
internal sealed class RefIndexLoopRule : IForeachSourceRule
{
    public ForeachSourceRuleOutcome? TryHandle(
        ForeachSourceSite site,
        ForeachSourceRewriteContext context,
        CommonForEachStatementSyntax visitedNode)
    {
        if (visitedNode is not ForEachStatementSyntax node || site.RefKind == RefKind.None)
        {
            return null;
        }

        var binding = site.Binding;
        var collectionType = site.ExpressionType ?? binding?.CollectionType;
        if (collectionType == null || binding == null)
        {
            return null;
        }

        // FR-4 (decision): dynamic ref sites cannot be lowered statically;
        // they reuse the FA dynamic bucket CS2J5456.
        if (ForeachSourceAnalyzer.IsDynamic(collectionType) || binding.IsDynamic)
        {
            context.Statistics.DynamicForeachSites++;
            var severity = site.Options.Strict
                ? ForeachSourceSeverity.Error
                : ForeachSourceSeverity.Warning;
            context.Report(
                severity,
                PreprocessDiagnosticCodes.ForeachSourceDynamicForeach,
                "[FR-4] dynamically dispatched ref/ref-readonly foreach cannot be lowered " +
                "statically by foreach-source; the node is left unchanged (dynamic normalization belongs to C25).",
                node);
            return ForeachSourceRuleOutcome.KeepUnchanged;
        }

        var semanticNode = site.OriginalNode as ForEachStatementSyntax ?? node;
        var declaredLocal = site.Model.GetDeclaredSymbol(semanticNode) as ILocalSymbol;
        var elementType = declaredLocal?.Type ?? binding.ElementType;

        // FR-1: arrays. Rank-1 arrays are directly ref-indexable; the C#
        // language rejects ref foreach over multi-dimensional arrays (CS1579),
        // which FR-4 surfaces as CS2J5485 with the verify build as backstop.
        if (collectionType is IArrayTypeSymbol array)
        {
            if (array.Rank != 1)
            {
                return Unsupported(context, node,
                    $"multi-dimensional array (rank {array.Rank}) source; C# allows ref foreach over rank-1 arrays only");
            }

            if (elementType == null
                || !SymbolEqualityComparer.Default.Equals(array.ElementType, elementType))
            {
                return Unsupported(context, node,
                    "array element type does not match the ref iteration variable type");
            }

            return LowerIndex(context, node, binding, lengthMember: null, elementType, "array");
        }

        // FR-2: Span/custom Length(Count) + single-int indexer sources.
        if (binding is { IsIndexPattern: true, LengthMember: not null, Indexer: { } indexer })
        {
            if (!IndexerSatisfies(indexer, site.RefKind, elementType))
            {
                var kind = ForeachSourceAnalyzer.IsSpanType(collectionType) ? "span" : "custom";
                return Unsupported(context, node,
                    $"{kind} indexer returns by value or is read-only relative to the foreach ref kind " +
                    $"(indexer RefKind={indexer.RefKind}, foreach RefKind={site.RefKind})");
            }

            var sourceKind = ForeachSourceAnalyzer.IsSpanType(collectionType) ? "span" : "custom Length/Count + indexer";
            return LowerIndex(context, node, binding, binding.LengthMember, elementType, sourceKind);
        }

        // Non-indexable: let FR-3 claim a ref-Current enumerator source.
        if (RefCurrentEnumeratorRule.IsEligible(binding, site.RefKind, site, semanticNode))
        {
            return null;
        }

        return Unsupported(context, node,
            "source is neither indexable (rank-1 array / span / Length/Count + by-ref int indexer) " +
            "nor enumerated through a by-ref Current property");
    }

    private static bool IndexerSatisfies(
        IPropertySymbol indexer,
        RefKind foreachRefKind,
        ITypeSymbol? elementType)
    {
        if (elementType == null
            || !SymbolEqualityComparer.Default.Equals(indexer.Type, elementType))
        {
            return false;
        }

        return foreachRefKind switch
        {
            RefKind.Ref => indexer.RefKind == RefKind.Ref,
            RefKind.RefReadOnly => indexer.RefKind is RefKind.Ref or RefKind.RefReadOnly,
            _ => false,
        };
    }

    private ForeachSourceRuleOutcome LowerIndex(
        ForeachSourceRewriteContext context,
        ForEachStatementSyntax node,
        ForeachBinding binding,
        ISymbol? lengthMember,
        ITypeSymbol elementType,
        string sourceKind)
    {
        var sourceName = context.FreshName(ForeachSourceSyntaxFacts.FrSourceName, node);
        var indexName = context.FreshName(ForeachSourceSyntaxFacts.FrIndexName, node);

        var block = ForeachIndexLoopBuilder.Build(
            node,
            sourceName,
            indexName,
            lengthMember,
            elementRefKind: binding.RefKind,
            elementType,
            needsCast: false);

        context.Changed = true;
        if (binding.RefKind == RefKind.RefReadOnly)
        {
            context.Statistics.RefReadonlySites++;
            context.Report(
                ForeachSourceSeverity.Info,
                PreprocessDiagnosticCodes.ForeachSourceRefReadonlyIndexLoop,
                $"[FR-1/FR-2] ref-readonly foreach over a {sourceKind} source lowered to an index loop " +
                "(ref-readonly item aliases __cs2jFrSrc[i]).",
                node);
        }
        else
        {
            context.Statistics.RefIndexLoopSites++;
            context.Report(
                ForeachSourceSeverity.Info,
                PreprocessDiagnosticCodes.ForeachSourceRefIndexLoop,
                $"[FR-1/FR-2] ref foreach over a {sourceKind} source lowered to an index loop " +
                "(writable ref item aliases __cs2jFrSrc[i]).",
                node);
        }

        return new ForeachSourceRuleOutcome(block, Changed: true);
    }

    private static ForeachSourceRuleOutcome Unsupported(
        ForeachSourceRewriteContext context,
        ForEachStatementSyntax node,
        string reason)
    {
        // FR-4 CS2J5485 is always Error: a lowered ref loop over such a source
        // would not compile, so the node must survive untouched for the verify
        // build to surface the original compiler error.
        context.Report(
            ForeachSourceSeverity.Error,
            PreprocessDiagnosticCodes.ForeachSourceRefForeachUnsupported,
            "[FR-4] ref/ref-readonly foreach cannot be lowered: " + reason +
            "; the node at this site is left unchanged.",
            node);
        return ForeachSourceRuleOutcome.KeepUnchanged;
    }
}
