using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Transformers.Type;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Utility class for argument list transformation.
/// Not dispatched through ExpressionTransformerRegistry; used directly by other transformers.
/// </summary>
public class ArgumentTransformer
{
    // ArgumentTransformer is a special case - it doesn't handle specific SyntaxKinds directly
    // but is used by other transformers for argument list transformation.
    // Touched by ExpressionTransformerRegistry to allow unit testing and explicit invocation.
    static ArgumentTransformer() { }

    private static readonly Lazy<ArgumentTransformer> _instance = new(() => new());
    public static ArgumentTransformer Instance => _instance.Value;

    /// <summary>
    /// Transforms an argument expression directly via the expression facade.
    /// ArgumentTransformer is a utility class; use TransformArgumentList for full argument lists.
    /// </summary>
    public string Transform(ExpressionSyntax node, ConversionContext context)
        => ExpressionTransformerFacade.Instance.Transform(node, context);

    /// <summary>
    /// Transforms an argument list to Java code.
    /// Named arguments are reordered to positional order using the semantic model when available.
    /// out/ref/in arguments are handled with the holder-object pattern or pass-by-value respectively.
    /// </summary>
    /// <param name="argStartIndex">
    /// Issue 5: index of the first argument to include. Pass 1 when in the static-extension-receiver
    /// call path to skip the receiver that has already been prepended to the argument list.
    /// </param>
    public static string TransformArgumentList(ArgumentListSyntax? argumentList, ConversionContext context, IExpressionTransformer transformer, int argStartIndex = 0, IMethodSymbol? methodSymbol = null)
    {
        if (argumentList == null) return "";

        var args = argumentList.Arguments;
        if (args.Count <= argStartIndex) return "";

        // Issue 5: slice from argStartIndex when in the static-extension-receiver path
        IReadOnlyList<ArgumentSyntax> relevantArgs = argStartIndex > 0
            ? args.Skip(argStartIndex).ToList()
            : args.ToList();

        var orderedArgs = ReorderNamedArguments(relevantArgs, argumentList, context);

        // Build parameter list from the method symbol (accounting for extension methods and argStartIndex)
        ImmutableArray<IParameterSymbol>? parameters = null;
        if (methodSymbol != null)
        {
            parameters = methodSymbol.Parameters;
        }

        var transformed = orderedArgs.Select((arg, index) =>
        {
            var result = TransformSingleArgument(arg, context, transformer);

            // Apply type coercion when we have parameter type information
            if (parameters.HasValue && context.SemanticModel != null)
            {
                // For extension methods in static path, parameters are shifted by 1
                // (first param is the receiver). For instance-call form, argStartIndex is 0.
                int paramIndex = index;
                // Handle params (varargs): last param absorbs remaining args
                if (paramIndex >= parameters.Value.Length && parameters.Value.Length > 0
                    && parameters.Value[^1].IsParams)
                {
                    paramIndex = parameters.Value.Length - 1;
                }
                if (paramIndex < parameters.Value.Length)
                {
                    result = CoerceArgumentType(arg, result, parameters.Value[paramIndex], context);
                }
            }
            else
            {
                result = CoerceEnumerableStreamFallback(arg, result, context);
            }

            return result;
        });
        return string.Join(", ", transformed);
    }

    /// <summary>
    /// Reorders named arguments to match the target method's positional parameter order.
    /// Returns the original order when the semantic model is unavailable or the symbol cannot be resolved.
    /// </summary>
    private static IReadOnlyList<ArgumentSyntax> ReorderNamedArguments(
        IReadOnlyList<ArgumentSyntax> args,
        ArgumentListSyntax argumentList,
        ConversionContext context)
    {
        if (!args.Any(a => a.NameColon != null))
            return args.ToList();

        if (context.SemanticModel == null || argumentList.Parent == null)
            return args.ToList();

        var symbolInfo = context.SemanticModel.GetSymbolInfo(argumentList.Parent);
        if (symbolInfo.Symbol is not IMethodSymbol methodSymbol)
            return args.ToList();

        var parameters = methodSymbol.Parameters;
        var result = new ArgumentSyntax?[parameters.Length];

        // Place named arguments at their declared parameter positions
        foreach (var arg in args)
        {
            if (arg.NameColon == null) continue;
            var paramName = arg.NameColon.Name.Identifier.Text;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].Name == paramName)
                {
                    result[i] = arg;
                    break;
                }
            }
        }

        // Fill remaining slots with positional arguments in order
        var positional = args.Where(a => a.NameColon == null).ToList();
        int pi = 0;
        for (int i = 0; i < result.Length && pi < positional.Count; i++)
        {
            if (result[i] == null)
                result[i] = positional[pi++];
        }

        return result.Where(a => a != null).Cast<ArgumentSyntax>().ToList();
    }

    /// <summary>
    /// Transforms a single argument to its Java representation.
    /// Handles ref/out/in modifiers; out var uses the holder-object pattern via pre-statements.
    /// </summary>
    private static string TransformSingleArgument(ArgumentSyntax arg, ConversionContext context, IExpressionTransformer transformer)
    {
        var refKind = arg.RefKindKeyword.Kind();

        if (refKind == SyntaxKind.OutKeyword)
        {
            // out _ (discard) — pass a scratch one-element array; the result is intentionally unused
            if (arg.Expression is DeclarationExpressionSyntax { Designation: DiscardDesignationSyntax })
                return "new Object[1]";

            // Bare _ identifier used as a discard
            if (arg.Expression is IdentifierNameSyntax { Identifier.Text: "_" })
                return "new Object[1]";

            // out var result — holder-object pattern: declare HolderType _resultHolder = new HolderType(); before the call
            // and read back the value after: javaType result = _resultHolder.value;
            if (arg.Expression is DeclarationExpressionSyntax outDecl &&
                outDecl.Designation is SingleVariableDesignationSyntax svd)
            {
                var varName = svd.Identifier.Text;
                var holderName = context.GenerateSyntheticName($"_{varName}Holder");
                var javaType = ResolveOutVarType(outDecl, context);
                var holderType = DelegateTransformer.GetHolderType(javaType);
                var holderInit = GetHolderInstantiation(holderType);
                context.AddPreStatement($"{holderType} {holderName} = {holderInit}");
                context.SetActiveRefHolder(varName, holderName);
                context.AddPostStatement($"{javaType} {varName} = {holderName}.value");
                return holderName;
            }

            // out existingVar — use Holder class matching the variable's type
            // and read back the updated value into the existing variable after the call
            if (arg.Expression is IdentifierNameSyntax ident)
            {
                // If the identifier is already a ref/out parameter (i.e. already a holder),
                // pass it directly — same as the ref-forwarding fix (Bug 1).
                if (context.SemanticModel?.GetSymbolInfo(ident).Symbol is IParameterSymbol outParam
                    && (outParam.RefKind == RefKind.Ref || outParam.RefKind == RefKind.Out))
                {
                    return ConversionContext.EscapeJavaKeyword(outParam.Name);
                }

                var varName = ident.Identifier.Text;
                var holderName = context.GenerateSyntheticName($"_{varName}Holder");
                var javaType = "Object";
                if (context.SemanticModel != null)
                {
                    var typeInfo = context.SemanticModel.GetTypeInfo(ident);
                    if (typeInfo.Type != null)
                        javaType = context.MapType(typeInfo.Type);
                }
                var holderType = DelegateTransformer.GetHolderType(javaType);
                var holderInit = GetHolderInstantiation(holderType);
                context.AddPreStatement($"{holderType} {holderName} = {holderInit}");
                context.SetActiveRefHolder(varName, holderName);
                context.AddPostStatement($"{varName} = {holderName}.value");
                return holderName;
            }

            // Fallback for other out expressions
            context.Diagnostics.Warning("out parameter has no direct Java equivalent", arg.GetLocation());
            return $"/* out */ {transformer.Transform(arg.Expression, context)}";
        }

        if (refKind == SyntaxKind.RefKeyword)
        {
            // Bug 1: if the argument is already a ref/out parameter (e.g. forwarding ref d2 to another ref method),
            // the IdentifierExpressionTransformer would emit "d2.value" — but we must pass the holder itself.
            if (arg.Expression is IdentifierNameSyntax refIdent)
            {
                if (context.SemanticModel?.GetSymbolInfo(refIdent).Symbol is IParameterSymbol refParam
                    && (refParam.RefKind == RefKind.Ref || refParam.RefKind == RefKind.Out))
                {
                    // Already a holder — pass it directly with no wrapping.
                    return ConversionContext.EscapeJavaKeyword(refParam.Name);
                }

                // Bug 2: local variable (or non-ref parameter) passed as ref — wrap it in a holder
                // so it can be mutated by the callee and the new value written back afterward.
                var varName = refIdent.Identifier.Text;

                // Bug 4: if a holder is already active for this variable (e.g. the same local is passed
                // as ref a second time before the first holder's writeback has been drained), reuse the
                // existing holder instead of allocating a new one — which would produce a duplicate Java
                // variable declaration and a duplicate writeback post-statement.
                if (context.TryGetActiveRefHolder(varName, out var existingHolder))
                    return existingHolder;

                var refHolderName = context.AllocateRefHolderName(varName);
                var javaType = "Object";
                if (context.SemanticModel != null)
                {
                    var typeInfo = context.SemanticModel.GetTypeInfo(refIdent);
                    if (typeInfo.Type != null)
                        javaType = context.MapType(typeInfo.Type);
                }
                var refHolderType = DelegateTransformer.GetHolderType(javaType);
                var refHolderInit = refHolderType.StartsWith("ObjectHolder<")
                    ? $"new ObjectHolder<>({varName})"
                    : $"new {refHolderType}({varName})";
                context.AddPreStatement($"{refHolderType} {refHolderName} = {refHolderInit}");
                context.AddPostStatement($"{ConversionContext.EscapeJavaKeyword(varName)} = {refHolderName}.value");
                return refHolderName;
            }

            // Member/element ref argument: wrap expression value in a holder and write back after call.
            // Example: RefMethod(ref obj.field) -> Holder h = new Holder(obj.field); RefMethod(h); obj.field = h.value;
            if (arg.Expression is MemberAccessExpressionSyntax or ElementAccessExpressionSyntax)
            {
                var exprText = transformer.Transform(arg.Expression, context);
                var holderName = context.GenerateSyntheticName("_refArgHolder");

                var javaType = "Object";
                if (context.SemanticModel != null)
                {
                    var typeInfo = context.SemanticModel.GetTypeInfo(arg.Expression);
                    if (typeInfo.Type != null)
                        javaType = context.MapType(typeInfo.Type);
                }

                var holderType = DelegateTransformer.GetHolderType(javaType);
                var holderInit = holderType.StartsWith("ObjectHolder<")
                    ? $"new ObjectHolder<>({exprText})"
                    : $"new {holderType}({exprText})";

                context.AddPreStatement($"{holderType} {holderName} = {holderInit}");
                context.AddPostStatement($"{exprText} = {holderName}.value");
                return holderName;
            }

            // Complex expression (e.g. ref field, ref array element) — no holder support; pass by value.
            context.Diagnostics.Warning("ref argument with complex expression has no direct Java equivalent; passing by value", arg.GetLocation());
            return transformer.Transform(arg.Expression, context);
        }

        if (refKind == SyntaxKind.InKeyword)
        {
            // 'in' is read-only — pass the value directly (no write-back needed).
            return transformer.Transform(arg.Expression, context);
        }

        return transformer.Transform(arg.Expression, context);
    }

    /// <summary>
    /// Returns the Java constructor call for a holder type.
    /// Generic ObjectHolder&lt;T&gt; uses diamond type inference; primitive holders use default constructor.
    /// </summary>
    private static string GetHolderInstantiation(string holderType)
    {
        if (holderType.StartsWith("ObjectHolder<"))
            return "new ObjectHolder<>()";
        return $"new {holderType}()";
    }

    /// <summary>
    /// Coerces an argument expression when the C# argument type does not directly match
    /// the Java target parameter type. Handles three scenarios:
    /// 1. Array passed where Iterable/Collection is expected → Arrays.asList(...) or stream boxing
    /// 2. IEnumerable (Iterable) passed where Java method needs Collection → wrap to materialize
    /// 3. byte/short parameter receives an int/long literal → insert narrowing cast (byte)/short)
    ///    (C# allows this implicitly, Java requires explicit cast)
    /// </summary>
    private static string CoerceArgumentType(
        ArgumentSyntax arg,
        string transformedExpr,
        IParameterSymbol targetParam,
        ConversionContext context)
    {
        // Skip ref/out/in arguments — they have their own handling
        if (arg.RefKindKeyword.Kind() is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword or SyntaxKind.InKeyword)
            return transformedExpr;

        if (context.SemanticModel == null)
            return transformedExpr;

        var argType = context.SemanticModel.GetTypeInfo(arg.Expression).Type;
        if (argType == null)
            return transformedExpr;

        var paramType = targetParam.Type;

        // Enum parameter + integral argument: map ordinal to enum constant.
        // C# allows passing 0 / int where enum is expected in some contexts; Java requires explicit enum value.
        if (paramType.TypeKind == TypeKind.Enum
            && argType.TypeKind != TypeKind.Enum
            && argType.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int16
                or SpecialType.System_Int64 or SpecialType.System_Byte or SpecialType.System_SByte
                or SpecialType.System_UInt16 or SpecialType.System_UInt32 or SpecialType.System_UInt64)
        {
            var javaEnumType = context.MapType(paramType);
            if (!string.IsNullOrWhiteSpace(javaEnumType))
                return $"{javaEnumType}.values()[(int)({transformedExpr})]";
        }

        // ── Case 1: Array argument → parameter expects IEnumerable/ICollection/IList ──
        // In Java, arrays don't implement Iterable or Collection, so we must wrap.
        if (argType is IArrayTypeSymbol argArrayType && paramType is INamedTypeSymbol paramNamed
            && IsEnumerableOrCollectionInterface(paramNamed))
        {
            // Primitive arrays (int[], long[], double[]) need boxing for generics
            if (argArrayType.ElementType.IsValueType && IsPrimitiveSpecialType(argArrayType.ElementType.SpecialType))
            {
                context.AddImport("java.util.Arrays");
                context.AddImport("java.util.stream.Collectors");
                return $"java.util.Arrays.stream({transformedExpr}).boxed().collect(java.util.stream.Collectors.toList())";
            }

            // Reference type arrays: Arrays.asList() works directly
            context.AddImport("java.util.Arrays");
            return $"Arrays.asList({transformedExpr})";
        }

        // ── Case 2: IEnumerable (Iterable) argument → Java target needs Collection ──
        // C# AddRange(IEnumerable<T>) maps to Java addAll(Collection<T>).
        // C# IEnumerable<T> maps to Iterable<T> which does NOT extend Collection.
        // Also applies to ICollection/IList params where the arg is IEnumerable (Iterable).
        if (argType is INamedTypeSymbol argNamed && !IsCollectionType(argType))
        {
            bool argIsEnumerable = argNamed.Name is "IEnumerable"
                && argNamed.ContainingNamespace?.ToDisplayString().StartsWith("System") == true;
            // Check: does the C# parameter itself or the Java target require Collection?
            bool javaTargetNeedsCollection = false;

            if (paramType is INamedTypeSymbol paramNamed2)
            {
                // Direct: C# param is ICollection/IList → Java param is Collection/List
                if (paramNamed2.Name is "ICollection" or "IList" or "IReadOnlyCollection" or "IReadOnlyList"
                    && paramNamed2.ContainingNamespace?.ToDisplayString().StartsWith("System") == true)
                {
                    javaTargetNeedsCollection = true;
                }
                // Indirect: C# param is IEnumerable but Java mapped method needs Collection
                // (e.g. AddRange(IEnumerable<T>) → addAll(Collection<T>))
                else if (argIsEnumerable && paramNamed2.Name is "IEnumerable"
                    && IsJavaMethodRequiringCollection(targetParam.ContainingSymbol as IMethodSymbol, context))
                {
                    javaTargetNeedsCollection = true;
                }

                // C# constructors like List<T>(IEnumerable<T>) map to Java constructors
                // that require Collection, not Iterable.
                else if (argIsEnumerable && paramNamed2.Name is "IEnumerable"
                    && IsJavaCollectionConstructor(targetParam.ContainingSymbol as IMethodSymbol))
                {
                    javaTargetNeedsCollection = true;
                }

                // If target is IEnumerable<T> but the transformed argument is already a Java Stream,
                // materialize it so Java receives an Iterable/Collection value.
                if (argIsEnumerable && paramNamed2.Name is "IEnumerable"
                    && LooksLikeJavaStreamExpression(transformedExpr))
                {
                    javaTargetNeedsCollection = true;
                }
            }

            if (argIsEnumerable && javaTargetNeedsCollection)
            {
                context.AddImport("java.util.ArrayList");
                context.AddImport("java.util.stream.StreamSupport");
                context.AddImport("java.util.stream.Collectors");
                if (LooksLikeJavaStreamExpression(transformedExpr))
                {
                    if (transformedExpr.Contains(".collect(", StringComparison.Ordinal))
                    {
                        return transformedExpr;
                    }
                    return $"{transformedExpr}.collect(Collectors.toList())";
                }
                return $"StreamSupport.stream({transformedExpr}.spliterator(), false).collect(Collectors.toCollection(ArrayList::new))";
            }
        }

        // ── Case 3: byte/short parameter receives a wider integer (int/long) ──
        // C# allows implicit narrowing of constant integer expressions to byte/short/sbyte/ushort.
        // Java does NOT — an int literal passed to a (byte) or (short) parameter is a compile error.
        // Insert the required explicit cast so the generated Java compiles.
        var javaCast = GetNarrowingCast(paramType.SpecialType, argType.SpecialType);
        if (javaCast != null)
            return $"({javaCast}) {transformedExpr}";

        return transformedExpr;
    }

    /// <summary>
    /// Returns the Java narrowing cast keyword to insert when passing a wider integer to a
    /// narrower parameter type, or <c>null</c> when no cast is needed.
    /// </summary>
    private static string? GetNarrowingCast(SpecialType paramSpecial, SpecialType argSpecial)
    {
        // Only insert a cast when the argument is a wider integer type
        bool argIsWiderInt = argSpecial is SpecialType.System_Int32
            or SpecialType.System_UInt32
            or SpecialType.System_Int64
            or SpecialType.System_UInt64;

        if (!argIsWiderInt) return null;

        return paramSpecial switch
        {
            // byte and sbyte both map to Java's 'byte' (signed 8-bit)
            SpecialType.System_Byte or SpecialType.System_SByte => "byte",
            // short and ushort both map to Java's 'short' (signed 16-bit)
            SpecialType.System_Int16 or SpecialType.System_UInt16 => "short",
            _ => null
        };
    }

    /// <summary>
    /// Checks if a C# method maps to a Java method that requires Collection parameters
    /// (e.g. AddRange → addAll, which takes Collection not Iterable).
    /// </summary>
    private static bool IsJavaMethodRequiringCollection(IMethodSymbol? method, ConversionContext context)
    {
        if (method == null) return false;
        var typeName = method.ContainingType.ToDisplayString();
        var mapped = context.TypeMappings.MapMethod(typeName, method.Name);
        if (mapped == null)
        {
            var fqn = $"{method.ContainingType.ContainingNamespace}.{method.ContainingType.Name}";
            mapped = context.TypeMappings.MapMethod(fqn, method.Name);
        }
        // Java Collection methods that take Collection<E> as parameter
        return mapped is "addAll" or "removeAll" or "containsAll" or "retainAll";
    }

    private static bool IsEnumerableOrCollectionInterface(INamedTypeSymbol type)
        => type.Name is "IEnumerable" or "ICollection" or "IList" or "IReadOnlyCollection" or "IReadOnlyList"
           && type.ContainingNamespace?.ToDisplayString().StartsWith("System") == true;

    private static bool IsPrimitiveSpecialType(SpecialType st)
        => st is SpecialType.System_Int32 or SpecialType.System_Int16 or SpecialType.System_Byte
            or SpecialType.System_Int64 or SpecialType.System_Double or SpecialType.System_Single
            or SpecialType.System_Boolean or SpecialType.System_Char;

    private static bool IsCollectionType(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol named)
        {
            if (named.Name is "ICollection" or "IList" or "List" or "HashSet" or "SortedSet"
                or "IReadOnlyCollection" or "IReadOnlyList" or "Collection")
                return true;
            return named.AllInterfaces.Any(i =>
                i.OriginalDefinition?.ToDisplayString() is "System.Collections.Generic.ICollection<T>" or "System.Collections.ICollection");
        }
        return false;
    }

    private static bool IsJavaCollectionConstructor(IMethodSymbol? method)
    {
        if (method == null || method.MethodKind != MethodKind.Constructor)
            return false;

        var typeName = method.ContainingType.Name;
        return typeName is "List" or "HashSet" or "SortedSet" or "LinkedList" or "Queue";
    }

    private static bool LooksLikeJavaStreamExpression(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr))
            return false;

        return expr.Contains("java.util.stream.Stream.", StringComparison.Ordinal)
            || expr.Contains(".stream()", StringComparison.Ordinal)
            || expr.Contains(".map(", StringComparison.Ordinal)
            || expr.Contains(".filter(", StringComparison.Ordinal)
            || expr.Contains(".flatMap(", StringComparison.Ordinal)
            || expr.Contains(".sorted(", StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves the Java type name for an out var declaration.
    /// Uses the semantic model when available; falls back to syntax-based mapping.
    /// </summary>
    private static string ResolveOutVarType(DeclarationExpressionSyntax decl, ConversionContext context)
    {
        if (context.SemanticModel != null)
        {
            var typeInfo = context.SemanticModel.GetTypeInfo(decl.Type);
            if (typeInfo.Type != null)
                return context.MapType(typeInfo.Type);
        }
        return context.MapTypeFromSyntax(decl.Type);
    }

    private static string CoerceEnumerableStreamFallback(ArgumentSyntax arg, string transformedExpr, ConversionContext context)
    {
        if (context.SemanticModel == null)
            return transformedExpr;

        var argType = context.SemanticModel.GetTypeInfo(arg.Expression).Type as INamedTypeSymbol;
        if (argType == null)
            return transformedExpr;

        bool argIsEnumerable = argType.Name == "IEnumerable"
            && argType.ContainingNamespace?.ToDisplayString().StartsWith("System") == true;

        if (!argIsEnumerable || !LooksLikeJavaStreamExpression(transformedExpr))
            return transformedExpr;

        context.AddImport("java.util.stream.Collectors");
        if (transformedExpr.Contains(".collect(", StringComparison.Ordinal))
            return transformedExpr;

        return $"{transformedExpr}.collect(Collectors.toList())";
    }
}
