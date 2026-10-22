using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// C# implicit conversions to <c>Span&lt;T&gt;</c>/<c>ReadOnlySpan&lt;T&gt;</c>
/// (array → span, string → ReadOnlySpan&lt;char&gt;, Span → ReadOnlySpan) share
/// one Java rendering because the compat types are ordinary classes and Java has
/// no implicit structural conversion. Applied at argument/local/assignment/
/// return/ternary/cast positions.
/// </summary>
internal static class SpanCoercion
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
        if (target is not INamedTypeSymbol { TypeArguments.Length: 1 } span
            || span.ContainingNamespace?.ToDisplayString() != "System"
            || span.Name is not ("Span" or "ReadOnlySpan"))
        {
            return false;
        }

        var sourceType = context.GetTypeInfo(source).Type;

        // Ternary arms are bridged individually by ControlFlowTransformer, so the
        // whole conditional must not be wrapped again (avoids asSpan(asSpan(...))).
        if (source is ConditionalExpressionSyntax)
        {
            return false;
        }

        // Already the target span type (e.g. a ternary whose arms were bridged):
        // do not wrap twice.
        if (sourceType is INamedTypeSymbol sameSpan
            && sameSpan.Name == span.Name
            && sameSpan.ContainingNamespace?.ToDisplayString() == "System")
        {
            return false;
        }

        if (span.Name == "ReadOnlySpan"
            && sourceType?.SpecialType == SpecialType.System_String)
        {
            context.AddImport("io.github.ningpp.compat.MemoryExtensions");
            rendered = $"MemoryExtensions.asSpan({text})";
            return true;
        }

        if (sourceType is IArrayTypeSymbol { Rank: 1 })
        {
            context.AddImport($"io.github.ningpp.compat.{span.Name}");
            rendered = $"new {span.Name}<{BoxElement(span.TypeArguments[0], context)}>({text})";
            return true;
        }

        if (span.Name == "ReadOnlySpan"
            && sourceType is INamedTypeSymbol { Name: "Span", TypeArguments.Length: 1 } sourceSpan
            && sourceSpan.ContainingNamespace?.ToDisplayString() == "System")
        {
            context.AddImport("io.github.ningpp.compat.ReadOnlySpan");
            rendered = $"{text}.asReadOnlySpan()";
            return true;
        }

        return false;
    }

    private static string BoxElement(ITypeSymbol element, ConversionContext context)
    {
        var mapped = context.MapType(element);
        return mapped switch
        {
            "char" => "Character",
            "int" => "Integer",
            "long" => "Long",
            "short" => "Short",
            "byte" => "Byte",
            "boolean" => "Boolean",
            "float" => "Float",
            "double" => "Double",
            _ => mapped,
        };
    }
}
