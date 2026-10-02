using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

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

        // Plain enum casts: (E)i -> E.values()[i], (int)e -> e.ordinal().
        if (sym is INamedTypeSymbol targetEnum && targetEnum.TypeKind == TypeKind.Enum)
        {
            var target = Map(n.Type, c);
            if (srcSym is INamedTypeSymbol sourceEnum && sourceEnum.TypeKind == TypeKind.Enum)
            {
                return SymbolEqualityComparer.Default.Equals(sourceEnum, targetEnum)
                    ? e
                    : $"{target}.values()[{e}.ordinal()]";
            }
            var index = srcSym?.SpecialType is SpecialType.System_Int64 or SpecialType.System_UInt64
                ? $"(int)({e})"
                : e;
            return $"{target}.values()[{index}]";
        }
        if (srcSym?.TypeKind == TypeKind.Enum)
            e = $"{e}.ordinal()";

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

        return sym?.SpecialType switch
        {
            SpecialType.System_Int32 => e,
            SpecialType.System_Byte => $"{e} & 0xFF",                                   // unsigned narrowing → mask
            SpecialType.System_UInt16 => $"(((int)({e})) & 0xFFFF)",
            SpecialType.System_UInt32 => $"(int)(({e}) & 0xFFFFFFFFL)",
            _ => $"({Map(n.Type, c)})({e})"
        };
    }

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
            return $"{ConversionContext.GetClassLiteral(tp, c)}.isInstance({left})"; // Java forbids instanceof T
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
        if (c.GetTypeInfo(n.Type).Type is ITypeParameterSymbol tp) return ConversionContext.GetClassLiteral(tp, c);
        return $"{Rt(Map(n.Type, c))}.class";
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
