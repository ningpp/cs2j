using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ForeachSource;

/// <summary>
/// Semantic classification for foreach sites (design §4.2, decision 4/5/9):
/// expression static type, element type/family for deconstruction foreach,
/// guard predicates (ref/await keywords, error/dynamic binding).
/// </summary>
internal static class ForeachSourceAnalyzer
{
    // In the Roslyn 5.0 surface used by this project there is no RefKeyword
    // token on ForEachStatementSyntax; a ref/ref-readonly foreach parses with
    // a RefTypeSyntax as the iteration Type. AwaitKeyword is on the common
    // base type.
    public static bool HasRefKeyword(ForEachStatementSyntax node)
        => node.Type is RefTypeSyntax;

    public static bool HasAwaitKeyword(CommonForEachStatementSyntax node)
        => node.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword);

    /// <summary>
    /// Static type of the iteration expression. Null when the model gives no
    /// binding (error type is reported separately via <see cref="IsErrorOrDynamic"/>).
    /// </summary>
    public static ITypeSymbol? GetExpressionType(SemanticModel model, ExpressionSyntax expression)
    {
        TypeInfo info;
        try
        {
            info = model.GetTypeInfo(expression);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return info.Type;
    }

    public static bool IsString(ITypeSymbol? type)
        => type?.SpecialType == SpecialType.System_String;

    public static bool IsErrorType(ITypeSymbol? type)
        => type is { TypeKind: TypeKind.Error };

    public static bool IsDynamic(ITypeSymbol? type)
        => type is { TypeKind: TypeKind.Dynamic };

    /// <summary>
    /// Decision 9/FA-5: <c>System.Span&lt;T&gt;</c> /
    /// <c>System.ReadOnlySpan&lt;T&gt;</c> by metadata name (ref structs can
    /// never appear under an interface/alias the way IEnumerable sources do).
    /// </summary>
    public static bool IsSpanType(ITypeSymbol? type)
        => type is INamedTypeSymbol named
           && named.TypeArguments.Length == 1
           && named.ContainingNamespace?.ToDisplayString() == "System"
           && (named.MetadataName == "Span`1" || named.MetadataName == "ReadOnlySpan`1");

    /// <summary>
    /// Decision 3: the type itself (it may be the interface) or any of its
    /// implemented interfaces is <c>System.Collections.IEnumerable</c> or
    /// <c>IEnumerable&lt;T&gt;</c>. Arrays are excluded by the callers.
    /// </summary>
    public static bool ImplementsIEnumerable(ITypeSymbol type, Compilation compilation)
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

    /// <summary>
    /// FS-5 CS2J5470 residual-bucket predicate: a plain (non-ref, non-await)
    /// foreach whose source is not an array/string/IEnumerable, where the
    /// semantic binding provides a usable pattern enumerator. Such a site
    /// must never survive unlowered — if every rule declines it, the rewriter
    /// counts it for the residual gate instead of a silent pass-through.
    /// </summary>
    public static bool IsResidualEnumeratorCandidate(
        ITypeSymbol expressionType,
        ForeachBinding? binding,
        Compilation compilation)
    {
        if (expressionType is IArrayTypeSymbol
            || IsString(expressionType)
            || IsDynamic(expressionType)
            || ImplementsIEnumerable(expressionType, compilation))
        {
            return false;
        }

        if (binding == null || binding.RefKind != RefKind.None)
        {
            return false;
        }

        return binding.MoveNextMethod != null
               && binding.CurrentProperty != null
               && (binding.GetEnumeratorMethod != null || binding.DirectEnumerator);
    }

    /// <summary>
    /// Element type behind a <c>foreach (var (…) in …)</c> site; null when the
    /// node is detached from the model or the binding is unavailable.
    /// </summary>
    public static INamedTypeSymbol? TryGetForEachElementType(
        SemanticModel model,
        ForEachVariableStatementSyntax node)
    {
        try
        {
            return model.GetForEachStatementInfo(node).ElementType as INamedTypeSymbol;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // ── Deconstruction family classification (ported from E07, decision 5) ──

    public static ForeachSourceFamily ClassifyFamily(INamedTypeSymbol elementType)
    {
        if (IsKeyValuePair(elementType))
            return ForeachSourceFamily.KeyValuePair;
        foreach (var iface in elementType.AllInterfaces)
        {
            if (IsKeyValuePair(iface))
                return ForeachSourceFamily.KeyValuePair;
        }

        if (IsTuple(elementType))
            return ForeachSourceFamily.Tuple;

        if (HasInstanceDeconstruct(elementType))
            return ForeachSourceFamily.Deconstruct;

        return ForeachSourceFamily.Other;
    }

    private static bool IsKeyValuePair(INamedTypeSymbol type)
        => type.Name == "KeyValuePair"
           && type.TypeArguments.Length == 2
           && (type.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic"
               || type.OriginalDefinition.ToDisplayString().StartsWith(
                   "System.Collections.Generic.KeyValuePair<", StringComparison.Ordinal));

    private static bool IsTuple(INamedTypeSymbol type)
    {
        if (type.IsTupleType)
            return true;
        var defName = type.OriginalDefinition.ToDisplayString();
        if (defName.StartsWith("System.ValueTuple<", StringComparison.Ordinal)
            || defName.StartsWith("System.Tuple<", StringComparison.Ordinal))
            return true;
        return type.AllInterfaces.Any(i =>
            i.OriginalDefinition.ToDisplayString() == "System.Runtime.CompilerServices.ITuple");
    }

    /// <summary>
    /// Instance Deconstruct methods whose parameters are all <c>out</c>.
    /// Extension-method Deconstruct is deliberately NOT recognized here
    /// (compilation-level reduction is out of scope; such elements classify as
    /// Other and receive CS2J5421 — design decision 7, ruling 4).
    /// </summary>
    private static bool HasInstanceDeconstruct(INamedTypeSymbol type)
        => type.GetMembers("Deconstruct")
            .OfType<IMethodSymbol>()
            .Any(m => !m.IsStatic
                      && m.Parameters.Length > 0
                      && m.Parameters.All(p => p.RefKind == RefKind.Out));
}
