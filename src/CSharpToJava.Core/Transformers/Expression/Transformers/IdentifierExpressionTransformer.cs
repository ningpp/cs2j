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
        ["Byte"]     = ("byte",    "Integer"),
        ["SByte"]    = ("sbyte",   "Byte"),
        ["UInt32"]   = ("uint",    "Long"),
        ["UInt64"]   = ("ulong",   "Long"),
        ["UInt16"]   = ("ushort",  "Short"),
        ["Char"]     = ("char",    "Character"),
        ["Boolean"]  = ("bool",    "Boolean"),
        ["Decimal"]  = ("decimal", "Decimal"),
    };

    private enum EnclosingInstanceMemberKind
    {
        Property,
        Field
    }

    /// <summary>
    /// Computes the Java getter method name for a C# property.
    /// When the class property conflicts with an explicit interface implementation
    /// of the same property (same name, different return type), the class accessor is
    /// renamed to avoid a Java overload clash, so callers must use the renamed name.
    /// However, when the containing type implements IEnumerator&lt;T&gt; and the property
    /// is the typed Current, ResolveExplicitInterfacePropertyConflicts will remove the
    /// Object-returning explicit accessor instead, so no $Class suffix is needed.
    /// </summary>
    private static string GetPropertyGetterName(IPropertySymbol propSymbol)
    {
        string baseName = "get" + char.ToUpperInvariant(propSymbol.Name[0]) + propSymbol.Name[1..];

        // If this is the class property and the containing type has an explicit interface
        // implementation of the same property with a different return type, the class
        // accessor will be renamed to baseName + "$Class".
        if (propSymbol.ExplicitInterfaceImplementations.Length == 0
            && propSymbol.ContainingType is INamedTypeSymbol containingType)
        {
            // Special case: IEnumerator<T>.Current maps to getCurrent() on
            // CSharpGenericEnumerator<T> in Java (no $Class suffix). This check must
            // come BEFORE the conflict detection loop because the struct may have
            // IEnumerator.Current (object-returning) as an explicit interface
            // implementation, which would incorrectly trigger $Class renaming.
            // Only applies to generic IEnumerator<T>, not non-generic IEnumerator.
            if (propSymbol.Name == "Current" && ImplementsGenericIEnumerator(containingType))
                return baseName;

            foreach (var member in containingType.GetMembers())
            {
                if (member is IPropertySymbol otherProp
                    && otherProp.ExplicitInterfaceImplementations.Length > 0
                    && otherProp.ExplicitInterfaceImplementations.Any(e => e.Name == propSymbol.Name)
                    && otherProp.Parameters.Length == propSymbol.Parameters.Length
                    && !SymbolEqualityComparer.Default.Equals(otherProp.Type, propSymbol.Type))
                {
                    return baseName + "$Class";
                }
            }

            // Special case: IEnumerator<T>.Current conflict is resolved by
            // ResolveExplicitInterfacePropertyConflicts removing the Object-returning
            // explicit accessor, so the class accessor keeps the standard name.
            // Note: This check must come AFTER the conflict detection above because
            // when the explicit accessor is NOT removed (e.g. for IEnumerator without
            // generic T), the class accessor still needs the $Class suffix.
            if (propSymbol.Name == "Current" && IsEnumeratorLikeType(containingType))
                return baseName;
        }

        return baseName;
    }

    /// <summary>
    /// Returns true if the type implements System.Collections.Generic.IEnumerator&lt;T&gt;.
    /// </summary>
    private static bool ImplementsGenericIEnumerator(INamedTypeSymbol type)
    {
        foreach (var iface in type.AllInterfaces)
        {
            if (iface.IsGenericType
                && iface.Name == "IEnumerator"
                && iface.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic")
                return true;
        }
        return false;
    }

    public string Transform(ExpressionSyntax node, ConversionContext context)
        => node.Kind() switch
        {
            SyntaxKind.IdentifierName => TransformIdentifier((IdentifierNameSyntax)node, context),
            SyntaxKind.PredefinedType => TransformPredefinedType((PredefinedTypeSyntax)node, context),
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
                var symbol = context.GetSymbolInfo(node).Symbol;
                if (symbol is ILocalSymbol or IParameterSymbol)
                {
                    var name = ConversionContext.EscapeJavaKeyword(id.Identifier.Text);
                    // Lambda capture holder: replace references to externally-reassigned
                    // captured variables with holder element access (_varNameCap[0]).
                    if (symbol is ILocalSymbol && context.MethodState.TryGetActiveLambdaCaptureHolder(id.Identifier.Text, out var irHolderName))
                        return new JavaArrayAccessExpression { Target = new JavaIdentifierExpression { Name = irHolderName }, Index = new JavaLiteralExpression { Value = "0" } };
                    return new JavaRawExpression(name, context.MapType(GetSymbolType(symbol)!));
                }

                // Property read → JavaMethodCallExpression for getter
                if (symbol is IPropertySymbol identProp)
                {
                    bool isLhsOfAssignment = node.Parent is AssignmentExpressionSyntax asgn && asgn.Left == node;
                    if (!isLhsOfAssignment)
                    {
                        var getter = GetPropertyGetterName(identProp);
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
                var symbol = context.GetSymbolInfo(memberAccess).Symbol;
                if (symbol is IFieldSymbol primitiveStaticField
                    && TryMapPrimitiveStaticFieldAccess(primitiveStaticField, context, out var primitiveStaticFieldExpression))
                {
                    return primitiveStaticFieldExpression;
                }

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
                        var receiverType = ResolveReceiverType(memberAccess.Expression, context);
                        if (IsSystemArrayLengthOnConcreteArray(prop, receiverType))
                        {
                            var targetCode = Transform(memberAccess.Expression, context);
                            return new JavaRawExpression($"{targetCode}.length", context.MapType(prop.Type));
                        }
                        if (prop.Name == "Length" && IsDeclaredAsConcreteArray(memberAccess.Expression, context))
                        {
                            var targetCode = Transform(memberAccess.Expression, context);
                            return new JavaRawExpression($"{targetCode}.length", context.MapType(prop.Type));
                        }

                        if (prop.Name == "Length" && IsSystemArrayLengthOnCSharpArray(prop, receiverType))
                        {
                            var target = facade.TransformToIR(memberAccess.Expression, context);
                            return new JavaMethodCallExpression
                            {
                                Target = target,
                                MethodName = "getLength",
                            };
                        }
                        if (prop.Name == "Length" && IsDeclaredAsSystemArray(memberAccess.Expression, context))
                        {
                            var target = facade.TransformToIR(memberAccess.Expression, context);
                            return new JavaMethodCallExpression
                            {
                                Target = target,
                                MethodName = "getLength",
                            };
                        }

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

        if (context.TryGetSymbolInfo(node, out var propSymbolResult) && propSymbolResult is IPropertySymbol identProp)
        {
            bool isLhsOfAssignment = node.Parent is AssignmentExpressionSyntax asgn && asgn.Left == node;
            if (!isLhsOfAssignment)
            {
                var getter = GetPropertyGetterName(identProp);
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
        if (context.GetSymbolInfo(node).Symbol is IParameterSymbol outParam
            && (outParam.RefKind == RefKind.Out || outParam.RefKind == RefKind.Ref)
            && !context.IsReadOnlyRefStructParam(outParam.Name))
        {
            return $"{ConversionContext.EscapeJavaKeyword(outParam.Name)}.value";
        }

        // Fix: Handle event references within the same class.
        // C#: ProgressChanged != null  → Java: !_progressChangedListeners.isEmpty()
        // C#: ProgressChanged(...)    → Java: fireProgressChanged(...)
        if (context.GetSymbolInfo(node).Symbol is IEventSymbol eventSym)
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
        if (context.GetSymbolInfo(node).Symbol is IMethodSymbol bareMethodGroup
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
        if (context.GetSymbolInfo(node).Symbol is ITypeSymbol)
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
    private string TransformPredefinedType(PredefinedTypeSyntax node, ConversionContext context)
    {
        var typeName = node.Keyword.Text;

        if (typeName == "decimal")
        {
            context.AddImport("io.github.ningpp.compat.Decimal");
            return "Decimal";
        }

        // Fix 3: generic type arguments require boxed types (e.g., List<Integer> not List<int>)
        if (node.Parent is TypeArgumentListSyntax)
            return ExpressionTransformerHelpers.BoxedTypeName(node);

        // Non-generic context: use Java primitive / value types
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
            var typeInfo = context.GetTypeInfo(typeArg);
            if (typeInfo.Type != null)
            {
                typeArgs.Add(context.MapType(typeInfo.Type));
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
        ITypeSymbol? receiverType = node.Expression is not TypeSyntax
            ? ResolveReceiverType(node.Expression, context)
            : null;
        if ((receiverType == null || receiverType.TypeKind == TypeKind.Error)
            && context.SemanticModel != null && node.Expression is not TypeSyntax)
        {
            if (receiverType == null)
            {
                var exprSym = context.GetSymbolInfo(node.Expression).Symbol;
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
            if (receiverType == null && node.Expression is IdentifierNameSyntax idExpr)
            {
                var name = idExpr.Identifier.Text;
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
                    && context.VarTypeMap.TryGetValue(idExpr.Identifier.Text, out var mappedType)
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
                    var castType = context.GetTypeInfo(cast.Type).Type;
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
                    var innerTypeInfo = context.GetTypeInfo(innerMemberAccess).Type;
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
            // When the receiver was resolved as an instance property/field, also set receiverType
            // so that subsequent property resolution paths can work. Without this, receiverType
            // stays null because IdentifierNameSyntax is a TypeSyntax, causing the initial
            // resolution at line ~515 to skip it.
            if (receiverType == null || receiverType.TypeKind == TypeKind.Error)
            {
                var preferredSym = GetPreferredIdentifierSymbol(simpleReceiver, context, preferInstanceCandidate: true);
                if (preferredSym is IPropertySymbol instProp)
                    receiverType = instProp.Type;
                else if (preferredSym is IFieldSymbol instField)
                    receiverType = instField.Type;
            }
        }
        if (node.Expression is IdentifierNameSyntax simpleValueReceiver
            && instanceReceiverTarget == null
            && (receiverType == null || receiverType.TypeKind == TypeKind.Error))
        {
            if (context.LocalTypeOverrides.TryGetValue(simpleValueReceiver.Identifier.Text, out var overrideType)
                && overrideType.TypeKind != TypeKind.Error)
            {
                receiverType = overrideType;
            }
            else
            {
                var preferredSym = GetPreferredIdentifierSymbol(simpleValueReceiver, context, preferInstanceCandidate: true);
                if (preferredSym is ILocalSymbol or IParameterSymbol)
                    receiverType = GetSymbolType(preferredSym);
            }
        }
        if (instanceReceiverTarget == null
            && ExpressionTransformerHelpers.TryGetStaticTypeReceiverJavaReference(
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

            // Check if this is a method group (e.g. char.IsHighSurrogate used as a delegate value).
            // If so, generate a Java method reference like Character::isHighSurrogate.
            var primMemberSymbol = context.GetSymbolInfo(node).Symbol;
            if (primMemberSymbol is IMethodSymbol primMethodGroup
                && !(node.Parent is InvocationExpressionSyntax primInv && primInv.Expression == node))
            {
                var primMappedMethod = InvocationExpressionTransformer.MapPrimitiveStaticMethodName(primTypeSyntax.Keyword.Text, rawMember);
                var primJavaMethodName = primMappedMethod ?? (rawMember.Length > 0
                    ? char.ToLowerInvariant(rawMember[0]) + rawMember[1..]
                    : rawMember);
                return $"{boxedName}::{primJavaMethodName}";
            }

            var mappedMember = MapPrimitiveStaticFieldName(primTypeSyntax.Keyword.Text, rawMember);
            if (primTypeSyntax.Keyword.Text == "decimal")
                context.AddImport("io.github.ningpp.compat.Decimal");
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

        if (instanceReceiverTarget == null
            && staticTypeTarget == null
            && TryMapConfiguredSimpleStaticReceiver(node.Expression, context, out var configuredStaticReceiver))
        {
            if (TryMapBoxedPrimitiveStaticFieldAccess(node.Expression, memberName, context, out var primitiveStaticFieldAccess))
                return primitiveStaticFieldAccess;

            return $"{configuredStaticReceiver}.{ConversionContext.EscapeJavaKeyword(memberName)}";
        }

        var transformedExpr = instanceReceiverTarget ?? facade.Transform(node.Expression, context);
        var target = staticTypeTarget ?? transformedExpr;
        // Strip type arguments from type qualifiers — Java forbids Type<T>.member().
        target = ExpressionTransformerHelpers.StripTypeArguments(target);

        // Fix: When the receiver resolved to a Java primitive keyword (double, int, float, etc.)
        // but the member is a primitive static constant (MaxValue, NaN, etc.), the receiver must
        // be the Java wrapper type (Double, Integer, Float, etc.) — Java primitives have no members.
        // This happens when C# code uses the class-name form (Double.MaxValue) and
        // TryTransformSimpleIdentifierReceiver or facade.Transform maps the receiver to the
        // Java primitive keyword instead of the wrapper type.
        // Skip only boxed class names whose constants need C#-specific literal handling.
        // Regular primitive wrappers (Double, Single, Int32, etc.) can be fixed here.
        var boxedIdTextEarly = node.Expression switch
        {
            IdentifierNameSyntax idName => idName.Identifier.Text,
            MemberAccessExpressionSyntax { Name: IdentifierNameSyntax name } => name.Identifier.Text,
            AliasQualifiedNameSyntax { Name: IdentifierNameSyntax aliasName } => aliasName.Identifier.Text,
            _ => null
        };
        if (ExpressionTransformerHelpers.IsJavaPrimitiveType(target)
            && TryMapPrimitiveStaticFieldName(ExpressionTransformerHelpers.UnboxJavaPrimitiveType(target), memberName, out var _primMapped)
            && !RequiresBoxedClassPrimitiveConstantFallback(boxedIdTextEarly))
        {
            var boxedTarget = ExpressionTransformerHelpers.BoxJavaPrimitiveType(target);
            var primKeyword = ExpressionTransformerHelpers.UnboxJavaPrimitiveType(target);
            if (primKeyword == "decimal")
                context.AddImport("io.github.ningpp.compat.Decimal");
            if (_primMapped.StartsWith("(") || _primMapped.StartsWith("-")
                || char.IsDigit(_primMapped[0]))
                return _primMapped;
            return $"{boxedTarget}.{_primMapped}";
        }

        if (target == "String" && memberName == "Empty")
            return "\"\"";

        // Fix: BitConverter.IsLittleEndian → ByteOrder.nativeOrder() == ByteOrder.LITTLE_ENDIAN
        // C# BitConverter.IsLittleEndian is a static bool property; Java ByteBuffer has no
        // equivalent. The correct Java idiom is ByteOrder.nativeOrder() == ByteOrder.LITTLE_ENDIAN.
        if (memberName == "IsLittleEndian"
            && ExpressionTransformerHelpers.StaticReceiverMatches(
                node.Expression,
                context,
                "BitConverter",
                "System.BitConverter"))
        {
            context.AddImport("java.nio.ByteOrder");
            return "(ByteOrder.nativeOrder() == ByteOrder.LITTLE_ENDIAN)";
        }

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
        if (context.GetSymbolInfo(node).Symbol is IEventSymbol eventSym)
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
        var methodGroupInfo = context.GetSymbolInfo(node);
        var methodGroupSym = methodGroupInfo.Symbol as IMethodSymbol
            ?? methodGroupInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
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

        // Special case: Type.Assembly / MemberInfo.Assembly / Module.Assembly → TypeHelper.getAssembly(...)
        // Without this, the generic TypeMappings rule converts Type.Assembly → getPackage(),
        // which returns java.lang.Package instead of AssemblyCompat and breaks subsequent
        // calls like GetManifestResourceStream or GetName.
        if (memberName is "Assembly" or "get_Assembly"
            && (IsGetTypeInvocation(node.Expression)
                || node.Expression is TypeOfExpressionSyntax
                || IsSystemType(receiverType)
                || IsJavaLangClassType(receiverType)
                || IsSystemModuleType(receiverType)
                || IsMemberInfoType(receiverType)))
        {
            context.AddImport("io.github.ningpp.compat.TypeHelper");
            return $"TypeHelper.getAssembly({target})";
        }

        if (context.GetSymbolInfo(node).Symbol is IPropertySymbol prop)
        {
            // .NET exception members without direct Java equivalents → ExceptionCompat helpers
            bool isLhsOfAssignment = node.Parent is AssignmentExpressionSyntax asgn && asgn.Left == node;
            if (!isLhsOfAssignment
                && IsOrInheritsFromException(receiverType)
                && prop.Name is "InnerException" or "Source" or "StackTrace")
            {
                context.AddImport("io.github.ningpp.compat.ExceptionCompat");
                var helperName = prop.Name switch
                {
                    "InnerException" => "getInnerException",
                    "Source" => "getSource",
                    "StackTrace" => "getStackTrace",
                    _ => throw new InvalidOperationException($"Unhandled exception property {prop.Name}")
                };
                return $"ExceptionCompat.{helperName}({target})";
            }

            // Type.FullName has no direct Java equivalent (Class.getName() differs from .NET FullName).
            // Route through TypeHelper so the generated code compiles and behaves correctly.
            if (memberName == "FullName" && IsSystemType(prop.ContainingType))
            {
                context.AddImport("io.github.ningpp.compat.TypeHelper");
                return $"TypeHelper.getFullName({target})";
            }

            // C# System.Type is mapped to java.lang.Class, but many C# Type properties/methods
            // have no Java equivalent. Bridge them through TypeHelper.
            if (IsSystemType(receiverType) && memberName is "BaseType" or "DeclaringType" or "IsGenericType"
                or "ContainsGenericParameters" or "IsAbstract" or "IsValueType" or "IsVisible"
                or "IsNestedPublic" or "IsClass" or "ArrayRank" or "IsGenericParameter")
            {
                context.AddImport("io.github.ningpp.compat.TypeHelper");
                return $"TypeHelper.get{memberName}({target})";
            }

            if (IsSystemType(receiverType) && memberName == "MakeArrayType")
            {
                context.AddImport("io.github.ningpp.compat.TypeHelper");
                return $"TypeHelper.makeArrayType({target})";
            }

            if (IsSystemType(receiverType) && memberName == "GenericArguments")
            {
                context.AddImport("io.github.ningpp.compat.TypeHelper");
                return $"TypeHelper.getGenericArguments({target})";
            }

            // C# ParameterInfo.IsOptional → ReflectionHelper.isOptional(parameter).
            // ParameterInfo is mapped to java.lang.reflect.Parameter, which has no isOptional method.
            if (memberName == "IsOptional" && IsJavaReflectParameterInfoType(receiverType))
            {
                context.AddImport("io.github.ningpp.compat.ReflectionHelper");
                return $"ReflectionHelper.isOptional({target})";
            }

            if (prop.Name == "Current" && IsEnumeratorCurrentProperty(prop))
                return $"{target}.{GetPropertyGetterName(prop)}()";

            var propertyTarget = prop.IsStatic
                ? MapStaticTypeReceiver(prop.ContainingType, context)
                : target;

            if (IsSystemArrayLengthOnConcreteArray(prop, receiverType))
                return $"{propertyTarget}.length";
            if (prop.Name == "Length" && IsDeclaredAsConcreteArray(node.Expression, context))
                return $"{propertyTarget}.length";

            if (prop.Name == "Length" && IsSystemArrayLengthOnCSharpArray(prop, receiverType))
                return CSharpArrayLength(propertyTarget);
            if (prop.Name == "Length" && IsDeclaredAsSystemArray(node.Expression, context))
                return CSharpArrayLength(propertyTarget);

            var propContainer = prop.ContainingType;
            bool isGenericDictionaryLike =
                propContainer?.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic"
                && propContainer.Name is "Dictionary" or "SortedDictionary" or "IDictionary" or "IReadOnlyDictionary";

            if (isGenericDictionaryLike)
            {
                if (prop.Name == "Values") return $"{propertyTarget}.values()";
                if (prop.Name == "Keys") return $"{propertyTarget}.keySet()";
            }

            // SortedList.Values/Keys must use getValues()/getKeys() (returning CSharpICollection)
            // instead of values()/keySet() (returning Collection/Set from Map).
            // Check the expression's actual type, not the property's declaring type.
            var exprType = context.GetTypeInfo(node.Expression).Type;
            if (exprType?.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic"
                && exprType.Name == "SortedList")
            {
                if (prop.Name == "Values") return $"{propertyTarget}.getValues()";
                if (prop.Name == "Keys") return $"{propertyTarget}.getKeys()";
            }

            // KeyValuePair<K,V>.Key/.Value → Map.Entry<K,V>.getKey()/.getValue()
            if (propContainer?.Name == "KeyValuePair"
                && propContainer?.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic"
                && prop.Name is "Key" or "Value")
            {
                return prop.Name == "Key" ? $"{propertyTarget}.getKey()" : $"{propertyTarget}.getValue()";
            }

            if (prop.Name == "Capacity"
                && prop.ContainingType?.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.List<T>")
            {
                // Java ArrayList has no readable capacity API; use size() as a safe compilable approximation.
                return $"{target}.size()";
            }

            if (prop.Name == "Preamble" && IsSystemTextEncodingType(prop.ContainingType))
            {
                context.AddImport("io.github.ningpp.compat.MemoryExtensions");
                return $"MemoryExtensions.asSpan({target}.getPreamble())";
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
                    propertyTarget = mappedType;
            }
                if (mappedMethod != null)
                {
                    ExpressionTransformerHelpers.AddImportForMappedHelperMethod(mappedMethod, context);
                    if (mappedMethod == "getValues")
                        return $"{propertyTarget}.values()";
                    if (mappedMethod == "getKeys")
                        return $"{propertyTarget}.keySet()";
                    if (IsJavaFieldMapping(mappedMethod))
                        return mappedMethod.Contains('.') ? mappedMethod : $"{propertyTarget}.{mappedMethod}";
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
                {
                    // Add import for fully-qualified type references in mapped methods
                    // e.g. Inet6Address.ofLiteral("::1") needs import java.net.Inet6Address
                    AddImportForDottedMappedMethod(mappedMethod, context);
                    return mappedMethod;
                }
                if (prop.ContainingType.SpecialType == SpecialType.System_Array)
                {
                    return $"{propertyTarget}.{mappedMethod}";
                }
                return $"{propertyTarget}.{mappedMethod}()";
            }

            if (prop.Name == "Position" && IsSystemIoStreamType(prop.ContainingType))
                return $"{propertyTarget}.getPosition()";

            if (prop.Name == "Length" && IsSystemIoStreamType(prop.ContainingType))
                return $"{propertyTarget}.getLength()";

            // StringBuilder.Length → length() (Java's StringBuilder has length() not getLength())
            if (prop.Name == "Length" && IsSystemTextStringBuilder(prop.ContainingType))
                return $"{propertyTarget}.length()";

            // Fix 2: no mapping configured — generate getXxx() for read accesses
            if (!isLhsOfAssignment)
            {
                // GCHandle.IsAllocated → GCHandle.isAllocated(receiver)
                // The C# GCHandle type is mapped to Object in Java, so the default
                // getter generation would produce destHandle.getIsAllocated() which
                // doesn't exist on Object. Use the static compat helper instead.
                if (prop.Name == "IsAllocated"
                    && prop.ContainingType?.ToDisplayString() == "System.Runtime.InteropServices.GCHandle")
                {
                    context.AddImport("io.github.ningpp.compat.GCHandle");
                    return $"GCHandle.isAllocated({target})";
                }

                if (prop.Name == "Length" && IsSystemStringType(prop.ContainingType))
                    return $"{propertyTarget}.length()";

                // For anonymous types synthesized as Java records, use camelCase accessor (e.g. id() not getId())
                if (prop.ContainingType.IsAnonymousType
                    && context.Options.UseRecords && context.Options.TargetJavaVersion >= JavaVersion.Java25)
                {
                    var recordAccessor = char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..];
                    return $"{propertyTarget}.{recordAccessor}()";
                }

                // C# Nullable<T>.Value → Java wrapper unbox method (Boolean.booleanValue(), etc.)
                if (ExpressionTransformerHelpers.TryGetNullableValueUnboxMethod(prop, context, out var nullableUnboxMethod))
                    return $"{propertyTarget}.{nullableUnboxMethod}()";

                var getter = GetPropertyGetterName(prop);
                return $"{propertyTarget}.{getter}()";
            }
        }

        // Path C: Per-type property/field resolution via receiverType.GetMembers().
        // This runs immediately after Path A (GetSymbolInfo) fails. It uses the
        // type's own metadata to decide — no global whitelist needed.
        // Type-parameter receivers such as Assert.Throws<T>(...).Message need to
        // resolve through their constraints even when the transformed receiver is
        // an invocation expression that starts with an uppercase static type name.
        if (receiverType is ITypeParameterSymbol
            && staticTypeTarget == null
            && instanceReceiverTarget == null)
        {
            var resolved = TryResolvePropertyByType(memberName, target, receiverType, context);
            if (resolved != null)
                return resolved;
        }

        // Only fires for instance access (target starts with lowercase or underscore).
        // Guard: if either staticTypeTarget or instanceReceiverTarget is set, this is
        // a resolved receiver (static type or instance property), not a raw instance access.
        if (target.Length > 0 && (char.IsLower(target[0]) || target[0] == '_') && receiverType != null
            && staticTypeTarget == null && instanceReceiverTarget == null)
        {
            var resolved = TryResolvePropertyByType(memberName, target, receiverType, context);
            if (resolved != null)
                return resolved;
        }

        // Fix 3: GetSymbolInfo returned no IPropertySymbol (e.g. lambda param in LINQ-rewritten tree).
        // Fall back to GetTypeInfo on the receiver expression for TypeMappings lookup.
        if (context.SemanticModel != null)
        {
            var exprType = context.GetTypeInfo(node.Expression).Type;
            if (exprType != null)
            {
                if (memberName == "Current" && IsEnumeratorLikeType(exprType))
                    return $"{target}.getCurrent()";

                if (memberName == "Length" && IsSystemStringType(exprType))
                    return $"{target}.length()";

                if (memberName == "Length" && IsSystemArrayReferenceType(exprType))
                    return CSharpArrayLength(target);

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
                if (memberName == "Length" && IsSystemTextStringBuilder(exprType))
                    return $"{target}.length()";

            }
        }

        // Fix 4: When GetSymbolInfo returned null (e.g. missing assembly reference)
        // but the static receiver type was resolved (e.g. IPAddress → InetAddress),
        // try MapMethod using the C# type name from the expression text.
        // This handles cases like IPAddress.Loopback → InetAddress.getLoopbackAddress()
        // where the property symbol is unavailable but the type mapping is known.
        if (staticTypeTarget != null && context.SemanticModel != null)
        {
            var receiverText = node.Expression.ToString();
            var csharpTypeName = receiverText;
            // Handle qualified names like System.Net.IPAddress → extract the FQN
            if (node.Expression is MemberAccessExpressionSyntax maExpr)
                csharpTypeName = maExpr.ToString();

            var mappedStaticMethod = context.TypeMappings.MapMethod(csharpTypeName, memberName);
            if (mappedStaticMethod == null)
            {
                // Try resolving the type symbol to get the FQN
                var receiverTypeInfo = context.GetSymbolInfo(node.Expression);
                if (receiverTypeInfo.Symbol is INamedTypeSymbol receiverNamedType)
                {
                    var fqn = $"{receiverNamedType.ContainingNamespace}.{receiverNamedType.Name}";
                    mappedStaticMethod = context.TypeMappings.MapMethod(fqn, memberName);
                }
            }
            if (mappedStaticMethod != null)
            {
                ExpressionTransformerHelpers.AddImportForMappedHelperMethod(mappedStaticMethod, context);
                if (IsJavaFieldMapping(mappedStaticMethod)) return mappedStaticMethod.Contains('.') ? mappedStaticMethod : $"{target}.{mappedStaticMethod}";
                if (ExpressionTransformerHelpers.IsMappedCompatibilityHelperMethod(mappedStaticMethod))
                    return $"{mappedStaticMethod}()";
                if (mappedStaticMethod.Contains('.'))
                {
                    AddImportForDottedMappedMethod(mappedStaticMethod, context);
                    return mappedStaticMethod;
                }
                return $"{target}.{mappedStaticMethod}()";
            }
        }

        // Fix: C# boxed class-name static constants — Double.MaxValue → Double.MAX_VALUE,
        // Int32.MaxValue → Integer.MAX_VALUE, Single.MaxValue → Float.MAX_VALUE, etc.
        // The PredefinedTypeSyntax path above handles keyword forms (e.g. 'double.MaxValue'),
        // but when code uses the class name form the receiver is an IdentifierNameSyntax.
        // Also handle qualified forms like System.UInt32.MaxValue and global::System.UInt32.MaxValue.
        if (TryMapBoxedPrimitiveStaticFieldAccess(node.Expression, memberName, context, out var boxedPrimitiveStaticFieldAccess))
            return boxedPrimitiveStaticFieldAccess;

        // C# static field/property mappings for non-primitive types.
        // Current TimeSpan mappings target the compat type, whose constants use Java naming.
        if (target == "CSharpTimeSpan" && memberName is "Zero" or "ZERO")
            return "CSharpTimeSpan.ZERO";

        // CSharpTimeSpan static fields: C# PascalCase → Java UPPER_SNAKE_CASE
        if (target == "CSharpTimeSpan")
        {
            var tsField = memberName switch
            {
                "TicksPerMillisecond" => "TICKS_PER_MILLISECOND",
                "TicksPerSecond" => "TICKS_PER_SECOND",
                "TicksPerMinute" => "TICKS_PER_MINUTE",
                "TicksPerHour" => "TICKS_PER_HOUR",
                "TicksPerDay" => "TICKS_PER_DAY",
                "MinValue" => "MIN_VALUE",
                "MaxValue" => "MAX_VALUE",
                _ => null
            };
            if (tsField != null)
                return $"CSharpTimeSpan.{tsField}";
        }

        // CSharpDateTime static fields: C# PascalCase → Java UPPER_SNAKE_CASE
        if (target == "CSharpDateTime")
        {
            var dtField = memberName switch
            {
                "MinValue" => "MIN_VALUE",
                "MaxValue" => "MAX_VALUE",
                _ => null
            };
            if (dtField != null)
                return $"CSharpDateTime.{dtField}";
        }

        // Guid.Empty → new UUID(0L, 0L) — java.util.UUID has no Empty field.
        if ((target == "UUID" || target == "Guid") && memberName == "Empty")
        {
            context.AddImport("java.util.UUID");
            return "new UUID(0L, 0L)";
        }

        // Legacy TimeSpan mapping fallback for older Duration-based conversions.
        if (target == "Duration" && memberName is "Zero" or "ZERO")
        {
            context.AddImport("java.time.ZoneOffset");
            return "ZoneOffset.UTC";
        }

        if (memberName == "Current") return $"{target}.getCurrent()";
        if (memberName == "Values") return $"{target}.values()";
        if (memberName == "Keys") return $"{target}.keySet()";

        // Last-resort generic fallback for known C#→Java property mappings
        if (memberName == "Count") return $"{target}.size()";
        if (memberName == "Position" && IsSystemIoStreamType(receiverType)) return $"{target}.getPosition()";
        if (memberName == "Length" && IsSystemIoStreamType(receiverType)) return $"{target}.getLength()";
        if (memberName == "Length" && IsSystemStringType(receiverType)) return $"{target}.length()";
        if (memberName == "Length" && IsSystemTextStringBuilder(receiverType)) return $"{target}.length()";
        if (memberName == "Length" && IsSystemArrayReferenceType(receiverType)) return CSharpArrayLength(target);
        if (memberName == "Length" && IsDeclaredAsSystemArray(node.Expression, context)) return CSharpArrayLength(target);
        // For types not matched above (e.g. ValueListBuilder, custom structs with a
        // Length property), use getter pattern when the receiver type is unknown.
        // When the receiver type is known, TryResolvePropertyByType above already
        // decided whether the member is a field or a property.
        if (memberName == "Length" && (receiverType == null || receiverType.TypeKind == TypeKind.Error))
            return $"{target}.getLength()";
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
        // C# tuple Item1/Item2/... → vavr Tuple._1()/_2()/...
        if (IsTupleItemName(memberName, out var tupleIdx)
            && receiverType is INamedTypeSymbol nts
            && (nts.IsTupleType || IsVavrTupleType(nts)))
        {
            return $"{target}._{tupleIdx}()";
        }
        // Auto-properties emitted as fields in project pipeline
        if (memberName == "AlgorithmData") return $"{target}.AlgorithmData";
        // GCHandle.IsAllocated → GCHandle.isAllocated(receiver)
        // This property is unique to GCHandle but the C# source may declare the variable as 'object'.
        if (memberName == "IsAllocated")
        {
            context.AddImport("io.github.ningpp.compat.GCHandle");
            return $"GCHandle.isAllocated({target})";
        }
        // System.Globalization.CultureInfo.InvariantCulture → java.util.Locale.ROOT
        if (memberName == "InvariantCulture" && target.EndsWith(".CultureInfo"))
        {
            context.AddImport("java.util.Locale");
            return "Locale.ROOT";
        }

        // Fix: when receiverType is an IErrorTypeSymbol (unresolved type, e.g. missing assembly
        // reference like System.Xml.XmlReader), try MapMethod with the error type's FQN.
        // If a mapping is found, use it; otherwise default to the getter pattern since C#
        // properties should generally be converted to Java getters.
        if (receiverType != null && receiverType.TypeKind == TypeKind.Error
            && receiverType is INamedTypeSymbol errorType
            && char.IsUpper(memberName[0]))
        {
            var errorTypeName = errorType.ToDisplayString();
            var errorTypeFqn = errorType.ContainingNamespace != null && !errorType.ContainingNamespace.IsGlobalNamespace
                ? $"{errorType.ContainingNamespace}.{errorType.Name}"
                : errorType.Name;
            var mappedMethod = context.TypeMappings.MapMethod(errorTypeFqn, memberName)
                ?? context.TypeMappings.MapMethod(errorTypeName, memberName);
            if (mappedMethod != null)
            {
                ExpressionTransformerHelpers.AddImportForMappedHelperMethod(mappedMethod, context);
                if (IsJavaFieldMapping(mappedMethod))
                    return mappedMethod.Contains('.') ? mappedMethod : $"{target}.{mappedMethod}";
                if (ExpressionTransformerHelpers.IsMappedCompatibilityHelperMethod(mappedMethod))
                    return IsStaticReceiverExpression(node.Expression, context) ? $"{mappedMethod}()" : $"{mappedMethod}({target})";
                return mappedMethod.Contains('.') ? mappedMethod : $"{target}.{mappedMethod}()";
            }
            // No explicit mapping — default to getter pattern for C# properties on unresolved types
            var getter = "get" + char.ToUpperInvariant(memberName[0]) + memberName[1..];
            context.Diagnostics.Info(
                $"Property fallback (unresolved type): '{node}' -> {target}.{getter}() | receiverType Error: {errorTypeFqn}",
                node.GetLocation());
            return $"{target}.{getter}()";
        }

        // Phase 1 diagnostic: log why we fell through to raw field access
        if (memberName != "AlgorithmData") // AlgorithmData is intentionally a field
        {
            var reasonA = context.SemanticModel == null ? "SemanticModel null"
                : context.GetSymbolInfo(node).Symbol switch
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

        // Handle C# tuple field access fallthrough: Item1/Item2/... → vavr _1()/_2()/...
        if (IsTupleItemName(memberName, out var tupIdx))
        {
            return $"{target}._{tupIdx}()";
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
            if (accessedSymbol?.ContainingType is INamedTypeSymbol staticContainingType)
            {
                transformedReceiver = MapStaticTypeReceiver(staticContainingType, context);
                return true;
            }

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
            transformedReceiver = $"{GetPropertyGetterName(propertySymbol)}()";
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
        var symbolInfo = context.GetSymbolInfo(memberAccess);
        return symbolInfo.Symbol
            ?? symbolInfo.CandidateSymbols.FirstOrDefault(IsStaticMemberSymbol)
            ?? symbolInfo.CandidateSymbols.FirstOrDefault();
    }

    private static ISymbol? GetPreferredIdentifierSymbol(
        IdentifierNameSyntax identifier,
        ConversionContext context,
        bool preferInstanceCandidate)
    {
        var symbolInfo = context.GetSymbolInfo(identifier);

        if (preferInstanceCandidate && symbolInfo.CandidateSymbols.Length > 1)
        {
            var nonTypeCandidate = symbolInfo.CandidateSymbols.FirstOrDefault(
                s => s is ILocalSymbol or IParameterSymbol or IFieldSymbol or IPropertySymbol
                     or IEventSymbol or IMethodSymbol);
            if (nonTypeCandidate != null)
                return nonTypeCandidate;
        }

        return symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
    }

    /// <summary>
    /// Adds the appropriate Java import for a mapped method that contains a dot
    /// (e.g. "Inet6Address.ofLiteral(\"::1\")" needs "java.net.Inet6Address").
    /// The type name is extracted as the portion before the first dot.
    /// </summary>
    private static void AddImportForDottedMappedMethod(string mappedMethod, ConversionContext context)
    {
        var dotIndex = mappedMethod.IndexOf('.');
        if (dotIndex <= 0) return;
        var typeName = mappedMethod[..dotIndex];
        var import = typeName switch
        {
            "Inet6Address" => "java.net.Inet6Address",
            "InetAddress" => "java.net.InetAddress",
            "Inet4Address" => "java.net.Inet4Address",
            "Locale" => "java.util.Locale",
            _ => null
        };
        if (import != null)
            context.AddImport(import);
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
    {
        if (typeSymbol is INamedTypeSymbol namedType
            && namedType.ContainingType == null
            && context.TryGetAssemblyScopedJavaTypeName(namedType, out var scopedName))
        {
            var namespaceName = namedType.ContainingNamespace?.ToDisplayString();
            if (!string.IsNullOrWhiteSpace(namespaceName)
                && namespaceName != "<global namespace>"
                && !string.Equals(namespaceName, context.CurrentNamespace, StringComparison.Ordinal))
            {
                context.AddImport($"{context.NamespaceToPackage(namespaceName)}.{scopedName}");
            }

            return scopedName;
        }

        return ExpressionTransformerHelpers.StripTypeArguments(context.MapType(typeSymbol));
    }

    private static bool IsStaticReceiverExpression(ExpressionSyntax expression, ConversionContext context)
        => ExpressionTransformerHelpers.TryGetStaticReceiverType(expression, context, out _);

    private static bool TryMapConfiguredSimpleStaticReceiver(
        ExpressionSyntax expression,
        ConversionContext context,
        out string javaReceiver)
    {
        javaReceiver = string.Empty;

        if (expression is not IdentifierNameSyntax receiver)
            return false;

        var receiverName = receiver.Identifier.Text;
        if (string.IsNullOrWhiteSpace(receiverName)
            || !char.IsUpper(receiverName[0]))
        {
            return false;
        }

        var preferredSymbol = GetPreferredIdentifierSymbol(receiver, context, preferInstanceCandidate: true);
        if (preferredSymbol is ILocalSymbol or IParameterSymbol or IFieldSymbol or IPropertySymbol
            or IEventSymbol or IMethodSymbol)
        {
            return false;
        }

        var configKey = context.TypeMappings.FindConfigKeyBySimpleName(receiverName);
        if (configKey == null)
            return false;

        var mappedType = context.TypeMappings.MapType(configKey);
        javaReceiver = ExpressionTransformerHelpers.StripTypeArguments(mappedType);
        if (javaReceiver.Contains('.'))
            javaReceiver = javaReceiver[(javaReceiver.LastIndexOf('.') + 1)..];

        foreach (var import in context.TypeMappings.GetRequiredImports(configKey))
            context.AddImport(import);

        return !string.IsNullOrWhiteSpace(javaReceiver);
    }

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

        if (memberName == "Length" && IsSystemArrayReferenceType(receiverType))
            return CSharpArrayLength(target);

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
                    var propertyTarget = foundProp.IsStatic
                        ? MapStaticTypeReceiver(foundProp.ContainingType, context)
                        : target;

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
                            return mapped.Contains('.') ? mapped : $"{propertyTarget}.{mapped}";
                        if (ExpressionTransformerHelpers.IsMappedCompatibilityHelperMethod(mapped))
                            return foundProp.IsStatic
                                ? $"{mapped}()"
                                : $"{mapped}({propertyTarget})";
                        if (mapped.StartsWith("get", StringComparison.Ordinal)
                            && mapped.Length > 3
                            && memberName == mapped[3..])
                            return $"{propertyTarget}.{mapped}()";
                        return mapped.Contains('.') ? mapped : $"{propertyTarget}.{mapped}()";
                    }

                    if (memberName == "FullName" && IsSystemType(foundProp.ContainingType))
                    {
                        context.AddImport("io.github.ningpp.compat.TypeHelper");
                        return $"TypeHelper.getFullName({propertyTarget})";
                    }

                    if (memberName == "Position" && IsSystemIoStreamType(foundProp.ContainingType))
                        return $"{propertyTarget}.getPosition()";
                    if (memberName == "Length" && IsSystemIoStreamType(foundProp.ContainingType))
                        return $"{propertyTarget}.getLength()";

                    // Default: generate getXxx() getter
                    var getter = GetPropertyGetterName(foundProp);
                    return $"{propertyTarget}.{getter}()";
                }
                if (m is IFieldSymbol { IsStatic: false })
                {
                    // Handle C# tuple field access: Item1/Item2/... → vavr _1()/_2()/...
                    // (KeyValuePair Item1/Item2 case is handled earlier in TransformMemberAccess)
                    if (IsTupleItemName(memberName, out var tupIdx))
                    {
                        return $"{target}._{tupIdx}()";
                    }
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

    private static bool IsSystemType(ITypeSymbol? type)
    {
        if (type == null) return false;
        if (type.ToDisplayString() == "System.Type") return true;
        for (var current = type.BaseType; current != null; current = current.BaseType)
        {
            if (current.ToDisplayString() == "System.Type") return true;
        }
        return false;
    }

    private static bool IsJavaLangClassType(ITypeSymbol? type)
        => type?.ToDisplayString() == "java.lang.Class";

    private static bool IsSystemModuleType(ITypeSymbol? type)
        => type?.ToDisplayString() is "System.Reflection.Module" or "java.lang.Module";

    private static bool IsMemberInfoType(ITypeSymbol? type)
    {
        if (type == null) return false;
        var display = type.ToDisplayString();
        if (display is "System.Reflection.MemberInfo"
            or "System.Reflection.MethodInfo"
            or "System.Reflection.MethodBase"
            or "System.Reflection.FieldInfo"
            or "System.Reflection.PropertyInfo"
            or "System.Reflection.ConstructorInfo"
            or "io.github.ningpp.compat.MemberInfo"
            or "io.github.ningpp.compat.MethodInfo"
            or "io.github.ningpp.compat.FieldInfo"
            or "io.github.ningpp.compat.ConstructorInfo")
        {
            return true;
        }
        for (var current = type.BaseType; current != null; current = current.BaseType)
        {
            if (current.ToDisplayString() == "System.Reflection.MemberInfo")
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns true when the C# receiver type is ParameterInfo, which maps to
    /// java.lang.reflect.Parameter and has no direct isOptional method.
    /// </summary>
    private static bool IsJavaReflectParameterInfoType(ITypeSymbol? type)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            var display = current.OriginalDefinition.ToDisplayString();
            if (display == "System.Reflection.ParameterInfo")
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsSystemArrayReferenceType(ITypeSymbol? type)
        => type is not IArrayTypeSymbol
            && type?.TypeKind != TypeKind.Array
            && (type?.SpecialType == SpecialType.System_Array
                || type?.ToDisplayString() == "System.Array");

    private static bool IsDeclaredAsSystemArray(ExpressionSyntax receiver, ConversionContext context)
    {
        if (receiver is not IdentifierNameSyntax id)
            return false;

        var name = id.Identifier.Text;
        if (TryGetCurrentIdentifierType(id, context, out var currentType))
            return IsSystemArrayReferenceType(currentType);

        if (TryGetScopedIdentifierTypeSyntax(id, name, out var typeSyntax)
            && IsSystemArraySyntax(typeSyntax, context))
            return true;

        return false;
    }

    private static bool IsDeclaredAsConcreteArray(ExpressionSyntax receiver, ConversionContext context)
    {
        if (receiver is not IdentifierNameSyntax id)
            return false;

        var name = id.Identifier.Text;
        if (TryGetCurrentIdentifierType(id, context, out var currentType))
            return currentType is IArrayTypeSymbol || currentType.TypeKind == TypeKind.Array;

        if (TryGetScopedIdentifierTypeSyntax(id, name, out var typeSyntax)
            && IsConcreteArraySyntax(typeSyntax, context))
            return true;

        return false;
    }

    private static bool TryGetCurrentIdentifierType(
        IdentifierNameSyntax identifier,
        ConversionContext context,
        out ITypeSymbol type)
    {
        type = null!;

        if (context.LocalTypeOverrides.TryGetValue(identifier.Identifier.Text, out var overrideType)
            && overrideType.TypeKind != TypeKind.Error)
        {
            type = overrideType;
            return true;
        }

        if (TryGetNearestScopedIdentifierType(identifier, context, out type))
            return true;

        var typeInfo = context.GetTypeInfo(identifier);
        var resolved = typeInfo.Type ?? typeInfo.ConvertedType;
        if (resolved != null && resolved.TypeKind != TypeKind.Error)
        {
            type = resolved;
            return true;
        }

        resolved = GetSymbolType(context.GetSymbolInfo(identifier).Symbol);
        if (resolved != null && resolved.TypeKind != TypeKind.Error)
        {
            type = resolved;
            return true;
        }

        if (context.VarTypeMap.TryGetValue(identifier.Identifier.Text, out var mappedType)
            && mappedType.TypeKind != TypeKind.Error)
        {
            type = mappedType;
            return true;
        }

        return false;
    }

    private static bool TryGetNearestScopedIdentifierType(
        IdentifierNameSyntax identifier,
        ConversionContext context,
        out ITypeSymbol type)
    {
        type = null!;
        var name = identifier.Identifier.Text;
        var root = identifier.SyntaxTree.GetRoot();

        var declarator = root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Identifier.Text == name && v.SpanStart <= identifier.SpanStart)
            .OrderByDescending(v => v.SpanStart)
            .FirstOrDefault(v => v.Parent is VariableDeclarationSyntax declaration
                && IsVariableDeclarationInScope(declaration, identifier));
        if (declarator?.Parent is VariableDeclarationSyntax variableDeclaration)
        {
            var declaredType = context.GetTypeInfo(variableDeclaration.Type).Type;
            if (IsUsableResolvedType(declaredType))
            {
                type = declaredType!;
                return true;
            }

            if (declarator.Initializer?.Value != null)
            {
                var initializerTypeInfo = context.GetTypeInfo(declarator.Initializer.Value);
                var initializerType = initializerTypeInfo.Type ?? initializerTypeInfo.ConvertedType;
                if (IsUsableResolvedType(initializerType))
                {
                    type = initializerType!;
                    return true;
                }
            }
        }

        var parameter = root.DescendantNodes()
            .OfType<ParameterSyntax>()
            .Where(p => p.Identifier.Text == name && p.SpanStart <= identifier.SpanStart)
            .OrderByDescending(p => p.SpanStart)
            .FirstOrDefault(p => p.Type != null && IsParameterInScope(p, identifier));
        if (parameter?.Type != null)
        {
            var parameterType = context.GetTypeInfo(parameter.Type).Type;
            if (IsUsableResolvedType(parameterType))
            {
                type = parameterType!;
                return true;
            }
        }

        return false;
    }

    private static bool IsUsableResolvedType(ITypeSymbol? type)
        => type is { TypeKind: not (TypeKind.Error or TypeKind.Unknown) };

    private static bool TryGetScopedIdentifierTypeSyntax(
        IdentifierNameSyntax identifier,
        string name,
        out TypeSyntax typeSyntax)
    {
        typeSyntax = null!;
        var root = identifier.SyntaxTree.GetRoot();

        var declarator = root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Identifier.Text == name && v.SpanStart <= identifier.SpanStart)
            .OrderByDescending(v => v.SpanStart)
            .FirstOrDefault(v => v.Parent is VariableDeclarationSyntax declaration
                && IsVariableDeclarationInScope(declaration, identifier));
        if (declarator?.Parent is VariableDeclarationSyntax variableDeclaration)
        {
            typeSyntax = variableDeclaration.Type;
            return true;
        }

        var parameter = root.DescendantNodes()
            .OfType<ParameterSyntax>()
            .Where(p => p.Identifier.Text == name && p.SpanStart <= identifier.SpanStart)
            .OrderByDescending(p => p.SpanStart)
            .FirstOrDefault(p => p.Type != null && IsParameterInScope(p, identifier));
        if (parameter?.Type != null)
        {
            typeSyntax = parameter.Type;
            return true;
        }

        return false;
    }

    private static bool IsVariableDeclarationInScope(
        VariableDeclarationSyntax declaration,
        IdentifierNameSyntax use)
    {
        if (declaration.Parent is LocalDeclarationStatementSyntax local)
        {
            var block = local.Parent as BlockSyntax;
            return block != null
                && block.Span.Contains(use.SpanStart)
                && local.SpanStart <= use.SpanStart;
        }

        if (declaration.Parent is FieldDeclarationSyntax field)
        {
            var declaringType = field.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            return declaringType != null && declaringType.Span.Contains(use.SpanStart);
        }

        var owner = declaration.Parent?.AncestorsAndSelf().FirstOrDefault(n =>
            n is ForStatementSyntax
                or UsingStatementSyntax
                or FixedStatementSyntax
                or BaseMethodDeclarationSyntax
                or LocalFunctionStatementSyntax
                or AnonymousFunctionExpressionSyntax);

        return owner != null
            && owner.Span.Contains(use.SpanStart)
            && declaration.SpanStart <= use.SpanStart;
    }

    private static bool IsParameterInScope(ParameterSyntax parameter, IdentifierNameSyntax use)
    {
        var owner = parameter.Ancestors().FirstOrDefault(n =>
            n is BaseMethodDeclarationSyntax
                or LocalFunctionStatementSyntax
                or AnonymousFunctionExpressionSyntax);

        return owner != null && owner.Span.Contains(use.SpanStart);
    }

    private static bool IsConcreteArraySyntax(TypeSyntax typeSyntax, ConversionContext context)
    {
        var type = context.GetTypeInfo(typeSyntax).Type;
        if (type is IArrayTypeSymbol || type?.TypeKind == TypeKind.Array)
            return true;

        return typeSyntax is ArrayTypeSyntax;
    }

    private static bool IsSystemArraySyntax(TypeSyntax typeSyntax, ConversionContext context)
    {
        var type = context.GetTypeInfo(typeSyntax).Type;
        if (IsSystemArrayReferenceType(type))
            return true;

        var text = typeSyntax.ToString().Trim();
        return text is "Array" or "System.Array" or "global::System.Array";
    }

    private static bool IsSystemArrayLengthOnCSharpArray(IPropertySymbol prop, ITypeSymbol? receiverType)
        => prop.Name == "Length"
            && prop.ContainingType?.SpecialType == SpecialType.System_Array
            && IsSystemArrayReferenceType(receiverType);

    private static bool IsSystemArrayLengthOnConcreteArray(IPropertySymbol prop, ITypeSymbol? receiverType)
        => prop.Name == "Length"
            && prop.ContainingType?.SpecialType == SpecialType.System_Array
            && (receiverType is IArrayTypeSymbol || receiverType?.TypeKind == TypeKind.Array);

    private static ITypeSymbol? GetSymbolType(ISymbol? symbol)
        => symbol switch
        {
            ILocalSymbol local => local.Type,
            IParameterSymbol parameter => parameter.Type,
            IFieldSymbol field => field.Type,
            IPropertySymbol property => property.Type,
            _ => null,
        };

    private static ITypeSymbol? ResolveReceiverType(ExpressionSyntax receiver, ConversionContext context)
    {
        if (receiver is IdentifierNameSyntax overrideIdentifier
            && context.LocalTypeOverrides.TryGetValue(overrideIdentifier.Identifier.Text, out var overrideType)
            && overrideType.TypeKind != TypeKind.Error)
        {
            return overrideType;
        }

        if (receiver is IdentifierNameSyntax scopedIdentifier
            && TryGetNearestScopedIdentifierType(scopedIdentifier, context, out var scopedType))
        {
            return scopedType;
        }

        if (TryResolveXunitGenericExceptionAssertResultType(receiver, context, out var xunitResultType))
            return xunitResultType;

        var typeInfo = context.GetTypeInfo(receiver);
        var resolved = typeInfo.Type ?? typeInfo.ConvertedType;
        if (resolved != null && resolved.TypeKind != TypeKind.Error)
            return resolved;

        resolved = GetSymbolType(context.GetSymbolInfo(receiver).Symbol);
        if (resolved != null && resolved.TypeKind != TypeKind.Error)
            return resolved;

        if (receiver is IdentifierNameSyntax id)
        {
            var name = id.Identifier.Text;
            if (context.VarTypeMap.TryGetValue(name, out var mappedType)
                && mappedType.TypeKind != TypeKind.Error)
                return mappedType;

            var declarator = receiver.SyntaxTree.GetRoot()
                .DescendantNodes()
                .OfType<VariableDeclaratorSyntax>()
                .LastOrDefault(v => v.Identifier.Text == name && v.SpanStart <= receiver.SpanStart);
            if (declarator?.Parent is VariableDeclarationSyntax variableDeclaration)
            {
                var declaredType = context.GetTypeInfo(variableDeclaration.Type).Type;
                if (declaredType != null && declaredType.TypeKind != TypeKind.Error)
                    return declaredType;
            }

            var parameter = receiver.SyntaxTree.GetRoot()
                .DescendantNodes()
                .OfType<ParameterSyntax>()
                .LastOrDefault(p => p.Identifier.Text == name && p.SpanStart <= receiver.SpanStart);
            if (parameter?.Type != null)
            {
                var parameterType = context.GetTypeInfo(parameter.Type).Type;
                if (parameterType != null && parameterType.TypeKind != TypeKind.Error)
                    return parameterType;
            }
        }

        return null;
    }

    private static bool TryResolveXunitGenericExceptionAssertResultType(
        ExpressionSyntax receiver,
        ConversionContext context,
        out ITypeSymbol? resultType)
    {
        resultType = null;

        if (!context.HasUsingDirective("Xunit"))
            return false;

        if (receiver is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax
                {
                    Expression: IdentifierNameSyntax { Identifier.Text: "Assert" },
                    Name: GenericNameSyntax genericName
                }
            })
            return false;

        if (genericName.Identifier.Text is not ("Throws" or "ThrowsAny" or "ThrowsAsync")
            || genericName.TypeArgumentList.Arguments.Count == 0)
            return false;

        var candidate = context.GetTypeInfo(genericName.TypeArgumentList.Arguments[0]).Type;
        if (candidate == null || candidate.TypeKind == TypeKind.Error)
            return false;

        resultType = candidate;
        return true;
    }

    private static string CSharpArrayLength(string target)
        => $"{target}.getLength()";

    private static bool IsSystemTextStringBuilder(ITypeSymbol? type)
        => type?.ToDisplayString() == "System.Text.StringBuilder";

    private static bool IsSystemTextEncodingType(ITypeSymbol? type)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            if (current.ToDisplayString() == "System.Text.Encoding")
                return true;
        }

        return false;
    }

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

    private static bool TryMapPrimitiveStaticFieldAccess(
        IFieldSymbol field,
        ConversionContext context,
        out JavaExpression expression)
    {
        expression = null!;

        if (!field.IsStatic || field.ContainingType is not INamedTypeSymbol containingType)
            return false;

        if (!TryGetPrimitiveStaticFieldInfo(containingType, out var primitiveKeyword, out var javaWrapper))
            return false;

        if (!TryMapPrimitiveStaticFieldName(primitiveKeyword, field.Name, out var mappedMember))
            return false;

        if (primitiveKeyword == "decimal")
            context.AddImport("io.github.ningpp.compat.Decimal");

        var code = IsSelfContainedPrimitiveStaticMapping(mappedMember)
            ? mappedMember
            : $"{javaWrapper}.{mappedMember}";
        expression = new JavaRawExpression(code, context.MapType(field.Type));
        return true;
    }

    private static bool TryGetPrimitiveStaticFieldInfo(
        INamedTypeSymbol containingType,
        out string primitiveKeyword,
        out string javaWrapper)
    {
        primitiveKeyword = string.Empty;
        javaWrapper = string.Empty;

        if (containingType.ContainingNamespace?.ToDisplayString() == "System"
            && _csharpBoxedClassNames.TryGetValue(containingType.Name, out var info))
        {
            primitiveKeyword = info.keyword;
            javaWrapper = info.javaWrapper;
            return true;
        }

        return false;
    }

    private static bool RequiresBoxedClassPrimitiveConstantFallback(string? boxedClassName)
        => boxedClassName is "Byte" or "UInt16" or "UInt32" or "UInt64";

    private static bool TryMapBoxedPrimitiveStaticFieldAccess(
        ExpressionSyntax receiverExpression,
        string memberName,
        ConversionContext context,
        out string mappedAccess)
    {
        mappedAccess = string.Empty;

        var boxedIdText = receiverExpression switch
        {
            IdentifierNameSyntax idName => idName.Identifier.Text,
            MemberAccessExpressionSyntax { Name: IdentifierNameSyntax name } => name.Identifier.Text,
            AliasQualifiedNameSyntax { Name: IdentifierNameSyntax aliasName } => aliasName.Identifier.Text,
            _ => null
        };

        if (boxedIdText == null
            || !_csharpBoxedClassNames.TryGetValue(boxedIdText, out var primitiveInfo)
            || !TryMapPrimitiveStaticFieldName(primitiveInfo.keyword, memberName, out var mappedMember))
        {
            return false;
        }

        if (primitiveInfo.keyword == "decimal")
            context.AddImport("io.github.ningpp.compat.Decimal");

        mappedAccess = IsSelfContainedPrimitiveStaticMapping(mappedMember)
            ? mappedMember
            : $"{primitiveInfo.javaWrapper}.{mappedMember}";
        return true;
    }

    private static bool IsSelfContainedPrimitiveStaticMapping(string mappedMember)
        => mappedMember.StartsWith("(", StringComparison.Ordinal)
            || mappedMember.StartsWith("-", StringComparison.Ordinal)
            || char.IsDigit(mappedMember[0]);

    private static bool TryMapPrimitiveStaticFieldName(string primitiveKeyword, string memberName, out string mappedMember)
    {
        mappedMember = (primitiveKeyword, memberName) switch
        {
            // C# byte (unsigned) MaxValue=255, MinValue=0 — emit literals directly
            ("byte", "MaxValue") => "255",
            ("byte", "MinValue") => "0",
            // C# unsigned types — Java has no unsigned primitives, emit literal values
            ("uint", "MaxValue") => "4294967295L",
            ("uint", "MinValue") => "0",
            ("ushort", "MaxValue") => "65535",
            ("ushort", "MinValue") => "0",
            // ulong.MaxValue exceeds Long.MAX_VALUE; emit hex literal (bit-preserving,
            // but Java interprets 0xFFFFFFFFFFFFFFFFL as -1L due to signed representation).
            ("ulong", "MaxValue") => "0xFFFFFFFFFFFFFFFFL",
            ("ulong", "MinValue") => "0",
            ("decimal", "MaxValue") => "MAX_VALUE",
            ("decimal", "MinValue") => "MIN_VALUE",
            ("decimal", "One") => "ONE",
            ("decimal", "Zero") => "ZERO",
            ("decimal", "MinusOne") => "MINUS_ONE",
            // Duration (mapped from C# TimeSpan)
            ("Duration", "Zero") => "ZERO",
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
        var facade = ExpressionTransformerFacade.Instance;
        var target = facade.Transform(node.Expression, context);
        var member = ConversionContext.EscapeJavaKeyword(node.Name.Identifier.Text);

        if (context.IsInFixedScope)
        {
            context.Diagnostics.Warning("Pointer member access (->) requires struct layout info - converting to field access", node.GetLocation());
            return $"{target}.{member}";
        }

        context.Diagnostics.Warning("Pointer member access (->) has no Java equivalent - unsafe code not supported", node.GetLocation());
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

    /// <summary>
    /// Checks whether the expression is a call to GetType(), possibly qualified with 'this.'.
    /// Matches both <c>GetType()</c> and <c>this.GetType()</c>.
    /// </summary>
    private static bool IsGetTypeInvocation(ExpressionSyntax expression)
    {
        if (expression is InvocationExpressionSyntax inv)
        {
            // GetType()
            if (inv.Expression is IdentifierNameSyntax id && id.Identifier.Text == "GetType")
                return true;
            // this.GetType()
            if (inv.Expression is MemberAccessExpressionSyntax mas
                && mas.Name.Identifier.Text == "GetType"
                && mas.Expression is ThisExpressionSyntax)
                return true;
        }
        return false;
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

    /// <summary>
    /// Returns true if the member name is a C# tuple item name (Item1, Item2, ..., Item8).
    /// Outputs the numeric index (1-based → 1,2,...,8).
    /// </summary>
    private static bool IsTupleItemName(string memberName, out int index)
    {
        index = 0;
        if (memberName.Length >= 5 && memberName.StartsWith("Item") && int.TryParse(memberName.AsSpan(4), out index))
        {
            return index >= 1 && index <= 8;
        }
        return false;
    }

    /// <summary>
    /// Returns true if the type is a vavr Tuple type (io.vavr.Tuple1..Tuple8).
    /// These are the Java mapping targets for C# ValueTuple types.
    /// </summary>
    private static bool IsVavrTupleType(INamedTypeSymbol type)
    {
        var display = type.ToDisplayString();
        return display.StartsWith("io.vavr.Tuple") && display.Contains("<");
    }

    /// <summary>
    /// Returns true if the type is <see cref="System.Exception"/> or derives from it.
    /// </summary>
    private static bool IsOrInheritsFromException(ITypeSymbol? type)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            if (current.ToDisplayString() == "System.Exception")
                return true;
        }
        return false;
    }
}
