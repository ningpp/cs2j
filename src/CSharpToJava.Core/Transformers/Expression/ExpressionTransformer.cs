using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.Transformers.Expression;

/// <summary>
/// 表达式转换器
/// [Obsolete] This class now forwards to ExpressionTransformerFacade.
/// The actual implementation has been split into specialized transformer classes.
/// </summary>
[Obsolete("Use ExpressionTransformerFacade.Instance instead.")]
public class ExpressionTransformer : IExpressionTransformer
{
    private readonly ExpressionTransformerFacade _facade = ExpressionTransformerFacade.Instance;

    /// <summary>
    /// Transforms a C# expression to Java code by forwarding to the facade.
    /// </summary>
    public string Transform(ExpressionSyntax node, ConversionContext context)
        => _facade.Transform(node, context);
}
