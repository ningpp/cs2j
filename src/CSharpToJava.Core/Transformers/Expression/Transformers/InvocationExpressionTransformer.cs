using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using System.Collections.Generic;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// Handles method invocation expressions.
/// </summary>
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
        var target = facade.Transform(node.Expression, context);

        var args = new List<string>();
        foreach (var arg in node.ArgumentList.Arguments)
        {
            var transformedArg = facade.Transform(arg.Expression, context);

            // Check for ref/out arguments using semantic model
            if (context.SemanticModel != null)
            {
                var argumentList = node.ArgumentList;
                var argumentIndex = argumentList.Arguments.IndexOf(arg);
                var symbolInfo = context.SemanticModel.GetSymbolInfo(node);

                if (symbolInfo.Symbol is IMethodSymbol methodSymbol &&
                    argumentIndex >= 0 && argumentIndex < methodSymbol.Parameters.Length)
                {
                    var param = methodSymbol.Parameters[argumentIndex];
                    if (param.RefKind == RefKind.Ref)
                    {
                        context.Diagnostics.Warning("ref parameter has no direct Java equivalent", arg.GetLocation());
                        transformedArg = $"/* ref */ {transformedArg}";
                    }
                    else if (param.RefKind == RefKind.Out)
                    {
                        context.Diagnostics.Warning("out parameter has no direct Java equivalent", arg.GetLocation());
                        transformedArg = $"/* out */ {transformedArg}";
                    }
                }
            }

            args.Add(transformedArg);
        }

        return $"{target}({string.Join(", ", args)})";
    }
}
