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
            || !allParams.Skip(firstDefaultIdx).All(p => p.Default != null)
            || isAbstract)
        {
            return new List<JavaSyntaxNode>();
        }

        var overloads = new List<JavaSyntaxNode>();
        int javaParamOffset = hasStrippedThisParam ? 1 : 0;

        // Generic methods have runtime Class<T> parameters inserted at the front of the
        // Java signature (e.g. "Class<T> _cs2j_T"). These synthetic parameters must be
        // preserved in every overload and forwarded to the full method.
        int leadingSyntheticCount = CountLeadingSyntheticClassParameters(fullMethod.Parameters);

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

            int javaCutAt = leadingSyntheticCount + cutAt - javaParamOffset;
            for (int j = 0; j < javaCutAt && j < fullMethod.Parameters.Count; j++)
                overload.Parameters.Add(fullMethod.Parameters[j]);

            var callArgs = new List<string>();
            for (int s = 0; s < leadingSyntheticCount; s++)
                callArgs.Add(fullMethod.Parameters[s].Name);
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

            string callPrefix = fullMethod.ReturnType == "void" ? "" : "return ";
            overload.Body = $"{callPrefix}{fullMethod.Name}({string.Join(", ", callArgs)});";
            overloads.Add(overload);
        }

        return overloads;
    }

    private static int CountLeadingSyntheticClassParameters(IList<JavaParameter> parameters)
    {
        int count = 0;
        while (count < parameters.Count
            && parameters[count].Name.StartsWith("_cs2j_", StringComparison.Ordinal)
            && parameters[count].Type.StartsWith("Class<", StringComparison.Ordinal))
        {
            count++;
        }
        return count;
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

        // Generic classes may have runtime Class<T> parameters inserted at the front of
        // constructors. Preserve and forward them just like for methods.
        int leadingSyntheticCount = CountLeadingSyntheticClassParameters(fullCtor.Parameters);

        for (int cutAt = firstDefaultIdx; cutAt < allParams.Count; cutAt++)
        {
            var overload = new JavaConstructorDeclaration
            {
                ClassName = fullCtor.ClassName,
                Modifiers = fullCtor.Modifiers,
                LeadingComment = fullCtor.LeadingComment,
            };

            int javaCutAt = leadingSyntheticCount + cutAt;
            for (int j = 0; j < javaCutAt && j < fullCtor.Parameters.Count; j++)
                overload.Parameters.Add(fullCtor.Parameters[j]);

            var callArgs = new List<string>();
            for (int s = 0; s < leadingSyntheticCount; s++)
                callArgs.Add(fullCtor.Parameters[s].Name);
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
