using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.Interpolations;

/// <summary>
/// Hole type/syntax facts for C23 (design §2.2, Q3 whitelist). The Concat
/// equivalence whitelist is deliberately tight: primitive special types +
/// enum + string only; every other IFormattable (DateTime/Guid/custom) routes
/// to Format (Part A bridge), and non-materializable ref struct/pointer holes
/// are rejected with CS2J0513.
/// </summary>
internal static class InterpolationSyntaxFacts
{
    /// <summary>Q3: primitive types whose parameterless ToString is culture-correct for Concat.</summary>
    private static readonly HashSet<SpecialType> PrimitiveConcatWhitelist = new()
    {
        SpecialType.System_Boolean,
        SpecialType.System_Char,
        SpecialType.System_SByte,
        SpecialType.System_Byte,
        SpecialType.System_Int16,
        SpecialType.System_UInt16,
        SpecialType.System_Int32,
        SpecialType.System_UInt32,
        SpecialType.System_Int64,
        SpecialType.System_UInt64,
        SpecialType.System_Single,
        SpecialType.System_Double,
        SpecialType.System_Decimal,
    };

    /// <summary>Classifies the unsigned numeric kind (nullable forms unwrapped).</summary>
    public static UnsignedHoleKind GetUnsignedKind(ITypeSymbol? type)
    {
        var inner = UnwrapNullable(type);
        return inner switch
        {
            { SpecialType: SpecialType.System_Byte } => UnsignedHoleKind.Byte,
            { SpecialType: SpecialType.System_UInt16 } => UnsignedHoleKind.UInt16,
            { SpecialType: SpecialType.System_UInt32 } => UnsignedHoleKind.UInt32,
            { SpecialType: SpecialType.System_UInt64 } => UnsignedHoleKind.UInt64,
            _ => UnsignedHoleKind.None,
        };
    }

    public static bool IsPointerType(ITypeSymbol? type)
        => type is { TypeKind: TypeKind.Pointer or TypeKind.FunctionPointer };

    /// <summary>
    /// Q3 whitelist: primitive special types, enums and string (nullable
    /// value forms unwrap to their underlying type).
    /// </summary>
    public static bool IsWhitelistType(ITypeSymbol? type)
    {
        if (type == null)
            return false;

        if (type.SpecialType == SpecialType.System_String)
            return true;

        var inner = UnwrapNullable(type);
        if (inner.TypeKind == TypeKind.Enum)
            return true;

        return PrimitiveConcatWhitelist.Contains(inner.SpecialType);
    }

    /// <summary>Nullable&lt;T&gt; unwrap for static hole classification; reference nullability stays.</summary>
    public static ITypeSymbol UnwrapNullable(ITypeSymbol? type)
    {
        if (type is INamedTypeSymbol
            {
                SpecialType: SpecialType.System_Nullable_T,
                TypeArguments.Length: 1
            } named)
        {
            return named.TypeArguments[0];
        }

        return type!;
    }

    /// <summary>
    /// True when the type (or its nullable underlying type) implements
    /// System.IFormattable. Such a non-whitelist hole forces the Format path
    /// (parameterless ToString may differ from ToString(null, CurrentCulture)).
    /// </summary>
    public static bool ImplementsIFormattable(Compilation compilation, ITypeSymbol? type)
    {
        if (type == null)
            return false;

        var iFormattable = compilation.GetTypeByMetadataName("System.IFormattable");
        if (iFormattable == null)
            return false;

        var inner = UnwrapNullable(type);
        return inner.AllInterfaces.Any(i =>
            SymbolEqualityComparer.Default.Equals(i, iFormattable));
    }

    /// <summary>
    /// True when the type itself declares an override of the parameterless
    /// ToString — the only ref-struct hole shape Concat may emit as
    /// x.ToString() (probed: ReadOnlySpan&lt;char&gt; declares one; an unknown
    /// ref struct without it is CS2J0513).
    /// </summary>
    public static bool HasOwnParameterlessToStringOverride(ITypeSymbol? type)
    {
        if (type == null)
            return false;

        foreach (var member in type.GetMembers("ToString"))
        {
            if (member is IMethodSymbol
                {
                    IsOverride: true,
                    Parameters.Length: 0,
                } method
                && SymbolEqualityComparer.Default.Equals(
                    method.ContainingType, type))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Decoded literal text of an interpolation text segment.
    /// <para>
    /// <c>ValueText</c> already resolves ordinary character escapes
    /// (<c>\" \\ \n</c> …) and raw-string normalization, but probed against
    /// Roslyn it keeps the lexical brace doubling of regular/verbatim
    /// interpolations (source <c>{{a}}</c> stays the two-character sequences
    /// <c>{{a}}</c>, whereas the runtime value is a single <c>{a}</c>);
    /// raw interpolations instead carry literal braces verbatim because
    /// their brace grammar is delimiter-count based, so those are returned
    /// untouched (Part C raw matrix keeps using this same path).
    /// </para>
    /// </summary>
    public static string GetTextValue(InterpolatedStringTextSyntax text)
    {
        var value = text.TextToken.ValueText;
        if (text.Parent is InterpolatedStringExpressionSyntax owner && IsRaw(owner))
            return value;

        return value.Replace("{{", "{").Replace("}}", "}");
    }

    private static bool IsRaw(InterpolatedStringExpressionSyntax interpolation)
    {
        var kind = interpolation.StringStartToken.Kind();
        return kind is SyntaxKind.InterpolatedSingleLineRawStringStartToken
            or SyntaxKind.InterpolatedMultiLineRawStringStartToken;
    }

    public static bool HasAlignment(InterpolationSyntax hole)
        => hole.AlignmentClause != null;

    public static bool HasFormat(InterpolationSyntax hole)
        => hole.FormatClause != null;

    /// <summary>Format clause ValueText (the raw composite specifier text).</summary>
    public static string? GetFormatText(InterpolationSyntax hole)
        => hole.FormatClause?.FormatStringToken.ValueText;

    /// <summary>
    /// Compile-time alignment value, including unary negation (probed:
    /// GetConstantValue on the alignment expression resolves -5).
    /// </summary>
    public static int? GetConstantAlignment(
        SemanticModel model,
        InterpolationSyntax hole)
    {
        var alignment = hole.AlignmentClause;
        if (alignment == null)
            return null;

        var constant = model.GetConstantValue(alignment.Value);
        return constant.HasValue && constant.Value is int value ? value : null;
    }

    /// <summary>Residual gate node kind: interpolation syntax only (never ordinary strings).</summary>
    public static bool IsResidualNode(SyntaxNode node)
        => node is InterpolatedStringExpressionSyntax;

    /// <summary>
    /// True when the node itself or any ancestor carries the family explained
    /// annotation: a preserved site accounts for its whole subtree.
    /// </summary>
    public static bool IsExplained(SyntaxNode node)
    {
        var current = node;
        while (current != null)
        {
            if (current.HasAnnotation(ExpressionDesugarSyntaxFacts.ExplainedAnnotation))
                return true;
            current = current.Parent;
        }

        return false;
    }
}
