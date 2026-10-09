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

    public string Transform(ExpressionSyntax node, ConversionContext context) => node.Kind() switch
    {
        SyntaxKind.InvocationExpression => TransformInvocation((InvocationExpressionSyntax)node, context),
        _ => throw new NotSupportedException($"Invocation expression kind {node.Kind()} not supported.")
    };

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
        if (node is not InvocationExpressionSyntax invocation)
            return new JavaRawExpression(Transform(node, context));

        // nameof(X) → Java string literal "argumentName"
        if (IsNameOfInvocation(invocation, out var nameofArgument))
        {
            var name = ExtractNameOfArgument(nameofArgument!);
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
                    text = ApplyIntegralNarrowing(text, a.Expression, invocationMethod, index, context);
                    return new JavaRawExpression(text);
                })
                .ToList();
        }

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
                var receiver = TransformReceiverToIR(
                    UnwrapExplicitDisposeCast(memberAccess, context), context);
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

                var methodSymbol = context.GetSymbolInfo(invocation).Symbol as IMethodSymbol;

                var (mappedName, isStatic) =
                    TryResolveMappedName(invocation, memberAccess.Name.Identifier.Text, context);

                var call = new JavaMethodCallExpression { Target = receiver };

                if (mappedName != null)
                {
                    ApplyMappedName(call, mappedName, isStatic, receiver);

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

                call.MethodName = WellKnownMethodName(memberAccess.Name.Identifier.Text);

                foreach (var argument in arguments)
                    call.Arguments.Add(argument);

                return call;
            }

            // CallMethod(arg1, arg2) → callMethod(arg1, arg2)
            case IdentifierNameSyntax identifier:
            {
                var (mappedName, _) = TryResolveMappedName(invocation, identifier.Identifier.Text, context);

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

                var call = new JavaMethodCallExpression
                {
                    MethodName = mappedName ?? ToCamelCase(genericMethod.Identifier.Text)
                };
                foreach (var argument in arguments)
                    call.Arguments.Add(argument);
                return call;
            }

            // Other invocation shapes are not part of the minimal supported set.
            default:
                return new JavaRawExpression(Transform(invocation, context));
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
        if (invocation.Expression is MemberAccessExpressionSyntax
            {
                Name: IdentifierNameSyntax invokedName
            } && invokedName.Identifier.Text == "Invoke")
        {
            return false;
        }
        if (invocation.Expression is not (IdentifierNameSyntax or MemberAccessExpressionSyntax))
            return false;

        var sam = DelegateTransformer.InferSamMethodName(
            methodSymbol.ReturnsVoid, methodSymbol.Parameters.Length);
        var receiver = ExpressionTransformerFacade.Instance.Transform(invocation.Expression, context);
        rendered = $"{receiver}.{sam}({argumentText})";
        return true;
    }

    private string TransformInvocation(InvocationExpressionSyntax node, ConversionContext context)
    {
        // nameof(X) → "argumentName"
        if (IsNameOfInvocation(node, out var nameofArgument))
        {
            var name = ExtractNameOfArgument(nameofArgument!);
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
                    return ApplyIntegralNarrowing(
                        text, a.Expression, context.GetSymbolInfo(node).Symbol as IMethodSymbol, index, context);
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

                var receiver = TransformReceiver(
                    UnwrapExplicitDisposeCast(memberAccess, context), context);

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

                var (mappedName, isStatic) = TryResolveMappedName(node, originalName, context);

                if (mappedName != null)
                {
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

                return $"{receiver}.{WellKnownMethodName(originalName)}({arguments})";
            }

            // CallMethod(arg1, arg2) → callMethod(arg1, arg2)
            case IdentifierNameSyntax identifier:
            {
                var originalName = identifier.Identifier.Text;
                var (mappedName, _) = TryResolveMappedName(node, originalName, context);

                var methodName = mappedName ?? AccessorOrCamelName(originalName);
                return $"{methodName}({arguments})";
            }

            // GenericMethod<T>(arg) → genericMethod(arg): Java erases method
            // type arguments at the call site.
            case GenericNameSyntax genericMethod:
            {
                var originalName = genericMethod.Identifier.Text;
                var (mappedName, _) = TryResolveMappedName(node, originalName, context);

                var methodName = mappedName ?? AccessorOrCamelName(originalName);
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
        SpecialType.System_UInt16 => "Short",
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

        if (originalMethodName == "ToString" && method.Parameters.Length == 0)
        {
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
    /// Simple-prefix → FQN import table for dotted mapped names rendered by
    /// <see cref="ApplyMappedName"/>/<see cref="RenderMappedCall"/> (e.g. an
    /// invocation mapped to "Objects.equals" needs <c>import java.util.Objects;</c>).
    /// <para>
    /// The project pipeline already emits <c>java.util.*</c> wildcard imports, but
    /// <c>ObjectHelper</c>/<c>DelegateHelper</c>/<c>StringHelper</c> are not covered
    /// by that wildcard, and the single-file pipeline has no wildcard at all — both
    /// pipelines rely on this rule for explicit helper imports. Prefixes absent from
    /// this table keep their historical behavior (no import added here).
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> DottedNameImports =
        new(StringComparer.Ordinal)
        {
            ["Objects"] = "java.util.Objects",
            ["ObjectHelper"] = "io.github.ningpp.compat.ObjectHelper",
            ["DelegateHelper"] = "io.github.ningpp.compat.DelegateHelper",
            ["Environment"] = "io.github.ningpp.compat.Environment",
            ["StringHelper"] = "io.github.ningpp.compat.StringHelper",
            ["MathHelper"] = "io.github.ningpp.compat.MathHelper",
            ["MemoryExtensions"] = "io.github.ningpp.compat.MemoryExtensions",
            ["AsyncTasks"] = "io.github.ningpp.compat.AsyncTasks",
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
        if (mappedName == null && containingType is INamedTypeSymbol namedType)
        {
            foreach (var iface in namedType.AllInterfaces)
            {
                if (!MethodSignatureMatchesInterface(methodSymbol, iface, originalName))
                    continue;

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
            AddImportForMappedName(mappedName, context);

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
