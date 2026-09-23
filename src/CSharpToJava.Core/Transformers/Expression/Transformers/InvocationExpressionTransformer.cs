using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Expression.Utilities;

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
        var arguments = invocation.ArgumentList.Arguments
            .Select(a => facade.TransformToIR(a.Expression, context))
            .ToList();

        switch (invocation.Expression)
        {
            // abc.CallMethod(arg1, arg2) → abc.callMethod(arg1, arg2)
            case MemberAccessExpressionSyntax memberAccess:
            {
                var receiver = facade.TransformToIR(memberAccess.Expression, context);
                var (mappedName, isStatic) =
                    TryResolveMappedName(invocation, memberAccess.Name.Identifier.Text, context);

                var call = new JavaMethodCallExpression { Target = receiver };

                if (mappedName != null)
                {
                    ExpressionTransformerHelpers.AddImportForMappedHelperMethod(mappedName, context);
                    ApplyMappedName(call, mappedName, isStatic, receiver);
                }
                else
                {
                    call.MethodName = ToCamelCase(memberAccess.Name.Identifier.Text);
                }

                foreach (var argument in arguments)
                    call.Arguments.Add(argument);

                return call;
            }

            // CallMethod(arg1, arg2) → callMethod(arg1, arg2)
            case IdentifierNameSyntax identifier:
            {
                var (mappedName, _) = TryResolveMappedName(invocation, identifier.Identifier.Text, context);
                if (mappedName != null)
                    ExpressionTransformerHelpers.AddImportForMappedHelperMethod(mappedName, context);

                var call = new JavaMethodCallExpression
                {
                    MethodName = mappedName ?? ToCamelCase(identifier.Identifier.Text)
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

    private string TransformInvocation(InvocationExpressionSyntax node, ConversionContext context)
    {
        // nameof(X) → "argumentName"
        if (IsNameOfInvocation(node, out var nameofArgument))
        {
            var name = ExtractNameOfArgument(nameofArgument!);
            return "\"" + JavaEscapeString(name) + "\"";
        }

        var facade = ExpressionTransformerFacade.Instance;
        var arguments = string.Join(", ",
            node.ArgumentList.Arguments.Select(a => facade.Transform(a.Expression, context)));

        switch (node.Expression)
        {
            // abc.CallMethod(arg1, arg2) → abc.callMethod(arg1, arg2)
            case MemberAccessExpressionSyntax memberAccess:
            {
                var receiver = facade.Transform(memberAccess.Expression, context);
                var originalName = memberAccess.Name.Identifier.Text;
                var (mappedName, isStatic) = TryResolveMappedName(node, originalName, context);

                if (mappedName != null)
                {
                    ExpressionTransformerHelpers.AddImportForMappedHelperMethod(mappedName, context);
                    return RenderMappedCall(mappedName, isStatic, receiver, arguments);
                }

                return $"{receiver}.{ToCamelCase(originalName)}({arguments})";
            }

            // CallMethod(arg1, arg2) → callMethod(arg1, arg2)
            case IdentifierNameSyntax identifier:
            {
                var originalName = identifier.Identifier.Text;
                var (mappedName, _) = TryResolveMappedName(node, originalName, context);
                if (mappedName != null)
                    ExpressionTransformerHelpers.AddImportForMappedHelperMethod(mappedName, context);

                var methodName = mappedName ?? ToCamelCase(originalName);
                return $"{methodName}({arguments})";
            }

            // Fallback: transform the callee expression as-is and append the arguments.
            default:
                return $"{facade.Transform(node.Expression, context)}({arguments})";
        }
    }

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
                return $"{mappedName}({arguments})";
            return string.IsNullOrEmpty(arguments)
                ? $"{mappedName}({receiver})"
                : $"{mappedName}({receiver}, {arguments})";
        }

        return $"{receiver}.{mappedName}({arguments})";
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
}
