using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Transformers.Member;

namespace CSharpToJava.Core.Transformers.Expression.Utilities;

internal static class SyntaxExprExtensions
{
    public static ExpressionSyntax UnwrapParens(this ExpressionSyntax expr)
    {
        while (expr is ParenthesizedExpressionSyntax p) expr = p.Expression;
        return expr;
    }
}

internal enum BinOpCat { Coalesce, Arithmetic, Relational, Equality, Logical, Bitwise, Shift }

/// <summary>Single source of truth for every binary operator the transformer registers.</summary>
/// <param name="Kind">Roslyn syntax kind.</param>
/// <param name="Java">Java operator text.</param>
/// <param name="Prec">C# precedence (higher binds tighter), used for parenthesization.</param>
/// <param name="Roslyn">Roslyn user-operator method name, when overloadable in C#.</param>
/// <param name="Cat">Category driving dispatch.</param>
internal sealed record BinOp(SyntaxKind Kind, string Java, int Prec, string? Roslyn, BinOpCat Cat);

internal static class BinOps
{
    public static readonly IReadOnlyList<BinOp> All = new[]
    {
        new BinOp(SyntaxKind.CoalesceExpression,            "??",  1, null,                BinOpCat.Coalesce),
        new BinOp(SyntaxKind.LogicalOrExpression,           "||",  3, null,                BinOpCat.Logical),
        new BinOp(SyntaxKind.LogicalAndExpression,          "&&",  4, null,                BinOpCat.Logical),
        new BinOp(SyntaxKind.BitwiseOrExpression,            "|",  5, "op_BitwiseOr",       BinOpCat.Bitwise),
        new BinOp(SyntaxKind.ExclusiveOrExpression,          "^",  6, "op_ExclusiveOr",     BinOpCat.Bitwise),
        new BinOp(SyntaxKind.BitwiseAndExpression,           "&",  7, "op_BitwiseAnd",      BinOpCat.Bitwise),
        new BinOp(SyntaxKind.EqualsExpression,              "==",  8, "op_Equality",        BinOpCat.Equality),
        new BinOp(SyntaxKind.NotEqualsExpression,           "!=",  8, "op_Inequality",      BinOpCat.Equality),
        new BinOp(SyntaxKind.LessThanExpression,             "<",  9, "op_LessThan",        BinOpCat.Relational),
        new BinOp(SyntaxKind.LessThanOrEqualExpression,     "<=",  9, "op_LessThanOrEqual", BinOpCat.Relational),
        new BinOp(SyntaxKind.GreaterThanExpression,          ">",  9, "op_GreaterThan",     BinOpCat.Relational),
        new BinOp(SyntaxKind.GreaterThanOrEqualExpression,  ">=",  9, "op_GreaterThanOrEqual", BinOpCat.Relational),
        new BinOp(SyntaxKind.LeftShiftExpression,           "<<", 10, null,                BinOpCat.Shift),
        new BinOp(SyntaxKind.RightShiftExpression,          ">>", 10, null,                BinOpCat.Shift),
        new BinOp(SyntaxKind.UnsignedRightShiftExpression,  ">>>", 10, null,                BinOpCat.Shift),
        new BinOp(SyntaxKind.AddExpression,                  "+", 11, "op_Addition",        BinOpCat.Arithmetic),
        new BinOp(SyntaxKind.SubtractExpression,             "-", 11, "op_Subtraction",     BinOpCat.Arithmetic),
        new BinOp(SyntaxKind.MultiplyExpression,             "*", 12, "op_Multiply",        BinOpCat.Arithmetic),
        new BinOp(SyntaxKind.DivideExpression,               "/", 12, "op_Division",        BinOpCat.Arithmetic),
        new BinOp(SyntaxKind.ModuloExpression,               "%", 12, "op_Modulus",         BinOpCat.Arithmetic),
    };

    public static readonly IReadOnlyDictionary<SyntaxKind, BinOp> ByKind = All.ToDictionary(o => o.Kind);
    public static readonly IReadOnlyDictionary<string, BinOp> ByJava = All.ToDictionary(o => o.Java);
    public static readonly SyntaxKind[] RegisteredKinds = All.Select(o => o.Kind).ToArray();

    /// <summary>Spaced markers that force parentheses before an enum suffix (.getValue()).</summary>
    public static readonly string[] CompoundMarkers = All
        .Where(o => o.Cat is BinOpCat.Arithmetic or BinOpCat.Bitwise or BinOpCat.Shift)
        // ">>" also matches ">>>"; keep only one right-shift marker.
        .Select(o => o.Java == ">>>" ? null : (o.Java == ">>" ? ">>" : $" {o.Java} "))
        .OfType<string>()
        .Append(" ? ").Append(" : ").Distinct().ToArray();
}

/// <summary>Enum arithmetic/relational/equality/bitwise rewrites.</summary>
internal static class EnumBinary
{
    /// <summary>Parenthesize a binary operand when precedence requires it (plus &amp; 0xFF masks).</summary>
    public static string WrapOperand(ExpressionSyntax operand, string transformed, string parentOp, bool isLeft)
    {
        if (operand is BinaryExpressionSyntax
            && BinOps.ByKind.TryGetValue(operand.Kind(), out var child)
            && BinOps.ByJava.TryGetValue(parentOp, out var parent)
            && (child.Prec < parent.Prec || (!isLeft && child.Prec == parent.Prec)))
            return $"({transformed})";
        if (transformed.Contains("& 0xFF") && !transformed.TrimEnd().EndsWith(")")
            && BinOps.ByJava.TryGetValue(parentOp, out var prec) && prec.Prec > 7)
            return $"({transformed})";
        return transformed;
    }

    private static (INamedTypeSymbol? lt, INamedTypeSymbol? rt, bool le, bool re) Sides(
        BinaryExpressionSyntax node, ConversionContext ctx)
    {
        var lt = ctx.GetTypeInfo(node.Left).Type as INamedTypeSymbol;
        var rt = ctx.GetTypeInfo(node.Right).Type as INamedTypeSymbol;
        return (lt, rt, lt?.TypeKind == TypeKind.Enum, rt?.TypeKind == TypeKind.Enum);
    }

    /// <summary>Null for [Flags] (mapped to int); otherwise .getValue() (explicit) or .ordinal().</summary>
    private static string? Suffix(INamedTypeSymbol enumType, ConversionContext ctx)
    {
        if (IsFlags(enumType, ctx)) return null;
        return HasExplicitValues(enumType) || IsRegisteredExplicit(enumType, ctx)
            ? ".getValue()" : ".ordinal()";
    }

    /// <summary>
    /// Transform both operands, widening enum sides to their numeric form.
    /// With <paramref name="flagsBail"/> a [Flags] side (already an int) routes back to standard.
    /// </summary>
    private static (string? text, INamedTypeSymbol? enumType) Widen(
        BinaryExpressionSyntax node, BinOp op, ConversionContext ctx, bool flagsBail)
    {
        var (lt, rt, le, re) = Sides(node, ctx);
        var enumType = le ? lt! : rt;
        if (enumType == null) return (null, null);
        if (flagsBail && ((le && IsFlags(lt!, ctx)) || (re && IsFlags(rt!, ctx)))) return (null, enumType);
        var f = ExpressionTransformerFacade.Instance;
        var left = f.Transform(node.Left, ctx);
        var right = f.Transform(node.Right, ctx);
        if (le) left = AppendAccess(left, Suffix(lt!, ctx));
        if (re) right = AppendAccess(right, Suffix(rt!, ctx));
        left = WrapOperand(node.Left, left, op.Java, isLeft: true);
        right = WrapOperand(node.Right, right, op.Java, isLeft: false);
        return ($"{left} {op.Java} {right}", enumType);
    }

    /// <summary>Enum + - * / %: byte/short underlying results keep C# wrap-around.</summary>
    public static string? TryArithmetic(BinaryExpressionSyntax node, BinOp op, ConversionContext ctx)
    {
        var (result, enumType) = Widen(node, op, ctx, flagsBail: true);
        if (result == null) return null;
        if (op.Java is "+" or "-")
        {
            var mask = enumType!.EnumUnderlyingType?.SpecialType switch
            {
                SpecialType.System_Byte or SpecialType.System_SByte => "0xFF",
                SpecialType.System_UInt16 or SpecialType.System_Int16 => "0xFFFF",
                _ => null
            };
            if (mask != null) result = $"(({result}) & {mask})";
        }
        return result;
    }

    /// <summary>Enum &lt; &gt; &lt;= &gt;=: compare ordinals/values (Flags enums are already ints).</summary>
    public static string? TryComparison(BinaryExpressionSyntax node, BinOp op, ConversionContext ctx)
        => Widen(node, op, ctx, flagsBail: false).text;

    /// <summary>enum == enum where one side is an enum arith/bitwise (already numeric) expression.</summary>
    public static string? TryEquality(BinaryExpressionSyntax node, BinOp op, ConversionContext ctx)
    {
        var (lt, _, le, re) = Sides(node, ctx);
        if (!le || !re || IsFlags(lt!, ctx)) return null;
        var leftIsNum = IsNumericExpr(node.Left);
        var rightIsNum = IsNumericExpr(node.Right);
        if (!leftIsNum && !rightIsNum) return null;
        var f = ExpressionTransformerFacade.Instance;
        var left = f.Transform(node.Left, ctx);
        var right = f.Transform(node.Right, ctx);
        if (!leftIsNum) left += ".getValue()";
        if (!rightIsNum) right += ".getValue()";
        return $"{left} {op.Java} {right}";
    }

    /// <summary>Enum &amp; | ^: widen operands; wrap result back to enum only in enum-expecting contexts.</summary>
    public static string? TryBitwise(BinaryExpressionSyntax node, BinOp op, ConversionContext ctx)
    {
        var (lt, rt, le, re) = Sides(node, ctx);
        if (!le && !re) return null;
        if ((le && IsFlags(lt!, ctx)) || (re && IsFlags(rt!, ctx))) return null;
        var f = ExpressionTransformerFacade.Instance;
        string left, right;
        var leftUnwrapped = false;
        var rightUnwrapped = false;
        if (le) { var u = UnwrapCast(node.Left, lt!, ctx); if (u != null) { left = u; leftUnwrapped = true; } else left = f.Transform(node.Left, ctx); }
        else left = f.Transform(node.Left, ctx);
        if (re) { var u = UnwrapCast(node.Right, rt!, ctx); if (u != null) { right = u; rightUnwrapped = true; } else right = f.Transform(node.Right, ctx); }
        else right = f.Transform(node.Right, ctx);
        if (le && !leftUnwrapped) left = AppendBitwise(node.Left, left, lt!, ctx);
        if (re && !rightUnwrapped) right = AppendBitwise(node.Right, right, rt!, ctx);
        left = WrapOperand(node.Left, left, op.Java, isLeft: true);
        right = WrapOperand(node.Right, right, op.Java, isLeft: false);
        var result = $"{left} {op.Java} {right}";
        if (ShouldWrapResult(node) && TryWrapResult(node, result, ctx, out var wrapped)) return wrapped;
        return result;
    }

    /// <summary>Safety net for the standard path: enum-typed result -> fromValue()/values()[].</summary>
    public static bool TryWrapResult(BinaryExpressionSyntax node, string numeric, ConversionContext ctx, out string result)
    {
        result = "";
        var rt = ctx.GetTypeInfo(node).ConvertedType as INamedTypeSymbol
            ?? ctx.GetTypeInfo(node).Type as INamedTypeSymbol;
        if (rt?.TypeKind != TypeKind.Enum || IsFlags(rt, ctx)) return false;
        var jt = ctx.MapType(rt);
        var angle = jt.IndexOf('<');
        if (angle > 0) jt = jt[..angle];
        result = HasExplicitValues(rt) || IsRegisteredExplicit(rt, ctx)
            ? $"{jt}.fromValue({numeric})" : $"{jt}.values()[{numeric}]";
        return true;
    }

    private static bool ShouldWrapResult(BinaryExpressionSyntax node)
    {
        var parent = node.Parent;
        while (parent is ParenthesizedExpressionSyntax) parent = parent.Parent;
        return parent is ReturnStatementSyntax or ArrowExpressionClauseSyntax or YieldStatementSyntax
            or EqualsValueClauseSyntax or AssignmentExpressionSyntax or ArgumentSyntax or CastExpressionSyntax;
    }

    /// <summary>(MyEnum)int casts unwrap to the raw integer instead of fromValue(...).getValue().</summary>
    private static string? UnwrapCast(ExpressionSyntax operand, INamedTypeSymbol enumType, ConversionContext ctx)
    {
        var s = operand;
        while (s is ParenthesizedExpressionSyntax p) s = p.Expression;
        if (s is not CastExpressionSyntax cast
            || ctx.GetTypeInfo(cast.Type).Type is not INamedTypeSymbol { TypeKind: TypeKind.Enum } target
            || !SymbolEqualityComparer.Default.Equals(target, enumType)) return null;
        var f = ExpressionTransformerFacade.Instance;
        var inner = f.Transform(cast.Expression, ctx);
        var innerType = ctx.GetTypeInfo(cast.Expression).Type;
        if (innerType?.TypeKind == TypeKind.Enum)
        {
            var sfx = Suffix((INamedTypeSymbol)innerType, ctx);
            if (sfx != null) inner = ApplySuffix(inner, sfx);
        }
        else if (innerType?.SpecialType != SpecialType.System_Int32)
            inner = $"(int)({inner})";
        return inner;
    }

    private static string AppendAccess(string expr, string? suffix)
        => suffix != null && !expr.EndsWith(suffix, StringComparison.Ordinal)
            ? ApplySuffix(expr, suffix) : expr;

    private static string AppendBitwise(ExpressionSyntax operand, string expr, INamedTypeSymbol enumType, ConversionContext ctx)
    {
        var suffix = Suffix(enumType, ctx);
        if (suffix == null || expr.EndsWith(suffix, StringComparison.Ordinal)
            || IsBitwiseNotWithSuffix(expr, suffix)) return expr;
        if (operand.UnwrapParens() is BinaryExpressionSyntax b
            && BinOps.ByKind.TryGetValue(b.Kind(), out var bo) && bo.Cat == BinOpCat.Bitwise) return expr;
        return ApplySuffix(expr, suffix);
    }

    private static string ApplySuffix(string expr, string suffix)
        => BinOps.CompoundMarkers.Any(expr.Contains) ? $"({expr}){suffix}" : $"{expr}{suffix}";

    /// <summary>Enum arith/bit/shift child expression: already numeric in a == / != comparison.</summary>
    private static bool IsNumericExpr(ExpressionSyntax expr)
        => expr.UnwrapParens() is BinaryExpressionSyntax b
           && BinOps.ByKind.TryGetValue(b.Kind(), out var bo)
           && bo.Cat is BinOpCat.Arithmetic or BinOpCat.Bitwise or BinOpCat.Shift;

    private static bool IsBitwiseNotWithSuffix(string expr, string suffix)
    {
        if (!expr.StartsWith("~", StringComparison.Ordinal)) return false;
        var inner = expr[1..].TrimStart();
        if (inner.StartsWith("(", StringComparison.Ordinal) && inner.EndsWith(")", StringComparison.Ordinal))
            inner = inner[1..^1];
        return inner.Contains(suffix, StringComparison.Ordinal);
    }

    public static bool HasExplicitValues(INamedTypeSymbol enumType)
    {
        var ordinal = 0;
        foreach (var m in enumType.GetMembers())
        {
            if (m is not IFieldSymbol { IsConst: true, HasConstantValue: true } f
                || f.Name == WellKnownMemberNames.InstanceConstructorName) continue;
            if (f.ConstantValue is not (int or long or uint or ulong)) return false;
            if (Convert.ToInt64(f.ConstantValue) != ordinal) return true;
            ordinal++;
        }
        return false;
    }

    public static bool IsRegisteredExplicit(INamedTypeSymbol enumType, ConversionContext ctx)
    {
        var fqn = Fqn(enumType);
        return ctx.IsExplicitValueEnum(enumType.ToDisplayString()) || ctx.IsExplicitValueEnum(fqn);
    }

    public static bool IsFlags(INamedTypeSymbol enumType, ConversionContext ctx)
    {
        if (enumType.GetAttributes().Any(a => a.AttributeClass?.Name == "FlagsAttribute")) return true;
        var fqn = Fqn(enumType);
        return ctx.IsFlagsEnum(enumType.ToDisplayString()) || ctx.IsFlagsEnum(fqn);
    }

    private static string Fqn(INamedTypeSymbol type)
    {
        var fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return fqn.StartsWith("global::", StringComparison.Ordinal) ? fqn["global::".Length..] : fqn;
    }
}

/// <summary>Unsigned relational comparisons and nullable-bool equality.</summary>
internal static class BinarySpecial
{
    public static string? TryUnsignedComparison(BinaryExpressionSyntax node, BinOp op, ConversionContext ctx)
    {
        var lt = ctx.GetTypeInfo(node.Left).Type;
        var rt = ctx.GetTypeInfo(node.Right).Type;
        bool u32L = lt?.SpecialType == SpecialType.System_UInt32, u32R = rt?.SpecialType == SpecialType.System_UInt32;
        bool u64L = lt?.SpecialType == SpecialType.System_UInt64, u64R = rt?.SpecialType == SpecialType.System_UInt64;
        bool i64L = lt?.SpecialType == SpecialType.System_Int64, i64R = rt?.SpecialType == SpecialType.System_Int64;
        // uint mixed with signed long promotes to signed long in C# — plain compare there.
        var mixedSignedLong = (u32L && i64R) || (u32R && i64L);
        var f = ExpressionTransformerFacade.Instance;

        string Render() => $"{(u64L || u64R ? "Long" : "Integer")}.compareUnsigned("
            + $"{StripUIntMask(f.Transform(node.Left, ctx))}, {StripUIntMask(f.Transform(node.Right, ctx))}) {op.Java} 0";

        if ((u32L || u32R || u64L || u64R) && !mixedSignedLong) return Render();
        // Fallback for (uint) casts hidden by promotion to long (detected syntactically).
        if (ContainsUIntCast(node.Left) || ContainsUIntCast(node.Right)) return Render();
        return null;
    }

    public static string? TryNullableBoolEquality(BinaryExpressionSyntax node, BinOp op, ConversionContext ctx)
    {
        var lt = ctx.GetTypeInfo(node.Left).Type;
        var rt = ctx.GetTypeInfo(node.Right).Type;
        bool lnb = IsNullableBoolean(lt)
            || (lt?.SpecialType == SpecialType.System_Boolean && node.Left is ConditionalAccessExpressionSyntax);
        bool rnb = IsNullableBoolean(rt)
            || (rt?.SpecialType == SpecialType.System_Boolean && node.Right is ConditionalAccessExpressionSyntax);
        bool lb = node.Left.IsKind(SyntaxKind.TrueLiteralExpression)
            || node.Left.IsKind(SyntaxKind.FalseLiteralExpression);
        bool rb = node.Right.IsKind(SyntaxKind.TrueLiteralExpression)
            || node.Right.IsKind(SyntaxKind.FalseLiteralExpression);
        if (!((lnb && rnb) || (lnb && rb) || (rnb && lb))) return null;

        var f = ExpressionTransformerFacade.Instance;
        var left = f.Transform(node.Left, ctx);
        var right = f.Transform(node.Right, ctx);
        string subject, comparison;
        if (lb)
        {
            subject = right;
            comparison = node.Left.IsKind(SyntaxKind.TrueLiteralExpression) ? "Boolean.TRUE" : "Boolean.FALSE";
        }
        else if (rb)
        {
            subject = left;
            comparison = node.Right.IsKind(SyntaxKind.TrueLiteralExpression) ? "Boolean.TRUE" : "Boolean.FALSE";
        }
        else { subject = left; comparison = right; }

        ctx.AddImport("java.util.Objects");
        var eq = $"Objects.equals({subject}, {comparison})";
        return op.Java == "==" ? eq : $"!{eq}";
    }

    /// <summary>(int)((x) &amp; 0xFFFFFFFFL) → x: compareUnsigned handles unsigned semantics itself.</summary>
    private static string StripUIntMask(string expr)
    {
        const string prefix = "(int)((";
        const string suffix = ") & 0xFFFFFFFFL)";
        if (!expr.StartsWith(prefix) || !expr.EndsWith(suffix)) return expr;
        var inner = expr.Substring(prefix.Length, expr.Length - prefix.Length - suffix.Length);
        if (!IsBalancedParentheses(inner)) return expr;
        if (inner.StartsWith("(") && inner.EndsWith(")")
            && IsBalancedParentheses(inner.Substring(1, inner.Length - 2)))
            inner = inner.Substring(1, inner.Length - 2);
        return inner;
    }

    private static bool IsBalancedParentheses(string s)
    {
        var depth = 0;
        foreach (var c in s)
        {
            if (c == '(') depth++;
            else if (c == ')' && --depth < 0) return false;
        }
        return depth == 0;
    }

    /// <summary>Subtree contains a (uint) cast not hidden inside an invocation argument.</summary>
    private static bool ContainsUIntCast(ExpressionSyntax node)
        => node.DescendantNodesAndSelf().OfType<CastExpressionSyntax>().Any(cast =>
            cast.Type is PredefinedTypeSyntax pts && pts.Keyword.IsKind(SyntaxKind.UIntKeyword)
            && !cast.Ancestors().TakeWhile(a => !ReferenceEquals(a, node.Parent))
                .OfType<InvocationExpressionSyntax>().Any());

    private static bool IsNullableBoolean(ITypeSymbol? type)
        => type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n
           && n.TypeArguments.Length == 1
           && n.TypeArguments[0].SpecialType == SpecialType.System_Boolean;
}

/// <summary>Resolution of user-defined and BCL-mapped (compat library) binary operators.</summary>
internal static class BinaryOperatorResolution
{
    public static bool IsSourceUserDefinedOperator(IMethodSymbol ms, BinaryExpressionSyntax node)
        => ms.MethodKind == MethodKind.UserDefinedOperator
           && ms.ContainingType is { } ct && !IsBuiltInType(ct)
           && HasSourceDeclaration(ms)
           && !IsStructNullLiteralComparison(ms, node);

    public static bool ShouldUseTypeInfoFallback(BinaryExpressionSyntax node, ConversionContext ctx)
    {
        var s = ctx.GetSymbolInfo(node).Symbol;
        return s == null
            || (s is IMethodSymbol m && m.MethodKind == MethodKind.UserDefinedOperator
                && !HasSourceDeclaration(m));
    }

    /// <summary>Maps the operator kind to its Java static-method name when the type declares it.</summary>
    public static bool TryGetJavaOperatorName(INamedTypeSymbol operatorType, BinOp op, out string javaName)
    {
        javaName = "";
        if (op.Roslyn is not { } roslynName
            || !operatorType.GetMembers(roslynName).Any(m =>
                m is IMethodSymbol om && om.MethodKind == MethodKind.UserDefinedOperator))
            return false;
        javaName = OperatorTransformer.OpSymbolToJavaName.TryGetValue(roslynName, out var n)
            ? n : roslynName;
        return true;
    }

    public static string JavaName(IMethodSymbol ms)
        => OperatorTransformer.OpSymbolToJavaName.TryGetValue(ms.Name, out var n) ? n : ms.Name;

    /// <summary>
    /// Fallback when GetSymbolInfo cannot resolve the operator: infer the operator's
    /// containing type from operand type info (and syntax, when the semantic model is incomplete).
    /// </summary>
    public static INamedTypeSymbol? ResolveFallbackOperatorType(BinaryExpressionSyntax node,
        ConversionContext ctx, bool inference)
    {
        var sm = ctx.GetSemanticModelForNode(node) ?? ctx.SemanticModel!;
        var lt = sm.GetTypeInfo(node.Left).Type as INamedTypeSymbol;
        var rt = sm.GetTypeInfo(node.Right).Type as INamedTypeSymbol;
        if (lt != null && rt != null && IsBuiltInType(lt) && IsBuiltInType(rt)) return null;
        if (IsValidOperatorType(lt)) return lt;
        if (IsValidOperatorType(rt)) return rt;
        if (!inference) return null;
        if (ResolveOperandBySymbol(node.Left, sm) is { } l && IsValidOperatorType(l)) return l;
        if (ResolveOperandBySymbol(node.Right, sm) is { } r && IsValidOperatorType(r)) return r;
        var inferred = InferOperatorType(node.Left, sm) ?? InferOperatorType(node.Right, sm);
        return IsValidOperatorType(inferred) ? inferred : null;
    }

    private static bool IsValidOperatorType(INamedTypeSymbol? t)
        => t is { TypeKind: not TypeKind.Error } && t.TypeKind != TypeKind.Interface && !IsBuiltInType(t);

    private static INamedTypeSymbol? ResolveOperandBySymbol(ExpressionSyntax expr, SemanticModel sm)
    {
        if (DeclaredType(sm.GetSymbolInfo(expr).Symbol) is INamedTypeSymbol t
            && t.TypeKind != TypeKind.Error) return t;
        if (expr is IdentifierNameSyntax id)
            return sm.LookupSymbols(expr.SpanStart, name: id.Identifier.Text)
                .Select(DeclaredType)
                .OfType<INamedTypeSymbol>()
                .FirstOrDefault(t => t.TypeKind != TypeKind.Error);
        return null;
    }

    private static INamedTypeSymbol? InferOperatorType(ExpressionSyntax expr, SemanticModel sm)
    {
        expr = expr.UnwrapParens();
        if (sm.GetTypeInfo(expr).Type is INamedTypeSymbol t0
            && t0.TypeKind != TypeKind.Error && !IsBuiltInType(t0)) return t0;

        if (expr is MemberAccessExpressionSyntax ma)
        {
            if (GoodNamed(DeclaredType(sm.GetSymbolInfo(ma).Symbol)) is { } mt) return mt;
            var recv = InferOperatorType(ma.Expression, sm);
            if (recv == null) return null;
            foreach (var m in recv.GetMembers(ma.Name.Identifier.Text))
            {
                if (m is not (IPropertySymbol or IFieldSymbol)) continue;
                return GoodNamed(DeclaredType(m)) ?? recv;
            }
            return recv;
        }

        if (expr is InvocationExpressionSyntax inv)
        {
            // Any non-error named type; the caller's IsValidOperatorType gate filters built-ins.
            if (sm.GetTypeInfo(inv).Type is INamedTypeSymbol it && it.TypeKind != TypeKind.Error) return it;
            if (inv.Expression is MemberAccessExpressionSyntax ima)
            {
                if (GoodNamed(DeclaredType(sm.GetSymbolInfo(ima).Symbol)) is { } mt) return mt;
                if (InferOperatorType(ima.Expression, sm) is { } recv) return recv;
            }
        }

        if (expr is IdentifierNameSyntax identifier)
        {
            if (GoodNamed(DeclaredType(sm.GetSymbolInfo(identifier).Symbol)) is { } t) return t;
            if (sm.GetTypeInfo(identifier).ConvertedType is INamedTypeSymbol ct
                && ct.TypeKind != TypeKind.Error && !IsBuiltInType(ct)) return ct;
            for (var (decl, depth) = (FindVarDeclaration(identifier), 0);
                 depth < 5 && decl?.Initializer?.Value is { } init; depth++)
            {
                if (InferOperatorType(init, sm) is { } it) return it;
                if (init is not IdentifierNameSyntax chain) break;
                decl = FindVarDeclaration(chain);
            }
        }

        if (expr is BinaryExpressionSyntax bin)
            return InferOperatorType(bin.Left, sm) ?? InferOperatorType(bin.Right, sm);
        return null;
    }

    private static ITypeSymbol? DeclaredType(ISymbol? symbol) => symbol switch
    {
        ILocalSymbol l => l.Type,
        IFieldSymbol f => f.Type,
        IParameterSymbol p => p.Type,
        IPropertySymbol pr => pr.Type,
        IMethodSymbol m => m.ReturnType,
        _ => null
    };

    private static INamedTypeSymbol? GoodNamed(ITypeSymbol? type)
        => type is INamedTypeSymbol { TypeKind: not TypeKind.Error } n && !IsBuiltInType(n) ? n : null;

    private static VariableDeclaratorSyntax? FindVarDeclaration(IdentifierNameSyntax id)
    {
        var name = id.Identifier.Text;
        for (var parent = id.Parent; parent != null; parent = parent.Parent)
        {
            if (parent is not BlockSyntax block) continue;
            foreach (var stmt in block.Statements)
                if (stmt is LocalDeclarationStatementSyntax ld)
                    foreach (var v in ld.Declaration.Variables)
                        if (v.Identifier.Text == name) return v;
        }
        return null;
    }

    public static bool HasSourceDeclaration(IMethodSymbol op)
        => op.Locations.Any(l => l.IsInSource) || op.DeclaringSyntaxReferences.Length > 0;

    /// <summary>struct op null is a HasValue check in C#, never an operator call (would NPE in Java).</summary>
    public static bool IsStructNullLiteralComparison(IMethodSymbol op, BinaryExpressionSyntax node)
        => op.ContainingType != null && op.ContainingType.IsValueType
           && (node.Left.IsKind(SyntaxKind.NullLiteralExpression)
               || node.Right.IsKind(SyntaxKind.NullLiteralExpression));

    // SpecialType types actually mapped to compat-library types (DateTime - DateTime -> subtract, ...).
    private static readonly HashSet<string> CompatMappedTypeNames = new(StringComparer.Ordinal)
    {
        "System.DateTime", "System.TimeSpan", "System.DateTimeOffset",
    };

    public static bool IsBuiltInType(INamedTypeSymbol type)
    {
        var name = type.ToDisplayString();
        return type.TypeKind == TypeKind.Enum
            || name == "System.Type"
            || (type.SpecialType != SpecialType.None && !CompatMappedTypeNames.Contains(name));
    }
}
