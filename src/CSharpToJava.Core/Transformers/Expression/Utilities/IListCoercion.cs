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

        // A C# System.Collections.ICollection static type reaches Java as the
        // CSharpICollection view of the compat dictionary member mappings; its
        // rendering ends in getValues()/getKeys(), and the receiver guarantees
        // a CSharpList or CSharpICollection shape — both acceptable to the
        // CSharpIList.from overloads (real chain: XmlSchemaSet.Schemas()
        // returning _schemas.Values, its chameleonKeys local reading
        // _chameleonSchemas.Keys, NamespaceList.Enumerate returning _set.Keys).
        if (sourceType.OriginalDefinition.ToDisplayString() == "System.Collections.ICollection"
            && (trimmed.EndsWith(".getValues()", StringComparison.Ordinal)
                || trimmed.EndsWith(".getKeys()", StringComparison.Ordinal)))
        {
            context.AddImport("io.github.ningpp.compat.CSharpIList");
            rendered = $"CSharpIList.from({text})";
            return true;
        }

        return false;
    }
}
