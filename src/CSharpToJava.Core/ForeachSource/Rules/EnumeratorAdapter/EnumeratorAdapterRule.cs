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
/// <item>collection static type is not an array and implements neither
/// <c>System.Collections.IEnumerable</c> nor <c>IEnumerable&lt;T&gt;</c> on the
/// type itself or any of its interfaces (List&lt;T&gt;/Dictionary/… therefore
/// stay with the converter path);</item>
/// <item>the semantic binding provides an enumerator: a GetEnumerator method
/// (instance or extension) or the direct collection-is-enumerator pattern.</item>
/// </list>
/// A dynamic-dispatch site is diagnosed CS2J5456 and left untouched; an
/// incomplete binding on a non-IEnumerable source keeps passing through in M1
/// (CS2J5457 is reserved for M2).
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

        // Arrays and IEnumerable/IEnumerable<T> sources are owned by the
        // converter (zero collateral damage, decision 3).
        if (collectionType is IArrayTypeSymbol)
        {
            return null;
        }
        if (ImplementsIEnumerable(collectionType, site.Model.Compilation))
        {
            return null;
        }

        // Decision 11: dynamic dispatch cannot be lowered statically. Classified
        // from the collection type so it works even when the foreach info could
        // not be resolved for the dynamic node.
        if (ForeachSourceAnalyzer.IsDynamic(collectionType) || (binding?.IsDynamic ?? false))
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
        // Odd incomplete-binding states stay pass-through in M1 (5457 in M2).
        if (binding == null
            || binding.MoveNextMethod == null
            || binding.CurrentProperty == null
            || (binding.GetEnumeratorMethod == null && !binding.DirectEnumerator))
        {
            return null;
        }

        var lowering = EnumeratorLoweringBuilder.Lower(node, binding, site.Model, context);

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
            if (lowering.DisposeBoxed)
            {
                context.Statistics.DisposeBoxedStructSites++;
            }
            context.Report(
                ForeachSourceSeverity.Info,
                PreprocessDiagnosticCodes.ForeachSourceDisposeGenerated,
                "[FA-4] pattern/IDisposable Dispose call generated in finally.",
                node);
        }
        else
        {
            context.Statistics.NoDisposeSites++;
        }

        return new ForeachSourceRuleOutcome(lowering.Block, Changed: true);
    }

    /// <summary>
    /// Decision 3: the type itself (it may be the interface) or any of its
    /// implemented interfaces is <c>System.Collections.IEnumerable</c> or
    /// <c>IEnumerable&lt;T&gt;</c>. Arrays are excluded by the caller.
    /// </summary>
    private static bool ImplementsIEnumerable(ITypeSymbol type, Compilation compilation)
    {
        var genericIEnumerable = compilation.GetTypeByMetadataName(
            "System.Collections.Generic.IEnumerable`1");

        if (IsIEnumerable(type, genericIEnumerable))
        {
            return true;
        }

        foreach (var iface in type.AllInterfaces)
        {
            if (IsIEnumerable(iface, genericIEnumerable))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsIEnumerable(ITypeSymbol type, INamedTypeSymbol? genericIEnumerable)
    {
        if (type.SpecialType == SpecialType.System_Collections_IEnumerable)
        {
            return true;
        }

        return genericIEnumerable != null
               && type is INamedTypeSymbol named
               && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, genericIEnumerable);
    }
}
