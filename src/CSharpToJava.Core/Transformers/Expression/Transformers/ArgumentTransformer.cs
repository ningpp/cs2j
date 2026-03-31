using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Transformers;
using CSharpToJava.Core.Transformers.Expression.Utilities;
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
    public static string TransformArgumentList(ArgumentListSyntax? argumentList, ConversionContext context, IExpressionTransformer transformer, int argStartIndex = 0, IMethodSymbol? methodSymbol = null, int maxArgCount = -1)
    {
        if (argumentList == null) return "";

        var args = argumentList.Arguments;
        if (args.Count <= argStartIndex) return "";

        // When maxArgCount is specified, limit the arguments taken from the list
        int effectiveEnd = maxArgCount >= 0 ? Math.Min(args.Count, maxArgCount) : args.Count;

        // Issue 5: slice from argStartIndex when in the static-extension-receiver path
        IReadOnlyList<ArgumentSyntax> relevantArgs = (argStartIndex > 0 || effectiveEnd < args.Count)
            ? args.Skip(argStartIndex).Take(effectiveEnd - argStartIndex).ToList()
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
            IParameterSymbol? currentParam = null;
            if (parameters.HasValue)
            {
                int paramIndexForRefKind = index;
                if (paramIndexForRefKind >= parameters.Value.Length && parameters.Value.Length > 0
                    && parameters.Value[^1].IsParams)
                {
                    paramIndexForRefKind = parameters.Value.Length - 1;
                }
                if (paramIndexForRefKind < parameters.Value.Length)
                    currentParam = parameters.Value[paramIndexForRefKind];
            }

            var result = TransformSingleArgument(arg, context, transformer, currentParam);

            result = ApplyStructValueCopyIfNeeded(arg, result, currentParam, context);

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
    private static string TransformSingleArgument(ArgumentSyntax arg, ConversionContext context, IExpressionTransformer transformer, IParameterSymbol? parameterSymbol = null)
    {
        var refKind = arg.RefKindKeyword.Kind();
        if (refKind == SyntaxKind.None && parameterSymbol != null)
        {
            refKind = parameterSymbol.RefKind switch
            {
                RefKind.Out => SyntaxKind.OutKeyword,
                RefKind.Ref => SyntaxKind.RefKeyword,
                RefKind.In => SyntaxKind.InKeyword,
                _ => SyntaxKind.None
            };
        }

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
                var holderName = context.AllocateOutHolderName(varName);
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
                var holderName = context.AllocateOutHolderName(varName);
                var javaType = "Object";
                if (context.SemanticModel != null)
                {
                    var typeInfo = context.SemanticModel.GetTypeInfo(ident);
                    if (typeInfo.Type != null && typeInfo.Type is not IErrorTypeSymbol)
                        javaType = context.MapType(typeInfo.Type);
                }
                // Fallback to parameter type when expression type is unresolved
                if (javaType == "Object" && parameterSymbol != null)
                {
                    if (parameterSymbol.Type is not IErrorTypeSymbol)
                        javaType = context.MapType(parameterSymbol.Type);
                    else
                    {
                        var syntaxRef = parameterSymbol.DeclaringSyntaxReferences.FirstOrDefault();
                        if (syntaxRef?.GetSyntax() is ParameterSyntax paramSyntax && paramSyntax.Type != null)
                            javaType = context.MapTypeFromSyntax(paramSyntax.Type);
                    }
                }
                if (string.IsNullOrEmpty(javaType))
                    javaType = "Object";
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
            // Optimization: if the called method's ref parameter is effectively read-only for a struct type,
            // skip ObjectHolder wrapping — Java passes the class instance by reference, so field reads/writes
            // are visible to both caller and callee without a holder object.
            if (parameterSymbol != null && StructCloneHelper.IsRefParamEffectivelyReadOnly(parameterSymbol))
            {
                return transformer.Transform(arg.Expression, context);
            }

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
                    // Use the argument's type if it is not an error type (unresolved reference);
                    // if it is an error type, fall through to the parameterSymbol fallback below.
                    if (typeInfo.Type != null && typeInfo.Type is not IErrorTypeSymbol)
                        javaType = context.MapType(typeInfo.Type);
                }
                // If the argument type is unresolved, fall back to the callee's parameter type.
                // First try the semantic type; if that is also an error, recover from the parameter's
                // declaring syntax (e.g. "double" keyword) via MapTypeFromSyntax.
                if (javaType == "Object" && parameterSymbol != null)
                {
                    if (parameterSymbol.Type is not IErrorTypeSymbol)
                        javaType = context.MapType(parameterSymbol.Type);
                    else
                    {
                        var syntaxRef = parameterSymbol.DeclaringSyntaxReferences.FirstOrDefault();
                        if (syntaxRef?.GetSyntax() is ParameterSyntax paramSyntax && paramSyntax.Type != null)
                            javaType = context.MapTypeFromSyntax(paramSyntax.Type);
                    }
                }
                if (string.IsNullOrEmpty(javaType))
                    javaType = "Object";
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
                    if (typeInfo.Type != null && typeInfo.Type is not IErrorTypeSymbol)
                        javaType = context.MapType(typeInfo.Type);
                }
                // Fallback to parameter type when expression type is unresolved
                if (javaType == "Object" && parameterSymbol != null)
                {
                    if (parameterSymbol.Type is not IErrorTypeSymbol)
                        javaType = context.MapType(parameterSymbol.Type);
                    else
                    {
                        var syntaxRef = parameterSymbol.DeclaringSyntaxReferences.FirstOrDefault();
                        if (syntaxRef?.GetSyntax() is ParameterSyntax paramSyntax && paramSyntax.Type != null)
                            javaType = context.MapTypeFromSyntax(paramSyntax.Type);
                    }
                }
                if (string.IsNullOrEmpty(javaType))
                    javaType = "Object";

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
        // For enums with explicit values, use fromValue() instead of values()[] to avoid AIOOBE.
        if (paramType.TypeKind == TypeKind.Enum
            && argType.TypeKind != TypeKind.Enum
            && argType.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int16
                or SpecialType.System_Int64 or SpecialType.System_Byte or SpecialType.System_SByte
                or SpecialType.System_UInt16 or SpecialType.System_UInt32 or SpecialType.System_UInt64)
        {
            var javaEnumType = context.MapType(paramType);
            if (!string.IsNullOrWhiteSpace(javaEnumType))
            {
                bool isExplicit = paramType is INamedTypeSymbol namedParam
                    && (context.IsExplicitValueEnum(namedParam.Name)
                        || context.IsExplicitValueEnum(namedParam.ToDisplayString()));
                return isExplicit
                    ? $"{javaEnumType}.fromValue((int)({transformedExpr}))"
                    : $"{javaEnumType}.values()[(int)({transformedExpr})]";
            }
        }

        // ── Case 1: Array argument → parameter expects IEnumerable/ICollection/IList ──
        // In Java, arrays don't implement Iterable or Collection, so we must wrap.
        // Wrap array arguments when the parameter expects IEnumerable/ICollection interface.
        if (argType is IArrayTypeSymbol argArrayType && paramType is INamedTypeSymbol paramNamed
            && IsEnumerableOrCollectionInterface(paramNamed))
        {
            return ExpressionTransformerHelpers.BuildArrayToCollectionExpression(transformedExpr, argArrayType, context);
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
                    return $"{transformedExpr}.collect(Collectors.toCollection(ArrayList::new))";
                }
                return $"StreamSupport.stream({transformedExpr}.spliterator(), false).collect(Collectors.toCollection(ArrayList::new))";
            }
        }

        // Generic variance bridge: C# allows IEnumerable<Derived> -> IEnumerable<Base>.
        // Java generics are invariant, so emit an explicit Iterable bridge cast when element
        // conversion is implicit (e.g., Set<IntPair> -> Iterable<IEdge>).
        if (paramType is INamedTypeSymbol pNamed
            && pNamed.IsGenericType
            && pNamed.Name is "IEnumerable" or "ICollection" or "IList" or "IReadOnlyCollection" or "IReadOnlyList"
            && pNamed.ContainingNamespace?.ToDisplayString().StartsWith("System") == true
            && argType is INamedTypeSymbol aNamed
            && aNamed.IsGenericType
            && aNamed.TypeArguments.Length >= 1
            && pNamed.TypeArguments.Length >= 1)
        {
            var argElem = aNamed.TypeArguments[0];
            var paramElem = pNamed.TypeArguments[0];
            bool sameElem = SymbolEqualityComparer.Default.Equals(argElem, paramElem);
            bool implicitElemConv = context.SemanticModel.Compilation
                .ClassifyConversion(argElem, paramElem).IsImplicit;

            if (!sameElem && implicitElemConv)
            {
                var javaParamType = context.MapType(paramType);
                if (!string.IsNullOrWhiteSpace(javaParamType))
                    return $"({javaParamType})(Iterable<?>)({transformedExpr})";
            }
        }

        // Fallback: dictionary keySet()/values() often appears as Set<Derived> where C# expected
        // IEnumerable<Base>. Bridge with explicit Iterable cast to avoid Java invariance failures.
        if (paramType is INamedTypeSymbol pNamedFallback
            && pNamedFallback.IsGenericType
            && pNamedFallback.Name is "IEnumerable" or "ICollection" or "IList" or "IReadOnlyCollection" or "IReadOnlyList"
            && pNamedFallback.ContainingNamespace?.ToDisplayString().StartsWith("System") == true)
        {
            var exprTrim = transformedExpr.Trim();
            bool isCollectionCtorTarget = IsJavaCollectionConstructor(targetParam.ContainingSymbol as IMethodSymbol);
            if (exprTrim.EndsWith(".keySet()", StringComparison.Ordinal)
                || (!isCollectionCtorTarget && exprTrim.EndsWith(".values()", StringComparison.Ordinal)))
            {
                var javaParamType = context.MapType(paramType);
                if (!string.IsNullOrWhiteSpace(javaParamType))
                    return $"({javaParamType})(Iterable<?>)({transformedExpr})";
            }
        }

        return ExpressionTransformerHelpers.AdaptExpressionToTargetType(
            arg.Expression,
            transformedExpr,
            paramType,
            context);
    }

    private static string ApplyStructValueCopyIfNeeded(
        ArgumentSyntax arg,
        string transformedExpr,
        IParameterSymbol? targetParam,
        ConversionContext context)
    {
        if (context.SemanticModel == null || targetParam == null)
            return transformedExpr;

        if (arg.RefKindKeyword.Kind() is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword or SyntaxKind.InKeyword)
            return transformedExpr;

        var paramType = targetParam.Type;
        var argType = context.SemanticModel.GetTypeInfo(arg.Expression).Type;
        if (!RequiresStructClone(argType, paramType))
            return transformedExpr;
        // Skip clone for read-only value parameters (method never modifies the struct)
        if (StructCloneHelper.IsValueParamEffectivelyReadOnly(targetParam, context.ProjectCompilation))
            return transformedExpr;
        if (LooksLikeCloneableTemporary(arg.Expression, context.SemanticModel))
            return transformedExpr;

        return BuildCloneInvocation(arg.Expression, transformedExpr);
    }

    private static bool RequiresStructClone(ITypeSymbol? argType, ITypeSymbol paramType)
        => StructCloneHelper.IsUserDefinedStruct(argType) && StructCloneHelper.IsUserDefinedStruct(paramType);

    private static bool LooksLikeCloneableTemporary(ExpressionSyntax expression, SemanticModel? model)
        => StructCloneHelper.IsCloneableTemporary(expression)
            || expression is ElementAccessExpressionSyntax;  // keep backward compat for args

    private static string BuildCloneInvocation(ExpressionSyntax expressionSyntax, string transformedExpression)
        => StructCloneHelper.BuildCloneExpression(expressionSyntax, transformedExpression);

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
        context.AddImport("java.util.ArrayList");
        if (transformedExpr.Contains(".collect(", StringComparison.Ordinal))
            return transformedExpr;

        return $"{transformedExpr}.collect(Collectors.toCollection(ArrayList::new))";
    }
}
