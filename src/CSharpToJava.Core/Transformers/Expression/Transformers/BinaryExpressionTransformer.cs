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
/// Handles binary expressions (arithmetic, logical, bitwise, comparison, coalesce).
/// </summary>
[TransformerRegistration]
public class BinaryExpressionTransformer : IIRExpressionTransformer
{
    static BinaryExpressionTransformer()
    {
        ExpressionTransformerRegistry.Register(new[]
        {
            SyntaxKind.AddExpression,
            SyntaxKind.SubtractExpression,
            SyntaxKind.MultiplyExpression,
            SyntaxKind.DivideExpression,
            SyntaxKind.ModuloExpression,
            SyntaxKind.GreaterThanExpression,
            SyntaxKind.GreaterThanOrEqualExpression,
            SyntaxKind.LessThanExpression,
            SyntaxKind.LessThanOrEqualExpression,
            SyntaxKind.EqualsExpression,
            SyntaxKind.NotEqualsExpression,
            SyntaxKind.LogicalAndExpression,
            SyntaxKind.LogicalOrExpression,
            SyntaxKind.CoalesceExpression,
            SyntaxKind.BitwiseAndExpression,
            SyntaxKind.BitwiseOrExpression,
            SyntaxKind.ExclusiveOrExpression,
            SyntaxKind.LeftShiftExpression,
            SyntaxKind.RightShiftExpression,
            SyntaxKind.UnsignedRightShiftExpression
        }, new BinaryExpressionTransformer());
    }

    private static readonly Lazy<BinaryExpressionTransformer> _instance = new(() => new());
    public static BinaryExpressionTransformer Instance => _instance.Value;

    public string Transform(ExpressionSyntax node, ConversionContext context)
        => node.Kind() switch
        {
            SyntaxKind.AddExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "+", context),
            SyntaxKind.SubtractExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "-", context),
            SyntaxKind.MultiplyExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "*", context),
            SyntaxKind.DivideExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "/", context),
            SyntaxKind.ModuloExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "%", context),
            SyntaxKind.GreaterThanExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, ">", context),
            SyntaxKind.GreaterThanOrEqualExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, ">=", context),
            SyntaxKind.LessThanExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "<", context),
            SyntaxKind.LessThanOrEqualExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "<=", context),
            SyntaxKind.EqualsExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "==", context),
            SyntaxKind.NotEqualsExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "!=", context),
            SyntaxKind.LogicalAndExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "&&", context),
            SyntaxKind.LogicalOrExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "||", context),
            SyntaxKind.CoalesceExpression => TransformCoalesceExpression((BinaryExpressionSyntax)node, context),
            SyntaxKind.BitwiseAndExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "&", context),
            SyntaxKind.BitwiseOrExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "|", context),
            SyntaxKind.ExclusiveOrExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "^", context),
            SyntaxKind.LeftShiftExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, "<<", context),
            SyntaxKind.RightShiftExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, ">>", context),
            SyntaxKind.UnsignedRightShiftExpression => TransformBinaryExpression((BinaryExpressionSyntax)node, ">>>", context),
            _ => throw new NotSupportedException($"Binary expression kind {node.Kind()} not supported.")
        };

    /// <inheritdoc />
    public JavaExpression TransformToIR(ExpressionSyntax node, ConversionContext context)
    {
        if (node is not BinaryExpressionSyntax binExpr)
            return new JavaRawExpression(Transform(node, context));

        var facade = ExpressionTransformerFacade.Instance;

        // Coalesce (??) → JavaConditionalExpression
        if (binExpr.IsKind(SyntaxKind.CoalesceExpression))
        {
            // Delegate to Transform because of temp var hoisting for side-effectful left
            var code = Transform(node, context);
            return new JavaRawExpression(code);
        }

        // Map syntax kind to Java operator (null for special-cased kinds)
        var op = binExpr.Kind() switch
        {
            SyntaxKind.AddExpression => "+",
            SyntaxKind.SubtractExpression => "-",
            SyntaxKind.MultiplyExpression => "*",
            SyntaxKind.DivideExpression => "/",
            SyntaxKind.ModuloExpression => "%",
            SyntaxKind.GreaterThanExpression => ">",
            SyntaxKind.GreaterThanOrEqualExpression => ">=",
            SyntaxKind.LessThanExpression => "<",
            SyntaxKind.LessThanOrEqualExpression => "<=",
            SyntaxKind.EqualsExpression => "==",
            SyntaxKind.NotEqualsExpression => "!=",
            SyntaxKind.LogicalAndExpression => "&&",
            SyntaxKind.LogicalOrExpression => "||",
            SyntaxKind.BitwiseAndExpression => "&",
            SyntaxKind.BitwiseOrExpression => "|",
            SyntaxKind.ExclusiveOrExpression => "^",
            SyntaxKind.LeftShiftExpression => "<<",
            SyntaxKind.RightShiftExpression => ">>",
            SyntaxKind.UnsignedRightShiftExpression => ">>>",
            _ => null
        };

        if (op == null)
            return new JavaRawExpression(Transform(node, context));

        // Check for user-defined operators → JavaMethodCallExpression
        if (context.SemanticModel != null)
        {
            var symbolInfo = context.GetSymbolInfo(node);
            if (symbolInfo.Symbol is IMethodSymbol ms
                && ms.MethodKind == MethodKind.UserDefinedOperator
                && ms.ContainingType != null
                && !IsBuiltInType(ms.ContainingType)
                && OperatorHasSourceDeclaration(ms))
            {
                var javaMethodName = GetOperatorMethodName(ms);
                var leftIR = facade.TransformToIR(binExpr.Left, context);
                var rightIR = facade.TransformToIR(binExpr.Right, context);

                var currentTypeName = context.CurrentType?.Name;
                var operatorTypeName = ms.ContainingType.Name;

                JavaExpression? target;
                if (currentTypeName != null && operatorTypeName == currentTypeName)
                {
                    target = null; // unqualified call within same class
                }
                else
                {
                    var containingType = context.MapType(ms.ContainingType);
                    var angleIdx = containingType.IndexOf('<');
                    if (angleIdx > 0) containingType = containingType[..angleIdx];
                    target = new JavaIdentifierExpression { Name = containingType };
                }

                var call = new JavaMethodCallExpression { Target = target, MethodName = javaMethodName };
                call.Arguments.Add(leftIR);
                call.Arguments.Add(rightIR);
                return call;
            }

            // Fallback: when GetSymbolInfo fails or operator is from a non-source (BCL) type,
            // use operand type info to detect compat library method calls
            if (IsArithmeticOp(op))
            {
                bool shouldFallback = symbolInfo.Symbol == null
                    || (symbolInfo.Symbol is IMethodSymbol ms2
                        && ms2.MethodKind == MethodKind.UserDefinedOperator
                        && !OperatorHasSourceDeclaration(ms2));
                if (shouldFallback)
                {
                    var fallbackResult = TryTransformOperatorByTypeInfoToIR(binExpr, op, context);
                    if (fallbackResult != null)
                        return fallbackResult;
                }
            }
        }

        // Check for string equality (needs Objects.equals) → JavaMethodCallExpression
        if ((op == "==" || op == "!=") && context.SemanticModel != null)
        {
            bool leftIsString = IsStringType(binExpr.Left, context.SemanticModel);
            bool rightIsString = IsStringType(binExpr.Right, context.SemanticModel);
            bool leftIsNull = binExpr.Left.IsKind(SyntaxKind.NullLiteralExpression);
            bool rightIsNull = binExpr.Right.IsKind(SyntaxKind.NullLiteralExpression);
            if ((leftIsString || rightIsString) && !leftIsNull && !rightIsNull)
            {
                context.AddImport("java.util.Objects");
                var leftIR = facade.TransformToIR(binExpr.Left, context);
                var rightIR = facade.TransformToIR(binExpr.Right, context);
                var equalsCall = new JavaMethodCallExpression
                {
                    Target = new JavaIdentifierExpression { Name = "Objects" },
                    MethodName = "equals"
                };
                equalsCall.Arguments.Add(leftIR);
                equalsCall.Arguments.Add(rightIR);
                if (op == "!=")
                    return new JavaUnaryExpression { Operator = "!", Operand = equalsCall, IsPostfix = false };
                return equalsCall;
            }
        }

        // Check for event comparisons — fall back to raw
        if ((op == "==" || op == "!=") && context.SemanticModel != null)
        {
            var leftSym = context.GetSymbolInfo(binExpr.Left).Symbol;
            var rightSym = context.GetSymbolInfo(binExpr.Right).Symbol;
            if (leftSym is IEventSymbol || rightSym is IEventSymbol)
                return new JavaRawExpression(Transform(node, context));
        }

        // Handle enum comparison operators (<, >, <=, >=) in IR path.
        if (IsComparisonOp(op) && context.SemanticModel != null)
        {
            var enumIR = TryTransformEnumComparisonToIR(binExpr, op, context);
            if (enumIR != null)
                return enumIR;
        }

        // Standard binary expression — produce structured IR
        var standardLeftIR = facade.TransformToIR(binExpr.Left, context);
        var standardRightIR = facade.TransformToIR(binExpr.Right, context);

        return new JavaBinaryExpression
        {
            Left = standardLeftIR,
            Operator = op,
            Right = standardRightIR
        };
    }

    private string TransformBinaryExpression(BinaryExpressionSyntax node, string op, ConversionContext context)
    {
        if (op == "+")
        {
            var leftType = context.GetTypeInfo(node.Left).Type;
            if (leftType is IPointerTypeSymbol pointerType)
            {
                var facade2 = ExpressionTransformerFacade.Instance;
                var leftExpr = facade2.Transform(node.Left, context);
                var rightExpr = facade2.Transform(node.Right, context);

                // Try to find pointer info from registry first (for simple identifiers)
                var pointerInfo = context.FindPointerInfo(leftExpr.Trim());

                // If not found (e.g., for nested expressions like "pChars + startPos"),
                // create pointer info from the semantic type information
                if (pointerInfo == null && pointerType.PointedAtType != null)
                {
                    var elementTypeName = pointerType.PointedAtType.ToDisplayString();
                    pointerInfo = FfmHelper.CreatePointerInfo("", elementTypeName);
                }

                if (pointerInfo != null)
                {
                    // Use base segment with out-of-bounds protection
                    if (context.TryGetPointerBase(leftExpr.Trim(), out var baseVar))
                    {
                        string offsetExpr = pointerInfo.ElementSize == 1
                            ? $"{leftExpr}.address() - {baseVar}.address() + {rightExpr}"
                            : $"{leftExpr}.address() - {baseVar}.address() + (long)({rightExpr}) * {pointerInfo.ElementSize}";
                        string deltaBytesExpr = pointerInfo.ElementSize == 1
                            ? rightExpr
                            : $"(long)({rightExpr}) * {pointerInfo.ElementSize}";
                        return $"({offsetExpr} >= 0 && {offsetExpr} <= {baseVar}.byteSize() ? {baseVar}.asSlice({offsetExpr}) : MemorySegment.ofAddress({leftExpr}.address() + {deltaBytesExpr}))";
                    }
                    return FfmHelper.GeneratePointerArithmetic(leftExpr.Trim(), pointerInfo, rightExpr);
                }
            }
        }

        if (op == "-")
        {
            var leftType = context.GetTypeInfo(node.Left).Type;
            var rightType = context.GetTypeInfo(node.Right).Type;

            // Pointer - pointer (must be checked before Pointer - integer)
            if (leftType is IPointerTypeSymbol && rightType is IPointerTypeSymbol)
            {
                var facade2 = ExpressionTransformerFacade.Instance;
                var leftExpr = facade2.Transform(node.Left, context);
                var rightExpr = facade2.Transform(node.Right, context);

                var pointerInfo = context.FindPointerInfo(leftExpr.Trim());
                if (pointerInfo == null && leftType is IPointerTypeSymbol leftPtr)
                {
                    var elementTypeName = leftPtr.PointedAtType?.ToDisplayString() ?? "byte";
                    pointerInfo = FfmHelper.CreatePointerInfo("", elementTypeName);
                }

                var elementSize = pointerInfo?.ElementSize ?? 1;
                if (elementSize == 1)
                    return $"({leftExpr}.address() - {rightExpr}.address())";
                return $"({leftExpr}.address() - {rightExpr}.address()) / {elementSize}";
            }

            // Pointer - integer
            if (leftType is IPointerTypeSymbol leftPtrType)
            {
                var facade2 = ExpressionTransformerFacade.Instance;
                var leftExpr = facade2.Transform(node.Left, context);
                var rightExpr = facade2.Transform(node.Right, context);

                var pointerInfo = context.FindPointerInfo(leftExpr.Trim());
                if (pointerInfo == null && leftPtrType.PointedAtType != null)
                {
                    var elementTypeName = leftPtrType.PointedAtType.ToDisplayString();
                    pointerInfo = FfmHelper.CreatePointerInfo("", elementTypeName);
                }

                if (pointerInfo != null)
                {
                    if (context.TryGetPointerBase(leftExpr.Trim(), out var baseVar))
                    {
                        // Use base segment with out-of-bounds protection.
                        // Must check offsetExpr >= 0 to prevent negative offset in asSlice.
                        string offsetExpr = pointerInfo.ElementSize == 1
                            ? $"{leftExpr}.address() - {baseVar}.address() - {rightExpr}"
                            : $"{leftExpr}.address() - {baseVar}.address() - (long)({rightExpr}) * {pointerInfo.ElementSize}";
                        string deltaBytesExpr = pointerInfo.ElementSize == 1
                            ? $"-{rightExpr}"
                            : $"-(long)({rightExpr}) * {pointerInfo.ElementSize}";
                        return $"({offsetExpr} >= 0 && {offsetExpr} <= {baseVar}.byteSize() ? {baseVar}.asSlice({offsetExpr}) : MemorySegment.ofAddress({leftExpr}.address() + {deltaBytesExpr}))";
                    }
                    // No base segment: use MemorySegment.ofAddress since asSlice doesn't support negative offsets
                    if (pointerInfo.ElementSize == 1)
                        return $"MemorySegment.ofAddress({leftExpr}.address() - {rightExpr})";
                    return $"MemorySegment.ofAddress({leftExpr}.address() - (long)({rightExpr}) * {pointerInfo.ElementSize})";
                }
            }
        }

        // Handle pointer comparison operators (<, >, <=, >=)
        if (IsComparisonOp(op) && context.SemanticModel != null)
        {
            var leftType = context.GetTypeInfo(node.Left).Type;
            var rightType = context.GetTypeInfo(node.Right).Type;
            if (leftType is IPointerTypeSymbol && rightType is IPointerTypeSymbol)
            {
                var facade2 = ExpressionTransformerFacade.Instance;
                var leftExpr = facade2.Transform(node.Left, context);
                var rightExpr = facade2.Transform(node.Right, context);
                return $"{leftExpr}.address() {op} {rightExpr}.address()";
            }
        }

        var facade = ExpressionTransformerFacade.Instance;

        // Handle pointer equality operators (==, !=)
        // MemorySegment objects from asSlice() are never reference-equal even when pointing
        // to the same address, so pointer comparisons must use .address().
        if ((op == "==" || op == "!=") && context.SemanticModel != null)
        {
            var leftType = context.GetTypeInfo(node.Left).Type;
            var rightType = context.GetTypeInfo(node.Right).Type;
            bool leftIsPointer = leftType is IPointerTypeSymbol;
            bool rightIsPointer = rightType is IPointerTypeSymbol;

            // Pointer == pointer or pointer == null
            if (leftIsPointer || rightIsPointer)
            {
                var facade2 = ExpressionTransformerFacade.Instance;
                var leftExpr = facade2.Transform(node.Left, context);
                var rightExpr = facade2.Transform(node.Right, context);

                bool rightIsNull = node.Right.IsKind(SyntaxKind.NullLiteralExpression);
                bool leftIsNull = node.Left.IsKind(SyntaxKind.NullLiteralExpression);

                if (leftIsPointer && rightIsPointer)
                {
                    // Both are pointers: compare addresses
                    return op == "=="
                        ? $"{leftExpr}.address() == {rightExpr}.address()"
                        : $"{leftExpr}.address() != {rightExpr}.address()";
                }
                if (leftIsPointer && rightIsNull)
                {
                    // Pointer == null: check if segment is null or zero-address
                    return op == "=="
                        ? $"({leftExpr} == null || {leftExpr}.address() == 0)"
                        : $"({leftExpr} != null && {leftExpr}.address() != 0)";
                }
                if (rightIsPointer && leftIsNull)
                {
                    return op == "=="
                        ? $"({rightExpr} == null || {rightExpr}.address() == 0)"
                        : $"({rightExpr} != null && {rightExpr}.address() != 0)";
                }
            }
        }

        // Fix: Handle event null comparisons (e.g., ProgressChanged != null)
        // C#: event != null  → Java: !_eventListeners.isEmpty()
        // C#: event == null  → Java: _eventListeners.isEmpty()
        if ((op == "==" || op == "!=") && context.SemanticModel != null)
        {
            // Check if this is an event compared to null
            var leftSymbol = context.GetSymbolInfo(node.Left).Symbol;
            var rightSymbol = context.GetSymbolInfo(node.Right).Symbol;
            bool rightIsNull = node.Right.IsKind(SyntaxKind.NullLiteralExpression);
            bool leftIsNull = node.Left.IsKind(SyntaxKind.NullLiteralExpression);

            if ((leftSymbol is IEventSymbol eventSym && rightIsNull) ||
                (rightSymbol is IEventSymbol && leftIsNull))
            {
                var actualEventSym = leftSymbol as IEventSymbol ?? rightSymbol as IEventSymbol;
                var eventName = actualEventSym?.Name;
                var currentTypeName = context.CurrentType?.Name;

                // Only convert to listener access within the same class
                if (eventName != null && currentTypeName != null &&
                    actualEventSym?.ContainingType?.Name == currentTypeName)
                {
                    var fieldName = $"_{char.ToLower(eventName[0])}{eventName.Substring(1)}Listeners";
                    if (op == "!=")
                        return $"!{fieldName}.isEmpty()";
                    else
                        return $"{fieldName}.isEmpty()";
                }
            }
        }

        // Check if this is a user-defined operator that should be converted to a static method call
        if (context.SemanticModel != null)
        {
            var symbolInfo = context.GetSymbolInfo(node);
            if (symbolInfo.Symbol is IMethodSymbol methodSymbol && methodSymbol.ContainingType != null)
            {
                // Only convert to method call if it's a user-defined type (not built-in types)
                if (methodSymbol.MethodKind == MethodKind.UserDefinedOperator
                    && !IsBuiltInType(methodSymbol.ContainingType)
                    && OperatorHasSourceDeclaration(methodSymbol))
                {
                    return TransformUserDefinedOperator(node, methodSymbol, context);
                }
            }

            // Fallback: when GetSymbolInfo fails to resolve the operator (possible when the
            // compilation has incomplete metadata references), or when the operator is from a
            // non-source (BCL) type that maps to a compat library type (e.g. DateTime - DateTime),
            // use GetTypeInfo on the operands to detect user-defined types and emit the
            // corresponding Java static method call.
            if (IsArithmeticOp(op))
            {
                bool shouldFallback = symbolInfo.Symbol == null
                    || (symbolInfo.Symbol is IMethodSymbol ms2
                        && ms2.MethodKind == MethodKind.UserDefinedOperator
                        && !OperatorHasSourceDeclaration(ms2));
                if (shouldFallback)
                {
                    var fallbackResult = TryTransformOperatorByTypeInfo(node, op, context);
                    if (fallbackResult != null)
                        return fallbackResult;
                }
            }
        }

        // Handle enum arithmetic operators (+, -, *, /, %).
        // Java enums do not support arithmetic operators; must use ordinal() or getValue() on operands.
        if (IsArithmeticOp(op) && context.SemanticModel != null)
        {
            var enumArithmeticResult = TryTransformEnumArithmeticOperation(node, op, context);
            if (enumArithmeticResult != null)
                return enumArithmeticResult;
        }

        // Handle enum comparison operators (<, >, <=, >=).
        // Java enums do not support ordering operators; must compare ordinal() or getValue().
        if (IsComparisonOp(op) && context.SemanticModel != null)
        {
            var enumResult = TryTransformEnumComparison(node, op, context);
            if (enumResult != null)
                return enumResult;

            // C# (uint)x <= y uses unsigned comparison semantics.
            // The (uint) cast is converted to (int)((x) & 0xFFFFFFFFL), but the (int)
            // truncation reverses the unsigned wrap-around, making the comparison wrong.
            // Fix: use Integer.compareUnsigned for uint comparisons.
            var leftType = context.GetTypeInfo(node.Left).Type;
            var rightType = context.GetTypeInfo(node.Right).Type;
            bool leftIsUInt = leftType?.SpecialType == SpecialType.System_UInt32;
            bool rightIsUInt = rightType?.SpecialType == SpecialType.System_UInt32;
            if (leftIsUInt || rightIsUInt)
            {
                var leftExpr = facade.Transform(node.Left, context);
                var rightExpr = facade.Transform(node.Right, context);
                // Strip the & 0xFFFFFFFFL mask from uint operands since
                // Integer.compareUnsigned handles the unsigned semantics directly.
                leftExpr = StripUIntMask(leftExpr);
                rightExpr = StripUIntMask(rightExpr);
                var cmpOp = op switch
                {
                    "<" => "Integer.compareUnsigned({0}, {1}) < 0",
                    ">" => "Integer.compareUnsigned({0}, {1}) > 0",
                    "<=" => "Integer.compareUnsigned({0}, {1}) <= 0",
                    ">=" => "Integer.compareUnsigned({0}, {1}) >= 0",
                    _ => "{0} " + op + " {1}"
                };
                return string.Format(cmpOp, leftExpr, rightExpr);
            }
        }

        // Handle bitwise operations (&, |, ^) on non-Flags enum types.
        // Java enums do not support bitwise operators; must use getValue() on operands.
        if (IsBitwiseOp(op) && context.SemanticModel != null)
        {
            var enumBitwiseResult = TryTransformEnumBitwiseOperation(node, op, context);
            if (enumBitwiseResult != null)
                return enumBitwiseResult;
        }

        // Standard operator - use Java's built-in operators
        var left = facade.Transform(node.Left, context);

        // For || and &&, the right operand is in a short-circuit context:
        // side effects (like *ptr++) must only execute when the left operand
        // doesn't short-circuit. Set the flag so TransformPostfix can defer
        // the pointer increment into the expression instead of a pre-statement.
        bool isShortCircuit = op == "||" || op == "&&";
        if (isShortCircuit)
            context.EnterShortCircuitOperand();
        var right = facade.Transform(node.Right, context);
        if (isShortCircuit)
            context.ExitShortCircuitOperand();

        if (TryTransformDecimalBinaryExpression(node, op, context, left, right, out var decimalResult))
            return decimalResult;

        // String == / != must preserve C#'s null-safe value semantics.
        if ((op == "==" || op == "!=") && context.SemanticModel != null)
        {
            bool leftIsString = IsStringType(node.Left, context.SemanticModel);
            bool rightIsString = IsStringType(node.Right, context.SemanticModel);
            if (leftIsString || rightIsString)
            {
                // null comparisons remain as == / !=
                bool leftIsNull = node.Left.IsKind(SyntaxKind.NullLiteralExpression);
                bool rightIsNull = node.Right.IsKind(SyntaxKind.NullLiteralExpression);
                if (!leftIsNull && !rightIsNull)
                {
                    context.AddImport("java.util.Objects");
                    string eq = $"Objects.equals({left}, {right})";
                    return op == "!=" ? $"!{eq}" : eq;
                }
            }
        }

        // Wrap operands in parentheses when needed for operator precedence
        left = WrapOperandIfNeeded(node.Left, left, op, true);
        right = WrapOperandIfNeeded(node.Right, right, op, false);

        var result = $"{left} {op} {right}";
        if (TryWrapEnumBitwiseResult(node, result, context, out var wrappedResult))
            return wrappedResult;

        return result;
    }

    private static bool TryWrapEnumBitwiseResult(
        BinaryExpressionSyntax node,
        string numericExpression,
        ConversionContext context,
        out string result)
    {
        result = string.Empty;

        var resultType = context.GetTypeInfo(node).ConvertedType as INamedTypeSymbol
            ?? context.GetTypeInfo(node).Type as INamedTypeSymbol;
        if (resultType?.TypeKind != TypeKind.Enum || IsFlagsEnumType(resultType, context))
            return false;

        var javaEnumType = context.MapType(resultType);
        var angleIndex = javaEnumType.IndexOf('<');
        if (angleIndex > 0)
            javaEnumType = javaEnumType[..angleIndex];

        if (EnumHasExplicitValues(resultType) || IsRegisteredExplicitValueEnum(resultType, context))
        {
            result = $"{javaEnumType}.fromValue({numericExpression})";
        }
        else
        {
            result = $"{javaEnumType}.values()[{numericExpression}]";
        }

        return true;
    }

    private static bool IsArithmeticOp(string op) => op is "*" or "/" or "+" or "-" or "%";

    private static bool IsComparisonOp(string op) => op is "<" or ">" or "<=" or ">=";

    /// <summary>
    /// Strips the (int)((expr) &amp; 0xFFFFFFFFL) pattern from uint operands
    /// since Integer.compareUnsigned handles the unsigned semantics directly.
    /// Returns the inner expression without the mask and (int) cast.
    /// </summary>
    private static string StripUIntMask(string expr)
    {
        // Match pattern: (int)((<expr>) & 0xFFFFFFFFL)
        // The pattern is generated by TypeOperationTransformer for (uint) casts.
        // We need to extract <expr> and pass it to Integer.compareUnsigned.
        // Example: (int)(((character - 'a')) & 0xFFFFFFFFL) → (character - 'a')
        const string prefix = "(int)((";
        const string suffix = ") & 0xFFFFFFFFL)";
        if (expr.StartsWith(prefix) && expr.EndsWith(suffix))
        {
            // Extract the inner expression between prefix and suffix
            string inner = expr.Substring(prefix.Length, expr.Length - prefix.Length - suffix.Length);
            // The inner expression may have an extra layer of parentheses from the
            // original cast, e.g. ((character - 'a')). Strip one layer if present.
            if (inner.StartsWith("(") && inner.EndsWith(")") && IsBalancedParentheses(inner.Substring(1, inner.Length - 2)))
            {
                inner = inner.Substring(1, inner.Length - 2);
            }
            return inner;
        }
        return expr;
    }

    private static bool IsBalancedParentheses(string s)
    {
        int depth = 0;
        foreach (char c in s)
        {
            if (c == '(') depth++;
            else if (c == ')')
            {
                depth--;
                if (depth < 0) return false;
            }
        }
        return depth == 0;
    }

    /// <summary>
    /// Checks if a node is inside a pointer dereference expression *(expr),
    /// traversing through ParenthesizedExpressionSyntax layers.
    /// </summary>
    private static bool IsInsidePointerDereference(ExpressionSyntax node)
    {
        var parent = node.Parent;
        while (parent is ParenthesizedExpressionSyntax)
            parent = parent.Parent;
        return parent is PrefixUnaryExpressionSyntax prefix
            && prefix.OperatorToken.IsKind(SyntaxKind.AsteriskToken);
    }

    private static bool IsBitwiseOp(string op) => op is "&" or "|" or "^";

    private static bool IsDecimalExpression(ExpressionSyntax expression, ConversionContext context)
        => context.SemanticModel != null
            && context.GetTypeInfo(expression).Type?.SpecialType == SpecialType.System_Decimal;

    private string? TryConvertToDecimalOperand(ExpressionSyntax expression, string transformedExpression, ConversionContext context)
    {
        if (IsDecimalExpression(expression, context))
            return transformedExpression;

        if (context.SemanticModel == null)
            return null;

        var typeInfo = context.GetTypeInfo(expression);
        if (typeInfo.ConvertedType?.SpecialType != SpecialType.System_Decimal
            && !ExpressionTransformerHelpers.IsNumericOrCharType(typeInfo.Type))
        {
            return null;
        }

        context.AddImport("io.github.ningpp.compat.Decimal");
        return ExpressionTransformerHelpers.ToDecimalExpression(expression, transformedExpression, typeInfo.Type);
    }

    private bool TryTransformDecimalBinaryExpression(
        BinaryExpressionSyntax node,
        string op,
        ConversionContext context,
        string left,
        string right,
        out string result)
    {
        result = string.Empty;

        var leftIsDecimal = IsDecimalExpression(node.Left, context);
        var rightIsDecimal = IsDecimalExpression(node.Right, context);
        if (!leftIsDecimal && !rightIsDecimal)
            return false;

        left = TryConvertToDecimalOperand(node.Left, left, context) ?? left;
        right = TryConvertToDecimalOperand(node.Right, right, context) ?? right;

        result = ExpressionTransformerHelpers.BuildDecimalBinaryOperation(left, right, op);

        return result.Length > 0;
    }

    private static string? GetEnumAccessSuffix(INamedTypeSymbol enumType, ConversionContext context)
    {
        if (IsFlagsEnumType(enumType, context))
            return null;

        // Prefer the current symbol over the name registry to avoid same-simple-name
        // enum collisions across conversions or type groups.
        if (EnumHasExplicitValues(enumType))
            return ".getValue()";

        var enumName = enumType.ToDisplayString();
        var fullyQualifiedName = enumType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (fullyQualifiedName.StartsWith("global::", StringComparison.Ordinal))
            fullyQualifiedName = fullyQualifiedName["global::".Length..];

        if (context.IsExplicitValueEnum(enumName) || context.IsExplicitValueEnum(fullyQualifiedName))
            return ".getValue()";

        return ".ordinal()";
    }

    /// <summary>
    /// Checks if an enum type has explicit value initializers by inspecting its members.
    /// </summary>
    private static bool EnumHasExplicitValues(INamedTypeSymbol enumType)
    {
        foreach (var member in enumType.GetMembers())
        {
            if (member is IFieldSymbol { IsConst: true, HasConstantValue: true } field
                && field.Name != WellKnownMemberNames.InstanceConstructorName)
            {
                // If any member has a non-default value, the enum has explicit values.
                // Default auto-increment starts at 0, so check if any value differs from its ordinal.
                var ordinal = 0;
                foreach (var checkMember in enumType.GetMembers())
                {
                    if (checkMember is IFieldSymbol { IsConst: true, HasConstantValue: true } checkField
                        && checkField.Name != WellKnownMemberNames.InstanceConstructorName)
                    {
                        if (checkField.ConstantValue is int intVal && intVal != ordinal)
                            return true;
                        if (checkField.ConstantValue is long longVal && longVal != ordinal)
                            return true;
                        if (checkField.ConstantValue is uint uintVal && uintVal != ordinal)
                            return true;
                        if (checkField.ConstantValue is ulong ulongVal && ulongVal != (ulong)ordinal)
                            return true;
                        ordinal++;
                    }
                }
                return false;
            }
        }
        return false;
    }

    private string? TryTransformEnumComparison(BinaryExpressionSyntax node, string op, ConversionContext context)
    {
        var leftType = context.GetTypeInfo(node.Left).Type as INamedTypeSymbol;
        var rightType = context.GetTypeInfo(node.Right).Type as INamedTypeSymbol;

        bool leftIsEnum = leftType?.TypeKind == TypeKind.Enum;
        bool rightIsEnum = rightType?.TypeKind == TypeKind.Enum;

        if (!leftIsEnum && !rightIsEnum)
            return null;

        var facade = ExpressionTransformerFacade.Instance;
        var left = facade.Transform(node.Left, context);
        var right = facade.Transform(node.Right, context);

        if (leftIsEnum)
        {
            var suffix = GetEnumAccessSuffix(leftType!, context);
            if (suffix != null) left = $"{left}{suffix}";
        }

        if (rightIsEnum)
        {
            var suffix = GetEnumAccessSuffix(rightType!, context);
            if (suffix != null) right = $"{right}{suffix}";
        }

        left = WrapOperandIfNeeded(node.Left, left, op, true);
        right = WrapOperandIfNeeded(node.Right, right, op, false);

        return $"{left} {op} {right}";
    }

    private JavaExpression? TryTransformEnumComparisonToIR(BinaryExpressionSyntax node, string op, ConversionContext context)
    {
        var leftType = context.GetTypeInfo(node.Left).Type as INamedTypeSymbol;
        var rightType = context.GetTypeInfo(node.Right).Type as INamedTypeSymbol;

        bool leftIsEnum = leftType?.TypeKind == TypeKind.Enum;
        bool rightIsEnum = rightType?.TypeKind == TypeKind.Enum;

        if (!leftIsEnum && !rightIsEnum)
            return null;

        return new JavaRawExpression(TryTransformEnumComparison(node, op, context)!);
    }

    /// <summary>
    /// Handles bitwise operations (&amp;, |, ^) on non-Flags enum types.
    /// Java enums do not support bitwise operators, so must use getValue()/ordinal() on operands.
    /// For Flags enums (mapped to int/long), no conversion is needed.
    /// </summary>
    private string? TryTransformEnumBitwiseOperation(BinaryExpressionSyntax node, string op, ConversionContext context)
    {
        var leftType = context.GetTypeInfo(node.Left).Type as INamedTypeSymbol;
        var rightType = context.GetTypeInfo(node.Right).Type as INamedTypeSymbol;

        bool leftIsEnum = leftType?.TypeKind == TypeKind.Enum;
        bool rightIsEnum = rightType?.TypeKind == TypeKind.Enum;

        if (!leftIsEnum && !rightIsEnum)
            return null;

        // Flags enums are mapped to int/long in Java, so bitwise ops work natively
        if (leftIsEnum && IsFlagsEnumType(leftType!, context))
            return null;
        if (rightIsEnum && IsFlagsEnumType(rightType!, context))
            return null;

        var facade = ExpressionTransformerFacade.Instance;
        var left = facade.Transform(node.Left, context);
        var right = facade.Transform(node.Right, context);

        // Wrap enum operands with getValue() or ordinal() depending on enum kind.
        // Guard: if the transformed expression already ends with a value-access
        // suffix (from nested enum expression processing), don't append another.
        if (leftIsEnum)
        {
            var suffix = GetEnumAccessSuffix(leftType!, context);
            if (suffix != null
                && !IsNestedBitwiseExpression(node.Left)
                && !left.EndsWith(suffix, StringComparison.Ordinal)
                && !IsBitwiseNotWithSuffix(left, suffix))
                left = ApplyEnumAccessSuffix(left, suffix);
        }

        if (rightIsEnum)
        {
            var suffix = GetEnumAccessSuffix(rightType!, context);
            if (suffix != null
                && !IsNestedBitwiseExpression(node.Right)
                && !right.EndsWith(suffix, StringComparison.Ordinal)
                && !IsBitwiseNotWithSuffix(right, suffix))
                right = ApplyEnumAccessSuffix(right, suffix);
        }

        left = WrapOperandIfNeeded(node.Left, left, op, true);
        right = WrapOperandIfNeeded(node.Right, right, op, false);

        return $"{left} {op} {right}";
    }

    /// <summary>
    /// Handles arithmetic operations (+, -, *, /, %) on non-Flags enum types.
    /// Java enums do not support arithmetic operators, so must use getValue()/ordinal() on operands.
    /// For Flags enums (mapped to int/long), no conversion is needed.
    /// </summary>
    private string? TryTransformEnumArithmeticOperation(BinaryExpressionSyntax node, string op, ConversionContext context)
    {
        var leftType = context.GetTypeInfo(node.Left).Type as INamedTypeSymbol;
        var rightType = context.GetTypeInfo(node.Right).Type as INamedTypeSymbol;

        bool leftIsEnum = leftType?.TypeKind == TypeKind.Enum;
        bool rightIsEnum = rightType?.TypeKind == TypeKind.Enum;

        if (!leftIsEnum && !rightIsEnum)
            return null;

        // Flags enums are mapped to int/long in Java, so arithmetic ops work natively
        if (leftIsEnum && IsFlagsEnumType(leftType!, context))
            return null;
        if (rightIsEnum && IsFlagsEnumType(rightType!, context))
            return null;

        var facade = ExpressionTransformerFacade.Instance;
        var left = facade.Transform(node.Left, context);
        var right = facade.Transform(node.Right, context);

        if (leftIsEnum)
        {
            var suffix = GetEnumAccessSuffix(leftType!, context);
            if (suffix != null && !left.EndsWith(suffix, StringComparison.Ordinal))
                left = ApplyEnumAccessSuffix(left, suffix);
        }

        if (rightIsEnum)
        {
            var suffix = GetEnumAccessSuffix(rightType!, context);
            if (suffix != null && !right.EndsWith(suffix, StringComparison.Ordinal))
                right = ApplyEnumAccessSuffix(right, suffix);
        }

        left = WrapOperandIfNeeded(node.Left, left, op, true);
        right = WrapOperandIfNeeded(node.Right, right, op, false);

        return $"{left} {op} {right}";
    }

    private static bool IsNestedBitwiseExpression(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;

        return expression is BinaryExpressionSyntax binary
            && IsBitwiseOp(binary.Kind() switch
            {
                SyntaxKind.BitwiseAndExpression => "&",
                SyntaxKind.BitwiseOrExpression => "|",
                SyntaxKind.ExclusiveOrExpression => "^",
                _ => string.Empty
            });
    }

    /// <summary>
    /// Applies an enum access suffix (.getValue() or .ordinal()) to an expression,
    /// wrapping in parentheses if the expression is compound to ensure the suffix
    /// applies to the whole expression, not just the last token.
    /// </summary>
    private static string ApplyEnumAccessSuffix(string expr, string suffix)
    {
        // If the expression contains operators or is complex, wrap in parens
        if (expr.Contains(" | ") || expr.Contains(" & ") || expr.Contains(" ^ ")
            || expr.Contains(" << ") || expr.Contains(">>")
            || expr.Contains(" + ") || expr.Contains(" - ") || expr.Contains(" * ")
            || expr.Contains(" / ") || expr.Contains(" % ")
            || expr.Contains(" ? ") || expr.Contains(" : "))
        {
            return $"({expr}){suffix}";
        }
        return $"{expr}{suffix}";
    }

    /// <summary>
    /// Checks if the expression is a bitwise-not (~) whose inner operand already
    /// contains the enum value-access suffix (e.g. ~(A.getValue() | B.getValue())).
    /// In that case the ~result is already an int, so appending another .getValue()
    /// would be invalid (cannot dereference int).
    /// </summary>
    private static bool IsBitwiseNotWithSuffix(string expr, string suffix)
    {
        if (!expr.StartsWith("~", StringComparison.Ordinal))
            return false;
        var inner = expr[1..].TrimStart();
        // Strip outer parens added by the unary transformer
        if (inner.StartsWith("(", StringComparison.Ordinal) && inner.EndsWith(")", StringComparison.Ordinal))
            inner = inner[1..^1];
        return inner.Contains(suffix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Checks if an enum type is a [Flags] enum, using both the context registration
    /// and direct semantic model inspection (to handle cases where the enum hasn't
    /// been registered yet due to processing order).
    /// </summary>
    private static bool IsFlagsEnumType(INamedTypeSymbol enumType, ConversionContext context)
    {
        if (HasFlagsAttribute(enumType))
            return true;

        var displayName = enumType.ToDisplayString();
        var fullyQualifiedName = enumType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (fullyQualifiedName.StartsWith("global::", StringComparison.Ordinal))
            fullyQualifiedName = fullyQualifiedName["global::".Length..];

        if (context.IsFlagsEnum(displayName)
            || context.IsFlagsEnum(fullyQualifiedName))
            return true;

        return false;
    }

    private static bool IsRegisteredExplicitValueEnum(INamedTypeSymbol enumType, ConversionContext context)
    {
        var displayName = enumType.ToDisplayString();
        var fullyQualifiedName = enumType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (fullyQualifiedName.StartsWith("global::", StringComparison.Ordinal))
            fullyQualifiedName = fullyQualifiedName["global::".Length..];

        return context.IsExplicitValueEnum(displayName)
            || context.IsExplicitValueEnum(fullyQualifiedName);
    }

    private static bool HasFlagsAttribute(INamedTypeSymbol enumType)
    {
        return enumType.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString() is "System.FlagsAttribute"
                or "System.Flags"
                or "FlagsAttribute"
                or "Flags");
    }

    /// <summary>
    /// Fallback: when GetSymbolInfo can't resolve the operator method, use operand type info
    /// to detect user-defined operators and emit the Java static method call.
    /// </summary>
    private string? TryTransformOperatorByTypeInfo(BinaryExpressionSyntax node, string op, ConversionContext context)
    {
        var semanticModel = context.SemanticModel!;
        var leftType = semanticModel.GetTypeInfo(node.Left).Type;
        var rightType = semanticModel.GetTypeInfo(node.Right).Type;

        // If both operand types are definitively built-in (e.g. double + double),
        // no user-defined operator can apply — skip the syntax-based inference fallback.
        if (leftType != null && rightType != null
            && leftType is INamedTypeSymbol leftNamed && IsBuiltInType(leftNamed)
            && rightType is INamedTypeSymbol rightNamed && IsBuiltInType(rightNamed))
            return null;

        // Prefer the left operand's type for the operator container (e.g. Point * double → Point).
        // Skip error types (unresolved 'var' etc.) and interface types.
        bool IsValidOperatorType(INamedTypeSymbol? t) =>
            t != null && t.TypeKind != TypeKind.Error && t.TypeKind != TypeKind.Interface && !IsBuiltInType(t);

        INamedTypeSymbol? operatorType = null;
        if (IsValidOperatorType(leftType as INamedTypeSymbol))
            operatorType = (INamedTypeSymbol)leftType!;
        else if (IsValidOperatorType(rightType as INamedTypeSymbol))
            operatorType = (INamedTypeSymbol)rightType!;

        // If both GetTypeInfo calls failed, try to resolve the operand types via their symbols
        if (operatorType == null)
        {
            static INamedTypeSymbol? ResolveOperandType(ExpressionSyntax expr, SemanticModel sm)
            {
                var type = sm.GetTypeInfo(expr).Type;
                if (type is INamedTypeSymbol n && n.TypeKind != TypeKind.Error)
                    return n;
                var sym = sm.GetSymbolInfo(expr).Symbol;
                var st = sym switch
                {
                    ILocalSymbol ls => ls.Type,
                    IFieldSymbol fs => fs.Type,
                    IParameterSymbol ps => ps.Type,
                    IPropertySymbol pr => pr.Type,
                    _ => null
                };
                if (st is INamedTypeSymbol sn && sn.TypeKind != TypeKind.Error)
                    return sn;
                // Try scope-based lookup for var-declared locals that GetSymbolInfo misses
                if (expr is IdentifierNameSyntax id)
                {
                    var lookup = sm.LookupSymbols(expr.SpanStart, name: id.Identifier.Text)
                        .Select(s => s switch
                        {
                            ILocalSymbol ls => ls.Type,
                            IParameterSymbol ps => ps.Type,
                            IFieldSymbol fs => fs.Type,
                            _ => null
                        })
                        .OfType<INamedTypeSymbol>()
                        .FirstOrDefault(t => t.TypeKind != TypeKind.Error);
                    if (lookup != null) return lookup;
                }
                return null;
            }
            var resolvedLeft = ResolveOperandType(node.Left, semanticModel);
            var resolvedRight = ResolveOperandType(node.Right, semanticModel);
            if (IsValidOperatorType(resolvedLeft))
                operatorType = resolvedLeft;
            else if (IsValidOperatorType(resolvedRight))
                operatorType = resolvedRight;
        }

        // Syntax-based fallback: if semantic model can't resolve types, try to infer
        // operator type from operand structure (method calls on known types, etc.)
        if (operatorType == null)
        {
            operatorType = InferOperatorTypeFromSyntax(node, semanticModel);
        }

        if (operatorType == null) return null;

        // Map SyntaxKind to Roslyn operator method name, then to Java method name
        var roslynOpName = SyntaxKindToRoslynOperatorName(node.Kind());
        if (roslynOpName == null) return null;

        // Verify the type actually declares this operator
        bool hasSrcOp = operatorType.GetMembers(roslynOpName).Any(m =>
                m is IMethodSymbol operatorMethod
                && operatorMethod.MethodKind == MethodKind.UserDefinedOperator
                && OperatorHasSourceDeclaration(operatorMethod));
        bool hasBclOp = !hasSrcOp
            && operatorType.GetMembers(roslynOpName).Any(m =>
                m is IMethodSymbol operatorMethod
                && operatorMethod.MethodKind == MethodKind.UserDefinedOperator);
        if (!hasSrcOp && !hasBclOp)
            return null;

        var javaMethodName = CSharpToJava.Core.Transformers.Member.OperatorTransformer.OpSymbolToJavaName
            .TryGetValue(roslynOpName, out var n) ? n : roslynOpName;

        var facade = ExpressionTransformerFacade.Instance;
        var left = facade.Transform(node.Left, context);
        var right = facade.Transform(node.Right, context);

        var currentTypeName = context.CurrentType?.Name;
        var operatorTypeName = operatorType.Name;

        if (currentTypeName != null && operatorTypeName == currentTypeName)
            return $"{javaMethodName}({left}, {right})";

        var containingType = context.MapType(operatorType);
        var angleIdx = containingType.IndexOf('<');
        if (angleIdx > 0) containingType = containingType[..angleIdx];
        return $"{containingType}.{javaMethodName}({left}, {right})";
    }

    /// <summary>
    /// IR-based fallback for user-defined operators when GetSymbolInfo fails.
    /// </summary>
    private JavaExpression? TryTransformOperatorByTypeInfoToIR(BinaryExpressionSyntax node, string op, ConversionContext context)
    {
        var semanticModel = context.SemanticModel!;
        var leftType = semanticModel.GetTypeInfo(node.Left).Type;
        var rightType = semanticModel.GetTypeInfo(node.Right).Type;

        // If both operand types are definitively built-in, no user-defined operator applies.
        if (leftType != null && rightType != null
            && leftType is INamedTypeSymbol leftNamed2 && IsBuiltInType(leftNamed2)
            && rightType is INamedTypeSymbol rightNamed2 && IsBuiltInType(rightNamed2))
            return null;

        INamedTypeSymbol? operatorType = null;
        if (leftType is INamedTypeSymbol lnt && lnt.TypeKind != TypeKind.Error && lnt.TypeKind != TypeKind.Interface && !IsBuiltInType(lnt))
            operatorType = lnt;
        else if (rightType is INamedTypeSymbol rnt && rnt.TypeKind != TypeKind.Error && rnt.TypeKind != TypeKind.Interface && !IsBuiltInType(rnt))
            operatorType = rnt;

        if (operatorType == null) return null;

        var roslynOpName = SyntaxKindToRoslynOperatorName(node.Kind());
        if (roslynOpName == null) return null;

        // Check if the operator type maps to a compat library type that supports the operation
        var mappedType = context.MapType(operatorType);
        var angleIdx2 = mappedType.IndexOf('<');
        if (angleIdx2 > 0) mappedType = mappedType[..angleIdx2];

        bool hasSourceOperator = operatorType.GetMembers(roslynOpName).Any(m =>
                m is IMethodSymbol operatorMethod
                && operatorMethod.MethodKind == MethodKind.UserDefinedOperator
                && OperatorHasSourceDeclaration(operatorMethod));

        // Also check for BCL operators on types that map to compat types
        // (e.g. DateTime - DateTime → CSharpDateTime.subtract)
        bool hasBclOperator = !hasSourceOperator
            && operatorType.GetMembers(roslynOpName).Any(m =>
                m is IMethodSymbol operatorMethod
                && operatorMethod.MethodKind == MethodKind.UserDefinedOperator);

        if (!hasSourceOperator && !hasBclOperator)
            return null;

        var javaMethodName = CSharpToJava.Core.Transformers.Member.OperatorTransformer.OpSymbolToJavaName
            .TryGetValue(roslynOpName, out var n) ? n : roslynOpName;

        var facade = ExpressionTransformerFacade.Instance;
        var leftIR = facade.TransformToIR(node.Left, context);
        var rightIR = facade.TransformToIR(node.Right, context);

        var currentTypeName = context.CurrentType?.Name;
        var operatorTypeName = operatorType.Name;

        JavaExpression? target;
        if (currentTypeName != null && operatorTypeName == currentTypeName)
        {
            target = null;
        }
        else
        {
            var containingType = context.MapType(operatorType);
            var angleIdx = containingType.IndexOf('<');
            if (angleIdx > 0) containingType = containingType[..angleIdx];
            target = new JavaIdentifierExpression { Name = containingType };
        }

        var call = new JavaMethodCallExpression { Target = target, MethodName = javaMethodName };
        call.Arguments.Add(leftIR);
        call.Arguments.Add(rightIR);
        return call;
    }

    /// <summary>
    /// Syntax-based inference of the operator's containing type when all semantic-model methods fail.
    /// Tries GetTypeInfo on the whole expression first, then recurses into sub-expressions.
    /// Only returns non-built-in types (Point, Rectangle, etc.), never built-in types.
    /// </summary>
    private static INamedTypeSymbol? InferOperatorTypeFromSyntax(BinaryExpressionSyntax node, SemanticModel sm)
    {
        // First try GetTypeInfo on the whole operand expression
        static INamedTypeSymbol? TryGetWholeType(ExpressionSyntax expr, SemanticModel sm)
        {
            var t = sm.GetTypeInfo(expr).Type;
            if (t is INamedTypeSymbol n && n.TypeKind != TypeKind.Error && !IsBuiltInTypeStatic(n))
                return n;
            return null;
        }

        static INamedTypeSymbol? TryFromExpr(ExpressionSyntax expr, SemanticModel sm)
        {
            // First try the whole expression type
            var whole = TryGetWholeType(expr, sm);
            if (whole != null) return whole;

            // Try member access: get member type from symbol first, then walk receiver type members
            if (expr is MemberAccessExpressionSyntax ma)
            {
                var maSym = sm.GetSymbolInfo(ma).Symbol;
                if (maSym is IPropertySymbol { Type: INamedTypeSymbol maPt } && maPt.TypeKind != TypeKind.Error && !IsBuiltInTypeStatic(maPt))
                    return maPt;
                if (maSym is IFieldSymbol { Type: INamedTypeSymbol maFt } && maFt.TypeKind != TypeKind.Error && !IsBuiltInTypeStatic(maFt))
                    return maFt;
                // Resolve receiver type and look up member on it
                var recvType = TryFromExpr(ma.Expression, sm);
                if (recvType != null)
                {
                    var memberName = ma.Name.Identifier.Text;
                    bool foundPropertyOrField = false;
                    foreach (var m in recvType.GetMembers(memberName))
                    {
                        if (m is IPropertySymbol { Type: INamedTypeSymbol mt } && mt.TypeKind != TypeKind.Error && !IsBuiltInTypeStatic(mt))
                            return mt;
                        if (m is IFieldSymbol { Type: INamedTypeSymbol mft } && mft.TypeKind != TypeKind.Error && !IsBuiltInTypeStatic(mft))
                            return mft;
                        if (m is IPropertySymbol or IFieldSymbol)
                            foundPropertyOrField = true;
                    }
                    // If member is a property/field with built-in type, return null
                    // (expression resolves to built-in type, no user-defined operator applies)
                    if (foundPropertyOrField)
                        return null;
                    return recvType;
                }
            }
            // Try invocation: get return type, or receiver type
            if (expr is InvocationExpressionSyntax inv)
            {
                // First try the return type of the whole invocation
                var invType = sm.GetTypeInfo(inv).Type;
                if (invType is INamedTypeSymbol n && n.TypeKind != TypeKind.Error) return n;
                // Try to get return type from the invoked member symbol
                if (inv.Expression is MemberAccessExpressionSyntax invMa2)
                {
                    var memberSym = sm.GetSymbolInfo(invMa2).Symbol;
                    if (memberSym is IPropertySymbol { Type: INamedTypeSymbol pt } && pt.TypeKind != TypeKind.Error && !IsBuiltInTypeStatic(pt))
                        return pt;
                    if (memberSym is IMethodSymbol { ReturnType: INamedTypeSymbol mrt } && mrt.TypeKind != TypeKind.Error && !IsBuiltInTypeStatic(mrt))
                        return mrt;
                }
                // Then try the receiver
                if (inv.Expression is MemberAccessExpressionSyntax invMa)
                {
                    var recv = TryFromExpr(invMa.Expression, sm);
                    if (recv != null) return recv;
                }
            }
            // Try simple identifier type
            if (expr is IdentifierNameSyntax id)
            {
                var idType = sm.GetTypeInfo(id).Type;
                if (idType is INamedTypeSymbol n && n.TypeKind != TypeKind.Error && !IsBuiltInTypeStatic(n))
                    return n;
                var idSym = sm.GetSymbolInfo(id).Symbol;
                if (idSym is ILocalSymbol { Type: INamedTypeSymbol lsType } && lsType.TypeKind != TypeKind.Error && !IsBuiltInTypeStatic(lsType))
                    return lsType;
                if (idSym is IFieldSymbol { Type: INamedTypeSymbol fsType } && fsType.TypeKind != TypeKind.Error && !IsBuiltInTypeStatic(fsType))
                    return fsType;
                if (idSym is IParameterSymbol { Type: INamedTypeSymbol psType } && psType.TypeKind != TypeKind.Error && !IsBuiltInTypeStatic(psType))
                    return psType;
                // Try GetTypeInfo again on the identifier for cases where the local was inferred
                var idConvertedType = sm.GetTypeInfo(id).ConvertedType;
                if (idConvertedType is INamedTypeSymbol cn && cn.TypeKind != TypeKind.Error && !IsBuiltInTypeStatic(cn))
                    return cn;
                // Walk syntax tree to find var declaration and resolve type from initializer.
                // Try up to 5 levels of var-declaration chains to find a resolvable type.
                var decl = FindVarDeclaration(id);
                for (int chainDepth = 0; chainDepth < 5 && decl?.Initializer?.Value is ExpressionSyntax initExpr; chainDepth++)
                {
                    var initType = sm.GetTypeInfo(initExpr).Type;
                    if (initType is INamedTypeSymbol initN && initN.TypeKind != TypeKind.Error && !IsBuiltInTypeStatic(initN))
                        return initN;
                    var initResult = TryFromExpr(initExpr, sm);
                    if (initResult != null) return initResult;
                    // If initializer is a simple identifier (another var), follow the chain
                    if (initExpr is IdentifierNameSyntax chainId)
                        decl = FindVarDeclaration(chainId);
                    else
                        break;
                }
            }
            // Try parenthesized expression
            if (expr is ParenthesizedExpressionSyntax paren)
                return TryFromExpr(paren.Expression, sm);
            // Recurse into nested binary expressions but PREFER non-built-in types
            if (expr is BinaryExpressionSyntax bin)
            {
                var left = TryFromExpr(bin.Left, sm);
                var right = TryFromExpr(bin.Right, sm);
                // Return the first non-built-in type found (left then right)
                if (left != null && !IsBuiltInTypeStatic(left)) return left;
                if (right != null && !IsBuiltInTypeStatic(right)) return right;
                if (left != null) return left;
                return right;
            }
            return null;
        }

        // Walk up the syntax tree to find the variable declaration for a given identifier.
        // Used when semantic model can't resolve var-declared local types.
        static Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax? FindVarDeclaration(IdentifierNameSyntax id)
        {
            var name = id.Identifier.Text;
            var parent = id.Parent;
            while (parent != null)
            {
                if (parent is BlockSyntax block)
                {
                    foreach (var stmt in block.Statements)
                    {
                        if (stmt is LocalDeclarationStatementSyntax localDecl)
                        {
                            foreach (var v in localDecl.Declaration.Variables)
                                if (v.Identifier.Text == name) return v;
                        }
                    }
                }
                else if (parent is ForEachStatementSyntax fe && fe.Identifier.Text == name)
                {
                    // For foreach (var x in col), x has no initializer, but we can check col
                    return null;
                }
                parent = parent.Parent;
            }
            return null;
        }

        var result = TryFromExpr(node.Left, sm) ?? TryFromExpr(node.Right, sm);
        // Only return non-built-in types (the caller will verify operator membership)
        if (result != null && !IsBuiltInTypeStatic(result) && result.TypeKind != TypeKind.Error)
            return result;
        return null;
    }

    internal static string? SyntaxKindToRoslynOperatorNameStatic(Microsoft.CodeAnalysis.CSharp.SyntaxKind? kind) => kind switch
    {
        Microsoft.CodeAnalysis.CSharp.SyntaxKind.AddExpression => "op_Addition",
        Microsoft.CodeAnalysis.CSharp.SyntaxKind.SubtractExpression => "op_Subtraction",
        Microsoft.CodeAnalysis.CSharp.SyntaxKind.MultiplyExpression => "op_Multiply",
        Microsoft.CodeAnalysis.CSharp.SyntaxKind.DivideExpression => "op_Division",
        Microsoft.CodeAnalysis.CSharp.SyntaxKind.ModuloExpression => "op_Modulus",
        Microsoft.CodeAnalysis.CSharp.SyntaxKind.EqualsExpression => "op_Equality",
        Microsoft.CodeAnalysis.CSharp.SyntaxKind.NotEqualsExpression => "op_Inequality",
        Microsoft.CodeAnalysis.CSharp.SyntaxKind.GreaterThanExpression => "op_GreaterThan",
        Microsoft.CodeAnalysis.CSharp.SyntaxKind.LessThanExpression => "op_LessThan",
        Microsoft.CodeAnalysis.CSharp.SyntaxKind.GreaterThanOrEqualExpression => "op_GreaterThanOrEqual",
        Microsoft.CodeAnalysis.CSharp.SyntaxKind.LessThanOrEqualExpression => "op_LessThanOrEqual",
        Microsoft.CodeAnalysis.CSharp.SyntaxKind.BitwiseAndExpression => "op_BitwiseAnd",
        Microsoft.CodeAnalysis.CSharp.SyntaxKind.BitwiseOrExpression => "op_BitwiseOr",
        Microsoft.CodeAnalysis.CSharp.SyntaxKind.ExclusiveOrExpression => "op_ExclusiveOr",
        _ => null
    };

    private static string? SyntaxKindToRoslynOperatorName(Microsoft.CodeAnalysis.CSharp.SyntaxKind kind) =>
        SyntaxKindToRoslynOperatorNameStatic(kind);

    private static bool IsStringType(ExpressionSyntax expr, SemanticModel semanticModel)
    {
        var typeInfo = semanticModel.GetTypeInfo(expr);
        return typeInfo.Type?.SpecialType == SpecialType.System_String;
    }

    private static bool OperatorHasSourceDeclaration(IMethodSymbol operatorSymbol)
        => operatorSymbol.Locations.Any(location => location.IsInSource)
            || operatorSymbol.DeclaringSyntaxReferences.Length > 0;

    internal static bool IsBuiltInTypeStatic(INamedTypeSymbol type) =>
        type.TypeKind == TypeKind.Enum ||
        (type.SpecialType != SpecialType.None && !IsMappedToCompatType(type.ToDisplayString())) ||
        BuiltInTypeNames.Contains(type.ToDisplayString());

    private static bool IsBuiltInType(INamedTypeSymbol type)
    {
        // Check if the type is a built-in C# type (int, double, string, bool, etc.)
        var typeName = type.ToDisplayString();
        return type.TypeKind == TypeKind.Enum ||
               (type.SpecialType != SpecialType.None && !IsMappedToCompatType(typeName)) ||
               BuiltInTypeNames.Contains(typeName);
    }

    // Types that have SpecialType != None but are mapped to compat library types
    // which support operator methods (e.g. DateTime - DateTime → CSharpDateTime.subtract)
    private static readonly HashSet<string> CompatMappedTypeNames = new(StringComparer.Ordinal)
    {
        "System.DateTime",
        "System.TimeSpan",
        "System.DateTimeOffset",
        "DateTime",
        "TimeSpan",
        "DateTimeOffset",
    };

    private static bool IsMappedToCompatType(string typeName)
        => CompatMappedTypeNames.Contains(typeName);

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
        ,"System.Type"
    };

    private string TransformUserDefinedOperator(BinaryExpressionSyntax node, IMethodSymbol operatorSymbol, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;

        // Get the Java method name for this operator (e.g., operator+ → add)
        var javaMethodName = GetOperatorMethodName(operatorSymbol);

        // Transform both operands
        var left = facade.Transform(node.Left, context);
        var right = facade.Transform(node.Right, context);

        // For user-defined operators, we need to call the static method
        // Format: TypeName.method(left, right) or method(left, right) if in same class.
        // Only omit the class qualifier when the call site is inside the operator's own class.
        // Being in the same namespace/package is NOT sufficient — Java requires the class name.
        var currentTypeName = context.CurrentType?.Name;  // e.g. "DemoSet"
        var operatorTypeName = operatorSymbol.ContainingType.Name; // e.g. "DemoSet" (no generics)

        if (currentTypeName != null && operatorTypeName == currentTypeName)
        {
            return $"{javaMethodName}({left}, {right})";
        }
        else
        {
            var containingType = context.MapType(operatorSymbol.ContainingType);
            // Strip type parameters from the class name used as a static call qualifier
            // e.g. "DemoSet<T>" → "DemoSet"; Java doesn't allow type args on static calls.
            var angleIdx = containingType.IndexOf('<');
            if (angleIdx > 0) containingType = containingType[..angleIdx];

            // Java name lookup treats member names and type names in the same space at call sites.
            // If the current type also declares a member with the same simple name as the operator
            // container type (e.g. field/property Point), force a fully-qualified static receiver.
            var operatorTypeSimpleName = operatorSymbol.ContainingType.Name;
            if (context.SemanticModel?.GetEnclosingSymbol(node.SpanStart)?.ContainingType is INamedTypeSymbol enclosingType
                && enclosingType.GetMembers(operatorTypeSimpleName).Any(m => m is not INamedTypeSymbol))
            {
                var ns = operatorSymbol.ContainingType.ContainingNamespace?.ToDisplayString();
                containingType = string.IsNullOrWhiteSpace(ns)
                    ? operatorTypeSimpleName
                    : $"{ns}.{operatorTypeSimpleName}";
            }

            return $"{containingType}.{javaMethodName}({left}, {right})";
        }
    }

    private static string GetOperatorMethodName(IMethodSymbol operatorSymbol)
    {
        // Use the shared operator-name map from OperatorTransformer to avoid divergence
        return CSharpToJava.Core.Transformers.Member.OperatorTransformer.OpSymbolToJavaName
            .TryGetValue(operatorSymbol.Name, out var name)
            ? name
            : operatorSymbol.Name;
    }

    private static bool IsInSameCompilationUnit(ConversionContext context, INamedTypeSymbol type)
    {
        // Check if the type is defined in the current namespace/compilation unit
        var currentNs = context.CurrentNamespace;
        var typeNs = type.ContainingNamespace?.ToDisplayString() ?? "";

        // Simple heuristic: same namespace means same compilation unit
        return currentNs == typeNs || string.IsNullOrEmpty(typeNs);
    }

    private string WrapOperandIfNeeded(ExpressionSyntax operand, string transformedOperand, string parentOp, bool isLeft)
    {
        // If the operand is a binary expression with lower precedence, wrap in parentheses
        if (operand is BinaryExpressionSyntax)
        {
            var operandKind = operand.Kind();
            var operandPrecedence = GetOperatorPrecedence(operandKind);
            var parentPrecedence = GetOperatorPrecedenceFromToken(parentOp);

            // For left operand, only wrap if operand has lower precedence
            // For right operand, wrap if operand has lower or equal precedence (for right associativity)
            if (isLeft)
            {
                if (operandPrecedence < parentPrecedence) return $"({transformedOperand})";
            }
            else
            {
                if (operandPrecedence <= parentPrecedence) return $"({transformedOperand})";
            }
        }

        // Expressions emitted as "expr & 0xFF" (C# byte/ushort/uint casts and byte[] reads)
        // have bitwise-& precedence. Wrap when the parent operator has higher precedence
        // so the mask applies to the whole operand, not just part of it.
        if (transformedOperand.Contains("& 0xFF") && !transformedOperand.TrimEnd().EndsWith(")"))
        {
            var parentPrecedence = GetOperatorPrecedenceFromToken(parentOp);
            if (parentPrecedence > 7) // 7 is & precedence; higher number = higher precedence
            {
                return $"({transformedOperand})";
            }
        }

        return transformedOperand;
    }

    private static int GetOperatorPrecedence(SyntaxKind kind)
    {
        return kind switch
        {
            SyntaxKind.CoalesceExpression => 1,
            SyntaxKind.ConditionalExpression => 2,
            SyntaxKind.LogicalOrExpression => 3,
            SyntaxKind.LogicalAndExpression => 4,
            SyntaxKind.BitwiseOrExpression => 5,
            SyntaxKind.ExclusiveOrExpression => 6,
            SyntaxKind.BitwiseAndExpression => 7,
            SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression => 8,
            SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression or
            SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression => 9,
            SyntaxKind.LeftShiftExpression or SyntaxKind.RightShiftExpression or SyntaxKind.UnsignedRightShiftExpression => 10,
            SyntaxKind.AddExpression or SyntaxKind.SubtractExpression => 11,
            SyntaxKind.MultiplyExpression or SyntaxKind.DivideExpression or SyntaxKind.ModuloExpression => 12,
            _ => 0
        };
    }

    private static int GetOperatorPrecedenceFromToken(string op)
    {
        return op switch
        {
            "||" => 3,
            "&&" => 4,
            "|" => 5,
            "^" => 6,
            "&" => 7,
            "==" or "!=" => 8,
            "<" or "<=" or ">" or ">=" => 9,
            "<<" or ">>" or ">>>" => 10,
            "+" or "-" => 11,
            "*" or "/" or "%" => 12,
            _ => 0
        };
    }

    private string TransformCoalesceExpression(BinaryExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var left = facade.Transform(node.Left, context);
        var right = facade.Transform(node.Right, context);

        // If the left operand is always null (e.g. from 'IEnumerable as T[]' which
        // always yields null in Java), simplify to just the right operand.
        if (left == "null")
        {
            return right;
        }

        // If the left operand is already a ternary from ?. conversion (e.g. _array?.Length),
        // the null check is already embedded. Replace the false branch with the ?? right side.
        // Pattern: "(expr != null ? ... : falseBranch)" → "(expr != null ? ... : rightSide)"
        if (left.StartsWith("(") && left.Contains(" != null ? ") && left.Contains(" : "))
        {
            // Find the last " : " to split the ternary (handles nested ternaries)
            var colonIndex = left.LastIndexOf(" : ");
            if (colonIndex > 0)
            {
                var beforeColon = left.Substring(0, colonIndex);
                var falseBranch = left.Substring(colonIndex + 3);
                // If the false branch is already the same as right, return as-is
                if (falseBranch.Trim() == right.Trim())
                {
                    return left;
                }
                // Preserve trailing characters from falseBranch (e.g. closing parentheses)
                // that are not part of the value itself. e.g. "null)" → keep ")"
                var trailing = "";
                for (int i = falseBranch.Length - 1; i >= 0; i--)
                {
                    if (falseBranch[i] == ')' || falseBranch[i] == ']' || falseBranch[i] == '}')
                        trailing = falseBranch[i] + trailing;
                    else
                        break;
                }
                // Replace the false branch with the ?? right side
                return $"{beforeColon} : {right}{trailing}";
            }
        }

        // Avoid evaluating the left operand twice when it has side effects.
        // Simple identifiers and single-level member accesses are safe to repeat.
        bool isSafeToRepeat = node.Left is IdentifierNameSyntax
            || node.Left is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax };

        if (isSafeToRepeat)
        {
            // C# a ?? b  → Java  a != null ? a : b
            return $"{left} != null ? {left} : {right}";
        }
        else
        {
            // Use a temp variable so the left expression is evaluated only once
            var tmpName = context.GenerateSyntheticName("_coalesce");
            context.AddPreStatement($"var {tmpName} = {left};");
            return $"{tmpName} != null ? {tmpName} : {right}";
        }
    }
}
