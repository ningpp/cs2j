using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.RegularExpressions;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.Transformers.Member;

/// <summary>
/// Converts C# operator declarations (OperatorDeclarationSyntax) to Java static methods.
/// e.g. "public static Point operator+(Point a, Point b)" → "public static Point add(Point a, Point b)"
/// </summary>
public class OperatorTransformer
{
    public JavaMethodDeclaration? Transform(OperatorDeclarationSyntax opDecl, ConversionContext context)
    {
        var paramCount = opDecl.ParameterList.Parameters.Count;
        var opToken = opDecl.OperatorToken.Text;
        var javaName = ConvertOperatorTokenToJavaName(opToken, paramCount);

        // Get return type
        string returnType;
        var retTypeInfo = context.SemanticModel?.GetTypeInfo(opDecl.ReturnType);
        if (retTypeInfo.HasValue && retTypeInfo.Value.Type != null)
            returnType = context.MapType(retTypeInfo.Value.Type);
        else
            returnType = "Object";

        // Build Java method
        var javaMethod = new JavaMethodDeclaration
        {
            Name = javaName,
            Modifiers = JavaModifiers.Public | JavaModifiers.Static,
            ReturnType = returnType
        };

        // Parameters
        var methodTransformer = new MethodTransformer();
        foreach (var param in opDecl.ParameterList.Parameters)
        {
            var javaParam = ConvertParameter(param, context);
            if (javaParam != null)
                javaMethod.Parameters.Add(javaParam);
        }

        // Body
        if (opDecl.Body != null)
        {
            var stmtTransformer = new Transformers.Statement.StatementTransformer();
            javaMethod.Body = stmtTransformer.TransformBlock(opDecl.Body, context);
        }
        else if (opDecl.ExpressionBody != null)
        {
            var exprTransformer = new Transformers.Expression.ExpressionTransformer();
            javaMethod.Body = exprTransformer.Transform(opDecl.ExpressionBody.Expression, context);
            javaMethod.IsBodyExpression = true;
        }
        else
        {
            javaMethod.Body = null; // abstract / extern
        }

        // Java rule: static methods cannot reference the enclosing class's type parameters.
        // Promote class-level type params used in the signature to method-level type params.
        if (context.CurrentType?.TypeParameters.Count > 0)
        {
            var methodOwnTypeParamNames = javaMethod.TypeParameters.Select(tp => tp.Name).ToHashSet(StringComparer.Ordinal);
            var signatureText = javaMethod.ReturnType + " " +
                string.Join(" ", javaMethod.Parameters.Select(p => p.Type));
            var toAdd = new List<JavaTypeParameter>();
            foreach (var classParam in context.CurrentType.TypeParameters)
            {
                if (!methodOwnTypeParamNames.Contains(classParam.Name) &&
                    System.Text.RegularExpressions.Regex.IsMatch(signatureText,
                        $@"\b{System.Text.RegularExpressions.Regex.Escape(classParam.Name)}\b"))
                {
                    toAdd.Add(classParam);
                }
            }
            for (int i = toAdd.Count - 1; i >= 0; i--)
                javaMethod.TypeParameters.Insert(0, toAdd[i]);
        }

        return javaMethod;
    }

    private JavaParameter? ConvertParameter(ParameterSyntax param, ConversionContext context)
    {
        var typeInfo = context.SemanticModel?.GetTypeInfo(param.Type!);
        var javaType = typeInfo.HasValue && typeInfo.Value.Type != null
            ? context.MapType(typeInfo.Value.Type)
            : "Object";
        var paramName = ConversionContext.EscapeJavaKeyword(param.Identifier.Text);
        return new JavaParameter(javaType, paramName);
    }

    private static string ConvertOperatorTokenToJavaName(string opToken, int paramCount)
    {
        // Unary vs binary disambiguation for - and +
        if (opToken == "-" && paramCount == 1) return "negate";
        if (opToken == "+" && paramCount == 1) return "plus";

        return opToken switch
        {
            "+" => "add",
            "-" => "subtract",
            "*" => "multiply",
            "/" => "divide",
            "%" => "mod",
            "==" => "equals",
            "!=" => "notEquals",
            ">" => "greaterThan",
            "<" => "lessThan",
            ">=" => "greaterThanOrEqual",
            "<=" => "lessThanOrEqual",
            "&" => "and",
            "|" => "or",
            "^" => "xor",
            "!" => "not",
            "~" => "onesComplement",
            "++" => "increment",
            "--" => "decrement",
            "true" => "isTrue",
            "false" => "isFalse",
            ">>" => "rightShift",
            "<<" => "leftShift",
            _ => opToken
        };
    }
}
