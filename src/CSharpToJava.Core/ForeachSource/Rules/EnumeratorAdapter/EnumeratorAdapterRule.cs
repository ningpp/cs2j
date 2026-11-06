using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.Core.ForeachSource;

/// <summary>
/// C40 FA-1/FA-2/FA-3 dispatch rule (FA-4 Dispose/try-finally is the shared
/// construction inside <see cref="EnumeratorLoweringBuilder"/>, not a separate
/// site rule — design §4.3).
///
/// Match condition (decision 3, all required):
/// <list type="bullet">
/// <item>plain <see cref="ForEachStatementSyntax"/> (deconstruction foreach
/// is owned by E07/C16; await/ref sites are stopped by the rewriter guards);</item>
/// <item>collection static type is not an array, not Span/ReadOnlySpan (FA-5,
/// decision 9) and implements neither <c>System.Collections.IEnumerable</c>
/// nor <c>IEnumerable&lt;T&gt;</c> on the type itself or any of its
/// interfaces (List&lt;T&gt;/Dictionary/… therefore stay with the converter
/// path);</item>
/// <item>the semantic binding provides an enumerator: a GetEnumerator method
/// (instance or extension) or the direct collection-is-enumerator pattern.</item>
/// </list>
/// A dynamic-dispatch site is diagnosed CS2J5456 and left untouched; an
/// incomplete binding on a non-IEnumerable, non-span source is diagnosed
/// CS2J5457 and left untouched (decision 11).
///
/// <c>Nullable&lt;T&gt;</c> sources are classified by their underlying value
/// type T (C# binds foreach over a nullable struct against T through
/// <c>Nullable&lt;T&gt;.Value</c>). A nullable struct implementing IEnumerable
/// therefore stays converter-owned exactly like a non-nullable one; a nullable
/// duck enumerator is diagnosed CS2J5458 instead of being lowered, because the
/// enumerator skeleton would have to emit <c>T?.GetEnumerator()</c>, which does
/// not compile.
/// </summary>
internal sealed class EnumeratorAdapterRule : IForeachSourceRule
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

        // Ref foreach belongs to FR (M3); the rewriter guard already filters it.
        if (site.RefKind != RefKind.None)
        {
            return null;
        }

        // Nullable<T> sources are classified by the underlying value type T:
        // C# binds `foreach (x in nullableStruct)` against T (it evaluates
        // nullable.Value), while Nullable<T> itself implements no interface,
        // so a wrapper-level check would misclassify every nullable struct.
        // An unresolved T was already filtered by IsErrorType above.
        var underlyingType = ForeachSourceAnalyzer.UnwrapNullableValueType(collectionType);
        var sourceType = underlyingType ?? collectionType;

        // Arrays and IEnumerable/IEnumerable<T> sources are owned by the
        // converter (zero collateral damage, decision 3). The same holds for a
        // nullable struct that implements IEnumerable: the node is kept
        // verbatim (no `.Value` rewrite — that would not be equivalent).
        if (sourceType is IArrayTypeSymbol)
        {
            return null;
        }
        if (ForeachSourceAnalyzer.ImplementsIEnumerable(sourceType, site.Model.Compilation))
        {
            return null;
        }

        // Decision 9: Span<T>/ReadOnlySpan<T> always lower to an index loop;
        // SpanIndexLoopRule is registered immediately after this rule. The two
        // rules are mutually exclusive via this explicit exclusion.
        // Nullable<Span<T>> is not a legal C# type; the unwrapped check is
        // defensive only.
        if (ForeachSourceAnalyzer.IsSpanType(sourceType))
        {
            return null;
        }

        // Decision 11: dynamic dispatch cannot be lowered statically. Classified
        // from the collection type so it works even when the foreach info could
        // not be resolved for the dynamic node.
        if (ForeachSourceAnalyzer.IsDynamic(sourceType) || (binding?.IsDynamic ?? false))
        {
            context.Statistics.DynamicForeachSites++;
            var severity = site.Options.Strict
                ? ForeachSourceSeverity.Error
                : ForeachSourceSeverity.Warning;
            context.Report(
                severity,
                PreprocessDiagnosticCodes.ForeachSourceDynamicForeach,
                "[FA] dynamically dispatched foreach cannot be lowered statically by " +
                "foreach-source; the node is left unchanged (dynamic normalization belongs to C25).",
                node);
            return ForeachSourceRuleOutcome.KeepUnchanged;
        }

        // A usable enumerator binding is required: either GetEnumerator
        // (instance/extension) or the direct collection-is-enumerator pattern.
        // A non-array/non-string/non-IEnumerable/non-span site without one is
        // CS2J5457: the node survives (usually the source itself fails to
        // compile), but the gap is now an explicit signal instead of a silent
        // pass-through. Error-type/no-model sites returned earlier and do not
        // reach here.
        if (binding == null
            || binding.MoveNextMethod == null
            || binding.CurrentProperty == null
            || (binding.GetEnumeratorMethod == null && !binding.DirectEnumerator))
        {
            context.Statistics.UnresolvedForeachSites++;
            var severity = site.Options.Strict
                ? ForeachSourceSeverity.Error
                : ForeachSourceSeverity.Warning;
            context.Report(
                severity,
                PreprocessDiagnosticCodes.ForeachSourceUnresolvedForeach,
                "[FA] foreach over a non-array/non-string/non-IEnumerable source has an incomplete " +
                "enumerator binding (no usable GetEnumerator/MoveNext/Current symbols); the node at " +
                "this site is left unchanged.",
                node);
            return ForeachSourceRuleOutcome.KeepUnchanged;
        }

        // Nullable value-type duck source: C# lets `foreach (x in nullableDuck)`
        // compile and reads the underlying value first (probe-pinned on
        // Roslyn 5.0 / net10: the source is evaluated into a T? temp and
        // T?.Value is read, so a null source throws InvalidOperationException
        // — not NullReferenceException — before GetEnumerator runs). The
        // enumerator skeleton cannot express that: `__cs2jFaSrc` is typed T?
        // and `T?.GetEnumerator()` does not compile (member lookup does not
        // fall back to T), so rewriting here would emit uncompilable code.
        // Diagnose precisely instead of the generic "non-IEnumerable" 5457.
        if (underlyingType != null)
        {
            context.Statistics.UnresolvedForeachSites++;
            var nullableSeverity = site.Options.Strict
                ? ForeachSourceSeverity.Error
                : ForeachSourceSeverity.Warning;
            context.Report(
                nullableSeverity,
                PreprocessDiagnosticCodes.ForeachSourceNullableDuckEnumerator,
                "[FA] foreach source is a Nullable<T> value type whose underlying struct exposes a " +
                "pattern enumerator; C# evaluates the source through Nullable<T>.Value " +
                "(InvalidOperationException when null), which the GetEnumerator/while skeleton cannot " +
                "express without emitting uncompilable code on T?; the node at this site is left unchanged.",
                node);
            return ForeachSourceRuleOutcome.KeepUnchanged;
        }

        var semanticNode = site.OriginalNode as ForEachStatementSyntax ?? node;
        var lowering = EnumeratorLoweringBuilder.Lower(
            node, semanticNode, binding, site.Model, context);

        context.Changed = true;

        // CS2J5450: one aggregate count per lowered duck-enumerator site.
        context.Statistics.DuckEnumeratorSitesLowered++;
        context.Report(
            ForeachSourceSeverity.Info,
            PreprocessDiagnosticCodes.ForeachSourceDuckEnumeratorLowered,
            "[FA-1] duck-typed enumerator foreach lowered to GetEnumerator/while(MoveNext)/Current skeleton.",
            node);

        if (lowering.ExtensionGetEnumerator)
        {
            context.Statistics.ExtensionGetEnumeratorSites++;
            context.Report(
                ForeachSourceSeverity.Info,
                PreprocessDiagnosticCodes.ForeachSourceExtensionGetEnumerator,
                "[FA-2] foreach bound through an extension GetEnumerator method; instance call syntax kept.",
                node);
        }

        if (lowering.DirectEnumerator)
        {
            context.Statistics.DirectEnumeratorSites++;
            context.Report(
                ForeachSourceSeverity.Info,
                PreprocessDiagnosticCodes.ForeachSourceDirectEnumerator,
                "[FA-3] foreach iterates an enumerator variable directly (source copied, no GetEnumerator call).",
                node);
        }

        if (lowering.StructEnumerator)
        {
            context.Statistics.StructEnumeratorSites++;
            context.Report(
                ForeachSourceSeverity.Info,
                PreprocessDiagnosticCodes.ForeachSourceStructEnumerator,
                "[FA] value-type enumerator kept concrete via var (MoveNext/Current/Dispose unboxed).",
                node);
        }

        if (lowering.DisposeGenerated)
        {
            context.Statistics.DisposePatternCallSites++;
            context.Report(
                ForeachSourceSeverity.Info,
                PreprocessDiagnosticCodes.ForeachSourceDisposeGenerated,
                lowering.RefStructPatternDispose
                    ? "[FA-4] ref struct pattern Dispose call generated directly (unboxed) in finally."
                    : "[FA-4] pattern/IDisposable Dispose call generated in finally.",
                node);

            if (lowering.DisposeBoxed)
            {
                // Risk 1 observable point: explicit-interface-only Dispose on a
                // value type forces ((System.IDisposable)e).Dispose() and boxes
                // the enumerator. Info by default, Warning under strict; the
                // emitted call site/line comes from node.
                context.Statistics.DisposeBoxedStructSites++;
                var boxedSeverity = site.Options.Strict
                    ? ForeachSourceSeverity.Warning
                    : ForeachSourceSeverity.Info;
                context.Report(
                    boxedSeverity,
                    PreprocessDiagnosticCodes.ForeachSourceBoxedStructDispose,
                    "[FA-4] value-type enumerator at this foreach site is boxed because Dispose() is " +
                    "available only through an explicit IDisposable implementation; the lowered site " +
                    "calls ((global::System.IDisposable)__cs2jFaE).Dispose().",
                    node);
            }
        }
        else
        {
            context.Statistics.NoDisposeSites++;
        }

        return new ForeachSourceRuleOutcome(lowering.Block, Changed: true);
    }
}
