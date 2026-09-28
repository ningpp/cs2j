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
