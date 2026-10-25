using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// C# reference conversions into the IList family (System.Collections.IList /
/// ICollection and the generic IList&lt;T&gt; / ICollection&lt;T&gt; /
/// IReadOnlyList&lt;T&gt; / IReadOnlyCollection&lt;T&gt;) share one Java rendering
/// because they all map to compat CSharpIList. The value side renders as
/// CSharpList&lt;T&gt; (for List&lt;T&gt;) or as the CSharpICollection views returned
/// by the compat dictionary member mappings (SortedList.Values /
/// Hashtable.Keys → getValues()/getKeys()), and neither shape implements
/// CSharpIList — the two compat interface families cannot be merged because
/// C# IList.Add yields int while ICollection&lt;T&gt;.Add yields boolean. The
/// converter therefore adapts with CSharpIList.from(...) at the conversion
/// site, mirroring <see cref="SpanCoercion"/>; wired at the argument,
/// local-declaration and return positions.
/// </summary>
internal static class IListCoercion
{
    public static string CoerceOrSame(
        ExpressionSyntax source, string text, ITypeSymbol? target, ConversionContext context)
        => TryCoerce(source, text, target, context, out var rendered) ? rendered : text;

    public static bool TryCoerce(
        ExpressionSyntax source,
        string text,
        ITypeSymbol? target,
        ConversionContext context,
        out string rendered)
    {
        rendered = text;
        if (target is not INamedTypeSymbol || context.SemanticModel == null)
            return false;

        // Only IList-family targets: all C# flavours map to CSharpIList[<T>].
        var mappedTarget = context.MapType(target);
        if (!mappedTarget.StartsWith("CSharpIList", StringComparison.Ordinal))
            return false;

        // Already the adapter (e.g. a ternary whose arms were bridged): never
        // wrap the bridge again.
        var trimmed = text.Trim();
        if (trimmed.StartsWith("CSharpIList.from(", StringComparison.Ordinal))
            return false;

        // C# arrays covary into the IList family (T[] implements IList<T> /
        // IReadOnlyList<T>); Java has no such relation — T[] is neither a
        // CSharpIList nor a CSharpGenericIList, so the slot gets the array view
        // CSharpIList.from(ArrayHelper.asListView(arr)) (real chains:
        // FunctionQuery/StringFunctions copy constructors `_args = tmp`,
        // RootAction passing UseAttributeSetsAction.UsedSets to an ICollection
        // parameter, XmlListConverter.ToList returning its object[] buffer).
        // Java primitive arrays (int[] etc.) have no reference T[] to view, so
        // they stay on the caller's boxed-stream handling.
        if (context.GetTypeInfo(source).Type is IArrayTypeSymbol { Rank: 1 } arraySource
            && !IsJavaPrimitiveMapped(context.MapType(arraySource.ElementType)))
        {
            context.AddImport("io.github.ningpp.compat.ArrayHelper");
            context.AddImport("io.github.ningpp.compat.CSharpIList");
            rendered = $"CSharpIList.from(ArrayHelper.asListView({text}))";
            return true;
        }

        if (context.GetTypeInfo(source).Type is not INamedTypeSymbol sourceType)
            return false;
            // C# List<T> renders CSharpList<T>, which implements neither
            // CSharpIList nor CSharpICollection: every List<T> → IList-family
            // slot needs the adapter view (real chain: SchemaElementDecl's
            // List<IDtdDefaultAttributeInfo> DefaultAttDefs property returning
            // through an IList<IDtdDefaultAttributeInfo> slot, and XmlSchemaSet
            // passing its reprocessList to GetExternalSchemasList(IList, ...)).
            if (sourceType.Name == "List"
                && sourceType.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic")
            {
                context.AddImport("io.github.ningpp.compat.CSharpIList");
                rendered = $"CSharpIList.from({text})";
                return true;
            }

            // A C# System.Collections.ICollection (or generic ICollection<T>, e.g.
            // Dictionary<K,V>.Values — whose static type is the nested
            // ValueCollection/KeyCollection class implementing ICollection<T>)
            // reaches Java as the CSharpICollection view of the compat dictionary
            // member mappings; its rendering ends in getValues()/getKeys(), and
            // the receiver guarantees a CSharpList or CSharpICollection shape —
            // both acceptable to the CSharpIList.from overloads (real chain:
            // XmlSchemaSet.Schemas() returning _schemas.Values, its chameleonKeys
            // local reading _chameleonSchemas.Keys, serialization NameTable.Values
            // reading Dictionary.Values).
            if (IsCollectionInterfaceShape(sourceType)
                && (trimmed.EndsWith(".getValues()", StringComparison.Ordinal)
                    || trimmed.EndsWith(".getKeys()", StringComparison.Ordinal)))
            {
                context.AddImport("io.github.ningpp.compat.CSharpIList");
                rendered = $"CSharpIList.from({text})";
                return true;
            }

            // A C# System.Collections.IList local whose rendering was substituted
            // with a CSharpGenericIList-producing bridge (ArrayHelper.asListView /
            // CSharpGenericIList.fromTyped): the raw CSharpIList slot still needs
            // the adapter view (real chain: XmlListConverter.ToList returning its
            // `IList listSrc` local rewritten to ArrayHelper.asListView(arrDst)).
            if (sourceType.OriginalDefinition.ToDisplayString() == "System.Collections.IList"
                && (trimmed.Contains("ArrayHelper.asListView(", StringComparison.Ordinal)
                    || trimmed.StartsWith("CSharpGenericIList.fromTyped(", StringComparison.Ordinal)))
            {
                context.AddImport("io.github.ningpp.compat.CSharpIList");
                rendered = $"CSharpIList.from({text})";
                return true;
            }

            return false;
    }

    /// <summary>
    /// True for static types that render as the CSharpICollection view family:
    /// the ICollection interfaces themselves and concrete views implementing
    /// them (Dictionary&lt;TKey,TValue&gt;.ValueCollection/.KeyCollection).
    /// </summary>
    private static bool IsCollectionInterfaceShape(INamedTypeSymbol sourceType)
        => sourceType.OriginalDefinition.ToDisplayString()
            is "System.Collections.ICollection" or "System.Collections.Generic.ICollection<T>"
        || sourceType.AllInterfaces.Any(i =>
            i.OriginalDefinition.ToDisplayString()
                is "System.Collections.ICollection" or "System.Collections.Generic.ICollection<T>");

    private static bool IsJavaPrimitiveMapped(string mappedElementType)
        => mappedElementType is "int" or "long" or "short" or "byte"
            or "char" or "boolean" or "float" or "double";
}