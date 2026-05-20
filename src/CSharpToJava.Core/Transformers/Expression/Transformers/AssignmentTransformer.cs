using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers;
using CSharpToJava.Core.Transformers.Expression.Utilities;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles assignment expressions (simple, compound, and shifted assignments).
/// </summary>
[TransformerRegistration]
public class AssignmentTransformer : IIRExpressionTransformer
{
    static AssignmentTransformer()
    {
        ExpressionTransformerRegistry.Register(new[]
        {
            SyntaxKind.SimpleAssignmentExpression,
            SyntaxKind.AddAssignmentExpression,
            SyntaxKind.SubtractAssignmentExpression,
            SyntaxKind.MultiplyAssignmentExpression,
            SyntaxKind.DivideAssignmentExpression,
            SyntaxKind.ModuloAssignmentExpression,
            SyntaxKind.AndAssignmentExpression,
            SyntaxKind.OrAssignmentExpression,
            SyntaxKind.ExclusiveOrAssignmentExpression,
            SyntaxKind.LeftShiftAssignmentExpression,
            SyntaxKind.RightShiftAssignmentExpression,
            SyntaxKind.CoalesceAssignmentExpression,   // Fix 3: register ??=
        }, new AssignmentTransformer());
    }

    private static readonly Lazy<AssignmentTransformer> _instance = new(() => new());
    public static AssignmentTransformer Instance => _instance.Value;
    private static int _thisAssignCounter;

    public string Transform(ExpressionSyntax node, ConversionContext context)
        => node.Kind() switch
        {
            SyntaxKind.SimpleAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "=", context),
            SyntaxKind.AddAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "+=", context),
            SyntaxKind.SubtractAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "-=", context),
            SyntaxKind.MultiplyAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "*=", context),
            SyntaxKind.DivideAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "/=", context),
            SyntaxKind.ModuloAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "%=", context),
            SyntaxKind.AndAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "&=", context),
            SyntaxKind.OrAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "|=", context),
            SyntaxKind.ExclusiveOrAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "^=", context),
            SyntaxKind.LeftShiftAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, "<<=", context),
            SyntaxKind.RightShiftAssignmentExpression => TransformAssignment((AssignmentExpressionSyntax)node, ">>=", context),
            SyntaxKind.CoalesceAssignmentExpression => TransformCoalesceAssignment((AssignmentExpressionSyntax)node, context),
            _ => throw new NotSupportedException($"Assignment expression kind {node.Kind()} not supported.")
        };

    /// <inheritdoc />
    public JavaExpression TransformToIR(ExpressionSyntax node, ConversionContext context)
    {
        if (node is not AssignmentExpressionSyntax assignment)
            return new JavaRawExpression(Transform(node, context));

        // Coalesce assignment (??=) always needs special handling
        if (assignment.Kind() == SyntaxKind.CoalesceAssignmentExpression)
            return new JavaRawExpression(Transform(node, context));

        var facade = ExpressionTransformerFacade.Instance;

        // Only produce structured IR for simple identifier assignments to local/field/param
        if (assignment.Left is IdentifierNameSyntax ident)
        {
            var symbol = context.SemanticModel?.GetSymbolInfo(ident).Symbol;

            // Property, event → setter calls, complex handling
            if (symbol is IPropertySymbol or IEventSymbol)
                return new JavaRawExpression(Transform(node, context));

            // Out/ref param → .value = rhs
            if (symbol is IParameterSymbol param
                && (param.RefKind == RefKind.Out || param.RefKind == RefKind.Ref))
                return new JavaRawExpression(Transform(node, context));

            string op = GetOperatorForKind(assignment.Kind());

            // User-defined operator on compound assignment → static method call
            if (op != "=" && context.SemanticModel != null)
            {
                var symbolInfo = context.SemanticModel.GetSymbolInfo(assignment);
                if (symbolInfo.Symbol is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator })
                    return new JavaRawExpression(Transform(node, context));
            }

            var leftIR = facade.TransformToIR(ident, context);
            var rightIR = facade.TransformToIR(assignment.Right, context);
            return new JavaAssignmentExpression
            {
                Target = leftIR,
                Operator = op,
                Value = rightIR
            };
        }

        // MemberAccess, IndexerAccess, this assignments → complex property/indexer handling
        return new JavaRawExpression(Transform(node, context));
    }

    private static string GetOperatorForKind(SyntaxKind kind) => kind switch
    {
        SyntaxKind.SimpleAssignmentExpression => "=",
        SyntaxKind.AddAssignmentExpression => "+=",
        SyntaxKind.SubtractAssignmentExpression => "-=",
        SyntaxKind.MultiplyAssignmentExpression => "*=",
        SyntaxKind.DivideAssignmentExpression => "/=",
        SyntaxKind.ModuloAssignmentExpression => "%=",
        SyntaxKind.AndAssignmentExpression => "&=",
        SyntaxKind.OrAssignmentExpression => "|=",
        SyntaxKind.ExclusiveOrAssignmentExpression => "^=",
        SyntaxKind.LeftShiftAssignmentExpression => "<<=",
        SyntaxKind.RightShiftAssignmentExpression => ">>=",
        _ => "="
    };

    private string TransformAssignment(AssignmentExpressionSyntax node, string op, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var leftNode = node.Left;
        var rightNode = node.Right;

        // Fix: `this = expr` in C# struct methods — Java can't assign to `this`.
        // Expand to field-by-field copy from the RHS value.
        if (op == "=" && leftNode is ThisExpressionSyntax && context.CurrentType is Java.JavaClassDeclaration { IsConvertedFromStruct: true } structClass)
        {
            return ExpandThisAssignment(structClass, rightNode, context);
        }

        // Fix 4: Detect event += / -= using semantic model → listener methods
        if ((op == "+=" || op == "-=") && leftNode is MemberAccessExpressionSyntax evtMa)
        {
            if (context.SemanticModel?.GetSymbolInfo(leftNode).Symbol is IEventSymbol evt)
            {
                var receiver = facade.Transform(evtMa.Expression, context);
                var handler = facade.Transform(rightNode, context);
                string method = op == "+="
                    ? $"add{evt.Name}Listener"
                    : $"remove{evt.Name}Listener";
                return $"{receiver}.{method}({handler})";
            }
        }

        if ((op == "+=" || op == "-=") && leftNode is IdentifierNameSyntax)
        {
            if (context.SemanticModel?.GetSymbolInfo(leftNode).Symbol is IEventSymbol evt)
            {
                var listenerField = $"_{char.ToLowerInvariant(evt.Name[0])}{evt.Name[1..]}Listeners";
                string handler;
                if (rightNode is IdentifierNameSyntax { Identifier.Text: "value" }
                    && context.CurrentMethod?.Parameters.Length == 1)
                {
                    handler = ConversionContext.EscapeJavaKeyword(context.CurrentMethod.Parameters[0].Name);
                }
                else
                {
                    handler = facade.Transform(rightNode, context);
                }

                var listMethod = op == "+=" ? "add" : "remove";
                return $"{listenerField}.{listMethod}({handler})";
            }
        }

        if ((op == "+=" || op == "-=") && leftNode is IdentifierNameSyntax)
        {
            var leftText = facade.Transform(leftNode, context);
            if (leftText.StartsWith("fire", StringComparison.Ordinal) && leftText.Length > 4)
            {
                var eventName = leftText[4..];
                var listenerField = $"_{char.ToLowerInvariant(eventName[0])}{eventName[1..]}Listeners";
                var handler = rightNode is IdentifierNameSyntax { Identifier.Text: "value" }
                    ? "handler"
                    : facade.Transform(rightNode, context);
                var listMethod = op == "+=" ? "add" : "remove";
                return $"{listenerField}.{listMethod}({handler})";
            }
        }

        // Fix 1: Detect property assignments using semantic model → setter calls (simple assignment only)
        if (op == "=" && leftNode is MemberAccessExpressionSyntax propMa)
        {
            if (context.SemanticModel?.GetSymbolInfo(leftNode).Symbol is IPropertySymbol prop)
            {
                // If this property assignment is used as a sub-expression (not a standalone statement),
                // the setter call would return void in Java which is invalid as a value.
                // Exception: explicit setter methods generate a direct field write (this.field = value)
                // which IS a valid Java value expression, so no hoisting needed in that case.
                bool inExplicitSetter = IsInExplicitSetterMethod(prop, context)
                    && propMa.Expression is ThisExpressionSyntax;
                if (!inExplicitSetter && node.Parent is not ExpressionStatementSyntax)
                {
                    // Hoist: emit setter as a pre-statement and return the temp variable holding the value.
                    return HoistChainedPropertyAssignment(node, context);
                }

                var receiver = facade.Transform(propMa.Expression, context);
                // When a using alias collides with an instance property on the
                // enclosing type (e.g. "using Label = X;" + "public Label Label {…}"),
                // the semantic model may resolve the identifier as the type rather than
                // the property.  Detect this via the syntax tree and coerce to the getter.
                if (receiver.Contains(".") && propMa.Expression is IdentifierNameSyntax recvId)
                {
                    var recvName = recvId.Identifier.Text;
                    var enclosingTypeDecl = recvId.Ancestors()
                        .OfType<TypeDeclarationSyntax>()
                        .FirstOrDefault();
                    if (enclosingTypeDecl != null
                        && enclosingTypeDecl.Members
                            .OfType<PropertyDeclarationSyntax>()
                            .Any(p => p.Identifier.Text == recvName))
                    {
                        receiver = "get" + char.ToUpperInvariant(recvName[0]) + recvName[1..] + "()";
                    }
                }
                if (prop.Name == "Capacity"
                    && prop.ContainingType?.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.List<T>")
                {
                    var rightCapacity = facade.Transform(rightNode, context);
                    return $"{receiver}.ensureCapacity({rightCapacity})";
                }

                if (prop.Name == "Position"
                    && IsSystemIoStreamType(prop.ContainingType))
                {
                    var rightPosition = facade.Transform(rightNode, context);
                    rightPosition = ExpressionTransformerHelpers.AdaptExpressionToTargetType(
                        rightNode,
                        rightPosition,
                        prop.Type,
                        context);
                    return $"{receiver}.setPosition({rightPosition})";
                }

                // If RHS is itself a property setter assignment, hoist to avoid void-return nesting
                // e.g. p1.X = p2.X = p3.X  →  var _chainVal0 = p3.getX(); p2.setX(_chainVal0); p1.setX(_chainVal0)
                var right = IsPropertySetterAssignment(rightNode, context)
                    ? HoistChainedPropertyAssignment(rightNode, context)
                    : facade.Transform(rightNode, context);

                // Fix: Array assignment to IList/ICollection property - wrap with Arrays.asList()
                if (context.SemanticModel != null && op == "=")
                {
                    var rhsType = context.SemanticModel.GetTypeInfo(rightNode).Type;
                    var propType = prop.Type;

                    if (rhsType is IArrayTypeSymbol arrayType && propType is INamedTypeSymbol propNamed
                        && IsEnumerableOrCollectionInterface(propNamed))
                    {
                        right = ObjectCreationTransformer.WrapArrayForCollectionArg(right, arrayType, context);
                    }

                    right = ExpressionTransformerHelpers.AdaptExpressionToTargetType(rightNode, right, propType, context);
                }

                // Avoid recursion when an explicit SetX(...) method assigns to property X.
                // In that case we need a direct backing-field write, not a setter call.
                if (inExplicitSetter)
                {
                    string fieldName = char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..];
                    return $"this.{fieldName} = {right}";
                }

                string setter = "set" + char.ToUpperInvariant(prop.Name[0]) + prop.Name[1..];
                return $"{receiver}.{setter}({right})";
            }
        }

        // Fix 2: Detect indexer assignments → put/set methods (simple assignment only)
        if (op == "=" && leftNode is ElementAccessExpressionSyntax ela)
        {
            var indexerSymbol = context.SemanticModel?.GetSymbolInfo(ela).Symbol as IPropertySymbol;
            var containerExprType = context.SemanticModel?.GetTypeInfo(ela.Expression).Type;
            bool isArrayElement = containerExprType is IArrayTypeSymbol;
            bool isIndexerAssignment = !isArrayElement
                && (indexerSymbol?.IsIndexer == true || indexerSymbol == null);

            if (isIndexerAssignment)
            {
                var target = facade.Transform(ela.Expression, context);
                var argList = ela.ArgumentList.Arguments;
                var right = facade.Transform(rightNode, context);
                ITypeSymbol? keyType = indexerSymbol?.Parameters.FirstOrDefault()?.Type;
                ITypeSymbol? valueType = indexerSymbol?.Type;

                if (argList.Count == 1)
                {
                    var argExpr = argList[0].Expression;
                    var containerType = (ITypeSymbol?)indexerSymbol?.ContainingType
                        ?? context.SemanticModel?.GetTypeInfo(ela.Expression).Type;
                    string method = "set"; // default for indexers
                    if (containerType is INamedTypeSymbol namedContainer)
                    {
                        var fullName = namedContainer.OriginalDefinition.ToDisplayString();
                        bool isDictionaryContainer = IsDictionaryLikeContainer(namedContainer);
                        bool isListContainer = fullName is
                            "System.Collections.Generic.List<T>"
                            or "System.Collections.Generic.IList<T>"
                            or "System.Collections.Generic.IReadOnlyList<T>"
                            or "System.Collections.Immutable.ImmutableArray<T>";
                        if (isDictionaryContainer) method = "put";
                        else if (isListContainer) method = "set";

                        if (namedContainer.TypeArguments.Length >= 2 && isDictionaryContainer)
                        {
                            keyType ??= namedContainer.TypeArguments[0];
                            valueType ??= namedContainer.TypeArguments[1];
                        }
                        else if (namedContainer.TypeArguments.Length >= 1 && isListContainer)
                        {
                            valueType ??= namedContainer.TypeArguments[0];
                        }
                    }
                    else if (indexerSymbol == null)
                    {
                        // Semantic model failed to resolve (common in partial/incomplete compilations);
                        // for map-like indexers this should be put(key, value) instead of get(key)=value.
                        method = "put";
                    }
                    var arg0 = facade.Transform(argExpr, context);
                    arg0 = ExpressionTransformerHelpers.AdaptExpressionToTargetType(argExpr, arg0, keyType, context);
                    right = ExpressionTransformerHelpers.AdaptExpressionToTargetType(rightNode, right, valueType, context);
                    return $"{target}.{method}({arg0}, {right})";
                }

                // Multi-argument indexer: use set(arg0, arg1, ..., value)
                var transformedArgs = string.Join(
                    ", ",
                    argList.Select((argument, index) =>
                    {
                        var transformedArg = facade.Transform(argument.Expression, context);
                        var parameterType = indexerSymbol?.Parameters.Length > index
                            ? indexerSymbol.Parameters[index].Type
                            : null;
                        return ExpressionTransformerHelpers.AdaptExpressionToTargetType(
                            argument.Expression,
                            transformedArg,
                            parameterType,
                            context);
                    }));
                right = ExpressionTransformerHelpers.AdaptExpressionToTargetType(rightNode, right, valueType, context);
                return $"{target}.set({transformedArgs}, {right})";
            }
        }

        static bool IsDictionaryLikeContainer(INamedTypeSymbol type)
        {
            var self = type.OriginalDefinition.ToDisplayString();
            if (type.Name.Contains("Dictionary", StringComparison.Ordinal)
                || type.Name.Contains("SortedList", StringComparison.Ordinal)
                || type.Name.Contains("Map", StringComparison.Ordinal))
                return true;

            if (self is
                "System.Collections.Generic.Dictionary<TKey, TValue>"
                or "System.Collections.Generic.SortedDictionary<TKey, TValue>"
                or "System.Collections.Generic.SortedList<TKey, TValue>"
                or "System.Collections.Immutable.ImmutableDictionary<TKey, TValue>"
                or "System.Collections.Generic.IDictionary<TKey, TValue>"
                or "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>")
                return true;

            return type.AllInterfaces.Any(i =>
            {
                var n = i.OriginalDefinition.ToDisplayString();
                return n is "System.Collections.Generic.IDictionary<TKey, TValue>"
                    or "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>"
                    || i.Name.Contains("Dictionary", StringComparison.Ordinal)
                    || i.Name.Contains("Map", StringComparison.Ordinal);
            });
        }

        // Fix 7: Compound assignment on indexer (e.g. dict[key] += value → dict.put(key, dict.get(key) + value))
        // Java does not support compound-assignment on method-call results.
        if (op != "=" && leftNode is ElementAccessExpressionSyntax compoundEla)
        {
            var compoundElaContainerType = context.SemanticModel?.GetTypeInfo(compoundEla.Expression).Type;
            bool isCompoundArray = compoundElaContainerType is IArrayTypeSymbol;
            if (!isCompoundArray && compoundEla.ArgumentList.Arguments.Count == 1)
            {
                var target = facade.Transform(compoundEla.Expression, context);
                var arg0 = facade.Transform(compoundEla.ArgumentList.Arguments[0].Expression, context);
                var rhs = facade.Transform(rightNode, context);
                string baseOp = op[..^1]; // "+=" → "+", "-=" → "-", "*=" → "*", etc.

                bool isDictLike = compoundElaContainerType is INamedTypeSymbol cn && IsDictionaryLikeContainer(cn);
                if (isDictLike)
                {
                    return $"{target}.put({arg0}, {target}.get({arg0}) {baseOp} {rhs})";
                }
                else
                {
                    // List-like: set(idx, get(idx) op rhs)
                    return $"{target}.set({arg0}, {target}.get({arg0}) {baseOp} {rhs})";
                }
            }
        }

        // Handle bare-identifier property assignment (e.g., Demo = value; → setDemo(value);)
        if (op == "=" && leftNode is IdentifierNameSyntax propIdentifier)
        {
            if (context.SemanticModel?.GetSymbolInfo(leftNode).Symbol is IPropertySymbol bareIdentProp)
            {
                // If this property assignment is used as a sub-expression (not a standalone statement),
                // the setter call would return void in Java which is invalid as a value.
                // Exception: explicit setter methods generate a direct field write (this.field = value)
                // which IS a valid Java value expression, so no hoisting needed in that case.
                bool bareInExplicitSetter = IsInExplicitSetterMethod(bareIdentProp, context);
                if (!bareInExplicitSetter && node.Parent is not ExpressionStatementSyntax)
                {
                    // Hoist: emit setter as a pre-statement and return the temp variable holding the value.
                    return HoistChainedPropertyAssignment(node, context);
                }

                if (bareIdentProp.Name == "Capacity"
                    && bareIdentProp.ContainingType?.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.List<T>")
                {
                    var rightCapacity = facade.Transform(rightNode, context);
                    return $"ensureCapacity({rightCapacity})";
                }

                var right = IsPropertySetterAssignment(rightNode, context)
                    ? HoistChainedPropertyAssignment(rightNode, context)
                    : facade.Transform(rightNode, context);

                // Fix: Array assignment to IList/ICollection property - wrap with Arrays.asList()
                if (context.SemanticModel != null)
                {
                    var rhsType = context.SemanticModel.GetTypeInfo(rightNode).Type;
                    var propType = bareIdentProp.Type;

                    if (rhsType is IArrayTypeSymbol arrayType && propType is INamedTypeSymbol propNamed
                        && IsEnumerableOrCollectionInterface(propNamed))
                    {
                        right = ObjectCreationTransformer.WrapArrayForCollectionArg(right, arrayType, context);
                    }

                    right = ExpressionTransformerHelpers.AdaptExpressionToTargetType(rightNode, right, propType, context);
                }

                if (bareInExplicitSetter)
                {
                    string fieldName = char.ToLowerInvariant(bareIdentProp.Name[0]) + bareIdentProp.Name[1..];
                    return $"this.{fieldName} = {right}";
                }

                string setter = "set" + char.ToUpperInvariant(bareIdentProp.Name[0]) + bareIdentProp.Name[1..];
                return $"{setter}({right})";
            }
        }

        // Detect assignment to an out/ref parameter inside a method body → paramName.value = rhs
        // Exception: read-only ref struct parameters are generated without ObjectHolder.
        if (op == "=" && leftNode is IdentifierNameSyntax outParamIdent)
        {
            if (context.SemanticModel?.GetSymbolInfo(leftNode).Symbol is IParameterSymbol param
                && (param.RefKind == RefKind.Out || param.RefKind == RefKind.Ref)
                && !context.IsReadOnlyRefStructParam(param.Name))
            {
                var right = facade.Transform(rightNode, context);
                return $"{ConversionContext.EscapeJavaKeyword(param.Name)}.value = {right}";
            }
        }

        // Fix 5: Compound assignment to a property via member access (e.g. a.Length *= 0.5).
        // Simple = already handled above; +=, -=, *=, /=, etc. need getter+setter expansion.
        // Guard: if the compound op resolves to a user-defined operator, let ExpandCompoundOperatorOverload below handle it.
        if (op != "=" && leftNode is MemberAccessExpressionSyntax compoundMa)
        {
            bool fix5IsUserDefined = context.SemanticModel?.GetSymbolInfo(node).Symbol
                is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator };
            if (!fix5IsUserDefined && context.SemanticModel?.GetSymbolInfo(leftNode).Symbol is IPropertySymbol compoundProp)
            {
                var getter = "get" + char.ToUpperInvariant(compoundProp.Name[0]) + compoundProp.Name[1..];
                var setter = "set" + char.ToUpperInvariant(compoundProp.Name[0]) + compoundProp.Name[1..];
                var rhs = facade.Transform(rightNode, context);
                string baseOp = op[..^1]; // "+=" → "+", "-=" → "-", "*=" → "*", etc.
                // If the property type has a user-defined operator for this operation,
                // use the qualified method call instead of the raw operator.
                string operatorExpr = TryResolveOperatorExpr(compoundProp.Type, baseOp, context)
                    ?? baseOp;
                // Simple receivers (local variable, field, "this") are safe to reference twice.
                if (compoundMa.Expression is IdentifierNameSyntax or ThisExpressionSyntax or MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax })
                {
                    var receiver = facade.Transform(compoundMa.Expression, context);
                    if (operatorExpr != baseOp)
                        return $"{receiver}.{setter}({operatorExpr}({receiver}.{getter}(), {rhs}))";
                    return $"{receiver}.{setter}({receiver}.{getter}() {baseOp} {rhs})";
                }
                else
                {
                    var receiverExpr = facade.Transform(compoundMa.Expression, context);
                    var tmpReceiver = context.GenerateSyntheticName("_recv");
                    context.AddPreStatement($"var {tmpReceiver} = {receiverExpr};");
                    if (operatorExpr != baseOp)
                        return $"{tmpReceiver}.{setter}({operatorExpr}({tmpReceiver}.{getter}(), {rhs}))";
                    return $"{tmpReceiver}.{setter}({tmpReceiver}.{getter}() {baseOp} {rhs})";
                }
            }
        }

        // Fix 6: Compound assignment to a property via bare identifier (e.g. Count += 1).
        // Guard: if the compound op resolves to a user-defined operator, let ExpandCompoundOperatorOverload below handle it.
        if (op != "=" && leftNode is IdentifierNameSyntax compoundIdent)
        {
            bool fix6IsUserDefined = context.SemanticModel?.GetSymbolInfo(node).Symbol
                is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator };
            if (!fix6IsUserDefined && context.SemanticModel?.GetSymbolInfo(leftNode).Symbol is IPropertySymbol compoundIdentProp)
            {
                var getter = "get" + char.ToUpperInvariant(compoundIdentProp.Name[0]) + compoundIdentProp.Name[1..];
                var setter = "set" + char.ToUpperInvariant(compoundIdentProp.Name[0]) + compoundIdentProp.Name[1..];
                var rhs = facade.Transform(rightNode, context);
                string baseOp = op[..^1];
                return $"{setter}({getter}() {baseOp} {rhs})";
            }
        }

        // Compound assignment where the operator is user-defined (e.g. Point2 += Point2).
        // Java has no operator overloading, so expand: lhs op= rhs → lhs = TypeName.method(lhs, rhs)
        // For property LHS this must also go through getter/setter.
        if (op != "=" && context.SemanticModel != null)
        {
            var symbolInfo = context.SemanticModel.GetSymbolInfo(node);
            if (symbolInfo.Symbol is IMethodSymbol opMethod
                && opMethod.MethodKind == MethodKind.UserDefinedOperator)
            {
                return ExpandCompoundOperatorOverload(node, opMethod, context);
            }

            // Fallback: when GetSymbolInfo fails, use operand type info to detect user-defined
            // operators and expand compound assignment (e.g. a /= d → a = Type.divide(a, d))
            if (symbolInfo.Symbol == null)
            {
                var fallbackResult = ExpandCompoundByTypeInfo(node, op, context);
                if (fallbackResult != null)
                    return fallbackResult;
            }
        }

        var left = facade.Transform(leftNode, context);
        // Guard: if the RHS is a property setter assignment, hoist it so we don't embed a void call
        // as the RHS of a plain assignment. e.g. a = p2.X = p3.X → pre: ...; a = _chainVal0
        if (op == "=" && IsPropertySetterAssignment(rightNode, context))
        {
            var tmpName = HoistChainedPropertyAssignment(rightNode, context);
            return $"{left} = {tmpName}";
        }

        // Fix: Array assignment to IList/ICollection property or variable
        // In C#, arrays implement IList<T>, so assigning an array to an IList<T> variable/property is valid.
        // In Java, arrays don't implement List<T>, so we must wrap with Arrays.asList().
        if (op == "=" && context.SemanticModel != null)
        {
            var rhsType = context.SemanticModel.GetTypeInfo(rightNode).Type;
            var lhsType = context.SemanticModel.GetTypeInfo(leftNode).Type;

            if (rhsType is IArrayTypeSymbol arrayType && IsCollectionOrListInterface(lhsType))
            {
                var right = facade.Transform(rightNode, context);
                right = ObjectCreationTransformer.WrapArrayForCollectionArg(right, arrayType, context);
                return $"{left} = {right}";
            }
        }

        var rightStr = facade.Transform(rightNode, context);

        if ((op == "+=" || op == "-=")
            && rightNode is IdentifierNameSyntax { Identifier.Text: "value" }
            && left.StartsWith("fire", StringComparison.Ordinal)
            && left.Length > 4)
        {
            var eventName = left[4..];
            var listenerField = $"_{char.ToLowerInvariant(eventName[0])}{eventName[1..]}Listeners";
            var listMethod = op == "+=" ? "add" : "remove";
            return $"{listenerField}.{listMethod}(handler)";
        }

        // Struct value copy: simple assignment of user-defined struct needs .clone()
        // to preserve C# value-type copy semantics.
        if (op == "=" && context.SemanticModel != null)
        {
            var rhsTypeForClone = context.SemanticModel.GetTypeInfo(rightNode).Type;
            rightStr = StructCloneHelper.CloneStructValueIfNeeded(rightNode, rightStr, rhsTypeForClone, context);
            var lhsType = context.SemanticModel.GetTypeInfo(leftNode).Type;
            rightStr = ExpressionTransformerHelpers.AdaptExpressionToTargetType(rightNode, rightStr, lhsType, context);

            // Stream → Iterable/Collection: when LHS is IEnumerable/ICollection/IList (Java Iterable/Collection)
            // and RHS is a Java stream expression (from LINQ .Where/.Select), collect the stream.
            // Java Stream does NOT implement Iterable, so direct assignment would fail.
            if (lhsType is INamedTypeSymbol lhsNamed
                && lhsNamed.Name is "IEnumerable" or "ICollection" or "IList" or "IReadOnlyCollection" or "IReadOnlyList"
                && lhsNamed.ContainingNamespace?.ToDisplayString().StartsWith("System") == true
                && IsLikelyStreamExpression(rightStr)
                && !rightStr.Contains(".collect("))
            {
                rightStr = $"{rightStr}.collect(Collectors.toCollection(() -> new ArrayList<>()))";
                context.AddImport("java.util.stream.Collectors");
                context.AddImport("java.util.ArrayList");
            }
        }

        return $"{left} {op} {rightStr}";
    }

    /// <summary>
    /// Returns true when <paramref name="expr"/> is a simple (=) assignment whose LHS resolves to
    /// an <see cref="IPropertySymbol"/>. Used to detect chained property assignments whose setter
    /// calls would produce invalid void-return nesting in Java if left un-hoisted.
    /// </summary>
    private bool IsPropertySetterAssignment(ExpressionSyntax expr, ConversionContext context)
    {
        if (expr is not AssignmentExpressionSyntax assign
            || assign.Kind() != SyntaxKind.SimpleAssignmentExpression)
            return false;

        return context.SemanticModel?.GetSymbolInfo(assign.Left).Symbol is IPropertySymbol;
    }

    private static bool IsInExplicitSetterMethod(IPropertySymbol property, ConversionContext context)
    {
        var currentMethod = context.CurrentMethod;
        if (currentMethod == null)
            return false;

        if (!SymbolEqualityComparer.Default.Equals(currentMethod.ContainingType, property.ContainingType))
            return false;

        if (currentMethod.Parameters.Length != 1)
            return false;

        return string.Equals(currentMethod.Name, "Set" + property.Name, StringComparison.Ordinal);
    }

    private static bool IsSystemIoStreamType(ITypeSymbol? type)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            if (current.ToDisplayString() == "System.IO.Stream")
                return true;
        }
        return false;
    }

    /// <summary>
    /// Recursively hoists a chained property-setter assignment into pre-statements so each setter
    /// is emitted as a separate void statement, returning the name of the temp variable that holds
    /// the innermost value. All levels of the chain share the same temp, so every property in the
    /// chain receives the same final value — matching C# chained-assignment semantics.
    /// <example>
    /// <code>
    /// // C#:   p1.X = p2.X = p3.X
    /// // pre-stmt 1 → var _chainVal0 = p3.getX();
    /// // pre-stmt 2 → p2.setX(_chainVal0);
    /// // main stmt  → p1.setX(_chainVal0);
    /// </code>
    /// </example>
    /// </summary>
    private string HoistChainedPropertyAssignment(ExpressionSyntax expr, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;

        if (expr is AssignmentExpressionSyntax assign
            && assign.Kind() == SyntaxKind.SimpleAssignmentExpression)
        {
            // MemberAccess property LHS: receiver.Prop = rhs
            if (assign.Left is MemberAccessExpressionSyntax ma
                && context.SemanticModel?.GetSymbolInfo(ma).Symbol is IPropertySymbol maProp)
            {
                var tmpName = HoistChainedPropertyAssignment(assign.Right, context);
                var receiver = facade.Transform(ma.Expression, context);
                string setter = "set" + char.ToUpperInvariant(maProp.Name[0]) + maProp.Name[1..];
                context.AddPreStatement($"{receiver}.{setter}({tmpName})");
                return tmpName;
            }

            // Bare identifier property LHS: Prop = rhs (inside instance method)
            if (assign.Left is IdentifierNameSyntax id
                && context.SemanticModel?.GetSymbolInfo(id).Symbol is IPropertySymbol idProp)
            {
                var tmpName = HoistChainedPropertyAssignment(assign.Right, context);
                string setter = "set" + char.ToUpperInvariant(idProp.Name[0]) + idProp.Name[1..];
                context.AddPreStatement($"{setter}({tmpName})");
                return tmpName;
            }
        }

        // Base case: any other expression (getter call, literal, local variable, non-property assignment)
        // — transform it and capture to a temp so callers can reference the value without re-evaluating.
        var tmpVal = facade.Transform(expr, context);
        // Optimization: null literal doesn't need a temp variable — use it inline.
        if (tmpVal == "null")
            return "null";
        var tmp = context.GenerateSyntheticName("_chainVal");
        context.AddPreStatement($"var {tmp} = {tmpVal}");
        return tmp;
    }

    /// <summary>
    /// Expands a compound assignment whose operator is user-defined into an explicit assignment
    /// that calls the static Java operator method, respecting property getter/setter conventions.
    /// e.g. LeftTop += new Point2()  →  setLeftTop(Point2.add(getLeftTop(), new Point2()))
    /// e.g. this.pos += delta        →  this.pos = Point2.add(this.pos, delta)
    /// </summary>
    private string ExpandCompoundOperatorOverload(
        AssignmentExpressionSyntax node,
        IMethodSymbol opMethod,
        ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var leftNode = node.Left;
        var right = facade.Transform(node.Right, context);

        // Resolve Java method name, qualified with the containing class if necessary.
        string javaMethod = CSharpToJava.Core.Transformers.Member.OperatorTransformer.OpSymbolToJavaName
            .TryGetValue(opMethod.Name, out var m) ? m : opMethod.Name;
        string qualifiedMethod = BuildQualifiedOperatorCall(opMethod, javaMethod, context);

        // Case 1: Bare identifier (e.g. LeftTop += rhs; inside instance method/property accessor)
        if (leftNode is IdentifierNameSyntax)
        {
            var lhsSymbol = context.SemanticModel?.GetSymbolInfo(leftNode).Symbol;
            if (lhsSymbol is IPropertySymbol bareProp)
            {
                string getter = "get" + char.ToUpperInvariant(bareProp.Name[0]) + bareProp.Name[1..];
                string setter = "set" + char.ToUpperInvariant(bareProp.Name[0]) + bareProp.Name[1..];
                return $"{setter}({qualifiedMethod}({getter}(), {right}))";
            }
            // Field or local variable — simple expand
            var lhsStr = facade.Transform(leftNode, context);
            return $"{lhsStr} = {qualifiedMethod}({lhsStr}, {right})";
        }

        // Case 2: Member access (e.g. obj.LeftTop += rhs  or  this.field += rhs)
        if (leftNode is MemberAccessExpressionSyntax ma)
        {
            var memberSymbol = context.SemanticModel?.GetSymbolInfo(leftNode).Symbol;
            var receiverExpr = ma.Expression;
            string memberName = ConversionContext.EscapeJavaKeyword(ma.Name.Identifier.Text);

            if (memberSymbol is IPropertySymbol memberProp)
            {
                string getter = "get" + char.ToUpperInvariant(memberProp.Name[0]) + memberProp.Name[1..];
                string setter = "set" + char.ToUpperInvariant(memberProp.Name[0]) + memberProp.Name[1..];

                // Simple receivers (identifier, this, this.Member) are safe to evaluate twice
                bool simpleReceiver = receiverExpr is IdentifierNameSyntax
                    || receiverExpr is ThisExpressionSyntax
                    || (receiverExpr is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax });
                if (simpleReceiver)
                {
                    var recv = facade.Transform(receiverExpr, context);
                    return $"{recv}.{setter}({qualifiedMethod}({recv}.{getter}(), {right}))";
                }
                // Complex receiver — extract to temp variable to avoid double evaluation
                var tmpName = context.GenerateSyntheticName("__opTemp");
                var recvStr = facade.Transform(receiverExpr, context);
                context.AddPreStatement($"var {tmpName} = {recvStr}");
                return $"{tmpName}.{setter}({qualifiedMethod}({tmpName}.{getter}(), {right}))";
            }

            // Non-property member (field in member-access form)
            var receiverStr = facade.Transform(receiverExpr, context);
            var lhs = $"{receiverStr}.{memberName}";
            return $"{lhs} = {qualifiedMethod}({lhs}, {right})";
        }

        // Fallback: expand as-is (best effort for indexers etc.)
        var fallbackLhs = facade.Transform(leftNode, context);
        return $"{fallbackLhs} = {qualifiedMethod}({fallbackLhs}, {right})";
    }

    private static string BuildQualifiedOperatorCall(
        IMethodSymbol opMethod,
        string javaMethod,
        ConversionContext context)
    {
        var currentTypeName = context.CurrentType?.Name;
        var operatorTypeName = opMethod.ContainingType.Name;

        // If call site is inside the operator's own class, no qualifier needed
        if (currentTypeName != null && operatorTypeName == currentTypeName)
            return javaMethod;

        var containingType = context.MapType(opMethod.ContainingType);
        // Strip generic type parameters — Java doesn't allow them on static call qualifiers
        var angleIdx = containingType.IndexOf('<');
        if (angleIdx > 0) containingType = containingType[..angleIdx];
        return $"{containingType}.{javaMethod}";
    }

    /// <summary>
    /// Fallback for compound assignments: when GetSymbolInfo can't resolve the user-defined operator,
    /// use operand type info to detect it and expand a /= b → a = Type.divide(a, b).
    /// </summary>
    /// <summary>
    /// If the given type has a user-defined operator for the operation, returns the
    /// qualified Java method name (e.g. "Point.add"). Otherwise returns null.
    /// </summary>
    private static string? TryResolveOperatorExpr(ITypeSymbol? type, string baseOp, ConversionContext context)
    {
        if (type is not INamedTypeSymbol named || named.TypeKind == TypeKind.Error)
            return null;
        if (BinaryExpressionTransformer.IsBuiltInTypeStatic(named))
            return null;

        var roslynOpName = BinaryExpressionTransformer.SyntaxKindToRoslynOperatorNameStatic(
            baseOp switch
            {
                "+" => Microsoft.CodeAnalysis.CSharp.SyntaxKind.AddExpression,
                "-" => Microsoft.CodeAnalysis.CSharp.SyntaxKind.SubtractExpression,
                "*" => Microsoft.CodeAnalysis.CSharp.SyntaxKind.MultiplyExpression,
                "/" => Microsoft.CodeAnalysis.CSharp.SyntaxKind.DivideExpression,
                "%" => Microsoft.CodeAnalysis.CSharp.SyntaxKind.ModuloExpression,
                _ => null
            });
        if (roslynOpName == null) return null;

        if (!named.GetMembers(roslynOpName).Any(m => m is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator }))
            return null;

        var javaMethod = CSharpToJava.Core.Transformers.Member.OperatorTransformer.OpSymbolToJavaName
            .TryGetValue(roslynOpName, out var n) ? n : roslynOpName;

        var containingType = context.MapType(named);
        var angleIdx = containingType.IndexOf('<');
        if (angleIdx > 0) containingType = containingType[..angleIdx];
        return $"{containingType}.{javaMethod}";
    }

    private string? ExpandCompoundByTypeInfo(AssignmentExpressionSyntax node, string op, ConversionContext context)
    {
        if (context.SemanticModel == null) return null;

        var leftType = context.SemanticModel.GetTypeInfo(node.Left).Type;
        INamedTypeSymbol? leftNamed = leftType as INamedTypeSymbol;
        // If GetTypeInfo fails, try to get the type from the member symbol (property or field)
        if ((leftNamed == null || leftNamed.TypeKind == TypeKind.Error)
            && node.Left is MemberAccessExpressionSyntax leftMa
            && context.SemanticModel?.GetSymbolInfo(leftMa).Symbol is {} leftSym)
        {
            leftNamed = leftSym switch
            {
                IPropertySymbol ps => ps.Type as INamedTypeSymbol,
                IFieldSymbol fs => fs.Type as INamedTypeSymbol,
                _ => null
            };
        }
        if (leftNamed == null || leftNamed.TypeKind == TypeKind.Error) return null;
        if (BinaryExpressionTransformer.IsBuiltInTypeStatic(leftNamed)) return null;
        if (leftNamed.TypeKind == TypeKind.Interface) return null;

        var baseOpStr = op[..^1]; // "+=" → "+", "-=" → "-", etc.
        var roslynOpName = BinaryExpressionTransformer.SyntaxKindToRoslynOperatorNameStatic(
            baseOpStr switch
            {
                "+" => Microsoft.CodeAnalysis.CSharp.SyntaxKind.AddExpression,
                "-" => Microsoft.CodeAnalysis.CSharp.SyntaxKind.SubtractExpression,
                "*" => Microsoft.CodeAnalysis.CSharp.SyntaxKind.MultiplyExpression,
                "/" => Microsoft.CodeAnalysis.CSharp.SyntaxKind.DivideExpression,
                "%" => Microsoft.CodeAnalysis.CSharp.SyntaxKind.ModuloExpression,
                "&" => Microsoft.CodeAnalysis.CSharp.SyntaxKind.BitwiseAndExpression,
                "|" => Microsoft.CodeAnalysis.CSharp.SyntaxKind.BitwiseOrExpression,
                "^" => Microsoft.CodeAnalysis.CSharp.SyntaxKind.ExclusiveOrExpression,
                _ => (Microsoft.CodeAnalysis.CSharp.SyntaxKind?)null
            });
        if (roslynOpName == null) return null;

        // Verify the type has the operator
        if (!leftNamed.GetMembers(roslynOpName).Any(m => m is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator }))
            return null;

        var javaMethodName = CSharpToJava.Core.Transformers.Member.OperatorTransformer.OpSymbolToJavaName
            .TryGetValue(roslynOpName, out var n) ? n : roslynOpName;

        var currentTypeName = context.CurrentType?.Name;
        var operatorTypeName = leftNamed.Name;

        string qualifiedMethod;
        if (currentTypeName != null && operatorTypeName == currentTypeName)
            qualifiedMethod = javaMethodName;
        else
        {
            var containingType = context.MapType(leftNamed);
            var angleIdx = containingType.IndexOf('<');
            if (angleIdx > 0) containingType = containingType[..angleIdx];
            qualifiedMethod = $"{containingType}.{javaMethodName}";
        }

        var facade = ExpressionTransformerFacade.Instance;
        var leftStr = facade.Transform(node.Left, context);
        var rightStr = facade.Transform(node.Right, context);

        return $"{leftStr} = {qualifiedMethod}({leftStr}, {rightStr})";
    }

    // Fix 3 + Fix 5: Handle ??= (CoalesceAssignment), avoiding double evaluation of complex LHS
    private string TransformCoalesceAssignment(AssignmentExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var right = facade.Transform(node.Right, context);

        if (node.Left is IdentifierNameSyntax)
        {
            // Simple identifier: a ??= b → inject pre-statement: if (a == null) a = b
            var leftStr = facade.Transform(node.Left, context);
            context.AddPreStatement($"if ({leftStr} == null) {leftStr} = {right}");
            // Return the identifier; statement transformer will filter it as a non-statement
            return leftStr;
        }

        if (node.Left is MemberAccessExpressionSyntax memberAccess)
        {
            // Fix 5: Extract receiver to a temp variable to avoid double evaluation
            // e.g. GetContainer().Value ??= x → var __tmp = GetContainer(); if (__tmp.Value == null) __tmp.Value = x;
            var tmpName = context.GenerateSyntheticName("__nullCoalTemp");
            var receiver = facade.Transform(memberAccess.Expression, context);
            var memberName = ConversionContext.EscapeJavaKeyword(memberAccess.Name.Identifier.Text);
            context.AddPreStatement($"var {tmpName} = {receiver}");
            context.AddPreStatement($"if ({tmpName}.{memberName} == null) {tmpName}.{memberName} = {right}");
            return $"{tmpName}.{memberName}";
        }

        // General complex case (best effort — evaluates LHS expression string once but may have side effects)
        var lhs = facade.Transform(node.Left, context);
        context.AddPreStatement($"if ({lhs} == null) {lhs} = {right}");
        return lhs;
    }

    /// <summary>
    /// Returns true if the given type is a C# collection interface (IEnumerable&lt;T&gt;, ICollection&lt;T&gt;, IList&lt;T&gt;, etc.)
    /// that maps to Java Collection/List interfaces.
    /// </summary>
    private static bool IsCollectionOrListInterface(ITypeSymbol? type)
    {
        if (type is INamedTypeSymbol named)
        {
            // Check both direct namespace and original definition namespace
            bool isCollectionInterface = named.Name is "IEnumerable" or "ICollection" or "IList"
                or "IReadOnlyCollection" or "IReadOnlyList";
            bool isSystemCollection = named.ContainingNamespace?.ToDisplayString().StartsWith("System.Collections.Generic") == true
                || named.OriginalDefinition?.ContainingNamespace?.ToDisplayString().StartsWith("System.Collections.Generic") == true;
            return isCollectionInterface && isSystemCollection;
        }
        return false;
    }

    /// <summary>
    /// Returns true if the type is an IEnumerable/ICollection/IList interface (from System.Collections.Generic).
    /// Matches the logic in ArgumentTransformer for consistency.
    /// </summary>
    private static bool IsEnumerableOrCollectionInterface(INamedTypeSymbol type)
    {
        if (type.Name is not "IEnumerable" and not "ICollection" and not "IList"
            and not "IReadOnlyCollection" and not "IReadOnlyList")
            return false;
        // Check namespace - should be from System.Collections.Generic
        var ns = type.ContainingNamespace?.ToDisplayString() ?? type.OriginalDefinition?.ContainingNamespace?.ToDisplayString();
        return ns != null && ns.StartsWith("System.Collections.Generic");
    }

    /// <summary>
    /// Heuristic check: does the transformed Java expression look like an unmaterialized Stream?
    /// Matches common Java Stream API method calls that appear when LINQ is converted.
    /// </summary>
    private static bool IsLikelyStreamExpression(string expr)
    {
        return expr.Contains(".filter(") || expr.Contains(".map(") || expr.Contains(".flatMap(")
            || expr.Contains(".sorted(") || expr.Contains(".distinct(") || expr.Contains(".limit(")
            || expr.Contains(".skip(") || expr.Contains(".peek(")
            || expr.Contains("StreamSupport.stream(") || expr.Contains("Arrays.stream(")
            || expr.Contains("Stream.concat(") || expr.Contains(".stream()");
    }

    /// <summary>
    /// Expands <c>this = expr</c> (valid in C# struct methods) to field-by-field copy,
    /// since Java doesn't allow assigning to <c>this</c>.
    /// Generates: <c>StructType _tmp = expr; this.field1 = _tmp.field1; this.field2 = _tmp.field2; ...</c>
    /// </summary>
    private string ExpandThisAssignment(JavaClassDeclaration structClass, ExpressionSyntax rhsNode, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var rhs = facade.Transform(rhsNode, context);

        var instanceFields = structClass.Fields
            .Where(f => (f.Modifiers & JavaModifiers.Static) == 0)
            .ToList();

        if (instanceFields.Count == 0)
            return $"/* this = {rhs}; — no instance fields to copy */";

        // Determine if the RHS is a simple expression that can be inlined without a temp variable,
        // or if it needs to be evaluated once into a temp.
        bool rhsIsSimple = rhsNode is IdentifierNameSyntax or ThisExpressionSyntax or MemberAccessExpressionSyntax;

        // For `this = default` or `this = new StructType()`, generate direct zero-initialization
        bool rhsIsDefault = rhsNode is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.DefaultLiteralExpression }
            || rhsNode is DefaultExpressionSyntax
            || rhsNode is ObjectCreationExpressionSyntax ocr && ocr.ArgumentList?.Arguments.Count == 0 && ocr.Initializer == null
            || rhsNode is ImplicitObjectCreationExpressionSyntax iocr && iocr.ArgumentList?.Arguments.Count == 0 && iocr.Initializer == null;

        if (rhsIsDefault)
        {
            // Build a set of struct-typed field names for proper default initialization.
            // In C#, struct-typed fields default to new StructType(), NOT null.
            var structFieldNames = GetStructFieldNames(context);

            // Just reset all fields to defaults
            var lines = new List<string>();
            foreach (var field in instanceFields)
            {
                if (structFieldNames.Contains(field.Name))
                    lines.Add($"this.{field.Name} = new {field.Type}()");
                else
                    lines.Add($"this.{field.Name} = {GetJavaFieldDefault(field.Type)}");
            }
            // Use pre-statements for all but the last line (the last is the "expression" return)
            for (int i = 0; i < lines.Count - 1; i++)
                context.AddPreStatement(lines[i] + ";");
            return lines[^1];
        }

        if (rhsIsSimple)
        {
            // Inline: this.f1 = src.f1; this.f2 = src.f2; ...
            var lines = new List<string>();
            foreach (var field in instanceFields)
                lines.Add($"this.{field.Name} = {rhs}.{field.Name}");
            for (int i = 0; i < lines.Count - 1; i++)
                context.AddPreStatement(lines[i] + ";");
            return lines[^1];
        }
        else
        {
            // Use temp variable: StructType _tmp = expr; this.f1 = _tmp.f1; ...
            var tmpName = $"_thisAssignTmp{Interlocked.Increment(ref _thisAssignCounter)}";
            context.AddPreStatement($"{structClass.Name} {tmpName} = {rhs};");
            var lines = new List<string>();
            foreach (var field in instanceFields)
                lines.Add($"this.{field.Name} = {tmpName}.{field.Name}");
            for (int i = 0; i < lines.Count - 1; i++)
                context.AddPreStatement(lines[i] + ";");
            return lines[^1];
        }
    }

    private static string GetJavaFieldDefault(string javaType)
    {
        string t = javaType.Trim();
        return t switch
        {
            "boolean" => "false",
            "byte" => "(byte)0",
            "short" => "(short)0",
            "int" => "0",
            "long" => "0L",
            "float" => "0.0f",
            "double" => "0.0d",
            "char" => "'\\0'",
            _ => "null"
        };
    }

    /// <summary>
    /// Gets the set of field names in the enclosing struct type whose C# type is a user-defined struct.
    /// Used by ExpandThisAssignment to determine which fields need new StructType() instead of null
    /// when doing <c>this = default</c>.
    /// </summary>
    private static HashSet<string> GetStructFieldNames(ConversionContext context)
    {
        var result = new HashSet<string>();
        if (context.SemanticModel == null || context.CurrentMethod == null)
            return result;

        var containingType = context.CurrentMethod.ContainingType;
        if (containingType == null || containingType.TypeKind != TypeKind.Struct)
            return result;

        foreach (var member in containingType.GetMembers())
        {
            if (member is IFieldSymbol field
                && !field.IsStatic
                && StructCloneHelper.IsUserDefinedStruct(field.Type))
            {
                result.Add(ConversionContext.EscapeJavaKeyword(field.Name));
            }
        }

        return result;
    }
}
