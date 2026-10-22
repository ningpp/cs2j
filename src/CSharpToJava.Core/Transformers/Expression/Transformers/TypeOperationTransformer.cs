using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Type;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>Handles cast / is / as / typeof / default / checked / unchecked / sizeof.</summary>
[TransformerRegistration]
public class TypeOperationTransformer : IIRExpressionTransformer
{
    static TypeOperationTransformer() => ExpressionTransformerRegistry.Register(new[]
    {
        SyntaxKind.CastExpression, SyntaxKind.IsExpression, SyntaxKind.IsPatternExpression, SyntaxKind.AsExpression,
        SyntaxKind.TypeOfExpression, SyntaxKind.DefaultExpression, SyntaxKind.DefaultLiteralExpression,
        SyntaxKind.CheckedExpression, SyntaxKind.UncheckedExpression, SyntaxKind.SizeOfExpression
    }, new TypeOperationTransformer());
    private static readonly Lazy<TypeOperationTransformer> _instance = new(() => new());
    public static TypeOperationTransformer Instance => _instance.Value;
    public JavaExpression TransformToIR(ExpressionSyntax n, ConversionContext c) => new JavaRawExpression(Transform(n, c));
    public string Transform(ExpressionSyntax n, ConversionContext c) => n switch
    {
        CastExpressionSyntax x => Cast(x, c),
        BinaryExpressionSyntax x when x.IsKind(SyntaxKind.IsExpression) => Is(x, c),
        BinaryExpressionSyntax x when x.IsKind(SyntaxKind.AsExpression) => As(x, c),
        IsPatternExpressionSyntax x => IsPattern(x, c),
        TypeOfExpressionSyntax x => TypeOf(x, c),
        DefaultExpressionSyntax x => GetDefaultValueForType(Map(x.Type, c), c.GetTypeInfo(x.Type).Type, c),
        CheckedExpressionSyntax x => (x.IsKind(SyntaxKind.CheckedExpression) ? "/* checked */ " : "") + Expr(c, x.Expression),
        SizeOfExpressionSyntax x => SizeOf(x, c),
        _ when n.IsKind(SyntaxKind.DefaultLiteralExpression) =>
            c.GetTypeInfo(n).ConvertedType is { } t ? GetDefaultValueForType(c.MapType(t), t, c) : "null",
        _ => throw new NotSupportedException($"Type operation kind {n.Kind()} not supported.")
    };
    private static string Expr(ConversionContext c, ExpressionSyntax n) => ExpressionTransformerFacade.Instance.Transform(n, c);
    private static string Map(TypeSyntax ts, ConversionContext c)
    {
        var s = c.GetTypeInfo(ts).Type;
        return s is null or IErrorTypeSymbol ? c.MapTypeFromSyntax(ts) : c.MapType(s);
    }
    // Runtime type form: strip generic arguments and box primitives (for instanceof / .class).
    private static string Rt(string t)
    {
        var s = t.IndexOf('<') is int i && i >= 0 ? t[..i] : t;
        return s switch
        {
            "int" => "Integer", "long" => "Long", "short" => "Short", "byte" => "Byte",
            "double" => "Double", "float" => "Float", "char" => "Character", "boolean" => "Boolean", _ => s
        };
    }
    private static string Cast(CastExpressionSyntax n, ConversionContext c)
    {
        var e = Expr(c, n.Expression);
        var sym = c.GetTypeInfo(n.Type).Type;
        var srcSym = c.GetTypeInfo(n.Expression).Type;
        if (sym is IPointerTypeSymbol) return e; // all pointers lower to MemorySegment

        // C# casts to IEnumerable<T> become compat adapters: generated collections
        // may implement the non-generic interfaces only, and Java cannot prove a
        // class→interface cast is valid in all shapes.
        if (sym is INamedTypeSymbol { Name: "IEnumerable", TypeArguments.Length: 1 } enumerableCast
            && enumerableCast.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic")
        {
            c.AddImport("io.github.ningpp.compat.CSharpGenericIterable");
            var element = c.MapType(enumerableCast.TypeArguments[0]);
            return $"CSharpGenericIterable.<{element}>fromAnyTyped({e})";
        }

        // Casts to the __suppress__ sentinel (ISerializable et al.): the sentinel
        // is an interface, but the source is often a FINAL generated class —
        // javac rejects a provably-disjoint final-class→interface cast. Routing
        // through Object keeps the cast legal (it fails at runtime only when the
        // class really does not implement the sentinel, which the converted
        // assertion code expects as an exception anyway).
        if (sym != null && c.MapType(sym) == "__suppress__")
        {
            c.AddImport("io.github.ningpp.compat.__suppress__");
            return $"(__suppress__)(Object)({e})";
        }

        // C# implicit array/string → Span<T>/ReadOnlySpan<T> conversions become
        // explicit casts in the intermediate tree; the compat types are plain
        // classes, so render the construction/asSpan bridge instead of a Java cast.
        if (SpanCoercion.TryCoerce(n.Expression, e, sym, c, out var spanCast))
            return spanCast;

        // Plain enum casts (including C# lifted Nullable<E> casts, which map to
        // the same Java enum reference type). Ordinal enums: (E)i ->
        // E.values()[i], (int)e -> e.ordinal(). Value enums (explicit values /
        // non-int underlying): fromValue/getValue.
        if (TryUnwrapNullableEnum(sym, out var targetEnum))
        {
            // (E)null / (E?)null: the Java enum reference itself is nullable,
            // so the bare null literal passes through unchanged.
            if (n.Expression.IsKind(SyntaxKind.NullLiteralExpression))
                return e;
            var target = Map(n.Type, c);
            var richTarget = EnumTransformer.IsValueEnum(targetEnum);
            var targetStorage = EnumTransformer.StorageTypeName(
                targetEnum.EnumUnderlyingType?.SpecialType ?? SpecialType.System_Int32);
            // 元数据 flags 枚举（BindingFlags/MethodAttributes 等）由 compat 以 int
            // 常量提供，Java 侧没有值类：转成底层整数即可。
            if (EnumTransformer.IsIntConstantMetadataEnum(targetEnum))
                return $"({targetStorage})({e})";
            if (TryUnwrapNullableEnum(srcSym, out var sourceEnum))
            {
                if (SymbolEqualityComparer.Default.Equals(sourceEnum, targetEnum))
                    return e;
                if (!richTarget)
                    return $"{target}.values()[{e}.ordinal()]";
                var sourceValue = EnumTransformer.IsValueEnum(sourceEnum)
                    ? $"{e}.getValue()"
                    : $"{e}.ordinal()";
                return $"{target}.fromValue(({targetStorage}){sourceValue})";
            }
            // C# unboxing from object is type-strict: `(E)obj` succeeds only when obj
            // holds a boxed E (never a boxed int — that needs `(E)(int)obj`). Lower
            // the object-typed source to a plain Java reference cast.
            if (srcSym?.SpecialType == SpecialType.System_Object)
                return $"({target})({e})";
            if (richTarget)
                return $"{target}.fromValue(({targetStorage}){e})";
            var index = srcSym?.SpecialType is SpecialType.System_Int64 or SpecialType.System_UInt64
                ? $"(int)({e})"
                : e;
            return $"{target}.values()[{index}]";
        }
        if (TryUnwrapNullableEnum(srcSym, out var srcEnum))
            e = EnumTransformer.IsIntConstantMetadataEnum(srcEnum)
                ? e
                : EnumTransformer.IsValueEnum(srcEnum)
                    ? $"{e}.getValue()"
                    : $"{e}.ordinal()";

        // C# numeric-to-decimal conversions (C34 DC-9 operand promotion emits
        // them explicitly): Java has no primitive->reference cast, so they must
        // become factory calls against the compat Decimal. valueOf(int)/(long)/
        // (double) cover the signed and narrow sources (char/short/byte widen to
        // the int overload); real uint/ulong keep their magnitude through the
        // unsigned helpers (the Java int/long bit pattern is reinterpreted there).
        // An identity (decimal)d cast is dropped.
        if (sym?.SpecialType == SpecialType.System_Decimal)
        {
            if (srcSym?.SpecialType == SpecialType.System_Decimal)
                return e;
            if (srcSym?.SpecialType is SpecialType.System_UInt32)
            {
                c.AddImport("io.github.ningpp.compat.UnsignedMath");
                return $"UnsignedMath.toDecimalUInt32({e})";
            }
            if (srcSym?.SpecialType is SpecialType.System_UInt64)
            {
                c.AddImport("io.github.ningpp.compat.UnsignedMath");
                return $"UnsignedMath.toDecimalUInt64({e})";
            }
            if (srcSym is null || IsNumericPromotableToDecimal(srcSym))
            {
                c.AddImport("io.github.ningpp.compat.Decimal");
                return $"Decimal.valueOf({e})";
            }
        }

        // Unboxing a Nullable<T> primitive: C# throws InvalidOperationException
        // (mapped to IllegalStateException) on null rather than NPE. Route
        // through the compat guard, then cast to the target primitive (covers
        // narrowing such as (short)int?).
        if (srcSym is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullableSource
            && sym is not null && IsPrimitiveSpecialType(sym.SpecialType))
        {
            c.AddImport("io.github.ningpp.compat.Nullable");
            var targetPrimitive = Map(n.Type, c);
            return IsJavaPrimitive(targetPrimitive)
                ? $"({targetPrimitive})(Nullable.value({e}))"
                : $"Nullable.value({e})";
        }

        // Unboxing: a reference-typed (or boxed/Nullable/dynamic) operand cast to
        // a primitive must become a cast to the boxed wrapper; Java then unboxes
        // via assignment/binary context. Dropping the cast entirely, as the
        // primitive-to-primitive branch below does, yields `int x = objectExpr`,
        // which does not compile (e.g. non-generic IEnumerator foreach lowering
        // emits `int item = (int)enumerator.Current`).
        if (srcSym is not null && sym is not null
            && IsPrimitiveSpecialType(sym.SpecialType)
            && IsBoxedOrReferenceSource(srcSym))
        {
            return $"({BoxedWrapperName(sym.SpecialType)})({e})";
        }

        return sym?.SpecialType switch
        {
            SpecialType.System_Int32 when NeedsExplicitIntCast(srcSym) => $"(int)({e})",
            SpecialType.System_Int32 => e,
            SpecialType.System_Byte => $"({e} & 0xFF)",                                   // unsigned narrowing → mask
            SpecialType.System_UInt16 => $"(((int)({e})) & 0xFFFF)",
            SpecialType.System_UInt32 => $"(int)(({e}) & 0xFFFFFFFFL)",
            _ => $"({Map(n.Type, c)})({e})"
        };
    }

    /// <summary>
    /// Unwraps a type that is either an enum or a lifted <see cref="Nullable{T}"/>
    /// whose underlying type is an enum. Both lower to the same Java enum
    /// reference type, so casts between them are identity conversions.
    /// </summary>
    private static bool TryUnwrapNullableEnum(ITypeSymbol? type, out INamedTypeSymbol enumType)
    {
        if (type is INamedTypeSymbol named && named.TypeKind == TypeKind.Enum)
        {
            enumType = named;
            return true;
        }
        if (type is INamedTypeSymbol nullable
            && nullable.IsValueType
            && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            && nullable.TypeArguments is [INamedTypeSymbol { TypeKind: TypeKind.Enum } inner])
        {
            enumType = inner;
            return true;
        }
        enumType = null!;
        return false;
    }

    /// <summary>
    /// Java requires an explicit narrowing cast for these → int conversions;
    /// all other int casts (byte/short/char/int widening) are implicit.
    /// </summary>
    private static bool NeedsExplicitIntCast(ITypeSymbol? source)
        => source?.SpecialType is SpecialType.System_Single
            or SpecialType.System_Double
            or SpecialType.System_Int64
            or SpecialType.System_UInt64;

    private static bool IsPrimitiveSpecialType(SpecialType special)
        => special is SpecialType.System_Boolean
            or SpecialType.System_Int32
            or SpecialType.System_Int64
            or SpecialType.System_Int16
            or SpecialType.System_SByte
            or SpecialType.System_Byte
            or SpecialType.System_Char
            or SpecialType.System_Single
            or SpecialType.System_Double;

    private static bool IsJavaPrimitive(string? name)
        => name is "boolean" or "int" or "long" or "short" or "byte"
            or "char" or "float" or "double";

    /// <summary>
    /// A cast source whose Java representation is a reference (needs a wrapper
    /// cast before unboxing): reference types, boxed value types, Nullable
    /// wrappers, and dynamic.
    /// </summary>
    private static bool IsBoxedOrReferenceSource(ITypeSymbol source)
        => source.IsReferenceType
            || source.TypeKind == TypeKind.Dynamic
            || (source is INamedTypeSymbol named
                && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T);

    private static string BoxedWrapperName(SpecialType special)
        => special switch
        {
            SpecialType.System_Boolean => "Boolean",
            SpecialType.System_Int32 => "Integer",
            SpecialType.System_Int64 => "Long",
            SpecialType.System_Int16 => "Short",
            SpecialType.System_SByte => "Byte",
            SpecialType.System_Byte => "Byte",
            SpecialType.System_Char => "Character",
            SpecialType.System_Single => "Float",
            SpecialType.System_Double => "Double",
            _ => "Object"
        };

    /// <summary>
    /// Numeric sources whose rendered Java primitive converts losslessly through
    /// <c>Decimal.valueOf</c> (and enums via ordinal). Real uint/ulong are handled
    /// separately via the unsigned-magnitude helpers and must not appear here.
    /// </summary>
    private static bool IsNumericPromotableToDecimal(ITypeSymbol source)
        => source.SpecialType is SpecialType.System_SByte
            or SpecialType.System_Byte
            or SpecialType.System_Int16
            or SpecialType.System_UInt16
            or SpecialType.System_Int32
            or SpecialType.System_Int64
            or SpecialType.System_Char
            or SpecialType.System_Single
            or SpecialType.System_Double
            || source.TypeKind == TypeKind.Enum;
    private static string Is(BinaryExpressionSyntax n, ConversionContext c)
    {
        var left = Expr(c, n.Left);
        if (c.GetTypeInfo(n.Right).Type is ITypeParameterSymbol tp)
            return $"{TypeParameterToken(n, tp)}.isInstance({left})"; // Java forbids instanceof T
        return $"{left} instanceof {Rt(Map((TypeSyntax)n.Right, c))}";
    }
    private static string As(BinaryExpressionSyntax n, ConversionContext c)
    {
        var e = Expr(c, n.Left);
        if (n.Left is InvocationExpressionSyntax or ConditionalAccessExpressionSyntax
            or ElementAccessExpressionSyntax or AssignmentExpressionSyntax)
        {
            var tmp = c.GenerateSyntheticName("_asExpr"); // hoist side effects, evaluate once
            c.AddPreStatement($"var {tmp} = {e};");
            e = tmp;
        }
        var t = Map((TypeSyntax)n.Right, c);
        return $"({e} instanceof {Rt(t)} ? ({t})({e}) : null)";
    }
    private static string IsPattern(IsPatternExpressionSyntax n, ConversionContext c)
    {
        var e = Expr(c, n.Expression);
        return n.Pattern switch
        {
            DeclarationPatternSyntax d => DeclPattern(e, d, c),
            // JavaGenericPatternRewriter lowers generic declaration patterns to a
            // type-only pattern plus a hoisted cast declaration; C#9 `x is T` also
            // parses here for qualified types. Same reified form as the binary `is`.
            TypePatternSyntax tp => $"{e} instanceof {Rt(Map(tp.Type, c))}",
            ConstantPatternSyntax { Expression: LiteralExpressionSyntax l } when l.IsKind(SyntaxKind.NullLiteralExpression) => $"{e} == null",
            DiscardPatternSyntax => "true",
            _ => "/* TODO: unsupported pattern */ true"
        };
    }
    private static string DeclPattern(string e, DeclarationPatternSyntax d, ConversionContext c)
    {
        var t = Rt(Map(d.Type, c));
        var v = ConversionContext.EscapeJavaKeyword(d.Designation.ToString());
        return (int)c.Options.TargetJavaVersion >= 16
            ? $"{e} instanceof {t} {v}"
            : $"{e} instanceof {t} && ({v} = ({t}){e}) != null";
    }
    private static string TypeOf(TypeOfExpressionSyntax n, ConversionContext c)
    {
        if (c.GetTypeInfo(n.Type).Type is ITypeParameterSymbol tp) return TypeParameterToken(n, tp);
        return $"{Rt(Map(n.Type, c))}.class";
    }

    /// <summary>
    /// Java cannot form <c>T.class</c> for an erased type parameter. Prefer the
    /// generic-deepen anchor local (<c>__cs2j_token_T</c>) when one is in scope;
    /// otherwise — including inside the anchor's own initializer — degrade to
    /// <c>Object.class</c> so the generated expression always references a
    /// declared symbol.
    /// </summary>
    private static string TypeParameterToken(SyntaxNode site, ITypeParameterSymbol typeParameter)
    {
        var anchorName = "__cs2j_token_" + typeParameter.Name;
        // Only locals injected into the enclosing executable scope are usable; a
        // class-level (field initializer) site must not pick up an anchor from a
        // method body elsewhere in the same type.
        var scope = site.Ancestors().FirstOrDefault(a => a is
            MethodDeclarationSyntax or LocalFunctionStatementSyntax or
            AccessorDeclarationSyntax or LambdaExpressionSyntax or
            ConstructorDeclarationSyntax or OperatorDeclarationSyntax);
        var anchor = scope?
            .DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v => v.Identifier.Text == anchorName);
        if (anchor == null)
            return "Object.class";

        // The anchor's own `= typeof(T)` must not reference the variable being declared.
        if (anchor.Initializer?.Span.Contains(site.Span) == true)
            return "Object.class";

        return anchorName;
    }
    /// <summary>Primitives get zero, structs get new T(), Nullable/enum-default etc. as shown; otherwise null.</summary>
    internal static string GetDefaultValueForType(string typeName, ITypeSymbol? typeSymbol, ConversionContext context)
    {
        if (typeSymbol is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T }) return "null";
        var zero = typeName switch
        {
            "int" => "0", "long" => "0L", "short" => "(short)0", "byte" => "0",
            "float" => "0.0f", "double" => "0.0", "boolean" => "false",
            "char" => "'\\0'", "Decimal" => "Decimal.ZERO", _ => null
        };
        if (zero != null)
        {
            if (zero == "Decimal.ZERO") context.AddImport("io.github.ningpp.compat.Decimal");
            return zero;
        }
        if (typeSymbol is INamedTypeSymbol { IsTupleType: true } tuple)
        {
            context.AddImport("io.vavr.Tuple");
            return $"Tuple.of({string.Join(", ", tuple.TupleElements.Select(el => GetDefaultValueForType(context.MapType(el.Type), el.Type, context)))})";
        }
        // Serialization-only BCL structs (StreamingContext et al.) map to the
        // __suppress__ sentinel, which is not instantiable — default is null.
        if (typeName == "__suppress__") return "null";
        if (typeSymbol is INamedTypeSymbol { TypeKind: TypeKind.Struct }) return $"new {typeName}()";
        if (typeSymbol is INamedTypeSymbol { TypeKind: TypeKind.Enum } en) return $"{en.Name}.values()[0]";
        return "null";
    }
    private static string SizeOf(SizeOfExpressionSyntax n, ConversionContext c) => c.GetTypeInfo(n.Type).Type?.SpecialType switch
    {
        SpecialType.System_Boolean or SpecialType.System_SByte => "1",
        SpecialType.System_Char or SpecialType.System_Int16 or SpecialType.System_UInt16 => "2",
        SpecialType.System_Byte or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Single => "4",
        SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Double
            or SpecialType.System_IntPtr or SpecialType.System_UIntPtr => "8",
        SpecialType.System_Decimal => "16",
        _ => "/* sizeof */"
    };
}
