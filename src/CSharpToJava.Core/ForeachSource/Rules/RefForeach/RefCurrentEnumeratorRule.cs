using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.Core.ForeachSource;

/// <summary>
/// C40 FR-3 (design §4.3): a <c>ref</c>/<c>ref readonly</c> foreach whose
/// source exposes the enumerator pattern with a <i>by-reference</i>
/// <c>Current</c> property lowers to the FA enumerator skeleton, replacing the
/// per-iteration fetch with
/// <c>ref [readonly] T item = ref __cs2jFaE.Current;</c>. Dispose classification
/// is unchanged (FA-4 matrix inside <see cref="EnumeratorLoweringBuilder"/>).
///
/// Match (all required): plain foreach with <see cref="RefKind.Ref"/>/
/// <see cref="RefKind.RefReadOnly"/>; usable GetEnumerator/MoveNext/Current
/// symbols (or the direct-enumerator pattern); <c>Current</c> returns by ref
/// (Ref for a writable foreach; Ref or RefReadOnly for a readonly foreach)
/// and its type is identical to the iteration element type — ref casts do not
/// exist. Registered after <see cref="RefIndexLoopRule"/>: indexable sources
/// (arrays, Span, custom Length/Count + ref indexer) always win over the
/// enumerator form.
/// </summary>
internal sealed class RefCurrentEnumeratorRule : IForeachSourceRule
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
        if (binding == null || !IsEligible(binding, site.RefKind, site, node))
        {
            return null;
        }

        var semanticNode = site.OriginalNode as ForEachStatementSyntax ?? node;
        var lowering = EnumeratorLoweringBuilder.Lower(
            node, semanticNode, binding, site.Model, context, site.RefKind);

        context.Changed = true;
        context.Statistics.RefCurrentEnumeratorSites++;
        context.Report(
            ForeachSourceSeverity.Info,
            PreprocessDiagnosticCodes.ForeachSourceRefCurrentEnumerator,
            site.RefKind == RefKind.RefReadOnly
                ? "[FR-3] ref-readonly foreach lowered to the enumerator skeleton (ref-readonly item = ref __cs2jFaE.Current)."
                : "[FR-3] ref foreach lowered to the enumerator skeleton (ref item = ref __cs2jFaE.Current).",
            node);

        // The shared builder's FA-4 diagnostics (5450/5451/5452/5453/5454/5455)
        // are not re-emitted here: FR-3 is reported under 5482; the Dispose
        // counters are still advanced by the adapter through the lowering flags
        // only when FA rules run, so keep bookkeeping minimal and accurate
        // (RefCurrentEnumeratorSites is the authoritative bucket).
        return new ForeachSourceRuleOutcome(lowering.Block, Changed: true);
    }

    /// <summary>
    /// FR-3 eligibility, also consulted by <see cref="RefIndexLoopRule"/> to
    /// decide whether a non-indexable ref source should defer to this rule
    /// instead of terminating with CS2J5485.
    /// </summary>
    public static bool IsEligible(
        ForeachBinding binding,
        RefKind foreachRefKind,
        ForeachSourceSite site,
        ForEachStatementSyntax semanticNode)
    {
        if (binding.MoveNextMethod == null
            || binding.CurrentProperty is not { } current
            || (binding.GetEnumeratorMethod == null && !binding.DirectEnumerator))
        {
            return false;
        }

        if (current.RefKind != RefKind.Ref && current.RefKind != RefKind.RefReadOnly)
        {
            return false;
        }

        // A writable ref foreach needs a writable Current; ref-readonly
        // accepts both ref and ref-readonly returns.
        if (foreachRefKind == RefKind.Ref && current.RefKind != RefKind.Ref)
        {
            return false;
        }

        var declaredLocal = site.Model.GetDeclaredSymbol(semanticNode) as ILocalSymbol;
        var elementType = declaredLocal?.Type ?? binding.ElementType;
        return elementType != null
               && SymbolEqualityComparer.Default.Equals(current.Type, elementType);
    }
}
