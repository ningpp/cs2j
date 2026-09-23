using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

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

    /// <inheritdoc />
    public JavaExpression TransformToIR(ExpressionSyntax node, ConversionContext context)
    {
        if (node is not InvocationExpressionSyntax invocation)
            return new JavaRawExpression(Transform(node, context));

        var facade = ExpressionTransformerFacade.Instance;
        var methodCall = new JavaMethodCallExpression();

        switch (invocation.Expression)
        {
            // abc.CallMethod(arg1, arg2) → abc.callMethod(arg1, arg2)
            case MemberAccessExpressionSyntax memberAccess:
                methodCall.Target = facade.TransformToIR(memberAccess.Expression, context);
                methodCall.MethodName = ToCamelCase(memberAccess.Name.Identifier.Text);
                break;

            // CallMethod(arg1, arg2) → callMethod(arg1, arg2)
            case IdentifierNameSyntax identifier:
                methodCall.MethodName = ToCamelCase(identifier.Identifier.Text);
                break;

            // Other invocation shapes are not part of the minimal supported set.
            default:
                return new JavaRawExpression(Transform(invocation, context));
        }

        foreach (var argument in invocation.ArgumentList.Arguments)
            methodCall.Arguments.Add(facade.TransformToIR(argument.Expression, context));

        return methodCall;
    }

    private string TransformInvocation(InvocationExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;
        var arguments = string.Join(", ",
            node.ArgumentList.Arguments.Select(a => facade.Transform(a.Expression, context)));

        switch (node.Expression)
        {
            // abc.CallMethod(arg1, arg2) → abc.callMethod(arg1, arg2)
            case MemberAccessExpressionSyntax memberAccess:
            {
                var receiver = facade.Transform(memberAccess.Expression, context);
                var methodName = ToCamelCase(memberAccess.Name.Identifier.Text);
                return $"{receiver}.{methodName}({arguments})";
            }

            // CallMethod(arg1, arg2) → callMethod(arg1, arg2)
            case IdentifierNameSyntax identifier:
                return $"{ToCamelCase(identifier.Identifier.Text)}({arguments})";

            // Fallback: transform the callee expression as-is and append the arguments.
            default:
                return $"{facade.Transform(node.Expression, context)}({arguments})";
        }
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
