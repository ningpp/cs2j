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
/// Handles identifier and member access expressions.
/// </summary>
[TransformerRegistration]
public class IdentifierExpressionTransformer : IIRExpressionTransformer
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
        ["Byte"]     = ("int",     "Integer"),
        ["SByte"]    = ("byte",    "Byte"),
        ["UInt32"]   = ("int",     "Integer"),
        ["UInt64"]   = ("long",    "Long"),
        ["UInt16"]   = ("short",   "Short"),
        ["Char"]     = ("char",    "Character"),
        ["Boolean"]  = ("bool",    "Boolean"),
    };

    private enum EnclosingInstanceMemberKind
    {
        Property,
        Field
    }

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

    /// <inheritdoc />
    public JavaExpression TransformToIR(ExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;

        // Simple identifier → structured JavaIdentifierExpression
        if (node is IdentifierNameSyntax id)
        {
            // Check for simple cases (local variables, parameters) — not properties/events
            if (context.SemanticModel != null)
            {
                var symbol = context.SemanticModel.GetSymbolInfo(node).Symbol;
                if (symbol is ILocalSymbol or IParameterSymbol)
                {
                    var name = ConversionContext.EscapeJavaKeyword(id.Identifier.Text);
                    // Lambda capture holder: replace references to externally-reassigned
                    // captured variables with holder element access (_varNameCap[0]).
                    if (symbol is ILocalSymbol && context.MethodState.TryGetActiveLambdaCaptureHolder(id.Identifier.Text, out var irHolderName))
                        return new JavaArrayAccessExpression { Target = new JavaIdentifierExpression { Name = irHolderName }, Index = new JavaLiteralExpression { Value = "0" } };
                    return new JavaIdentifierExpression { Name = name };
                }

                // Property read → JavaMethodCallExpression for getter
                if (symbol is IPropertySymbol identProp)
                {
                    bool isLhsOfAssignment = node.Parent is AssignmentExpressionSyntax asgn && asgn.Left == node;
                    if (!isLhsOfAssignment)
                    {
                        var getter = "get" + char.ToUpperInvariant(identProp.Name[0]) + identProp.Name[1..];
                        return new JavaMethodCallExpression { Target = null, MethodName = getter };
                    }
                }

                // Const field → JavaIdentifierExpression
                if (symbol is IFieldSymbol { IsConst: true } constField)
                {
                    var code = Transform(node, context);
                    return new JavaIdentifierExpression { Name = code };
                }
            }
        }

        // Member access → structured IR for fields, properties, enums
        if (node is MemberAccessExpressionSyntax memberAccess)
        {
            if (context.SemanticModel != null)
            {
                var symbol = context.SemanticModel.GetSymbolInfo(memberAccess).Symbol;

                // Non-const field → JavaMemberAccessExpression
                if (symbol is IFieldSymbol { IsConst: false })
                {
                    var target = facade.TransformToIR(memberAccess.Expression, context);
                    var memberName = ConversionContext.EscapeJavaKeyword(memberAccess.Name.Identifier.Text);
                    return new JavaMemberAccessExpression { Target = target, MemberName = memberName };
                }

                // Enum member → JavaMemberAccessExpression
                if (symbol is IFieldSymbol { IsStatic: true, ContainingType.TypeKind: TypeKind.Enum })
                {
                    var code = Transform(node, context);
                    // The string form is "EnumType.MemberName" — split at the last dot
                    var dotIdx = code.LastIndexOf('.');
                    if (dotIdx > 0)
                    {
                        return new JavaMemberAccessExpression
                        {
                            Target = new JavaIdentifierExpression { Name = code[..dotIdx] },
                            MemberName = code[(dotIdx + 1)..]
                        };
                    }
                }

                // Property access on member → prefer structured IR
                if (symbol is IPropertySymbol prop)
                {
                    bool isLhsOfAssignment = node.Parent is AssignmentExpressionSyntax asgnM && asgnM.Left == node;
                    if (!isLhsOfAssignment)
                    {
                        var code = Transform(node, context);
                        // Convert getter calls to JavaMethodCallExpression when possible
                        if (code.EndsWith("()"))
                        {
                            var parenIdx = code.LastIndexOf('(');
                            var callPart = code[..parenIdx];
                            var dotIdx = callPart.LastIndexOf('.');
                            if (dotIdx > 0)
                            {
                                return new JavaMethodCallExpression
                                {
                                    Target = new JavaIdentifierExpression { Name = callPart[..dotIdx] },
                                    MethodName = callPart[(dotIdx + 1)..]
                                };
                            }
                            return new JavaMethodCallExpression { Target = null, MethodName = callPart };
                        }
                        // Fallback for non-method-call patterns (e.g., field access, .length)
                        if (!code.Contains('(') && !code.Contains('[') && !code.Contains(' '))
                        {
                            var dotIdx = code.LastIndexOf('.');
                            if (dotIdx > 0)
                            {
                                return new JavaMemberAccessExpression
                                {
                                    Target = new JavaIdentifierExpression { Name = code[..dotIdx] },
                                    MemberName = code[(dotIdx + 1)..]
                                };
                            }
                        }
                        return new JavaRawExpression(code);
                    }
                }

                // Const field on member → JavaMemberAccessExpression  
                if (symbol is IFieldSymbol { IsConst: true } constMemberField)
                {
                    var code = Transform(node, context);
                    var dotIdx = code.LastIndexOf('.');
                    if (dotIdx > 0)
                    {
                        return new JavaMemberAccessExpression
                        {
                            Target = new JavaIdentifierExpression { Name = code[..dotIdx] },
                            MemberName = code[(dotIdx + 1)..]
                        };
                    }
                }
            }
        }

        // General fallback: prefer structured IR over raw when possible
        var fallbackCode = Transform(node, context);
        if (!fallbackCode.Contains('(') && !fallbackCode.Contains('[') && !fallbackCode.Contains(' '))
        {
            var dotIdx = fallbackCode.LastIndexOf('.');
            if (dotIdx > 0)
            {
                return new JavaMemberAccessExpression
                {
                    Target = new JavaIdentifierExpression { Name = fallbackCode[..dotIdx] },
                    MemberName = fallbackCode[(dotIdx + 1)..]
                };
            }
            return new JavaIdentifierExpression { Name = fallbackCode };
        }
        return new JavaRawExpression(fallbackCode);
    }

    private string TransformIdentifier(IdentifierNameSyntax node, ConversionContext context)
    {
        var name = node.Identifier.Text;

        // Fix 2: resolve LINQ 'let' clause variables inlined via QueryLetAliases
        if (context.QueryLetAliases.TryGetValue(name, out var letAlias))
            return letAlias;

        if (context.TryGetActiveRefHolder(name, out var activeHolderName))
            return $"{activeHolderName}.value";

        // Lambda capture holder: if this variable was captured by a lambda and externally
        // reassigned, replace all references with holder element access (_varName[0]).
        if (context.MethodState.TryGetActiveLambdaCaptureHolder(name, out var captureHolderName))
            return $"{captureHolderName}[0]";

        if (node.Parent is MemberAccessExpressionSyntax receiverAccess
            && receiverAccess.Expression == node
            && TryTransformSimpleIdentifierReceiver(node, receiverAccess, context, out var receiverText))
        {
            return receiverText;
        }

        // Check for using aliases — Fix 5: chain alias resolution through type-registry
        if (context.IsAlias(name))
        {
            var javaType = context.MapAliasToJavaType(name);
            if (javaType != null)
            {
                // When the alias name collides with an instance property on the
                // enclosing type (e.g. "using Label = X;" + "public Label Label {…}"),
                // treat it as an instance member access, not a type reference.
                if (context.CurrentEnclosingRoslynType?.GetMembers(name)
                        .Any(m => m is IPropertySymbol) == true)
                {
                    var getter = "get" + char.ToUpperInvariant(name[0]) + name[1..];
                    return $"{getter}()";
                }

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
                // C# IEnumerator.Current is a stable read after MoveNext().
                if (identProp.Name == "Current" && IsEnumeratorLikeType(identProp.ContainingType))
                    return "getCurrent()";

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
        // Exception: read-only ref struct parameters are generated without ObjectHolder — use as-is.
        if (context.SemanticModel?.GetSymbolInfo(node).Symbol is IParameterSymbol outParam
            && (outParam.RefKind == RefKind.Out || outParam.RefKind == RefKind.Ref)
            && !context.IsReadOnlyRefStructParam(outParam.Name))
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

        // Standalone identifier that resolves to a type but matches an instance
        // property on the enclosing type (e.g. "using Label = X;" colliding with
        // "public Label Label {…}" used in a comparison "Label == null").
        // The member-access guard above only handles MemberAccessExpression parents.
        if (context.SemanticModel?.GetSymbolInfo(node).Symbol is ITypeSymbol)
        {
            var enclosingType = context.CurrentEnclosingRoslynType;
            if (enclosingType != null
                && enclosingType.GetMembers(name).Any(m => m is IPropertySymbol))
            {
                var getter = "get" + char.ToUpperInvariant(name[0]) + name[1..];
                return $"{getter}()";
            }
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
            "byte" => "int",
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
        var memberName = node.Name.Identifier.Text;
        string? staticTypeTarget = null;

        // Extract receiver type early for fallback property resolution.
        // Try multiple approaches: GetTypeInfo first, then GetSymbolInfo on the expression.
        ITypeSymbol? receiverType = null;
        if (context.SemanticModel != null && node.Expression is not TypeSyntax)
        {
            receiverType = context.SemanticModel.GetTypeInfo(node.Expression).Type;
            if (receiverType == null)
            {
                var exprSym = context.SemanticModel.GetSymbolInfo(node.Expression).Symbol;
                receiverType = exprSym switch
                {
                    ILocalSymbol ls => ls.Type,
                    IFieldSymbol fs => fs.Type,
                    IParameterSymbol ps => ps.Type,
                    IPropertySymbol prs => prs.Type,
                    _ => null
                };
            }
            // Last resort: try to resolve the receiver by its name in the enclosing scope
            if (receiverType == null && node.Expression is IdentifierNameSyntax id)
            {
                var name = id.Identifier.Text;
                // Search the enclosing method's locals and parameters
                var enclosingSym = context.SemanticModel.GetEnclosingSymbol(node.SpanStart);
                if (enclosingSym is IMethodSymbol method)
                {
                    foreach (var p in method.Parameters)
                        if (p.Name == name) { receiverType = p.Type; break; }
                }
                // Search the enclosing type's fields and properties
                if (receiverType == null && enclosingSym?.ContainingType is INamedTypeSymbol enclosingType)
                {
                    foreach (var m in enclosingType.GetMembers(name))
                    {
                        if (m is IFieldSymbol f) { receiverType = f.Type; break; }
                        if (m is IPropertySymbol fp) { receiverType = fp.Type; break; }
                    }
                }
                // Search foreach iteration variables
                if (receiverType == null)
                {
                    // Try to find the symbol in the semantic model's lookup
                    var lookupSymbols = context.SemanticModel.LookupSymbols(node.SpanStart, name: name);
                    foreach (var s in lookupSymbols)
                    {
                        receiverType = s switch
                        {
                            ILocalSymbol ls => ls.Type,
                            IParameterSymbol ps => ps.Type,
                            IFieldSymbol fs => fs.Type,
                            IPropertySymbol ps2 => ps2.Type,
                            _ => null
                        };
                        if (receiverType != null) break;
                    }
                }
                // VarTypeMap fallback: pre-scanned var-declared local types
                if ((receiverType == null || receiverType.TypeKind == TypeKind.Error)
                    && context.VarTypeMap.TryGetValue(id.Identifier.Text, out var mappedType)
                    && mappedType.TypeKind != TypeKind.Error)
                {
                    receiverType = mappedType;
                }
            }
        }

            // Strategy 5: Unwrap parenthesized and cast expressions.
            // e.g. ((ICurve)obj).Start → get type of obj cast to ICurve
            if (receiverType == null || receiverType.TypeKind == TypeKind.Error)
            {
                var inner = node.Expression;
                while (inner is ParenthesizedExpressionSyntax paren)
                    inner = paren.Expression;
                if (inner is CastExpressionSyntax cast)
                {
                    var castType = context.SemanticModel?.GetTypeInfo(cast.Type).Type;
                    if (castType != null && castType.TypeKind != TypeKind.Error)
                        receiverType = castType;
                }
            }

            // Strategy 6: Resolve 'this' and 'base' expressions to the enclosing type.
            if (receiverType == null || receiverType.TypeKind == TypeKind.Error)
            {
                if (node.Expression is ThisExpressionSyntax)
                {
                    var enclosingSym = context.SemanticModel?.GetEnclosingSymbol(node.SpanStart);
                    receiverType = enclosingSym?.ContainingType;
                }
                else if (node.Expression is BaseExpressionSyntax)
                {
                    var enclosingSym = context.SemanticModel?.GetEnclosingSymbol(node.SpanStart);
                    receiverType = enclosingSym?.ContainingType?.BaseType;
                }
            }

            // Strategy 7: For MemberAccessExpressionSyntax receiver (chained access),
            // recursively resolve the type of the inner member.
            // e.g. obj.Property.Start → resolve type of obj.Property first
            if (receiverType == null || receiverType.TypeKind == TypeKind.Error)
            {
                if (node.Expression is MemberAccessExpressionSyntax innerMemberAccess)
                {
                    var innerTypeInfo = context.SemanticModel?.GetTypeInfo(innerMemberAccess).Type;
                    if (innerTypeInfo != null && innerTypeInfo.TypeKind != TypeKind.Error)
                        receiverType = innerTypeInfo;
                }
            }

            // Strategy 8: For IdentifierNameSyntax that looks like a type name (starts
            // with uppercase), try the compilation's GetTypeByMetadataName.
            if (receiverType == null || receiverType.TypeKind == TypeKind.Error)
            {
                if (node.Expression is IdentifierNameSyntax typeId
                    && typeId.Identifier.Text.Length > 0
                    && char.IsUpper(typeId.Identifier.Text[0])
                    && context.ProjectCompilation != null)
                {
                    // Try current namespace + name, then bare name
                    var currentNs = context.CurrentNamespace ?? "";
                    var qualifiedName = string.IsNullOrEmpty(currentNs)
                        ? typeId.Identifier.Text
                        : $"{currentNs}.{typeId.Identifier.Text}";
                    receiverType = context.ProjectCompilation.GetTypeByMetadataName(qualifiedName)
                        ?? context.ProjectCompilation.GetTypeByMetadataName(typeId.Identifier.Text);
                }
            }

        string? instanceReceiverTarget = null;
        if (node.Expression is IdentifierNameSyntax simpleReceiver
            && TryTransformSimpleIdentifierReceiver(simpleReceiver, node, context, out var simpleReceiverText))
        {
            instanceReceiverTarget = simpleReceiverText;
        }
        else if (ExpressionTransformerHelpers.TryGetStaticTypeReceiverJavaReference(
            node.Expression,
            context,
            boxJavaPrimitiveType: true,
            out var staticReceiver,
            out _))
        {
            staticTypeTarget = staticReceiver;
        }

        // Fix: Generic type static member access — C# allows Set<T>.Method() but Java requires Set.Method().
        // Strip type arguments from the receiver whenever it is a generic name expression.
        if (node.Expression is GenericNameSyntax genericExprName && staticTypeTarget == null)
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

            if (primTypeSyntax.Keyword.Text == "string" && rawMember == "Empty")
                return "\"\"";

            var mappedMember = MapPrimitiveStaticFieldName(primTypeSyntax.Keyword.Text, rawMember);
            // If the mapping already produced a self-contained expression (e.g. "(-Double.MAX_VALUE)")
            // don't prefix it with the boxed type name — that would create "Double.(-Double.MAX_VALUE)".
            if (mappedMember.StartsWith("(") || mappedMember.StartsWith("-")
                || char.IsDigit(mappedMember[0]))
                return mappedMember;
            return $"{boxedName}.{mappedMember}";
        }

        if (ExpressionTransformerHelpers.TryFormatEnumMemberAccess(
            node,
            context,
            useUnqualifiedRegularEnumInSwitchLabel: false,
            out var formattedEnumMemberAccess))
        {
            return formattedEnumMemberAccess;
        }

        var transformedExpr = instanceReceiverTarget ?? facade.Transform(node.Expression, context);
        var target = staticTypeTarget ?? transformedExpr;
        // Strip type arguments from type qualifiers — Java forbids Type<T>.member().
        target = ExpressionTransformerHelpers.StripTypeArguments(target);

        if (target == "String" && memberName == "Empty")
            return "\"\"";

        // Fix: Stopwatch.Frequency → 1_000_000_000L (Java uses nanosecond precision via System.nanoTime)
        if (memberName == "Frequency"
            && ExpressionTransformerHelpers.StaticReceiverMatches(
                node.Expression,
                context,
                "Stopwatch",
                "System.Diagnostics.Stopwatch"))
        {
            return "1_000_000_000L";
        }

        // Fallback for unresolved method-group symbol: Parallel.Invoke used as delegate value.
        if (memberName == "Invoke"
            && ExpressionTransformerHelpers.StaticReceiverMatches(
                node.Expression,
                context,
                "Parallel",
                "System.Threading.Tasks.Parallel"))
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
            {
                ExpressionTransformerHelpers.AddImportForMappedHelperMethod(mapped, context);
                javaMethodName = mapped;
            }

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
        // Auto-properties emitted as public fields in project pipeline: skip getter
        if (memberName == "AlgorithmData") return $"{target}.AlgorithmData";

        if (context.SemanticModel?.GetSymbolInfo(node).Symbol is IPropertySymbol prop)
        {
            if (prop.Name == "Current" && IsEnumeratorCurrentProperty(prop))
                return $"{target}.getCurrent()";

            var propContainer = prop.ContainingType;
            bool isGenericDictionaryLike =
                propContainer?.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic"
                && propContainer.Name is "Dictionary" or "SortedDictionary" or "IDictionary" or "IReadOnlyDictionary";

            if (isGenericDictionaryLike)
            {
                if (prop.Name == "Values") return $"{target}.values()";
                if (prop.Name == "Keys") return $"{target}.keySet()";
            }

            // KeyValuePair<K,V>.Key/.Value → Map.Entry<K,V>.getKey()/.getValue()
            if (propContainer?.Name == "KeyValuePair"
                && propContainer?.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic"
                && prop.Name is "Key" or "Value")
            {
                return prop.Name == "Key" ? $"{target}.getKey()" : $"{target}.getValue()";
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
            // Interface hierarchy fallback: when the containing type itself has no mapping,
            // check its implemented interfaces (e.g. IReadOnlyCollection<T> → .Count → size).
            // Roslyn may resolve a property through any interface in the hierarchy, so we must
            // walk AllInterfaces to find a matching TypeMappings entry.
            if (mappedMethod == null && prop.ContainingType.AllInterfaces.Length > 0)
            {
                foreach (var iface in prop.ContainingType.AllInterfaces)
                {
                    mappedMethod = context.TypeMappings.MapMethod(iface.ToDisplayString(), prop.Name);
                    if (mappedMethod == null)
                        mappedMethod = context.TypeMappings.MapMethod(
                            $"{iface.ContainingNamespace}.{iface.Name}", prop.Name);
                    if (mappedMethod != null)
                        break;
                }
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
                    ExpressionTransformerHelpers.AddImportForMappedHelperMethod(mappedMethod, context);
                    if (mappedMethod == "getValues")
                        return $"{target}.values()";
                    if (mappedMethod == "getKeys")
                        return $"{target}.keySet()";
                    if (IsJavaFieldMapping(mappedMethod))
                        return mappedMethod.Contains('.') ? mappedMethod : $"{target}.{mappedMethod}";
                    if (ExpressionTransformerHelpers.IsMappedCompatibilityHelperMethod(mappedMethod))
                        return prop.IsStatic || IsStaticReceiverExpression(node.Expression, context)
                            ? $"{mappedMethod}()"
                            : $"{mappedMethod}({target})";

                    // If the mapped value is a fully-qualified Java field (contains a dot, e.g.
                // "java.util.Locale.ROOT") emit it directly without a receiver prefix or ().
                // For array.length: Java arrays expose length as a public final field, not a
                // method — emit without parentheses.
                // Otherwise it is a method name (e.g. "size") — emit as target.method().
                if (mappedMethod.Contains('.'))
                    return mappedMethod;
                if (prop.ContainingType.SpecialType == SpecialType.System_Array)
                {
                    return $"{target}.{mappedMethod}";
                }
                return $"{target}.{mappedMethod}()";
            }

            if (prop.Name == "Position" && IsSystemIoStreamType(prop.ContainingType))
                return $"{target}.getPosition()";

            if (prop.Name == "Length" && IsSystemIoStreamType(prop.ContainingType))
                return $"{target}.getLength()";

            // Fix 2: no mapping configured — generate getXxx() for read accesses
            bool isLhsOfAssignment = node.Parent is AssignmentExpressionSyntax assign && assign.Left == node;
            if (!isLhsOfAssignment)
            {
                if (prop.Name == "Length" && IsSystemStringType(prop.ContainingType))
                    return $"{target}.length()";

                // For anonymous types synthesized as Java records, use camelCase accessor (e.g. id() not getId())
                if (prop.ContainingType.IsAnonymousType
                    && context.Options.UseRecords && context.Options.TargetJavaVersion >= JavaVersion.Java25)
                {
                    var recordAccessor = char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..];
                    return $"{target}.{recordAccessor}()";
                }
                // C# IEnumerator.Current is a stable read after MoveNext().
                if (prop.Name == "Current"
                    && (IsEnumeratorLikeType(prop.ContainingType)
                        || IsEnumeratorLikeType(receiverType)
                        || (receiverType != null
                            && context.TypeMappings.MapType(receiverType.ToDisplayString()) is "Iterator" or "Iterator<T>")))
                    return $"{target}.getCurrent()";

                var getter = "get" + char.ToUpperInvariant(prop.Name[0]) + prop.Name[1..];
                return $"{target}.{getter}()";
            }
        }

        // Path C: Per-type property/field resolution via receiverType.GetMembers().
        // This runs immediately after Path A (GetSymbolInfo) fails. It uses the
        // type's own metadata to decide — no global whitelist needed.
        // Only fires for instance access (target starts with lowercase).
        if (target.Length > 0 && char.IsLower(target[0]) && receiverType != null)
        {
            var resolved = TryResolvePropertyByType(memberName, target, receiverType, context);
            if (resolved != null)
                return resolved;
        }

        // Fix 3: GetSymbolInfo returned no IPropertySymbol (e.g. lambda param in LINQ-rewritten tree).
        // Fall back to GetTypeInfo on the receiver expression for TypeMappings lookup.
        if (context.SemanticModel != null)
        {
            var exprType = context.SemanticModel.GetTypeInfo(node.Expression).Type;
            if (exprType != null)
            {
                if (memberName == "Current" && IsEnumeratorLikeType(exprType))
                    return $"{target}.getCurrent()";

                if (memberName == "Length" && IsSystemStringType(exprType))
                    return $"{target}.length()";

                // KeyValuePair<K,V>.Key/.Value → getKey()/getValue()
                if (exprType is INamedTypeSymbol kvpType
                    && kvpType.Name == "KeyValuePair"
                    && kvpType.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic"
                    && memberName is "Key" or "Value")
                {
                    return memberName == "Key" ? $"{target}.getKey()" : $"{target}.getValue()";
                }

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
                // Interface hierarchy fallback for Fix 3 path
                if (mm3 == null && exprType is INamedTypeSymbol namedForIface)
                {
                    foreach (var iface in namedForIface.AllInterfaces)
                    {
                        mm3 = context.TypeMappings.MapMethod(iface.ToDisplayString(), memberName);
                        if (mm3 == null)
                            mm3 = context.TypeMappings.MapMethod(
                                $"{iface.ContainingNamespace}.{iface.Name}", memberName);
                        if (mm3 != null) break;
                    }
                }
                if (mm3 != null)
                {
                    ExpressionTransformerHelpers.AddImportForMappedHelperMethod(mm3, context);
                    if (mm3 == "getValues") return $"{target}.values()";
                    if (mm3 == "getKeys") return $"{target}.keySet()";
                    if (IsJavaFieldMapping(mm3)) return mm3.Contains('.') ? mm3 : $"{target}.{mm3}";
                    if (ExpressionTransformerHelpers.IsMappedCompatibilityHelperMethod(mm3))
                        return IsStaticReceiverExpression(node.Expression, context) ? $"{mm3}()" : $"{mm3}({target})";
                    return mm3.Contains('.') ? mm3 : $"{target}.{mm3}()";
                }

                // Generic fallback: Array.Length → .length (field), collection.Count → .size()
                if (exprType.TypeKind == TypeKind.Array && memberName == "Length")
                    return $"{target}.length";
                if (memberName == "Count" && exprType is INamedTypeSymbol namedExpr)
                {
                    foreach (var iface in namedExpr.AllInterfaces)
                    {
                        var ifaceDisplay = iface.ToDisplayString();
                        if (ifaceDisplay.StartsWith("System.Collections.Generic.ICollection") ||
                            ifaceDisplay.StartsWith("System.Collections.Generic.IList") ||
                            ifaceDisplay.StartsWith("System.Collections.Generic.IReadOnlyCollection") ||
                            ifaceDisplay.StartsWith("System.Collections.Generic.ISet") ||
                            ifaceDisplay == "System.Collections.ICollection" ||
                            ifaceDisplay == "System.Collections.IList")
                            return $"{target}.size()";
                    }
                }
                // Array.Length on arrays that weren't caught by the TypeKind check
                if (memberName == "Length" && exprType is IArrayTypeSymbol)
                    return $"{target}.length";

                if (memberName == "Position" && IsSystemIoStreamType(exprType))
                    return $"{target}.getPosition()";
                if (memberName == "Length" && IsSystemIoStreamType(exprType))
                    return $"{target}.getLength()";

            }
        }

        // Fix: C# boxed class-name static constants — Double.MaxValue → Double.MAX_VALUE,
        // Int32.MaxValue → Integer.MAX_VALUE, Single.MaxValue → Float.MAX_VALUE, etc.
        // The PredefinedTypeSyntax path above handles keyword forms (e.g. 'double.MaxValue'),
        // but when code uses the class name form the receiver is an IdentifierNameSyntax.
        if (node.Expression is IdentifierNameSyntax { Identifier.Text: var boxedIdText }
            && _csharpBoxedClassNames.TryGetValue(boxedIdText, out var primInfo))
        {
            if (TryMapPrimitiveStaticFieldName(primInfo.keyword, memberName, out var mappedConst))
            {
                // Some mappings return self-contained expressions like "(-Double.MAX_VALUE)".
                if (mappedConst.StartsWith("(") || mappedConst.StartsWith("-")
                    || char.IsDigit(mappedConst[0]))
                    return mappedConst;
                return $"{primInfo.javaWrapper}.{mappedConst}";
            }
        }

        if (memberName == "Current") return $"{target}.getCurrent()";
        if (memberName == "Values") return $"{target}.values()";
        if (memberName == "Keys") return $"{target}.keySet()";

        // Last-resort generic fallback for known C#→Java property mappings
        if (memberName == "Count") return $"{target}.size()";
        if (memberName == "Position" && IsSystemIoStreamType(receiverType)) return $"{target}.getPosition()";
        if (memberName == "Length" && IsSystemIoStreamType(receiverType)) return $"{target}.getLength()";
        if (memberName == "Length" && IsSystemStringType(receiverType)) return $"{target}.length()";
        if (memberName == "Length") return $"{target}.length";
        // Map.Entry Key/Value (from C# KeyValuePair<K,V>)
        // Only apply when receiver type is CONFIRMED to be KeyValuePair/IGrouping/Map.Entry.
        // Do NOT apply as a best-effort guess when receiverType is unknown (null) — many
        // types have Key/Value FIELDS (not properties), and getKey()/getValue() would
        // be incorrect for them (e.g. PointSet.Key, a double field).
        if ((memberName == "Key" || memberName == "Value")
            && IsKeyValuePairLikeType(receiverType))
        {
            return memberName == "Key" ? $"{target}.getKey()" : $"{target}.getValue()";
        }
        // ValueTuple Item1/Item2 → Map.Entry.getKey()/getValue()
        // (the converter maps C# ValueTuple<K,V> to Java Map.Entry<K,V>)
        if ((memberName == "Item1" || memberName == "Item2")
            && IsKeyValuePairLikeType(receiverType))
        {
            return memberName == "Item1" ? $"{target}.getKey()" : $"{target}.getValue()";
        }
        // Auto-properties emitted as fields in project pipeline
        if (memberName == "AlgorithmData") return $"{target}.AlgorithmData";
        // System.Globalization.CultureInfo.InvariantCulture → java.util.Locale.ROOT
        if (memberName == "InvariantCulture" && target.EndsWith(".CultureInfo"))
        {
            context.AddImport("java.util.Locale");
            return "Locale.ROOT";
        }
        // Phase 1 diagnostic: log why we fell through to raw field access
        if (memberName != "AlgorithmData") // AlgorithmData is intentionally a field
        {
            var reasonA = context.SemanticModel == null ? "SemanticModel null"
                : context.SemanticModel.GetSymbolInfo(node).Symbol switch
                {
                    null => "GetSymbolInfo returned null",
                    IFieldSymbol => $"Symbol was IFieldSymbol({memberName})",
                    IMethodSymbol => $"Symbol was IMethodSymbol({memberName})",
                    var s => $"Symbol was {s?.Kind}({memberName})"
                };
            var reasonReceiverType = receiverType == null ? "receiverType null"
                : receiverType.TypeKind == TypeKind.Error ? "receiverType Error"
                : $"receiverType={receiverType.ToDisplayString()}, GetMembers returned no IPropertySymbol for '{memberName}'";
            context.Diagnostics.Info(
                $"Property fallback: '{node}' -> {target}.{memberName} | PathA: {reasonA} | PathC: {reasonReceiverType}",
                node.GetLocation());
        }

        var member = ConversionContext.EscapeJavaKeyword(memberName);
        return $"{target}.{member}";
    }

    /// <summary>
    /// Resolves a simple identifier when it is used as the receiver in a member
    /// access. This is the central tie-breaker for identifiers that may be both a
    /// type name and an instance property/field on the enclosing type.
    /// </summary>
    private static bool TryTransformSimpleIdentifierReceiver(
        IdentifierNameSyntax receiver,
        MemberAccessExpressionSyntax memberAccess,
        ConversionContext context,
        out string transformedReceiver)
    {
        transformedReceiver = string.Empty;

        var receiverName = receiver.Identifier.Text;
        var accessedSymbol = GetAccessedMemberSymbol(memberAccess, context);

        if (IsStaticMemberSymbol(accessedSymbol))
        {
            var receiverSymbol = GetPreferredIdentifierSymbol(receiver, context, preferInstanceCandidate: false);
            if (receiverSymbol is IAliasSymbol { Target: INamedTypeSymbol aliasedType })
            {
                transformedReceiver = MapStaticTypeReceiver(aliasedType, context);
                return true;
            }

            if (receiverSymbol is ITypeSymbol typeSymbol)
            {
                transformedReceiver = MapStaticTypeReceiver(typeSymbol, context);
                return true;
            }

            if (receiverSymbol is IPropertySymbol { Type: INamedTypeSymbol propertyType })
            {
                transformedReceiver = MapStaticTypeReceiver(propertyType, context);
                return true;
            }

            if (context.IsAlias(receiverName))
            {
                var aliasType = context.MapAliasToJavaType(receiverName);
                if (!string.IsNullOrWhiteSpace(aliasType))
                {
                    transformedReceiver = MapAliasTypeReceiver(aliasType, context);
                    return true;
                }
            }

            if (ExpressionTransformerHelpers.TryGetStaticTypeReceiverJavaReference(
                receiver,
                context,
                boxJavaPrimitiveType: true,
                out var staticReceiver,
                out _))
            {
                transformedReceiver = staticReceiver;
                return true;
            }

            return false;
        }

        var preferredSymbol = GetPreferredIdentifierSymbol(receiver, context, preferInstanceCandidate: true);
        if (accessedSymbol == null
            && preferredSymbol is IPropertySymbol { Type: INamedTypeSymbol unresolvedPropertyType })
        {
            var memberName = memberAccess.Name.Identifier.Text;
            if (HasStaticMember(unresolvedPropertyType, memberName)
                || IsLikelyStaticReceiverFromStaticContext(receiver, unresolvedPropertyType, context))
            {
                transformedReceiver = MapStaticTypeReceiver(unresolvedPropertyType, context);
                return true;
            }
        }

        if (preferredSymbol is ILocalSymbol or IParameterSymbol)
            return false;

        if (preferredSymbol is IPropertySymbol propertySymbol && !IsAssignmentLeftHandSide(receiver))
        {
            if (propertySymbol.Name == "Current" && IsEnumeratorLikeType(propertySymbol.ContainingType))
                transformedReceiver = "getCurrent()";
            else
                transformedReceiver = GetterCall(propertySymbol.Name);
            return true;
        }

        if (preferredSymbol is IFieldSymbol { IsStatic: false })
        {
            transformedReceiver = ConversionContext.EscapeJavaKeyword(receiverName);
            return true;
        }

        if (TryFindEnclosingInstanceMember(receiver, context, out var memberKind))
        {
            transformedReceiver = memberKind == EnclosingInstanceMemberKind.Property
                ? GetterCall(receiverName)
                : ConversionContext.EscapeJavaKeyword(receiverName);
            return true;
        }

        return false;
    }

    private static ISymbol? GetAccessedMemberSymbol(MemberAccessExpressionSyntax memberAccess, ConversionContext context)
    {
        var symbolInfo = context.SemanticModel?.GetSymbolInfo(memberAccess);
        if (symbolInfo == null)
            return null;

        return symbolInfo.Value.Symbol
            ?? symbolInfo.Value.CandidateSymbols.FirstOrDefault(IsStaticMemberSymbol)
            ?? symbolInfo.Value.CandidateSymbols.FirstOrDefault();
    }

    private static ISymbol? GetPreferredIdentifierSymbol(
        IdentifierNameSyntax identifier,
        ConversionContext context,
        bool preferInstanceCandidate)
    {
        var symbolInfo = context.SemanticModel?.GetSymbolInfo(identifier);
        if (symbolInfo == null)
            return null;

        if (preferInstanceCandidate && symbolInfo.Value.CandidateSymbols.Length > 1)
        {
            var nonTypeCandidate = symbolInfo.Value.CandidateSymbols.FirstOrDefault(
                s => s is ILocalSymbol or IParameterSymbol or IFieldSymbol or IPropertySymbol
                     or IEventSymbol or IMethodSymbol);
            if (nonTypeCandidate != null)
                return nonTypeCandidate;
        }

        return symbolInfo.Value.Symbol ?? symbolInfo.Value.CandidateSymbols.FirstOrDefault();
    }

    private static bool IsStaticMemberSymbol(ISymbol? symbol)
        => symbol is IMethodSymbol { IsStatic: true }
            or IPropertySymbol { IsStatic: true }
            or IFieldSymbol { IsStatic: true };

    private static bool IsJavaFieldMapping(string mappedMember)
        => mappedMember is "out" or "err" or "in"
            || mappedMember.EndsWith(".out", StringComparison.Ordinal)
            || mappedMember.EndsWith(".err", StringComparison.Ordinal)
            || mappedMember.EndsWith(".in", StringComparison.Ordinal);

    private static bool IsAssignmentLeftHandSide(ExpressionSyntax expression)
        => expression.Parent is AssignmentExpressionSyntax assignment && assignment.Left == expression;

    private static string GetterCall(string propertyName)
        => $"get{char.ToUpperInvariant(propertyName[0])}{propertyName[1..]}()";

    private static string MapStaticTypeReceiver(ITypeSymbol typeSymbol, ConversionContext context)
        => ExpressionTransformerHelpers.StripTypeArguments(context.MapType(typeSymbol));

    private static bool IsStaticReceiverExpression(ExpressionSyntax expression, ConversionContext context)
        => ExpressionTransformerHelpers.TryGetStaticReceiverType(expression, context, out _);

    private static string MapAliasTypeReceiver(string javaType, ConversionContext context)
    {
        if (!javaType.Contains('.'))
        {
            var remapped = context.TypeMappings.MapType(javaType);
            if (remapped != javaType)
                javaType = remapped;
        }

        return ExpressionTransformerHelpers.StripTypeArguments(javaType);
    }

    private static bool TryFindEnclosingInstanceMember(
        IdentifierNameSyntax identifier,
        ConversionContext context,
        out EnclosingInstanceMemberKind memberKind)
    {
        var name = identifier.Identifier.Text;

        if (TryFindInstanceMemberInType(context.CurrentEnclosingRoslynType, name, out memberKind))
            return true;

        var enclosingType = context.SemanticModel?.GetEnclosingSymbol(identifier.SpanStart)?.ContainingType;
        if (TryFindInstanceMemberInType(enclosingType, name, out memberKind))
            return true;

        var enclosingTypeDecl = identifier.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (enclosingTypeDecl != null)
        {
            if (enclosingTypeDecl.Members
                .OfType<PropertyDeclarationSyntax>()
                .Any(p => p.Identifier.Text == name && !p.Modifiers.Any(SyntaxKind.StaticKeyword)))
            {
                memberKind = EnclosingInstanceMemberKind.Property;
                return true;
            }

            if (enclosingTypeDecl.Members
                .OfType<FieldDeclarationSyntax>()
                .Where(f => !f.Modifiers.Any(SyntaxKind.StaticKeyword))
                .SelectMany(f => f.Declaration.Variables)
                .Any(v => v.Identifier.Text == name))
            {
                memberKind = EnclosingInstanceMemberKind.Field;
                return true;
            }
        }

        memberKind = default;
        return false;
    }

    private static bool TryFindInstanceMemberInType(
        INamedTypeSymbol? type,
        string name,
        out EnclosingInstanceMemberKind memberKind)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            if (current.GetMembers(name).Any(m => m is IPropertySymbol { IsStatic: false }))
            {
                memberKind = EnclosingInstanceMemberKind.Property;
                return true;
            }

            if (current.GetMembers(name).Any(m => m is IFieldSymbol { IsStatic: false }))
            {
                memberKind = EnclosingInstanceMemberKind.Field;
                return true;
            }
        }

        memberKind = default;
        return false;
    }

    private static bool HasStaticMember(INamedTypeSymbol type, string memberName)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            if (current.GetMembers(memberName).Any(
                m => m.IsStatic && m is (IMethodSymbol or IPropertySymbol or IFieldSymbol)))
            {
                return true;
            }
        }

        return type.GetTypeMembers().Any(t => t.Name == memberName && t.IsStatic);
    }

    private static bool IsLikelyStaticReceiverFromStaticContext(
        IdentifierNameSyntax receiver,
        INamedTypeSymbol receiverType,
        ConversionContext context)
    {
        if (!string.Equals(receiver.Identifier.Text, receiverType.Name, StringComparison.Ordinal))
            return false;

        var enclosingMethod = context.SemanticModel?.GetEnclosingSymbol(receiver.SpanStart);
        return enclosingMethod is IMethodSymbol { IsStatic: true };
    }

    /// <summary>
    /// Per-type property/field resolution via GetMembers(). Walks the full type
    /// hierarchy (receiverType + base types + all interfaces) to determine whether
    /// the member name is a property (needs getter) or a field (access directly).
    /// Returns null when the type cannot be resolved or the member is not found.
    /// </summary>
    private string? TryResolvePropertyByType(
        string memberName,
        string target,
        ITypeSymbol receiverType,
        ConversionContext context)
    {
        if (receiverType.TypeKind == TypeKind.Error)
            return null;

        // Array: Length is a property in C# but a field in Java
        if (receiverType.TypeKind == TypeKind.Array)
        {
            if (memberName == "Length")
                return $"{target}.length";
            return null;
        }

        // Type parameter: check constraint types
        if (receiverType is ITypeParameterSymbol typeParam)
        {
            foreach (var constraint in typeParam.ConstraintTypes)
            {
                var result = TryResolvePropertyByType(memberName, target, constraint, context);
                if (result != null)
                    return result;
            }
            return null;
        }

        if (receiverType is not INamedTypeSymbol namedReceiver)
            return null;

        if (memberName == "Length" && IsSystemStringType(namedReceiver))
            return $"{target}.length()";

        // Collect all types to check: receiverType + base types + all interfaces
        var typesToCheck = new List<INamedTypeSymbol> { namedReceiver };
        var baseType = namedReceiver.BaseType;
        while (baseType != null)
        {
            typesToCheck.Add(baseType);
            baseType = baseType.BaseType;
        }
        typesToCheck.AddRange(namedReceiver.AllInterfaces);

        // Walk each type in the hierarchy looking for the member
        foreach (var typeSymbol in typesToCheck)
        {
            foreach (var m in typeSymbol.GetMembers(memberName))
            {
                if (m is IPropertySymbol foundProp)
                {
                    // Check TypeMappings for a configured method name override
                    var typeName = foundProp.ContainingType.ToDisplayString();
                    var mapped = context.TypeMappings.MapMethod(typeName, memberName);
                    if (mapped == null && foundProp.ContainingType.ContainingNamespace != null)
                        mapped = context.TypeMappings.MapMethod(
                            $"{foundProp.ContainingType.ContainingNamespace}.{foundProp.ContainingType.Name}", memberName);
                    // Walk AllInterfaces for TypeMappings matches too
                    if (mapped == null)
                    {
                        foreach (var iface in foundProp.ContainingType.AllInterfaces)
                        {
                            mapped = context.TypeMappings.MapMethod(iface.ToDisplayString(), memberName);
                            if (mapped == null)
                                mapped = context.TypeMappings.MapMethod(
                                    $"{iface.ContainingNamespace}.{iface.Name}", memberName);
                            if (mapped != null) break;
                        }
                    }
                    if (mapped != null)
                    {
                        ExpressionTransformerHelpers.AddImportForMappedHelperMethod(mapped, context);
                        if (mapped == "getValues") return $"{target}.values()";
                        if (mapped == "getKeys") return $"{target}.keySet()";
                        if (IsJavaFieldMapping(mapped))
                            return mapped.Contains('.') ? mapped : $"{target}.{mapped}";
                        if (ExpressionTransformerHelpers.IsMappedCompatibilityHelperMethod(mapped))
                            return foundProp.IsStatic
                                ? $"{mapped}()"
                                : $"{mapped}({target})";
                        if (mapped.StartsWith("get", StringComparison.Ordinal)
                            && mapped.Length > 3
                            && memberName == mapped[3..])
                            return $"{target}.{mapped}()";
                        return mapped.Contains('.') ? mapped : $"{target}.{mapped}()";
                    }

                    if (memberName == "Position" && IsSystemIoStreamType(foundProp.ContainingType))
                        return $"{target}.getPosition()";
                    if (memberName == "Length" && IsSystemIoStreamType(foundProp.ContainingType))
                        return $"{target}.getLength()";

                    if (memberName == "Current"
                        && (IsEnumeratorLikeType(foundProp.ContainingType)
                            || IsEnumeratorLikeType(namedReceiver)))
                        return $"{target}.getCurrent()";

                    // Default: generate getXxx() getter
                    var getter = "get" + char.ToUpperInvariant(memberName[0]) + memberName[1..];
                    return $"{target}.{getter}()";
                }
                if (m is IFieldSymbol { IsStatic: false })
                {
                    // It's a field — just access it directly
                    return $"{target}.{memberName}";
                }
            }
        }

        return null; // member not found in any type
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

    private static bool IsSystemStringType(ITypeSymbol? type)
        => type?.SpecialType == SpecialType.System_String
            || type?.ToDisplayString() is "string" or "System.String";

    /// <summary>
    /// Maps C# primitive-type static field/property names to their Java equivalents.
    /// e.g. double.MaxValue → MAX_VALUE, double.PositiveInfinity → POSITIVE_INFINITY
    /// Note: for double/float, MinValue in C# is the most-negative finite value
    ///       (-MAX_VALUE in Java), not the smallest positive value (Java's MIN_VALUE).
    /// </summary>
    private static string MapPrimitiveStaticFieldName(string primitiveKeyword, string memberName)
        => TryMapPrimitiveStaticFieldName(primitiveKeyword, memberName, out var mappedMember)
            ? mappedMember
            : memberName;

    private static bool TryMapPrimitiveStaticFieldName(string primitiveKeyword, string memberName, out string mappedMember)
    {
        mappedMember = (primitiveKeyword, memberName) switch
        {
            // C# byte (unsigned) MaxValue=255, MinValue=0 — emit literals directly
            ("byte", "MaxValue") => "255",
            ("byte", "MinValue") => "0",
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
            _                        => string.Empty
        };

        return mappedMember.Length > 0;
    }

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
        => prop.Name == "Current" && (IsEnumeratorLikeType(prop.ContainingType) || prop.ExplicitInterfaceImplementations.Any(i => IsEnumeratorLikeType(i.ContainingType)));

    private static bool IsEnumeratorLikeType(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named)
            return false;

        static bool IsEnumerator(INamedTypeSymbol t)
        {
            if (t.Name == "IEnumerator")
            {
                var ns = t.ContainingNamespace?.ToDisplayString();
                if (ns is "System.Collections" or "System.Collections.Generic")
                    return true;
            }
            var display = t.ToDisplayString();
            return display.StartsWith("System.Collections.IEnumerator", StringComparison.Ordinal)
                || display.StartsWith("System.Collections.Generic.IEnumerator", StringComparison.Ordinal);
        }

        if (IsEnumerator(named))
            return true;

        foreach (var iface in named.AllInterfaces)
        {
            if (IsEnumerator(iface))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true if the given type symbol is a KeyValuePair-like type
    /// (KeyValuePair&lt;K,V&gt;, IGrouping&lt;K,V&gt;, or Map.Entry&lt;K,V&gt;)
    /// where Key/Value properties should map to getKey()/getValue().
    /// </summary>
    private static bool IsKeyValuePairLikeType(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named)
            return false;

        var display = named.ToDisplayString();
        if (display.StartsWith("System.Collections.Generic.KeyValuePair<", StringComparison.Ordinal)
            || display.StartsWith("System.Linq.IGrouping<", StringComparison.Ordinal)
            || display.StartsWith("java.util.Map.Entry<", StringComparison.Ordinal))
            return true;

        // Also check the original definition (for KeyValuePair/IGrouping)
        if (named.OriginalDefinition is INamedTypeSymbol original)
        {
            var originalDisplay = original.ToDisplayString();
            return originalDisplay is "System.Collections.Generic.KeyValuePair<TKey, TValue>"
                or "System.Linq.IGrouping<TKey, TElement>"
                or "java.util.Map.Entry<K, V>";
        }

        return false;
    }
}
