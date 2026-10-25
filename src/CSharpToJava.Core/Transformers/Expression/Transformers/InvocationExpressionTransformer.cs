using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Expression.Utilities;
using CSharpToJava.Core.Transformers.Type;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles method invocation expressions.
/// </summary>
[TransformerRegistration]
public class InvocationExpressionTransformer : IIRExpressionTransformer
{
    static InvocationExpressionTransformer()
    {
        ExpressionTransformerRegistry.Register(new[]
        {
            SyntaxKind.InvocationExpression
        }, new InvocationExpressionTransformer());
    }

    private static readonly Lazy<InvocationExpressionTransformer> _instance = new(() => new());
    public static InvocationExpressionTransformer Instance => _instance.Value;

    public string Transform(ExpressionSyntax node, ConversionContext context)
    {
        var rendered = node.Kind() switch
        {
            SyntaxKind.InvocationExpression => TransformInvocation((InvocationExpressionSyntax)node, context),
            _ => throw new NotSupportedException($"Invocation expression kind {node.Kind()} not supported.")
        };

        // ArrayBuilder<T>.ToArray() and friends return T[], but Java's erasure
        // makes the runtime component type Object for an unbounded T. Call sites
        // that know the constructed receiver type re-materialize a typed array
        // so assignment/return/argument positions all stay ClassCastException-free.
        return node is InvocationExpressionSyntax invocation
               && TryWrapSpecializedGenericArrayResult(invocation, rendered, context, out var wrapped)
            ? wrapped
            : rendered;
    }

    /// <summary>
    /// 检测 nameof(...) 调用——在 cs2j 的解析器中 nameof 被解析为普通调用
    /// （IdentifierNameSyntax("nameof") + 单参数），而非 Roslyn 原生的 NameOfExpressionSyntax。
    /// 模式：IdentifierNameSyntax("nameof") + 单参数。
    /// </summary>
    private static bool IsNameOfInvocation(InvocationExpressionSyntax invocation, out ExpressionSyntax? argument)
    {
        argument = null;
        if (invocation.Expression is not IdentifierNameSyntax id)
            return false;
        if (!string.Equals(id.Identifier.Text, "nameof", StringComparison.Ordinal))
            return false;
        if (invocation.ArgumentList.Arguments.Count != 1)
            return false;
        argument = invocation.ArgumentList.Arguments[0].Expression;
        return true;
    }

    /// <summary>
    /// Resolves the nameof literal, honoring converter-side renames: a nameof
    /// argument bound to a method symbol must produce the Java method name
    /// (camelCase, compat map, keyword escape) — runtime consumers such as
    /// reflection-based test-data lookups search for the converted name.
    /// </summary>
    private static string ResolveNameOfName(ExpressionSyntax argument, ConversionContext context)
    {
        var name = ExtractNameOfArgument(argument);
        // nameof(X) binds a method group as CandidateSymbols (Symbol == null,
        // CandidateReason == MemberGroup), so consult the candidates too.
        var symbol = context.GetSymbolInfo(argument).Symbol
            ?? context.GetSymbolInfo(argument).CandidateSymbols.FirstOrDefault();
        if (symbol is IMethodSymbol method)
            return Member.MethodTransformer.GetJavaMethodName(name, method, context);
        return name;
    }

    /// <summary>
    /// 从 nameof 参数中提取"名称"字符串。按 C# 规范：
    /// - nameof(variable) → "variable"（剥离 verbatim @ 前缀）
    /// - nameof(Type)、nameof(Type.Member) → 最后一段简单名称
    /// - nameof(Namespace.Type) → 最后一段简单名称
    /// </summary>
    private static string ExtractNameOfArgument(ExpressionSyntax argument)
    {
        return argument switch
        {
            IdentifierNameSyntax id => StripVerbatimPrefix(id.Identifier.Text),
            GenericNameSyntax generic => StripVerbatimPrefix(generic.Identifier.Text),
            MemberAccessExpressionSyntax memberAccess => StripVerbatimPrefix(memberAccess.Name.Identifier.Text),
            QualifiedNameSyntax qualified => ExtractNameOfRightMostName(qualified),
            _ => argument.ToString()
        };
    }

    /// <summary>
    /// 处理 QualifiedNameSyntax（如 Namespace.Type 或 A.B.C）。
    /// 取最右端的 Name 节点。
    /// </summary>
    private static string ExtractNameOfRightMostName(QualifiedNameSyntax qualified)
    {
        if (qualified.Right is GenericNameSyntax rightGeneric)
            return StripVerbatimPrefix(rightGeneric.Identifier.Text);
        return StripVerbatimPrefix(qualified.Right.Identifier.Text);
    }

    private static string StripVerbatimPrefix(string name)
        => name.StartsWith("@") ? name[1..] : name;

    /// <summary>
    /// Escapes a string for use inside Java double-quoted string literals.
    /// nameof 结果一般是普通标识符，但仍需处理 \ " \n 等字符。
    /// </summary>
    private static string JavaEscapeString(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("X4"));
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <inheritdoc />
    public JavaExpression TransformToIR(ExpressionSyntax node, ConversionContext context)
    {
        var result = TransformToIRCore(node, context);
        if (node is InvocationExpressionSyntax invocation
            && TryWrapSpecializedGenericArrayResult(
                invocation, result.ToInlineString(), context, out var wrapped))
        {
            return new JavaRawExpression(wrapped);
        }
        return result;
    }

    private JavaExpression TransformToIRCore(ExpressionSyntax node, ConversionContext context)
    {
        if (node is not InvocationExpressionSyntax invocation)
            return new JavaRawExpression(Transform(node, context));

        // nameof(X) → Java string literal "argumentName"
        if (IsNameOfInvocation(invocation, out var nameofArgument))
        {
            var name = ResolveNameOfName(nameofArgument!, context);
            return new JavaLiteralExpression("\"" + JavaEscapeString(name) + "\"");
        }

        var facade = ExpressionTransformerFacade.Instance;
        var invocationMethod = context.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        // Holder expansion anchors pending statements on the enclosing statement;
        // inside a lambda body there is no such anchor (a dedicated preprocessor
        // owns that lowering), so the byref rewrite stays off there.
        var canExpandByRefHolders = !IsWithinLambda(invocation);
        List<JavaExpression> arguments;
        if (canExpandByRefHolders && ArgumentTransformer.HasByRefArguments(invocation, invocationMethod))
        {
            // ref/out args require holder allocation + read-back (pending
            // statements rendered by the enclosing statement transformer).
            arguments = new List<JavaExpression>
            {
                new JavaRawExpression(ArgumentTransformer.TransformInvocationArguments(invocation, context))
            };
        }
        else
        {
            arguments = invocation.ArgumentList.Arguments
                .Select<ArgumentSyntax, JavaExpression>((a, index) =>
                {
                    var text = facade.TransformToIR(a.Expression, context).ToInlineString();
                    text = ArgumentTransformer.CoerceImplicitSpanConversion(
                        a.Expression, text, invocationMethod, index, context);
                    text = ArgumentTransformer.CoerceNonGenericDictionaryBridge(
                        a.Expression, text, invocationMethod, index, context);
                    text = ArgumentTransformer.CoerceStreamToIterableParameter(
                        a.Expression, text, invocationMethod, index, context);
                    text = ArgumentTransformer.CoerceArrayToSystemArrayParameter(
                        a.Expression, text, invocationMethod, index, context);
                    text = ApplyIntegralNarrowing(text, a.Expression, invocationMethod, index, context);
                    return new JavaRawExpression(text);
                })
                .ToList();
        }

        // Xunit.Assert.Throws<T>(code) / Assert.IsType<T>(obj): the assertion
        // type travels as a generic argument that Java erases. The compat Assert
        // exposes Class-based overloads — prepend the materialized class literal
        // so the runtime type check survives conversion.
        if (TryMaterializeAssertTypeArgument(invocation, context, out var assertClassLiteral))
            arguments.Insert(0, new JavaLiteralExpression(assertClassLiteral));

        // C# string.Substring(startIndex, length) vs Java substring(beginIndex,
        // endIndex): the second argument has different semantics. Rewrite length
        // into startIndex + length so range lowering (s[a..b] → Substring(a, b-a))
        // keeps its C# meaning on the Java side.
        if (arguments.Count == 2 && IsStringSubstringRange(invocationMethod))
        {
            var startText = arguments[0].ToInlineString();
            var lengthText = arguments[1].ToInlineString();
            arguments[1] = new JavaRawExpression($"({startText}) + ({lengthText})");
        }

        // Implicit delegate invocation `d(args)` (no explicit `.Invoke`) —
        // common for delegate-typed parameters inside generic methods, where no
        // earlier C# pass materializes the Invoke member.
        var irArgumentText = string.Join(", ", arguments.Select(a => a.ToInlineString()));
        if (TryRenderImplicitDelegateInvoke(invocation, invocationMethod, irArgumentText, context, out var irDelegateCall))
            return new JavaRawExpression(irDelegateCall);
        if (TryRenderEqualityComparerCall(invocationMethod, irArgumentText, context, out var irComparerCall))
            return new JavaRawExpression(irComparerCall);
        if (TryRenderTypeReflectionCall(invocation, invocationMethod, irArgumentText, context, out var irTypeCall))
            return new JavaRawExpression(irTypeCall);

        switch (invocation.Expression)
        {
            // abc.CallMethod(arg1, arg2) → abc.callMethod(arg1, arg2)
            case MemberAccessExpressionSyntax memberAccess:
            {
                var originalName = memberAccess.Name.Identifier.Text;

                if (TryRenderPrimitiveStaticCall(
                        memberAccess,
                        originalName,
                        context,
                        string.Join(", ", arguments.Select(a => a.ToInlineString())),
                        out var primCall))
                {
                    return new JavaRawExpression(primCall);
                }

                // ((IDisposable)e).Dispose() (emitted by foreach lowering for
                // explicit-interface struct Dispose) maps to a cast to
                // AutoCloseable, whose close() throws Exception. The generated
                // class publicly overrides close() with no checked exception, so
                // the cast is both unnecessary and fatal under javac — unwrap it.
                // ((ICollection<T>)x).Contains(y)-style casts unwrap too: see
                // UnwrapErasedInterfaceCast — the compat collection interfaces
                // erase member parameters to Object, so the interface call would
                // bind to the Object flavor and can recurse into itself.
                var receiver = TransformReceiverToIR(
                    UnwrapErasedInterfaceCast(invocation, memberAccess, context)
                        ?? UnwrapExplicitDisposeCast(memberAccess, context), context);
                var receiverText = receiver.ToInlineString();
                var argumentText = string.Join(", ", arguments.Select(a => a.ToInlineString()));

                // Semantic precision overrides before the arity-based method mapping.
                if (TryRenderStringComparisonEquals(invocation, memberAccess, receiverText, argumentText, context, out var equalsCall))
                {
                    return new JavaRawExpression(equalsCall);
                }

                if (TryRenderStringCharSplit(invocation, memberAccess, receiverText, context, out var splitCall))
                {
                    return new JavaRawExpression(splitCall);
                }

                if (TryRenderStringCreate(invocation, memberAccess, irArgumentText, context, out var createCall))
                {
                    return new JavaRawExpression(createCall);
                }

                if (TryRenderListRemoveAllPredicate(invocation, receiverText, context, out var removeAllPredicateCall))
                {
                    return new JavaRawExpression(removeAllPredicateCall);
                }

                if (TryRenderStringBuilderAppendCharRepeat(invocation, receiverText, context, out var appendCharRepeatCall))
                {
                    return new JavaRawExpression(appendCharRepeatCall);
                }

                if (TryRenderStringBuilderAppendFormat(invocation, receiverText, context, out var appendFormatCall))
                {
                    return new JavaRawExpression(appendFormatCall);
                }

                if (TryRenderTypeGetConstructor(invocation, memberAccess, receiverText, context, out var getConstructorCall))
                {
                    return new JavaRawExpression(getConstructorCall);
                }

                var methodSymbol = context.GetSymbolInfo(invocation).Symbol as IMethodSymbol;

                // 静态接收者（ArrayPool<T>.Shared / SegmentStringBuilder.Create()）
                // 的类型只出现在表达式位置，导入收集器看不到，这里补 import。
                AddReceiverTypeImports(memberAccess.Expression, context);
                if (methodSymbol is { IsStatic: true }
                    && TryMapStaticReceiver(memberAccess.Expression, context, out var mappedStaticReceiverIr))
                {
                    receiverText = mappedStaticReceiverIr;
                    receiver = new JavaRawExpression(mappedStaticReceiverIr);
                }

                // C# Array.Empty<primitive>()：Java 泛型类型变量不能取基本类型
                // （CSharpArray.empty() 会推出 T=int 违反 Object 上界），直接给零长数组。
                if (methodSymbol is { Name: "Empty", TypeArguments.Length: 1, IsStatic: true }
                    && methodSymbol.ContainingType?.ToDisplayString() == "System.Array")
                {
                    var element = context.MapType(methodSymbol.TypeArguments[0]);
                    // C# byte 是无符号：标量存储映射为 int，但数组元素保持 byte[]
                    // （IArrayTypeSymbol 分支同规则）。Array.Empty<byte>() 落
                    // new byte[0]，否则与 byte[] 返回类型冲突（真实链
                    // BinHexDecoder.Decode 1 处 int[] 无法转换为 byte[]）。
                    if (methodSymbol.TypeArguments[0].SpecialType == SpecialType.System_Byte)
                        element = "byte";
                    if (element is "int" or "long" or "short" or "byte" or "char"
                        or "boolean" or "float" or "double")
                    {
                        return new JavaRawExpression($"new {element}[0]");
                    }
                    // CSharpArray.empty() 共享 new Object[0]：当赋值目标是具体数组类型
                    // （如 ReadOnlyMemory<char>[]）时，调用点编译出的 checkcast [LX;
                    // 会在运行期抛 ClassCastException（[LObject; cannot be cast to [LX;）。
                    // 改为 empty(元素类型.class) 生成真正元素类型的零长数组；
                    // 类型参数无法取 .class（Java 擦除），且其 T[] 目标擦除为 Object[]，
                    // no-arg empty() 行为正确，保持不变。
                    if (methodSymbol.TypeArguments[0] is not ITypeParameterSymbol)
                    {
                        context.AddImport("io.github.ningpp.compat.CSharpArray");
                        var emptyClassLiteral = ConversionContext.GetClassLiteral(methodSymbol.TypeArguments[0], context);
                        return new JavaRawExpression($"CSharpArray.empty({emptyClassLiteral})");
                    }
                }

                // Enumerable.ToArray<T>() on a compat iterable: Object[] would be
                // emitted by the raw iterator; materialize a typed array instead.
                if (methodSymbol is { Name: "ToArray", Parameters.Length: 0 }
                    && methodSymbol.ContainingType?.ToDisplayString() == "System.Linq.Enumerable"
                    && TryGetEnumerableElementType(context.GetTypeInfo(memberAccess.Expression).Type, out var toArrayElement))
                {
                    var element = BoxPrimitive(context.MapType(toArrayElement));
                    var receiverMapped = context.MapType(context.GetTypeInfo(memberAccess.Expression).Type);
                    var arrayFactory = receiverMapped.StartsWith("Stream", StringComparison.Ordinal)
                        || receiverMapped.StartsWith("java.util.stream", StringComparison.Ordinal)
                        || receiverText.Contains("StreamSupport.stream", StringComparison.Ordinal)
                        || receiverText.Contains(".filter(", StringComparison.Ordinal)
                        || receiverText.Contains(".map(", StringComparison.Ordinal)
                        ? $"{element}[]::new"
                        : $"new {element}[0]";
                    return new JavaRawExpression(WrapPrimitiveArrayUnbox(
                        $"{receiverText}.toArray({arrayFactory})",
                        context.GetTypeInfo(invocation).Type, context));
                }

                // C# String.StartsWith/EndsWith(value, StringComparison) → StringHelper.
                if (methodSymbol is { Name: "StartsWith" or "EndsWith", Parameters.Length: 2 }
                    && methodSymbol.ContainingType?.SpecialType == SpecialType.System_String
                    && methodSymbol.Parameters[1].Type is INamedTypeSymbol { Name: "StringComparison" })
                {
                    context.AddImport("io.github.ningpp.compat.StringHelper");
                    var helper = methodSymbol.Name == "StartsWith" ? "startsWith" : "endsWith";
                    return new JavaRawExpression(
                        $"StringHelper.{helper}({receiverText}, {argumentText})");
                }

                // C# String.ToLower/ToUpper(CultureInfo) → StringHelper (Locale bridge).
                if (methodSymbol is { Name: "ToLower" or "ToUpper", Parameters.Length: 1 }
                    && methodSymbol.ContainingType?.SpecialType == SpecialType.System_String
                    && methodSymbol.Parameters[0].Type is INamedTypeSymbol { Name: "CultureInfo" })
                {
                    context.AddImport("io.github.ningpp.compat.StringHelper");
                    var helper = methodSymbol.Name == "ToLower" ? "toLowerCase" : "toUpperCase";
                    return new JavaRawExpression(
                        $"StringHelper.{helper}({receiverText}, {argumentText})");
                }

                // C# String.ToLowerInvariant/ToUpperInvariant() → StringHelper (.NET invariant rules).
                // Java String.toLowerCase() applies the default locale plus full Unicode mappings
                // (e.g. U+0130 İ → "i" + combining dot), while .NET invariant uses the simple
                // mapping; the compat TextInfo keeps pattern/text case folding consistent.
                if (methodSymbol is { Name: "ToLowerInvariant" or "ToUpperInvariant", Parameters.Length: 0 }
                    && methodSymbol.ContainingType?.SpecialType == SpecialType.System_String)
                {
                    context.AddImport("io.github.ningpp.compat.StringHelper");
                    var helper = methodSymbol.Name == "ToLowerInvariant" ? "toLowerCaseInvariant" : "toUpperCaseInvariant";
                    return new JavaRawExpression(
                        $"StringHelper.{helper}({receiverText})");
                }

                // C# String.Split(char[]) → StringHelper.split (Java split takes a regex).
                if (methodSymbol is { Name: "Split" }
                    && methodSymbol.ContainingType?.SpecialType == SpecialType.System_String
                    && methodSymbol.Parameters.Length >= 1
                    && methodSymbol.Parameters[0].Type is IArrayTypeSymbol
                        { ElementType.SpecialType: SpecialType.System_Char })
                {
                    context.AddImport("io.github.ningpp.compat.StringHelper");
                    return new JavaRawExpression(
                        $"StringHelper.split({receiverText}, {argumentText})");
                }

                // Extension-method resolution may lower MemberInfo.GetCustomAttribute<T>()
                // to an instance call; Java erases T, so restore the inferred cast
                // before the result is used (…getCustomAttribute().State).
                if (methodSymbol is { Name: "GetCustomAttribute" })
                {
                    var attributeType = methodSymbol.TypeArguments.Length == 1
                        ? methodSymbol.TypeArguments[0]
                        : context.GetTypeInfo(node).Type;
                    var mappedAttribute = attributeType is null ? null : context.MapType(attributeType);
                    if (!string.IsNullOrEmpty(mappedAttribute)
                        && mappedAttribute is not "Object" and not "Attribute" and not "var")
                    {
                        context.AddImport("io.github.ningpp.compat.CustomAttributeExtensions");
                        var attributeReceiver = methodSymbol.IsStatic ? argumentText : receiverText;
                        return new JavaRawExpression(
                            $"CustomAttributeExtensions.getCustomAttribute({mappedAttribute}.class, {attributeReceiver})");
                    }
                }

                // 自写 struct 的 Nullable<T>.GetValueOrDefault()：Java 侧 T? 就是
                // 可空引用（无包装类），语义等价于接收者本身。
                if (methodSymbol is { Name: "GetValueOrDefault", Parameters.Length: 0 }
                    && methodSymbol.ContainingType is INamedTypeSymbol
                        { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
                    && nullable.TypeArguments[0].SpecialType == SpecialType.None)
                {
                    return new JavaRawExpression(receiverText);
                }

                // Java 原生数值没有实例方法：x.CompareTo(y) → Long.compare(x, y)。
                if (methodSymbol is { Name: "CompareTo", Parameters.Length: 1 }
                    && TryGetBoxedPrimitiveName(context.GetTypeInfo(memberAccess.Expression).Type)
                        is { } boxedPrimitive)
                {
                    return new JavaRawExpression(
                        $"{boxedPrimitive}.compare({receiverText}, {argumentText})");
                }

                // DynamicMethod.CreateDelegate<T>() → createDelegate(T.class)；raw 泛型
                // 返回值到 Predicate<…>/Consumer<…> 字段是 unchecked 赋值，Java 允许。
                if (methodSymbol is { Name: "CreateDelegate", TypeArguments.Length: 1 }
                    && methodSymbol.ContainingType?.ToDisplayString()
                        is "System.Reflection.MethodInfo" or "System.Reflection.Emit.DynamicMethod")
                {
                    var delegateJava = context.MapType(methodSymbol.TypeArguments[0]);
                    var rawDelegate = delegateJava.Contains('<')
                        ? delegateJava[..delegateJava.IndexOf('<')]
                        : delegateJava;
                    return new JavaRawExpression(
                        $"{receiverText}.createDelegate({rawDelegate}.class)");
                }

                // Erasure-conflict overloads were renamed at declaration time; bind
                // this call to the renamed sibling instead of the shadowing one.
                if (methodSymbol != null
                    && context.TryGetErasureConflictMethodName(methodSymbol, out var conflictName))
                {
                    var renamedCall = new JavaMethodCallExpression
                    {
                        Target = receiver,
                        MethodName = conflictName,
                    };
                    foreach (var argument in arguments)
                        renamedCall.Arguments.Add(argument);
                    return renamedCall;
                }

                // Primitive instance calls must win over generic method-name mappings:
                // int.ToString(IFormatProvider) maps to "toString", but Java primitives
                // cannot be dereferenced (Integer.toString(value) is required).
                if (TryRenderPrimitiveMemberByType(
                        invocation, memberAccess.Expression, receiverText, argumentText, originalName, context,
                        out var primInstanceCallBeforeMap))
                {
                    return new JavaRawExpression(primInstanceCallBeforeMap);
                }

                // Enumerable extension methods left behind by the procedural
                // LINQ pass (delegate-typed arguments) must still become valid
                // Java: wrap non-stream receivers and use Stream operations.
                if (TryRenderEnumerableInvocation(
                        invocation, memberAccess, receiverText, argumentText, context,
                        out var enumerableCall))
                {
                    return new JavaRawExpression(enumerableCall);
                }

                var (mappedName, isStatic) =
                    TryResolveMappedName(invocation, memberAccess.Name.Identifier.Text, context);

                var call = new JavaMethodCallExpression { Target = receiver };

                if (mappedName != null)
                {
                    ApplyMappedName(call, mappedName, isStatic, receiver);
                    AddCompatHelperImport(mappedName, context);

                    // Static calls written with a fully-qualified C# receiver
                    // (System.Threading.Monitor.enter, System.Threading.Interlocked.decrement, …)
                    // keep their FQN receiver text after the method-name-only mapping
                    // (ApplyMappedName does not touch the target), which is uncompilable
                    // Java. Collapse the receiver to the mapped type's simple name; the
                    // type mapping contributes the required compat import. Simple-name
                    // receivers ("Monitor.enter") already render to the same identifier,
                    // so this is a no-op for them.
                    if (isStatic
                        && !mappedName.Contains('.')
                        && methodSymbol != null
                        && TryGetMappedTypeSimpleName(methodSymbol, context, out var mappedReceiver))
                    {
                        call.Target = mappedReceiver;
                    }

                    foreach (var argument in arguments)
                        call.Arguments.Add(argument);

                    // Console.WriteLine/Write 复合格式重载（IR 路径孪生，见字符串路径
                    // TryBridgeConsoleFormatCall 注释）：arity 失配，包 formatCs。
                    if (IsConsoleFormatMappedCall(invocation, mappedName, context)
                        && arguments.Count >= 2                        && context.GetTypeInfo(invocation.ArgumentList.Arguments[0].Expression).Type?.SpecialType
                            == SpecialType.System_String)
                    {
                        context.AddImport("io.github.ningpp.compat.StringHelper");
                        var joinedArguments = string.Join(", ", arguments.Select(a => a.ToInlineString()));
                        call.Arguments.Clear();
                        call.Arguments.Add(new JavaRawExpression($"StringHelper.formatCs({joinedArguments})"));
                    }

                    WrapSortKeySelectorInComparator(call, memberAccess.Name.Identifier.Text, context);
                    return call;
                }

                // Primitive instance calls: Java primitives cannot be dereferenced.
                if (methodSymbol != null
                    && TryRenderPrimitiveInstanceCall(
                        methodSymbol, memberAccess.Expression, receiverText, originalName, context, out var primInstanceCall))
                {
                    return new JavaRawExpression(primInstanceCall);
                }

                // Static calls on explicitly type-mapped receivers (Cs2jUnsigned.UnsignedMath.*).
                if (isStatic && methodSymbol != null
                    && TryRenderMappedStaticTypeReceiver(methodSymbol, originalName, argumentText, context, out var mappedStaticCall))
                {
                    return new JavaRawExpression(mappedStaticCall);
                }

                AddStaticReceiverTypeImports(memberAccess.Expression, context);
                call.MethodName = WellKnownMethodName(memberAccess.Name.Identifier.Text);

                foreach (var argument in arguments)
                    call.Arguments.Add(argument);

                return call;
            }

            // CallMethod(arg1, arg2) → callMethod(arg1, arg2)
            case IdentifierNameSyntax identifier:
            {
                var (mappedName, _) = TryResolveMappedName(invocation, identifier.Identifier.Text, context);
                mappedName = ConflictRenamedName(invocation, context) ?? mappedName;

                var call = new JavaMethodCallExpression
                {
                    MethodName = mappedName ?? ToCamelCase(identifier.Identifier.Text)
                };
                foreach (var argument in arguments)
                    call.Arguments.Add(argument);
                return call;
            }

            // GenericMethod<T>(arg) → genericMethod(arg): Java erases method
            // type arguments at the call site.
            case GenericNameSyntax genericMethod:
            {
                var (mappedName, _) =
                    TryResolveMappedName(invocation, genericMethod.Identifier.Text, context);
                mappedName = ConflictRenamedName(invocation, context) ?? mappedName;

                var call = new JavaMethodCallExpression
                {
                    MethodName = mappedName ?? ToCamelCase(genericMethod.Identifier.Text)
                };
                foreach (var argument in arguments)
                    call.Arguments.Add(argument);
                return call;
            }

            // Other invocation shapes are not part of the minimal supported set.
            // Render the core text (not Transform) so the outer TransformToIR
            // wrapper cannot specialize the same invocation twice.
            default:
                return new JavaRawExpression(TransformInvocation(invocation, context));
        }
    }

    /// <summary>
    /// Lowers <c>EqualityComparer&lt;T&gt;.Default.Equals(a, b)</c> /
    /// <c>.GetHashCode(x)</c> (emitted by the tuple-struct preprocessor) to
    /// null-safe <c>java.util.Objects</c> helpers.
    /// </summary>
    private static bool TryRenderEqualityComparerCall(
        IMethodSymbol? methodSymbol,
        string argumentText,
        ConversionContext context,
        out string rendered)
    {
        rendered = "";
        if (methodSymbol?.ContainingType?.OriginalDefinition is not { } owner)
            return false;
        if (owner.ToDisplayString() is not "System.Collections.Generic.EqualityComparer<T>")
            return false;

        switch (methodSymbol.Name)
        {
            case "Equals" when methodSymbol.Parameters.Length == 2:
                context.AddImport("java.util.Objects");
                rendered = $"Objects.equals({argumentText})";
                return true;
            case "GetHashCode" when methodSymbol.Parameters.Length == 1:
                context.AddImport("java.util.Objects");
                rendered = $"Objects.hashCode({argumentText})";
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Wraps a transformed argument in an integral narrowing cast when the
    /// target parameter requires it (short/sbyte/char/int-from-uint). The
    /// argument-list coercion path is bypassed by direct invocation
    /// transforms, so narrowing must be applied here.
    /// </summary>
    private static string ApplyIntegralNarrowing(
        string transformed,
        ExpressionSyntax argumentExpression,
        IMethodSymbol? method,
        int index,
        ConversionContext context)
    {
        var parameters = method?.Parameters;
        IParameterSymbol? parameter = null;
        if (parameters is { Length: > 0 })
        {
            parameter = index < parameters.Value.Length
                ? parameters.Value[index]
                : parameters.Value[^1].IsParams ? parameters.Value[^1] : null;
        }
        if (parameter == null)
            return transformed;

        // These direct argument builders bypass CoerceArgumentType, so the
        // method-group → ::method-reference lowering must be repeated here
        // (Run(Twice) → run(Sample::twice)).
        if (ArgumentTransformer.TryEmitDelegateMethodReference(
                argumentExpression, parameter, context, out var methodReference))
        {
            return methodReference;
        }

        var argSpecial = context.GetTypeInfo(argumentExpression).Type?.SpecialType;
        return ArgumentTransformer.NarrowIntegralArgument(
                   transformed, parameter.Type.SpecialType, argSpecial)
               ?? transformed;
    }

    /// <summary>
    /// Maps <see cref="Type"/> instance reflection calls whose Java
    /// <c>java.lang.Class</c> counterpart uses a different method name:
    /// <c>type.IsInstanceOfType(o)</c> → <c>type.isInstance(o)</c>.
    /// </summary>
    private static bool TryRenderTypeReflectionCall(
        InvocationExpressionSyntax invocation,
        IMethodSymbol? methodSymbol,
        string argumentText,
        ConversionContext context,
        out string rendered)
    {
        rendered = "";
        if (methodSymbol is not { Name: "IsInstanceOfType", Parameters.Length: 1 }
            || methodSymbol.ContainingType?.ToDisplayString() != "System.Type"
            || invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return false;
        }

        var receiver = ExpressionTransformerFacade.Instance.Transform(memberAccess.Expression, context);
        rendered = $"{receiver}.isInstance({argumentText})";
        return true;
    }

    /// <summary>
    /// Lowers an implicit delegate invocation (<c>d(args)</c>, whose symbol is
    /// <see cref="MethodKind.DelegateInvoke"/>) to the Java SAM call
    /// (<c>d.apply/accept/run/get(...)</c>). Explicit <c>d.Invoke(...)</c> is
    /// handled by the regular member-access mapping and stays untouched.
    /// </summary>
    private static bool TryRenderImplicitDelegateInvoke(
        InvocationExpressionSyntax invocation,
        IMethodSymbol? methodSymbol,
        string argumentText,
        ConversionContext context,
        out string rendered)
    {
        rendered = string.Empty;
        if (methodSymbol is not { MethodKind: MethodKind.DelegateInvoke })
            return false;
        var callee = invocation.Expression;
        while (callee is ParenthesizedExpressionSyntax parenthesized)
            callee = parenthesized.Expression;
        if (callee is MemberAccessExpressionSyntax
            {
                Name: IdentifierNameSyntax invokedName
            } && invokedName.Identifier.Text == "Invoke")
        {
            return false;
        }
        // 裸名之外的三种委托表达式 callee（语义闸门已确证 DelegateInvoke 绑定）：
        // 成员访问（d.F(args)）、委托数组元素（Table[i](args)，回归 xml-tiny
        // LogicalExpr 的 s_CompXsltE[type1][type2](...)）、以及属性降糖后的 getter
        // 调用（_events.OnUnknownNode(args)，回归 XmlSerializationReader）。括号外壳
        // （(this._CurState._InitFunc)(...)，回归 XdrBuilder）在上方剥除。
        if (callee is not (IdentifierNameSyntax or MemberAccessExpressionSyntax
                or ElementAccessExpressionSyntax or InvocationExpressionSyntax))
            return false;

        var sam = methodSymbol.ContainingType is { } delegateType
            && delegateType.ContainingNamespace?.ToDisplayString() == "System"
            && ((delegateType.Name == "Func" && delegateType.TypeArguments.Length == 2
                 && delegateType.TypeArguments[1].SpecialType == SpecialType.System_Boolean)
                || (delegateType.Name == "Predicate" && delegateType.TypeArguments.Length == 1))
            && methodSymbol.Parameters.Length == 1
                ? "test"
                : DelegateTransformer.InferSamMethodName(
                    methodSymbol.ReturnsVoid, methodSymbol.Parameters.Length);
        var receiver = ExpressionTransformerFacade.Instance.Transform(callee, context);
        rendered = $"{receiver}.{sam}({argumentText})";
        return true;
    }

    private string TransformInvocation(InvocationExpressionSyntax node, ConversionContext context)
    {
        // nameof(X) → "argumentName"
        if (IsNameOfInvocation(node, out var nameofArgument))
        {
            var name = ResolveNameOfName(nameofArgument!, context);
            return "\"" + JavaEscapeString(name) + "\"";
        }

        var facade = ExpressionTransformerFacade.Instance;
        string arguments;
        if (!IsWithinLambda(node)
            && ArgumentTransformer.HasByRefArguments(node,
                context.GetSymbolInfo(node).Symbol as IMethodSymbol))
        {
            arguments = ArgumentTransformer.TransformInvocationArguments(node, context);
        }
        else
        {
            var argList = node.ArgumentList.Arguments
                .Select((a, index) =>
                {
                    var text = facade.Transform(a.Expression, context);
                    var invocationMethod = context.GetSymbolInfo(node).Symbol as IMethodSymbol;
                    text = ArgumentTransformer.CoerceImplicitSpanConversion(
                        a.Expression, text, invocationMethod, index, context);
                    text = ArgumentTransformer.CoerceNonGenericDictionaryBridge(
                        a.Expression, text, invocationMethod, index, context);
                    text = ArgumentTransformer.CoerceStreamToIterableParameter(
                        a.Expression, text, invocationMethod, index, context);
                    text = ArgumentTransformer.CoerceArrayToSystemArrayParameter(
                        a.Expression, text, invocationMethod, index, context);
                    return ApplyIntegralNarrowing(
                        text, a.Expression, invocationMethod, index, context);
                })
                .ToList();
            // See the matching rewrite in TransformToIR: C# Substring(start, length)
            // maps to Java substring(start, start + length).
            if (argList.Count == 2
                && context.GetSymbolInfo(node).Symbol is IMethodSymbol substringMethod
                && IsStringSubstringRange(substringMethod))
            {
                argList[1] = $"({argList[0]}) + ({argList[1]})";
            }
            arguments = string.Join(", ", argList);
        }

        // Xunit.Assert.Throws<T>/IsType<T>: see the matching rewrite in
        // TransformToIR — prepend the erased type argument as a class literal.
        if (TryMaterializeAssertTypeArgument(node, context, out var assertClassLiteral))
            arguments = assertClassLiteral + (arguments.Length == 0 ? "" : ", " + arguments);

        // Implicit delegate invocation `d(args)` (no explicit `.Invoke`).
        if (TryRenderImplicitDelegateInvoke(
                node, context.GetSymbolInfo(node).Symbol as IMethodSymbol, arguments, context, out var delegateCall))
        {
            return delegateCall;
        }
        if (TryRenderEqualityComparerCall(
                context.GetSymbolInfo(node).Symbol as IMethodSymbol, arguments, context, out var comparerCall))
        {
            return comparerCall;
        }
        if (TryRenderTypeReflectionCall(
                node, context.GetSymbolInfo(node).Symbol as IMethodSymbol, arguments, context, out var typeCall))
        {
            return typeCall;
        }

        switch (node.Expression)
        {
            // abc.CallMethod(arg1, arg2) → abc.callMethod(arg1, arg2)
            case MemberAccessExpressionSyntax memberAccess:
            {
                var originalName = memberAccess.Name.Identifier.Text;

                // C# primitive type qualifier — Java forbids using a primitive keyword as a
                // type name, so double.IsNaN(x) must become Double.isNaN(x). The unsigned
                // pass rewrites ulong.TryParse to System.UInt64.TryParse (qualified name),
                // so special types written as qualified names are recognized here too.
                if (TryRenderPrimitiveStaticCall(memberAccess, originalName, context, arguments, out var primCall))
                    return primCall;

                // ((ICollection<T>)x).Contains(y)-style casts unwrap the cast so the
                // call binds to the typed overload on the receiver's class — the
                // compat collection interfaces erase member parameters to Object,
                // so an interface-bound call can recurse into the Object flavor
                // (see UnwrapErasedInterfaceCast).
                var receiver = TransformReceiver(
                    UnwrapErasedInterfaceCast(node, memberAccess, context)
                        ?? UnwrapExplicitDisposeCast(memberAccess, context), context);

                // C# selects IEnumerator<KeyValuePair<K,V>> GetEnumerator when the
                // receiver's static type is IReadOnlyDictionary<K,V>/IDictionary<K,V>/
                // IEnumerable<KeyValuePair<K,V>> — the dictionary ENTRY view. Renaming it
                // to iterator() would call the dual-view collection's VALUE enumerator
                // (e.g. GroupCollection yields Group) and die on the KeyValuePair cast.
                if (TryRenderDictEntryGetEnumerator(node, memberAccess, context, out var dictEntryCall))
                {
                    return dictEntryCall;
                }

                // ((ICollection)x).CopyTo(array, i) binds to ICollection.CopyTo(Array,int),
                // which dispatches virtually to the class's own CopyTo body — e.g.
                // CaptureCollection/GroupCollection write through Array.SetValue and
                // surface IndexOutOfRangeException for bad indexes, while List<T>-backed
                // MatchCollection validates up front with ArgumentOutOfRangeException.
                // The compat copyTo(Object,int) erasure can only pick one behavior, so
                // route the destination through CSharpArray.asArrayView: the CSharpArray
                // static type binds to copyTo(CSharpArray,int), which converted classes
                // declare for their public CopyTo(Array,int) and override virtually.
                if (TryRenderNonGenericCollectionCopyTo(node, memberAccess, receiver, arguments, context, out var collectionCopyToCall))
                {
                    return collectionCopyToCall;
                }

                // Semantic precision overrides run BEFORE the arity-based method mapping:
                // string.Equals(other, StringComparison) shares arity with the static
                // object.Equals pair and would otherwise be swallowed by Objects.equals.
                if (TryRenderStringComparisonEquals(node, memberAccess, receiver, arguments, context, out var equalsCall))
                {
                    return equalsCall;
                }

                // string.Split(',') / Split(',', ';') → StringHelper.split(s, new char[]{...})
                if (TryRenderStringCharSplit(node, memberAccess, receiver, context, out var splitCall))
                {
                    return splitCall;
                }

                // string.Create(int, TState, SpanAction<char,TState>) →
                // StringHelper.createString(int, TState, BiConsumer<Span<Character>,TState>)
                if (TryRenderStringCreate(node, memberAccess, arguments, context, out var createCall))
                {
                    return createCall;
                }

                if (TryRenderListRemoveAllPredicate(node, receiver, context, out var removeAllPredicateCall))
                {
                    return removeAllPredicateCall;
                }

                if (TryRenderStringBuilderAppendCharRepeat(node, receiver, context, out var appendCharRepeatCall))
                {
                    return appendCharRepeatCall;
                }

                if (TryRenderStringBuilderAppendFormat(node, receiver, context, out var appendFormatCall))
                {
                    return appendFormatCall;
                }

                if (TryRenderTypeGetConstructor(node, memberAccess, receiver, context, out var getConstructorCall))
                {
                    return getConstructorCall;
                }

                if (context.GetSymbolInfo(node).Symbol is IMethodSymbol conflictMethod
                    && context.TryGetErasureConflictMethodName(conflictMethod, out var conflictName))
                {
                    return RenderMappedCall(conflictName, conflictMethod.IsStatic, receiver, arguments);
                }

                var memberMethodForOverrides = context.GetSymbolInfo(node).Symbol as IMethodSymbol;
                AddReceiverTypeImports(memberAccess.Expression, context);
                if (memberMethodForOverrides is { IsStatic: true }
                    && TryMapStaticReceiver(memberAccess.Expression, context, out var mappedStaticReceiver))
                {
                    receiver = mappedStaticReceiver;
                }

                if (memberMethodForOverrides is { Name: "Empty", TypeArguments.Length: 1, IsStatic: true }
                    && memberMethodForOverrides.ContainingType?.ToDisplayString() == "System.Array")
                {
                    var emptyElement = context.MapType(memberMethodForOverrides.TypeArguments[0]);
                    // C# byte 数组元素保持 byte[]（IR 路径同规则），否则与
                    // byte[] 目标冲突（真实链 BinHexDecoder Array.Empty<byte>()）。
                    if (memberMethodForOverrides.TypeArguments[0].SpecialType == SpecialType.System_Byte)
                        emptyElement = "byte";
                    if (emptyElement is "int" or "long" or "short" or "byte" or "char"
                        or "boolean" or "float" or "double")
                    {
                        return $"new {emptyElement}[0]";
                    }
                    // 与 IR 路径同理：no-arg CSharpArray.empty() 返回共享 Object[0]，
                    // 赋给具体数组类型会在调用点 checkcast 处抛 ClassCastException。
                    // 类型参数目标 T[] 擦除为 Object[]，no-arg empty() 保持不变。
                    if (memberMethodForOverrides.TypeArguments[0] is not ITypeParameterSymbol)
                    {
                        context.AddImport("io.github.ningpp.compat.CSharpArray");
                        var emptyClassLiteral = ConversionContext.GetClassLiteral(memberMethodForOverrides.TypeArguments[0], context);
                        return $"CSharpArray.empty({emptyClassLiteral})";
                    }
                }

                if (memberMethodForOverrides is { Name: "ToArray", Parameters.Length: 0 }
                    && memberMethodForOverrides.ContainingType?.ToDisplayString() == "System.Linq.Enumerable"
                    && TryGetEnumerableElementType(context.GetTypeInfo(memberAccess.Expression).Type, out var irToArrayElement))
                {
                    var element = BoxPrimitive(context.MapType(irToArrayElement));
                    var receiverMapped = context.MapType(context.GetTypeInfo(memberAccess.Expression).Type);
                    var arrayFactory = receiverMapped.StartsWith("Stream", StringComparison.Ordinal)
                        || receiverMapped.StartsWith("java.util.stream", StringComparison.Ordinal)
                        || receiver.Contains("StreamSupport.stream", StringComparison.Ordinal)
                        || receiver.Contains(".filter(", StringComparison.Ordinal)
                        || receiver.Contains(".map(", StringComparison.Ordinal)
                        ? $"{element}[]::new"
                        : $"new {element}[0]";
                    return WrapPrimitiveArrayUnbox(
                        $"{receiver}.toArray({arrayFactory})",
                        context.GetTypeInfo(node).Type, context);
                }

                if (memberMethodForOverrides is { Name: "StartsWith" or "EndsWith", Parameters.Length: 2 }
                    && memberMethodForOverrides.ContainingType?.SpecialType == SpecialType.System_String
                    && memberMethodForOverrides.Parameters[1].Type is INamedTypeSymbol { Name: "StringComparison" })
                {
                    context.AddImport("io.github.ningpp.compat.StringHelper");
                    var helper = memberMethodForOverrides.Name == "StartsWith" ? "startsWith" : "endsWith";
                    return $"StringHelper.{helper}({receiver}, {arguments})";
                }

                if (memberMethodForOverrides is { Name: "ToLower" or "ToUpper", Parameters.Length: 1 }
                    && memberMethodForOverrides.ContainingType?.SpecialType == SpecialType.System_String
                    && memberMethodForOverrides.Parameters[0].Type is INamedTypeSymbol { Name: "CultureInfo" })
                {
                    context.AddImport("io.github.ningpp.compat.StringHelper");
                    var helper = memberMethodForOverrides.Name == "ToLower" ? "toLowerCase" : "toUpperCase";
                    return $"StringHelper.{helper}({receiver}, {arguments})";
                }

                // C# String.ToLowerInvariant/ToUpperInvariant() → StringHelper (.NET invariant rules);
                // Java's default-locale String.toLowerCase() would expand U+0130 to two chars.
                if (memberMethodForOverrides is { Name: "ToLowerInvariant" or "ToUpperInvariant", Parameters.Length: 0 }
                    && memberMethodForOverrides.ContainingType?.SpecialType == SpecialType.System_String)
                {
                    context.AddImport("io.github.ningpp.compat.StringHelper");
                    var helper = memberMethodForOverrides.Name == "ToLowerInvariant" ? "toLowerCaseInvariant" : "toUpperCaseInvariant";
                    return $"StringHelper.{helper}({receiver})";
                }

                if (memberMethodForOverrides is { Name: "Split" }
                    && memberMethodForOverrides.ContainingType?.SpecialType == SpecialType.System_String
                    && memberMethodForOverrides.Parameters.Length >= 1
                    && memberMethodForOverrides.Parameters[0].Type is IArrayTypeSymbol
                        { ElementType.SpecialType: SpecialType.System_Char })
                {
                    context.AddImport("io.github.ningpp.compat.StringHelper");
                    return $"StringHelper.split({receiver}, {arguments})";
                }

                if (memberMethodForOverrides is { Name: "GetCustomAttribute" })
                {
                    var attributeType = memberMethodForOverrides.TypeArguments.Length == 1
                        ? memberMethodForOverrides.TypeArguments[0]
                        : context.GetTypeInfo(node).Type;
                    var mappedAttribute = attributeType is null ? null : context.MapType(attributeType);
                    if (!string.IsNullOrEmpty(mappedAttribute)
                        && mappedAttribute is not "Object" and not "Attribute" and not "var")
                    {
                        context.AddImport("io.github.ningpp.compat.CustomAttributeExtensions");
                        var attributeReceiver = memberMethodForOverrides.IsStatic ? arguments : receiver;
                        return $"CustomAttributeExtensions.getCustomAttribute({mappedAttribute}.class, {attributeReceiver})";
                    }
                }

                if (memberMethodForOverrides is { Name: "GetValueOrDefault", Parameters.Length: 0 }
                    && memberMethodForOverrides.ContainingType is INamedTypeSymbol
                        { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullableReceiver
                    && nullableReceiver.TypeArguments[0].SpecialType == SpecialType.None)
                {
                    return receiver;
                }

                if (memberMethodForOverrides is { Name: "CompareTo", Parameters.Length: 1 }
                    && TryGetBoxedPrimitiveName(context.GetTypeInfo(memberAccess.Expression).Type)
                        is { } boxedPrimitiveReceiver)
                {
                    return $"{boxedPrimitiveReceiver}.compare({receiver}, {arguments})";
                }

                if (memberMethodForOverrides is { Name: "CreateDelegate", TypeArguments.Length: 1 }
                    && memberMethodForOverrides.ContainingType?.ToDisplayString()
                        is "System.Reflection.MethodInfo" or "System.Reflection.Emit.DynamicMethod")
                {
                    var delegateJava = context.MapType(memberMethodForOverrides.TypeArguments[0]);
                    var rawDelegate = delegateJava.Contains('<')
                        ? delegateJava[..delegateJava.IndexOf('<')]
                        : delegateJava;
                    return $"{receiver}.createDelegate({rawDelegate}.class)";
                }

                if (TryRenderPrimitiveMemberByType(
                        node, memberAccess.Expression, receiver, arguments,
                        originalName, context, out var primitiveCall))
                {
                    return primitiveCall;
                }

                if (TryRenderEnumerableInvocation(
                        node, memberAccess, receiver, arguments, context, out var enumerableCall))
                {
                    return enumerableCall;
                }

                var (mappedName, isStatic) = TryResolveMappedName(node, originalName, context);

                if (mappedName != null)
                {
                    AddCompatHelperImport(mappedName, context);

                    // Same FQN static-receiver collapse as the IR path:
                    // System.Threading.Monitor.enter(…) keeps its qualified C# receiver
                    // after a method-name-only mapping, which is uncompilable Java.
                    if (isStatic
                        && !mappedName.Contains('.')
                        && context.GetSymbolInfo(node).Symbol is IMethodSymbol mappedMethod
                        && TryGetMappedTypeSimpleName(mappedMethod, context, out var mappedReceiverExpr))
                    {
                        receiver = mappedReceiverExpr.ToInlineString();
                    }

                    // OrderBy/ThenBy map to `sorted` but carry a C# KEY-SELECTOR
                    // argument; Java sorted(Comparator) cannot take a 1-arg key
                    // function directly — wrap it with Comparator.comparing.
                    if (mappedName == "sorted")
                        arguments = WrapSortKeySelectorText(originalName, arguments, context);

                    // Console.WriteLine/Write 的复合格式重载 (string format, args…)
                    // 映射 out.println/out.print 后 arity 失配——Java PrintStream
                    // 没有两参 println（mvn 现场：xml-tiny ModuleCore CError.java）。
                    // 按被调包含类型精确判定 Console（Debug/Trace.WriteLine(string,
                    // string) 的 category 不是格式实参，不能按形状桥接），统一包
                    // StringHelper.formatCs（与 StringBuilder AppendFormat 桥接同一助手）。
                    if (TryBridgeConsoleFormatCall(node, mappedName, arguments, context, out var consoleFormatArgs))
                        arguments = consoleFormatArgs;

                    // LINQ Single/SingleOrDefault 映射到流方法 findAny，但具体化集合
                    // 接收器（接口映射常返回 java.util.List/CSharpList）没有 findAny
                    // （mvn 现场：modulecore-tests XmlInlineDataDiscoverer）。集合上取
                    // 首元素 get(0)；流管道形态保持既有 findAny 约定不变。
                    if (mappedName == "findAny" && originalName is "Single" or "SingleOrDefault"
                        && arguments.Length == 0
                        && !LooksLikeStreamPipeline(receiver))
                    {
                        return $"{receiver}.get(0)";
                    }

                    return RenderMappedCall(mappedName, isStatic, receiver, arguments);
                }

                // Primitive instance calls: Java primitives cannot be dereferenced.
                // _value.GetHashCode() (long/int/...) → Long.hashCode(_value).
                if (context.GetSymbolInfo(node).Symbol is IMethodSymbol memberMethod
                    && TryRenderPrimitiveInstanceCall(
                        memberMethod, memberAccess.Expression, receiver, originalName, context, out var primInstanceCall))
                {
                    return primInstanceCall;
                }

                // Static calls whose receiver type has an explicit type mapping but whose
                // method has no method mapping (e.g. Cs2jUnsigned.UnsignedMath.AndUInt64):
                // the type mapping only fires at type-reference positions, so expression
                // positions must honor it here — UnsignedMath.andUInt64(...) + import.
                if (isStatic && context.GetSymbolInfo(node).Symbol is IMethodSymbol staticMethod
                    && TryRenderMappedStaticTypeReceiver(staticMethod, originalName, arguments, context, out var mappedStaticCall))
                {
                    return mappedStaticCall;
                }

                AddStaticReceiverTypeImports(memberAccess.Expression, context);
                var renderedCall = $"{receiver}.{WellKnownMethodName(originalName)}({arguments})";
                // Java erases explicit type arguments, so a generic helper whose
                // return type is its own type parameter (`GetCustomAttribute<T>`)
                // infers Object at the call site; restore the inferred type so
                // member access on the result still binds.
                if (memberAccess.Name is GenericNameSyntax { TypeArgumentList.Arguments.Count: > 0 }
                    && context.GetSymbolInfo(node).Symbol
                        is IMethodSymbol { ReturnType: ITypeParameterSymbol { TypeParameterKind: TypeParameterKind.Method } }
                    && context.GetTypeInfo(node).Type is { } inferredReturn
                    && inferredReturn is not ITypeParameterSymbol
                    && context.MapType(inferredReturn) is { } mappedReturn
                    && mappedReturn != "Object")
                {
                    renderedCall = $"({mappedReturn}) {renderedCall}";
                }
                return renderedCall;
            }

            // CallMethod(arg1, arg2) → callMethod(arg1, arg2)
            case IdentifierNameSyntax identifier:
            {
                var originalName = identifier.Identifier.Text;
                var (mappedName, _) = TryResolveMappedName(node, originalName, context);
                mappedName = ConflictRenamedName(node, context) ?? mappedName;

                // WellKnownMethodName (Dispose→close, GetType→getClass, …) must
                // also apply to receiver-less calls: the declaration side always
                // renames, so a bare `Dispose(true)` camel-cased to `dispose(true)`
                // would no longer bind to the renamed `close(boolean)` member.
                var methodName = mappedName ?? WellKnownMethodName(originalName);
                return $"{methodName}({arguments})";
            }

            // GenericMethod<T>(arg) → genericMethod(arg): Java erases method
            // type arguments at the call site.
            case GenericNameSyntax genericMethod:
            {
                var originalName = genericMethod.Identifier.Text;
                var (mappedName, _) = TryResolveMappedName(node, originalName, context);
                mappedName = ConflictRenamedName(node, context) ?? mappedName;

                var methodName = mappedName ?? WellKnownMethodName(originalName);
                return $"{methodName}({arguments})";
            }

            // Fallback: transform the callee expression as-is and append the arguments.
            default:
                return $"{facade.Transform(node.Expression, context)}({arguments})";
        }
    }

    /// <summary>
    /// For <c>((IDisposable)e).Dispose()</c> returns <c>e</c>; otherwise the
    /// original receiver. See the call-site comment: foreach lowering casts to
    /// reach explicit-interface Dispose, but the Java mapping (AutoCloseable
    /// cast + close()) introduces a checked-exception error on a receiver that
    /// already publicly overrides close().
    /// </summary>
    private static ExpressionSyntax UnwrapExplicitDisposeCast(
        MemberAccessExpressionSyntax memberAccess, ConversionContext context)
    {
        var receiverExpr = memberAccess.Expression;
        while (receiverExpr is ParenthesizedExpressionSyntax parens)
            receiverExpr = parens.Expression;
        if (memberAccess.Name.Identifier.Text == "Dispose"
            && receiverExpr is CastExpressionSyntax cast
            && IsDisposableCastType(cast, context))
        {
            return cast.Expression;
        }
        return memberAccess.Expression;
    }

    private static bool IsDisposableCastType(CastExpressionSyntax cast, ConversionContext context)
    {
        if (context.SemanticModel != null
            && context.GetTypeInfo(cast.Type).Type?.SpecialType == SpecialType.System_IDisposable)
        {
            return true;
        }

        // Post-rewrite trees may not resolve in the active semantic model; fall
        // back to the canonical spellings emitted by foreach lowering.
        var text = cast.Type.ToString();
        return text is "System.IDisposable" or "global::System.IDisposable" or "IDisposable";
    }

    /// <summary>
    /// For <c>((ICollection&lt;T&gt;)this).Contains(item)</c>-style receivers, returns the
    /// un-cast receiver so the call binds to the typed overload on the receiver's own
    /// class; null when the cast must be kept.
    ///
    /// <para>The compat collection interfaces (CSharpIList&lt;T&gt; and friends) erase every
    /// member parameter to Object so one Java method can implement the generic and
    /// non-generic C# flavours at once. A call through the mapped interface therefore
    /// always binds — at compile time and at runtime — to the Object flavor, even when
    /// the C# source selected the generic flavor via the cast. Generated collections
    /// whose Object flavor delegates back through this very cast (the standard C#
    /// explicit-implementation idiom, e.g. CaptureCollection) then recurse into
    /// themselves forever: contains(Object) → interface → contains(Object). A direct
    /// call on the un-cast receiver lets Java overload resolution pick the typed
    /// flavor, reproducing the C# interface dispatch.</para>
    /// </summary>
    private static ExpressionSyntax? UnwrapErasedInterfaceCast(
        InvocationExpressionSyntax invocation, MemberAccessExpressionSyntax memberAccess, ConversionContext context)
    {
        if (context.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method
            || method.IsStatic
            || method.Parameters.Length == 0
            || method.Parameters.All(p => p.Type.SpecialType == SpecialType.System_Object))
        {
            return null;
        }

        var receiverExpr = memberAccess.Expression;
        while (receiverExpr is ParenthesizedExpressionSyntax parens)
            receiverExpr = parens.Expression;
        if (receiverExpr is not CastExpressionSyntax cast
            || context.GetTypeInfo(cast.Type).Type is not INamedTypeSymbol castType
            || castType.TypeKind != TypeKind.Interface
            || !SymbolEqualityComparer.Default.Equals(
                method.ContainingType?.OriginalDefinition, castType.OriginalDefinition))
        {
            return null;
        }

        // Only unwrap when the receiver's own (non-interface) type provides the
        // member — otherwise the cast is load-bearing for compilation and the
        // Object-flavor binding is the best available.
        if (context.GetTypeInfo(cast.Expression).Type is not INamedTypeSymbol receiverType
            || receiverType.TypeKind == TypeKind.Interface)
        {
            return null;
        }

        return ProvidesMember(receiverType, method) ? cast.Expression : null;
    }

    /// <summary>
    /// True when <paramref name="type"/> provides an instance method that implements
    /// <paramref name="method"/>: either an ordinary method with the same name and
    /// substituted parameter types (found on the type or any base class — these render
    /// as inheritable public Java methods), or an explicit interface implementation
    /// (whose C# name is interface-qualified but which renders as a plain Java
    /// overload with the interface member's signature). Explicit implementations only
    /// count when declared on the type itself: they do not inherit in C#, and the
    /// Java rendering of a base class may have dropped the member entirely (e.g.
    /// SerializationInfo/StreamingContext suppression removes Regex's
    /// ISerializable.GetObjectData while a derived class still exists).
    /// </summary>
    private static bool ProvidesMember(INamedTypeSymbol type, IMethodSymbol method)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (member is not IMethodSymbol candidate || candidate.IsStatic)
                    continue;
                if (candidate.MethodKind == MethodKind.ExplicitInterfaceImplementation)
                {
                    if (!SymbolEqualityComparer.Default.Equals(current, type))
                        continue;
                    if (candidate.ExplicitInterfaceImplementations.Any(impl =>
                            SymbolEqualityComparer.Default.Equals(impl, method)))
                    {
                        return true;
                    }
                    continue;
                }
                if (candidate.Name != method.Name
                    || candidate.Parameters.Length != method.Parameters.Length)
                {
                    continue;
                }
                var parametersMatch = true;
                for (var i = 0; i < method.Parameters.Length; i++)
                {
                    if (!SymbolEqualityComparer.Default.Equals(
                            candidate.Parameters[i].Type, method.Parameters[i].Type))
                    {
                        parametersMatch = false;
                        break;
                    }
                }
                if (parametersMatch)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Renders the dictionary ENTRY view of GetEnumerator:
    /// <c>dict.GetEnumerator()</c> whose invoked symbol returns
    /// IEnumerator&lt;KeyValuePair&lt;K,V&gt;&gt; (selected when the receiver's static type
    /// is IReadOnlyDictionary&lt;K,V&gt;, IDictionary&lt;K,V&gt; or
    /// IEnumerable&lt;KeyValuePair&lt;K,V&gt;&gt;) becomes <c>receiver.pairIterator()</c>.
    ///
    /// <para>Renaming it to the well-known <c>iterator()</c> would dispatch to the
    /// receiver's VALUE enumerator: generated collections implement the public
    /// GetEnumerator and the IEnumerable&lt;KeyValuePair&gt; GetEnumerator through a
    /// single Java iterator() (Regex GroupCollection's yields Group/Match), so a
    /// dictionary-typed caller expecting KeyValuePair elements dies with a
    /// ClassCastException at the element cast. Compat CSharpReadOnlyDict
    /// provides pairIterator() — the entry view built from the order-aligned
    /// Keys/Values iterables every IReadOnlyDictionary implementation exposes.</para>
    /// </summary>
    private static bool TryRenderDictEntryGetEnumerator(
        InvocationExpressionSyntax node,
        MemberAccessExpressionSyntax memberAccess,
        ConversionContext context,
        out string rendered)
    {
        rendered = string.Empty;
        if (memberAccess.Name.Identifier.Text != "GetEnumerator"
            || node.ArgumentList.Arguments.Count != 0)
        {
            return false;
        }
        if (context.GetSymbolInfo(node).Symbol is not IMethodSymbol method
            || method.IsStatic
            || method.ReturnType is not INamedTypeSymbol enumeratorType
            || enumeratorType.Name != "IEnumerator"
            || enumeratorType.TypeArguments.Length != 1
            || enumeratorType.ContainingNamespace?.ToDisplayString() != "System.Collections.Generic"
            || enumeratorType.TypeArguments[0].OriginalDefinition is not INamedTypeSymbol pairType
            || pairType.Name != "KeyValuePair"
            || pairType.ContainingNamespace?.ToDisplayString() != "System.Collections.Generic")
        {
            return false;
        }

        // ((IEnumerable<KeyValuePair<K,V>>)x).GetEnumerator() wraps the receiver in a
        // cast that renders as a CSharpGenericIterable.fromAnyTyped adapter, which has
        // no pairIterator member. Unwrap it when the inner type really is a dictionary.
        var receiverExpr = memberAccess.Expression;
        while (receiverExpr is ParenthesizedExpressionSyntax parens)
            receiverExpr = parens.Expression;
        ExpressionSyntax entryReceiver = receiverExpr;
        if (receiverExpr is CastExpressionSyntax cast
            && context.GetTypeInfo(cast.Type).Type is INamedTypeSymbol castType
            && castType.Name == "IEnumerable"
            && castType.TypeArguments.Length == 1
            && castType.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic"
            && IsGenericDictionaryType(context.GetTypeInfo(cast.Expression).Type))
        {
            entryReceiver = cast.Expression;
        }

        if (!IsGenericDictionaryType(context.GetTypeInfo(entryReceiver).Type))
            return false;

        rendered = $"{TransformReceiver(entryReceiver, context)}.pairIterator()";
        return true;
    }

    /// <summary>
    /// True when the invoked member is the NON-generic
    /// <c>System.Collections.ICollection.CopyTo(System.Array, int)</c> (or the same
    /// member inherited by <c>System.Collections.IList</c>). The generic
    /// <c>ICollection&lt;T&gt;.CopyTo(T[], int)</c> overload binds to a different symbol
    /// (array-typed first parameter) and must keep the plain T[] rendering, because
    /// its C# implementations validate up front (ArgumentOutOfRangeException) instead
    /// of delegating to Array.SetValue.
    /// </summary>
    private static bool TryRenderNonGenericCollectionCopyTo(
        InvocationExpressionSyntax node,
        MemberAccessExpressionSyntax memberAccess,
        string receiver,
        string arguments,
        ConversionContext context,
        out string rendered)
    {
        rendered = string.Empty;
        if (memberAccess.Name.Identifier.Text != "CopyTo")
            return false;
        if (context.GetSymbolInfo(node).Symbol is not IMethodSymbol method
            || method.Name != "CopyTo"
            || method.Parameters.Length != 2
            || method.Parameters[0].Type is not INamedTypeSymbol arrayType
            || arrayType.Name != "Array"
            || arrayType.TypeArguments.Length != 0
            || arrayType.ContainingNamespace?.ToDisplayString() != "System")
        {
            return false;
        }
        var owner = method.ContainingType;
        if (owner is null
            || owner.ContainingNamespace?.ToDisplayString() != "System.Collections"
            || owner.Name is not ("ICollection" or "IList"))
        {
            return false;
        }

        var parts = SplitTopLevelArguments(arguments);
        if (parts.Count != 2)
            return false;

        context.AddImport("io.github.ningpp.compat.CSharpArray");
        rendered = $"{receiver}.copyTo(CSharpArray.asArrayView({parts[0]}), {parts[1]})";
        return true;
    }

    /// <summary>
    /// Splits a rendered argument list on top-level commas only (parentheses,
    /// brackets, generics and string literals increase the nesting depth).
    /// </summary>
    private static List<string> SplitTopLevelArguments(string arguments)
    {
        var result = new List<string>();
        var start = 0;
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = 0; i < arguments.Length; i++)
        {
            var ch = arguments[i];
            if (inString)
            {
                escaped = ch == '\\' && !escaped;
                if (ch == '"' && !escaped)
                    inString = false;
                if (ch != '\\')
                    escaped = false;
                continue;
            }

            switch (ch)
            {
                case '"':
                    inString = true;
                    break;
                case '<':
                case '(':
                case '[':
                    depth++;
                    break;
                case '>':
                case ')':
                case ']':
                    depth--;
                    break;
                case ',' when depth == 0:
                    result.Add(arguments[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }

        result.Add(arguments[start..].Trim());
        return result.Where(arg => arg.Length > 0).ToList();
    }

    /// <summary>
    /// True when the type IS or implements the generic System.Collections.Generic
    /// IReadOnlyDictionary&lt;K,V&gt;/IDictionary&lt;K,V&gt; — the interfaces whose GetEnumerator
    /// resolves to the KeyValuePair entry view. These map to CSharpReadOnlyDict /
    /// CSharpGenericIDictionary in Java, which carry the pairIterator member.
    /// </summary>
    private static bool IsGenericDictionaryType(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named)
            return false;
        static bool IsDictInterface(INamedTypeSymbol i)
            => (i.Name == "IReadOnlyDictionary" || i.Name == "IDictionary")
               && i.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic";
        return IsDictInterface(named.OriginalDefinition) || named.AllInterfaces.Any(IsDictInterface);
    }

    /// <summary>
    /// True for <c>string.Substring(int startIndex, int length)</c>, whose second
    /// parameter is a *length* in C# but an *end index* in Java.
    /// </summary>
    private static bool IsStringSubstringRange(IMethodSymbol? method)
        => method is { Name: "Substring", Parameters.Length: 2 }
           && method.ContainingType.SpecialType == SpecialType.System_String
           && method.Parameters[0].Type.SpecialType == SpecialType.System_Int32
           && method.Parameters[1].Type.SpecialType == SpecialType.System_Int32;

    /// <summary>
    /// True when the node is lexically inside a lambda/anonymous-method body,
    /// where ref/out holder expansion cannot anchor its pending statements.
    /// </summary>
    private static bool IsWithinLambda(SyntaxNode node)
        => node.Ancestors().Any(a => a is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax);


    /// <summary>
    /// Specializes the result of an instance call declared as <c>T[]</c> when the
    /// receiver's constructed type supplies a concrete type argument for <c>T</c>.
    /// <para>
    /// Java erases the generic declaration of e.g. <c>ArrayBuilder&lt;T&gt;.toArray()</c>
    /// to <c>Object[]</c> at runtime (<c>new T[n]</c> has no runtime token), so an
    /// assignment to <c>String[]</c> fails with a hidden checkcast. Re-materializing
    /// the array with the known component type fixes every context (assignment,
    /// return, argument) in one place.
    /// </para>
    /// </summary>
    private static bool TryWrapSpecializedGenericArrayResult(
        InvocationExpressionSyntax invocation,
        string rendered,
        ConversionContext context,
        out string wrapped)
    {
        wrapped = string.Empty;
        if (!TryGetSpecializedArrayElement(invocation, context, out var elementType))
            return false;

        var javaElementType = context.MapType(elementType);
        var classLiteral = ConversionContext.GetClassLiteral(elementType, context);
        context.AddImport("io.github.ningpp.compat.CSharpArray");
        wrapped = $"({javaElementType}[]) CSharpArray.toTypedArray({rendered}, {classLiteral})";
        return true;
    }

    /// <summary>
    /// Returns the concrete element type for a call whose declared (unsubstituted)
    /// return type is <c>T[]</c>, <c>T</c> is a type parameter of the receiver's
    /// generic definition, and the constructed receiver type binds <c>T</c> to a
    /// concrete type. Type-parameter bindings are left to the caller unchanged.
    /// </summary>
    private static bool TryGetSpecializedArrayElement(
        InvocationExpressionSyntax invocation,
        ConversionContext context,
        out ITypeSymbol elementType)
    {
        elementType = null!;

        if (context.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method || method.IsStatic)
            return false;

        var original = method.OriginalDefinition;
        if (original.ReturnType is not IArrayTypeSymbol { Rank: 1 } array
            || array.ElementType is not ITypeParameterSymbol typeParameter)
        {
            return false;
        }

        if (method.ContainingType is not INamedTypeSymbol { IsGenericType: true } containingType)
            return false;

        var definition = containingType.OriginalDefinition;
        if (!SymbolEqualityComparer.Default.Equals(typeParameter.ContainingSymbol, definition))
            return false;

        var ordinal = -1;
        for (var i = 0; i < definition.TypeParameters.Length; i++)
        {
            if (SymbolEqualityComparer.Default.Equals(definition.TypeParameters[i], typeParameter))
            {
                ordinal = i;
                break;
            }
        }
        if (ordinal < 0 || ordinal >= containingType.TypeArguments.Length)
            return false;

        var typeArgument = containingType.TypeArguments[ordinal];
        if (!IsConcreteArrayElementType(typeArgument))
            return false;

        elementType = typeArgument;
        return true;
    }

    /// <summary>
    /// True when the type can be named by a Java class literal at the call site
    /// (no type parameter or error type anywhere in its construction).
    /// </summary>
    private static bool IsConcreteArrayElementType(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => false,
        IArrayTypeSymbol array => IsConcreteArrayElementType(array.ElementType),
        INamedTypeSymbol named => named.TypeKind != TypeKind.Error
            && named.TypeArguments.All(IsConcreteArrayElementType),
        _ => type.TypeKind is not (TypeKind.Error or TypeKind.Unknown)
    };

    /// <summary>
    /// Renders an invocation receiver as Java source, erasing type arguments when the
    /// receiver is a static type qualifier written with an explicit generic argument list.
    /// </summary>
    private static string TransformReceiver(ExpressionSyntax expression, ConversionContext context)
    {
        var receiver = ExpressionTransformerFacade.Instance.Transform(expression, context);
        return ContainsGenericTypeQualifier(expression)
            ? StripTypeArgs(receiver)
            : receiver;
    }

    /// <summary>
    /// IR counterpart of <see cref="TransformReceiver"/>.
    /// </summary>
    private static JavaExpression TransformReceiverToIR(ExpressionSyntax expression, ConversionContext context)
    {
        var receiver = ExpressionTransformerFacade.Instance.TransformToIR(expression, context);
        if (!ContainsGenericTypeQualifier(expression))
            return receiver;

        return receiver switch
        {
            JavaRawExpression raw => new JavaRawExpression(
                StripTypeArgs(raw.Code), raw.ResolvedType),
            JavaIdentifierExpression identifier => new JavaIdentifierExpression
            {
                Name = StripTypeArgs(identifier.Name)
            },
            _ => receiver
        };
    }

    /// <summary>
    /// True when the expression contains a generic name segment (<c>Type&lt;T&gt;</c>).
    /// <para>
    /// C# allows an explicit generic type qualifier on a static member access
    /// (<c>ArraySortHelper&lt;T&gt;.IntrospectiveSort(...)</c>), but Java forbids type
    /// arguments on a static qualifier — <c>Type&lt;T&gt;.member()</c> is not valid Java and
    /// javac reports "illegal start of type". Such a receiver must therefore be erased to its
    /// raw type name. In an expression position a generic name can only ever denote a type, so
    /// this only matches static qualifiers and never an instance receiver.
    /// </para>
    /// </summary>
    private static bool ContainsGenericTypeQualifier(ExpressionSyntax expression) => expression switch
    {
        GenericNameSyntax => true,
        QualifiedNameSyntax qualified =>
            ContainsGenericTypeQualifier(qualified.Right) || ContainsGenericTypeQualifier(qualified.Left),
        AliasQualifiedNameSyntax alias => ContainsGenericTypeQualifier(alias.Name),
        MemberAccessExpressionSyntax member =>
            ContainsGenericTypeQualifier(member.Name) || ContainsGenericTypeQualifier(member.Expression),
        _ => false
    };

    /// <summary>
    /// Renders a static call whose receiver is a C# primitive keyword.
    /// <para>
    /// A Java primitive keyword is not a type name, so <c>double.IsNaN(x)</c> is illegal and
    /// must become <c>Double.isNaN(x)</c>. Java's wrapper classes also use different member
    /// names than the C# primitives (<c>IsInfinity</c> → <c>isInfinite</c>, <c>IsLower</c> →
    /// <c>isLowerCase</c>, …), and the C# <c>Parse</c>/<c>TryParse</c> overloads that take a
    /// format provider are routed through the compat <c>MathHelper</c>.
    /// </para>
    /// Returns <c>false</c> when the receiver is not a non-string primitive keyword.
    /// </summary>
    private static bool TryRenderPrimitiveStaticCall(
        MemberAccessExpressionSyntax memberAccess,
        string originalMethodName,
        ConversionContext context,
        string arguments,
        out string rendered)
    {
        rendered = string.Empty;

        // `string` keeps its existing handling: System.String maps to java.lang.String and the
        // TypeMappings table already routes the static helpers (StringHelper.*).
        // `object` is excluded as well: object.Equals/object.ReferenceEquals static calls must
        // resolve through the System.Object method-mapping entries (Objects.equals /
        // ObjectHelper.referenceEquals); the generic primitive path would render non-existent
        // static Object.equals/referenceEquals methods instead.
        var keyword = TryGetPrimitiveKeyword(memberAccess.Expression, context);
        if (keyword is null or "string" or "object")
        {
            return false;
        }
        var argumentCount = memberAccess.Parent is InvocationExpressionSyntax invocation
            ? invocation.ArgumentList.Arguments.Count
            : 0;

        // Numeric TryParse: double.TryParse(s, out result) → MathHelper.tryParseDouble(s, holder)
        if (originalMethodName == "TryParse" && argumentCount >= 2
            && MapPrimitiveTryParseHelper(keyword) is { } tryParseHelper)
        {
            context.AddImport("io.github.ningpp.compat.MathHelper");
            rendered = $"{tryParseHelper}({arguments})";
            return true;
        }

        // Parse → the compat helper, except the single-argument byte/sbyte/short forms which
        // are valid on the Java wrappers themselves (Byte.parseByte(s)).
        if (originalMethodName == "Parse" && MapPrimitiveParseHelper(keyword) is { } parseHelper)
        {
            var useWrapperDirectly = keyword is "byte" or "sbyte" or "short" && argumentCount <= 1;
            if (!useWrapperDirectly)
            {
                if (parseHelper.StartsWith("MathHelper.", StringComparison.Ordinal))
                    context.AddImport("io.github.ningpp.compat.MathHelper");
                else if (parseHelper.StartsWith("Decimal.", StringComparison.Ordinal))
                    context.AddImport("io.github.ningpp.compat.Decimal");
                rendered = $"{parseHelper}({arguments})";
                return true;
            }
        }

        // Double.IsPositiveInfinity(x) → (x == Double.POSITIVE_INFINITY)
        // Java's isInfinite() cannot distinguish the sign, so compare against the constant.
        if (originalMethodName is "IsPositiveInfinity" or "IsNegativeInfinity"
            && keyword is "double" or "float"
            && memberAccess.Parent is InvocationExpressionSyntax posInfInvocation
            && posInfInvocation.ArgumentList.Arguments.Count == 1)
        {
            var javaType = keyword == "float" ? "Float" : "Double";
            var constant = originalMethodName == "IsPositiveInfinity"
                ? "POSITIVE_INFINITY" : "NEGATIVE_INFINITY";
            var arg = ExpressionTransformerFacade.Instance.Transform(
                posInfInvocation.ArgumentList.Arguments[0].Expression, context);
            rendered = $"({arg} == {javaType}.{constant})";
            return true;
        }

        // char.GetUnicodeCategory(ch) → CharUnicodeInfo.getUnicodeCategory(ch)
        // java.lang.Character has no getUnicodeCategory; the compat helper returns the
        // C# System.Globalization.UnicodeCategory values.
        if (keyword == "char" && originalMethodName == "GetUnicodeCategory" && argumentCount >= 1)
        {
            context.AddImport("io.github.ningpp.compat.CharUnicodeInfo");
            rendered = $"CharUnicodeInfo.getUnicodeCategory({arguments})";
            return true;
        }

        // char.IsDigit(string, int) → Character.isDigit(s.charAt(index))
        if (keyword == "char" && originalMethodName == "IsDigit" && argumentCount == 2
            && memberAccess.Parent is InvocationExpressionSyntax isDigitInvocation)
        {
            var strArg = ExpressionTransformerFacade.Instance.Transform(
                isDigitInvocation.ArgumentList.Arguments[0].Expression, context);
            var idxArg = ExpressionTransformerFacade.Instance.Transform(
                isDigitInvocation.ArgumentList.Arguments[1].Expression, context);
            rendered = $"Character.isDigit({strArg}.charAt({idxArg}))";
            return true;
        }

        // byte maps to Java int, but Parse lives on Byte (Byte.parseByte).
        var boxedReceiver = keyword == "byte" && originalMethodName == "Parse"
            ? "Byte"
            : keyword switch
            {
                "bool" => "Boolean",
                "int" => "Integer",
                "long" => "Long",
                "double" => "Double",
                "float" => "Float",
                "decimal" => "Decimal",
                "char" => "Character",
                "short" => "Short",
                "byte" => "Integer",
                "sbyte" => "Byte",
                "uint" => "Integer",
                "ulong" => "Long",
                "ushort" => "Short",
                "string" => "String",
                "object" => "Object",
                _ => keyword
            };
        var mappedMethod = MapPrimitiveStaticMethodName(keyword, originalMethodName);

        // C34 normalization emits decimal.Add/Subtract/Multiply/Divide/Remainder/
        // Negate/Compare static calls; they land on the compat Decimal surface.
        // The import is normally carried by the configured System.Decimal type
        // mapping, but assert it here too so a decimal-call-only file still closes.
        if (keyword == "decimal")
            context.AddImport("io.github.ningpp.compat.Decimal");

        // char.ConvertFromUtf32 returns string in C# but Character.toChars returns char[].
        rendered = keyword == "char" && originalMethodName == "ConvertFromUtf32"
            ? $"new String({boxedReceiver}.{mappedMethod}({arguments}))"
            : $"{boxedReceiver}.{mappedMethod}({arguments})";
        return true;
    }

    /// <summary>
    /// Resolves the primitive keyword of a static type qualifier: a C# predefined
    /// keyword (<c>ulong</c>) or a qualified special type name (<c>System.UInt64</c>,
    /// emitted by the unsigned pass). Returns null for non-primitive type qualifiers.
    /// </summary>
    private static string? TryGetPrimitiveKeyword(ExpressionSyntax expression, ConversionContext context)
    {
        if (expression is PredefinedTypeSyntax predefined)
        {
            return predefined.Keyword.Text;
        }

        if (context.SemanticModel != null
            && context.GetSymbolInfo(expression).Symbol is INamedTypeSymbol namedType)
        {
            return namedType.SpecialType switch
            {
                SpecialType.System_Boolean => "bool",
                SpecialType.System_Byte => "byte",
                SpecialType.System_SByte => "sbyte",
                SpecialType.System_Int16 => "short",
                SpecialType.System_UInt16 => "ushort",
                SpecialType.System_Char => "char",
                SpecialType.System_Int32 => "int",
                SpecialType.System_UInt32 => "uint",
                SpecialType.System_Int64 => "long",
                SpecialType.System_UInt64 => "ulong",
                SpecialType.System_Single => "float",
                SpecialType.System_Double => "double",
                SpecialType.System_Decimal => "decimal",
                _ => null
            };
        }

        return null;
    }

    /// <summary>
    /// Java boxed type name for a primitive receiver, matching the converter's
    /// primitive erasure (C# <c>byte</c> → Java <c>int</c> → Integer, C# <c>sbyte</c>
    /// → Java byte → Byte; <c>ushort</c> → Short). Null for non-primitive types.
    /// </summary>
    private static string? TryGetBoxedPrimitiveName(ITypeSymbol? type) => type?.SpecialType switch
    {
        SpecialType.System_Boolean => "Boolean",
        SpecialType.System_Byte => "Integer",
        SpecialType.System_SByte => "Byte",
        SpecialType.System_Int16 => "Short",
        // C# ushort maps to Java int (not short), so its boxed twin is Integer.
        SpecialType.System_UInt16 => "Integer",
        SpecialType.System_Char => "Character",
        SpecialType.System_Int32 => "Integer",
        SpecialType.System_UInt32 => "Integer",
        SpecialType.System_Int64 => "Long",
        SpecialType.System_UInt64 => "Long",
        SpecialType.System_Single => "Float",
        SpecialType.System_Double => "Double",
        _ => null
    };

    /// <summary>
    /// <c>StringBuilder.AppendFormat(...)</c> (both the IFormatProvider-first and the
    /// format-first overloads) → <c>builder.append(StringHelper.formatCs(format, args...))</c>.
    /// java.lang.StringBuilder has no appendFormat overload; the compat formatter mirrors
    /// C# composite format.
    /// </summary>
    private static bool TryRenderStringBuilderAppendFormat(
        InvocationExpressionSyntax invocation,
        string receiver,
        ConversionContext context,
        out string rendered)
    {
        rendered = string.Empty;
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess
            || memberAccess.Name.Identifier.Text != "AppendFormat")
        {
            return false;
        }

        var method = context.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        if (method != null
            && method.ContainingType.ToDisplayString() != "System.Text.StringBuilder")
        {
            return false;
        }

        var args = invocation.ArgumentList.Arguments;
        if (args.Count == 0)
            return false;

        // The semantic signature may be unavailable in post-preprocessed trees;
        // CultureInfo is the IFormatProvider overload's usual first argument.
        var firstArgType = context.GetTypeInfo(args[0].Expression).Type?.ToDisplayString();
        var firstArgText = args[0].Expression.ToString();
        var providerFirst = method?.Parameters.Length >= 2
            // Nullable analysis appends '?' to the display string.
            ? method.Parameters[0].Type.ToDisplayString().StartsWith(
                "System.IFormatProvider", StringComparison.Ordinal)
            : firstArgType is "System.IFormatProvider" or "System.Globalization.CultureInfo"
                || firstArgText is "CultureInfo.InvariantCulture" or "CultureInfo.CurrentCulture";

        // The format-first overloads — AppendFormat(string), AppendFormat(string, object),
        // AppendFormat(string, object, object), AppendFormat(string, object, object, object)
        // and AppendFormat(string, params object[]) — lower the same way; only the format
        // argument's index shifts. Without a bound signature, only claim the call when the
        // first argument lexically looks like the format string.
        var formatIndex = providerFirst ? 1 : 0;
        if (args.Count <= formatIndex)
            return false;
        if (!providerFirst && method == null
            && firstArgType != "System.String"
            && args[0].Expression is not LiteralExpressionSyntax)
        {
            return false;
        }

        var f = ExpressionTransformerFacade.Instance;
        var format = f.Transform(args[formatIndex].Expression, context);
        var values = args.Skip(formatIndex + 1).Select(a => f.Transform(a.Expression, context)).ToList();
        var formatted = values.Count == 0
            ? $"StringHelper.formatCs({format})"
            : $"StringHelper.formatCs({format}, {string.Join(", ", values)})";

        context.AddImport("io.github.ningpp.compat.StringHelper");
        rendered = $"{receiver}.append({formatted})";
        return true;
    }

    /// <summary>
    /// <c>StringBuilder.Append(char, int)</c> (append a char repeatCount times) →
    /// <c>StringHelper.append(builder, c, repeatCount)</c>. java.lang.StringBuilder has
    /// no append(char, int) overload; the compat helper returns the builder, so chained
    /// calls keep working. The bridge matches the overload signature, not literals.
    /// </summary>
    private static bool TryRenderStringBuilderAppendCharRepeat(
        InvocationExpressionSyntax invocation,
        string receiver,
        ConversionContext context,
        out string rendered)
    {
        rendered = string.Empty;
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess
            || memberAccess.Name.Identifier.Text != "Append")
        {
            return false;
        }

        var args = invocation.ArgumentList.Arguments;
        if (args.Count != 2)
            return false;

        // Overload-shape gate: exactly (char, int). The semantic signature is
        // authoritative when available; post-preprocessed trees may lack it, so
        // fall back to per-argument type info.
        var method = context.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        if (method != null)
        {
            if (method.ContainingType.ToDisplayString() != "System.Text.StringBuilder"
                || method.Parameters.Length != 2
                || method.Parameters[0].Type.SpecialType != SpecialType.System_Char
                || method.Parameters[1].Type.SpecialType != SpecialType.System_Int32)
            {
                return false;
            }
        }
        else if (context.GetTypeInfo(args[0].Expression).Type?.SpecialType != SpecialType.System_Char
            || context.GetTypeInfo(args[1].Expression).Type?.SpecialType != SpecialType.System_Int32)
        {
            return false;
        }

        var f = ExpressionTransformerFacade.Instance;
        var charArg = f.Transform(args[0].Expression, context);
        var countArg = f.Transform(args[1].Expression, context);

        context.AddImport("io.github.ningpp.compat.StringHelper");
        rendered = $"StringHelper.append({receiver}, {charArg}, {countArg})";
        return true;
    }

    /// <summary>
    /// <c>List&lt;T&gt;.RemoveAll(Predicate&lt;T&gt;)</c> →
    /// <c>receiver.removeAllMatching(predicate)</c>. The camelCase fallback emits
    /// <c>removeAll(...)</c>, which Java binds to <c>Collection.removeAll(Collection)</c>
    /// (element-wise mass removal) — uncompilable for a lambda/method-reference argument.
    /// The compat helper removes every element matching the predicate, matching C#.
    /// The bridge matches the overload shape — receiver in the C# List family plus a
    /// single delegate argument — so lambdas, method references and Predicate-typed
    /// variables all qualify.
    /// </summary>
    private static bool TryRenderListRemoveAllPredicate(
        InvocationExpressionSyntax invocation,
        string receiver,
        ConversionContext context,
        out string rendered)
    {
        rendered = string.Empty;
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess
            || memberAccess.Name.Identifier.Text != "RemoveAll")
        {
            return false;
        }

        var args = invocation.ArgumentList.Arguments;
        if (args.Count != 1)
            return false;

        // Overload-shape gate: C# List<T>.RemoveAll only exists as the
        // (Predicate<T>) overload, so receiver-family + single delegate argument is
        // already unambiguous. The semantic signature is authoritative when
        // available; post-preprocessed trees may lack it, so fall back to the
        // receiver's type plus the argument's delegate type.
        var method = context.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        if (method != null)
        {
            if (!IsCSharpGenericList(method.ContainingType)
                || method.Parameters.Length != 1
                || !IsPredicateDelegateType(method.Parameters[0].Type))
            {
                return false;
            }
        }
        else if (!IsCSharpGenericList(context.GetTypeInfo(memberAccess.Expression).Type)
            || !IsPredicateDelegateType(context.GetTypeInfo(args[0].Expression).Type))
        {
            return false;
        }

        var predicate = ExpressionTransformerFacade.Instance.Transform(args[0].Expression, context);
        rendered = $"{receiver}.removeAllMatching({predicate})";
        return true;
    }

    /// <summary>
    /// True for <c>System.Collections.Generic.List&lt;T&gt;</c> (and subtypes), the only
    /// C# type carrying the <c>RemoveAll(Predicate&lt;T&gt;)</c> instance method. It maps
    /// to the compat <c>CSharpList</c>, which owns <c>removeAllMatching</c>.
    /// </summary>
    private static bool IsCSharpGenericList(ITypeSymbol? type)
        => type is INamedTypeSymbol named
            && named.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.List<T>";

    /// <summary>
    /// True for <c>Predicate&lt;T&gt;</c> / <c>Func&lt;T, bool&gt;</c> delegates (the shapes a
    /// lambda, method group or delegate variable can bind to a RemoveAll argument).
    /// </summary>
    private static bool IsPredicateDelegateType(ITypeSymbol? type)
        => type is INamedTypeSymbol named
            && named.OriginalDefinition.ContainingNamespace.ToDisplayString() == "System"
            && named.OriginalDefinition.MetadataName is "Predicate`1" or "Func`2";

    /// <summary>
    /// <c>Type.GetConstructor(BindingFlags, Binder, Type[], ParameterModifier[])</c> has
    /// no java.lang.Class counterpart of that arity: Java reflection lookup is
    /// <c>(Class, int, Class[])</c>. The managed Binder argument is always null in the
    /// converted code base and <c>ParameterModifier[]</c> is a .NET COM-interop relic,
    /// so the bridge drops both and routes the (bindingAttr, types) pair through the
    /// compat helper that implements the C# bindingFlags lookup. The 1-arg
    /// <c>GetConstructor(Type[])</c> keeps its public-instance method mapping.
    /// </summary>
    private static bool TryRenderTypeGetConstructor(
        InvocationExpressionSyntax invocation,
        MemberAccessExpressionSyntax memberAccess,
        string receiver,
        ConversionContext context,
        out string rendered)
    {
        rendered = string.Empty;
        if (memberAccess.Name.Identifier.Text != "GetConstructor")
        {
            return false;
        }

        var args = invocation.ArgumentList.Arguments;
        if (args.Count != 4)
            return false;

        // Overload-shape gate: exactly (BindingFlags, Binder, Type[], ParameterModifier[]).
        // The semantic signature is authoritative when available; post-preprocessed
        // trees may lack it, so fall back to receiver type + null binder/modifiers.
        // Display comparisons strip the trailing nullable annotation: the parameter
        // symbols may be NRT-annotated ("System.Reflection.Binder?") depending on
        // the compilation's flow analysis.
        var method = context.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        if (method != null)
        {
            if (method.ContainingType?.ToDisplayString() != "System.Type"
                || method.Parameters.Length != 4
                || DisplayWithoutNullability(method.Parameters[0].Type) != "System.Reflection.BindingFlags"
                || DisplayWithoutNullability(method.Parameters[1].Type) != "System.Reflection.Binder"
                || method.Parameters[2].Type is not IArrayTypeSymbol { ElementType: INamedTypeSymbol typesElement }
                || DisplayWithoutNullability(typesElement) != "System.Type"
                || method.Parameters[3].Type is not IArrayTypeSymbol { ElementType: INamedTypeSymbol modifiersElement }
                || DisplayWithoutNullability(modifiersElement) != "System.Reflection.ParameterModifier")
            {
                return false;
            }
        }
        else
        {
            if (context.GetTypeInfo(memberAccess.Expression).Type?.ToDisplayString() != "System.Type"
                || !IsNullArgument(args[1].Expression)
                || !IsNullArgument(args[3].Expression))
            {
                return false;
            }
        }

        var f = ExpressionTransformerFacade.Instance;
        var flagsArg = f.Transform(args[0].Expression, context);
        var typesArg = f.Transform(args[2].Expression, context);

        context.AddImport("io.github.ningpp.compat.TypeHelper");
        rendered = $"TypeHelper.getConstructorInfo({receiver}, {flagsArg}, {typesArg})";
        return true;
    }

    private static bool IsNullArgument(ExpressionSyntax expression)
        => expression.IsKind(SyntaxKind.NullLiteralExpression)
            || (expression is CastExpressionSyntax { Expression: var inner } && IsNullArgument(inner));

    /// <summary>
    /// Type display text with the trailing nullable annotation removed, so gates
    /// match regardless of the compilation's nullable flow analysis ("T?" vs "T").
    /// </summary>
    private static string DisplayWithoutNullability(ITypeSymbol type)
    {
        var text = type.ToDisplayString();
        return text.EndsWith("?", StringComparison.Ordinal) ? text[..^1] : text;
    }

    /// <summary>
    /// Renders residual System.Linq.Enumerable extension calls that the
    /// procedural LINQ pass could not lower (delegate-typed arguments, method
    /// groups). Java collections expose no filter/map/toList, so the receiver
    /// is wrapped in a Stream first. Also covers Enumerable.Range and
    /// Tuple.Create, whose static receivers have no method-mapping entry.
    /// </summary>
    private static bool TryRenderEnumerableInvocation(
        InvocationExpressionSyntax invocation,
        MemberAccessExpressionSyntax memberAccess,
        string receiver,
        string argumentText,
        ConversionContext context,
        out string rendered)
    {
        rendered = string.Empty;

        var name = memberAccess.Name.Identifier.Text;
        var argCount = invocation.ArgumentList.Arguments.Count;
        var symbol = context.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        var owner = symbol?.ContainingType?.ToDisplayString();

        // Tuple.Create(a, b) → Map.entry(a, b): System.Tuple`2 maps to
        // java.util.Map.Entry, which has a static entry() factory.
        if (name == "Create" && argCount == 2
            && (symbol?.ContainingType?.Name == "Tuple"
                || memberAccess.Expression is IdentifierNameSyntax { Identifier.Text: "Tuple" }))
        {
            context.AddImport("java.util.Map");
            rendered = $"Map.entry({argumentText})";
            return true;
        }

        // Enumerable.Range(start, count) → an Iterable<Integer> (the argument
        // position is the common residual; chained uses wrap it afterwards).
        if (name == "Range" && argCount == 2
            && (owner == "System.Linq.Enumerable"
                || memberAccess.Expression is IdentifierNameSyntax { Identifier.Text: "Enumerable" }))
        {
            var args = invocation.ArgumentList.Arguments;
            var start = ExpressionTransformerFacade.Instance.Transform(args[0].Expression, context);
            var count = ExpressionTransformerFacade.Instance.Transform(args[1].Expression, context);
            context.AddImport("java.util.stream.IntStream");
            context.AddImport("io.github.ningpp.compat.CSharpList");
            context.AddImport("io.github.ningpp.compat.CSharpGenericIterable");
            rendered = "CSharpGenericIterable.from(IntStream.range("
                + $"{start}, ({start}) + ({count}))"
                + ".boxed().collect(CSharpList.toCSharpList()))";
            return true;
        }

        // Stream.iterator() yields java.util.Iterator, but the C# caller binds
        // the result to a CSharpGenericEnumerator. Adapt it back.
        if (name == "GetEnumerator" && argCount == 0 && LooksLikeStreamPipeline(receiver))
        {
            context.AddImport("io.github.ningpp.compat.CSharpGenericEnumerator");
            rendered = $"CSharpGenericEnumerator.from({receiver}.iterator())";
            return true;
        }

        if (symbol is not null && owner != "System.Linq.Enumerable")
            return false;

        // string.Reverse() (Enumerable.Reverse on a string receiver) — the C#
        // intent is a reversed character sequence, usually fed to string.Concat;
        // StringHelper.reverse returns the materialized reversed string.
        if (name == "Reverse" && argCount == 0
            && context.GetTypeInfo(memberAccess.Expression).Type?.SpecialType == SpecialType.System_String)
        {
            context.AddImport("io.github.ningpp.compat.StringHelper");
            rendered = $"StringHelper.reverse({receiver})";
            return true;
        }

        if (!IsEnumerableReceiver(memberAccess.Expression, context))
            return false;

        // Already a Java Stream pipeline — nothing to wrap.
        bool receiverIsStream = LooksLikeStreamPipeline(receiver);

        if (name == "ToList" && argCount == 0)
        {
            var stream = receiverIsStream
                ? receiver
                : BuildStreamReceiver(receiver, memberAccess.Expression, context);
            context.AddImport("io.github.ningpp.compat.CSharpList");
            rendered = $"{stream}.collect(CSharpList.toCSharpList())";
            return true;
        }

        if ((name == "Concat" || name == "Union") && argCount == 1)
        {
            var first = receiverIsStream
                ? receiver
                : BuildStreamReceiver(receiver, memberAccess.Expression, context);
            var otherArg = invocation.ArgumentList.Arguments[0].Expression;
            var otherText = ExpressionTransformerFacade.Instance.Transform(otherArg, context);
            var second = LooksLikeStreamPipeline(otherText)
                ? otherText
                : BuildStreamReceiver(otherText, otherArg, context);
            context.AddImport("java.util.stream.Stream");
            context.AddImport("io.github.ningpp.compat.CSharpList");
            // Collect because the C# expression yields IEnumerable, and the
            // assignment target cannot hold a Stream.
            rendered = name == "Concat"
                ? $"Stream.concat({first}, {second}).collect(CSharpList.toCSharpList())"
                : $"Stream.concat({first}, {second}).distinct().collect(CSharpList.toCSharpList())";
            return true;
        }

        // IEnumerable.Cast<TResult>() — converted collections already carry
        // their C# element type, so the cast is an identity: a stream pipeline
        // stays as-is (type arguments already flow), everything else wraps into
        // the generic-iterable world so downstream Iterable/Stream consumers
        // bind against a typed element instead of a bare `cast()` call.
        // The from() call carries an explicit type witness: receivers typed
        // CSharpIList<T>/CSharpCollection erase their iterator to Object, and
        // an unwitnessed from() would infer a Stream<Object>.
        if (name == "Cast" && argCount == 0)
        {
            if (receiverIsStream)
            {
                rendered = receiver;
                return true;
            }
            context.AddImport("io.github.ningpp.compat.CSharpGenericIterable");
            var witness = TryGetCastTypeWitness(invocation, memberAccess, context);
            rendered = witness == null
                ? $"CSharpGenericIterable.from({receiver})"
                : $"CSharpGenericIterable.<{witness}>from({receiver})";
            return true;
        }

        // Enumerable.Count(predicate) — C# returns int; Java Stream.count()
        // returns long, so cast the filtered count.
        if (name == "Count" && argCount == 1)
        {
            var countStream = receiverIsStream
                ? receiver
                : BuildStreamReceiver(receiver, memberAccess.Expression, context);
            rendered = $"(int) {countStream}.filter({argumentText}).count()";
            return true;
        }

        var streamOperator = (name, argCount) switch
        {
            ("Where", 1) => "filter",
            ("Select", 1) => "map",
            ("SelectMany", 1) => "flatMap",
            ("Any", 1) => "anyMatch",
            ("All", 1) => "allMatch",
            ("Distinct", 0) => "distinct",
            _ => null
        };

        if (streamOperator == null)
            return false;

        var streamReceiver = receiverIsStream
            ? receiver
            : BuildStreamReceiver(receiver, memberAccess.Expression, context);
        rendered = argCount == 0
            ? $"{streamReceiver}.{streamOperator}()"
            : $"{streamReceiver}.{streamOperator}({argumentText})";
        return true;
    }

    /// <summary>
    /// Adds the configured imports for a static call receiver such as
    /// <c>CharUnicodeInfo.GetUnicodeCategory</c> when the semantic symbol is
    /// unavailable and no method mapping fired. The receiver text itself is
    /// already the mapped simple name via the type-table fallback.
    /// </summary>
    private static void AddStaticReceiverTypeImports(
        ExpressionSyntax receiverExpression,
        ConversionContext context)
    {
        // 仅静态接收者（类型名）适用：实例成员访问（prefixes.Keys）的 Name 是
        // 成员名，按简单名查类型表会错配同名类型（Keys → dotnet.winforms.swing.Keys，
        // 注入不存在的包 import）。接收者可绑定且不是类型名时直接跳过。
        if (context.GetSymbolInfo(receiverExpression).Symbol is not (null or INamedTypeSymbol))
            return;

        var simpleName = receiverExpression switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
            QualifiedNameSyntax q => q.Right.Identifier.Text,
            _ => null
        };
        if (string.IsNullOrEmpty(simpleName))
            return;

        var configKey = context.TypeMappings.FindConfigKeyBySimpleName(simpleName);
        if (configKey == null)
            return;

        foreach (var import in context.TypeMappings.GetRequiredImports(configKey))
            context.AddImport(import);
    }

    private static bool IsEnumerableReceiver(ExpressionSyntax receiverExpression, ConversionContext context)
    {
        var type = context.GetTypeInfo(receiverExpression).Type;
        if (type is IArrayTypeSymbol)
            return true;
        if (type is null || type.TypeKind is TypeKind.Error or TypeKind.Unknown)
        {
            // Post-preprocessed trees can lack a semantic model for synthesized
            // nodes; fall back to the syntactic shape of an Enumerable chain.
            for (ExpressionSyntax? current = receiverExpression; current != null;)
            {
                if (current is ParenthesizedExpressionSyntax paren)
                {
                    current = paren.Expression;
                    continue;
                }

                if (current is InvocationExpressionSyntax invocation
                    && invocation.Expression is MemberAccessExpressionSyntax access)
                {
                    if (access.Name.Identifier.Text is
                        "Where" or "Select" or "SelectMany" or "Range" or "Concat"
                        or "Union" or "ToList" or "Any" or "All" or "Distinct")
                    {
                        return true;
                    }

                    current = access.Expression;
                    continue;
                }

                if (current is IdentifierNameSyntax { Identifier.Text: "Enumerable" })
                    return true;

                break;
            }

            return false;
        }
        // C# arrays implement IEnumerable<T>: residual LINQ operators on arrays
        // (`allChars.Count(c => …)`) must route through the stream path too.
        if (type is IArrayTypeSymbol)
            return true;
        if (type is not INamedTypeSymbol named)
            return false;
        if (named.SpecialType == SpecialType.System_String)
            return false;
        if (named.Name is "IQueryable" or "IOrderedQueryable")
            return false;
        if (named.Name is "IEnumerable" or "IOrderedEnumerable" or "IGrouping" or "ILookup")
            return true;
        return named.AllInterfaces.Any(i =>
            i.Name == "IEnumerable"
            && i.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic");
    }

    private static string BuildStreamReceiver(
        string receiverText,
        ExpressionSyntax receiverExpression,
        ConversionContext context)
    {
        if (LooksLikeStreamPipeline(receiverText))
            return receiverText;

        if (context.GetTypeInfo(receiverExpression).Type is IArrayTypeSymbol array)
        {
            // Arrays.stream has int/long/double/T[] overloads only; char,
            // boolean, byte, short and float arrays must box through the
            // reflection-backed compat iterable instead. The explicit witness
            // keeps the stream element type from erasing to Object.
            if (array.ElementType is INamedTypeSymbol { TypeKind: TypeKind.Enum }
                || (array.ElementType.IsValueType && array.ElementType.SpecialType
                    is not (SpecialType.System_Int32 or SpecialType.System_Int64
                        or SpecialType.System_Double)))
            {
                context.AddImport("io.github.ningpp.compat.CSharpGenericIterable");
                context.AddImport("java.util.stream.StreamSupport");
                var witness = BoxPrimitive(context.MapType(array.ElementType));
                return $"StreamSupport.stream(CSharpGenericIterable.<{witness}>fromAnyTyped({receiverText}).spliterator(), false)";
            }
            context.AddImport("java.util.Arrays");
            return array.ElementType.IsValueType
                ? $"Arrays.stream({receiverText}).boxed()"
                : $"Arrays.stream({receiverText})";
        }

        context.AddImport("java.util.stream.StreamSupport");
        return $"StreamSupport.stream(({receiverText}).spliterator(), false)";
    }

    private static bool LooksLikeStreamPipeline(string expression)
        => expression.Contains(".stream(", StringComparison.Ordinal)
            || expression.Contains(".filter(", StringComparison.Ordinal)
            || expression.Contains(".map(", StringComparison.Ordinal)
            || expression.Contains(".flatMap(", StringComparison.Ordinal)
            || expression.Contains(".sorted(", StringComparison.Ordinal)
            || expression.Contains(".distinct(", StringComparison.Ordinal)
            || expression.Contains(".anyMatch(", StringComparison.Ordinal)
            || expression.Contains(".allMatch(", StringComparison.Ordinal);

    /// <summary>
    /// Receiver-type driven variant of <see cref="TryRenderPrimitiveInstanceCall"/>.
    /// The semantic method symbol can be missing in post-preprocessed trees; the
    /// receiver's static type still tells us the call is on a Java primitive.
    /// </summary>
    private static bool TryRenderPrimitiveMemberByType(
        InvocationExpressionSyntax invocation,
        ExpressionSyntax receiverExpression,
        string receiver,
        string argumentText,
        string originalMethodName,
        ConversionContext context,
        out string rendered)
    {
        rendered = string.Empty;
        var boxed = TryGetBoxedPrimitiveName(context.GetTypeInfo(receiverExpression).Type);
        if (boxed == null)
            return false;

        if (originalMethodName == "GetHashCode"
            && invocation.ArgumentList.Arguments.Count == 0)
        {
            rendered = $"{boxed}.hashCode({receiver})";
            return true;
        }

        // C# value-type Equals maps to IEquatable<T>.Equals (equalsTo) when the
        // call binds through the implicit interface implementation, but a Java
        // primitive cannot be dereferenced. Box the receiver so the C# value
        // equality semantics (including NaN and -0.0) are preserved.
        if (originalMethodName == "Equals"
            && invocation.ArgumentList.Arguments.Count == 1)
        {
            rendered = $"{boxed}.valueOf({receiver}).equals({argumentText})";
            return true;
        }

        if (originalMethodName == "ToString")
        {
            var args = invocation.ArgumentList.Arguments;

            // int.ToString("X2") → String.format("%02X", value). Integral
            // format strings have no wrapper-class equivalent.
            if (args.Count == 1
                && args[0].Expression is LiteralExpressionSyntax literal
                && literal.IsKind(SyntaxKind.StringLiteralExpression)
                && literal.Token.ValueText is { Length: 2 } fmt
                && char.IsDigit(fmt[1])
                && fmt[0] is 'X' or 'x')
            {
                // `((int)c).ToString("X4")`: the redundant char→int widening cast
                // is elided during rendering, but String.format's Object varargs
                // would box the char back to Character, which Formatter rejects
                // for %X ("x != java.lang.Character"). Re-impose the int box.
                var formatReceiver = IsCharTypedBeforeWidening(invocation.Expression, context)
                    ? $"(int)({receiver})"
                    : receiver;
                rendered = $"String.format(\"%0{fmt[1]}{fmt[0]}\", {formatReceiver})";
                return true;
            }

            var providerOnly = args.Count == 0
                || (args.Count == 1
                    && !args[0].RefKindKeyword.IsKind(SyntaxKind.RefKeyword)
                    && (context.GetTypeInfo(args[0].Expression).Type?.ToDisplayString()
                        is "System.IFormatProvider" or "System.Globalization.CultureInfo"));
            if (providerOnly)
            {
                rendered = $"{boxed}.toString({receiver})";
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the invoked member's receiver is char-typed, or is a widening
    /// cast whose operand is char-typed — the rendered form loses the (int) box
    /// that varargs consumers like String.format need to avoid a Character
    /// argument. <paramref name="callee"/> is the invocation's callee
    /// (`((int)c).ToString`), whose receiver segment is inspected.
    /// </summary>
    private static bool IsCharTypedBeforeWidening(ExpressionSyntax callee, ConversionContext context)
    {
        if (callee is MemberAccessExpressionSyntax member)
            callee = member.Expression;
        while (callee is ParenthesizedExpressionSyntax parenthesized)
            callee = parenthesized.Expression;
        if (context.GetTypeInfo(callee).Type?.SpecialType == SpecialType.System_Char)
            return true;
        return callee is CastExpressionSyntax cast
            && context.GetTypeInfo(cast.Expression).Type?.SpecialType == SpecialType.System_Char;
    }

    /// <summary>
    /// xUnit's <c>Assert.Throws&lt;T&gt;(code)</c> / <c>Assert.IsType&lt;T&gt;(obj)</c>
    /// carry the asserted type as a generic argument, which Java erases at the
    /// call site — the erased 1-arg compat overloads can only assert
    /// "some exception". The compat Assert also exposes Class-based overloads;
    /// when the explicit type argument resolves to a concrete type, materialize
    /// it as a leading class literal so the runtime type check survives.
    /// </summary>
    private static bool TryMaterializeAssertTypeArgument(
        InvocationExpressionSyntax node, ConversionContext context, out string classLiteral)
    {
        classLiteral = null!;
        if (node.Expression is not MemberAccessExpressionSyntax { Name: GenericNameSyntax generic })
            return false;
        if (generic.Identifier.Text is not ("Throws" or "ThrowsAny" or "IsType" or "IsNotType"))
            return false;
        if (generic.TypeArgumentList.Arguments.Count != 1)
            return false;
        if (context.GetSymbolInfo(node).Symbol is not IMethodSymbol { IsStatic: true } method
            || method.ContainingType?.ToDisplayString() != "Xunit.Assert")
            return false;
        var typeArgument = context.GetTypeInfo(generic.TypeArgumentList.Arguments[0]).Type;
        if (typeArgument == null
            || typeArgument is ITypeParameterSymbol or IErrorTypeSymbol)
        {
            return false;
        }
        classLiteral = ConversionContext.GetClassLiteral(typeArgument, context);
        return true;
    }

    /// <summary>
    /// Instance calls on primitive receivers cannot dereference in Java:
    /// <c>long v; v.GetHashCode()</c> → <c>Long.hashCode(v)</c>,
    /// <c>v.ToString()</c> → <c>Long.toString(v)</c>.
    /// </summary>
    private static bool TryRenderPrimitiveInstanceCall(
        IMethodSymbol method,
        ExpressionSyntax receiverExpression,
        string receiver,
        string originalMethodName,
        ConversionContext context,
        out string rendered)
    {
        rendered = string.Empty;

        if (method.IsStatic)
        {
            return false;
        }

        var boxed = TryGetBoxedPrimitiveName(context.GetTypeInfo(receiverExpression).Type);
        if (boxed == null)
        {
            return false;
        }

        if (originalMethodName == "GetHashCode")
        {
            rendered = $"{boxed}.hashCode({receiver})";
            return true;
        }

        if (originalMethodName == "ToString"
            && (method.Parameters.Length == 0
                || (method.Parameters.Length == 1
                    && method.Parameters[0].Type.ToDisplayString() == "System.IFormatProvider")))
        {
            // int.ToString(IFormatProvider): the provider does not affect integral
            // formatting for InvariantCulture, and Java's primitive wrappers have no
            // such overload.
            rendered = $"{boxed}.toString({receiver})";
            return true;
        }

        return false;
    }

    /// <summary>
    /// <c>s.Equals(other, StringComparison.OrdinalIgnoreCase)</c> and the static
    /// <c>string.Equals(a, b, comparison)</c> → <c>StringHelper.equals(...)</c>.
    /// The arity-only method mapping cannot tell the 2-parameter instance overload
    /// (string, StringComparison) apart from the 2-parameter static object.Equals
    /// shape, so the parameter type is resolved semantically here.
    /// </summary>
    private static bool TryRenderStringComparisonEquals(
        InvocationExpressionSyntax invocation,
        MemberAccessExpressionSyntax memberAccess,
        string receiver,
        string arguments,
        ConversionContext context,
        out string rendered)
    {
        rendered = string.Empty;

        if (context.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method
            || method.ContainingType.SpecialType != SpecialType.System_String
            || memberAccess.Name.Identifier.Text != "Equals"
            || !method.Parameters.Any(p => p.Type.ToDisplayString() == "System.StringComparison"))
        {
            return false;
        }

        context.AddImport("io.github.ningpp.compat.StringHelper");
        rendered = method.IsStatic
            ? $"StringHelper.equals({arguments})"
            : (string.IsNullOrEmpty(arguments)
                ? $"StringHelper.equals({receiver})"
                : $"StringHelper.equals({receiver}, {arguments})");
        return true;
    }

    /// <summary>
    /// <c>s.Split(',')</c>/<c>s.Split(',', ';')</c> (char or params char[] overloads) →
    /// <c>StringHelper.split(s, new char[]{...})</c>. Java's String.split takes a regex
    /// String, so a char literal argument must not pass through the default mapping.
    /// Only the all-char-argument overloads are handled here; char[] variables and
    /// StringSplitOptions keep their existing conversion.
    /// </summary>
    private static bool TryRenderStringCharSplit(
        InvocationExpressionSyntax invocation,
        MemberAccessExpressionSyntax memberAccess,
        string receiver,
        ConversionContext context,
        out string rendered)
    {
        rendered = string.Empty;

        var args = invocation.ArgumentList.Arguments;
        if (context.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method
            || memberAccess.Name.Identifier.Text != "Split"
            || method.ContainingType.SpecialType != SpecialType.System_String
            || args.Count == 0
            || context.SemanticModel == null)
        {
            return false;
        }

        var charArgs = new List<string>();
        foreach (var arg in args)
        {
            if (context.GetTypeInfo(arg.Expression).Type?.SpecialType != SpecialType.System_Char)
            {
                return false;
            }

            charArgs.Add(ExpressionTransformerFacade.Instance.Transform(arg.Expression, context));
        }

        context.AddImport("io.github.ningpp.compat.StringHelper");
        rendered = $"StringHelper.split({receiver}, new char[]{{{string.Join(", ", charArgs)}}})";
        return true;
    }

    /// <summary>
    /// Returns the renamed Java method when this invocation targets an overload that
    /// was renamed to avoid a Java erasure clash, or null.
    /// </summary>
    private static string? ConflictRenamedName(InvocationExpressionSyntax invocation, ConversionContext context)
        => context.GetSymbolInfo(invocation).Symbol is IMethodSymbol method
           && context.TryGetErasureConflictMethodName(method, out var renamed)
            ? renamed
            : null;

    /// <summary>
    /// <c>string.Create(int, TState, SpanAction&lt;char,TState&gt;)</c> →
    /// <c>StringHelper.createString(...)</c>: the compat helper takes the same
    /// (length, state, BiConsumer&lt;Span&lt;Character&gt;,TState&gt;) shape, so a
    /// method-group argument such as <c>Uri::cs2jLambda_...</c> stays valid.
    /// </summary>
    private static bool TryRenderStringCreate(
        InvocationExpressionSyntax invocation,
        MemberAccessExpressionSyntax memberAccess,
        string arguments,
        ConversionContext context,
        out string rendered)
    {
        rendered = string.Empty;
        if (memberAccess.Name.Identifier.Text != "Create")
            return false;
        if (context.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method
            || !method.IsStatic
            || method.ContainingType.SpecialType != SpecialType.System_String
            || method.Parameters.Length != 3)
        {
            return false;
        }

        context.AddImport("io.github.ningpp.compat.StringHelper");
        rendered = $"StringHelper.createString({arguments})";
        return true;
    }

    /// <summary>
    /// Static calls whose containing type has an explicit TypeMappings.json type
    /// mapping but whose method has no method-mapping entry: type mappings only fire
    /// at type-reference positions, so expression-position receivers must be rewritten
    /// here (<c>Cs2jUnsigned.UnsignedMath.AndUInt64(a, b)</c> →
    /// <c>UnsignedMath.andUInt64(a, b)</c> + the mapped import).
    /// </summary>
    private static bool TryRenderMappedStaticTypeReceiver(
        IMethodSymbol method,
        string originalMethodName,
        string arguments,
        ConversionContext context,
        out string rendered)
    {
        rendered = string.Empty;

        if (!method.IsStatic || method.ContainingType is not INamedTypeSymbol containingType)
        {
            return false;
        }

        var display = containingType.ToDisplayString();
        var fqn = $"{containingType.ContainingNamespace}.{containingType.Name}";

        // C# static Type.GetType(string[, bool]) resolves the System.Type →
        // java.lang.Class mapping and would render `Class.getClass(name)` —
        // Object.getClass() takes no arguments. Route the lookup through the
        // compat TypeHelper (Class.forName-based) instead.
        if (originalMethodName == "GetType"
            && (display == "System.Type" || fqn == "System.Type"))
        {
            context.AddImport("io.github.ningpp.compat.TypeHelper");
            rendered = $"TypeHelper.getType({arguments})";
            return true;
        }

        string? mappedType = null;
        var configKey = context.TypeMappings.HasTypeMapping(display) ? display
            : context.TypeMappings.HasTypeMapping(fqn) ? fqn
            : null;
        if (configKey == null)
        {
            return false;
        }

        mappedType = context.TypeMappings.MapType(configKey);
        foreach (var import in context.TypeMappings.GetRequiredImports(configKey))
        {
            context.AddImport(import);
        }

        rendered = $"{mappedType}.{WellKnownMethodName(originalMethodName)}({arguments})";
        return true;
    }

    /// <summary>
    /// Resolves a static method's containing type through the explicit
    /// TypeMappings.json type table and returns the receiver to render.
    /// Fully-qualified mapped types are emitted inline (no import needed);
    /// simple mapped names become an identifier and pull the mapping's imports.
    /// Returns false when no explicit type mapping exists (plain simple-name
    /// fallback must keep the caller's existing receiver rendering).
    /// </summary>
    private static bool TryGetMappedTypeSimpleName(
        IMethodSymbol method,
        ConversionContext context,
        out JavaExpression receiver)
    {
        receiver = null!;

        if (!method.IsStatic || method.ContainingType is not INamedTypeSymbol containingType)
        {
            return false;
        }

        var display = containingType.ToDisplayString();
        var fqn = $"{containingType.ContainingNamespace}.{containingType.Name}";

        var configKey = context.TypeMappings.HasTypeMapping(display) ? display
            : context.TypeMappings.HasTypeMapping(fqn) ? fqn
            : null;
        if (configKey == null)
        {
            return false;
        }

        var mappedType = context.TypeMappings.MapType(configKey);
        if (string.IsNullOrEmpty(mappedType))
        {
            return false;
        }

        if (mappedType.Contains('.'))
        {
            receiver = new JavaRawExpression(mappedType);
            return true;
        }

        foreach (var import in context.TypeMappings.GetRequiredImports(configKey))
        {
            context.AddImport(import);
        }

        receiver = new JavaIdentifierExpression { Name = mappedType };
        return true;
    }

    /// <summary>
    /// Well-known C# → Java instance method renames shared with the declaration side
    /// (MethodTransformer.JavaName) and the null-conditional binder
    /// (ExpressionTransformerFacade): GetHashCode → hashCode, GetType → getClass,
    /// GetEnumerator → iterator, Dispose → close, ToLower/Invariant → toLowerCase,
    /// ToUpper/Invariant → toUpperCase. Everything else gets get_/set_ stripping
    /// (member-desugar accessors) followed by ordinary camel-casing.
    /// </summary>
    private static string WellKnownMethodName(string name)
    {
        var renamed = name switch
        {
            "GetHashCode" => "hashCode",
            "GetEnumerator" => "iterator",
            "GetType" => "getClass",
            "Dispose" => "close",
            "ToLower" or "ToLowerInvariant" => "toLowerCase",
            "ToUpper" or "ToUpperInvariant" => "toUpperCase",
            _ when name.StartsWith("get_") || name.StartsWith("set_")
                => char.ToUpperInvariant(name[4]) + name[5..],
            _ => name.Length > 0 ? char.ToLowerInvariant(name[0]) + name[1..] : name
        };
        return ConversionContext.EscapeJavaKeyword(renamed);
    }

    /// <summary>
    /// Maps C# built-in primitive static method names to their Java equivalents.
    /// e.g. double.IsInfinity → Double.isInfinite, int.Parse → Integer.parseInt
    /// </summary>
    private static string MapPrimitiveStaticMethodName(string primitiveKeyword, string methodName)
        => (primitiveKeyword, methodName) switch
        {
            // char-specific mappings: the C# and Java Character classes differ.
            ("char", "IsLower") => "isLowerCase",
            ("char", "IsUpper") => "isUpperCase",
            ("char", "IsWhiteSpace") => "isWhitespace",
            ("char", "IsControl") => "isISOControl",
            ("char", "IsSurrogate") => "isSurrogate",
            ("char", "IsHighSurrogate") => "isHighSurrogate",
            ("char", "IsLowSurrogate") => "isLowSurrogate",
            ("char", "IsSurrogatePair") => "isSurrogatePair",
            ("char", "GetNumericValue") => "getNumericValue",
            ("char", "ToLower") => "toLowerCase",
            ("char", "ToLowerInvariant") => "toLowerCase",
            ("char", "ToUpper") => "toUpperCase",
            ("char", "ToUpperInvariant") => "toUpperCase",
            ("char", "ToString") => "toString",
            ("char", "ConvertFromUtf32") => "toChars",
            ("char", "ConvertToUtf32") => "toCodePoint",
            ("char", "Parse") => "valueOf",
            // generic mappings shared across all primitive types
            (_, "IsInfinity") => "isInfinite",
            (_, "IsNaN") => "isNaN",
            (_, "IsFinite") => "isFinite",
            (_, "Parse") => primitiveKeyword switch
            {
                "int" => "parseInt",
                "long" => "parseLong",
                "double" => "parseDouble",
                "float" => "parseFloat",
                "short" => "parseShort",
                "byte" => "parseByte",
                "sbyte" => "parseByte",
                "uint" => "parseUnsignedInt",
                "ulong" => "parseUnsignedLong",
                "ushort" => "parseUnsignedInt",
                _ => "parse" + char.ToUpperInvariant(primitiveKeyword[0]) + primitiveKeyword[1..]
            },
            _ when methodName.Length > 0 => char.ToLowerInvariant(methodName[0]) + methodName[1..],
            _ => methodName
        };

    private static string? MapPrimitiveParseHelper(string primitiveKeyword)
        => primitiveKeyword switch
        {
            "int" => "MathHelper.parseInt",
            "long" => "MathHelper.parseLong",
            "double" => "MathHelper.parseDouble",
            "float" => "MathHelper.parseFloat",
            "decimal" => "Decimal.parse",
            "uint" => "MathHelper.parseUInt",
            "ulong" => "MathHelper.parseULong",
            "ushort" => "MathHelper.parseUShort",
            "short" => "MathHelper.parseShort",
            "byte" => "MathHelper.parseByte",
            "sbyte" => "MathHelper.parseByte",
            _ => null
        };

    private static string? MapPrimitiveTryParseHelper(string primitiveKeyword)
        => primitiveKeyword switch
        {
            "double" => "MathHelper.tryParseDouble",
            "float" => "MathHelper.tryParseFloat",
            "int" => "MathHelper.tryParseInt",
            "long" => "MathHelper.tryParseLong",
            "short" => "MathHelper.tryParseShort",
            "byte" => "MathHelper.tryParseByte",
            "char" => "MathHelper.tryParseChar",
            "bool" => "MathHelper.tryParseBool",
            "uint" => "MathHelper.tryParseUInt",
            "ulong" => "MathHelper.tryParseULong",
            "ushort" => "MathHelper.tryParseUShort",
            _ => null
        };

    /// <summary>
    /// Applies a configured mapped method name to an IR call. Mapped names containing a dot
    /// (e.g. "StringHelper.insert") are static compatibility helpers: for instance calls the
    /// receiver becomes the first argument; for static calls the type receiver is dropped.
    /// Regular call arguments are appended by the caller afterwards.
    /// </summary>
    private static void ApplyMappedName(
        JavaMethodCallExpression call,
        string mappedName,
        bool isStatic,
        JavaExpression receiver)
    {
        if (mappedName.Contains('.'))
        {
            // Static helper call — clear the instance target and pass the
            // receiver as the first argument for instance invocations.
            call.Target = null;
            call.MethodName = mappedName;
            if (!isStatic)
                call.Arguments.Add(receiver);
        }
        else
        {
            call.MethodName = mappedName;
        }
    }

    /// <summary>
    /// Console.WriteLine/Write 复合格式重载的 println/print 桥接判定（见调用点注释）。
    /// 仅当：映射目标为 out.println/out.print、被调包含类型精确为 System.Console、
    /// 实参 ≥2 且首参 C# 类型为 string 时命中，实参串整体包 StringHelper.formatCs。
    /// </summary>
    private static bool TryBridgeConsoleFormatCall(
        InvocationExpressionSyntax node,
        string mappedName,
        string arguments,
        ConversionContext context,
        out string bridgedArguments)
    {
        bridgedArguments = string.Empty;
        if (!IsConsoleFormatMappedCall(node, mappedName, context))
            return false;

        var parts = SplitTopLevelArguments(arguments);
        if (parts.Count < 2)
            return false;
        // 首参必须是 string（复合格式串）；Console.Write(char[], int, int) 写段
        // 重载首参为数组，不在此列。
        if (context.GetTypeInfo(node.ArgumentList.Arguments[0].Expression).Type?.SpecialType
            != SpecialType.System_String)
            return false;

        context.AddImport("io.github.ningpp.compat.StringHelper");
        bridgedArguments = $"StringHelper.formatCs({string.Join(", ", parts)})";
        return true;
    }

    /// <summary>
    /// 点分静态 compat 助手映射（如 System.Exception.InnerException →
    /// ExceptionCompat.getInnerException）渲染为 Helper.method(receiver)，其导入
    /// 无法由 receiver 类型条目覆盖（receiver 常是 Exception 的子类，走不到
    /// System.Exception 的 imports）。在此按映射名前缀补 compat 导入，与
    /// AddReceiverTypeImports 的 GetRequiredImports 互补。
    /// </summary>
    internal static void AddCompatHelperImport(string mappedName, ConversionContext context)
    {
        // ExceptionCompat 为当前唯一以点分静态助手形态出现在方法映射表中的 compat 类；
        // 后续新增时在此登记（或扩展 TypeMappings 条目级 imports 结构）。
        if (mappedName.StartsWith("ExceptionCompat.", StringComparison.Ordinal))
            context.AddImport("io.github.ningpp.compat.ExceptionCompat");
    }

    /// <summary>
    /// 被调是否为映射到 out.println/out.print 的 System.Console 方法。按包含类型
    /// 精确判定：Debug/Trace.WriteLine(string, string) 的 category 不是格式实参，
    /// 不能按映射形状桥接。
    /// </summary>
    private static bool IsConsoleFormatMappedCall(
        InvocationExpressionSyntax invocation,
        string mappedName,
        ConversionContext context)
        => mappedName is "out.println" or "out.print"
           && context.GetSymbolInfo(invocation).Symbol is IMethodSymbol method
           && method.ContainingType?.ToDisplayString() == "System.Console";

    /// <summary>
    /// Renders the Java source for a method whose name was resolved through TypeMappings.json.
    /// </summary>
    private static string RenderMappedCall(
        string mappedName,
        bool isStatic,
        string receiver,
        string arguments)
    {
        if (mappedName.Contains('.'))
        {
            // Static compatibility helper: instance receiver is passed as the first argument;
            // a static type receiver (e.g. string.IsNullOrEmpty) is dropped entirely.
            if (isStatic)
            {
                // "out.println" is a field access on java.lang.System, not a class name:
                // qualify with System (java.lang needs no import).
                var rendered = mappedName.StartsWith("out.", StringComparison.Ordinal)
                    ? "System." + mappedName
                    : mappedName;
                return $"{rendered}({arguments})";
            }
            return string.IsNullOrEmpty(arguments)
                ? $"{mappedName}({receiver})"
                : $"{mappedName}({receiver}, {arguments})";
        }

        return $"{receiver}.{mappedName}({arguments})";
    }

    /// <summary>
    /// Adds imports for the static/instance receiver's type. Static receivers such as
    /// <c>ArrayPool&lt;T&gt;.Shared</c> or <c>SegmentStringBuilder.Create()</c> expose the type
    /// only inside an expression, so the declaration-driven import collector misses them.
    /// </summary>
    private static void AddReceiverTypeImports(ExpressionSyntax receiver, ConversionContext context)
    {
        if (receiver is not (IdentifierNameSyntax or MemberAccessExpressionSyntax)) return;
        if (context.GetTypeInfo(receiver).Type is not INamedTypeSymbol type) return;
        if (type.SpecialType != SpecialType.None) return;
        if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct
            or TypeKind.Interface or TypeKind.Enum)) return;
        // Cs2jByRef support forms are resolved by dedicated compat mappings/classes;
        // MapType on them fails fast (CS2J3320) and their receiver stays FQN.
        if (type.ContainingNamespace?.ToDisplayString()
                .StartsWith("Cs2j", StringComparison.Ordinal) == true) return;

        var configuredImports = context.TypeMappings.GetRequiredImports(
            type.OriginalDefinition.ToDisplayString());
        if (configuredImports.Count == 0)
        {
            // Roslyn displays ArrayPool<T> but the config key is the metadata name
            // (ArrayPool`1); try the backtick form as well.
            var metadataKey = (type.ContainingNamespace?.ToDisplayString() is { Length: > 0 } ns
                ? ns + "." : string.Empty) + type.OriginalDefinition.MetadataName;
            configuredImports = context.TypeMappings.GetRequiredImports(metadataKey);
        }
        foreach (var import in configuredImports)
            context.AddImport(import);
        // Configured mapping already supplies the Java type + imports (Cs2jUnsigned/
        // Cs2jArray support namespaces → compat); never add the C# package as import.
        if (configuredImports.Count > 0) return;

        // Project-local types have no config mapping; add their package-qualified name.
        if (type.Locations.Any(l => l.IsInSource) && type.ContainingType == null)
        {
            // 顶层源类型名必须走 GetJavaTopLevelTypeName（SR → DotnetUriSR 等
            // 跨程序集作用域改名），否则 import 仍指向未生成的简名。
            var javaName = context.GetJavaTopLevelTypeName(type);
            if (javaName.Contains('<'))
                javaName = javaName[..javaName.IndexOf('<')];
            if (javaName.Contains('.')) return;
            var package = context.NamespaceToPackage(
                type.ContainingNamespace?.ToDisplayString() ?? string.Empty);
            if (!string.IsNullOrEmpty(package))
                context.AddImport($"{package}.{javaName}");
        }
    }

    /// <summary>
    /// Static receiver written with its full C# namespace (extension-method lowering emits
    /// <c>System.Collections.HashtableExtensions.TryGetValue(...)</c>). Source types must be
    /// rendered by their Java simple name plus the mapped package import; otherwise the
    /// leading C# namespace is parsed as <c>java.lang.System</c>.
    /// </summary>
    private static bool TryMapStaticReceiver(
        ExpressionSyntax receiver, ConversionContext context, out string mappedReceiver)
    {
        mappedReceiver = string.Empty;
        if (context.GetTypeInfo(receiver).Type is not INamedTypeSymbol type) return false;
        if (type.SpecialType != SpecialType.None
            || type.ContainingType != null
            || !type.Locations.Any(l => l.IsInSource))
            return false;
        if (type.ContainingNamespace?.ToDisplayString()
                .StartsWith("Cs2j", StringComparison.Ordinal) == true)
            return false;
        var javaName = context.GetJavaTopLevelTypeName(type);
        if (javaName.Contains('<'))
            javaName = javaName[..javaName.IndexOf('<')];
        if (javaName.Contains('.')) return false;
        var configuredImports = context.TypeMappings.GetRequiredImports(
            type.OriginalDefinition.ToDisplayString());
        if (configuredImports.Count == 0)
        {
            // Roslyn displays ArrayPool<T> but the config key is the metadata name
            // (ArrayPool`1); try the backtick form as well.
            var metadataKey = (type.ContainingNamespace?.ToDisplayString() is { Length: > 0 } ns
                ? ns + "." : string.Empty) + type.OriginalDefinition.MetadataName;
            configuredImports = context.TypeMappings.GetRequiredImports(metadataKey);
        }
        foreach (var import in configuredImports)
            context.AddImport(import);
        if (configuredImports.Count == 0)
        {
            var package = context.NamespaceToPackage(
                type.ContainingNamespace?.ToDisplayString() ?? string.Empty);
            if (!string.IsNullOrEmpty(package))
                context.AddImport($"{package}.{javaName}");
        }
        mappedReceiver = javaName;
        return true;
    }

    private static bool TryGetEnumerableElementType(ITypeSymbol? type, out ITypeSymbol element)
    {
        element = null!;
        if (type is not INamedTypeSymbol named) return false;
        bool IsEnumerable(INamedTypeSymbol candidate)
            => candidate.Name == "IEnumerable"
               && candidate.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic"
               && candidate.TypeArguments.Length == 1;
        if (IsEnumerable(named))
        {
            element = named.TypeArguments[0];
            return true;
        }
        foreach (var iface in named.AllInterfaces)
        {
            if (IsEnumerable(iface))
            {
                element = iface.TypeArguments[0];
                return true;
            }
        }
        return false;
    }

    private static string BoxPrimitive(string mapped) => mapped switch
    {
        "char" => "Character",
        "int" => "Integer",
        "long" => "Long",
        "short" => "Short",
        "byte" => "Byte",
        "boolean" => "Boolean",
        "float" => "Float",
        "double" => "Double",
        _ => mapped,
    };

    /// <summary>
    /// Enumerable.ToArray() on a primitive-element source keeps its C# primitive
    /// array type (char[]), but the Java render pipeline materializes a boxed
    /// Character[] — javac reports the reference/primitive mismatch as a confusing
    /// generic-inference error. Unbox through the ArrayHelper converters.
    /// </summary>
    private static string WrapPrimitiveArrayUnbox(string expr, ITypeSymbol? invocationType, ConversionContext context)
    {
        if (invocationType is not IArrayTypeSymbol array)
            return expr;

        var unboxer = array.ElementType.SpecialType switch
        {
            SpecialType.System_Char => "toCharArray",
            SpecialType.System_Int32 => "toIntArray",
            SpecialType.System_Int64 => "toLongArray",
            SpecialType.System_Double => "toDoubleArray",
            SpecialType.System_Single => "toFloatArray",
            SpecialType.System_Int16 => "toShortArray",
            SpecialType.System_Byte => "toByteArray",
            SpecialType.System_Boolean => "toBooleanArray",
            _ => null
        };
        if (unboxer == null)
            return expr;

        context.AddImport("io.github.ningpp.compat.ArrayHelper");
        return $"ArrayHelper.{unboxer}({expr})";
    }

    /// <summary>
    /// Resolves the Java element type for an IEnumerable.Cast&lt;TResult&gt;()
    /// invocation, used as the explicit type witness on the emitted
    /// CSharpGenericIterable.from() call. Prefers the invocation's semantic
    /// type (IEnumerable&lt;TResult&gt;), then the GenericNameSyntax argument.
    /// </summary>
    private static string? TryGetCastTypeWitness(
        InvocationExpressionSyntax invocation,
        MemberAccessExpressionSyntax memberAccess,
        ConversionContext context)
    {
        ITypeSymbol? element = null;
        if (context.GetTypeInfo(invocation).Type is INamedTypeSymbol { TypeArguments.Length: 1 } enumerable)
            element = enumerable.TypeArguments[0];
        else if (memberAccess.Name is GenericNameSyntax { TypeArgumentList.Arguments.Count: 1 } generic)
        {
            element = context.GetTypeInfo(generic.TypeArgumentList.Arguments[0]).Type;
        }
        if (element == null)
            return null;
        var mapped = context.MapType(element);
        return string.IsNullOrEmpty(mapped) || mapped == "__suppress__" ? null : BoxPrimitive(mapped);
    }

    /// <summary>
    /// OrderBy/ThenBy/OrderByDescending/ThenByDescending map to `sorted`, but
    /// their C# argument is a key selector (T → key), not a java.util.Comparator.
    /// Wrap the single key-selector argument with Comparator.comparing (and
    /// .reversed() for the descending variants) so the Java call compiles.
    /// </summary>
    private static void WrapSortKeySelectorInComparator(
        JavaMethodCallExpression call, string originalName, ConversionContext context)
    {
        if (call.MethodName != "sorted" || call.Arguments.Count != 1)
            return;
        var wrapped = WrapSortKeySelectorText(originalName, call.Arguments[0].ToInlineString(), context);
        call.Arguments[0] = new JavaRawExpression(wrapped);
    }

    private static string WrapSortKeySelectorText(string originalName, string arguments, ConversionContext context)
    {
        if (arguments.Length == 0)
            return arguments;
        context.AddImport("java.util.Comparator");
        return originalName is "OrderByDescending" or "ThenByDescending"
            ? $"Comparator.comparing({arguments}).reversed()"
            : $"Comparator.comparing({arguments})";
    }

    private static readonly Dictionary<string, string> DottedNameImports =
        new(StringComparer.Ordinal)
        {
            ["Objects"] = "java.util.Objects",
            ["ObjectHelper"] = "io.github.ningpp.compat.ObjectHelper",
            ["DelegateHelper"] = "io.github.ningpp.compat.DelegateHelper",
            ["Environment"] = "io.github.ningpp.compat.Environment",
            ["StringHelper"] = "io.github.ningpp.compat.StringHelper",
            ["StringBuilderExtensions"] = "io.github.ningpp.compat.StringBuilderExtensions",
            ["MathHelper"] = "io.github.ningpp.compat.MathHelper",
            ["TypeHelper"] = "io.github.ningpp.compat.TypeHelper",
            ["MemoryExtensions"] = "io.github.ningpp.compat.MemoryExtensions",
            ["AsyncTasks"] = "io.github.ningpp.compat.AsyncTasks",
            ["IPAddressHelper"] = "io.github.ningpp.compat.IPAddressHelper",
            ["UriHelper"] = "dotnet.system.UriHelper",
            ["EnumHelper"] = "io.github.ningpp.compat.EnumHelper",
            ["ArrayHelper"] = "io.github.ningpp.compat.ArrayHelper",
            ["SetCompat"] = "io.github.ningpp.compat.SetCompat",
            ["Debug"] = "io.github.ningpp.compat.Debug",
            ["Trace"] = "io.github.ningpp.compat.Trace",
        };

    /// <summary>
    /// Adds the import required by a dotted mapped name (simple prefix → FQN).
    /// No-op for non-dotted names or unknown prefixes.
    /// </summary>
    private static void AddImportForMappedName(string mappedName, ConversionContext context)
    {
        var dotIndex = mappedName.IndexOf('.');
        if (dotIndex <= 0)
            return;

        var prefix = mappedName.Substring(0, dotIndex);
        if (DottedNameImports.TryGetValue(prefix, out var import))
            context.AddImport(import);
    }

    /// <summary>
    /// Looks up the configured Java method name for the invocation via TypeMappings.json.
    /// Checks the method's containing type (display name, then namespace+name FQN),
    /// then every implemented interface whose member signature matches the invocation.
    /// Returns (null, false) when the semantic model is unavailable or no mapping exists.
    /// </summary>
    private static (string? MappedName, bool IsStatic) TryResolveMappedName(
        InvocationExpressionSyntax invocation,
        string originalName,
        ConversionContext context)
    {
        if (context.SemanticModel == null)
            return (null, false);
        if (context.GetSymbolInfo(invocation).Symbol is not IMethodSymbol methodSymbol)
            return (null, false);

        var paramCount = invocation.ArgumentList.Arguments.Count;
        var containingType = methodSymbol.ContainingType;

        var mappedName = context.TypeMappings.MapMethod(containingType.ToDisplayString(), originalName, paramCount)
            ?? context.TypeMappings.MapMethod(
                $"{containingType.ContainingNamespace}.{containingType.Name}", originalName, paramCount);

        // Implicit interface implementations (e.g. IEquatable<T>.Equals → equalsTo).
        // 与声明侧（MethodTransformer.TryMapImplicitInterfaceImplementation）同规则：
        // 接口方法的实际实现（FindImplementationForInterfaceMember）必须就是被调
        // 方法才改名；且接收者类型必须来自源码（声明侧才会同步改名）。否则跨模
        // 块符号可能把调用绑到 BCL 同名类型的接口实现（真实链 xml 编译把
        // uri1.Equals(uri2) 绑到 BCL Uri 的 IEquatable<Uri>.Equals(Uri)，接收者
        // 映射到 dotnet.system.Uri，而源内 Uri.java 只声明 equals(Object)）。
        if (mappedName == null
            && containingType is INamedTypeSymbol namedType
            && namedType.Locations.All(l => l.IsInSource))
        {
            foreach (var iface in namedType.AllInterfaces)
            {
                if (!MethodSignatureMatchesInterface(methodSymbol, iface, originalName))
                    continue;

                var interfaceMethod = iface.GetMembers(originalName).OfType<IMethodSymbol>().FirstOrDefault();
                if (interfaceMethod == null)
                    continue;
                var implementation = namedType.FindImplementationForInterfaceMember(interfaceMethod);
                if (implementation == null
                    || (!SymbolEqualityComparer.Default.Equals(implementation, methodSymbol)
                        && !SymbolEqualityComparer.Default.Equals(
                            implementation.OriginalDefinition, methodSymbol.OriginalDefinition)))
                {
                    continue;
                }

                mappedName = context.TypeMappings.MapMethod(
                    iface.ConstructedFrom.ToDisplayString(), originalName, paramCount)
                    ?? context.TypeMappings.MapMethod(
                        $"{iface.ContainingNamespace}.{iface.Name}", originalName, paramCount);

                if (mappedName != null)
                    break;
            }
        }

        // J6: dotted mapped names need an explicit FQN import (single-file pipeline
        // and non-java.util helpers). Centralized here so all four render paths
        // (IR/text × member-access/identifier) get it.
        if (mappedName != null)
        {
            AddImportForMappedName(mappedName, context);

            // JavaLibrary auto-deduction (Pascal→camel) can resolve a method
            // whose containing type has an explicit type mapping (e.g.
            // CharUnicodeInfo.GetUnicodeCategory). The type mapping's imports
            // are otherwise only added at type-reference positions.
            var ownerType = methodSymbol.ContainingType;
            var typeKey = context.TypeMappings.HasTypeMapping(ownerType.ToDisplayString())
                ? ownerType.ToDisplayString()
                : context.TypeMappings.HasTypeMapping(
                    $"{ownerType.ContainingNamespace}.{ownerType.Name}")
                    ? $"{ownerType.ContainingNamespace}.{ownerType.Name}"
                    : null;
            if (typeKey != null)
            {
                foreach (var import in context.TypeMappings.GetRequiredImports(typeKey))
                    context.AddImport(import);
            }
        }

        return (mappedName, methodSymbol.IsStatic);
    }

    /// <summary>
    /// True when the method has the same arity and parameter types as the named
    /// interface member. Prevents Object.Equals(object) from being remapped via
    /// an IEquatable&lt;T&gt;.Equals(T) entry.
    /// </summary>
    private static bool MethodSignatureMatchesInterface(IMethodSymbol method, INamedTypeSymbol iface, string methodName)
    {
        var interfaceMethod = iface.GetMembers(methodName).OfType<IMethodSymbol>().FirstOrDefault();
        if (interfaceMethod == null || method.Parameters.Length != interfaceMethod.Parameters.Length)
            return false;

        for (var i = 0; i < method.Parameters.Length; i++)
        {
            if (!SymbolEqualityComparer.Default.Equals(method.Parameters[i].Type, interfaceMethod.Parameters[i].Type))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Converts a C# PascalCase method name to Java camelCase, escaping Java keywords.
    /// </summary>
    private static string ToCamelCase(string name)
    {
        if (name.Length == 0)
            return name;

        var camelCase = char.ToLowerInvariant(name[0]) + name[1..];
        return ConversionContext.EscapeJavaKeyword(camelCase);
    }

    /// <summary>
    /// Mirrors MethodTransformer.GetJavaMethodName: member-desugar (C11) accessor
    /// methods get_X/set_X render as X on the Java side, so call sites must strip
    /// exactly as method declarations do. Any other name gets ordinary camel-casing.
    /// Mapped names (e.g. ByRef.get_Value → getValue) are resolved by the caller
    /// before this and never reach here.
    /// </summary>
    private static string AccessorOrCamelName(string name)
    {
        if (name.StartsWith("get_") || name.StartsWith("set_"))
            return ConversionContext.EscapeJavaKeyword(char.ToUpper(name[4]) + name[5..]);
        return ToCamelCase(name);
    }

    /// <summary>
    /// Strips generic type arguments from a type name (e.g. List&lt;T&gt; → List).
    /// </summary>
    private static string StripTypeArgs(string typeName)
    {
        if (!typeName.Contains('<', StringComparison.Ordinal))
            return typeName;

        var builder = new StringBuilder(typeName.Length);
        var depth = 0;

        foreach (var ch in typeName)
        {
            if (ch == '<')
            {
                depth++;
                continue;
            }

            if (ch == '>')
            {
                depth--;
                continue;
            }

            if (depth == 0)
                builder.Append(ch);
        }

        return depth == 0 ? builder.ToString() : typeName;
    }
}
