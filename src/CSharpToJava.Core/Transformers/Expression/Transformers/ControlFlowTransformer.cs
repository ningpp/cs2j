using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles control flow expressions (conditional, conditional access, await, throw, switch, this, base, etc.).
/// </summary>
[TransformerRegistration]
public class ControlFlowTransformer : IIRExpressionTransformer
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

    /// <inheritdoc />
    public JavaExpression TransformToIR(ExpressionSyntax node, ConversionContext context)
    {
        // Ternary → structured JavaConditionalExpression
        if (node is ConditionalExpressionSyntax ternary)
        {
            var facade = ExpressionTransformerFacade.Instance;
            var condition = facade.TransformToIR(ternary.Condition, context);
            var whenTrue = facade.TransformToIR(ternary.WhenTrue, context);
            var whenFalse = facade.TransformToIR(ternary.WhenFalse, context);
            return new JavaConditionalExpression
            {
                Condition = condition,
                WhenTrue = whenTrue,
                WhenFalse = whenFalse
            };
        }

        // Parenthesized → structured JavaParenthesizedExpression
        if (node is ParenthesizedExpressionSyntax paren)
        {
            var inner = ExpressionTransformerFacade.Instance.TransformToIR(paren.Expression, context);
            return new JavaParenthesizedExpression { InnerExpression = inner };
        }

        // this/super → structured
        if (node.IsKind(SyntaxKind.ThisExpression))
            return new JavaThisExpression { IsSuper = false };
        if (node.IsKind(SyntaxKind.BaseExpression))
            return new JavaThisExpression { IsSuper = true };

        return new JavaRawExpression(Transform(node, context));
    }

    private string TransformConditional(ConditionalExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var condition = facade.Transform(node.Condition, context);

        // Snapshot out-holder allocation counts so both ternary branches reuse the
        // same holder name for the same out variable. Without this, each branch
        // allocates its own holder, and the unconditional post-statement read-back
        // from the unexecuted branch's holder overwrites the correct value with null.
        var savedOutCounts = context.MethodState.SnapshotOutHolderCounts();

        var trueExpr = facade.Transform(node.WhenTrue, context);

        // Restore out-holder counts so WhenFalse reuses the same holder names
        context.MethodState.RestoreOutHolderCounts(savedOutCounts);

        var falseExpr = facade.Transform(node.WhenFalse, context);

        // Deduplicate post-statements that both branches may have queued
        context.MethodState.DeduplicatePostStatements();

        // If the conditional expression is typed as IEnumerable/ICollection-like, but branches
        // are stream chains, collect each branch so both sides become Iterable-compatible.
        if (context.SemanticModel != null)
        {
            var converted = context.GetTypeInfo(node).ConvertedType;
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

        // In Java, assignment has lower precedence than the ternary operator.
        // C# allows `cond ? a : b = expr` but Java parses it as `(cond ? a : b) = expr`.
        // Wrap the false branch in parentheses when it contains an assignment.
        if (node.WhenFalse is AssignmentExpressionSyntax)
            falseExpr = $"({falseExpr})";

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
        return $"{expr}.collect(Collectors.toCollection(() -> new ArrayList<>()))";
    }

    private static string AdaptZeroArrayToEmptyIterable(string expr, ConversionContext context)
    {
        var t = expr.Trim();
        if (t.Contains("Array.newInstance(", StringComparison.Ordinal)
            && t.Contains(", 0)", StringComparison.Ordinal))
        {
            context.AddImport("java.util.Collections");
            return "Collections.emptyList()";
        }

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
            var typeInfo = context.GetTypeInfo(node);
            var convertedType = typeInfo.ConvertedType;
            // ConvertedType is the primitive when implicit unboxing is applied by the compiler.
            if (convertedType?.IsValueType == true
                && convertedType is not INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T })
            {
                falseBranch = GetPrimitiveDefaultValue(convertedType.SpecialType);
                if (convertedType.SpecialType == SpecialType.System_Decimal)
                    context.AddImport("io.github.ningpp.compat.Decimal");
            }
        }
        return $"({objExpr} != null ? {whenNotNull} : {falseBranch})";
    }

    private static string GetPrimitiveDefaultValue(SpecialType specialType) => specialType switch
    {
        SpecialType.System_Boolean => "false",
        SpecialType.System_Char => "'\\0'",
        SpecialType.System_Single or SpecialType.System_Double => "0.0",
        SpecialType.System_Decimal => "Decimal.ZERO",
        SpecialType.System_Byte or SpecialType.System_SByte
            or SpecialType.System_Int16 or SpecialType.System_UInt16
            or SpecialType.System_Int32 or SpecialType.System_UInt32
            or SpecialType.System_Int64 or SpecialType.System_UInt64 => "0",
        _ => "null"
    };

    private static string AddObjectsImportAndBuildEquals(string left, string right, ConversionContext context)
    {
        context.AddImport("java.util.Objects");
        return $"Objects.equals({left}, {right})";
    }

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
        // Java has no throw expressions; wrap in a Supplier lambda (works for unchecked exceptions).
        // Use the context type to determine the Supplier type parameter so that
        // ternary expressions like "a ? x : throw ..." compile correctly when x is int, etc.
        var contextType = context.GetTypeInfo(node).ConvertedType;
        string supplierType = "Object";
        if (contextType != null)
        {
            var javaType = context.MapType(contextType);
            if (javaType is "int" or "long" or "double" or "float" or "short" or "byte" or "char" or "boolean")
                supplierType = javaType switch
                {
                    "int" => "Integer",
                    "long" => "Long",
                    "double" => "Double",
                    "float" => "Float",
                    "short" => "Short",
                    "byte" => "Byte",
                    "char" => "Character",
                    "boolean" => "Boolean",
                    _ => "Object"
                };
            else if (javaType is "Integer" or "Long" or "Double" or "Float" or "Short" or "Byte" or "Character" or "Boolean")
                supplierType = javaType;
            else if (contextType.SpecialType == SpecialType.System_Int32)
                supplierType = "Integer";
            else if (contextType.SpecialType == SpecialType.System_Int64)
                supplierType = "Long";
            else if (contextType.SpecialType == SpecialType.System_Double)
                supplierType = "Double";
            else if (contextType.SpecialType == SpecialType.System_Single)
                supplierType = "Float";
            else if (contextType.SpecialType == SpecialType.System_Boolean)
                supplierType = "Boolean";
            else if (contextType.SpecialType == SpecialType.System_Char)
                supplierType = "Character";
            else if (contextType.SpecialType == SpecialType.System_Byte)
                supplierType = "Byte";
            else if (contextType.SpecialType == SpecialType.System_Int16)
                supplierType = "Short";
        }
        return $"((java.util.function.Supplier<{supplierType}>) () -> {{ throw {expr}; }}).get()";
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
                => AddObjectsImportAndBuildEquals(expr, facade.Transform(cp.Expression, context), context),
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
            RecursivePatternSyntax recPattern
                => BuildRecursivePatternCondition(expr, recPattern, context),
            VarPatternSyntax varPattern
                => BuildVarPatternCondition(expr, varPattern),
            ParenthesizedPatternSyntax parenPattern
                => BuildSwitchArmCondition(expr, parenPattern.Pattern, context),
            _ => $"/* TODO: pattern {pattern.GetType().Name} */ true"
        };
    }

    private string BuildDeclarationPatternCondition(string expr, DeclarationPatternSyntax dp, ConversionContext context)
    {
        var mappedType = context.MapTypeFromSyntax(dp.Type);
        var designation = dp.Designation switch
        {
            SingleVariableDesignationSyntax sv => ConversionContext.EscapeJavaKeyword(sv.Identifier.Text),
            DiscardDesignationSyntax => "_unused",
            _ => "_unused"
        };
        return $"({expr} instanceof {mappedType} {designation})";
    }

    private string BuildRecursivePatternCondition(string expr, RecursivePatternSyntax pattern, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        string? typeName = null;
        if (pattern.Type != null)
        {
            var typeInfo = context.GetTypeInfo(pattern.Type);
            typeName = typeInfo.Type != null
                ? context.MapType(typeInfo.Type)
                : context.MapTypeFromSyntax(pattern.Type);
        }

        var conditions = new List<string>();
        if (typeName != null)
            conditions.Add($"{expr} instanceof {typeName}");

        if (pattern.PropertyPatternClause != null && typeName != null)
        {
            var cast = $"(({typeName}){expr})";
            foreach (var sub in pattern.PropertyPatternClause.Subpatterns)
            {
                string? propName = sub.NameColon?.Name.Identifier.Text
                    ?? (sub.ExpressionColon?.Expression is IdentifierNameSyntax idName ? idName.Identifier.Text : null);
                if (propName == null) continue;
                string getter = $"{cast}.get{char.ToUpperInvariant(propName[0])}{propName[1..]}()";
                string cond = BuildSwitchArmCondition(getter, sub.Pattern, context);
                conditions.Add(cond);
            }
        }

        if (pattern.Designation is SingleVariableDesignationSyntax sv)
        {
            var varName = ConversionContext.EscapeJavaKeyword(sv.Identifier.Text);
            if (typeName != null)
                conditions.Add($"({varName} = ({typeName}){expr}) != null");
        }

        return conditions.Count > 0
            ? string.Join(" && ", conditions)
            : "true";
    }

    private string BuildVarPatternCondition(string expr, VarPatternSyntax pattern)
    {
        // `var x` always matches, assign x = expr
        var designation = pattern.Designation.ToString();
        if (designation != "_")
            return $"({ConversionContext.EscapeJavaKeyword(designation)} = {expr}) != null || true";
        return "true";
    }

    // with expression — for Java records, build a new record constructor call with overridden fields;
    // for non-record types (immutable class / record struct), fall back to clone + setters.
    private string TransformWithExpression(WithExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var sourceExpr = facade.Transform(node.Expression, context);

        // Collect the property overrides from the initializer
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var init in node.Initializer.Expressions)
        {
            if (init is AssignmentExpressionSyntax assignment)
            {
                var propName = assignment.Left.ToString();
                var propValue = facade.Transform(assignment.Right, context);
                overrides[propName] = propValue;
            }
        }

        // Check if the source type is a record (and we're emitting Java records)
        var typeInfo = context.GetTypeInfo(node.Expression);
        var namedType = typeInfo.Type as INamedTypeSymbol;
        bool isJavaRecord = namedType?.IsRecord == true
                            && context.Options.UseRecords
                            && context.Options.TargetJavaVersion >= JavaVersion.Java25
                            && !namedType.IsAbstract;

        if (isJavaRecord && namedType != null)
        {
            // Java records: build new constructor call with overridden components
            // e.g. p with { X = 5 } → new Point(5, p.y())
            var typeName = context.MapType(namedType);
            var args = new List<string>();
            foreach (var member in namedType.GetMembers())
            {
                if (member is IPropertySymbol prop && prop.IsReadOnly
                    && !prop.IsStatic && !prop.IsIndexer
                    && namedType.Constructors.Any(c => c.Parameters.Any(p =>
                        string.Equals(p.Name, prop.Name, StringComparison.OrdinalIgnoreCase))))
                {
                    if (overrides.TryGetValue(prop.Name, out var overrideValue))
                    {
                        args.Add(overrideValue);
                    }
                    else
                    {
                        // Access via record accessor method (camelCase name)
                        var accessorName = char.ToLower(prop.Name[0]) + prop.Name.Substring(1);
                        args.Add($"{sourceExpr}.{accessorName}()");
                    }
                }
            }
            return $"new {typeName}({string.Join(", ", args)})";
        }
        else
        {
            // Non-record path: clone + setters
            var tmpVar = context.GenerateSyntheticName("__withCopy");
            context.AddPreStatement($"var {tmpVar} = {sourceExpr}.clone();");
            foreach (var (propName, propValue) in overrides)
            {
                var setterName = $"set{char.ToUpperInvariant(propName[0])}{propName.Substring(1)}";
                context.AddPreStatement($"{tmpVar}.{setterName}({propValue});");
            }
            return tmpVar;
        }
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
