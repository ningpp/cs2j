using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Expression.Utilities;
using CSharpToJava.Core.Transformers.Member;
using CSharpToJava.Core.Transformers.Type;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>Binary expressions — thin dispatcher (logic lives in FfmHelper / BinaryExpressionSupport).</summary>
[TransformerRegistration]
public class BinaryExpressionTransformer : IIRExpressionTransformer
{
    static BinaryExpressionTransformer()
        => ExpressionTransformerRegistry.Register(BinOps.RegisteredKinds, new BinaryExpressionTransformer());

    private static readonly Lazy<BinaryExpressionTransformer> _instance = new(() => new());
    public static BinaryExpressionTransformer Instance => _instance.Value;

    public string Transform(ExpressionSyntax node, ConversionContext context)
    {
        if (!BinOps.ByKind.TryGetValue(node.Kind(), out var op))
            throw new NotSupportedException($"Unsupported binary expression: {node.Kind()}");
        return op.Cat == BinOpCat.Coalesce
            ? TransformCoalesce((BinaryExpressionSyntax)node, context)
            : TransformBinary((BinaryExpressionSyntax)node, op, context);
    }

    public JavaExpression TransformToIR(ExpressionSyntax node, ConversionContext context)
    {
        if (node is not BinaryExpressionSyntax bin
            || !BinOps.ByKind.TryGetValue(bin.Kind(), out var op)
            || op.Cat == BinOpCat.Coalesce)
            return new JavaRawExpression(Transform(node, context));
        var facade = ExpressionTransformerFacade.Instance;

        if (TryResolveOperatorCall(bin, op, context, inference: false, out var opType, out var javaName))
            return BuildOperatorCallIR(bin, opType!, javaName, context);
        if (op.Cat == BinOpCat.Relational && TryTransformCompatComparison(bin, op.Java, context) is { } cmp)
            return new JavaRawExpression(cmp);

        if (op.Cat == BinOpCat.Equality)
        {
            if (IsStringEquality(bin, context)) return BuildObjectsEqualsIR(bin, op.Java, context);
            if (BinarySpecial.TryNullableBoolEquality(bin, op, context) is { } nb)
                return new JavaRawExpression(nb);
            if (TryValueEnumEquality(bin, op.Java, context, out var ve))
                return new JavaRawExpression(ve);
            if (context.GetSymbolInfo(bin.Left).Symbol is IEventSymbol
                || context.GetSymbolInfo(bin.Right).Symbol is IEventSymbol)
                return new JavaRawExpression(Transform(node, context));
        }

        if (TryTransformValueEnumOperands(bin, op, context, out var valueEnumIr))
            return new JavaRawExpression(valueEnumIr);

        EnsureDirectBinaryOperands(bin, op, context);

        return new JavaBinaryExpression
        {
            Left = facade.TransformToIR(bin.Left, context),
            Operator = op.Java,
            Right = facade.TransformToIR(bin.Right, context)
        };
    }

    private string TransformBinary(BinaryExpressionSyntax node, BinOp op, ConversionContext ctx)
    {
        // Span<T> / System.Type define == as value equality; Java classes use identity.
        if (op.Cat == BinOpCat.Equality && TryTransformSpanOrTypeEquality(node, op.Java, ctx) is { } special)
            return special;
        // 【C06 退役评估 M3b】C06 归一后仅服务直连安全网：指针算术/比较在
        // UnsafeFfmPreprocessor(EnablePointer) 阶段已归为 int 索引运算；
        // 语句级 FFM 直翻（FfmHelper.TryTransformPointerBinary）已退役。
        if (FfmHelper.TryTransformPointerBinary(node, op.Java, ctx) is { } ptr) return ptr;
        if (op.Cat == BinOpCat.Equality && TryTransformEventNull(node, op.Java, ctx) is { } evt) return evt;

        if (TryResolveOperatorCall(node, op, ctx, inference: true, out var opType, out var javaName))
            return RenderOperatorCall(node, opType!, javaName, ctx);
        if (op.Cat == BinOpCat.Relational && TryTransformCompatComparison(node, op.Java, ctx) is { } cmp)
            return cmp;

        if (op.Cat == BinOpCat.Relational && BinarySpecial.TryUnsignedComparison(node, op, ctx) is { } unsigned)
            return unsigned;

        if (TryTransformValueEnumOperands(node, op, ctx, out var valueEnumResult))
            return valueEnumResult;

        EnsureDirectBinaryOperands(node, op, ctx);

        var facade = ExpressionTransformerFacade.Instance;
        var left = facade.Transform(node.Left, ctx);
        if (op.Cat == BinOpCat.Logical) ctx.EnterShortCircuitOperand();
        var right = facade.Transform(node.Right, ctx);
        if (op.Cat == BinOpCat.Logical) ctx.ExitShortCircuitOperand();

        // Decimal binary path in the original code returns before precedence wrapping.
        if (ctx.GetTypeInfo(node.Left).Type?.SpecialType == SpecialType.System_Decimal
            || ctx.GetTypeInfo(node.Right).Type?.SpecialType == SpecialType.System_Decimal)
        {
            AddDecimalImportIfNeeded(node, ctx);
            return $"{left} {op.Java} {right}";
        }

        if (op.Cat == BinOpCat.Equality)
        {
            if (BinarySpecial.TryNullableBoolEquality(node, op, ctx) is { } nb) return nb;
            if (TryValueEnumEquality(node, op.Java, ctx, out var valueEnumEq)) return valueEnumEq;
            if (IsStringEquality(node, ctx))
            {
                ctx.AddImport("java.util.Objects");
                var eq = $"Objects.equals({left}, {right})";
                return op.Java == "!=" ? $"!{eq}" : eq;
            }
        }
        if (op.Java == "+")
        {
            if (IsStringType(node.Left, ctx) && !IsStringType(node.Right, ctx))
                right = FormatNumericForConcat(node.Right, right, ctx);
            else if (IsStringType(node.Right, ctx) && !IsStringType(node.Left, ctx))
                left = FormatNumericForConcat(node.Left, left, ctx);
        }

        left = WrapOperand(node.Left, left, op.Java, isLeft: true);
        right = WrapOperand(node.Right, right, op.Java, isLeft: false);
        return $"{left} {op.Java} {right}";
    }

    /// <summary>Parenthesize a child binary expression when precedence requires it.</summary>
    private static string WrapOperand(ExpressionSyntax operand, string transformed, string parentOp, bool isLeft)
    {
        if (operand is BinaryExpressionSyntax
            && BinOps.ByKind.TryGetValue(operand.Kind(), out var child)
            && BinOps.ByJava.TryGetValue(parentOp, out var parent)
            && (child.Prec < parent.Prec || (!isLeft && child.Prec == parent.Prec)))
            return $"({transformed})";
        return transformed;
    }

    /// <summary>
    /// Value enums are emitted as immutable value classes whose fromValue() may
    /// create fresh instances for undeclared numeric values, so C# value
    /// equality inside a value enum must compare getValue() rather than identity.
    /// </summary>
    private static bool TryValueEnumEquality(
        BinaryExpressionSyntax node, string javaOp, ConversionContext ctx, out string result)
    {
        result = string.Empty;
        if (javaOp is not ("==" or "!=")) return false;
        if (node.Left.IsKind(SyntaxKind.NullLiteralExpression)
            || node.Right.IsKind(SyntaxKind.NullLiteralExpression))
            return false;

        var leftType = ctx.GetTypeInfo(node.Left).Type;
        var rightType = ctx.GetTypeInfo(node.Right).Type;
        if (leftType?.TypeKind != TypeKind.Enum || rightType?.TypeKind != TypeKind.Enum)
            return false;

        // Int-constant metadata enums (BindingFlags/TypeAttributes/...) are plain
        // ints in Java: direct ==/!= needs no getValue() pairing.
        if (IsIntConstantMetadataEnum(leftType) || IsIntConstantMetadataEnum(rightType))
            return false;

        var isValueEnum =
            (leftType is INamedTypeSymbol leftEnum && EnumTransformer.IsValueEnum(leftEnum))
            || (rightType is INamedTypeSymbol rightEnum && EnumTransformer.IsValueEnum(rightEnum));
        if (!isValueEnum) return false;

        var facade = ExpressionTransformerFacade.Instance;
        result = $"{facade.Transform(node.Left, ctx)}.getValue() {javaOp} " +
                 $"{facade.Transform(node.Right, ctx)}.getValue()";
        return true;
    }

    /// <summary>
    /// Plain Java enums support only direct == / !=. Any other operator applied to
    /// an enum operand (arithmetic, comparison, shifts, bitwise) must be lowered to
    /// ordinal/cast form by a C#→C# preprocessor.
    /// </summary>
    private static void EnsureDirectBinaryOperands(BinaryExpressionSyntax node, BinOp op, ConversionContext ctx)
    {
        var leftIsEnum = ctx.GetTypeInfo(node.Left).Type?.TypeKind == TypeKind.Enum;
        var rightIsEnum = ctx.GetTypeInfo(node.Right).Type?.TypeKind == TypeKind.Enum;
        if (!leftIsEnum && !rightIsEnum) return;

        if (op.Cat == BinOpCat.Equality && leftIsEnum && rightIsEnum) return;

        throw NormalizationRequired.At(
            $"enum operand in binary operator '{op.Java}'",
            node,
            leftIsEnum ? node.Left : node.Right);
    }

    /// <summary>
    /// Value enums are emitted as value classes exposing getValue()/fromValue(),
    /// so C#'s non-equality operator surface (bitwise, relational, mixed
    /// enum-vs-numeric equality) can be lowered here instead of requiring the
    /// C# enum preprocessor: every value-enum operand reads getValue(), and an
    /// enum-typed bitwise result is re-wrapped with E.fromValue(...). Plain Java
    /// enums (ordinal-aligned) still fall through to the preprocessor gate.
    /// Same-enum ==/!= keeps the dedicated <see cref="TryValueEnumEquality"/>
    /// rendering; null operands keep their nullable special paths.
    /// </summary>
    private static bool TryTransformValueEnumOperands(
        BinaryExpressionSyntax node, BinOp op, ConversionContext ctx, out string result)
    {
        result = string.Empty;
        if (node.Left.IsKind(SyntaxKind.NullLiteralExpression)
            || node.Right.IsKind(SyntaxKind.NullLiteralExpression))
            return false;

        var leftType = ctx.GetTypeInfo(node.Left).Type;
        var rightType = ctx.GetTypeInfo(node.Right).Type;
        var facade = ExpressionTransformerFacade.Instance;

        // Int-constant metadata enums (BindingFlags/TypeAttributes/...) are plain
        // ints in Java: operands render as compat constants, the result is an int —
        // no getValue() on operands and no fromValue() re-wrap of the result.
        if (IsIntConstantMetadataEnum(leftType) || IsIntConstantMetadataEnum(rightType))
        {
            if (op.Cat is BinOpCat.Equality or BinOpCat.Coalesce or BinOpCat.Logical
                or BinOpCat.Shift or BinOpCat.Arithmetic)
                return false;
            var metaLeft = facade.Transform(node.Left, ctx);
            var metaRight = facade.Transform(node.Right, ctx);
            if (node.Left is BinaryExpressionSyntax) metaLeft = $"({metaLeft})";
            if (node.Right is BinaryExpressionSyntax) metaRight = $"({metaRight})";
            result = $"{metaLeft} {op.Java} {metaRight}";
            return true;
        }

        var leftEnum = AsValueEnum(leftType);
        var rightEnum = AsValueEnum(rightType);
        if (leftEnum == null && rightEnum == null) return false;

        // Same-enum ==/!= (including plain enums) keeps its existing path.
        if (op.Cat == BinOpCat.Equality && leftEnum != null && rightEnum != null) return false;

        // A non-value enum operand cannot be lowered through getValue().
        if (IsPlainEnum(leftType) || IsPlainEnum(rightType)) return false;

        if (op.Cat is BinOpCat.Coalesce or BinOpCat.Logical or BinOpCat.Shift or BinOpCat.Arithmetic)
            return false;

        var left = facade.Transform(node.Left, ctx);
        var right = facade.Transform(node.Right, ctx);
        if (leftEnum != null) left = $"{left}.getValue()";
        if (rightEnum != null) right = $"{right}.getValue()";

        // Lowered nested binaries are raw strings; re-parenthesize them.
        if (node.Left is BinaryExpressionSyntax) left = $"({left})";
        if (node.Right is BinaryExpressionSyntax) right = $"({right})";

        var expr = $"{left} {op.Java} {right}";

        // The bitwise result is enum-typed in C#; restore the enum type so
        // assignments/returns to E keep compiling.
        if (op.Cat == BinOpCat.Bitwise
            && ctx.GetTypeInfo(node).Type is INamedTypeSymbol { TypeKind: TypeKind.Enum } resultEnum)
        {
            var storage = EnumTransformer.StorageTypeName(
                resultEnum.EnumUnderlyingType?.SpecialType ?? SpecialType.System_Int32);
            result = $"{ctx.MapType(resultEnum)}.fromValue(({storage}){expr})";
            return true;
        }

        result = expr;
        return true;
    }

    private static INamedTypeSymbol? AsValueEnum(ITypeSymbol? type)
        => type is INamedTypeSymbol { TypeKind: TypeKind.Enum } named && EnumTransformer.IsValueEnum(named)
            ? named
            : null;

    private static bool IsIntConstantMetadataEnum(ITypeSymbol? type)
        => type is INamedTypeSymbol { TypeKind: TypeKind.Enum } named
            && EnumTransformer.IsIntConstantMetadataEnum(named);

    private static bool IsPlainEnum(ITypeSymbol? type)
        => type is INamedTypeSymbol { TypeKind: TypeKind.Enum } named && !EnumTransformer.IsValueEnum(named);

    private string TransformCoalesce(BinaryExpressionSyntax node, ConversionContext ctx)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var left = facade.Transform(node.Left, ctx);
        var right = facade.Transform(node.Right, ctx);
        if (left == "null") return right;

        // Merge into the ternary already produced by ?. conversion: replace its false branch.
        if (left.StartsWith("(") && left.Contains(" != null ? ") && left.LastIndexOf(" : ") is var colon && colon > 0)
        {
            var before = left[..colon];
            var falseBranch = left[(colon + 3)..];
            if (falseBranch.Trim() == right.Trim()) return left;
            var trailing = "";
            for (var i = falseBranch.Length - 1; i >= 0 && falseBranch[i] is ')' or ']' or '}'; i--)
                trailing = falseBranch[i] + trailing;
            return $"{before} : {right}{trailing}";
        }

        // Identifiers / one-level member accesses are side-effect-free to evaluate twice.
        if (node.Left is IdentifierNameSyntax or MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax })
            return $"{left} != null ? {left} : {right}";

        var tmp = ctx.GenerateSyntheticName("_coalesce");
        ctx.AddPreStatement($"var {tmp} = {left};");
        return $"{tmp} != null ? {tmp} : {right}";
    }

    // (predicate on either operand, helper class, import, method)
    private static readonly (Func<ITypeSymbol?, bool> Match, string Type, string Import, string Method)[]
        ValueEqualityHelpers =
        {
            (IsSpanType,   "MemoryExtensions", "io.github.ningpp.compat.MemoryExtensions", "sequenceEqual"),
            (IsSystemType, "TypeHelper",       "io.github.ningpp.compat.TypeHelper",       "isSameType"),
        };

    private string? TryTransformSpanOrTypeEquality(BinaryExpressionSyntax node, string op, ConversionContext ctx)
    {
        var (lt, rt) = (ctx.GetTypeInfo(node.Left).Type, ctx.GetTypeInfo(node.Right).Type);
        var helper = ValueEqualityHelpers.FirstOrDefault(h =>
            h.Type == "MemoryExtensions" ? h.Match(lt) || h.Match(rt) : h.Match(lt) && h.Match(rt));
        if (helper.Type == null) return null;

        var f = ExpressionTransformerFacade.Instance;
        ctx.AddImport(helper.Import);
        var eq = $"{helper.Type}.{helper.Method}({f.Transform(node.Left, ctx)}, {f.Transform(node.Right, ctx)})";
        return op == "==" ? eq : $"!({eq})";
    }

    private static string? TryTransformEventNull(BinaryExpressionSyntax node, string op, ConversionContext ctx)
    {
        var evt = ctx.GetSymbolInfo(node.Left).Symbol is IEventSymbol le
            && node.Right.IsKind(SyntaxKind.NullLiteralExpression) ? le
            : ctx.GetSymbolInfo(node.Right).Symbol is IEventSymbol re
            && node.Left.IsKind(SyntaxKind.NullLiteralExpression) ? re : null;
        if (evt == null || ctx.CurrentType?.Name is not { } current || evt.ContainingType?.Name != current)
            return null;
        var field = EventListenerNaming.GetFieldName(
            EventListenerNaming.GetInterfaceQualifier(evt), evt.Name);
        return op == "!=" ? $"!{field}.isEmpty()" : $"{field}.isEmpty()";
    }

    // TimeSpan relational operators become compareTo(...) op 0.
    private string? TryTransformCompatComparison(BinaryExpressionSyntax node, string op, ConversionContext ctx)
    {
        var sm = ctx.GetSemanticModelForNode(node) ?? ctx.SemanticModel;
        if (sm?.GetTypeInfo(node.Left).Type?.ToDisplayString() is not "System.TimeSpan"
            && sm?.GetTypeInfo(node.Right).Type?.ToDisplayString() is not "System.TimeSpan") return null;
        var f = ExpressionTransformerFacade.Instance;
        return $"{f.Transform(node.Left, ctx)}.compareTo({f.Transform(node.Right, ctx)}) {op} 0";
    }

    // Source-declared user operator, or BCL operator inferred from type info for arithmetic.
    private static bool TryResolveOperatorCall(BinaryExpressionSyntax node, BinOp op, ConversionContext ctx,
        bool inference, out INamedTypeSymbol? operatorType, out string javaName)
    {
        operatorType = null; javaName = "";
        if (ctx.SemanticModel == null) return false;
        if (ctx.GetSymbolInfo(node).Symbol is IMethodSymbol ms
            && BinaryOperatorResolution.IsSourceUserDefinedOperator(ms, node))
        {
            operatorType = ms.ContainingType;
            javaName = BinaryOperatorResolution.JavaName(ms);
            return true;
        }
        if (op.Cat == BinOpCat.Arithmetic
            && BinaryOperatorResolution.ShouldUseTypeInfoFallback(node, ctx)
            && BinaryOperatorResolution.ResolveFallbackOperatorType(node, ctx, inference) is { } fbType
            && BinaryOperatorResolution.TryGetJavaOperatorName(fbType, op, out javaName))
        {
            operatorType = fbType;
            return true;
        }
        return false;
    }

    private string RenderOperatorCall(BinaryExpressionSyntax node, INamedTypeSymbol operatorType,
        string javaName, ConversionContext ctx)
    {
        var f = ExpressionTransformerFacade.Instance;
        var call = $"{javaName}({f.Transform(node.Left, ctx)}, {f.Transform(node.Right, ctx)})";
        return StaticQualifier(operatorType, ctx, node, qualifyCollision: true) is { } q ? $"{q}.{call}" : call;
    }

    private JavaExpression BuildOperatorCallIR(BinaryExpressionSyntax node, INamedTypeSymbol operatorType,
        string javaName, ConversionContext ctx)
    {
        var f = ExpressionTransformerFacade.Instance;
        JavaExpression? target = StaticQualifier(operatorType, ctx, node, qualifyCollision: false) is { } q
            ? new JavaIdentifierExpression { Name = q } : null;
        var call = new JavaMethodCallExpression { Target = target, MethodName = javaName };
        call.Arguments.Add(f.TransformToIR(node.Left, ctx));
        call.Arguments.Add(f.TransformToIR(node.Right, ctx));
        return call;
    }

    private JavaExpression BuildObjectsEqualsIR(BinaryExpressionSyntax node, string op, ConversionContext ctx)
    {
        var f = ExpressionTransformerFacade.Instance;
        ctx.AddImport("java.util.Objects");
        var call = new JavaMethodCallExpression
        {
            Target = new JavaIdentifierExpression { Name = "Objects" },
            MethodName = "equals"
        };
        call.Arguments.Add(f.TransformToIR(node.Left, ctx));
        call.Arguments.Add(f.TransformToIR(node.Right, ctx));
        return op == "!="
            ? new JavaUnaryExpression { Operator = "!", Operand = call }
            : call;
    }

    // Qualifier for a static operator call; null means an unqualified in-class call.
    private static string? StaticQualifier(INamedTypeSymbol operatorType, ConversionContext ctx,
        BinaryExpressionSyntax node, bool qualifyCollision)
    {
        if (ctx.CurrentType is { Name: var currentName } && currentName == operatorType.Name) return null;
        var q = ctx.MapType(operatorType);
        var angle = q.IndexOf('<');
        if (angle > 0) q = q[..angle];
        // A member with the type's simple name shadows the type at the call site: use the FQN.
        if (qualifyCollision
            && ctx.SemanticModel?.GetEnclosingSymbol(node.SpanStart)?.ContainingType is INamedTypeSymbol enc
            && enc.GetMembers(operatorType.Name).Any(m => m is not INamedTypeSymbol))
        {
            var ns = operatorType.ContainingNamespace?.ToDisplayString();
            q = string.IsNullOrWhiteSpace(ns) ? operatorType.Name : $"{ns}.{operatorType.Name}";
        }
        return q;
    }

    private static void AddDecimalImportIfNeeded(BinaryExpressionSyntax node, ConversionContext ctx)
    {
        foreach (var operand in new[] { node.Left, node.Right })
        {
            var ti = ctx.GetTypeInfo(operand);
            if (ti.Type?.SpecialType != SpecialType.System_Decimal
                && ti.ConvertedType?.SpecialType == SpecialType.System_Decimal)
                ctx.AddImport("io.github.ningpp.compat.Decimal");
        }
    }

    // double/float concatenated by C# trims trailing ".0"; route through DoubleHelper.
    private static string FormatNumericForConcat(ExpressionSyntax operand, string transformed, ConversionContext ctx)
    {
        if (ctx.GetTypeInfo(operand).Type?.SpecialType is SpecialType.System_Double or SpecialType.System_Single)
        {
            ctx.AddImport("io.github.ningpp.compat.DoubleHelper");
            return $"DoubleHelper.toString({transformed})";
        }
        return transformed;
    }

    private static bool IsStringType(ExpressionSyntax expr, ConversionContext ctx)
        => ctx.SemanticModel?.GetTypeInfo(expr).Type?.SpecialType == SpecialType.System_String;

    private static bool IsStringEquality(BinaryExpressionSyntax node, ConversionContext ctx)
        => ctx.SemanticModel != null
           && (IsStringType(node.Left, ctx) || IsStringType(node.Right, ctx))
           && !node.Left.IsKind(SyntaxKind.NullLiteralExpression)
           && !node.Right.IsKind(SyntaxKind.NullLiteralExpression);

    private static bool IsSpanType(ITypeSymbol? type)
        => type is INamedTypeSymbol n
           && n.OriginalDefinition.ToDisplayString() is "System.ReadOnlySpan<T>" or "System.Span<T>";

    private static bool IsSystemType(ITypeSymbol? type)
        => type is INamedTypeSymbol { Name: "Type" } t
           && t.ContainingNamespace?.ToDisplayString() == "System";
}
