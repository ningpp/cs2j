using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Expression.Utilities;
using System.Text;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles type-related expressions (cast, is, as, typeof, default, checked, unchecked).
/// </summary>
[TransformerRegistration]
public class TypeOperationTransformer : IIRExpressionTransformer
{
    static TypeOperationTransformer()
    {
        ExpressionTransformerRegistry.Register(new[]
        {
            SyntaxKind.CastExpression,
            SyntaxKind.IsExpression,
            SyntaxKind.IsPatternExpression,
            SyntaxKind.AsExpression,
            SyntaxKind.TypeOfExpression,
            SyntaxKind.DefaultExpression,
            SyntaxKind.DefaultLiteralExpression,
            SyntaxKind.CheckedExpression,
            SyntaxKind.UncheckedExpression,
            SyntaxKind.SizeOfExpression
        }, new TypeOperationTransformer());
    }

    private static readonly Lazy<TypeOperationTransformer> _instance = new(() => new());
    public static TypeOperationTransformer Instance => _instance.Value;

    public string Transform(ExpressionSyntax node, ConversionContext context)
        => node.Kind() switch
        {
            SyntaxKind.CastExpression => TransformCast((CastExpressionSyntax)node, context),
            SyntaxKind.IsExpression => TransformIs((BinaryExpressionSyntax)node, context),
            SyntaxKind.IsPatternExpression => TransformIsPattern((IsPatternExpressionSyntax)node, context),
            SyntaxKind.AsExpression => TransformAs((BinaryExpressionSyntax)node, context),
            SyntaxKind.TypeOfExpression => TransformTypeOf((TypeOfExpressionSyntax)node, context),
            SyntaxKind.DefaultExpression => TransformDefault((DefaultExpressionSyntax)node, context),
            SyntaxKind.DefaultLiteralExpression => TransformDefaultLiteral(node, context),
            SyntaxKind.CheckedExpression => TransformChecked((CheckedExpressionSyntax)node, context),
            SyntaxKind.UncheckedExpression => TransformUnchecked((CheckedExpressionSyntax)node, context),
            SyntaxKind.SizeOfExpression => TransformSizeOf((SizeOfExpressionSyntax)node, context),
            _ => throw new NotSupportedException($"Type operation kind {node.Kind()} not supported.")
        };

    /// <inheritdoc />
    public JavaExpression TransformToIR(ExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;

        // Simple cast → structured JavaCastExpression
        if (node is CastExpressionSyntax castExpr)
        {
            var code = Transform(node, context);
            // If the string-based result is a bitmask expression (e.g. ((expr) & 0xFFFF)),
            // return it as raw code — it is NOT a Java cast expression.
            if (code.Contains("& 0xFF") || code.Contains("& 0xFFFF") || code.Contains("& 0xFFFFFFFFL"))
            {
                return new JavaRawExpression(code);
            }
            // If the string-based result looks like a cast, produce structured IR
            if (code.StartsWith("(") && code.Contains(")"))
            {
                var inner = facade.TransformToIR(castExpr.Expression, context);
                var typeInfo = context.GetTypeInfo(castExpr.Type);
                var targetSymbol = typeInfo.Type;
                string targetType = targetSymbol != null ? context.MapType(targetSymbol) : castExpr.Type.ToString();
                if (!string.IsNullOrWhiteSpace(targetType))
                {
                    return new JavaCastExpression { Type = targetType, Expression = inner };
                }
            }
            return new JavaRawExpression(code);
        }

        // is Type → JavaInstanceOfExpression
        if (node is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.IsExpression } isExpr)
        {
            var exprIR = facade.TransformToIR(isExpr.Left, context);
            var typeInfo = context.GetTypeInfo(isExpr.Right);
            string targetType;
            if (typeInfo.Type != null)
                targetType = context.MapType(typeInfo.Type);
            else
                targetType = context.MapTypeFromSyntax(isExpr.Right as TypeSyntax ?? throw new ArgumentException("Expected type"));
            return new JavaInstanceOfExpression
            {
                Expression = exprIR,
                Type = ToRuntimeTypeForInstanceOf(targetType)
            };
        }

        // is Pattern (declaration pattern) → JavaInstanceOfExpression with PatternVariable
        if (node is IsPatternExpressionSyntax isPatternExpr
            && isPatternExpr.Pattern is DeclarationPatternSyntax declPattern
            && (int)context.Options.TargetJavaVersion >= 16)
        {
            var exprIR = facade.TransformToIR(isPatternExpr.Expression, context);
            var typeInfo = context.GetTypeInfo(declPattern.Type);
            string targetType;
            if (typeInfo.Type != null)
                targetType = context.MapType(typeInfo.Type);
            else
                targetType = context.MapTypeFromSyntax(declPattern.Type);
            var varName = ConversionContext.EscapeJavaKeyword(declPattern.Designation.ToString());
            return new JavaInstanceOfExpression
            {
                Expression = exprIR,
                Type = ToRuntimeTypeForInstanceOf(targetType),
                PatternVariable = varName
            };
        }

        // as Type → (expr instanceof Type ? (Type)expr : null)
        if (node is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AsExpression } asExpr)
        {
            // Delegate to Transform because of pre-statement hoisting for side-effectful expressions
            var code = Transform(node, context);
            return new JavaRawExpression(code);
        }

        // typeof(T) → T.class as JavaMemberAccessExpression
        if (node is TypeOfExpressionSyntax typeOfExpr)
        {
            var typeInfo = context.GetTypeInfo(typeOfExpr.Type);
            string typeName;
            if (typeInfo.Type is INamedTypeSymbol namedType
                && IsUnboundGenericType(namedType))
            {
                // Unbound generics such as typeof(Nullable<>) or typeof(ArraySegment<>)
                // are represented as constructed types whose type arguments are the
                // definition's own type parameters (or error types in some semantic
                // models). They must map to the configured Java generic definition
                // (e.g. Optional.class or ArraySegment.class), not to the unwrapped
                // type parameter.
                var ns = namedType.ContainingNamespace?.ToDisplayString() ?? "";
                var configKey = (string.IsNullOrEmpty(ns) ? "" : ns + ".") + namedType.Name + "`" + namedType.TypeArguments.Length;
                typeName = context.TypeMappings.MapType(configKey);
                if (typeName != configKey)
                {
                    context.AddImportsForTypePublic(configKey);
                }
                else
                {
                    typeName = context.MapType(typeInfo.Type);
                }
            }
            else if (typeInfo.Type != null)
                typeName = context.MapType(typeInfo.Type);
            else
                typeName = context.MapTypeFromSyntax(typeOfExpr.Type);
            if (typeInfo.Type is ITypeParameterSymbol)
                return new JavaRawExpression(Transform(node, context));
            return new JavaMemberAccessExpression
            {
                Target = new JavaIdentifierExpression { Name = ToRuntimeTypeForClassLiteral(typeName) },
                MemberName = "class"
            };
        }

        // default(T) / default literal → JavaLiteralExpression
        if (node.IsKind(SyntaxKind.DefaultExpression) || node.IsKind(SyntaxKind.DefaultLiteralExpression))
        {
            var code = Transform(node, context);
            return new JavaLiteralExpression { Value = code };
        }

        // checked/unchecked/sizeof/complex patterns → raw fallback
        return new JavaRawExpression(Transform(node, context));
    }

    private static bool IsUnboundGenericType(INamedTypeSymbol namedType)
    {
        if (!namedType.IsGenericType || namedType.TypeArguments.Length == 0)
            return false;

        // Roslyn exposes this directly for open generic type-of expressions.
        if (namedType.IsUnboundGenericType)
            return true;

        // Fallback: every type argument is either a type parameter declared by
        // this type, or an error type produced when the semantic model cannot
        // resolve the unbound generic parameter.
        return namedType.TypeArguments.All(a =>
            a is IErrorTypeSymbol
            || (a is ITypeParameterSymbol tp
                && tp.DeclaringType != null
                && SymbolEqualityComparer.Default.Equals(tp.DeclaringType, namedType)));
    }

    private string TransformCast(CastExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var expression = facade.Transform(node.Expression, context);

        // Get the target type
        var typeInfo = context.GetTypeInfo(node.Type);
        var targetSymbol = typeInfo.Type;
        string targetType;
        if (targetSymbol != null && targetSymbol is not IErrorTypeSymbol)
        {
            targetType = context.MapType(targetSymbol);
        }
        else
        {
            // Prefer syntax-based mapping for unresolved/error types — they can lose
            // generic type arguments (e.g. Tuple<int,int> → Tuple → Map.Entry instead
            // of Map.Entry<Integer,Integer>).
            targetType = context.MapTypeFromSyntax(node.Type);
        }

        if (targetSymbol?.SpecialType == SpecialType.System_Decimal)
        {
            context.AddImport("io.github.ningpp.compat.Decimal");
            var sourceType = context.GetTypeInfo(node.Expression).Type;
            return ExpressionTransformerHelpers.ToDecimalExpression(node.Expression, expression, sourceType);
        }

        if (context.SemanticModel != null
            && context.GetTypeInfo(node.Expression).Type?.SpecialType == SpecialType.System_Decimal)
        {
            return targetSymbol?.SpecialType switch
            {
                SpecialType.System_SByte
                    or SpecialType.System_Byte
                    or SpecialType.System_Int16
                    or SpecialType.System_UInt16
                    or SpecialType.System_Int32
                    or SpecialType.System_UInt32
                    or SpecialType.System_Char => $"{expression}.intValue()",
                SpecialType.System_Int64 or SpecialType.System_UInt64 => $"{expression}.longValue()",
                SpecialType.System_Single => $"{expression}.floatValue()",
                SpecialType.System_Double => $"{expression}.doubleValue()",
                _ => $"({targetType})({expression})"
            };
        }

        // C# pointer-to-pointer cast (e.g., (char*)(lptr + 1), (int*)(ptr + offset))
        // In Java, all pointers are MemorySegment, so pointer casts are no-ops.
        if (targetSymbol is IPointerTypeSymbol)
        {
            return expression;
        }

        // C# enum -> enum cast: convert through the underlying value.
        // Java enum casts only work within an inheritance hierarchy, which enums do not have.
        if (targetSymbol?.TypeKind == TypeKind.Enum
            && context.SemanticModel != null
            && targetType is not ("int" or "long" or "short" or "byte" or "double" or "float"))
        {
            var sourceType = context.GetTypeInfo(node.Expression).Type;
            if (sourceType?.TypeKind == TypeKind.Enum)
            {
                var sourceValue = GetEnumUnderlyingValueExpression(sourceType, expression, context);
                bool targetIsExplicitValue = IsExplicitValueEnum(targetSymbol, context);
                if (!targetIsExplicitValue && targetSymbol is INamedTypeSymbol namedTargetEnum)
                {
                    targetIsExplicitValue = namedTargetEnum.GetMembers()
                        .OfType<IFieldSymbol>()
                        .Any(f => f.IsConst && f.HasConstantValue && f.Name != "_UNMAPPED");
                }

                if (targetIsExplicitValue)
                {
                    var valueType = IsExplicitValueEnum(targetSymbol, context)
                        ? GetExplicitValueEnumValueType(targetSymbol, context)
                        : (targetSymbol is INamedTypeSymbol ne && ne.EnumUnderlyingType?.SpecialType
                            is SpecialType.System_Int64 or SpecialType.System_UInt64 ? "long" : "int");
                    return $"{targetType}.fromValue(({valueType})({sourceValue}))";
                }

                return $"{targetType}.values()[(int)({sourceValue})]";
            }
        }

        // C# numeric -> enum cast: (MyEnum)i
        // Java cannot cast int to enum directly; map by ordinal index instead.
        // For enums with explicit values, use fromValue() instead of values()[] to avoid AIOOBE.
        if (targetSymbol?.TypeKind == TypeKind.Enum
            && context.SemanticModel != null
            && targetType is not ("int" or "long" or "short" or "byte" or "double" or "float"))
        {
            var sourceType = context.GetTypeInfo(node.Expression).Type;
            if (sourceType?.SpecialType == SpecialType.System_Object)
            {
                return $"({targetType})({expression})";
            }

            if (sourceType?.TypeKind != TypeKind.Enum)
            {
                bool isExplicitValue = IsExplicitValueEnum(targetSymbol, context);
                // Fallback: check the enum symbol directly if not yet registered
                if (!isExplicitValue && targetSymbol is INamedTypeSymbol namedEnum)
                {
                    isExplicitValue = namedEnum.GetMembers()
                        .OfType<IFieldSymbol>()
                        .Any(f => f.IsConst && f.HasConstantValue && f.Name != "_UNMAPPED");
                }
                if (isExplicitValue)
                {
                    var valueType = IsExplicitValueEnum(targetSymbol, context)
                        ? GetExplicitValueEnumValueType(targetSymbol, context)
                        : (targetSymbol is INamedTypeSymbol ne && ne.EnumUnderlyingType?.SpecialType
                            is SpecialType.System_Int64 or SpecialType.System_UInt64 ? "long" : "int");
                    return $"{targetType}.fromValue(({valueType})({expression}))";
                }

                return $"{targetType}.fromValue((int)({expression}))";
            }
        }

        // C# enum -> numeric cast: (int)myEnum
        // Java enums cannot be cast to numeric primitives; use ordinal() and widen/narrow as needed.
        // For enums with explicit values, use getValue() instead of ordinal().
        if (context.SemanticModel != null)
        {
            var sourceType = context.GetTypeInfo(node.Expression).Type;
            if (sourceType?.TypeKind == TypeKind.Enum && IsJavaNumericType(targetType))
            {
                var mappedSourceType = context.MapType(sourceType);
                if (mappedSourceType is "int" or "long" or "short" or "byte"
                    or "Integer" or "Long" or "Short" or "Byte")
                {
                    var baseResult = targetType == "int"
                        ? expression
                        : $"({targetType})({expression})";
                    // When casting enum to unsigned types (ushort/byte/uint), apply
                    // bitmask to preserve unsigned semantics, consistent with the
                    // explicit (ushort)/(byte)/(uint) cast handling below.
                    if (targetSymbol?.SpecialType == SpecialType.System_UInt16)
                        return $"(((int)({expression})) & 0xFFFF)";
                    if (targetSymbol?.SpecialType == SpecialType.System_Byte)
                        return $"{expression} & 0xFF";
                    if (targetSymbol?.SpecialType == SpecialType.System_UInt32)
                        return $"(int)(({expression}) & 0xFFFFFFFFL)";
                    return baseResult;
                }

                bool sourceIsExplicitValue = IsExplicitValueEnum(sourceType, context);
                // Fallback: check the enum symbol directly if not yet registered
                if (!sourceIsExplicitValue && sourceType is INamedTypeSymbol namedSourceEnum)
                {
                    sourceIsExplicitValue = namedSourceEnum.GetMembers()
                        .OfType<IFieldSymbol>()
                        .Any(f => f.IsConst && f.HasConstantValue && f.Name != "_UNMAPPED");
                }
                if (sourceIsExplicitValue)
                {
                    var sourceValueType = IsExplicitValueEnum(sourceType, context)
                        ? GetExplicitValueEnumValueType(sourceType, context)
                        : (sourceType is INamedTypeSymbol nse && nse.EnumUnderlyingType?.SpecialType
                            is SpecialType.System_Int64 or SpecialType.System_UInt64 ? "long" : "int");
                    return targetType == sourceValueType
                        ? $"{expression}.getValue()"
                        : $"({targetType})({expression}.getValue())";
                }

                return targetType == "int"
                    ? $"{expression}.ordinal()"
                    : $"({targetType})({expression}.ordinal())";
            }
        }

        // Java cast syntax: (Type)expression
        // For primitives to wrapper types, use valueOf
        if (IsPrimitiveToWrapperCast(node.Expression, targetType, context))
        {
            return $"{targetNameOf(targetType)}({expression})";
        }

        if (context.SemanticModel != null
            && targetSymbol is IArrayTypeSymbol arrayCastTarget
            && IsSystemArrayReferenceType(context.GetTypeInfo(node.Expression).Type))
        {
            var elementType = arrayCastTarget.ElementType;
            var elementTypeName = context.MapType(elementType);
            var elementClassLiteral = $"{ToRuntimeTypeForClassLiteral(elementTypeName)}.class";
            context.AddImport("io.github.ningpp.compat.CSharpArray");

            // C# (T[])receiver.ToArray(typeof(T)) where receiver.ToArray returns System.Array.
            // In Java the helper returns CSharpArray, so a second .toArray(Class) converts it to T[].
            // For System.Collections.ArrayList the helper is CSharpArrayList.toArray(Class<T>),
            // which already returns T[]; the outer cast is redundant.
            if (node.Expression is InvocationExpressionSyntax invocation
                && invocation.Expression is MemberAccessExpressionSyntax memberAccess
                && memberAccess.Name.Identifier.Text == "ToArray"
                && invocation.ArgumentList.Arguments.Count == 1
                && invocation.ArgumentList.Arguments[0].Expression is TypeOfExpressionSyntax)
            {
                var receiverType = context.SemanticModel.GetTypeInfo(memberAccess.Expression).Type;
                bool isArrayList = receiverType?.ToDisplayString() == "System.Collections.ArrayList";
                if (isArrayList)
                {
                    return expression;
                }

                return $"{expression}.toArray({elementClassLiteral})";
            }

            // For other method invocations returning System.Array (e.g. EnsureArrayIndex),
            // the Java helper returns CSharpArray. Use toArray(componentType.class) to
            // produce a T[] that the surrounding code can consume.
            if (node.Expression is InvocationExpressionSyntax)
            {
                return $"{expression}.toArray({elementClassLiteral})";
            }

            return $"{expression}.as({ToRuntimeTypeForClassLiteral(targetType)}.class)";
        }

        // C# arrays can be cast to IEnumerable/ICollection/IList, but Java arrays are not Collection subtypes.
        // Adapt arrays to collection views so constructor chaining like this((IEnumerable<T>)arr) compiles.
        if (context.SemanticModel != null
            && context.GetTypeInfo(node.Expression).Type is IArrayTypeSymbol sourceArray
            && IsIterableLikeJavaType(targetType))
        {
            return WrapArrayAsIterable(expression, sourceArray, context);
        }

        if (context.SemanticModel != null
            && IsIterableLikeJavaType(targetType)
            && IsObjectLikeEnumerableCastSource(node.Expression, context))
        {
            context.AddImport("io.github.ningpp.compat.ReflectionHelper");
            return $"ReflectionHelper.asIterable({expression})";
        }

        // C# cast from collection interface/class to array, e.g. (T[])listLike.
        // Java does not allow casting List<T> to T[]; use toArray(new T[0]).
        if (context.SemanticModel != null
            && targetSymbol is IArrayTypeSymbol targetArrayType
            && targetArrayType.ElementType.SpecialType == SpecialType.None)
        {
            var sourceType = context.GetTypeInfo(node.Expression).Type as INamedTypeSymbol;
            if (sourceType != null && sourceType.SpecialType != SpecialType.System_Array)
            {
                bool isEnumerableLike = sourceType.AllInterfaces.Any(i =>
                    i.OriginalDefinition?.ToDisplayString() is
                        "System.Collections.Generic.IEnumerable<T>" or
                        "System.Collections.IEnumerable" or
                        "System.Collections.Generic.ICollection<T>" or
                        "System.Collections.ICollection" or
                        "System.Collections.Generic.IList<T>");

                bool isCollectionLike = sourceType.AllInterfaces.Any(i =>
                    i.OriginalDefinition?.ToDisplayString() is
                        "System.Collections.Generic.ICollection<T>" or
                        "System.Collections.ICollection" or
                        "System.Collections.Generic.IList<T>");

                if (isEnumerableLike)
                {
                    var arrayCreation = ExpressionTransformerHelpers.BuildJavaArrayCreationForElement(
                        targetArrayType.ElementType,
                        context,
                        "0");
                    if (isCollectionLike)
                    {
                        return $"{expression}.toArray({arrayCreation})";
                    }

                    var streamArrayCreation = ExpressionTransformerHelpers.BuildJavaArrayCreationForElement(
                        targetArrayType.ElementType,
                        context,
                        "size");
                    context.AddImport("java.util.stream.StreamSupport");
                    return $"StreamSupport.stream({expression}.spliterator(), false).toArray(size -> {streamArrayCreation})";
                }
            }
        }

        // C# (byte)expr → & 0xFF (byte maps to Java int, cast becomes masking)
        if (targetSymbol?.SpecialType == SpecialType.System_Byte)
        {
            var sourceType = context.GetTypeInfo(node.Expression).Type;
            // When casting from object, Java cannot use Object as a bitwise operand.
            // Unbox to int first; the runtime type check (e.g. value instanceof Byte)
            // in the surrounding C# code guarantees the cast is safe.
            if (sourceType?.SpecialType == SpecialType.System_Object)
                return $"(((Number)({expression})).intValue() & 0xFF)";
            return $"({expression} & 0xFF)";
        }

        // C# (ushort)expr → ((int)(expr) & 0xFFFF) (ushort maps to Java short, but & 0xFFFF
        // produces int which is the correct type for assignments and comparisons)
        // This is consistent with (byte) → & 0xFF and avoids (short) cast which
        // produces a short that can't be assigned to int variables.
        // The (int) prefix is needed when expr is long (e.g. _flags & IndexMask where
        // IndexMask is long), because long & 0xFFFF produces long, not int.
        // Parentheses are required around the & expression because & has lower precedence
        // than <, ==, etc. in Java, so without them "x & 0xFFFF < 10" would be parsed as
        // "x & (0xFFFF < 10)" which is a type error (int & boolean).
        if (targetSymbol?.SpecialType == SpecialType.System_UInt16)
        {
            var sourceType = context.GetTypeInfo(node.Expression).Type;
            // When casting from object, Java cannot cast Object directly to int.
            // Unbox to Integer first; the runtime instanceof check (e.g. value instanceof Integer)
            // guarantees the cast is safe.
            if (sourceType?.SpecialType == SpecialType.System_Object)
                return $"(((int)(((Integer)({expression})) & 0xFFFF)))";
            return $"(((int)({expression})) & 0xFFFF)";
        }

        // C# (uint)expr → (int)((expr) & 0xFFFFFFFFL) (uint maps to Java int, cast becomes masking)
        // This preserves unsigned semantics: (uint)(x - '0') <= 9 works correctly
        // because negative values wrap to large positive values via the mask.
        // Parentheses are required around the & expression because & has lower precedence
        // than <= in Java, so without them "x & 0xFFFFFFFFL <= 9" would be parsed as
        // "x & (0xFFFFFFFFL <= 9)" which is a type error (int & boolean).
        // The (int) cast is needed because 0xFFFFFFFFL is a long literal, making the
        // entire & expression a long; assigning a long to an int is a lossy conversion
        // in Java. The mask ensures only the lower 32 bits are set, so the (int) cast
        // produces the correct unsigned-to-signed mapping.
        if (targetSymbol?.SpecialType == SpecialType.System_UInt32)
        {
            var sourceType = context.GetTypeInfo(node.Expression).Type;
            // When casting from object, Java cannot use Object as a bitwise operand.
            // Unbox to Integer first; the runtime instanceof check (e.g. value instanceof Integer)
            // guarantees the cast is safe.
            if (sourceType?.SpecialType == SpecialType.System_Object)
                return $"(int)(((Integer)({expression})) & 0xFFFFFFFFL)";
            return $"(int)(({expression}) & 0xFFFFFFFFL)";
        }

        // User-defined conversion operators (implicit/explicit operator)
        // e.g. (string)qilLiteral where QilLiteral defines "implicit operator string"
        //      → QilLiteral.toSring(qilLiteral)
        // e.g. (Temperature)42.0 where Temperature defines "explicit operator Temperature(double)"
        //      → Temperature.toTemperature(42.0)
        if (context.SemanticModel != null && targetSymbol != null)
        {
            var conversion = context.SemanticModel.ClassifyConversion(
                node.Expression, targetSymbol);
            if (conversion.IsUserDefined && conversion.MethodSymbol != null)
            {
                var method = conversion.MethodSymbol;
                var javaContainingType = context.MapType(method.ContainingType);
                var javaTargetType = context.MapType(method.ReturnType);
                var javaMethodName = "to" + char.ToUpper(javaTargetType[0]) + javaTargetType[1..];
                return $"{javaContainingType}.{javaMethodName}({expression})";
            }
        }

        // Array → CSharpGenericIterable/Collection cast: Java arrays don't implement
        // Iterable<T>, so (CSharpGenericIterable<T>)(array) always fails.
        // Use ArrayHelper.toList() for mutable CSharpList, or CSharpGenericIterable.fromCollection()
        // for a view. C# casts like (IEnumerable<T>)array are valid because arrays
        // implement IEnumerable<T> in C#, but not in Java.
        if (context.SemanticModel != null)
        {
            var sourceType = context.GetTypeInfo(node.Expression).Type;
            if (sourceType is IArrayTypeSymbol)
            {
                if (targetType.StartsWith("CSharpGenericIterable<")
                    || targetType.StartsWith("CSharpICollection<")
                    || targetType.StartsWith("CSharpGenericIList<")
                    || targetType.StartsWith("CSharpList<"))
                {
                    context.AddImport("io.github.ningpp.compat.ArrayHelper");
                    return $"ArrayHelper.toList({expression})";
                }
                if (targetType.StartsWith("Iterable<") || targetType.StartsWith("Collection<")
                    || targetType.StartsWith("List<"))
                {
                    context.AddImport("io.github.ningpp.compat.ArrayHelper");
                    return $"ArrayHelper.toList({expression})";
                }
            }
        }

        // C# cast from IEnumerable<T>/ICollection<T> to List<T>:
        // In C# this is valid when the runtime object is a List<T>, but in Java
        // CSharpGenericIterable is not a CSharpList, so a direct cast fails.
        // Convert by constructing a new CSharpList from the iterable.
        if (context.SemanticModel != null
            && (targetType.StartsWith("CSharpList<") || targetType.StartsWith("CSharpGenericIList<")))
        {
            var sourceType = context.GetTypeInfo(node.Expression).Type;
            bool isEnumerableLikeSource = IsEnumerableLikeSourceType(sourceType);
            if (isEnumerableLikeSource)
            {
                context.AddImport("io.github.ningpp.compat.CSharpList");
                return $"new CSharpList<>({expression})";
            }
        }

        return $"({targetType})({expression})";
    }

    private static bool IsJavaNumericType(string javaType)
        => javaType is "int" or "long" or "short" or "byte" or "double" or "float" or "char"
            or "Integer" or "Long" or "Short" or "Byte" or "Double" or "Float" or "Character";

    private static bool IsSystemArrayReferenceType(ITypeSymbol? type)
        => type is not IArrayTypeSymbol
            && type?.TypeKind != TypeKind.Array
            && (type?.SpecialType == SpecialType.System_Array
                || type?.ToDisplayString() == "System.Array");

    private static bool IsExplicitValueEnum(ITypeSymbol? typeSymbol, ConversionContext context)
    {
        if (typeSymbol is not INamedTypeSymbol named || named.TypeKind != TypeKind.Enum)
            return false;
        return context.IsExplicitValueEnum(named.Name)
            || context.IsExplicitValueEnum(named.ToDisplayString());
    }

    private static string GetExplicitValueEnumValueType(ITypeSymbol typeSymbol, ConversionContext context)
    {
        if (typeSymbol is not INamedTypeSymbol named || named.TypeKind != TypeKind.Enum)
            return "int";

        if (context.IsExplicitValueEnum(named.Name))
            return context.GetExplicitValueEnumValueType(named.Name);
        if (context.IsExplicitValueEnum(named.ToDisplayString()))
            return context.GetExplicitValueEnumValueType(named.ToDisplayString());

        return named.EnumUnderlyingType?.SpecialType is SpecialType.System_Int64 or SpecialType.System_UInt64
            ? "long"
            : "int";
    }

    private static string GetEnumUnderlyingValueExpression(ITypeSymbol enumType, string expression, ConversionContext context)
    {
        bool isExplicitValue = IsExplicitValueEnum(enumType, context);
        if (!isExplicitValue && enumType is INamedTypeSymbol namedEnum)
        {
            isExplicitValue = namedEnum.GetMembers()
                .OfType<IFieldSymbol>()
                .Any(f => f.IsConst && f.HasConstantValue && f.Name != "_UNMAPPED");
        }

        return isExplicitValue
            ? $"{expression}.getValue()"
            : $"{expression}.ordinal()";
    }

    private static bool IsIterableLikeJavaType(string mappedType)
    {
        return mappedType == "Iterable" || mappedType.StartsWith("Iterable<")
            || mappedType == "Collection" || mappedType.StartsWith("Collection<")
            || mappedType == "List" || mappedType.StartsWith("List<")
            || mappedType.StartsWith("CSharpGenericIterable")
            || mappedType.StartsWith("CSharpICollection")
            || mappedType.StartsWith("CSharpGenericIList")
            || mappedType.StartsWith("CSharpCollection")
            || mappedType.StartsWith("CSharpReadOnlyCollection")
            || mappedType.StartsWith("CSharpReadOnlyList");
    }

    private static bool IsEnumerableLikeSourceType(ITypeSymbol? sourceType)
    {
        if (sourceType is not INamedTypeSymbol named)
            return false;

        var defName = named.OriginalDefinition?.ToDisplayString();

        // Source is already List<T> — a direct cast is fine
        if (defName == "System.Collections.Generic.List<T>")
            return false;

        // Source is itself IEnumerable<T>, ICollection<T>, or IList<T>
        if (defName is
            "System.Collections.Generic.IEnumerable<T>"
            or "System.Collections.Generic.ICollection<T>"
            or "System.Collections.Generic.IList<T>"
            or "System.Collections.IEnumerable"
            or "System.Collections.ICollection"
            or "System.Collections.IList")
        {
            return true;
        }

        // Source implements IEnumerable<T> (class or struct that implements the interface)
        return named.AllInterfaces.Any(i =>
            i.OriginalDefinition?.ToDisplayString() is
                "System.Collections.Generic.IEnumerable<T>" or
                "System.Collections.Generic.ICollection<T>" or
                "System.Collections.Generic.IList<T>");
    }

    private static string WrapArrayAsIterable(string expr, IArrayTypeSymbol arrayType, ConversionContext context)
    {
        return ExpressionTransformerHelpers.BuildArrayToCollectionExpression(expr, arrayType, context);
    }

    private static bool IsObjectLikeEnumerableCastSource(ExpressionSyntax expression, ConversionContext context)
    {
        var sourceType = context.GetTypeInfo(expression).Type;
        if (sourceType?.SpecialType != SpecialType.System_Object)
            return false;

        if (expression is InvocationExpressionSyntax invocation
            && invocation.Expression is MemberAccessExpressionSyntax memberAccess
            && memberAccess.Name.Identifier.Text == "Invoke")
        {
            var receiverType = context.GetTypeInfo(memberAccess.Expression).Type;
            if (receiverType?.ToDisplayString() == "System.Reflection.MethodInfo")
                return true;
        }

        return false;
    }

    private static bool IsPrimitiveSpecialType(SpecialType st)
        => st is SpecialType.System_Int32 or SpecialType.System_Int16 or SpecialType.System_Byte
            or SpecialType.System_Int64 or SpecialType.System_Double or SpecialType.System_Single
            or SpecialType.System_Boolean or SpecialType.System_Char;

    private string TransformIs(BinaryExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var left = facade.Transform(node.Left, context);

        // Get the type being checked
        var typeInfo = context.GetTypeInfo(node.Right);
        string targetType;
        if (typeInfo.Type != null)
        {
            targetType = context.MapType(typeInfo.Type);
        }
        else
        {
            targetType = context.MapTypeFromSyntax(node.Right as TypeSyntax ?? throw new ArgumentException("Expected type"));
        }

        // C#: obj is Type  → Java: obj instanceof Type
        return $"{left} instanceof {ToRuntimeTypeForInstanceOf(targetType)}";
    }

    private string TransformIsPattern(IsPatternExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var expression = facade.Transform(node.Expression, context);

        // Handle different pattern types
        var pattern = node.Pattern;
        return pattern switch
        {
            DeclarationPatternSyntax declPattern => TransformDeclarationPattern(expression, declPattern, context),
            ConstantPatternSyntax constPattern => TransformConstantPattern(expression, constPattern, context),
            RecursivePatternSyntax recPattern => TransformRecursivePattern(expression, recPattern, context),
            UnaryPatternSyntax unaryPattern when unaryPattern.OperatorToken.IsKind(SyntaxKind.NotKeyword)
                => $"!({TransformIsPatternInner(expression, unaryPattern.Pattern, context)})",
            BinaryPatternSyntax binPattern when binPattern.IsKind(SyntaxKind.AndPattern)
                => $"({TransformIsPatternInner(expression, binPattern.Left, context)} && {TransformIsPatternInner(expression, binPattern.Right, context)})",
            BinaryPatternSyntax binPattern when binPattern.IsKind(SyntaxKind.OrPattern)
                => $"({TransformIsPatternInner(expression, binPattern.Left, context)} || {TransformIsPatternInner(expression, binPattern.Right, context)})",
            RelationalPatternSyntax relPattern
                => $"{expression} {relPattern.OperatorToken.Text} {facade.Transform(relPattern.Expression, context)}",
            TypePatternSyntax typePattern
                => $"{expression} instanceof {context.MapTypeFromSyntax(typePattern.Type)}",
            VarPatternSyntax varPattern
                => TransformVarPattern(expression, varPattern, context),
            ParenthesizedPatternSyntax parenPattern
                => TransformIsPatternInner(expression, parenPattern.Pattern, context),
            DiscardPatternSyntax => "true",
            _ => $"/* TODO: complex pattern {pattern.GetType().Name} */ {expression}"
        };
    }

    /// <summary>
    /// Recursive helper for pattern matching — delegates to the same pattern dispatch logic.
    /// Used by unary/binary/parenthesized patterns that nest other patterns.
    /// </summary>
    private string TransformIsPatternInner(string expression, PatternSyntax pattern, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        return pattern switch
        {
            DeclarationPatternSyntax declPattern => TransformDeclarationPattern(expression, declPattern, context),
            ConstantPatternSyntax constPattern => TransformConstantPattern(expression, constPattern, context),
            RecursivePatternSyntax recPattern => TransformRecursivePattern(expression, recPattern, context),
            UnaryPatternSyntax unaryPattern when unaryPattern.OperatorToken.IsKind(SyntaxKind.NotKeyword)
                => $"!({TransformIsPatternInner(expression, unaryPattern.Pattern, context)})",
            BinaryPatternSyntax binPattern when binPattern.IsKind(SyntaxKind.AndPattern)
                => $"({TransformIsPatternInner(expression, binPattern.Left, context)} && {TransformIsPatternInner(expression, binPattern.Right, context)})",
            BinaryPatternSyntax binPattern when binPattern.IsKind(SyntaxKind.OrPattern)
                => $"({TransformIsPatternInner(expression, binPattern.Left, context)} || {TransformIsPatternInner(expression, binPattern.Right, context)})",
            RelationalPatternSyntax relPattern
                => $"{expression} {relPattern.OperatorToken.Text} {facade.Transform(relPattern.Expression, context)}",
            TypePatternSyntax typePattern
                => $"{expression} instanceof {context.MapTypeFromSyntax(typePattern.Type)}",
            VarPatternSyntax varPattern
                => TransformVarPattern(expression, varPattern, context),
            ParenthesizedPatternSyntax parenPattern
                => TransformIsPatternInner(expression, parenPattern.Pattern, context),
            DiscardPatternSyntax => "true",
            _ => $"/* TODO: sub-pattern {pattern.GetType().Name} */ true"
        };
    }

    private string TransformVarPattern(string expression, VarPatternSyntax pattern, ConversionContext context)
    {
        // `obj is var x` always matches; assign x = obj and evaluate to true.
        // In Java, we can use inline assignment: (x = obj) != null || true
        // But for simplicity, since `is var x` always matches, we generate `true`
        // and rely on the enclosing context to handle the variable introduction.
        var designation = pattern.Designation.ToString();
        if (designation != "_")
        {
            // For named var patterns, the variable is effectively an alias
            return $"({ConversionContext.EscapeJavaKeyword(designation)} = {expression}) != null || true";
        }
        return "true";
    }

    private string TransformDeclarationPattern(string expression, DeclarationPatternSyntax pattern, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;

        // C#: obj is Type variable  → Java needs instanceof check then cast
        var typeInfo = context.GetTypeInfo(pattern.Type);
        string targetType;
        if (typeInfo.Type != null)
        {
            targetType = context.MapType(typeInfo.Type);
        }
        else
        {
            targetType = context.MapTypeFromSyntax(pattern.Type);
        }

        var variableName = ConversionContext.EscapeJavaKeyword(pattern.Designation.ToString());

        // In Java, we use: expression instanceof Type && ((Type)expression).property
        // Or for newer Java: expression instanceof Type variableName
        if ((int)context.Options.TargetJavaVersion >= 16)
        {
            // Java 16+ pattern matching
            return $"{expression} instanceof {ToRuntimeTypeForInstanceOf(targetType)} {variableName}";
        }
        else
        {
            // Older Java - explicit cast and assignment
            return $"{expression} instanceof {ToRuntimeTypeForInstanceOf(targetType)} && ({variableName} = ({targetType}){expression}) != null";
        }
    }

    private string TransformConstantPattern(string expression, ConstantPatternSyntax pattern, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var constant = facade.Transform(pattern.Expression, context);

        // C#: obj is null  → Java: obj == null
        if (pattern.Expression is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.NullLiteralExpression))
        {
            return $"{expression} == null";
        }

        // Other constant patterns
        return $"{expression} == {constant}";
    }

    private string TransformRecursivePattern(string expression, RecursivePatternSyntax pattern, ConversionContext context)
    {
        // Determine the type name for the instanceof check
        string? typeName = null;
        if (pattern.Type != null)
        {
            var typeInfo = context.GetTypeInfo(pattern.Type);
            typeName = (typeInfo.Type != null)
                ? context.MapType(typeInfo.Type)
                : context.MapTypeFromSyntax(pattern.Type);
        }

        var conditions = new List<string>();
        if (typeName != null)
            conditions.Add($"{expression} instanceof {typeName}");

        // Translate property pattern subpatterns to getter calls + comparisons
        if (pattern.PropertyPatternClause != null && typeName != null)
        {
            var cast = $"(({typeName}){expression})";
            foreach (var sub in pattern.PropertyPatternClause.Subpatterns)
            {
                string? propName = sub.NameColon?.Name.Identifier.Text
                    ?? (sub.ExpressionColon?.Expression is IdentifierNameSyntax idName ? idName.Identifier.Text : null);
                if (propName == null) continue;
                string getter = $"{cast}.get{char.ToUpperInvariant(propName[0])}{propName[1..]}()";
                string cond = TransformSubPattern(getter, sub.Pattern, context);
                conditions.Add(cond);
            }
        }

        return conditions.Count > 0
            ? string.Join(" && ", conditions)
            : $"/* TODO: recursive pattern */ {expression}";
    }

    private string TransformSubPattern(string subject, PatternSyntax pattern, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        return pattern switch
        {
            ConstantPatternSyntax constPat when constPat.Expression is LiteralExpressionSyntax lit
                && lit.IsKind(SyntaxKind.NullLiteralExpression)
                => $"{subject} == null",
            ConstantPatternSyntax constPat
                => $"{subject} == {facade.Transform(constPat.Expression, context)}",
            RelationalPatternSyntax relPat
                => $"{subject} {relPat.OperatorToken.Text} {facade.Transform(relPat.Expression, context)}",
            UnaryPatternSyntax { Pattern: ConstantPatternSyntax { Expression: LiteralExpressionSyntax nullLit } }
                when nullLit.IsKind(SyntaxKind.NullLiteralExpression)
                => $"{subject} != null",
            _ => $"/* TODO: sub-pattern */ {subject}"
        };
    }

    private string TransformAs(BinaryExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var expression = TransformAsOperand(node.Left, context);

        // Get the target type
        var typeInfo = context.GetTypeInfo(node.Right);
        string targetType;
        if (typeInfo.Type != null)
        {
            targetType = context.MapType(typeInfo.Type);
        }
        else
        {
            targetType = context.MapTypeFromSyntax(node.Right as TypeSyntax ?? throw new ArgumentException("Expected type"));
        }

        if (RequiresSingleEvaluation(node.Left, expression))
        {
            var tempName = context.GenerateSyntheticName("_asExpr");
            context.AddPreStatement($"var {tempName} = {expression};");
            expression = tempName;
        }

        // C#: obj as Type  → Java doesn't have direct equivalent
        // We use: obj instanceof Type ? (Type)obj : null
        //
        // Special case: C# allows "IEnumerable<T> as T[]" because C# arrays implement
        // IEnumerable<T>. Java arrays do NOT implement Iterable<T>, so this cast is
        // always impossible in Java — emit null directly instead of invalid instanceof.
        if (targetType.EndsWith("[]", StringComparison.Ordinal))
        {
            var sourceType = context.GetTypeInfo(node.Left).Type;
            if (sourceType is INamedTypeSymbol sourceNamed
                && (sourceNamed.Name is "IEnumerable" or "ICollection" or "IList"
                    or "IReadOnlyList" or "IReadOnlyCollection"
                    || sourceNamed.AllInterfaces.Any(i => i.Name is "IEnumerable")))
            {
                var javaSourceType = context.MapType(sourceType);
                if (javaSourceType.StartsWith("Iterable<", StringComparison.Ordinal)
                    || javaSourceType.StartsWith("Collection<", StringComparison.Ordinal)
                    || javaSourceType.StartsWith("List<", StringComparison.Ordinal)
                    || javaSourceType.StartsWith("CSharpGenericIterable", StringComparison.Ordinal)
                    || javaSourceType.StartsWith("CSharpICollection", StringComparison.Ordinal)
                    || javaSourceType.StartsWith("CSharpGenericIList", StringComparison.Ordinal)
                    || javaSourceType.StartsWith("CSharpReadOnlyCollection", StringComparison.Ordinal)
                    || javaSourceType.StartsWith("CSharpReadOnlyList", StringComparison.Ordinal))
                {
                    return "null";
                }
            }
        }
        // C# expr as IList<T> / expr as CSharpGenericIList<T>: when the source is IEnumerable-like
        // (CSharpGenericIterable, etc.), instanceof CSharpGenericIList will be false, yielding null.
        // In C#, as IList<T> on an IEnumerable<T> that isn't also an IList<T> returns null — but
        // callers typically use the result as a collection anyway. Wrapping in new CSharpList<>(expr)
        // preserves the elements and avoids NPEs in generated code.
        if (context.SemanticModel != null
            && (targetType.StartsWith("CSharpList<") || targetType.StartsWith("CSharpGenericIList<")))
        {
            var sourceType = context.GetTypeInfo(node.Left).Type;
            bool isEnumerableLikeSource = IsEnumerableLikeSourceType(sourceType);
            if (isEnumerableLikeSource)
            {
                context.AddImport("io.github.ningpp.compat.CSharpList");
                return $"({expression} instanceof {ToRuntimeTypeForInstanceOf(targetType)} ? ({targetType})({expression}) : new CSharpList<>({expression}))";
            }
        }

        // C# System.Array as T[]: System.Array maps to the CSharpArray wrapper in Java.
        // A direct "expr instanceof T[] ? (T[])expr : null" is invalid because CSharpArray
        // is not a Java array. Use CSharpArray.tryAs(expr, T[].class) to unwrap and test safely.
        // The static helper also accepts plain Java arrays, which matters when the source
        // expression has already been lowered to a typed array (e.g. ArrayList.ToArray(typeof(T))).
        if (context.SemanticModel != null
            && targetType.EndsWith("[]", StringComparison.Ordinal)
            && IsSystemArrayReferenceType(context.GetTypeInfo(node.Left).Type))
        {
            var classLiteral = ToRuntimeTypeForClassLiteral(targetType);
            context.AddImport("io.github.ningpp.compat.CSharpArray");
            return $"({expression} != null ? CSharpArray.tryAs({expression}, {classLiteral}.class) : null)";
        }

        return $"({expression} instanceof {ToRuntimeTypeForInstanceOf(targetType)} ? ({targetType})({expression}) : null)";
    }

    /// <summary>
    /// Transforms the left operand of an 'as' expression, ensuring property access
    /// uses getter methods even when the semantic model can't resolve the receiver type.
    /// </summary>
    private static string TransformAsOperand(ExpressionSyntax operand, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        if (operand is MemberAccessExpressionSyntax ma)
            return TransformAsOperandInner(ma, context);
        return facade.Transform(operand, context);
    }

    private static string TransformAsOperandInner(MemberAccessExpressionSyntax ma, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var memberName = ma.Name.Identifier.Text;
        // Recursively transform nested member access to ensure getters at all levels
        string receiver;
        if (ma.Expression is MemberAccessExpressionSyntax innerMa)
            receiver = TransformAsOperandInner(innerMa, context);
        else
            receiver = facade.Transform(ma.Expression, context);

        // Try semantic model first
        if (context.SemanticModel != null)
        {
            // Auto-properties emitted as fields in project pipeline
            if (memberName == "AlgorithmData") return $"{receiver}.AlgorithmData";

            var symbol = context.GetSymbolInfo(ma).Symbol;
            if (symbol is IPropertySymbol asProp)
            {
                if (asProp.Name == "Current" && IsEnumeratorRelated(asProp.ContainingType))
                    return $"{receiver}.getCurrent()";
                var getter = "get" + char.ToUpperInvariant(memberName[0]) + memberName[1..];
                return $"{receiver}.{getter}()";
            }
            if (symbol is IFieldSymbol { IsStatic: false })
                return $"{receiver}.{ConversionContext.EscapeJavaKeyword(memberName)}";

            // Try GetMembers via receiver type (semantic model path)
            ITypeSymbol? recvType = context.GetTypeInfo(ma.Expression).Type
                ?? (context.GetSymbolInfo(ma.Expression).Symbol switch
                {
                    ILocalSymbol ls => ls.Type,
                    IFieldSymbol fs => fs.Type,
                    IParameterSymbol ps => ps.Type,
                    IPropertySymbol ps2 => ps2.Type,
                    _ => null
                });
            if (recvType is INamedTypeSymbol { TypeKind: not TypeKind.Error } named)
            {
                if (memberName == "Current" && IsEnumeratorRelated(named))
                    return $"{receiver}.getCurrent()";
                foreach (var m in named.GetMembers(memberName))
                {
                    if (m is IPropertySymbol)
                    {
                        var getter = "get" + char.ToUpperInvariant(memberName[0]) + memberName[1..];
                        return $"{receiver}.{getter}()";
                    }
                    if (m is IFieldSymbol { IsStatic: false })
                        break;
                }
            }

            // AST-level heuristic: if the member name is PascalCase (like C# properties),
            // emit a getter when the semantic model can't resolve the type.
            if ((recvType == null || recvType.TypeKind == TypeKind.Error) && char.IsUpper(memberName[0]))
            {
                if (memberName == "Current")
                    return $"{receiver}.getCurrent()";
                var getter = "get" + char.ToUpperInvariant(memberName[0]) + memberName[1..];
                return $"{receiver}.{getter}()";
            }
        }

        // Fallback: use the standard transform
        return facade.Transform(ma, context);
    }

    private static bool IsEnumeratorRelated(ITypeSymbol? type)
    {
        if (type == null) return false;
        var display = type.ToDisplayString();
        if (display.StartsWith("System.Collections.IEnumerator", StringComparison.Ordinal)
            || display.StartsWith("System.Collections.Generic.IEnumerator", StringComparison.Ordinal))
            return true;
        if (type is INamedTypeSymbol named)
        {
            foreach (var iface in named.AllInterfaces)
            {
                var ifaceDisplay = iface.ToDisplayString();
                if (ifaceDisplay.StartsWith("System.Collections.IEnumerator", StringComparison.Ordinal)
                    || ifaceDisplay.StartsWith("System.Collections.Generic.IEnumerator", StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    private static bool RequiresSingleEvaluation(ExpressionSyntax expressionSyntax, string transformedExpression)
    {
        if (transformedExpression.Contains(".next()", StringComparison.Ordinal))
            return true;

        return expressionSyntax switch
        {
            InvocationExpressionSyntax => true,
            AwaitExpressionSyntax => true,
            AssignmentExpressionSyntax => true,
            ConditionalAccessExpressionSyntax => true,
            ElementAccessExpressionSyntax => true,
            ObjectCreationExpressionSyntax => true,
            PostfixUnaryExpressionSyntax => true,
            PrefixUnaryExpressionSyntax prefix when prefix.IsKind(SyntaxKind.PreIncrementExpression)
                || prefix.IsKind(SyntaxKind.PreDecrementExpression) => true,
            _ => false
        };
    }

    private static string ToRuntimeTypeForInstanceOf(string mappedType)
    {
        var stripped = ExpressionTransformerHelpers.StripTypeArguments(mappedType);
        return ExpressionTransformerHelpers.BoxJavaPrimitiveType(stripped);
    }

    private static string ToRuntimeTypeForClassLiteral(string mappedType)
        => ExpressionTransformerHelpers.ToRuntimeTypeForClassLiteral(mappedType);

    private string TransformTypeOf(TypeOfExpressionSyntax node, ConversionContext context)
    {
        var typeInfo = context.GetTypeInfo(node.Type);
        if (typeInfo.Type != null
            && TryGetDistinctRuntimeClassLiteralForTypeOf(typeInfo.Type, context, out var runtimeClassLiteral))
        {
            return runtimeClassLiteral;
        }

        string typeName;
        if (typeInfo.Type is INamedTypeSymbol namedType
            && IsUnboundGenericType(namedType))
        {
            // Unbound generics such as typeof(Nullable<>) or typeof(ArraySegment<>)
            // are represented as constructed types whose type arguments are the
            // definition's own type parameters (or error types in some semantic
            // models). They must map to the configured Java generic definition
            // (e.g. Optional.class or ArraySegment.class), not to the unwrapped
            // type parameter.
            var ns = namedType.ContainingNamespace?.ToDisplayString() ?? "";
            var configKey = (string.IsNullOrEmpty(ns) ? "" : ns + ".") + namedType.Name + "`" + namedType.TypeArguments.Length;
            typeName = context.TypeMappings.MapType(configKey);
            if (typeName != configKey)
            {
                context.AddImportsForTypePublic(configKey);
            }
            else
            {
                typeName = context.MapType(typeInfo.Type);
            }
        }
        else if (typeInfo.Type != null)
        {
            typeName = context.MapType(typeInfo.Type);
        }
        else
        {
            typeName = context.MapTypeFromSyntax(node.Type);
        }

        if (typeInfo.Type is ITypeParameterSymbol typeParam)
        {
            // C# typeof(T) represents the runtime Type. In Java the runtime class
            // parameter for a primitive T may be the primitive class literal (int.class)
            // so that array creation produces int[], but typeof(T) comparisons expect
            // the wrapper class literal (Integer.class). Normalize via TypeHelper.
            context.AddImport("io.github.ningpp.compat.TypeHelper");
            if (context.TryGetRuntimeClassParameter(typeParam.Name, out var runtimeClassParam))
                return $"TypeHelper.toWrapperType({runtimeClassParam})";
            return $"TypeHelper.toWrapperType({ConversionContext.GetClassLiteral(typeParam, context)})";
        }
        return $"{ToRuntimeTypeForClassLiteral(typeName)}.class";
    }

    private static bool TryGetDistinctRuntimeClassLiteralForTypeOf(
        ITypeSymbol typeSymbol,
        ConversionContext context,
        out string classLiteral)
    {
        classLiteral = string.Empty;

        var markerType = typeSymbol.SpecialType switch
        {
            SpecialType.System_Byte => "CSharpByte",
            SpecialType.System_SByte => "CSharpSByte",
            SpecialType.System_UInt16 => "CSharpUInt16",
            SpecialType.System_UInt32 => "CSharpUInt32",
            SpecialType.System_UInt64 => "CSharpUInt64",
            _ => null
        };

        if (markerType == null)
            return false;

        context.AddImport($"io.github.ningpp.compat.{markerType}");
        classLiteral = $"{markerType}.class";
        return true;
    }

    private string TransformDefault(DefaultExpressionSyntax node, ConversionContext context)
    {
        // DefaultExpressionSyntax is the default(Type) form — it always has a Type.
        // The bare 'default' literal is handled by TransformDefaultLiteral.
        var typeInfo = context.GetTypeInfo(node.Type);
        string typeName;
        if (typeInfo.Type != null)
        {
            typeName = context.MapType(typeInfo.Type);
        }
        else
        {
            typeName = context.MapTypeFromSyntax(node.Type);
        }

        return GetDefaultValueForType(typeName, typeInfo.Type, context);
    }

    /// <summary>
    /// Handles the bare 'default' literal (C# 7.1+), inferring the target type from
    /// the semantic model's ConvertedType (the type the expression is being assigned to).
    /// </summary>
    private string TransformDefaultLiteral(ExpressionSyntax node, ConversionContext context)
    {
        // Use ConvertedType to infer the target type from the assignment/declaration context
        var typeInfo = context.GetTypeInfo(node);
        var targetType = typeInfo.ConvertedType ?? typeInfo.Type;

        if (targetType != null)
        {
            var typeName = context.MapType(targetType);
            return GetDefaultValueForType(typeName, targetType, context);
        }

        return "null";
    }

    /// <summary>
    /// Returns the Java default value for a given mapped type name and optional type symbol.
    /// Primitives get their zero values, structs get new T(), reference types get null.
    /// Type parameters with struct constraint get new T() for consistency with concrete structs.
    /// </summary>
    internal static string GetDefaultValueForType(string typeName, ITypeSymbol? typeSymbol, ConversionContext context)
    {
        // Nullable<T> (e.g. int?) defaults to null in C#, not a zero-initialized struct.
        if (typeSymbol is INamedTypeSymbol { IsGenericType: true } nullableType
            && nullableType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            return "null";
        }

        if (typeSymbol is INamedTypeSymbol { IsTupleType: true } tupleType)
        {
            context.AddImport("io.vavr.Tuple");
            var elementDefaults = tupleType.TupleElements
                .Select(element => GetDefaultValueForType(context.MapType(element.Type), element.Type, context));
            return $"Tuple.of({string.Join(", ", elementDefaults)})";
        }

        var defaultValue = typeName switch
        {
            "int" => "0",
            "long" => "0L",
            "short" => "(short)0",
            "byte" => "0",
            "float" => "0.0f",
            "double" => "0.0",
            "boolean" => "false",
            "char" => "'\\0'",
            "Decimal" => "Decimal.ZERO",
            _ => null
        };

        if (defaultValue != null)
        {
            if (typeName == "Decimal")
                context.AddImport("io.github.ningpp.compat.Decimal");
            return defaultValue;
        }

        // For user-defined structs/value types, emit new T().
        if (typeSymbol is INamedTypeSymbol { TypeKind: TypeKind.Struct })
            return $"new {typeName}()";

        // For enum types, C# default(EnumType) returns the member with value 0,
        // but Java enum references default to null. Return the 0-valued member.
        if (typeSymbol is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
        {
            bool isFlags = context.IsFlagsEnum(enumType.Name)
                || enumType.GetAttributes().Any(a =>
                    a.AttributeClass?.Name is "FlagsAttribute" or "Flags");
            if (isFlags)
                return "0";
            var enumTypeRef = BuildEnumTypeReference(enumType);
            var zeroMember = FindEnumMemberByValue(enumType, 0);
            return zeroMember != null
                ? $"{enumTypeRef}.{zeroMember}"
                : $"{enumTypeRef}.values()[0]";
        }

        // For type parameters with struct constraint, emit new T().
        // C# default(T) where T : struct yields the zero-initialized value,
        // which in Java corresponds to new T() (structs become classes).
        if (typeSymbol is ITypeParameterSymbol { HasValueTypeConstraint: true })
            return $"new {typeName}()";

        // For type parameters with class constraint, null is always correct.
        if (typeSymbol is ITypeParameterSymbol { HasReferenceTypeConstraint: true })
            return "null";

        // For unconstrained type parameters, query the binding analyzer to check
        // if all subclass instantiations bind this parameter to the same struct type.
        if (typeSymbol is ITypeParameterSymbol typeParam)
        {
            var binding = context.GetBindingAnalyzer().GetBinding(typeParam);
            if (binding.Kind == Analysis.TypeParameterBindingKind.AlwaysSameStruct
                && binding.ConcreteStructType != null)
            {
                var concreteJavaType = context.MapType(binding.ConcreteStructType);
                return $"new {concreteJavaType}()";
            }

            // When the binding is unknown (e.g. independent library conversion),
            // emit a call to a protected factory method that subclasses can override
            // to supply a non-null default value for struct type parameters.
            // Without the override, the factory returns DefaultValue.of() (null),
            // but a subclass that binds TValue to a concrete struct type can
            // override the factory to return new ValueType(), preventing NPEs.
            //
            // Only class-level type parameters can use this pattern — method-level
            // type parameters fall back to DefaultValue.of() since there's no
            // class method to override.
            if (binding.Kind == Analysis.TypeParameterBindingKind.Unknown)
            {
                var containingType = typeParam.ContainingType;
                var isClassLevel = typeParam.DeclaringMethod == null && containingType != null;
                var isInStaticContext = context.IsInStaticMember
                    || (context.CurrentMethod?.IsStatic == true);

                if (isClassLevel && !isInStaticContext)
                {
                    // Class-level TP in instance context: emit a factory method call
                    // that subclasses can override to return new ValueType().
                    var fullName = Analysis.TypeParameterBindingAnalyzer.GetFullMetadataName(
                        containingType.OriginalDefinition);
                    context.RegisterDefaultFactoryMethod(fullName, typeParam.Name);
                    return $"_cs2jDefault_{typeParam.Name}()";
                }

                // Method-level TP: add a Class<T> parameter to the method signature.
                // The method body uses DefaultValue.of(_cs2j_T) which returns the
                // proper default (zero for int, new ValueType() for structs, null for ref types).
                if (typeParam.DeclaringMethod != null && containingType != null)
                {
                    if (context.TryGetRuntimeClassParameter(typeParam.Name, out var existingRuntimeClassParameter))
                    {
                        context.AddImport("io.github.ningpp.compat.DefaultValue");
                        // Runtime class parameter from AddRuntimeClassParametersForTypeParameterArrays
                        // is Class<?>, so DefaultValue.of(Class<?>) cannot infer T.
                        // Use DefaultValue.ofClass(Class<?>) + cast to preserve type safety.
                        return $"({typeParam.Name}) DefaultValue.ofClass({existingRuntimeClassParameter})";
                    }

                    var method = typeParam.DeclaringMethod;
                    context.RequireClassTypeParam(
                        containingType.MetadataName,
                        method.MetadataName,
                        typeParam.Name);
                    context.AddImport("io.github.ningpp.compat.DefaultValue");
                    return $"DefaultValue.of(_cs2j_{typeParam.Name})";
                }
                return "null";
            }
        }

        return "null"; // Reference types and AlwaysReference type parameters default to null
    }

    private string TransformChecked(CheckedExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var expression = facade.Transform(node.Expression, context);

        // C# checked context - Java doesn't have overflow checking by default
        // For Java, we might want to add Math.addExact(), etc. but that's complex
        // For now, emit the expression with a comment
        return $"/* checked */ {expression}";
    }

    private string TransformUnchecked(CheckedExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var expression = facade.Transform(node.Expression, context);

        // C# unchecked context - Java's default behavior
        return expression;
    }

    private string TransformSizeOf(SizeOfExpressionSyntax node, ConversionContext context)
    {
        // Map C# built-in types to their byte sizes (platform-independent for well-known types).
        if (node.Type is PredefinedTypeSyntax predefined)
        {
            return predefined.Keyword.Text switch
            {
                "byte" => "4",
                "sbyte" or "bool" => "1",
                "short" or "ushort" or "char" => "2",
                "int" or "uint" or "float" => "4",
                "long" or "ulong" or "double" => "8",
                "decimal" => "16",
                _ => $"/* sizeof({node.Type}) */"
            };
        }
        // For user-defined struct types, emit a comment — Java has no sizeof operator.
        var typeName = context.MapTypeFromSyntax(node.Type);
        return $"/* sizeof({typeName}) */";
    }

    // Helper methods

    private static bool IsPrimitiveToWrapperCast(ExpressionSyntax expr, string targetType, ConversionContext context)
    {
        // Check if we're casting from a primitive type to its wrapper
        var typeInfo = context.GetTypeInfo(expr);
        if (typeInfo.Type == null) return false;

        var sourceType = context.MapType(typeInfo.Type);

        return (sourceType, targetType) switch
        {
            ("int", "Integer") or ("long", "Long") or ("short", "Short") or
            ("byte", "Byte") or ("float", "Float") or ("double", "Double") or
            ("boolean", "Boolean") or ("char", "Character") => true,
            _ => false
        };
    }

    private static string targetNameOf(string wrapperType)
    {
        return wrapperType switch
        {
            "Integer" => "Integer.valueOf",
            "Long" => "Long.valueOf",
            "Short" => "Short.valueOf",
            "Byte" => "Byte.valueOf",
            "Float" => "Float.valueOf",
            "Double" => "Double.valueOf",
            "Boolean" => "Boolean.valueOf",
            "Character" => "Character.valueOf",
            _ => wrapperType
        };
    }

    private static string BuildEnumTypeReference(INamedTypeSymbol enumType)
    {
        return enumType.ContainingType is INamedTypeSymbol parentType
            ? $"{BuildEnumTypeReference(parentType)}.{enumType.Name}"
            : enumType.Name;
    }

    private static string? FindEnumMemberByValue(INamedTypeSymbol enumType, long value)
    {
        foreach (var member in enumType.GetMembers())
        {
            if (member is IFieldSymbol { IsConst: true, HasConstantValue: true } field)
            {
                if (field.ConstantValue is int intVal && intVal == value)
                    return field.Name;
                if (field.ConstantValue is long longVal && longVal == value)
                    return field.Name;
                if (field.ConstantValue is short shortVal && shortVal == value)
                    return field.Name;
                if (field.ConstantValue is byte byteVal && byteVal == value)
                    return field.Name;
                if (field.ConstantValue is sbyte sbyteVal && sbyteVal == value)
                    return field.Name;
                if (field.ConstantValue is ushort ushortVal && ushortVal == value)
                    return field.Name;
                if (field.ConstantValue is uint uintVal && uintVal == value)
                    return field.Name;
                if (field.ConstantValue is ulong ulongVal && ulongVal == (ulong)value)
                    return field.Name;
            }
        }
        return null;
    }
}
