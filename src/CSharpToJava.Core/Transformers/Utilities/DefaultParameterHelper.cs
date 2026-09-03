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

        // Generic methods have runtime Class<T> parameters inserted at the front of the
        // Java signature (e.g. "Class<T> _cs2j_T"). These synthetic parameters must be
        // preserved in every overload and forwarded to the full method.
        int leadingSyntheticCount = CountLeadingSyntheticClassParameters(fullMethod.Parameters);

        // Generic methods may also have trailing Class<?> parameters (e.g. "tValueClass")
        // appended by AddRuntimeClassParametersForTypeParameterArrays. These are NOT in the
        // C# parameter list (allParams), so the call-args loop above would miss them.
        // We need to forward null for each trailing synthetic parameter.
        int trailingSyntheticCount = CountTrailingSyntheticClassParameters(fullMethod.Parameters, allParams.Count + leadingSyntheticCount + javaParamOffset);

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
                    // Cast the null default to the parameter's Java type so overload
                    // resolution is unambiguous when sibling optional-parameter
                    // overloads differ in type (e.g. (String, JsonSerializerOptions)
                    // vs (String, Class) both accept a bare null).
                    var defaultValueExpr = allParams[i].Default?.Value;
                    var isNullLiteral = defaultValueExpr is Microsoft.CodeAnalysis.CSharp.Syntax.LiteralExpressionSyntax litExpr
                        && litExpr.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.NullLiteralExpression);
                    var defaultVal = isNullLiteral
                        ? $"({context.MapTypeFromSyntax(allParams[i].Type)}) null"
                        : exprXf.Transform(defaultValueExpr!, context);
                    callArgs.Add(defaultVal);                }
            }
            // Forward trailing synthetic Class<?> parameters (e.g. tValueClass) as null.
            // These are appended after the C# parameters in the Java signature and are
            // not part of allParams, so the loop above doesn't cover them.
            for (int t = 0; t < trailingSyntheticCount; t++)
            {
                int paramIdx = fullMethod.Parameters.Count - trailingSyntheticCount + t;
                var trailingParam = fullMethod.Parameters[paramIdx];
                callArgs.Add($"({trailingParam.Type}) null");
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

    private static int CountLeadingSyntheticClassParameters(IList<JavaParameter> parameters)
    {
        int count = 0;
        while (count < parameters.Count
            && parameters[count].Name.StartsWith("_cs2j_", StringComparison.Ordinal)
            && (parameters[count].Type.StartsWith("Class<", StringComparison.Ordinal)
                || parameters[count].Type.Contains("TypeReference<", StringComparison.Ordinal)))
        {
            count++;
        }
        return count;
    }

    /// <summary>
    /// Counts trailing synthetic Class<?> parameters that were appended after the
    /// C# parameter list (e.g. "Class&lt;?&gt; tValueClass" added by
    /// AddRuntimeClassParametersForTypeParameterArrays). The expectedCSharpParamCount
    /// is the number of Java parameters that correspond to C# parameters (leading
    /// synthetic + allParams - offset). Any parameters beyond that are trailing
    /// synthetic ones that need null forwarding in overload call sites.
    /// </summary>
    private static int CountTrailingSyntheticClassParameters(
        IList<JavaParameter> parameters,
        int expectedCSharpParamCount)
    {
        int trailing = parameters.Count - expectedCSharpParamCount;
        if (trailing <= 0)
            return 0;

        // Verify that the trailing parameters are indeed synthetic Class<?>/TypeReference
        // parameters (not regular C# parameters that happen to be at the end).
        int count = 0;
        for (int i = parameters.Count - trailing; i < parameters.Count; i++)
        {
            if (i < 0 || i >= parameters.Count)
                break;
            var p = parameters[i];
            // Trailing synthetic parameters have names like "tValueClass" (NOT prefixed
            // with _cs2j_) and type "Class<?>" or "Class<T>". They are appended by
            // AddRuntimeClassParametersForTypeParameterArrays.
            if (p.Type.StartsWith("Class<", StringComparison.Ordinal))
                count++;
            else
                break;
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
