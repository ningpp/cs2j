using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles control flow expressions (conditional, conditional access, await, throw, switch, this, base, etc.).
/// </summary>
[TransformerRegistration]
public class ControlFlowTransformer : IExpressionTransformer
{
    static ControlFlowTransformer()
    {
        ExpressionTransformerRegistry.Register(new[]
        {
            SyntaxKind.ConditionalExpression,
            SyntaxKind.ConditionalAccessExpression,
            SyntaxKind.AwaitExpression,
            SyntaxKind.ThrowExpression,
            SyntaxKind.SwitchExpression,
            SyntaxKind.ThisExpression,
            SyntaxKind.BaseExpression,
            SyntaxKind.ParenthesizedExpression,
            SyntaxKind.ArgListExpression,
            SyntaxKind.MakeRefExpression,
            SyntaxKind.RefTypeExpression,
            SyntaxKind.RefValueExpression,
            SyntaxKind.WithExpression,
            SyntaxKind.RangeExpression
        }, new ControlFlowTransformer());
    }

    private static readonly Lazy<ControlFlowTransformer> _instance = new(() => new());
    public static ControlFlowTransformer Instance => _instance.Value;

    public string Transform(ExpressionSyntax node, ConversionContext context)
        => node.Kind() switch
        {
            SyntaxKind.ConditionalExpression => TransformConditional((ConditionalExpressionSyntax)node, context),
            SyntaxKind.ConditionalAccessExpression => TransformConditionalAccess((ConditionalAccessExpressionSyntax)node, context),
            SyntaxKind.AwaitExpression => TransformAwait((AwaitExpressionSyntax)node, context),
            SyntaxKind.ThrowExpression => TransformThrowExpression((ThrowExpressionSyntax)node, context),
            SyntaxKind.SwitchExpression => TransformSwitchExpression((SwitchExpressionSyntax)node, context),
            SyntaxKind.ThisExpression => "this",
            SyntaxKind.BaseExpression => "super",
            SyntaxKind.ParenthesizedExpression => $"({ExpressionTransformerFacade.Instance.Transform(((ParenthesizedExpressionSyntax)node).Expression, context)})",
            SyntaxKind.ArgListExpression => "/* TODO: __arglist */",
            SyntaxKind.MakeRefExpression or SyntaxKind.RefTypeExpression or SyntaxKind.RefValueExpression => "/* TODO: ref expression */",
            SyntaxKind.WithExpression => TransformWithExpression((WithExpressionSyntax)node, context),  // Fix 1
            SyntaxKind.RangeExpression => TransformRangeExpression((RangeExpressionSyntax)node, context),  // Fix 2
            _ => throw new NotSupportedException($"Control flow expression kind {node.Kind()} not supported.")
        };

    private string TransformConditional(ConditionalExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var condition = facade.Transform(node.Condition, context);
        var trueExpr = facade.Transform(node.WhenTrue, context);
        var falseExpr = facade.Transform(node.WhenFalse, context);

        // If the conditional expression is typed as IEnumerable/ICollection-like, but branches
        // are stream chains, collect each branch so both sides become Iterable-compatible.
        if (context.SemanticModel != null)
        {
            var converted = context.SemanticModel.GetTypeInfo(node).ConvertedType;
            var convertedDisplay = converted?.OriginalDefinition.ToDisplayString();
            bool expectsIterable = convertedDisplay is
                "System.Collections.Generic.IEnumerable<T>" or
                "System.Collections.IEnumerable" or
                "System.Collections.Generic.ICollection<T>" or
                "System.Collections.ICollection" or
                "System.Collections.Generic.IList<T>";

            if (expectsIterable)
            {
                trueExpr = CollectIfStreamLike(trueExpr, context);
                falseExpr = CollectIfStreamLike(falseExpr, context);
                trueExpr = AdaptZeroArrayToEmptyIterable(trueExpr, context);
                falseExpr = AdaptZeroArrayToEmptyIterable(falseExpr, context);
            }
        }

        return $"({condition} ? {trueExpr} : {falseExpr})";
    }

    private static string CollectIfStreamLike(string expr, ConversionContext context)
    {
        bool streamLike = expr.Contains(".stream(", StringComparison.Ordinal)
            || expr.Contains(".filter(", StringComparison.Ordinal)
            || expr.Contains(".map(", StringComparison.Ordinal)
            || expr.Contains(".sorted(", StringComparison.Ordinal)
            || expr.Contains(".distinct(", StringComparison.Ordinal)
            || expr.Contains(".flatMap(", StringComparison.Ordinal);

        if (!streamLike || expr.Contains(".collect(", StringComparison.Ordinal))
            return expr;

        context.AddImport("java.util.stream.Collectors");
        context.AddImport("java.util.ArrayList");
        return $"{expr}.collect(Collectors.toCollection(ArrayList::new))";
    }

    private static string AdaptZeroArrayToEmptyIterable(string expr, ConversionContext context)
    {
        var t = expr.Trim();
        // Match original zero-length array: new T[0]
        if (t.Contains("new ", StringComparison.Ordinal)
            && t.Contains("[0]", StringComparison.Ordinal))
        {
            context.AddImport("java.util.Collections");
            return "Collections.emptyList()";
        }

        // Match already-converted empty ArrayList: new ArrayList<T>()
        if (t.StartsWith("new ArrayList<", StringComparison.Ordinal)
            && t.EndsWith(">()", StringComparison.Ordinal))
        {
            context.AddImport("java.util.Collections");
            return "Collections.emptyList()";
        }

        return expr;
    }

    private string TransformConditionalAccess(ConditionalAccessExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var objExpr = facade.Transform(node.Expression, context);
        var whenNotNull = facade.TransformWhenNotNull(node.WhenNotNull, objExpr, context);

        // Fix 4: if the expression is consumed as a non-nullable primitive, emit its default
        // rather than null (null cannot be unboxed to a primitive in Java).
        string falseBranch = "null";
        if (context.SemanticModel != null)
        {
            var typeInfo = context.SemanticModel.GetTypeInfo(node);
            var convertedType = typeInfo.ConvertedType;
            // ConvertedType is the primitive when implicit unboxing is applied by the compiler.
            if (convertedType?.IsValueType == true
                && convertedType is not INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T })
            {
                falseBranch = GetPrimitiveDefaultValue(convertedType.SpecialType);
            }
        }
        return $"({objExpr} != null ? {whenNotNull} : {falseBranch})";
    }

    private static string GetPrimitiveDefaultValue(SpecialType specialType) => specialType switch
    {
        SpecialType.System_Boolean => "false",
        SpecialType.System_Char => "'\\0'",
        SpecialType.System_Single or SpecialType.System_Double => "0.0",
        SpecialType.System_Byte or SpecialType.System_SByte
            or SpecialType.System_Int16 or SpecialType.System_UInt16
            or SpecialType.System_Int32 or SpecialType.System_UInt32
            or SpecialType.System_Int64 or SpecialType.System_UInt64
            or SpecialType.System_Decimal => "0",
        _ => "null"
    };

    private string TransformAwait(AwaitExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var expr = facade.Transform(node.Expression, context);
        // Map C# await → CompletableFuture.join()
        return $"{expr}.join()";
    }

    private string TransformThrowExpression(ThrowExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var expr = facade.Transform(node.Expression, context);
        // Java has no throw expressions; wrap in a Supplier lambda (works for unchecked exceptions)
        return $"((java.util.function.Supplier<Object>) () -> {{ throw {expr}; }}).get()";
    }

    private string TransformSwitchExpression(SwitchExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var governingExpr = facade.Transform(node.GoverningExpression, context);

        // Build a ternary chain from last arm to first
        string result = "null";
        foreach (var arm in node.Arms.Reverse())
        {
            var armValue = facade.Transform(arm.Expression, context);
            if (arm.Pattern is DiscardPatternSyntax && arm.WhenClause == null)
            {
                result = armValue;
            }
            else
            {
                var condition = BuildSwitchArmCondition(governingExpr, arm.Pattern, context);
                if (arm.WhenClause != null)
                    condition = $"({condition}) && ({facade.Transform(arm.WhenClause.Condition, context)})";
                result = $"({condition} ? {armValue} : {result})";
            }
        }
        return result;
    }

    private string BuildSwitchArmCondition(string expr, PatternSyntax pattern, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        return pattern switch
        {
            ConstantPatternSyntax cp when cp.Expression.IsKind(SyntaxKind.NullLiteralExpression)
                => $"({expr} == null)",
            ConstantPatternSyntax cp
                => $"Objects.equals({expr}, {facade.Transform(cp.Expression, context)})",
            DeclarationPatternSyntax dp
                => BuildDeclarationPatternCondition(expr, dp, context),
            TypePatternSyntax tp
                => $"({expr} instanceof {context.MapTypeFromSyntax(tp.Type)})",
            DiscardPatternSyntax
                => "true",
            UnaryPatternSyntax np when np.OperatorToken.IsKind(SyntaxKind.NotKeyword)
                => $"!({BuildSwitchArmCondition(expr, np.Pattern, context)})",
            // Fix 5: relational patterns (> 0, <= 10, etc.)
            RelationalPatternSyntax rel
                => $"({expr} {rel.OperatorToken.Text} {facade.Transform(rel.Expression, context)})",
            // Fix 5: combined patterns (and, or)
            BinaryPatternSyntax bin when bin.IsKind(SyntaxKind.AndPattern)
                => $"({BuildSwitchArmCondition(expr, bin.Left, context)} && {BuildSwitchArmCondition(expr, bin.Right, context)})",
            BinaryPatternSyntax bin when bin.IsKind(SyntaxKind.OrPattern)
                => $"({BuildSwitchArmCondition(expr, bin.Left, context)} || {BuildSwitchArmCondition(expr, bin.Right, context)})",
            _ => $"/* TODO: pattern {pattern.GetType().Name} */ true"
        };
    }

    private string BuildDeclarationPatternCondition(string expr, DeclarationPatternSyntax dp, ConversionContext context)
    {
        var mappedType = context.MapTypeFromSyntax(dp.Type);
        var designation = dp.Designation switch
        {
            SingleVariableDesignationSyntax sv => ConversionContext.EscapeJavaKeyword(sv.Identifier.Text),
            DiscardDesignationSyntax => "_",
            _ => "_unused"
        };
        return $"({expr} instanceof {mappedType} {designation})";
    }

    // Fix 1: with expression — clone source and apply property setters as pre-statements.
    private string TransformWithExpression(WithExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var sourceExpr = facade.Transform(node.Expression, context);
        var tmpVar = context.GenerateSyntheticName("__withCopy");
        context.AddPreStatement($"var {tmpVar} = {sourceExpr}.clone();");
        foreach (var init in node.Initializer.Expressions)
        {
            if (init is AssignmentExpressionSyntax assignment)
            {
                var propName = assignment.Left.ToString();
                var propValue = facade.Transform(assignment.Right, context);
                var setterName = $"set{char.ToUpperInvariant(propName[0])}{propName.Substring(1)}";
                context.AddPreStatement($"{tmpVar}.{setterName}({propValue});");
            }
        }
        return tmpVar;
    }

    // Fix 2: range expression — emit Arrays.copyOfRange for the standalone case.
    // The common arr[lo..hi] case is handled earlier in ElementAccessTransformer.
    private string TransformRangeExpression(RangeExpressionSyntax node, ConversionContext context)
    {
        if (node.LeftOperand == null && node.RightOperand == null)
            return "/* full range */";
        var facade = ExpressionTransformerFacade.Instance;
        string lo = node.LeftOperand != null ? facade.Transform(node.LeftOperand, context) : "0";
        string hi = node.RightOperand != null ? facade.Transform(node.RightOperand, context) : "/* length */";
        context.AddImport("java.util.Arrays");
        return $"Arrays.copyOfRange(/* array */, {lo}, {hi})";
    }
}
