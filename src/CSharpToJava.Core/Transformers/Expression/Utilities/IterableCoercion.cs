using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// C# conversions into an <c>IEnumerable&lt;T&gt;</c>/<c>IEnumerable</c> slot, which
/// map to the compat carrier <c>CSharpGenericIterable&lt;T&gt;</c> — a
/// <c>java.util.Collection</c> with a <c>CSharpGenericEnumerator</c> iterator.
///
/// <para>That carrier is a separate compat hierarchy: the non-generic
/// <c>IList</c>/<c>ICollection</c> world erases to <c>CSharpIterable&lt;Object&gt;</c>
/// (so a generated <c>Set&lt;T&gt; : ICollection&lt;T&gt;</c> class does NOT implement
/// it), plain <c>List&lt;T&gt;</c> values are <c>CSharpList</c> (an
/// <c>ArrayList</c>), dictionaries are not collections at all, and arrays
/// cannot implement interfaces. Every value shape therefore needs the compat
/// adapter <c>CSharpGenericIterable.fromAnyTyped(...)</c> at the conversion
/// site; LINQ chains additionally have to be materialized (a Java
/// <c>Stream</c> is not an <c>Iterable</c>, and an inline
/// <c>map(methodRef)</c> has no element type to infer from an
/// <c>Object</c>-taking adapter). Mirrors <see cref="IListCoercion"/> /
/// <see cref="SpanCoercion"/>; wired at the argument, assignment,
/// local-declaration, field-initializer and return positions.</para>
/// </summary>
internal static class IterableCoercion
{
    /// <summary>
    /// Adapts <paramref name="text"/> when <paramref name="target"/> is an
    /// <c>IEnumerable</c>-family slot; returns the original text otherwise.
    /// </summary>
    public static string CoerceOrSame(
        ExpressionSyntax? source, string text, ITypeSymbol? target, ConversionContext context)
        => TryCoerce(source, text, target, context, out var rendered) ? rendered : text;

    public static bool TryCoerce(
        ExpressionSyntax? source,
        string text,
        ITypeSymbol? target,
        ConversionContext context,
        out string rendered)
    {
        rendered = text;
        if (source == null || target == null || context.SemanticModel == null)
            return false;

        // Only IEnumerable-family targets carry the CSharpGenericIterable mapping.
        if (!IsGenericIterableSlot(target, context))
            return false;

        if (source.IsKind(SyntaxKind.NullLiteralExpression))
            return false;

        var trimmed = text.Trim();

        // Already an adapter (possibly emitted by an earlier stage/hook).
        if (trimmed.StartsWith("CSharpGenericIterable.", StringComparison.Ordinal)
            || trimmed.StartsWith("ArrayHelper.asIterable(", StringComparison.Ordinal)
            || trimmed.StartsWith("ArrayHelper.asListView(", StringComparison.Ordinal))
            return false;

        var srcType = context.GetTypeInfo(source).Type;
        if (srcType == null || srcType.TypeKind == TypeKind.Error)
            return false;

        var srcMapped = context.MapType(srcType);
        var srcIsIterable = srcMapped.StartsWith("CSharpGenericIterable", StringComparison.Ordinal);

        if (srcIsIterable)
        {
            // The value already has the carrier's Java type: only a LINQ chain
            // rendered as a Stream pipeline still needs materializing.
            if (!LooksLikeStreamPipeline(trimmed))
                return false;
            context.AddImport("io.github.ningpp.compat.CSharpGenericIterable");
            if (trimmed.Contains(".collect(", StringComparison.Ordinal))
            {
                rendered = $"CSharpGenericIterable.from({text})";
                return true;
            }
            context.AddImport("io.github.ningpp.compat.CSharpList");
            rendered = $"CSharpGenericIterable.from({text}.collect(CSharpList.toCSharpList()))";
            return true;
        }

        if (srcType is IArrayTypeSymbol array)
        {
            // Arrays need a bridge in every shape: a Java array implements
            // neither Iterable nor the compat carriers. A reference array keeps
            // reference identity (and the live-view semantics) through
            // ArrayHelper.asListView (so an array -> IEnumerable -> T[] round
            // trip sees the same array); primitive arrays have no Iterable view
            // and go through the reflective factory.
            if (!IsPrimitiveElement(array.ElementType))
            {
                context.AddImport("io.github.ningpp.compat.ArrayHelper");
                rendered = $"ArrayHelper.asListView({text})";
                return true;
            }
            context.AddImport("io.github.ningpp.compat.CSharpGenericIterable");
            rendered = $"{Witness(target, context)}fromAnyTyped({text})";
            return true;
        }

        // C# string is IEnumerable<char>; the compat factory box-chars it.
        if (srcType.SpecialType == SpecialType.System_String)
        {
            context.AddImport("io.github.ningpp.compat.CSharpGenericIterable");
            rendered = $"CSharpGenericIterable.from({text})";
            return true;
        }

        // A C# dictionary is IEnumerable<KeyValuePair<K,V>> but its compat
        // mapping deliberately stays outside Collection; materialize it through
        // the dedicated entry adapter when an IEnumerable slot receives it.
        if (IsGenericDictionary(srcType))
        {
            context.AddImport("io.github.ningpp.compat.CSharpGenericIterable");
            rendered = $"CSharpGenericIterable.fromEntries({text})";
            return true;
        }

        if (!IsEnumerableSource(srcType))
            return false;

        // Concrete collection classes (List<T> -> CSharpList, generated
        // ICollection<T> classes such as MSAGL's Set<T>, compat ICollection
        // views) do not implement the generic-iterable carrier.
        context.AddImport("io.github.ningpp.compat.CSharpGenericIterable");
        rendered = $"{Witness(target, context)}fromAnyTyped({text})";
        return true;
    }

    /// <summary>True when the C# type maps to <c>CSharpGenericIterable</c>.</summary>
    public static bool IsGenericIterableSlot(ITypeSymbol? target, ConversionContext context)
        => target != null
           && context.MapType(target).StartsWith("CSharpGenericIterable", StringComparison.Ordinal);

    /// <summary>
    /// Explicit type witness for the adapter factory (<c>CSharpGenericIterable.&lt;T&gt;fromAnyTyped</c>)
    /// so the slot's element type survives erasure; empty for the non-generic
    /// <c>IEnumerable</c> (mapped to <c>CSharpGenericIterable&lt;Object&gt;</c>).
    /// </summary>
    private static string Witness(ITypeSymbol target, ConversionContext context)
    {
        if (target is not INamedTypeSymbol { TypeArguments.Length: 1 } named)
            return "CSharpGenericIterable.";
        var element = context.MapType(named.TypeArguments[0]);
        if (string.IsNullOrWhiteSpace(element) || element == "var")
            return "CSharpGenericIterable.";
        return $"CSharpGenericIterable.<{BoxPrimitive(element)}>";
    }

    private static string BoxPrimitive(string javaType)
        => javaType switch
        {
            "int" => "Integer",
            "long" => "Long",
            "double" => "Double",
            "float" => "Float",
            "boolean" => "Boolean",
            "short" => "Short",
            "byte" => "Byte",
            "char" => "Character",
            _ => javaType,
        };

    private static bool IsPrimitiveElement(ITypeSymbol type)
        => type.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64
            or SpecialType.System_Double or SpecialType.System_Single or SpecialType.System_Boolean
            or SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16
            or SpecialType.System_UInt16 or SpecialType.System_UInt32 or SpecialType.System_UInt64
            or SpecialType.System_Char;

    private static bool IsEnumerableSource(ITypeSymbol type)
    {
        if (IsEnumerableDefinition(type))
            return true;
        foreach (var iface in type.AllInterfaces)
        {
            if (IsEnumerableDefinition(iface))
                return true;
        }
        return false;
    }

    private static bool IsEnumerableDefinition(ITypeSymbol type)
        => type.OriginalDefinition is INamedTypeSymbol named
           && named.Name == "IEnumerable"
           && named.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic";

    private static bool IsGenericDictionary(ITypeSymbol type)
        => type.AllInterfaces.Any(i => i.OriginalDefinition is INamedTypeSymbol named
            && named.Name == "IDictionary"
            && named.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic");

    /// <summary>
    /// True when the rendered text is a Java stream pipeline (LINQ rewrite of a
    /// chain with no materializing terminal operator). Only a top-level stream
    /// producer qualifies: a call whose own result is the carrier merely
    /// carries stream text inside its arguments.
    /// </summary>
    internal static bool LooksLikeStreamPipeline(string text)
    {
        var trimmed = text.TrimStart('(');
        return trimmed.StartsWith("Arrays.stream(", StringComparison.Ordinal)
            || trimmed.StartsWith("StreamSupport.stream(", StringComparison.Ordinal)
            || trimmed.StartsWith("Stream.concat(", StringComparison.Ordinal)
            || trimmed.StartsWith("Stream.of(", StringComparison.Ordinal)
            || trimmed.StartsWith("IntStream.", StringComparison.Ordinal)
            || trimmed.StartsWith("LongStream.", StringComparison.Ordinal)
            || trimmed.StartsWith("DoubleStream.", StringComparison.Ordinal);
    }
}
