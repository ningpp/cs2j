using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles method invocation expressions.
/// </summary>
[TransformerRegistration]
public class InvocationExpressionTransformer : IExpressionTransformer
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
        => node.Kind() switch
        {
            SyntaxKind.InvocationExpression => TransformInvocation((InvocationExpressionSyntax)node, context),
            _ => throw new NotSupportedException($"Invocation expression kind {node.Kind()} not supported.")
        };

    private string TransformInvocation(InvocationExpressionSyntax node, ConversionContext context)
    {
        var facade = ExpressionTransformerFacade.Instance;

        // Issue 6 — nameof(x) → "x" string literal; nameof(List<int>) → "List"
        if (node.Expression is IdentifierNameSyntax { Identifier.Text: "nameof" } &&
            node.ArgumentList.Arguments.Count == 1)
        {
            return TransformNameof(node.ArgumentList.Arguments[0].Expression);
        }

        // Issue 1 & 5: member-access invocations need method-name mapping and
        // extension-receiver double-insertion guarding.
        if (node.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            return TransformMemberInvocation(node, memberAccess, context, facade);
        }

        var target = facade.Transform(node.Expression, context);
        var args = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade);
        return $"{target}({args})";
    }

    /// <summary>
    /// Issue 6: nameof(expr) → Java string literal with the last dotted segment.
    /// Strips generic type arguments so nameof(List&lt;int&gt;) → "List".
    /// </summary>
    private static string TransformNameof(ExpressionSyntax argument)
    {
        var text = argument.ToString();
        var last = text.Split('.').Last();
        // Issue 6 fix: strip generic type arguments from the last segment
        var angleBracketIdx = last.IndexOf('<');
        if (angleBracketIdx >= 0)
            last = last[..angleBracketIdx];
        return $"\"{last}\"";
    }

    /// <summary>
    /// Issue 1: Applies method-name mapping from TypeMappings at the call site.
    /// Issue 5: Guards against double-insertion of the extension method receiver in the static-call path.
    /// Issue 4: Delegates to ArgumentTransformer's indexed-loop implementation (no O(n²) IndexOf).
    /// </summary>
    private static string TransformMemberInvocation(
        InvocationExpressionSyntax node,
        MemberAccessExpressionSyntax memberAccess,
        ConversionContext context,
        ExpressionTransformerFacade facade)
    {
        var receiver = facade.Transform(memberAccess.Expression, context);
        var originalMethodName = memberAccess.Name.Identifier.Text;

        IMethodSymbol? methodSymbol = null;
        bool isExtensionInStaticPath = false;

        if (context.SemanticModel != null)
        {
            var symbolInfo = context.SemanticModel.GetSymbolInfo(node);
            methodSymbol = symbolInfo.Symbol as IMethodSymbol;

            // Issue 5: detect reduced extension method; set isExtensionInStaticPath = true
            // when promoting to a static call so the receiver is not double-passed as arg[0].
            // Currently instance-call form is kept, so isExtensionInStaticPath stays false.
            if (methodSymbol is { IsExtensionMethod: true, MethodKind: MethodKind.ReducedExtension })
                isExtensionInStaticPath = false;
        }

        // Issue 1: apply method-name mapping from the type-mapping registry.
        string methodName = originalMethodName;
        if (methodSymbol != null)
        {
            var receiverTypeName = methodSymbol.ContainingType.ToDisplayString();
            var mapped = context.TypeMappings.MapMethod(receiverTypeName, originalMethodName);
            if (mapped != null)
                methodName = mapped;
        }

        methodName = ConversionContext.EscapeJavaKeyword(methodName);

        // Issue 5: when promoting to static-call form, start at index 1 to skip the receiver
        // that was already prepended; use 0 for standard instance calls.
        int argStartIndex = isExtensionInStaticPath ? 1 : 0;
        var args = ArgumentTransformer.TransformArgumentList(node.ArgumentList, context, facade, argStartIndex);

        return $"{receiver}.{methodName}({args})";
    }
}
