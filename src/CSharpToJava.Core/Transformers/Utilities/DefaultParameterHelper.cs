using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers.Expression;

namespace CSharpToJava.Core.Transformers.Utilities;

public static class DefaultParameterHelper
{
    /// <summary>
    /// Generates default-parameter overloads for a method.
    /// Returns overloads (full declaration excluded) or empty list.
    /// </summary>
    public static List<JavaSyntaxNode> GenerateMethodOverloads(
        List<ParameterSyntax> allParams,
        JavaMethodDeclaration fullMethod,
        bool isAbstract,
        bool hasStrippedThisParam,
        ConversionContext context,
        ExpressionTransformerFacade exprXf)
    {
        int firstDefaultIdx = allParams.FindIndex(p => p.Default != null);
        if (firstDefaultIdx < 0
            || !allParams.Skip(firstDefaultIdx).All(p => p.Default != null))
        {
            return new List<JavaSyntaxNode>();
        }

        var overloads = new List<JavaSyntaxNode>();
        int javaParamOffset = hasStrippedThisParam ? 1 : 0;

        for (int cutAt = firstDefaultIdx; cutAt < allParams.Count; cutAt++)
        {
            var overload = new JavaMethodDeclaration
            {
                Name = fullMethod.Name,
                Modifiers = fullMethod.Modifiers,
                ReturnType = fullMethod.ReturnType,
                LeadingComment = fullMethod.LeadingComment,
            };
            foreach (var tp in fullMethod.TypeParameters)
                overload.TypeParameters.Add(tp);

            int javaCutAt = cutAt - javaParamOffset;
            for (int j = 0; j < javaCutAt && j < fullMethod.Parameters.Count; j++)
                overload.Parameters.Add(fullMethod.Parameters[j]);

            var callArgs = new List<string>();
            for (int i = 0; i < allParams.Count; i++)
            {
                if (i < cutAt)
                {
                    callArgs.Add(ConversionContext.EscapeJavaKeyword(allParams[i].Identifier.Text));
                }
                else
                {
                    // Cast the null default to the parameter's Java type so overload
                    // resolution is unambiguous when sibling optional-parameter
                    // overloads differ in type.
                    var defaultValueExpr = allParams[i].Default?.Value;
                    var isNullLiteral = defaultValueExpr is LiteralExpressionSyntax litExpr
                        && litExpr.IsKind(SyntaxKind.NullLiteralExpression);
                    var defaultVal = isNullLiteral
                        ? $"({context.MapTypeFromSyntax(allParams[i].Type)}) null"
                        : exprXf.Transform(defaultValueExpr!, context);
                    callArgs.Add(defaultVal);
                }
            }

            // Abstract methods cannot have bodies; the overload is abstract too and
            // relies on the implementing subclass (which gets its own overloads via
            // the override path). Concrete methods forward to the full method.
            if (!isAbstract)
            {
                string callPrefix = fullMethod.ReturnType == "void" ? "" : "return ";
                overload.Body = $"{callPrefix}{fullMethod.Name}({string.Join(", ", callArgs)});";
            }
            overloads.Add(overload);
        }

        return overloads;
    }

    /// <summary>
    /// Generates default-parameter overloads for a constructor.
    /// Returns overloads (full declaration excluded) or empty list.
    /// </summary>
    public static List<JavaSyntaxNode> GenerateConstructorOverloads(
        List<ParameterSyntax> allParams,
        JavaConstructorDeclaration fullCtor,
        ConversionContext context,
        ExpressionTransformerFacade exprXf)
    {
        int firstDefaultIdx = allParams.FindIndex(p => p.Default != null);
        if (firstDefaultIdx < 0
            || !allParams.Skip(firstDefaultIdx).All(p => p.Default != null))
        {
            return new List<JavaSyntaxNode>();
        }

        var overloads = new List<JavaSyntaxNode>();

        for (int cutAt = firstDefaultIdx; cutAt < allParams.Count; cutAt++)
        {
            var overload = new JavaConstructorDeclaration
            {
                ClassName = fullCtor.ClassName,
                Modifiers = fullCtor.Modifiers,
                LeadingComment = fullCtor.LeadingComment,
            };

            for (int j = 0; j < cutAt && j < fullCtor.Parameters.Count; j++)
                overload.Parameters.Add(fullCtor.Parameters[j]);

            var callArgs = new List<string>();
            for (int i = 0; i < allParams.Count; i++)
            {
                if (i < cutAt)
                {
                    callArgs.Add(ConversionContext.EscapeJavaKeyword(allParams[i].Identifier.Text));
                }
                else
                {
                    var defaultVal = allParams[i].Default?.Value != null
                        ? exprXf.Transform(allParams[i].Default!.Value, context)
                        : "null";
                    callArgs.Add(defaultVal);
                }
            }

            overload.Body = $"this({string.Join(", ", callArgs)});";
            overloads.Add(overload);
        }

        return overloads;
    }
}
