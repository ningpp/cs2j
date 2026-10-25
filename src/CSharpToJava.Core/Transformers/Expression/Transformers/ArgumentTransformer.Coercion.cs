using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Transformers;

namespace CSharpToJava.Core.Transformers.Expression;

public partial class ArgumentTransformer
{
    // System.Type (mapped to java.lang.Class) needs explicit adapters for these compat interfaces.
    private static readonly (string InterfaceName, string FactoryMethod)[] SystemTypeArgumentWrappers =
    {
        ("System.Reflection.ICustomAttributeProvider", "asCustomAttributeProvider"),
        ("System.Reflection.MemberInfo", "asMemberInfo")
    };

    /// <summary>
    /// Applies the implicit C# Span&lt;T&gt; → ReadOnlySpan&lt;T&gt; conversion to a
    /// directly-transformed argument (the non-ref invocation path bypasses
    /// <see cref="CoerceArgumentType"/>).
    /// </summary>
    public static string CoerceImplicitSpanConversion(
        ExpressionSyntax argument,
        string text,
        IMethodSymbol? method,
        int index,
        ConversionContext context)
    {
        if (method == null || index >= method.Parameters.Length)
            return text;

        var parameter = method.Parameters[index];
        return SpanCoercion.CoerceOrSame(argument, text, parameter.Type, context);
    }

    /// <summary>
    /// Non-generic IDictionary parameter bridge for the direct invocation path
    /// (which bypasses <see cref="CoerceArgumentType"/>): C# Dictionary&lt;K,V&gt;
    /// implements the non-generic IDictionary, but the compat CSharpDictionary
    /// and CSharpIDictionary types are unrelated, so wrap with the adapter.
    /// </summary>
    public static string CoerceNonGenericDictionaryBridge(
        ExpressionSyntax argument,
        string text,
        IMethodSymbol? method,
        int index,
        ConversionContext context)
    {
        if (method == null || index >= method.Parameters.Length)
            return text;

        var parameter = method.Parameters[index];
        if (parameter.Type.ToDisplayString() != "System.Collections.IDictionary")
            return text;

        if (context.GetTypeInfo(argument).Type?.ToDisplayString()
            .StartsWith("System.Collections.Generic.Dictionary", StringComparison.Ordinal) != true)
            return text;

        if (text.StartsWith("CSharpIDictionary.from", StringComparison.Ordinal))
            return text;

        context.AddImport("io.github.ningpp.compat.CSharpIDictionary");
        return $"CSharpIDictionary.fromAny({text})";
    }

    /// <summary>
    /// Direct invocation path (which bypasses <see cref="CoerceArgumentType"/>)
    /// IList-family parameter bridge: C# List&lt;T&gt; implements the non-generic
    /// IList (and List&lt;T&gt; ↔ IList&lt;T&gt; interconvert freely), but the compat
    /// CSharpList / CSharpICollection value shapes implement neither
    /// CSharpIList flavour — wrap with the adapter view (real chain: XmlSchemaSet
    /// passing its reprocessList to GetExternalSchemasList(IList, ...)).
    /// </summary>
    public static string CoerceIListFamilyBridge(
        ExpressionSyntax argument,
        string text,
        IMethodSymbol? method,
        int index,
        ConversionContext context)
    {
        if (method == null || index >= method.Parameters.Length)
            return text;

        // CSharpList/CSharpArrayList 的 addRange 接受裸数组（Object[] 重载），
        // 与 CoerceArrayToCollection 的 AddRange 早退一致：数组实参保持原样，
        // 不包 CSharpIList.from 视图（真实链 SchemaCollectionCompiler
        // memberTypeDefinitions.AddRange(uniondt.BaseMemberTypes)）。
        if (method.Name is "AddRange" or "addRange")
            return text;

        return IListCoercion.CoerceOrSame(argument, text, method.Parameters[index].Type, context);
    }

    /// <summary>
    /// Materializes a Java Stream pipeline produced for an IEnumerable&lt;T&gt;
    /// argument when the target parameter maps to CSharpGenericIterable
    /// (e.g. procedural-LINQ helper `_linqitems` parameters): the stream must
    /// be collected back into an Iterable for the call to compile.
    /// Only fires when the argument's TOP-LEVEL expression is a stream producer —
    /// a call whose outermost operator already yields an Iterable (e.g. a
    /// procedural-LINQ helper) must stay untouched even when its inner arguments
    /// contain `.boxed()`/`.collect()` text.
    /// </summary>
    public static string CoerceStreamToIterableParameter(
        ExpressionSyntax argument,
        string text,
        IMethodSymbol? method,
        int index,
        ConversionContext context)
    {
        if (method == null || index >= method.Parameters.Length)
            return text;

        var parameter = method.Parameters[index];
        if (parameter.Type is not INamedTypeSymbol paramNamed
            || paramNamed.Name != "IEnumerable"
            || paramNamed.TypeKind != TypeKind.Interface
            || paramNamed.ContainingNamespace?.ToDisplayString() != "System.Collections.Generic")
            return text;

        if (!LooksLikeStreamPipeline(text))
            return text;

        if (text.StartsWith("CSharpGenericIterable.from(", StringComparison.Ordinal))
            return text;

        // Already collected (e.g. the Concat/Union residual render appends its
        // own .collect(CSharpList.toCSharpList())): the expression already
        // yields an Iterable-shaped value — collecting again is invalid.
        if (text.Contains(".collect(", StringComparison.Ordinal))
            return text;

        // Stream-producer prefixes: `Arrays.stream(x).map(...)`,
        // `StreamSupport.stream(x, false).map(...)`, `Stream.concat(...)`,
        // `IntStream.range(...).boxed()...`. Anything else (a helper call that
        // merely CONTAINS stream text in its arguments) already yields an
        // Iterable and must not be collected.
        var trimmed = text.TrimStart('(');
        var isStreamProducer = trimmed.StartsWith("Arrays.stream(", StringComparison.Ordinal)
            || trimmed.StartsWith("StreamSupport.stream(", StringComparison.Ordinal)
            || trimmed.StartsWith("Stream.concat(", StringComparison.Ordinal)
            || trimmed.StartsWith("IntStream.", StringComparison.Ordinal)
            || trimmed.StartsWith("LongStream.", StringComparison.Ordinal)
            || trimmed.StartsWith("DoubleStream.", StringComparison.Ordinal);
        if (!isStreamProducer)
            return text;

        context.AddImport("io.github.ningpp.compat.CSharpGenericIterable");
        context.AddImport("io.github.ningpp.compat.CSharpList");
        return $"CSharpGenericIterable.from({text}.collect(CSharpList.toCSharpList()))";
    }

    private static bool IsSpanOfT(ITypeSymbol? type, string name)
        => type is INamedTypeSymbol named
           && named.Name == name
           && named.TypeArguments.Length == 1
           && named.ContainingNamespace?.ToDisplayString() == "System";

    /// <summary>
    /// Direct invocation path (which bypasses <see cref="CoerceArgumentType"/>)
    /// array → System.Array parameter bridge: C# T[] implicitly upcasts to
    /// System.Array, but Java arrays are unrelated to compat CSharpArray — wrap
    /// with the write-through asArrayView (real chain: XmlTextReaderImpl's
    /// IncrementalRead(Array, ...) receiving byte[]/char[]).
    /// </summary>
    public static string CoerceArrayToSystemArrayParameter(
        ExpressionSyntax argument,
        string text,
        IMethodSymbol? method,
        int index,
        ConversionContext context)
    {
        if (method == null || index >= method.Parameters.Length)
            return text;

        var parameter = method.Parameters[index];
        if (parameter.Type.SpecialType != SpecialType.System_Array
            // System.Buffer operates on raw Java arrays; never wrap.
            || method.ContainingType?.ToDisplayString() == "System.Buffer")
        {
            return text;
        }

        if (context.GetTypeInfo(argument).Type is not IArrayTypeSymbol)
            return text;

        context.AddImport("io.github.ningpp.compat.CSharpArray");
        return $"CSharpArray.asArrayView({text})";
    }

    private static string CoerceArgumentType(
        ArgumentSyntax arg,
        string transformedExpr,
        IParameterSymbol targetParam,
        ConversionContext context)
    {
        if (arg.RefKindKeyword.Kind() is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword or SyntaxKind.InKeyword
            || context.SemanticModel == null)
        {
            return transformedExpr;
        }

        // Bare method groups passed where a delegate/functional interface is
        // expected lower to Java method references (Owner::method / this::method).
        // The target delegate may be a constructed Func</> with method type
        // parameters, so its TypeKind stays Delegate even though it is not a
        // concrete named delegate.
        if (TryEmitDelegateMethodReference(arg.Expression, targetParam, context, out var methodReference))
        {
            return methodReference;
        }

        var argType = context.GetTypeInfo(arg.Expression).Type;
        var paramType = targetParam.Type;
        bool isFormatProvider = paramType is INamedTypeSymbol fmt
            && fmt.Name == "IFormatProvider" && InSystemNamespace(fmt);

        if (IsNullEmittingArgument(arg, context))
            transformedExpr = CoerceNullArgument(arg, transformedExpr, targetParam, context, isFormatProvider);

        // C# Dictionary<K,V> implements non-generic IDictionary; the compat types are
        // unrelated, so adapt at the argument boundary.
        if (paramType?.ToDisplayString() == "System.Collections.IDictionary"
            && argType?.ToDisplayString().StartsWith(
                "System.Collections.Generic.Dictionary", StringComparison.Ordinal) == true)
        {
            context.AddImport("io.github.ningpp.compat.CSharpIDictionary");
            return $"CSharpIDictionary.fromAny({transformedExpr})";
        }

        // C# Span<T> → ReadOnlySpan<T> / array|string → Span<T> 的隐式转换：Java
        // compat 类型是普通类，需要显式构造或 asSpan 桥接。
        if (SpanCoercion.TryCoerce(arg.Expression, transformedExpr, paramType, context, out var spanCoercedArg))
        {
            return spanCoercedArg;
        }

        // System.Buffer operates on raw Java arrays; never wrap.
        if (paramType?.SpecialType == SpecialType.System_Array
            && targetParam.ContainingSymbol is IMethodSymbol { ContainingType: { } bufferType }
            && bufferType.ToDisplayString() == "System.Buffer")
        {
            return transformedExpr;
        }

        // C# T[] 隐式上转型 System.Array 参数：Java 数组与 compat CSharpArray
        // 类型不相干，经 asArrayView 包成写透视图（真实链 XmlTextReaderImpl 的
        // IncrementalRead(Array, ...) 收 byte[]/char[]，8 处参数不匹配拒编；
        // System.Buffer 的裸数组例外已在上方先行返回）。
        if (paramType?.SpecialType == SpecialType.System_Array
            && argType is IArrayTypeSymbol)
        {
            context.AddImport("io.github.ningpp.compat.CSharpArray");
            return $"CSharpArray.asArrayView({transformedExpr})";
        }

        foreach (var (interfaceName, factory) in SystemTypeArgumentWrappers)
        {
            if (paramType is INamedTypeSymbol capType && capType.ToDisplayString() == interfaceName
                && argType is INamedTypeSymbol argNamed && IsSystemTypeOrDerived(argNamed))
            {
                context.AddImport("io.github.ningpp.compat.TypeHelper");
                return $"TypeHelper.{factory}({transformedExpr})";
            }
        }

        if (arg.Expression is ThisExpressionSyntax
            && paramType is INamedTypeSymbol { Name: "IComparer", TypeArguments.Length: 1 })
        {
            context.AddImport("java.util.Comparator");
            return $"({context.MapType(paramType)}) this::compare";
        }

        if (arg.Expression is AnonymousFunctionExpressionSyntax
            && paramType.TypeKind == TypeKind.Delegate
            && HasOverloadWithDifferentFunctionalParam(targetParam))
        {
            transformedExpr = CoerceLambdaArgument(transformedExpr, targetParam, context);
        }

        if (argType == null)
            return transformedExpr;

        if (IsIntegralEnumParameter(paramType, argType))
            return CoerceIntegralToEnum(transformedExpr, paramType, context);

        if (argType is INamedTypeSymbol grouping
            && grouping.OriginalDefinition?.ToDisplayString().StartsWith("System.Linq.IGrouping<") == true
            && paramType is INamedTypeSymbol groupingParam
            && IsEnumerableOrCollectionInterface(groupingParam))
        {
            return $"{transformedExpr}.getValue()";
        }

        if (argType is IArrayTypeSymbol && paramType is INamedTypeSymbol arrayParam
            && IsEnumerableOrCollectionInterface(arrayParam))
        {
            return CoerceArrayToCollection(arg, transformedExpr, arrayParam, targetParam, context);
        }

        if (argType is INamedTypeSymbol argNamedType && !IsCollectionType(argType))
        {
            var materialized = TryMaterializeStream(transformedExpr, targetParam, argNamedType, context);
            if (materialized != null)
                return materialized;
        }

        if (paramType is INamedTypeSymbol emptyParam && emptyParam.Name == "IEnumerable" && InSystemNamespace(emptyParam))
        {
            var trimmed = transformedExpr.Trim();
            if (trimmed.StartsWith("Collections.", StringComparison.Ordinal)
                && (trimmed.Contains("emptyList()") || trimmed.EndsWith(".emptyList()")))
            {
                context.AddImport("io.github.ningpp.compat.CSharpGenericIterable");
                return $"CSharpGenericIterable.from({transformedExpr})";
            }
        }

        if (TryCoerceMultiEnumerableDictionary(transformedExpr, paramType, argType, out var dictCoerced))
            return dictCoerced;

        if (ShouldWrapCollectionAsIterable(transformedExpr, paramType, argType, targetParam, context))
        {
            context.AddImport("io.github.ningpp.compat.CSharpGenericIterable");
            return $"CSharpGenericIterable.from({transformedExpr})";
        }

        var bridged = TryBuildIterableBridge(transformedExpr, paramType, argType, targetParam, context);
        if (bridged != null)
            return bridged;

        if (paramType.ToDisplayString() == "System.Collections.IList"
            && argType.ToDisplayString() == "System.Collections.IList"
            && !transformedExpr.StartsWith("CSharpGenericIList.from(", StringComparison.Ordinal))
        {
            context.AddImport("io.github.ningpp.compat.CSharpGenericIList");
            return $"CSharpGenericIList.from({transformedExpr})";
        }

        // C# List<T>/非泛型 ICollection 值绑定到 IList 家族参数（映射 CSharpIList）：
        // CSharpList 与 CSharpICollection 视图都不实现 CSharpIList，包适配视图
        // （真实链 XmlSchemaSet 把 reprocessList 传给 GetExternalSchemasList(IList,…)）。
        if (IListCoercion.TryCoerce(arg.Expression, transformedExpr, paramType, context, out var iListCoercedArg))
            return iListCoercedArg;

        if (paramType is INamedTypeSymbol dictParam && dictParam.ToDisplayString() == "System.Collections.IDictionary"
            && argType is INamedTypeSymbol dictArg && IsGenericDictionary(dictArg)
            && !transformedExpr.StartsWith("CSharpIDictionary.from(", StringComparison.Ordinal))
        {
            context.AddImport("io.github.ningpp.compat.CSharpIDictionary");
            return $"CSharpIDictionary.from({transformedExpr})";
        }

        if (paramType is INamedTypeSymbol { TypeKind: TypeKind.Delegate, TypeArguments.Length: 1 } supplierParam)
        {
            var mappedDelegate = context.MapType(supplierParam);
            if (mappedDelegate.StartsWith("Supplier<", StringComparison.Ordinal))
            {
                transformedExpr = System.Text.RegularExpressions.Regex.Replace(
                    transformedExpr,
                    @"^\(Supplier<[^>]+>\)\s*\(\)\s*->",
                    $"({mappedDelegate}) () ->");
            }
        }

        var refArgType = paramType ?? argType;
        if (transformedExpr.Contains("getReference(", StringComparison.Ordinal))
        {
            if (refArgType.SpecialType == SpecialType.System_Byte
                && TryExtractGetReferenceSpanArg(transformedExpr, out var spanArg))
            {
                context.AddImport("io.github.ningpp.compat.Unsafe");
                return $"Unsafe.createByteSpanRef({spanArg})";
            }
            var holderType = HolderTypeResolver.GetHolderType(context.MapType(refArgType));
            return HolderTypeResolver.GetHolderInstantiationWithValue(holderType, transformedExpr);
        }

        if (TryNarrowIntegralArgument(transformedExpr, paramType, argType, out var narrowed))
            return narrowed;

        return argType.SpecialType == SpecialType.System_Byte
            && paramType.SpecialType == SpecialType.System_SByte
            ? $"(byte)({transformedExpr})"
            : transformedExpr;
    }

    /// <summary>
    /// Numeric narrowing wraps required when binding an argument to a method
    /// parameter (Java invocation context applies no narrowing constant
    /// conversion): int→short/sbyte/char casts, and uint→int for unsigned
    /// literals that are rendered with an L suffix. Returns null when no wrap
    /// is needed.
    /// </summary>
    public static string? NarrowIntegralArgument(string transformedExpr, SpecialType param, SpecialType? arg)
    {
        if (arg is not { } argSpecial)
            return null;
        switch (param)
        {
            case SpecialType.System_Int16 when IsIntWidthForNarrowing(argSpecial):
                return $"(short)({transformedExpr})";
            case SpecialType.System_SByte when IsIntWidthForNarrowing(argSpecial):
                return $"(byte)({transformedExpr})";
            case SpecialType.System_Char when argSpecial != SpecialType.System_Char
                && IsIntWidthForNarrowing(argSpecial):
                return $"(char)({transformedExpr})";
            case SpecialType.System_Int32 when argSpecial == SpecialType.System_UInt32:
                return $"(int)({transformedExpr})";
            // uint parameters lower to Java int; uint literals above int range
            // render with an L suffix and need an int cast at the call site.
            case SpecialType.System_UInt32 when argSpecial == SpecialType.System_UInt32:
                return $"(int)({transformedExpr})";
            default:
                return null;
        }
    }

    private static bool IsIntWidthForNarrowing(SpecialType special)
        => special is SpecialType.System_Int32
            or SpecialType.System_UInt16
            or SpecialType.System_Byte
            or SpecialType.System_SByte
            or SpecialType.System_Char;

    /// <summary>
    /// Java method invocation contexts (unlike assignment contexts) do not apply
    /// narrowing constant conversion, so int arguments bound to short/sbyte/char
    /// parameters need an explicit cast. C# byte/ushort lower to Java int.
    /// </summary>
    private static bool TryNarrowIntegralArgument(
        string transformedExpr,
        ITypeSymbol paramType,
        ITypeSymbol? argType,
        out string narrowed)
    {
        var candidate = NarrowIntegralArgument(transformedExpr, paramType.SpecialType, argType?.SpecialType);
        narrowed = candidate ?? "";
        return candidate != null;
    }

    private static string CoerceNullArgument(
        ArgumentSyntax arg,
        string transformedExpr,
        IParameterSymbol targetParam,
        ConversionContext context,
        bool isFormatProvider)
    {
        var javaTarget = context.MapType(targetParam.Type);
        if (string.IsNullOrWhiteSpace(javaTarget))
            return transformedExpr;

        // C# `ICollection.CopyTo(null, 0)` lowers to compat copyTo overloads
        // (Object[] vs CSharpArray); the declared parameter is System.Array, so
        // cast the null to the exact overload.
        if (targetParam.Type.SpecialType == SpecialType.System_Array)
        {
            context.AddImport("io.github.ningpp.compat.CSharpArray");
            return $"(CSharpArray) {transformedExpr}";
        }

        var receiver = GetReceiverTypeOfInvocation(arg, context);
        if (isFormatProvider
            || HasAmbiguousSiblingOverload(targetParam, receiver, context)
            || IsSystemCollectionsInterfaceReceiver(receiver)
            || IsMultiEnumerableReceiver(receiver))
        {
            var castType = javaTarget;
            if (isFormatProvider)
            {
                castType = "CultureInfo";
                context.AddImport("io.github.ningpp.compat.CultureInfo");
            }
            else if (castType == "Object")
            {
                var elementType = MapReceiverElementType(receiver, context);
                if (!string.IsNullOrWhiteSpace(elementType) && elementType != "Object" && !IsJavaPrimitiveTypeName(elementType))
                    castType = elementType;
            }
            return $"({castType}) {transformedExpr}";
        }

        if (!IsJavaPrimitiveTypeName(javaTarget) && javaTarget.EndsWith("[]", StringComparison.Ordinal))
            return $"({javaTarget}) {transformedExpr}";

        return transformedExpr;
    }

    private static string CoerceLambdaArgument(
        string transformedExpr,
        IParameterSymbol targetParam,
        ConversionContext context)
    {
        var javaFuncType = context.MapType(targetParam.Type);
        if (javaFuncType is "Object" or "var" || string.IsNullOrWhiteSpace(javaFuncType))
            return transformedExpr;

        // Task.Run(Func<Task>) maps to CompletableFuture.runAsync(Runnable).
        if (javaFuncType.StartsWith("Supplier<", StringComparison.Ordinal)
            && targetParam.ContainingSymbol is IMethodSymbol runMethod
            && runMethod.Name == "Run"
            && runMethod.ContainingType?.ToDisplayString() == "System.Threading.Tasks.Task"
            && javaFuncType.Contains("CompletableFuture", StringComparison.Ordinal))
        {
            context.AddImport("java.lang.Runnable");
            return $"(Runnable) {transformedExpr}";
        }

        return $"({javaFuncType}) {transformedExpr}";
    }

    /// <summary>
    /// Lowers a bare method group (identifier or member access) bound to a
    /// delegate/SAM parameter to a Java method reference
    /// (<c>Owner::method</c>/<c>this::method</c>). Shared by the coercion path
    /// and the two direct invocation argument builders, which otherwise bypass
    /// <see cref="CoerceArgumentType"/> and leave e.g. <c>run(Twice)</c>.
    /// </summary>
    public static bool TryEmitDelegateMethodReference(
        ExpressionSyntax expression,
        IParameterSymbol targetParam,
        ConversionContext context,
        out string rendered)
        => TryEmitDelegateMethodReference(expression, targetParam.Type, context, out rendered);

    /// <param name="targetType">期望的目标委托/函数式接口类型（参数类型或赋值左侧类型）。</param>
    public static bool TryEmitDelegateMethodReference(
        ExpressionSyntax expression,
        ITypeSymbol targetType,
        ConversionContext context,
        out string rendered)
    {
        rendered = "";
        if (context.SemanticModel == null
            || expression is not (IdentifierNameSyntax or MemberAccessExpressionSyntax)
            || context.GetSymbolInfo(expression).Symbol is not IMethodSymbol methodGroup
            || methodGroup.IsGenericMethod
            || !IsDelegateLikeTarget(targetType))
        {
            return false;
        }

        rendered = ObjectCreationTransformer.EmitDelegateMethodReference(expression, methodGroup, context);
        return true;
    }

    internal static bool IsDelegateLikeTarget(ITypeSymbol type)
        => type.TypeKind == TypeKind.Delegate || IsSingleMethodInterface(type);

    private static bool IsIntegralEnumParameter(ITypeSymbol paramType, ITypeSymbol argType)
        => paramType.TypeKind == TypeKind.Enum
            && argType.TypeKind != TypeKind.Enum
            && argType.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int16
                or SpecialType.System_Int64 or SpecialType.System_Byte or SpecialType.System_SByte
                or SpecialType.System_UInt16 or SpecialType.System_UInt32 or SpecialType.System_UInt64;

    private static string CoerceIntegralToEnum(string transformedExpr, ITypeSymbol paramType, ConversionContext context)
    {
        var javaEnumType = context.MapType(paramType);
        if (string.IsNullOrWhiteSpace(javaEnumType))
            return transformedExpr;
        if (javaEnumType is "int" or "long" or "short" or "byte" or "Integer" or "Long" or "Short" or "Byte")
            return transformedExpr;

        return $"{javaEnumType}.values()[(int)({transformedExpr})]";
    }

    private static string CoerceArrayToCollection(
        ArgumentSyntax arg,
        string transformedExpr,
        INamedTypeSymbol arrayParam,
        IParameterSymbol targetParam,
        ConversionContext context)
    {
        // CSharpList.addRange accepts raw arrays.
        if (targetParam.ContainingSymbol is IMethodSymbol { Name: "AddRange" })
            return transformedExpr;

        context.AddImport("io.github.ningpp.compat.ArrayHelper");
        var collectionExpr = $"ArrayHelper.toList({transformedExpr})";
        // C# System.Collections.IList 与 System.Collections.Generic.IList`1 都映射
        // CSharpIList：数组实参落 CSharpGenericIList.fromTyped(...) 与声明侧
        // CSharpIList 形参永不兼容（真实链 SortQuery.AddFunction →
        // StringFunctions(IList<Query>)，javac 不存在 T 使
        // CSharpGenericIList<T> 与 CSharpIList 一致）。按映射落对应的
        // CSharpIList.from(ArrayHelper.asListView(arr)) 数组视图。
        if (arrayParam.Name == "IList")
        {
            // metadata 形键（System.Collections.Generic.IList`1 / System.Collections.IList）
            var iListDef = arrayParam.OriginalDefinition;
            var iListNs = iListDef.ContainingNamespace?.ToDisplayString();
            var mapKey = arrayParam.Arity > 0
                ? $"{iListNs}.{iListDef.Name}`{arrayParam.Arity}"
                : $"{iListNs}.{iListDef.Name}";
            if (context.TypeMappings.HasTypeMapping(mapKey)
                && context.TypeMappings.MapType(mapKey) is { } mappedIListParam)
            {
                if (mappedIListParam.StartsWith("CSharpIList", StringComparison.Ordinal))
                {
                    context.AddImport("io.github.ningpp.compat.CSharpIList");
                    return $"CSharpIList.from(ArrayHelper.asListView({transformedExpr}))";
                }
                if (mappedIListParam.StartsWith("CSharpGenericIList", StringComparison.Ordinal)
                    && !collectionExpr.StartsWith("CSharpList<", StringComparison.Ordinal))
                {
                    context.AddImport("io.github.ningpp.compat.CSharpGenericIList");
                    return $"CSharpGenericIList.fromTyped({collectionExpr})";
                }
            }
        }
        return collectionExpr;
    }

    /// <summary>
    /// Materializes an Iterable/Stream argument when the Java target needs Collection.
    /// Returns null when no materialization applies.
    /// </summary>
    private static string? TryMaterializeStream(
        string transformedExpr,
        IParameterSymbol targetParam,
        INamedTypeSymbol argType,
        ConversionContext context)
    {
        bool argIsEnumerable = IsEnumerableOrLinqStreamType(argType);
        bool needsCollection = false;
        bool targetIsIEnumerable = false;

        if (targetParam.Type is INamedTypeSymbol p && InSystemNamespace(p))
        {
            if (IsCollectionFamilyInterface(p))
                needsCollection = true;
            if (argIsEnumerable && p.Name == "IEnumerable")
            {
                bool mappedNeedsCollection = IsJavaMethodRequiringCollection(targetParam.ContainingSymbol as IMethodSymbol, context)
                    || IsJavaCollectionConstructor(targetParam.ContainingSymbol as IMethodSymbol);
                targetIsIEnumerable = transformedExpr.Contains(".stream(", StringComparison.Ordinal);
                if (mappedNeedsCollection || targetIsIEnumerable)
                    needsCollection = true;
            }
        }

        if (!argIsEnumerable || !needsCollection)
            return null;

        context.AddImport("java.util.ArrayList");
        context.AddImport("io.github.ningpp.compat.CSharpList");
        context.AddImport("java.util.stream.StreamSupport");
        context.AddImport("java.util.stream.Collectors");
        var collected = $"StreamSupport.stream({transformedExpr}.spliterator(), false).collect(CSharpList.toCSharpList())";
        if (targetIsIEnumerable)
        {
            context.AddImport("io.github.ningpp.compat.CSharpGenericIterable");
            return $"CSharpGenericIterable.from({collected})";
        }
        return collected;
    }

    /// <summary>
    /// A type implementing both IEnumerable&lt;V&gt; (via dictionary Values) and
    /// IEnumerable&lt;KeyValuePair&gt; needs explicit getValues()/getKeys() adaptation.
    /// </summary>
    private static bool TryCoerceMultiEnumerableDictionary(
        string transformedExpr,
        ITypeSymbol paramType,
        ITypeSymbol? argType,
        out string coerced)
    {
        coerced = transformedExpr;
        if (paramType is not INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } param
            || param.Name != "IEnumerable" || !InSystemNamespace(param)
            || argType is not INamedTypeSymbol arg)
        {
            return false;
        }

        var paramElement = param.TypeArguments[0];
        var dictionary = arg.AllInterfaces.FirstOrDefault(i => i.Name == "IReadOnlyDictionary" && i.TypeArguments.Length == 2);
        if (dictionary == null)
            return false;

        bool enumeratesParamElement = arg.AllInterfaces.Any(i =>
            i.Name == "IEnumerable" && i.IsGenericType && i.TypeArguments.Length == 1
            && SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], paramElement));
        if (!enumeratesParamElement)
            return false;

        if (SymbolEqualityComparer.Default.Equals(dictionary.TypeArguments[1], paramElement))
            coerced = $"{transformedExpr}.getValues()";
        else if (SymbolEqualityComparer.Default.Equals(dictionary.TypeArguments[0], paramElement))
            coerced = $"{transformedExpr}.getKeys()";
        else
            return false;

        return true;
    }

    private static bool ShouldWrapCollectionAsIterable(
        string transformedExpr,
        ITypeSymbol paramType,
        ITypeSymbol argType,
        IParameterSymbol targetParam,
        ConversionContext context)
    {
        return paramType is INamedTypeSymbol p && p.Name == "IEnumerable" && InSystemNamespace(p)
            && argType is INamedTypeSymbol a
            && (IsCollectionType(argType)
                || a.AllInterfaces.Any(i => i.OriginalDefinition?.ToDisplayString() == "System.Collections.Generic.IEnumerable<T>"))
            && !IsGenericDictionary(a)
            && !IsJavaMethodRequiringCollection(targetParam.ContainingSymbol as IMethodSymbol, context)
            && !IsJavaCollectionConstructor(targetParam.ContainingSymbol as IMethodSymbol)
            && !transformedExpr.StartsWith("CSharpGenericIterable.from(", StringComparison.Ordinal);
    }

    /// <summary>Builds the (JavaType)(Iterable&lt;?&gt;) bridge for generic variance and keySet()/values().</summary>
    private static string? TryBuildIterableBridge(
        string transformedExpr,
        ITypeSymbol paramType,
        ITypeSymbol argType,
        IParameterSymbol targetParam,
        ConversionContext context)
    {
        if (paramType is not INamedTypeSymbol param || !param.IsGenericType || !IsEnumerableOrCollectionInterface(param))
            return null;

        bool needsBridge = false;
        if (argType is INamedTypeSymbol arg && arg.IsGenericType
            && arg.TypeArguments.Length >= 1 && param.TypeArguments.Length >= 1)
        {
            needsBridge = !SymbolEqualityComparer.Default.Equals(arg.TypeArguments[0], param.TypeArguments[0])
                && context.ProjectCompilation.ClassifyConversion(arg.TypeArguments[0], param.TypeArguments[0]).IsImplicit;
        }

        if (!needsBridge && !IsJavaCollectionConstructor(targetParam.ContainingSymbol as IMethodSymbol))
        {
            var trimmed = transformedExpr.Trim();
            needsBridge = trimmed.EndsWith(".keySet()", StringComparison.Ordinal)
                || trimmed.EndsWith(".values()", StringComparison.Ordinal);
        }

        if (!needsBridge)
            return null;

        var javaParamType = context.MapType(paramType);
        return string.IsNullOrWhiteSpace(javaParamType)
            ? null
            : $"({javaParamType})(Iterable<?>)({transformedExpr})";
    }

    private static bool HasOverloadWithDifferentFunctionalParam(IParameterSymbol targetParam)
    {
        if (targetParam.ContainingSymbol is not IMethodSymbol containingMethod || containingMethod.ContainingType == null)
            return false;

        var overloads = containingMethod.MethodKind == MethodKind.Constructor
            ? containingMethod.ContainingType.Constructors
            : containingMethod.ContainingType.GetMembers(containingMethod.Name).OfType<IMethodSymbol>();

        return overloads.Any(overload =>
            !SymbolEqualityComparer.Default.Equals(overload, containingMethod)
            && overload.Parameters.Length > targetParam.Ordinal
            && !SymbolEqualityComparer.Default.Equals(overload.Parameters[targetParam.Ordinal].Type, targetParam.Type)
            && (overload.Parameters[targetParam.Ordinal].Type.TypeKind == TypeKind.Delegate
                || IsSingleMethodInterface(overload.Parameters[targetParam.Ordinal].Type)));
    }

    /// <summary>
    /// A bare null is ambiguous in Java when a sibling overload (same other parameters)
    /// accepts either a value type (mapped to a class in Java) or a different reference type.
    /// </summary>
    private static bool HasAmbiguousSiblingOverload(
        IParameterSymbol targetParam,
        INamedTypeSymbol? receiverType,
        ConversionContext context)
    {
        var targetJava = context.MapType(targetParam.Type);
        bool targetIsReference = !string.IsNullOrWhiteSpace(targetJava) && !IsJavaPrimitiveTypeName(targetJava);

        foreach (var otherParam in GetSiblingOverloadParamAtPosition(targetParam, receiverType))
        {
            if (otherParam.Type.IsValueType)
                return true;
            if (targetIsReference)
            {
                var otherJava = context.MapType(otherParam.Type);
                if (!string.IsNullOrWhiteSpace(otherJava)
                    && !IsJavaPrimitiveTypeName(otherJava)
                    && otherJava != targetJava)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static IEnumerable<IParameterSymbol> GetSiblingOverloadParamAtPosition(
        IParameterSymbol targetParam,
        INamedTypeSymbol? receiverType)
    {
        if (targetParam.ContainingSymbol is not IMethodSymbol containingMethod || containingMethod.ContainingType == null)
            yield break;

        int ordinal = targetParam.Ordinal;
        var parameters = containingMethod.Parameters;
        IEnumerable<IMethodSymbol> overloads = containingMethod.MethodKind == MethodKind.Constructor
            ? containingMethod.ContainingType.Constructors
            : containingMethod.ContainingType.GetMembers(containingMethod.Name).OfType<IMethodSymbol>();
        if (receiverType != null && !SymbolEqualityComparer.Default.Equals(receiverType, containingMethod.ContainingType))
            overloads = overloads.Concat(receiverType.GetMembers(containingMethod.Name).OfType<IMethodSymbol>());

        foreach (var overload in overloads)
        {
            if (SymbolEqualityComparer.Default.Equals(overload, containingMethod)
                || overload.Parameters.Length != parameters.Length)
                continue;

            bool otherParametersMatch = true;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (i != ordinal && !SymbolEqualityComparer.Default.Equals(overload.Parameters[i].Type, parameters[i].Type))
                {
                    otherParametersMatch = false;
                    break;
                }
            }
            if (!otherParametersMatch)
                continue;

            var otherParam = overload.Parameters[ordinal];
            if (!SymbolEqualityComparer.Default.Equals(otherParam.Type, targetParam.Type))
                yield return otherParam;
        }
    }

    private static INamedTypeSymbol? GetReceiverTypeOfInvocation(ArgumentSyntax arg, ConversionContext context)
    {
        if (arg.Parent is not ArgumentListSyntax argList
            || argList.Parent is not InvocationExpressionSyntax invocation
            || invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return null;
        }

        var receiver = context.GetTypeInfo(memberAccess.Expression).Type as INamedTypeSymbol;
        if (receiver == null)
            return null;

        // An interface-typed local emitted as `var`: recover the concrete initializer type,
        // which determines the Java overload set.
        if (memberAccess.Expression is IdentifierNameSyntax receiverId
            && context.GetSymbolInfo(receiverId).Symbol is ILocalSymbol receiverLocal)
        {
            if (receiverLocal.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is VariableDeclaratorSyntax { Initializer: { } initializer }
                && context.GetTypeInfo(initializer.Value).Type is INamedTypeSymbol initializedType)
            {
                receiver = initializedType;
            }
        }
        return receiver;
    }

    private static bool IsNullEmittingArgument(ArgumentSyntax arg, ConversionContext context)
    {
        if (arg.Expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression })
            return true;

        if (arg.Expression is DefaultExpressionSyntax defaultExpr)
        {
            var type = context.GetTypeInfo(defaultExpr.Type).Type;
            return type != null
                && (type.IsReferenceType
                    || type.IsValueType && type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T);
        }

        return false;
    }

    private static bool IsMultiEnumerableReceiver(INamedTypeSymbol? receiver)
    {
        if (receiver == null)
            return false;

        return receiver.AllInterfaces
            .Where(i => i.Name == "IEnumerable"
                && i.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic"
                && i.TypeArguments.Length == 1)
            .Select(i => i.TypeArguments[0])
            .Distinct(SymbolEqualityComparer.Default)
            .Count() > 1;
    }

    private static string? MapReceiverElementType(INamedTypeSymbol? receiver, ConversionContext context)
    {
        if (receiver == null)
            return null;

        var dictionary = receiver.AllInterfaces
            .FirstOrDefault(i => i.Name == "IReadOnlyDictionary" && i.TypeArguments.Length == 2);
        if (dictionary != null)
            return context.MapType(dictionary.TypeArguments[1]);

        var element = receiver.AllInterfaces
            .Where(i => i.Name == "IEnumerable"
                && i.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic"
                && i.TypeArguments.Length == 1)
            .Select(i => i.TypeArguments[0])
            .FirstOrDefault();
        return element != null ? context.MapType(element) : null;
    }

    private static bool IsSystemCollectionsInterfaceReceiver(INamedTypeSymbol? receiver)
        => receiver is { TypeKind: TypeKind.Interface }
            && receiver.ContainingNamespace?.ToDisplayString().StartsWith("System.Collections", StringComparison.Ordinal) == true;

    private static bool IsJavaPrimitiveTypeName(string? javaType)
        => javaType is "int" or "long" or "short" or "byte" or "char" or "boolean" or "double" or "float";

    private static bool IsSingleMethodInterface(ITypeSymbol type)
        => type is INamedTypeSymbol { TypeKind: TypeKind.Interface } named
            && named.GetMembers().OfType<IMethodSymbol>()
                .Count(m => m.MethodKind == MethodKind.Ordinary && m.IsAbstract) == 1;

    private static bool IsJavaMethodRequiringCollection(IMethodSymbol? method, ConversionContext context)
    {
        if (method == null)
            return false;
        var mapped = context.TypeMappings.MapMethod(method.ContainingType.ToDisplayString(), method.Name)
            ?? context.TypeMappings.MapMethod(
                $"{method.ContainingType.ContainingNamespace}.{method.ContainingType.Name}", method.Name);
        return mapped is "addAll" or "removeAll" or "containsAll" or "retainAll" or "addRange";
    }

    private static bool InSystemNamespace(INamedTypeSymbol type)
        => type.ContainingNamespace?.ToDisplayString().StartsWith("System", StringComparison.Ordinal) == true;

    private static bool IsEnumerableOrCollectionInterface(INamedTypeSymbol type)
        => InSystemNamespace(type)
            && type.Name is "IEnumerable" or "ICollection" or "IList" or "IReadOnlyCollection" or "IReadOnlyList";

    private static bool IsCollectionFamilyInterface(INamedTypeSymbol type)
        => IsEnumerableOrCollectionInterface(type) && type.Name != "IEnumerable";

    private static bool IsEnumerableOrLinqStreamType(INamedTypeSymbol type)
        => InSystemNamespace(type)
            && type.Name is "IEnumerable" or "IOrderedEnumerable" or "IQueryable" or "IOrderedQueryable"
                or "IGrouping" or "ILookup" or "ParallelQuery";

    private static bool IsCollectionType(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named)
            return false;
        if (named.Name is "ICollection" or "IList" or "List" or "HashSet" or "SortedSet"
            or "IReadOnlyCollection" or "IReadOnlyList" or "Collection")
        {
            return true;
        }
        return named.AllInterfaces.Any(i =>
            i.OriginalDefinition?.ToDisplayString() is "System.Collections.Generic.ICollection<T>" or "System.Collections.ICollection");
    }

    private static bool IsGenericDictionary(INamedTypeSymbol type)
        => (type.Name is "Dictionary" or "SortedDictionary" or "IDictionary" && type.IsGenericType)
            || type.AllInterfaces.Any(i => i.Name == "IDictionary" && i.IsGenericType);

    private static bool IsSystemTypeOrDerived(INamedTypeSymbol type)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            var display = current.ToDisplayString();
            if (display is "System.Type" or "System.Reflection.TypeInfo")
                return true;
        }
        return false;
    }

    private static bool IsJavaCollectionConstructor(IMethodSymbol? method)
        => method is { MethodKind: MethodKind.Constructor }
            && method.ContainingType.Name is "List" or "HashSet" or "SortedSet" or "LinkedList" or "Queue";

    private static bool LooksLikeStreamPipeline(string expr)
        => expr.Contains(".stream(", StringComparison.Ordinal)
            || expr.Contains(".boxed(", StringComparison.Ordinal)
            || expr.Contains(".filter(", StringComparison.Ordinal)
            || expr.Contains(".map(", StringComparison.Ordinal)
            || expr.Contains(".flatMap(", StringComparison.Ordinal)
            || expr.Contains(".sorted(", StringComparison.Ordinal)
            || expr.Contains(".distinct(", StringComparison.Ordinal)
            || expr.Contains(".anyMatch(", StringComparison.Ordinal)
            || expr.Contains(".allMatch(", StringComparison.Ordinal)
            || expr.Contains(".collect(", StringComparison.Ordinal);

    private static string CoerceEnumerableStreamFallback(ArgumentSyntax arg, string transformedExpr, ConversionContext context)
    {
        if (context.SemanticModel == null)
            return transformedExpr;
        if (context.GetTypeInfo(arg.Expression).Type is not INamedTypeSymbol argType || !IsEnumerableOrLinqStreamType(argType))
            return transformedExpr;

        context.AddImport("java.util.stream.Collectors");
        context.AddImport("java.util.ArrayList");
        context.AddImport("io.github.ningpp.compat.CSharpList");
        if (transformedExpr.Contains(".collect(", StringComparison.Ordinal))
            return transformedExpr;

        // Only a Java Stream pipeline needs a collect() terminal. A plain
        // IEnumerable expression already maps to CSharpGenericIterable, and
        // appending .collect to it produces an uncompilable call.
        if (!LooksLikeStreamPipeline(transformedExpr))
            return transformedExpr;

        var collected = $"{transformedExpr}.collect(CSharpList.toCSharpList())";
        if (argType.Name == "IEnumerable")
        {
            context.AddImport("io.github.ningpp.compat.CSharpGenericIterable");
            return $"CSharpGenericIterable.from({collected})";
        }
        return collected;
    }
}
