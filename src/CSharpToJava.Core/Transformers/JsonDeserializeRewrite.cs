using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Transformers;

/// <summary>
/// Shared re-typing for Newtonsoft <c>JsonConvert.DeserializeObject&lt;T&gt;</c>.
/// Java erases the generic argument, so the no-type compat entry answers a
/// generic structure (CSharpDictionary / CSharpList&lt;Object&gt;) and the caller's
/// cast to the DTO fails at runtime. Slots that know the closed target type
/// (local declarations, returns, assignments) route the value through the typed
/// compat entry instead.
/// </summary>
internal static class JsonDeserializeRewrite
{
    /// <summary>
    /// Rewrites <c>JsonConvert.deserializeObject(json[, settings])</c> to the typed
    /// compat entry so the deserialized value carries the DTO type instead of the
    /// generic CSharpDictionary / CSharpList&lt;Object&gt; structure:
    /// <list type="bullet">
    /// <item>List/IList/IEnumerable-like targets → <c>deserializeList(json, T.class)</c></item>
    /// <item>concrete classes and arrays → <c>deserializeObject(json, X.class)</c></item>
    /// </list>
    /// The settings argument is dropped (the compat mapper already accepts both
    /// snake_case and camelCase). Returns null when the target type cannot be
    /// named by a Java class literal (type parameters / non-List collections), in
    /// which case the caller keeps the old erasure + cast bridge.
    /// </summary>
    internal static string? TryRewriteTypedJsonDeserialize(
        string rendered, ITypeSymbol? targetType, ConversionContext ctx)
    {
        if (ctx.SemanticModel == null || targetType == null) return null;
        if (targetType is ITypeParameterSymbol or IErrorTypeSymbol) return null;

        const string prefix = "JsonConvert.deserializeObject(";
        var text = rendered.Trim();
        if (!text.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var close = FindMatchingParenthesis(text, prefix.Length - 1);
        if (close != text.Length - 1) return null;

        var jsonArgument = FirstTopLevelArgument(text.Substring(prefix.Length, close - prefix.Length));
        if (jsonArgument == null) return null;

        if (JsonDeserializeListElement(targetType) is { } element)
        {
            ctx.AddImport("io.github.ningpp.compat.JsonConvert");
            var elementClass = ConversionContext.GetClassLiteral(element, ctx);
            return $"JsonConvert.deserializeList({jsonArgument}, {elementClass})";
        }

        if (JsonDeserializeClassTarget(targetType))
        {
            ctx.AddImport("io.github.ningpp.compat.JsonConvert");
            var classLiteral = ConversionContext.GetClassLiteral(targetType, ctx);
            return $"JsonConvert.deserializeObject({jsonArgument}, {classLiteral})";
        }
        return null;
    }

    /// <summary>
    /// Re-types a value written into an assignment slot (field / property / simple
    /// local). Mirrors the local-declaration and return handling: a direct
    /// <c>JsonConvert.deserializeObject(...)</c> goes through the typed compat entry
    /// and is cast back to the mapped target type; a generic helper call
    /// (<see cref="TryWrapGenericJsonDeserializeCall"/>) is converted through
    /// <c>JsonConvert.convertToType</c>. Returns null when the value is not one of
    /// those shapes or the target type cannot be named, so the caller keeps its
    /// existing rendering.
    /// </summary>
    internal static string? RetypeAssignmentValue(
        ExpressionSyntax value, string rendered, ITypeSymbol? targetType, ConversionContext ctx)
    {
        if (ctx.SemanticModel == null || targetType == null) return null;
        if (targetType is ITypeParameterSymbol or IErrorTypeSymbol) return null;

        if (rendered.TrimStart().StartsWith("JsonConvert.deserializeObject(", StringComparison.Ordinal))
        {
            // Only concrete class/array targets and List-family slots (mapped to
            // CSharpList) can carry the typed result as-is; other collection
            // interfaces keep their existing adapter path.
            if (JsonDeserializeClassTarget(targetType))
            {
                var typed = TryRewriteTypedJsonDeserialize(rendered, targetType, ctx);
                if (typed == null) return null;
                var mapped = ctx.MapType(targetType);
                return IsUncastableTarget(mapped) ? typed : $"({mapped}) {typed}";
            }
            if (JsonDeserializeListElement(targetType) != null
                && ctx.MapType(targetType).StartsWith("CSharpList", StringComparison.Ordinal))
            {
                var typed = TryRewriteTypedJsonDeserialize(rendered, targetType, ctx);
                return typed == null ? null : $"({ctx.MapType(targetType)}) {typed}";
            }
            return null;
        }

        if (!JsonDeserializeClassTarget(targetType)
            && !(JsonDeserializeListElement(targetType) != null
                && ctx.MapType(targetType).StartsWith("CSharpList", StringComparison.Ordinal)))
        {
            return null;
        }

        return TryWrapGenericJsonDeserializeCall(value, rendered, targetType, ctx);
    }

    private static bool IsUncastableTarget(string mapped)
        => mapped is "Object" or "var"
            or "int" or "long" or "short" or "byte"
            or "char" or "boolean" or "float" or "double";

    /// <summary>
    /// Element type for the List-family targets that <c>deserializeList</c> can
    /// materialize as CSharpList&lt;T&gt;. Returns null for other collections
    /// (HashSet/Dictionary) and for type parameters, whose class literal erases.
    /// </summary>
    internal static ITypeSymbol? JsonDeserializeListElement(ITypeSymbol targetType)
    {
        if (targetType is not INamedTypeSymbol { TypeArguments.Length: 1 } named) return null;
        var definition = named.OriginalDefinition.ToDisplayString();
        if (definition is not ("System.Collections.Generic.List<T>"
            or "System.Collections.Generic.IList<T>"
            or "System.Collections.Generic.ICollection<T>"
            or "System.Collections.Generic.IEnumerable<T>"
            or "System.Collections.Generic.IReadOnlyList<T>"
            or "System.Collections.Generic.IReadOnlyCollection<T>"))
        {
            return null;
        }

        var element = named.TypeArguments[0];
        if (element is ITypeParameterSymbol or IErrorTypeSymbol) return null;
        return JsonDeserializeClassTarget(element) ? element : null;
    }

    /// <summary>
    /// True when the type can be named by a Java class literal and Jackson can
    /// bind the JSON to it (concrete class/struct, enum, or an array of those).
    /// Primitive element types are only allowed inside arrays (int[].class).
    /// </summary>
    internal static bool JsonDeserializeClassTarget(ITypeSymbol type)
        => JsonDeserializeClassTarget(type, allowPrimitive: false);

    private static bool JsonDeserializeClassTarget(ITypeSymbol type, bool allowPrimitive)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                return JsonDeserializeClassTarget(array.ElementType, allowPrimitive: true);
            case ITypeParameterSymbol or IErrorTypeSymbol:
                return false;
            case INamedTypeSymbol named:
                if (named.TypeKind is not (TypeKind.Class or TypeKind.Struct or TypeKind.Enum))
                    return false;
                if (named.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64
                    or SpecialType.System_Int16 or SpecialType.System_Byte or SpecialType.System_SByte
                    or SpecialType.System_Boolean or SpecialType.System_Char or SpecialType.System_Single
                    or SpecialType.System_Double or SpecialType.System_UInt16 or SpecialType.System_UInt32
                    or SpecialType.System_UInt64)
                {
                    return allowPrimitive;
                }
                if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
                    return false;
                return named.TypeArguments.All(argument => JsonDeserializeClassTarget(argument));
            default:
                return false;
        }
    }

    /// <summary>
    /// Re-types the result of a generic helper that deserializes through
    /// <c>JsonConvert.DeserializeObject&lt;T&gt;</c> (e.g.
    /// <c>RoutingTestSupport.LoadJson&lt;List&lt;X&gt;&gt;</c>). A Java generic method
    /// cannot name its own <c>T</c>, so the helper's <c>(T)</c> cast erases and it
    /// answers the generic CSharpDictionary/CSharpList&lt;Object&gt; structure. The
    /// call site knows the closed target type and converts the structure through
    /// Jackson. Returns null when the initializer is not such a helper call or the
    /// target type cannot be named.
    /// </summary>
    internal static string? TryWrapGenericJsonDeserializeCall(
        ExpressionSyntax initializer,
        string rendered,
        ITypeSymbol? targetType,
        ConversionContext ctx)
    {
        if (ctx.SemanticModel == null || targetType == null) return null;
        if (initializer is not InvocationExpressionSyntax invocation) return null;
        if (ctx.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method) return null;
        // Explicit type arguments substitute the return type, so the "returns its
        // own T" shape is on the unconstructed definition.
        var definition = method.OriginalDefinition;
        if (definition.TypeParameters.Length != 1) return null;
        if (!SymbolEqualityComparer.Default.Equals(definition.ReturnType, definition.TypeParameters[0]))
            return null;
        if (!MethodBodyDeserializesTypeParameter(definition)) return null;

        string rawClassLiteral;
        string? elementClassLiteral = null;
        if (JsonDeserializeListElement(targetType) is { } element)
        {
            ctx.AddImport("io.github.ningpp.compat.CSharpList");
            ctx.AddImport("io.github.ningpp.compat.JsonConvert");
            rawClassLiteral = "CSharpList.class";
            elementClassLiteral = ConversionContext.GetClassLiteral(element, ctx);
        }
        else if (JsonDeserializeClassTarget(targetType))
        {
            ctx.AddImport("io.github.ningpp.compat.JsonConvert");
            rawClassLiteral = ConversionContext.GetClassLiteral(targetType, ctx);
        }
        else
        {
            return null;
        }

        var call = elementClassLiteral == null
            ? $"JsonConvert.convertToType({rendered}, {rawClassLiteral})"
            : $"JsonConvert.convertToType({rendered}, {rawClassLiteral}, {elementClassLiteral})";
        var mapped = ctx.MapType(targetType);
        return mapped is "Object" or "var" ? call : $"({mapped}) {call}";
    }

    /// <summary>
    /// True when the method body contains a Newtonsoft
    /// <c>DeserializeObject&lt;T&gt;</c> call whose type argument is the method's
    /// own type parameter — the helper shape whose <c>(T)</c> cast Java erases.
    /// </summary>
    private static bool MethodBodyDeserializesTypeParameter(IMethodSymbol method)
    {
        var typeParameterName = method.TypeParameters[0].Name;
        foreach (var reference in method.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is not MethodDeclarationSyntax declaration) continue;
            foreach (var invocation in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax access) continue;
                if (access.Name is not GenericNameSyntax generic) continue;
                if (generic.Identifier.Text != "DeserializeObject") continue;
                var owner = access.Expression.ToString();
                if (owner is not ("JsonConvert" or "Newtonsoft.Json.JsonConvert"
                    or "global::Newtonsoft.Json.JsonConvert"))
                {
                    continue;
                }
                if (generic.TypeArgumentList.Arguments.Any(
                        argument => argument.ToString() == typeParameterName))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Index of the parenthesis that closes the one at <paramref name="open"/>, or -1.</summary>
    private static int FindMatchingParenthesis(string text, int open)
    {
        var depth = 0;
        var quote = '\0';
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == '\\') i++;
                else if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'')
            {
                if (i == 0 || text[i - 1] != '\\')
                    quote = c;
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    /// <summary>Text of the first argument at nesting depth 0 (commas inside calls/lambdas ignored).</summary>
    private static string? FirstTopLevelArgument(string arguments)
    {
        var depth = 0;
        var quote = '\0';
        for (var i = 0; i < arguments.Length; i++)
        {
            var c = arguments[i];
            if (quote != '\0')
            {
                if (c == '\\') i++;
                else if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'' && (i == 0 || arguments[i - 1] != '\\'))
            {
                quote = c;
                continue;
            }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ',' && depth == 0)
                return arguments[..i].Trim();
        }
        var trimmed = arguments.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
