using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.Core.ForeachSource;

/// <summary>
/// C40 FA-5 (design §4.3, decision 9): a plain (<see cref="RefKind.None"/>)
/// foreach over <c>System.Span&lt;T&gt;</c>/<c>System.ReadOnlySpan&lt;T&gt;</c>
/// lowers to a cached-source index loop:
/// <code>
/// var __cs2jFrSrc = &lt;expr&gt;;
/// for (int __cs2jFrI = 0; __cs2jFrI &lt; __cs2jFrSrc.Length; __cs2jFrI++)
/// {
///     var item = __cs2jFrSrc[__cs2jFrI];
///     body
/// }
/// </code>
/// Span never implements IEnumerable and its ref-struct enumerator has no
/// Java-side counterpart in the compat layer, so the enumerator skeleton is
/// intentionally avoided; the index form maps straight to the existing
/// Span.java length()/get()/set() surface. Registered immediately after
/// <see cref="EnumeratorAdapterRule"/>, which excludes Span types up front.
/// </summary>
internal sealed class SpanIndexLoopRule : IForeachSourceRule
{
    public ForeachSourceRuleOutcome? TryHandle(
        ForeachSourceSite site,
        ForeachSourceRewriteContext context,
        CommonForEachStatementSyntax visitedNode)
    {
        if (visitedNode is not ForEachStatementSyntax node)
        {
            return null;
        }

        var binding = site.Binding;
        var collectionType = site.ExpressionType ?? binding?.CollectionType;
        if (collectionType == null
            || ForeachSourceAnalyzer.IsErrorType(collectionType))
        {
            return null;
        }

        // M2 handles plain foreach only; ref/ref-readonly Span foreach is FR-2 (M3).
        if (site.RefKind != RefKind.None)
        {
            return null;
        }

        if (!ForeachSourceAnalyzer.IsSpanType(collectionType))
        {
            return null;
        }

        if (binding is not { IsIndexPattern: true }
            || binding.LengthMember == null
            || binding.Indexer == null)
        {
            // A Span source without resolvable Length/indexer is anomalous
            // (the binding itself would normally be broken): decline and let
            // the FS-5 residual gate account for the site instead of emitting
            // an uncompilable index loop.
            return null;
        }

        var sourceName = context.FreshName(ForeachSourceSyntaxFacts.FrSourceName, node);
        var indexName = context.FreshName(ForeachSourceSyntaxFacts.FrIndexName, node);

        var semanticNode = site.OriginalNode as ForEachStatementSyntax ?? node;
        var declaredLocal = site.Model.GetDeclaredSymbol(semanticNode) as ILocalSymbol;
        var elementType = declaredLocal?.Type ?? binding.ElementType;

        // Decision 7, same rule as the enumerator skeleton: var with an
        // indexer type identical to the element symbol stays cast-free; an
        // explicit element type or a symbol mismatch inserts a fully
        // qualified cast.
        var needsCast = elementType != null
                        && !SymbolEqualityComparer.Default.Equals(binding.Indexer.Type, elementType);

        var block = ForeachIndexLoopBuilder.Build(
            node,
            sourceName,
            indexName,
            binding.LengthMember,
            RefKind.None,
            elementType,
            needsCast);

        context.Changed = true;
        context.Statistics.SpanIndexLoopSites++;
        context.Report(
            ForeachSourceSeverity.Info,
            PreprocessDiagnosticCodes.ForeachSourceSpanIndexLoop,
            "[FA-5] span foreach lowered to index loop (cached source, Length + this[int]).",
            node);

        return new ForeachSourceRuleOutcome(block, Changed: true);
    }
}
