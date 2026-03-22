using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Transformers.Expression.Utilities;
using System.Collections.Generic;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles identifier and member access expressions.
/// </summary>
[TransformerRegistration]
public class IdentifierExpressionTransformer : IExpressionTransformer
{
    static IdentifierExpressionTransformer()
    {
        ExpressionTransformerRegistry.Register(new[]
        {
            SyntaxKind.IdentifierName,
            SyntaxKind.PredefinedType,
            SyntaxKind.GenericName,
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxKind.PointerMemberAccessExpression
        }, new IdentifierExpressionTransformer());
    }

    private static readonly Lazy<IdentifierExpressionTransformer> _instance = new(() => new());
    public static IdentifierExpressionTransformer Instance => _instance.Value;

    /// <summary>
    /// Maps C# BCL numeric class names (as IdentifierNameSyntax receivers) to their
    /// corresponding (keyword, Java wrapper class) pairs for static constant resolution.
    /// This handles patterns like <c>Double.MaxValue</c>, <c>Int32.MaxValue</c>, etc.
    /// where the receiver is the class name, not the keyword alias (double/int/...).
    /// </summary>
    private static readonly Dictionary<string, (string keyword, string javaWrapper)> _csharpBoxedClassNames = new()
    {
        ["Double"]   = ("double",  "Double"),
        ["Single"]   = ("float",   "Float"),
        ["Int32"]    = ("int",     "Integer"),
        ["Int64"]    = ("long",    "Long"),
        ["Int16"]    = ("short",   "Short"),
        ["Byte"]     = ("byte",    "Byte"),
        ["SByte"]    = ("byte",    "Byte"),
        ["UInt32"]   = ("int",     "Integer"),
        ["UInt64"]   = ("long",    "Long"),
        ["UInt16"]   = ("short",   "Short"),
        ["Char"]     = ("char",    "Character"),
        ["Boolean"]  = ("bool",    "Boolean"),
    };

    public string Transform(ExpressionSyntax node, ConversionContext context)
        => node.Kind() switch
        {
            SyntaxKind.IdentifierName => TransformIdentifier((IdentifierNameSyntax)node, context),
            SyntaxKind.PredefinedType => TransformPredefinedType((PredefinedTypeSyntax)node),
            SyntaxKind.GenericName => TransformGenericName((GenericNameSyntax)node, context),
            SyntaxKind.SimpleMemberAccessExpression => TransformMemberAccess((MemberAccessExpressionSyntax)node, context),
            SyntaxKind.PointerMemberAccessExpression => TransformPointerMemberAccess((MemberAccessExpressionSyntax)node, context),
            _ => throw new NotSupportedException($"Identifier expression kind {node.Kind()} not supported.")
        };

    private string TransformIdentifier(IdentifierNameSyntax node, ConversionContext context)
    {
        var name = node.Identifier.Text;

        // Fix 2: resolve LINQ 'let' clause variables inlined via QueryLetAliases
        if (context.QueryLetAliases.TryGetValue(name, out var letAlias))
            return letAlias;

        if (context.TryGetActiveRefHolder(name, out var activeHolderName))
            return $"{activeHolderName}.value";

        // Check for using aliases — Fix 5: chain alias resolution through type-registry
        if (context.IsAlias(name))
        {
            var javaType = context.MapAliasToJavaType(name);
            if (javaType != null)
            {
                // If MapAliasToJavaType returned a simple (unqualified) name, apply a
                // secondary type-registry lookup to pick up any package-mapping entries.
                if (!javaType.Contains('.'))
                {
                    var remapped = context.TypeMappings.MapType(javaType);
                    if (remapped != javaType)
                        return remapped;
                }
                return javaType;
            }
        }

        // Check if the identifier resolves to a property — generate getter() for reads,
        // or the camelCase backing-field name when it appears on the LHS of an assignment
        // (AssignmentTransformer will wrap that into a setXxx() call).
        if (context.SemanticModel?.GetSymbolInfo(node).Symbol is IPropertySymbol identProp)
        {
            bool isLhsOfAssignment = node.Parent is AssignmentExpressionSyntax asgn && asgn.Left == node;
            if (!isLhsOfAssignment)
            {
                var getter = "get" + char.ToUpperInvariant(identProp.Name[0]) + identProp.Name[1..];
                return $"{getter}()";
            }
            // LHS: return camelCase so AssignmentTransformer can build setXxx(rhs)
            return char.ToLower(identProp.Name[0]) + identProp.Name[1..];
        }

        // When this identifier is an out/ref parameter, any use as a receiver must go through
        // .value so that member accesses like p.X or p.X = 1 become p.value.X / p.value.setX(1).
        // Direct assignment (p = value → p.value = value) is handled separately by AssignmentTransformer
        // with an early return that never reaches this path.
        if (context.SemanticModel?.GetSymbolInfo(node).Symbol is IParameterSymbol outParam
            && (outParam.RefKind == RefKind.Out || outParam.RefKind == RefKind.Ref))
        {
            return $"{ConversionContext.EscapeJavaKeyword(outParam.Name)}.value";
        }

        // Fix: Handle event references within the same class.
        // C#: ProgressChanged != null  → Java: !_progressChangedListeners.isEmpty()
        // C#: ProgressChanged(...)    → Java: fireProgressChanged(...)
        if (context.SemanticModel?.GetSymbolInfo(node).Symbol is IEventSymbol eventSym)
        {
            var fieldName = $"_{char.ToLower(name[0])}{name.Substring(1)}Listeners";
            var fireMethodName = GetFireMethodName(name);

            // For null comparisons (ProgressChanged != null or ProgressChanged == null)
            if (node.Parent is BinaryExpressionSyntax binaryExpr)
            {
                // Only handle when the event is the left operand and right is null literal
                if (binaryExpr.Left == node && binaryExpr.Right.IsKind(SyntaxKind.NullLiteralExpression))
                {
                    var op = binaryExpr.OperatorToken.Kind();
                    if (op == SyntaxKind.EqualsExpression)
                    {
                        return $"{fieldName}.isEmpty()"; // ProgressChanged == null → _listeners.isEmpty()
                    }
                    if (op == SyntaxKind.NotEqualsExpression)
                    {
                        return $"!{fieldName}.isEmpty()"; // ProgressChanged != null → !_listeners.isEmpty()
                    }
                }
                // Handle case when event is the right operand (null != ProgressChanged, null == ProgressChanged)
                if (binaryExpr.Right == node && binaryExpr.Left.IsKind(SyntaxKind.NullLiteralExpression))
                {
                    var op = binaryExpr.OperatorToken.Kind();
                    if (op == SyntaxKind.EqualsExpression)
                    {
                        return $"{fieldName}.isEmpty()"; // null == ProgressChanged → _listeners.isEmpty()
                    }
                    if (op == SyntaxKind.NotEqualsExpression)
                    {
                        return $"!{fieldName}.isEmpty()"; // null != ProgressChanged → !_listeners.isEmpty()
                    }
                }
            }

            // For conditional access (ProgressChanged?.Invoke(...))
            // In this case, the identifier itself should be replaced with the fire method call
            // and the conditional access wrapper will handle the null check
            if (node.Parent is ConditionalAccessExpressionSyntax)
            {
                return fireMethodName;
            }

            // For direct invocation (ProgressChanged(sender, args)) or member access
            // Return the fire method name - the invocation will be handled by the parent
            return fireMethodName;
        }

        // Fix: Bare identifier method group used as value (not invoked) → Java method reference.
        // e.g. Action<int> a = Process; → Consumer<Integer> a = this::process;
        if (context.SemanticModel?.GetSymbolInfo(node).Symbol is IMethodSymbol bareMethodGroup
            && !(node.Parent is InvocationExpressionSyntax invNode && invNode.Expression == node)
            && !(node.Parent is MemberAccessExpressionSyntax))
        {
            var javaName = name switch
            {
                "GetHashCode"   => "hashCode",
                "GetEnumerator" => "iterator",
                "GetType"       => "getClass",
                "Dispose"       => "close",
                _ when name.Length > 0
                    => char.ToLowerInvariant(name[0]) + name[1..],
                _ => name
            };

            // Declare prefix for the fallback case (used in both branches)
            var prefix = bareMethodGroup.IsStatic ? bareMethodGroup.ContainingType.Name : "this";

            // Fix: For EventHandler-compatible method groups (void return, 2 params),
            // use an explicit lambda instead of a bare method reference.
            // This avoids Java type inference issues when assigning to BiConsumer<Object, T>.
            // Example: ProgressChanged += NotifyProgressChanged; should generate:
            //   (sender, args) -> notifyProgressChanged(sender, args)
            // instead of: this::notifyProgressChanged
            var parameters = bareMethodGroup.Parameters;
            var methodReturnsVoid = bareMethodGroup.ReturnsVoid || bareMethodGroup.ReturnType?.SpecialType == SpecialType.System_Void;

            if (methodReturnsVoid && parameters.Length == 2)
            {
                var firstParam = parameters[0];
                var secondParam = parameters[1];

                // Check if first parameter is Object (sender) and second is EventArgs-derived (args)
                var firstIsObject = firstParam.Type?.SpecialType == SpecialType.System_Object;
                var secondIsEventArgs = secondParam.Type?.ToDisplayString() == "System.EventArgs"
                    || (secondParam.Type?.BaseType?.ToDisplayString() == "System.EventArgs");
                var secondIsNamedType = secondParam.Type is INamedTypeSymbol;

                if (firstIsObject && (secondIsEventArgs || secondIsNamedType))
                {
                    // Generate explicit lambda: (sender, args) -> methodName(sender, args)
                    // For instance methods, omit the "this." prefix since it's optional in Java
                    var secondParamName = char.ToLowerInvariant(secondParam.Name[0]) + secondParam.Name[1..];
                    var methodPrefix = bareMethodGroup.IsStatic ? bareMethodGroup.ContainingType.Name + "." : "";
                    return $"(sender, {secondParamName}) -> {methodPrefix}{ConversionContext.EscapeJavaKeyword(javaName)}(sender, {secondParamName})";
                }
            }

            return $"{prefix}::{ConversionContext.EscapeJavaKeyword(javaName)}";
        }

        return ConversionContext.EscapeJavaKeyword(name);
    }

    // Fix 3 & 4: Replaced duplicate local BoxedTypeName with TransformPredefinedType.
    // Uses boxed types in generic-argument positions; delegates to ExpressionTransformerHelpers
    // (the canonical BoxedTypeName source) to avoid divergence.
    private string TransformPredefinedType(PredefinedTypeSyntax node)
    {
        // Fix 3: generic type arguments require boxed types (e.g., List<Integer> not List<int>)
        if (node.Parent is TypeArgumentListSyntax)
            return ExpressionTransformerHelpers.BoxedTypeName(node);

        // Non-generic context: use Java primitive / value types
        var typeName = node.Keyword.Text;
        return typeName switch
        {
            "int" => "int",
            "long" => "long",
            "short" => "short",
            "byte" => "byte",
            "sbyte" => "byte",
            "uint" => "int",
            "ulong" => "long",
            "ushort" => "short",
            "float" => "float",
            "double" => "double",
            "bool" => "boolean",
            "char" => "char",
            "string" => "String",
            "object" => "Object",
            "void" => "void",
            _ => typeName
        };
    }

    private string TransformGenericName(GenericNameSyntax node, ConversionContext context)
    {
        var name = ConversionContext.EscapeJavaKeyword(node.Identifier.Text);
        var typeArgs = new List<string>();

        foreach (var typeArg in node.TypeArgumentList.Arguments)
        {
            var typeInfo = context.SemanticModel?.GetTypeInfo(typeArg);
            if (typeInfo.HasValue && typeInfo.Value.Type != null)
            {
                typeArgs.Add(context.MapType(typeInfo.Value.Type));
            }
            else
            {
                typeArgs.Add(typeArg.ToString());
            }
        }

        return $"{name}<{string.Join(", ", typeArgs)}>";
    }

    private string TransformMemberAccess(MemberAccessExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;

        // Fix: Generic type static member access — C# allows Set<T>.Method() but Java requires Set.Method().
        // Strip type arguments from the receiver whenever it is a generic name expression.
        if (node.Expression is GenericNameSyntax genericExprName)
        {
            var rawReceiver = ConversionContext.EscapeJavaKeyword(genericExprName.Identifier.Text);
            var rawMember   = ConversionContext.EscapeJavaKeyword(node.Name.Identifier.Text);
            return $"{rawReceiver}.{rawMember}";
        }

        // Fix: Primitive type static member access — C# double.MaxValue → Java Double.MAX_VALUE etc.
        if (node.Expression is PredefinedTypeSyntax primTypeSyntax)
        {
            var boxedName  = ExpressionTransformerHelpers.BoxedTypeName(primTypeSyntax);
            var rawMember  = node.Name.Identifier.Text;
            var mappedMember = MapPrimitiveStaticFieldName(primTypeSyntax.Keyword.Text, rawMember);
            // If the mapping already produced a self-contained expression (e.g. "(-Double.MAX_VALUE)")
            // don't prefix it with the boxed type name — that would create "Double.(-Double.MAX_VALUE)".
            if (mappedMember.StartsWith("(") || mappedMember.StartsWith("-"))
                return mappedMember;
            return $"{boxedName}.{mappedMember}";
        }

        var target = facade.Transform(node.Expression, context);
        var memberName = node.Name.Identifier.Text;

        if (target == "String" && memberName == "Empty")
            return "\"\"";

        // Fallback for unresolved method-group symbol: Parallel.Invoke used as delegate value.
        if (memberName == "Invoke" && node.Expression.ToString() is "Parallel" or "System.Threading.Tasks.Parallel")
        {
            context.AddImport("java.util.Arrays");
            return "actions -> Arrays.stream(actions).forEach(Runnable::run)";
        }

        // Fix: Handle event member access within the same class.
        // C#: this.ProgressChanged != null  → Java: !_progressChangedListeners.isEmpty()
        // C#: this.ProgressChanged(...)    → Java: fireProgressChanged(...)
        if (context.SemanticModel?.GetSymbolInfo(node).Symbol is IEventSymbol eventSym)
        {
            var fieldName = $"_{char.ToLower(memberName[0])}{memberName.Substring(1)}Listeners";
            var currentTypeName = context.CurrentType?.Name;

            // Only convert to listener access within the same class
            if (currentTypeName != null && eventSym.ContainingType.Name == currentTypeName)
            {
                // For null comparisons (this.ProgressChanged != null)
                if (node.Parent is BinaryExpressionSyntax binaryExpr)
                {
                    if (binaryExpr.Left == node && binaryExpr.Right.IsKind(SyntaxKind.NullLiteralExpression))
                    {
                        var op = binaryExpr.OperatorToken.Kind();
                        if (op == SyntaxKind.EqualsExpression)
                            return fieldName + ".isEmpty()";
                        if (op == SyntaxKind.NotEqualsExpression)
                            return $"!{fieldName}.isEmpty()";
                    }
                    if (binaryExpr.Right == node && binaryExpr.Left.IsKind(SyntaxKind.NullLiteralExpression))
                    {
                        var op = binaryExpr.OperatorToken.Kind();
                        if (op == SyntaxKind.EqualsExpression)
                            return fieldName + ".isEmpty()";
                        if (op == SyntaxKind.NotEqualsExpression)
                            return $"!{fieldName}.isEmpty()";
                    }
                }

                // For invocation or direct access, use the fire method name
                return GetFireMethodName(memberName);
            }
        }

        // Fix: Method group used as value (not invoked) → Java method reference (receiver::method).
        // e.g. C# `Parallel.Invoke` as a delegate value → Java `Parallel::invoke`.
        var methodGroupInfo = context.SemanticModel?.GetSymbolInfo(node);
        var methodGroupSym = methodGroupInfo?.Symbol as IMethodSymbol
            ?? methodGroupInfo?.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
        if (methodGroupSym != null
            && !(node.Parent is InvocationExpressionSyntax inv && inv.Expression == node))
        {
            if (methodGroupSym.ContainingType?.ToDisplayString() == "System.Threading.Tasks.Parallel"
                && methodGroupSym.Name == "Invoke")
            {
                context.AddImport("java.util.Arrays");
                return "actions -> Arrays.stream(actions).forEach(Runnable::run)";
            }

            var javaMethodName = memberName switch
            {
                "GetHashCode"   => "hashCode",
                "GetEnumerator" => "iterator",
                "GetType"       => "getClass",
                "Dispose"       => "close",
                _ when memberName.Length > 0
                    => char.ToLowerInvariant(memberName[0]) + memberName[1..],
                _ => memberName
            };
            // Check TypeMappings for an explicit method name override
            var typeName = methodGroupSym.ContainingType.ToDisplayString();
            var mapped = context.TypeMappings.MapMethod(typeName, memberName);
            if (mapped != null)
                javaMethodName = mapped;

            // Fix: For EventHandler-compatible method groups (void return, 2 params),
            // use an explicit lambda instead of a bare method reference.
            // This avoids Java type inference issues when assigning to BiConsumer<Object, T>.
            var parameters = methodGroupSym.Parameters;
            var methodReturnsVoid = methodGroupSym.ReturnsVoid || methodGroupSym.ReturnType?.SpecialType == SpecialType.System_Void;

            if (methodReturnsVoid && parameters.Length == 2)
            {
                var firstParam = parameters[0];
                var secondParam = parameters[1];

                // Check if first parameter is Object (sender) and second is EventArgs-derived (args)
                var firstIsObject = firstParam.Type?.SpecialType == SpecialType.System_Object;
                var secondIsEventArgs = secondParam.Type?.ToDisplayString() == "System.EventArgs"
                    || (secondParam.Type?.BaseType?.ToDisplayString() == "System.EventArgs");
                var secondIsNamedType = secondParam.Type is INamedTypeSymbol;

                if (firstIsObject && (secondIsEventArgs || secondIsNamedType))
                {
                    // Generate explicit lambda: (sender, args) -> receiver.methodName(sender, args)
                    var secondParamName = char.ToLowerInvariant(secondParam.Name[0]) + secondParam.Name[1..];
                    return $"(sender, {secondParamName}) -> {target}.{ConversionContext.EscapeJavaKeyword(javaMethodName)}(sender, {secondParamName})";
                }
            }

            return $"{target}::{ConversionContext.EscapeJavaKeyword(javaMethodName)}";
        }

        // Fix 1 & 2: consult member-name mapping and generate property getters
        if (context.SemanticModel?.GetSymbolInfo(node).Symbol is IPropertySymbol prop)
        {
            if (prop.Name == "Current" && IsEnumeratorCurrentProperty(prop))
                return $"{target}.next()";

            var propContainer = prop.ContainingType;
            bool isGenericDictionaryLike =
                propContainer?.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic"
                && propContainer.Name is "Dictionary" or "SortedDictionary" or "IDictionary" or "IReadOnlyDictionary";

            if (isGenericDictionaryLike)
            {
                if (prop.Name == "Values") return $"{target}.values()";
                if (prop.Name == "Keys") return $"{target}.keySet()";
            }

            if (prop.Name == "Capacity"
                && prop.ContainingType?.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.List<T>")
            {
                // Java ArrayList has no readable capacity API; use size() as a safe compilable approximation.
                return $"{target}.size()";
            }

            // Fix 1: check TypeMappings for a configured method/member name mapping
            var typeName = prop.ContainingType.ToDisplayString();
            var mappedMethod = context.TypeMappings.MapMethod(typeName, prop.Name);
            // C# uses lowercase/alias display names (e.g. "string" for System.String).
            // TypeMappings keys use fully-qualified names; retry with FQN on alias miss.
            if (mappedMethod == null)
            {
                var fqn = $"{prop.ContainingType.ContainingNamespace}.{prop.ContainingType.Name}";
                mappedMethod = context.TypeMappings.MapMethod(fqn, prop.Name);
            }
            // For static properties, remap the target to the Java type name regardless of
            // which lookup path succeeded (e.g. DateTime.Now → LocalDateTime.now()).
            if (mappedMethod != null && prop.IsStatic)
            {
                var fqnForRemap = $"{prop.ContainingType.ContainingNamespace}.{prop.ContainingType.Name}";
                var mappedType = context.TypeMappings.MapType(fqnForRemap);
                if (!string.IsNullOrEmpty(mappedType) && mappedType != fqnForRemap)
                    target = mappedType;
            }
            if (mappedMethod != null)
            {
                if (mappedMethod == "getValues")
                    return $"{target}.values()";
                if (mappedMethod == "getKeys")
                    return $"{target}.keySet()";

                // If the mapped value is a fully-qualified Java field (contains a dot, e.g.
                // "java.util.Locale.ROOT") emit it directly without a receiver prefix or ().
                // For array.length: Java arrays expose length as a public final field, not a
                // method — emit without parentheses.
                // Otherwise it is a method name (e.g. "size") — emit as target.method().
                if (mappedMethod.Contains('.'))
                    return mappedMethod;
                if (prop.ContainingType.SpecialType == SpecialType.System_Array)
                    return $"{target}.{mappedMethod}";
                return $"{target}.{mappedMethod}()";
            }

            // Fix 2: no mapping configured — generate getXxx() for read accesses
            bool isLhsOfAssignment = node.Parent is AssignmentExpressionSyntax assign && assign.Left == node;
            if (!isLhsOfAssignment)
            {
                // For anonymous types synthesized as Java records, use camelCase accessor (e.g. id() not getId())
                if (prop.ContainingType.IsAnonymousType
                    && context.Options.UseRecords && context.Options.TargetJavaVersion >= JavaVersion.Java17)
                {
                    var recordAccessor = char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..];
                    return $"{target}.{recordAccessor}()";
                }
                var getter = "get" + char.ToUpperInvariant(prop.Name[0]) + prop.Name[1..];
                return $"{target}.{getter}()";
            }
        }

        // Fix 3: GetSymbolInfo returned no IPropertySymbol (e.g. lambda param in LINQ-rewritten tree).
        // Fall back to GetTypeInfo on the receiver expression for TypeMappings lookup.
        if (context.SemanticModel != null)
        {
            var exprType = context.SemanticModel.GetTypeInfo(node.Expression).Type;
            if (exprType != null)
            {
                if (memberName == "Current" && IsEnumeratorLikeType(exprType))
                    return $"{target}.next()";

                if (exprType is INamedTypeSymbol namedExprType
                    && namedExprType.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic"
                    && namedExprType.Name is "Dictionary" or "SortedDictionary" or "IDictionary" or "IReadOnlyDictionary")
                {
                    if (memberName == "Values") return $"{target}.values()";
                    if (memberName == "Keys") return $"{target}.keySet()";
                }

                var tn3 = exprType.ToDisplayString();
                var mm3 = context.TypeMappings.MapMethod(tn3, memberName);
                if (mm3 == null && exprType.ContainingNamespace != null)
                    mm3 = context.TypeMappings.MapMethod($"{exprType.ContainingNamespace}.{exprType.Name}", memberName);
                if (mm3 != null)
                {
                    if (mm3 == "getValues") return $"{target}.values()";
                    if (mm3 == "getKeys") return $"{target}.keySet()";
                    return mm3.Contains('.') ? mm3 : $"{target}.{mm3}()";
                }
            }
        }

        // Fix: C# boxed class-name static constants — Double.MaxValue → Double.MAX_VALUE,
        // Int32.MaxValue → Integer.MAX_VALUE, Single.MaxValue → Float.MAX_VALUE, etc.
        // The PredefinedTypeSyntax path above handles keyword forms (e.g. 'double.MaxValue'),
        // but when code uses the class name form the receiver is an IdentifierNameSyntax.
        if (node.Expression is IdentifierNameSyntax { Identifier.Text: var boxedIdText }
            && _csharpBoxedClassNames.TryGetValue(boxedIdText, out var primInfo))
        {
            var mappedConst = MapPrimitiveStaticFieldName(primInfo.keyword, memberName);
            if (mappedConst != memberName) // mapping found (not an identity pass-through)
            {
                // Some mappings return self-contained expressions like "(-Double.MAX_VALUE)".
                if (mappedConst.StartsWith("(") || mappedConst.StartsWith("-"))
                    return mappedConst;
                return $"{primInfo.javaWrapper}.{mappedConst}";
            }
        }

        if (memberName == "Values") return $"{target}.values()";
        if (memberName == "Keys") return $"{target}.keySet()";

        var member = ConversionContext.EscapeJavaKeyword(memberName);
        return $"{target}.{member}";
    }

    /// <summary>
    /// Maps C# primitive-type static field/property names to their Java equivalents.
    /// e.g. double.MaxValue → MAX_VALUE, double.PositiveInfinity → POSITIVE_INFINITY
    /// Note: for double/float, MinValue in C# is the most-negative finite value
    ///       (-MAX_VALUE in Java), not the smallest positive value (Java's MIN_VALUE).
    /// </summary>
    private static string MapPrimitiveStaticFieldName(string primitiveKeyword, string memberName)
        => (primitiveKeyword, memberName) switch
        {
            // Double/float MinValue = most negative finite → negate MAX_VALUE
            ("double" or "float", "MinValue") => $"(-{(primitiveKeyword == "double" ? "Double" : "Float")}.MAX_VALUE)",
            (_, "MaxValue")          => "MAX_VALUE",
            (_, "MinValue")          => "MIN_VALUE",
            (_, "Epsilon")           => "MIN_VALUE",
            (_, "PositiveInfinity")  => "POSITIVE_INFINITY",
            (_, "NegativeInfinity")  => "NEGATIVE_INFINITY",
            (_, "NaN")               => "NaN",
            // Static methods used as non-invocation members — pass through
            (_, "IsInfinity")        => "isInfinite",
            (_, "IsPositiveInfinity")=> "isInfinite",
            (_, "IsNegativeInfinity")=> "isInfinite",
            (_, "IsNaN")             => "isNaN",
            _                        => memberName
        };

    private string TransformPointerMemberAccess(MemberAccessExpressionSyntax node, ConversionContext context)
    {
        // C# pointer member access (ptr->member) has no direct Java equivalent
        context.Diagnostics.Warning("Pointer member access (->) has no Java equivalent - unsafe code not supported", node.GetLocation());
        var facade = ExpressionTransformerFacade.Instance;
        var target = facade.Transform(node.Expression, context);
        var member = ConversionContext.EscapeJavaKeyword(node.Name.Identifier.Text);
        // Fix 6: note that unsafe pointer semantics cannot be reproduced in Java
        return $"/* WARNING: C# unsafe pointer dereference — Java does not support pointer arithmetic. */ {target}.{member}";
    }

    /// <summary>
    /// Gets the Java fire method name for a C# event.
    /// e.g. ProgressChanged → fireProgressChanged
    /// </summary>
    private static string GetFireMethodName(string eventName)
    {
        return $"fire{char.ToUpperInvariant(eventName[0])}{eventName.Substring(1)}";
    }

    private static bool IsEnumeratorCurrentProperty(IPropertySymbol prop)
        => prop.Name == "Current" && IsEnumeratorLikeType(prop.ContainingType);

    private static bool IsEnumeratorLikeType(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named)
            return false;

        static bool IsEnumerator(INamedTypeSymbol t)
            => (t.ContainingNamespace?.ToDisplayString() == "System.Collections" && t.Name == "IEnumerator")
               || (t.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic" && t.Name == "IEnumerator");

        if (IsEnumerator(named))
            return true;

        foreach (var iface in named.AllInterfaces)
        {
            if (IsEnumerator(iface))
                return true;
        }

        return false;
    }
}
