using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ReadOnlyStructMaker;

/// <summary>
/// Computes the set of source-declared structs whose instances can reach an
/// <see cref="object"/>-like context (boxing into <c>object</c>/<c>ValueType</c>,
/// assignment to <c>object</c>/<c>dynamic</c>, etc.), directly or transitively
/// through the member-type graph of boxed types. Interface conversions are not
/// considered roots: polymorphic interface use does not imply reflection writes.
///
/// Rationale: once a mutable struct's value is reachable through a boxed object
/// graph, reflection-based writers (serializers, mappers, ORMs, source-generated
/// descriptors) may assign its fields/properties at runtime. Converting such a
/// struct to readonly removes those write targets and breaks the writers, so the
/// readonly conversion must skip these structs.
/// </summary>
internal static class BoxingReachabilityAnalyzer
{
    /// <summary>
    /// Returns the original definitions of source-declared structs that are
    /// reachable from an object-like context within <paramref name="compilation"/>.
    /// </summary>
    public static HashSet<INamedTypeSymbol> ComputeBoxingReachableStructs(Compilation compilation)
    {
        var reachable = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var queue = new Queue<ITypeSymbol>();

        void Enqueue(ITypeSymbol? type)
        {
            if (type == null) return;
            type = Unwrap(type);
            if (type == null) return;
            if (reachable.Add(type))
            {
                queue.Enqueue(type);
            }
        }

        // Pass 1: find direct boxing/object-like conversion roots.
        foreach (var tree in compilation.SyntaxTrees)
        {
            SemanticModel? model = null;
            SemanticModel GetModel() => model ??= compilation.GetSemanticModel(tree);

            var root = tree.GetRoot();

            foreach (var node in root.DescendantNodes())
            {
                switch (node)
                {
                    case ArgumentSyntax argument:
                        HandleArgument(argument, GetModel(), compilation, Enqueue);
                        break;

                    case VariableDeclaratorSyntax declarator when declarator.Initializer?.Value != null:
                        if (IsObjectLikeDeclaredType(GetDeclaredType(declarator, GetModel()), out var declaredType) &&
                            declaredType != null)
                        {
                            var valueType = GetModel().GetTypeInfo(declarator.Initializer.Value).Type;
                            if (IsUserCodeType(valueType))
                            {
                                Enqueue(valueType);
                            }
                        }
                        break;

                    case AssignmentExpressionSyntax assignment:
                        {
                            var targetType = GetModel().GetTypeInfo(assignment.Left).Type;
                            if (IsObjectLike(targetType))
                            {
                                var valueType = GetModel().GetTypeInfo(assignment.Right).Type;
                                if (IsUserCodeType(valueType))
                                {
                                    Enqueue(valueType);
                                }
                            }
                        }
                        break;

                    case CastExpressionSyntax cast:
                        {
                            var targetType = GetModel().GetTypeInfo(cast.Type).Type;
                            if (IsObjectLike(targetType))
                            {
                                var valueType = GetModel().GetTypeInfo(cast.Expression).Type;
                                if (IsUserCodeType(valueType))
                                {
                                    Enqueue(valueType);
                                }
                            }
                        }
                        break;

                    case ReturnStatementSyntax { Expression: not null } ret:
                        {
                            var enclosing = ret.FirstAncestorOrSelf<MethodDeclarationSyntax>();
                            if (enclosing != null)
                            {
                                var method = GetModel().GetDeclaredSymbol(enclosing);
                                if (method != null && IsObjectLike(method.ReturnType))
                                {
                                    var valueType = GetModel().GetTypeInfo(ret.Expression).Type;
                                    if (IsUserCodeType(valueType))
                                    {
                                        Enqueue(valueType);
                                    }
                                }
                            }
                        }
                        break;

                    case TypeArgumentListSyntax typeArgs:
                        // Generic type arguments to serializer-family APIs:
                        // `JsonSerializer.Deserialize<T>(json)` / `Serializer.DeserializeWrapper<T>(json)`.
                        // The struct flows into reflection-based writers inside the library,
                        // which the call site cannot see, so treat it as boxed.
                        if (IsSerializerFamilyMethod(typeArgs, GetModel(), compilation, out var serializerTypeArg))
                        {
                            Enqueue(serializerTypeArg);
                        }
                        break;

                    case TypeOfExpressionSyntax typeOf:
                        // typeof(T) arguments: `JsonSerializer.Deserialize(json, type, options)`.
                        if (IsArgumentToSerializerFamily(typeOf, GetModel(), out var typeOfArgType))
                        {
                            Enqueue(typeOfArgType);
                        }
                        break;
                }
            }
        }

        // Pass 2: propagate through the member-type graph of reachable types.
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            // Type arguments propagate for every type (e.g. Dictionary<int, Payload>
            // exposes Payload; List<Point> exposes Point).
            if (current is INamedTypeSymbol named)
            {
                foreach (var typeArg in named.TypeArguments)
                {
                    if (IsUserCodeType(typeArg))
                    {
                        Enqueue(typeArg);
                    }
                }

                // Instance members only propagate for source-declared types; walking
                // BCL member graphs would over-approximate and explode the set.
                if (IsSourceDeclared(named))
                {
                    foreach (var member in named.GetMembers())
                    {
                        ITypeSymbol? memberType = member switch
                        {
                            IFieldSymbol field when !field.IsStatic && !field.IsConst => field.Type,
                            IPropertySymbol { IsStatic: false, IsIndexer: false } prop => prop.Type,
                            _ => null,
                        };
                        if (memberType != null && IsUserCodeType(memberType))
                        {
                            Enqueue(memberType);
                        }
                    }
                }
            }
        }

        // Keep only source-declared struct definitions.
        var result = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var type in reachable)
        {
            if (type.OriginalDefinition is INamedTypeSymbol def &&
                def.IsValueType && def.TypeKind == TypeKind.Struct && IsSourceDeclared(def))
            {
                result.Add(def);
            }
        }

        return result;
    }

    private static void HandleArgument(
        ArgumentSyntax argument,
        SemanticModel model,
        Compilation compilation,
        Action<ITypeSymbol?> enqueue)
    {
        if (argument.Parent is not BaseArgumentListSyntax argList) return;
        var parent = argList.Parent;
        if (parent == null) return;

        var index = argList.Arguments.IndexOf(argument);
        IMethodSymbol? method = null;

        switch (parent)
        {
            case InvocationExpressionSyntax invocation:
                method = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                if (method != null && method.MethodKind == MethodKind.ReducedExtension)
                {
                    // Reduced extension call: receiver is not part of the argument list.
                    index += 1;
                }
                break;

            case ObjectCreationExpressionSyntax creation:
                method = model.GetSymbolInfo(creation).Symbol as IMethodSymbol;
                break;

            case ImplicitElementAccessSyntax elementAccess:
                // Collection/dictionary initializer element: binds to Add()/indexer.
                method = model.GetSymbolInfo(elementAccess).Symbol as IMethodSymbol;
                break;

            default:
                return;
        }

        if (method == null || index < 0 || index >= method.Parameters.Length) return;

        var parameter = method.Parameters[index];
        if (!IsObjectLike(parameter.Type))
        {
            // Generic parameter: `JsonSerializer.Serialize<T>(T value)` — the value
            // is sunk into reflection-based writers inside the library.
            if (parameter.Type is ITypeParameterSymbol && IsSerializerFamily(method.ContainingType))
            {
                var genericValueType = model.GetTypeInfo(argument.Expression).Type;
                if (IsUserCodeType(genericValueType))
                {
                    enqueue(genericValueType);
                }
            }
            return;
        }
        if (IsHarmlessFormattingSink(method)) return;

        var valueType = model.GetTypeInfo(argument.Expression).Type;
        if (IsUserCodeType(valueType))
        {
            enqueue(valueType);
        }
    }

    /// <summary>
    /// True when <paramref name="typeArgs"/> belongs to an invocation of a generic
    /// method on a serializer-family type (e.g. <c>JsonSerializer.Deserialize&lt;T&gt;</c>
    /// or a test helper <c>Serializer.DeserializeWrapper&lt;T&gt;</c> that forwards to
    /// one). Values passed as generic arguments to such APIs are processed by
    /// reflection-based readers/writers, so their mutable state must be preserved.
    /// </summary>
    private static bool IsSerializerFamilyMethod(
        TypeArgumentListSyntax typeArgs,
        SemanticModel model,
        Compilation compilation,
        out ITypeSymbol? declaredType)
    {
        declaredType = null;
        var parent = typeArgs.Parent;
        if (parent is not GenericNameSyntax genericName) return false;

        if (genericName.Parent is MemberAccessExpressionSyntax ma &&
            ma.Parent is InvocationExpressionSyntax invocation)
        {
            var symbol = model.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (symbol == null) return false;
            if (!IsSerializerFamily(symbol.ContainingType)) return false;
            declaredType = model.GetTypeInfo(genericName.TypeArgumentList.Arguments[0]).Type;
            return true;
        }

        // Bare generic invocation: DeserializeWrapper<T>(json) inside the same file.
        if (genericName.Parent is InvocationExpressionSyntax bareInvocation)
        {
            var symbol = model.GetSymbolInfo(bareInvocation).Symbol as IMethodSymbol;
            if (symbol == null) return false;
            if (!IsSerializerFamily(symbol.ContainingType)) return false;
            declaredType = model.GetTypeInfo(genericName.TypeArgumentList.Arguments[0]).Type;
            return true;
        }

        return false;
    }

    /// <summary>
    /// True for types whose name suggests reflection-based (de)serialization:
    /// JsonSerializer, XmlSerializer, Deserializer, Mapper, etc.
    /// </summary>
    private static bool IsSerializerFamily(INamedTypeSymbol? type)
    {
        if (type == null) return false;
        var name = type.Name;
        return name.IndexOf("Serializ", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("Deserializ", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.Equals("Mapper", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when <paramref name="typeOf"/> is the <c>typeof(T)</c> argument of an
    /// invocation on a serializer-family type. Returns the argument's type.
    /// </summary>
    private static bool IsArgumentToSerializerFamily(
        TypeOfExpressionSyntax typeOf,
        SemanticModel model,
        out ITypeSymbol? type)
    {
        type = null;
        if (typeOf.Parent is not ArgumentSyntax argument ||
            argument.Parent is not BaseArgumentListSyntax argList ||
            argList.Parent is not InvocationExpressionSyntax invocation)
        {
            return false;
        }
        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method) return false;
        if (!IsSerializerFamily(method.ContainingType)) return false;
        type = model.GetTypeInfo(typeOf.Type).Type;
        return true;
    }

    /// <summary>
    /// True for well-known read-only formatting/logging sinks that box values solely
    /// to call <c>ToString()</c>. Boxing into these APIs never leads to reflection
    /// mutation, so they must not suppress the readonly conversion.
    /// </summary>
    private static bool IsHarmlessFormattingSink(IMethodSymbol method)
    {
        var containing = method.ContainingType?.OriginalDefinition.ToDisplayString();
        if (containing == null) return false;

        switch (containing)
        {
            case "System.String":
                return method.Name is "Format" or "Concat" or "Join";
            case "System.Console":
                return method.Name.StartsWith("Write", StringComparison.Ordinal);
            case "System.Text.StringBuilder":
                return method.Name is "Append" or "AppendLine" or "AppendFormat" or "Insert";
            case "System.IO.TextWriter":
                return method.Name.StartsWith("Write", StringComparison.Ordinal);
            case "System.Diagnostics.Debug":
            case "System.Diagnostics.Trace":
                return true;
            case "object":
                return method.Name is "Equals" or "ReferenceEquals" or "GetHashCode" or "ToString";
            default:
                return false;
        }
    }

    /// <summary>Unwraps arrays and <see cref="Nullable{T}"/> to the underlying type.</summary>
    private static ITypeSymbol? Unwrap(ITypeSymbol type)
    {
        while (true)
        {
            switch (type)
            {
                case IArrayTypeSymbol array:
                    type = array.ElementType;
                    continue;
                case INamedTypeSymbol { IsGenericType: true } named
                    when named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T:
                    type = named.TypeArguments[0];
                    continue;
                default:
                    return type;
            }
        }
    }

    /// <summary>
    /// True when the type represents an object-like context that can expose values
    /// to reflection: <c>object</c>, <c>ValueType</c>, <c>Enum</c>, or <c>dynamic</c>.
    /// Interface conversions are deliberately excluded: boxing a struct to an
    /// interface for polymorphism is normal and does not imply reflection-based
    /// member writes, whereas serializers/mappers sink values into <c>object</c>.
    /// </summary>
    private static bool IsObjectLike(ITypeSymbol? type)
    {
        if (type == null) return false;
        if (type.SpecialType == SpecialType.System_Object) return true;
        if (type is IDynamicTypeSymbol) return true;
        return type is INamedTypeSymbol named &&
               named.ToDisplayString() is "System.ValueType" or "System.Enum";
    }

    private static bool IsObjectLikeDeclaredType(ITypeSymbol? declaredType, out ITypeSymbol? result)
    {
        result = declaredType;
        return IsObjectLike(declaredType);
    }

    private static ITypeSymbol? GetDeclaredType(VariableDeclaratorSyntax declarator, SemanticModel model)
    {
        var declaration = declarator.FirstAncestorOrSelf<VariableDeclarationSyntax>();
        if (declaration == null) return null;
        return model.GetTypeInfo(declaration.Type).Type;
    }

    /// <summary>
    /// True for user-defined value/reference types worth tracking: non-BCL structs,
    /// classes, and arrays/nullables thereof. Primitives, strings, and other special
    /// types are excluded.
    /// </summary>
    private static bool IsUserCodeType(ITypeSymbol? type)
    {
        if (type == null) return false;
        type = Unwrap(type)!;
        if (type.SpecialType != SpecialType.None) return false;
        if (type is IPointerTypeSymbol) return false;
        if (type.TypeKind == TypeKind.TypeParameter) return false;
        return type.TypeKind is TypeKind.Struct or TypeKind.Class;
    }

    private static bool IsSourceDeclared(INamedTypeSymbol type)
    {
        return type.DeclaringSyntaxReferences.Length > 0;
    }
}
