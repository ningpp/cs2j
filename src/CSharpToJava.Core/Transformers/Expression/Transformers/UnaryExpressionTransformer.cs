using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Expression.Utilities;
using System.Collections.Generic;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles unary expressions (prefix/postfix operators, address of, pointer indirection).
/// </summary>
[TransformerRegistration]
public class UnaryExpressionTransformer : IIRExpressionTransformer
{
    static UnaryExpressionTransformer()
    {
        ExpressionTransformerRegistry.Register(new[]
        {
            SyntaxKind.UnaryPlusExpression,
            SyntaxKind.UnaryMinusExpression,
            SyntaxKind.LogicalNotExpression,
            SyntaxKind.BitwiseNotExpression,
            SyntaxKind.AddressOfExpression,
            SyntaxKind.PointerIndirectionExpression,
            SyntaxKind.PostIncrementExpression,
            SyntaxKind.PostDecrementExpression,
            SyntaxKind.PreIncrementExpression,
            SyntaxKind.PreDecrementExpression,
            SyntaxKind.SuppressNullableWarningExpression
        }, new UnaryExpressionTransformer());
    }

    private static readonly Lazy<UnaryExpressionTransformer> _instance = new(() => new());
    public static UnaryExpressionTransformer Instance => _instance.Value;

    public string Transform(ExpressionSyntax node, ConversionContext context)
        => node.Kind() switch
        {
            SyntaxKind.UnaryPlusExpression => TransformUnaryExpression((PrefixUnaryExpressionSyntax)node, "+", context),
            SyntaxKind.UnaryMinusExpression => TransformUnaryExpression((PrefixUnaryExpressionSyntax)node, "-", context),
            SyntaxKind.LogicalNotExpression => TransformLogicalNot((PrefixUnaryExpressionSyntax)node, context),
            SyntaxKind.BitwiseNotExpression => TransformUnaryExpression((PrefixUnaryExpressionSyntax)node, "~", context),
            SyntaxKind.AddressOfExpression => TransformAddressOf((PrefixUnaryExpressionSyntax)node, context),
            SyntaxKind.PointerIndirectionExpression => TransformPointerIndirection((PrefixUnaryExpressionSyntax)node, context),
            SyntaxKind.PostIncrementExpression => TransformPostfix((PostfixUnaryExpressionSyntax)node, "++", context),
            SyntaxKind.PostDecrementExpression => TransformPostfix((PostfixUnaryExpressionSyntax)node, "--", context),
            SyntaxKind.PreIncrementExpression => TransformPrefix((PrefixUnaryExpressionSyntax)node, "++", context),
            SyntaxKind.PreDecrementExpression => TransformPrefix((PrefixUnaryExpressionSyntax)node, "--", context),
            SyntaxKind.SuppressNullableWarningExpression => ExpressionTransformerFacade.Instance.Transform(((PostfixUnaryExpressionSyntax)node).Operand, context),
            _ => throw new NotSupportedException($"Unary expression kind {node.Kind()} not supported.")
        };

    /// <inheritdoc />
    public JavaExpression TransformToIR(ExpressionSyntax node, ConversionContext context)
    {
        // For simple prefix/postfix ops that map 1:1 to Java, produce structured IR
        var (op, isPostfix) = node.Kind() switch
        {
            SyntaxKind.UnaryPlusExpression => ("+", false),
            SyntaxKind.UnaryMinusExpression => ("-", false),
            SyntaxKind.LogicalNotExpression => ("!", false),
            SyntaxKind.BitwiseNotExpression => ("~", false),
            SyntaxKind.PostIncrementExpression => ("++", true),
            SyntaxKind.PostDecrementExpression => ("--", true),
            SyntaxKind.PreIncrementExpression => ("++", false),
            SyntaxKind.PreDecrementExpression => ("--", false),
            _ => (null, false)
        };

        if (op != null)
        {
            // Check for user-defined operators or property increment — fall back to raw for those
            bool isUserDefined = false;
            if (context.SemanticModel != null)
            {
                var symbolInfo = context.GetSymbolInfo(node);
                if (symbolInfo.Symbol is IMethodSymbol ms && ms.ContainingType != null && !IsBuiltInType(ms.ContainingType))
                    isUserDefined = true;
            }

            if (!isUserDefined)
            {
                var operandSyntax = node switch
                {
                    PrefixUnaryExpressionSyntax prefix => prefix.Operand,
                    PostfixUnaryExpressionSyntax postfix => postfix.Operand,
                    _ => null
                };

                // Property/indexer increment needs complex hoisting — use raw fallback
                bool isPropertyTarget = operandSyntax != null && context.SemanticModel != null
                    && context.GetSymbolInfo(operandSyntax).Symbol is IPropertySymbol;

                bool isDecimalTarget = operandSyntax != null && context.SemanticModel != null
                    && ExpressionTransformerHelpers.IsDecimalType(context.GetTypeInfo(operandSyntax).Type);

                // For LogicalNotExpression (!), verify the operand is boolean.
                // C# allows ! on int (0→true, non-zero→false) but Java requires
                // boolean. Non-boolean operands must fall back to the raw string path
                // which calls TransformLogicalNot() → rewrites to (operand == 0).
                if (node.Kind() == SyntaxKind.LogicalNotExpression
                    && operandSyntax != null
                    && !IsBooleanExpression(operandSyntax, context))
                {
                    return new JavaRawExpression(Transform(node, context));
                }

                if (!isPropertyTarget && !isDecimalTarget && operandSyntax != null)
                {
                    var operandIR = ExpressionTransformerFacade.Instance.TransformToIR(operandSyntax, context);
                    return new JavaUnaryExpression
                    {
                        Operator = op,
                        Operand = operandIR,
                        IsPostfix = isPostfix
                    };
                }
            }
        }

        // Complex cases: fall back to string-based transform
        return new JavaRawExpression(Transform(node, context));
    }

    private string TransformUnaryExpression(PrefixUnaryExpressionSyntax node, string op, ConversionContext context)
    {
        // Check if this is a user-defined unary operator
        if (context.SemanticModel != null)
        {
            var symbolInfo = context.GetSymbolInfo(node);
            if (symbolInfo.Symbol is IMethodSymbol methodSymbol && methodSymbol.ContainingType != null)
            {
                // Only convert to method call if it's a user-defined type
                if (!IsBuiltInType(methodSymbol.ContainingType))
                {
                    return TransformUserDefinedUnaryOperator(node, methodSymbol, context);
                }
            }
        }

        var facade = ExpressionTransformerFacade.Instance;
        var operand = facade.Transform(node.Operand, context);

        if (op == "-" && IsDecimalExpression(node.Operand, context))
            return $"{operand}.negate()";

        if (op == "+" && IsDecimalExpression(node.Operand, context))
            return operand;

        // Wrap in parentheses if operand is a binary expression
        if (node.Operand is BinaryExpressionSyntax)
        {
            operand = $"({operand})";
        }

        // Special case: unary + is not valid on non-numeric types in Java — strip it
        if (op == "+" && context.SemanticModel != null)
        {
            var operandType = context.GetTypeInfo(node.Operand).Type;
            if (!IsNumericType(operandType))
                return operand;
        }

        return $"{op}{operand}";
    }

    /// <summary>
    /// Handles logical not (!). In C# the ! operator can be applied to int
    /// (0 → true, non-zero → false) but Java requires a boolean operand.
    /// When the operand is non-boolean, rewrite to (operand == 0).
    /// </summary>
    private string TransformLogicalNot(PrefixUnaryExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var operand = facade.Transform(node.Operand, context);

        if (IsBooleanExpression(node.Operand, context))
            return $"!{operand}";

        // Non-boolean: rewrite !x → (x == 0)
        return $"({operand} == 0)";
    }

    /// <summary>
    /// Determines whether an expression has boolean type.
    /// Uses GetTypeInfo first; falls back to GetSymbolInfo for method invocations,
    /// which is more reliable when the semantic model spans multiple files.
    /// </summary>
    private static bool IsBooleanExpression(ExpressionSyntax expr, ConversionContext context)
    {
        if (context.SemanticModel == null)
            return false;

        var typeInfo = context.GetTypeInfo(expr);
        if (typeInfo.Type != null && typeInfo.Type.SpecialType == SpecialType.System_Boolean)
            return true;

        // GetTypeInfo on InvocationExpression can fail to resolve return type
        // in cross-file scenarios. Fall back to checking the method symbol directly.
        if (expr is InvocationExpressionSyntax invExpr)
        {
            var symbolInfo = context.GetSymbolInfo(invExpr);
            if (symbolInfo.Symbol is IMethodSymbol method
                && method.ReturnType.SpecialType == SpecialType.System_Boolean)
                return true;

            // Fallback: when the semantic model can't resolve the method
            // (e.g. after LINQ rewrite in project pipeline), use a naming
            // heuristic — methods starting with "Is", "Has", "Can", etc.
            // typically return bool in C#.
            if (invExpr.Expression is MemberAccessExpressionSyntax ma
                && IsBooleanMethodName(ma.Name.Identifier.Text))
                return true;
        }

        // Fallback for property/field member access: when the semantic model
        // can't resolve the type (e.g. compat library types like StringHelper),
        // use naming heuristic — C# properties starting with "Is" are typically bool.
        if (expr is MemberAccessExpressionSyntax memberAccess
            && IsBooleanMethodName(memberAccess.Name.Identifier.Text))
            return true;

        return false;
    }

    private static bool IsBooleanMethodName(string methodName)
    {
        return methodName.StartsWith("Is", StringComparison.Ordinal)
            || methodName.StartsWith("Has", StringComparison.Ordinal)
            || methodName.StartsWith("Can", StringComparison.Ordinal)
            || methodName.StartsWith("Should", StringComparison.Ordinal)
            || methodName.StartsWith("Are", StringComparison.Ordinal)
            || methodName == "Exists"
            || methodName == "Contains"
            || methodName == "Equals"
            || methodName == "StartsWith"
            || methodName == "EndsWith"
            || methodName == "MoveNext"
            || methodName == "TryParse";
    }

    private static bool IsNumericType(ITypeSymbol? type)
    {
        if (type == null) return false;
        return type.SpecialType is
            SpecialType.System_Int32 or SpecialType.System_Int64 or
            SpecialType.System_Int16 or SpecialType.System_Byte or
            SpecialType.System_SByte or SpecialType.System_UInt32 or
            SpecialType.System_UInt64 or SpecialType.System_UInt16 or
            SpecialType.System_Single or SpecialType.System_Double or
            SpecialType.System_Decimal or SpecialType.System_Char;
    }

    private static bool IsDecimalExpression(ExpressionSyntax expression, ConversionContext context)
        => context.SemanticModel != null
            && context.GetTypeInfo(expression).Type?.SpecialType == SpecialType.System_Decimal;

    private static bool IsBuiltInType(INamedTypeSymbol type)
    {
        var typeName = type.ToDisplayString();
        return type.TypeKind == TypeKind.Enum ||
               type.SpecialType != SpecialType.None ||
               BuiltInTypeNames.Contains(typeName);
    }

    private static readonly HashSet<string> BuiltInTypeNames = new(StringComparer.Ordinal)
    {
        "int", "long", "short", "byte", "sbyte", "uint", "ulong", "ushort",
        "float", "double", "decimal",
        "bool", "boolean",
        "char", "string",
        "object",
        "System.Int32", "System.Int64", "System.Int16", "System.Byte",
        "System.SByte", "System.UInt32", "System.UInt64", "System.UInt16",
        "System.Single", "System.Double", "System.Decimal",
        "System.Boolean", "System.Char", "System.String", "System.Object"
    };

    private string TransformUserDefinedUnaryOperator(PrefixUnaryExpressionSyntax node, IMethodSymbol operatorSymbol, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var operand = facade.Transform(node.Operand, context);

        return TransformUserDefinedUnaryOperatorCore(operand, operatorSymbol, context);
    }

    private string TransformUserDefinedUnaryOperator(PostfixUnaryExpressionSyntax node, IMethodSymbol operatorSymbol, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var operand = facade.Transform(node.Operand, context);

        return TransformUserDefinedUnaryOperatorCore(operand, operatorSymbol, context);
    }

    private string TransformUserDefinedUnaryOperatorCore(string operand, IMethodSymbol operatorSymbol, ConversionContext context)
    {
        // Get the Java method name for this operator
        var javaMethodName = GetOperatorMethodName(operatorSymbol);
        var containingType = context.MapType(operatorSymbol.ContainingType);
        var currentType = context.CurrentType?.Name;

        if (containingType == currentType)
        {
            return $"{javaMethodName}({operand})";
        }
        else
        {
            return $"{containingType}.{javaMethodName}({operand})";
        }
    }

    private static string GetOperatorMethodName(IMethodSymbol operatorSymbol)
    {
        return CSharpToJava.Core.Transformers.Member.OperatorTransformer.OpSymbolToJavaName
            .TryGetValue(operatorSymbol.Name, out var name)
            ? name
            : operatorSymbol.Name;
    }

    private static bool IsInSameCompilationUnit(ConversionContext context, INamedTypeSymbol type)
    {
        var currentNs = context.CurrentNamespace;
        var typeNs = type.ContainingNamespace?.ToDisplayString() ?? "";
        return currentNs == typeNs || string.IsNullOrEmpty(typeNs);
    }

    private string TransformAddressOf(PrefixUnaryExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var operand = facade.Transform(node.Operand, context);

        if (context.IsInFixedScope)
        {
            context.Diagnostics.Warning("Address-of operator (&) in fixed scope - using MemorySegment offset", node.GetLocation());
            return operand;
        }

        if (TryTransformAddressOfScalarLocal(node, context, out var segmentName))
            return segmentName;

        context.Diagnostics.Warning("Address-of operator (&) has no Java equivalent - converting to unsafe memory access", node.GetLocation());
        return $"/* C# addressof — no Java equivalent: {operand} */";
    }

    private static bool TryTransformAddressOfScalarLocal(
        PrefixUnaryExpressionSyntax node, ConversionContext context, out string segmentName)
    {
        segmentName = string.Empty;

        if (node.Operand is not IdentifierNameSyntax identifier)
            return false;

        var sourceName = identifier.Identifier.Text;

        if (context.SemanticModel == null)
            return false;

        var symbol = context.GetSymbolInfo(identifier).Symbol;
        if (symbol is not (ILocalSymbol or IParameterSymbol))
            return false;

        var typeSymbol = symbol switch
        {
            ILocalSymbol local => local.Type,
            IParameterSymbol param => param.Type,
            _ => null
        };

        if (typeSymbol == null || !IsAddressableScalar(typeSymbol))
            return false;

        if (context.TryGetAddressOfScratchSegment(sourceName, out var existingSegment))
        {
            segmentName = existingSegment;
            return true;
        }

        var csharpElementType = GetCSharpElementTypeName(typeSymbol);
        var pointerInfo = FfmHelper.CreatePointerInfo(sourceName, csharpElementType);
        segmentName = $"_addr_{sourceName}";
        var init = FfmHelper.GenerateAddressOfScratchInit(segmentName, sourceName, pointerInfo);
        context.AddPreStatement(init);
        context.RegisterAddressOfScratchSegment(sourceName, segmentName, pointerInfo);
        foreach (var imp in FfmHelper.GetRequiredImports(false))
            context.AddImport(imp);

        return true;
    }

    private static bool IsAddressableScalar(ITypeSymbol type)
    {
        return type.SpecialType is
            SpecialType.System_Byte or SpecialType.System_SByte or
            SpecialType.System_Int16 or SpecialType.System_UInt16 or
            SpecialType.System_Int32 or SpecialType.System_UInt32 or
            SpecialType.System_Int64 or SpecialType.System_UInt64 or
            SpecialType.System_Single or SpecialType.System_Double or
            SpecialType.System_Boolean or SpecialType.System_Char;
    }

    private static string GetCSharpElementTypeName(ITypeSymbol type)
    {
        return type.SpecialType switch
        {
            SpecialType.System_Byte => "byte",
            SpecialType.System_SByte => "sbyte",
            SpecialType.System_Int16 => "short",
            SpecialType.System_UInt16 => "ushort",
            SpecialType.System_Int32 => "int",
            SpecialType.System_UInt32 => "uint",
            SpecialType.System_Int64 => "long",
            SpecialType.System_UInt64 => "ulong",
            SpecialType.System_Single => "float",
            SpecialType.System_Double => "double",
            SpecialType.System_Boolean => "bool",
            SpecialType.System_Char => "char",
            _ => type.Name
        };
    }

    private string TransformPointerIndirection(PrefixUnaryExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;

        // Detect *(ptr - N) pattern at the C# AST level
        var unwrappedOperand = node.Operand;
        while (unwrappedOperand is ParenthesizedExpressionSyntax paren)
            unwrappedOperand = paren.Expression;

        if (unwrappedOperand is BinaryExpressionSyntax binary
            && binary.OperatorToken.IsKind(SyntaxKind.MinusToken))
        {
            var leftType = context.GetTypeInfo(binary.Left).Type;
            if (leftType is IPointerTypeSymbol)
            {
                var leftExpr = facade.Transform(binary.Left, context);
                var rightExpr = facade.Transform(binary.Right, context);
                var subPointerInfo = context.FindPointerInfo(leftExpr.Trim());
                if (subPointerInfo == null && leftType is IPointerTypeSymbol ptrType)
                {
                    var elementTypeName = GetCSharpElementTypeName(ptrType.PointedAtType);
                    subPointerInfo = FfmHelper.CreatePointerInfo(leftExpr.Trim(), elementTypeName);
                }

                if (subPointerInfo != null)
                {
                    // Prefer base segment over backtrack variable.
                    // Backtrack variables are defined as pre-statements in one switch-case
                    // but may be referenced in another, causing Java "may not have been initialized"
                    // errors in state machine (goto→switch) conversion.
                    if (context.TryGetPointerBase(leftExpr.Trim(), out var baseVar))
                    {
                        string byteOffsetExpr = subPointerInfo.ElementSize == 1
                            ? $"{leftExpr}.address() - {baseVar}.address() - {rightExpr}"
                            : $"{leftExpr}.address() - {baseVar}.address() - (long)({rightExpr}) * {subPointerInfo.ElementSize}";
                        return FfmHelper.GeneratePointerReadWithBoundsCheck(baseVar, subPointerInfo, byteOffsetExpr, baseVar, context);
                    }

                    // Fallback to backtrack variable (e.g., *ptr++ then *(ptr - 1))
                    if (int.TryParse(rightExpr.Trim(), out int stepCount)
                        && context.TryGetPointerBacktrackVar(leftExpr.Trim(), stepCount, out var backtrackVar))
                    {
                        if (context.TryGetPointerBase(backtrackVar.Trim(), out var btBaseVar))
                        {
                            return FfmHelper.GeneratePointerReadWithBoundsCheck(backtrackVar, subPointerInfo, "0", btBaseVar, context);
                        }
                        return FfmHelper.GeneratePointerRead(backtrackVar, subPointerInfo, "0");
                    }
                }
            }
        }

        var operand = facade.Transform(node.Operand, context);

        var operandText = operand.Trim();

        // Check if the operand has a deferred side effect from a short-circuit context.
        // This happens when *ptr++ appears inside || or && right operand.
        // The pointer increment must be inlined into the short-circuit path.
        if (context.TryConsumeShortCircuitDeferredSideEffect(operandText, out var deferredSideEffect))
        {
            var pointerInfo = context.FindPointerInfo(operandText);
            if (pointerInfo == null)
            {
                var operandType = context.GetTypeInfo(node.Operand).Type;
                if (operandType is IPointerTypeSymbol ptrType)
                {
                    var elementTypeName = GetCSharpElementTypeName(ptrType.PointedAtType);
                    pointerInfo = FfmHelper.CreatePointerInfo(operandText, elementTypeName);
                }
            }
            if (pointerInfo != null)
            {
                // Generate: (deferredSideEffect) != null ? readValue : defaultValue
                // The assignment always succeeds (non-null), so the read always executes,
                // but the side effect (pointer increment) is now inside the short-circuit path.
                var readExpr = context.TryGetPointerBase(operandText, out var baseVar)
                    ? FfmHelper.GeneratePointerReadWithBoundsCheck(operandText, pointerInfo, "0", baseVar, context)
                    : FfmHelper.GeneratePointerRead(operandText, pointerInfo, "0");
                var defaultValue = pointerInfo.ElementSize switch
                {
                    1 => "(byte)0",
                    2 => "'\\0'",
                    4 => "0",
                    8 => "0L",
                    _ => "0"
                };
                return $"(({deferredSideEffect}) != null ? {readExpr} : {defaultValue})";
            }
        }

        var pointerInfo2 = context.FindPointerInfo(operandText);
        if (pointerInfo2 == null)
        {
            var operandType = context.GetTypeInfo(node.Operand).Type;
            if (operandType is IPointerTypeSymbol ptrType)
            {
                var elementTypeName = GetCSharpElementTypeName(ptrType.PointedAtType);
                pointerInfo2 = FfmHelper.CreatePointerInfo(operandText, elementTypeName);
            }
        }
        if (pointerInfo2 != null)
        {
            if (context.TryGetPointerBase(operandText, out var baseVar))
            {
                return FfmHelper.GeneratePointerReadWithBoundsCheck(operandText, pointerInfo2, "0", baseVar, context);
            }
            return FfmHelper.GeneratePointerRead(operandText, pointerInfo2, "0");
        }

        context.Diagnostics.Warning("Pointer indirection operator (*) has no Java equivalent - unsafe code not supported", node.GetLocation());
        return $"/* unsafe: pointer deref */ {operand}";
    }

    private string TransformPostfix(PostfixUnaryExpressionSyntax node, string op, ConversionContext context)
    {
        if (op == "++" || op == "--")
        {
            var operandType = context.GetTypeInfo(node.Operand).Type;
            if (operandType is IPointerTypeSymbol ptrType)
            {
                var facade0 = ExpressionTransformerFacade.Instance;
                var operandExpr = facade0.Transform(node.Operand, context);
                var pointerInfo = context.FindPointerInfo(operandExpr.Trim());
                if (pointerInfo == null)
                {
                    var elementTypeName = GetCSharpElementTypeName(ptrType.PointedAtType);
                    pointerInfo = FfmHelper.CreatePointerInfo(operandExpr.Trim(), elementTypeName);
                }
                if (pointerInfo != null)
                {
                    long delta = op == "++" ? pointerInfo.ElementSize : -pointerInfo.ElementSize;

                    string sliceExpr;
                    if (context.TryGetPointerBase(operandExpr.Trim(), out var baseVar))
                    {
                        // Use base segment for pointer arithmetic with out-of-bounds protection.
                        // C# allows one-past-end pointers (used only for address comparison),
                        // but Java's MemorySegment.asSlice throws if offset > byteSize or offset < 0.
                        // When the offset exceeds the base segment size or goes negative,
                        // create a zero-length address-only segment via MemorySegment.ofAddress().
                        var offsetExpr = $"{operandExpr}.address() - {baseVar}.address() + {delta}";
                        sliceExpr = $"({offsetExpr} >= 0 && {offsetExpr} <= {baseVar}.byteSize() ? {baseVar}.asSlice({offsetExpr}) : MemorySegment.ofAddress({operandExpr}.address() + {delta}))";
                    }
                    else
                    {
                        // No base segment: use MemorySegment.ofAddress since asSlice doesn't support negative offsets
                        sliceExpr = $"MemorySegment.ofAddress({operandExpr}.address() + {delta})";
                    }

                    if (IsDiscardedValueContext(node))
                        return $"{operandExpr} = {sliceExpr}";

                    var tmp = context.GenerateSyntheticName("_ptrPost");
                    context.AddImport("java.lang.foreign.MemorySegment");

                    if (context.IsInShortCircuitOperand)
                    {
                        // In a short-circuit context (|| or && right operand), we cannot
                        // use pre-statements for the pointer increment because they would
                        // execute unconditionally before the if condition, breaking
                        // short-circuit semantics. Instead, register the increment as a
                        // deferred side effect. The parent expression (e.g. *ptr++ via
                        // TransformPointerIndirection) will consume it and inline it into
                        // the short-circuit path.
                        context.AddPreStatement($"MemorySegment {tmp} = {operandExpr}");
                        context.RegisterShortCircuitDeferredSideEffect(tmp, $"{operandExpr} = {sliceExpr}");
                        // Register backtrack: after ptr++, *(ptr - 1) should use tmp
                        context.RegisterPointerBacktrackVar(operandExpr.Trim(), 1, tmp);
                        if (delta < 0)
                            context.InvalidatePointerBacktrackVars(operandExpr.Trim());
                        return tmp;
                    }
                    else
                    {
                        context.AddPreStatement($"MemorySegment {tmp} = {operandExpr}");
                        context.AddPreStatementAllowDuplicate($"{operandExpr} = {sliceExpr}");
                        // Register backtrack: after ptr++, *(ptr - 1) should use tmp
                        context.RegisterPointerBacktrackVar(operandExpr.Trim(), 1, tmp);
                        if (delta < 0)
                            context.InvalidatePointerBacktrackVars(operandExpr.Trim());
                        return tmp;
                    }
                }
            }
        }

        // Statement context: rewrite property/indexer in place (no return value needed)
        if (node.Parent is ExpressionStatementSyntax
            && TryTransformDecimalIncrementAsAssignment(node.Operand, op, context, out var decimalRewrite))
        {
            return decimalRewrite;
        }
        if (node.Parent is ExpressionStatementSyntax
            && TryTransformPropertyIncrementAsSetter(node.Operand, op, context, out var rewritten))
        {
            return rewritten;
        }

        if (node.Parent is ExpressionStatementSyntax
            && TryTransformIndexerIncrementAsMutation(node.Operand, op, context, out var indexerRewrite))
        {
            return indexerRewrite;
        }

        // Expression context: hoist property increment to pre-statement, return old value
        if (node.Parent is not ExpressionStatementSyntax
            && TryBuildDecimalHoistForPostfix(node.Operand, op, context, out var decimalHoisted))
        {
            return decimalHoisted;
        }

        if (node.Parent is not ExpressionStatementSyntax
            && TryTransformPropertyIncrementAsSetter(node.Operand, op, context, out var propRewrite))
        {
            // propRewrite = "recv.setter(recv.getter() ± 1)" — we need old value
            // Hoist: var _t = recv.getter(); recv.setter(_t ± 1); return _t;
            if (TryBuildPropertyHoistForPostfix(node.Operand, op, context, out var hoisted))
                return hoisted;
        }

        // Expression context: hoist indexer increment to pre-statement, return old value
        if (node.Parent is not ExpressionStatementSyntax
            && TryTransformIndexerIncrementAsMutation(node.Operand, op, context, out var idxRewrite))
        {
            if (TryBuildIndexerHoistForPostfix(node.Operand, op, context, out var hoisted))
                return hoisted;
        }

        // Check if this is a user-defined postfix operator (++, --)
        if (context.SemanticModel != null)
        {
            var symbolInfo = context.GetSymbolInfo(node);
            if (symbolInfo.Symbol is IMethodSymbol methodSymbol && methodSymbol.ContainingType != null)
            {
                // Only convert to method call if it's a user-defined type
                if (!IsBuiltInType(methodSymbol.ContainingType))
                {
                    return TransformUserDefinedPostfixOperator(node, methodSymbol, context);
                }
            }
        }

        // C# unsigned types (ushort/byte/uint/ulong) wrap around on overflow,
        // but Java signed types do not. For ++/-- on unsigned variables, we need
        // to add bitmask to simulate wrap-around semantics.
        // Postfix: i++/i-- returns old value, then applies mask.
        if (context.SemanticModel != null && (op == "++" || op == "--"))
        {
            var postfixType = context.GetTypeInfo(node.Operand).Type;
            if (postfixType?.SpecialType is SpecialType.System_UInt16 or SpecialType.System_Byte
                or SpecialType.System_UInt32)
            {
                var postfixFacade = ExpressionTransformerFacade.Instance;
                var postfixOperand = postfixFacade.Transform(node.Operand, context);
                var postfixDelta = op == "++" ? "+ 1" : "- 1";
                string maskExpr = postfixType.SpecialType switch
                {
                    SpecialType.System_UInt16 => $"(((int)({postfixOperand} {postfixDelta})) & 0xFFFF)",
                    SpecialType.System_Byte => $"({postfixOperand} {postfixDelta}) & 0xFF",
                    SpecialType.System_UInt32 => $"(int)(({postfixOperand} {postfixDelta}) & 0xFFFFFFFFL)",
                    _ => $"{postfixOperand} {postfixDelta}"
                };
                // In discarded value context (statement or for-incrementor),
                // we can just use the assignment form
                if (IsDiscardedValueContext(node))
                {
                    return $"{postfixOperand} = {maskExpr}";
                }
                // In expression context, we need to return the old value
                var tmpVar = context.GenerateSyntheticName("_us");
                context.AddPreStatement($"int {tmpVar} = {postfixOperand}");
                context.AddPreStatement($"{postfixOperand} = {maskExpr}");
                return tmpVar;
            }
        }

        var facade = ExpressionTransformerFacade.Instance;
        var operand = facade.Transform(node.Operand, context);
        return $"{operand}{op}";
    }

    private string TransformUserDefinedPostfixOperator(PostfixUnaryExpressionSyntax node, IMethodSymbol operatorSymbol, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var operand = facade.Transform(node.Operand, context);
        var javaMethodName = GetOperatorMethodName(operatorSymbol);
        var containingType = context.MapType(operatorSymbol.ContainingType);
        var currentType = context.CurrentType?.Name;

        // Postfix semantics: save pre-increment value, apply operator, return saved value
        var tmp = context.GenerateSyntheticName("_post");
        context.AddPreStatement($"var {tmp} = {operand};");
        var methodCall = (containingType == currentType)
            ? $"{operand} = {javaMethodName}({operand});"
            : $"{operand} = {containingType}.{javaMethodName}({operand});";
        context.AddPreStatement(methodCall);
        return tmp;
    }

    private string TransformPrefix(PrefixUnaryExpressionSyntax node, string op, ConversionContext context)
    {
        if (op == "++" || op == "--")
        {
            var operandType = context.GetTypeInfo(node.Operand).Type;
            if (operandType is IPointerTypeSymbol ptrType)
            {
                var facade0 = ExpressionTransformerFacade.Instance;
                var operandExpr = facade0.Transform(node.Operand, context);
                var pointerInfo = context.FindPointerInfo(operandExpr.Trim());
                if (pointerInfo == null)
                {
                    var elementTypeName = GetCSharpElementTypeName(ptrType.PointedAtType);
                    pointerInfo = FfmHelper.CreatePointerInfo(operandExpr.Trim(), elementTypeName);
                }
                if (pointerInfo != null)
                {
                    long delta = op == "++" ? pointerInfo.ElementSize : -pointerInfo.ElementSize;

                    string sliceExpr;
                    if (context.TryGetPointerBase(operandExpr.Trim(), out var baseVar))
                    {
                        // Use base segment with out-of-bounds protection (same as TransformPostfix).
                        // C# allows one-past-end pointers (used only for address comparison),
                        // but Java's MemorySegment.asSlice throws if offset > byteSize or offset < 0.
                        var offsetExpr = $"{operandExpr}.address() - {baseVar}.address() + {delta}";
                        sliceExpr = $"({offsetExpr} >= 0 && {offsetExpr} <= {baseVar}.byteSize() ? {baseVar}.asSlice({offsetExpr}) : MemorySegment.ofAddress({operandExpr}.address() + {delta}))";
                    }
                    else
                    {
                        // No base segment: use MemorySegment.ofAddress since asSlice doesn't support negative offsets
                        sliceExpr = $"MemorySegment.ofAddress({operandExpr}.address() + {delta})";
                    }

                    if (IsDiscardedValueContext(node))
                    {
                        if (delta < 0)
                            context.InvalidatePointerBacktrackVars(operandExpr.Trim());
                        return $"{operandExpr} = {sliceExpr}";
                    }

                    var tmp = context.GenerateSyntheticName("_ptrPre");
                    context.AddImport("java.lang.foreign.MemorySegment");
                    context.AddPreStatementAllowDuplicate($"{operandExpr} = {sliceExpr}");
                    context.AddPreStatement($"MemorySegment {tmp} = {operandExpr}");
                    if (delta < 0)
                        context.InvalidatePointerBacktrackVars(operandExpr.Trim());
                    return tmp;
                }
            }
        }

        // Statement context: rewrite property/indexer in place
        if (node.Parent is ExpressionStatementSyntax
            && TryTransformDecimalIncrementAsAssignment(node.Operand, op, context, out var decimalRewrite))
        {
            return decimalRewrite;
        }
        if (node.Parent is ExpressionStatementSyntax
            && TryTransformPropertyIncrementAsSetter(node.Operand, op, context, out var rewritten))
        {
            return rewritten;
        }

        if (node.Parent is ExpressionStatementSyntax
            && TryTransformIndexerIncrementAsMutation(node.Operand, op, context, out var indexerRewrite))
        {
            return indexerRewrite;
        }

        // Expression context: hoist property increment to pre-statement, return new value
        if (node.Parent is not ExpressionStatementSyntax
            && TryBuildDecimalHoistForPrefix(node.Operand, op, context, out var decimalHoisted))
        {
            return decimalHoisted;
        }

        if (node.Parent is not ExpressionStatementSyntax
            && TryTransformPropertyIncrementAsSetter(node.Operand, op, context, out var propRewrite))
        {
            if (TryBuildPropertyHoistForPrefix(node.Operand, op, context, out var hoisted))
                return hoisted;
        }

        // Expression context: hoist indexer increment to pre-statement, return new value
        if (node.Parent is not ExpressionStatementSyntax
            && TryTransformIndexerIncrementAsMutation(node.Operand, op, context, out var idxRewrite))
        {
            if (TryBuildIndexerHoistForPrefix(node.Operand, op, context, out var hoisted))
                return hoisted;
        }

        // Check if this is a user-defined prefix operator (++, --)
        if (context.SemanticModel != null)
        {
            var symbolInfo = context.GetSymbolInfo(node);
            if (symbolInfo.Symbol is IMethodSymbol methodSymbol && methodSymbol.ContainingType != null)
            {
                // Only convert to method call if it's a user-defined type
                if (!IsBuiltInType(methodSymbol.ContainingType))
                {
                    return TransformUserDefinedUnaryOperator(node, methodSymbol, context);
                }
            }
        }

        // C# unsigned types (ushort/byte/uint/ulong) wrap around on overflow,
        // but Java signed types do not. For ++/-- on unsigned variables, we need
        // to add bitmask to simulate wrap-around semantics.
        // e.g. C# ushort --i when i=0 → i=65535; Java int --i when i=0 → i=-1
        if (context.SemanticModel != null && (op == "++" || op == "--"))
        {
            var unsignedType = context.GetTypeInfo(node.Operand).Type;
            if (unsignedType?.SpecialType is SpecialType.System_UInt16 or SpecialType.System_Byte
                or SpecialType.System_UInt32)
            {
                var unsignedFacade = ExpressionTransformerFacade.Instance;
                var unsignedOperand = unsignedFacade.Transform(node.Operand, context);
                var unsignedDelta = op == "++" ? "+ 1" : "- 1";
                var assignmentExpr = unsignedType.SpecialType switch
                {
                    SpecialType.System_UInt16 => $"{unsignedOperand} = (((int)({unsignedOperand} {unsignedDelta})) & 0xFFFF)",
                    SpecialType.System_Byte => $"{unsignedOperand} = ({unsignedOperand} {unsignedDelta}) & 0xFF",
                    SpecialType.System_UInt32 => $"{unsignedOperand} = (int)(({unsignedOperand} {unsignedDelta}) & 0xFFFFFFFFL)",
                    _ => $"{op}{unsignedOperand}"
                };
                // In discarded value context (statement or for-incrementor),
                // no outer parentheses needed
                if (IsDiscardedValueContext(node))
                    return assignmentExpr;
                // In expression context, wrap in parentheses for correct precedence
                return $"({assignmentExpr})";
            }
        }

        var facade = ExpressionTransformerFacade.Instance;
        var operand = facade.Transform(node.Operand, context);
        return $"{op}{operand}";
    }

    private static bool IsDiscardedValueContext(SyntaxNode node)
    {
        if (node.Parent is ExpressionStatementSyntax)
            return true;

        return node.Parent is ForStatementSyntax forStatement
            && (forStatement.Initializers.Any(expr => ReferenceEquals(expr, node))
                || forStatement.Incrementors.Any(expr => ReferenceEquals(expr, node)));
    }

    private static bool TryTransformDecimalIncrementAsAssignment(
        ExpressionSyntax operand,
        string op,
        ConversionContext context,
        out string rewritten)
    {
        rewritten = string.Empty;
        if (context.SemanticModel == null
            || !ExpressionTransformerHelpers.IsDecimalType(context.GetTypeInfo(operand).Type))
        {
            return false;
        }

        var transformedOperand = ExpressionTransformerFacade.Instance.Transform(operand, context);
        var method = op == "++" ? "add" : "subtract";
        rewritten = $"{transformedOperand} = {transformedOperand}.{method}(Decimal.ONE)";
        context.AddImport("io.github.ningpp.compat.Decimal");
        return true;
    }

    private static bool TryBuildDecimalHoistForPostfix(
        ExpressionSyntax operand,
        string op,
        ConversionContext context,
        out string result)
    {
        result = string.Empty;
        if (context.SemanticModel == null
            || !ExpressionTransformerHelpers.IsDecimalType(context.GetTypeInfo(operand).Type))
        {
            return false;
        }

        var transformedOperand = ExpressionTransformerFacade.Instance.Transform(operand, context);
        var tmp = context.GenerateSyntheticName("_postDecimal");
        var method = op == "++" ? "add" : "subtract";
        context.AddImport("io.github.ningpp.compat.Decimal");
        context.AddPreStatement($"var {tmp} = {transformedOperand}");
        context.AddPreStatement($"{transformedOperand} = {transformedOperand}.{method}(Decimal.ONE)");
        result = tmp;
        return true;
    }

    private static bool TryBuildDecimalHoistForPrefix(
        ExpressionSyntax operand,
        string op,
        ConversionContext context,
        out string result)
    {
        result = string.Empty;
        if (context.SemanticModel == null
            || !ExpressionTransformerHelpers.IsDecimalType(context.GetTypeInfo(operand).Type))
        {
            return false;
        }

        var transformedOperand = ExpressionTransformerFacade.Instance.Transform(operand, context);
        var method = op == "++" ? "add" : "subtract";
        context.AddImport("io.github.ningpp.compat.Decimal");
        context.AddPreStatement($"{transformedOperand} = {transformedOperand}.{method}(Decimal.ONE)");
        result = transformedOperand;
        return true;
    }

    private static bool TryTransformPropertyIncrementAsSetter(
        ExpressionSyntax operand,
        string op,
        ConversionContext context,
        out string rewritten)
    {
        rewritten = string.Empty;
        if (context.SemanticModel == null)
            return false;

        var symbol = context.GetSymbolInfo(operand).Symbol as IPropertySymbol;
        if (symbol == null || symbol.SetMethod == null)
            return false;

        var delta = op == "++" ? "+ 1" : "- 1";
        var facade = ExpressionTransformerFacade.Instance;

        if (operand is MemberAccessExpressionSyntax ma)
        {
            var recv = facade.Transform(ma.Expression, context);
            var propName = symbol.Name;
            var getter = "get" + char.ToUpperInvariant(propName[0]) + propName[1..];
            var setter = "set" + char.ToUpperInvariant(propName[0]) + propName[1..];
            rewritten = $"{recv}.{setter}({recv}.{getter}() {delta})";
            return true;
        }

        if (operand is IdentifierNameSyntax id)
        {
            var propName = id.Identifier.Text;
            var getter = "get" + char.ToUpperInvariant(propName[0]) + propName[1..];
            var setter = "set" + char.ToUpperInvariant(propName[0]) + propName[1..];
            rewritten = $"{setter}({getter}() {delta})";
            return true;
        }

        return false;
    }

    private static bool TryTransformIndexerIncrementAsMutation(
        ExpressionSyntax operand,
        string op,
        ConversionContext context,
        out string rewritten)
    {
        rewritten = string.Empty;
        if (context.SemanticModel == null || operand is not ElementAccessExpressionSyntax ela)
            return false;

        if (ela.ArgumentList.Arguments.Count != 1)
            return false;

        var delta = op == "++" ? "+ 1" : "- 1";
        var facade = ExpressionTransformerFacade.Instance;
        var target = facade.Transform(ela.Expression, context);
        var key = facade.Transform(ela.ArgumentList.Arguments[0].Expression, context);
        var containerType = context.GetTypeInfo(ela.Expression).Type as INamedTypeSymbol;

        if (containerType != null && IsDictionaryLike(containerType))
        {
            rewritten = $"{target}.put({key}, {target}.get({key}) {delta})";
            return true;
        }

        if (containerType != null && IsListLike(containerType))
        {
            rewritten = $"{target}.set({key}, {target}.get({key}) {delta})";
            return true;
        }

        return false;
    }

    private static bool IsDictionaryLike(INamedTypeSymbol type)
    {
        var fullName = type.OriginalDefinition.ToDisplayString();
        if (fullName is
            "System.Collections.Generic.Dictionary<TKey, TValue>"
            or "System.Collections.Generic.SortedDictionary<TKey, TValue>"
            or "System.Collections.Generic.SortedList<TKey, TValue>"
            or "System.Collections.Generic.IDictionary<TKey, TValue>"
            or "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>"
            or "System.Collections.Immutable.ImmutableDictionary<TKey, TValue>")
            return true;

        return type.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() is
            "System.Collections.Generic.IDictionary<TKey, TValue>"
            or "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>");
    }

    private static bool IsListLike(INamedTypeSymbol type)
    {
        var fullName = type.OriginalDefinition.ToDisplayString();
        if (fullName is
            "System.Collections.Generic.List<T>"
            or "System.Collections.Generic.IList<T>"
            or "System.Collections.Generic.IReadOnlyList<T>")
            return true;

        return type.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() is
            "System.Collections.Generic.IList<T>"
            or "System.Collections.Generic.IReadOnlyList<T>");
    }

    /// <summary>
    /// Hoists a postfix property increment into pre-statements.
    /// Returns the old value (pre-increment) as the expression result.
    /// Pattern: var _t = getter(); setter(_t ± 1); → returns _t
    /// </summary>
    private static bool TryBuildPropertyHoistForPostfix(
        ExpressionSyntax operand, string op, ConversionContext context, out string result)
    {
        result = string.Empty;
        if (context.SemanticModel == null) return false;

        var symbol = context.GetSymbolInfo(operand).Symbol as IPropertySymbol;
        if (symbol == null || symbol.SetMethod == null) return false;

        var delta = op == "++" ? "+ 1" : "- 1";
        var facade = ExpressionTransformerFacade.Instance;
        var propName = symbol.Name;
        var getter = "get" + char.ToUpperInvariant(propName[0]) + propName[1..];
        var setter = "set" + char.ToUpperInvariant(propName[0]) + propName[1..];
        var tmp = context.GenerateSyntheticName("_prev");

        if (operand is MemberAccessExpressionSyntax ma)
        {
            var recv = facade.Transform(ma.Expression, context);
            context.AddPreStatement($"var {tmp} = {recv}.{getter}();");
            context.AddPreStatement($"{recv}.{setter}({tmp} {delta});");
            result = tmp;
            return true;
        }
        if (operand is IdentifierNameSyntax)
        {
            context.AddPreStatement($"var {tmp} = {getter}();");
            context.AddPreStatement($"{setter}({tmp} {delta});");
            result = tmp;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Hoists a prefix property increment into pre-statements.
    /// Returns the new value (post-increment) as the expression result.
    /// Pattern: setter(getter() ± 1); var _t = getter(); → returns _t
    /// </summary>
    private static bool TryBuildPropertyHoistForPrefix(
        ExpressionSyntax operand, string op, ConversionContext context, out string result)
    {
        result = string.Empty;
        if (context.SemanticModel == null) return false;

        var symbol = context.GetSymbolInfo(operand).Symbol as IPropertySymbol;
        if (symbol == null || symbol.SetMethod == null) return false;

        var delta = op == "++" ? "+ 1" : "- 1";
        var facade = ExpressionTransformerFacade.Instance;
        var propName = symbol.Name;
        var getter = "get" + char.ToUpperInvariant(propName[0]) + propName[1..];
        var setter = "set" + char.ToUpperInvariant(propName[0]) + propName[1..];
        var tmp = context.GenerateSyntheticName("_inc");

        if (operand is MemberAccessExpressionSyntax ma)
        {
            var recv = facade.Transform(ma.Expression, context);
            context.AddPreStatement($"var {tmp} = {recv}.{getter}() {delta};");
            context.AddPreStatement($"{recv}.{setter}({tmp});");
            result = tmp;
            return true;
        }
        if (operand is IdentifierNameSyntax)
        {
            context.AddPreStatement($"var {tmp} = {getter}() {delta};");
            context.AddPreStatement($"{setter}({tmp});");
            result = tmp;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Hoists a postfix indexer increment into pre-statements.
    /// Returns the old value (pre-increment) as the expression result.
    /// Pattern: var _t = map.get(key); map.put(key, _t ± 1); → returns _t
    /// </summary>
    private static bool TryBuildIndexerHoistForPostfix(
        ExpressionSyntax operand, string op, ConversionContext context, out string result)
    {
        result = string.Empty;
        if (context.SemanticModel == null || operand is not ElementAccessExpressionSyntax ela)
            return false;
        if (ela.ArgumentList.Arguments.Count != 1) return false;

        var delta = op == "++" ? "+ 1" : "- 1";
        var facade = ExpressionTransformerFacade.Instance;
        var target = facade.Transform(ela.Expression, context);
        var key = facade.Transform(ela.ArgumentList.Arguments[0].Expression, context);
        var containerType = context.GetTypeInfo(ela.Expression).Type as INamedTypeSymbol;
        var tmp = context.GenerateSyntheticName("_prev");

        if (containerType != null && IsDictionaryLike(containerType))
        {
            context.AddPreStatement($"var {tmp} = {target}.get({key});");
            context.AddPreStatement($"{target}.put({key}, {tmp} {delta});");
            result = tmp;
            return true;
        }
        if (containerType != null && IsListLike(containerType))
        {
            context.AddPreStatement($"var {tmp} = {target}.get({key});");
            context.AddPreStatement($"{target}.set({key}, {tmp} {delta});");
            result = tmp;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Hoists a prefix indexer increment into pre-statements.
    /// Returns the new value (post-increment) as the expression result.
    /// </summary>
    private static bool TryBuildIndexerHoistForPrefix(
        ExpressionSyntax operand, string op, ConversionContext context, out string result)
    {
        result = string.Empty;
        if (context.SemanticModel == null || operand is not ElementAccessExpressionSyntax ela)
            return false;
        if (ela.ArgumentList.Arguments.Count != 1) return false;

        var delta = op == "++" ? "+ 1" : "- 1";
        var facade = ExpressionTransformerFacade.Instance;
        var target = facade.Transform(ela.Expression, context);
        var key = facade.Transform(ela.ArgumentList.Arguments[0].Expression, context);
        var containerType = context.GetTypeInfo(ela.Expression).Type as INamedTypeSymbol;
        var tmp = context.GenerateSyntheticName("_inc");

        if (containerType != null && IsDictionaryLike(containerType))
        {
            context.AddPreStatement($"var {tmp} = {target}.get({key}) {delta};");
            context.AddPreStatement($"{target}.put({key}, {tmp});");
            result = tmp;
            return true;
        }
        if (containerType != null && IsListLike(containerType))
        {
            context.AddPreStatement($"var {tmp} = {target}.get({key}) {delta};");
            context.AddPreStatement($"{target}.set({key}, {tmp});");
            result = tmp;
            return true;
        }
        return false;
    }
}
