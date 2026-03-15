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
    /// <summary>
    /// Shared map from Roslyn operator method names (IMethodSymbol.Name, e.g. "op_Addition")
    /// to the Java method names emitted by this transformer.
    /// Used by BinaryExpressionTransformer to cross-reference dispatched method names.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> OpSymbolToJavaName =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "op_Addition",            "add" },
            { "op_Subtraction",         "subtract" },
            { "op_Multiply",            "multiply" },
            { "op_Division",            "divide" },
            { "op_Modulus",             "mod" },
            { "op_Equality",            "valueEquals" },
            { "op_Inequality",          "notEquals" },
            { "op_GreaterThan",         "greaterThan" },
            { "op_LessThan",            "lessThan" },
            { "op_GreaterThanOrEqual",  "greaterThanOrEqual" },
            { "op_LessThanOrEqual",     "lessThanOrEqual" },
            { "op_BitwiseAnd",          "and" },
            { "op_BitwiseOr",           "or" },
            { "op_ExclusiveOr",         "xor" },
            { "op_LogicalNot",          "not" },
            { "op_OnesComplement",      "onesComplement" },
            { "op_Increment",           "increment" },
            { "op_Decrement",           "decrement" },
            { "op_True",                "isTrue" },
            { "op_False",               "isFalse" },
            { "op_LeftShift",           "leftShift" },
            { "op_RightShift",          "rightShift" },
            { "op_UnsignedRightShift",  "unsignedRightShift" },
            { "op_UnaryNegation",       "negate" },
            { "op_UnaryPlus",           "plus" },
        };

    public JavaMethodDeclaration? Transform(OperatorDeclarationSyntax opDecl, ConversionContext context)
    {
        var paramCount = opDecl.ParameterList.Parameters.Count;
        var opToken = opDecl.OperatorToken.Text;
        var javaName = ConvertOperatorTokenToJavaName(opToken, paramCount);

        // Suffix for C# 11 checked operator variants to avoid duplicate method names (Issue 4)
        if (opDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.CheckedKeyword)))
            javaName += "Checked";

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
            javaMethod.Body = Transformers.Expression.ExpressionTransformerFacade.Instance.Transform(opDecl.ExpressionBody.Expression, context);
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

        // Verify return type consistency between semantic-model path and syntax-string path (Issue 5)
        System.Diagnostics.Debug.Assert(
            javaMethod.ReturnType == context.MapTypeFromSyntax(opDecl.ReturnType),
            $"Operator return type mismatch: {opDecl}");

        return javaMethod;
    }

    /// <summary>
    /// Converts a C# implicit/explicit conversion operator to a static Java method.
    /// e.g. "public static implicit operator double(Temperature t)" → "public static double toDouble(Temperature t)"
    /// </summary>
    public JavaMethodDeclaration? TransformConversion(ConversionOperatorDeclarationSyntax convDecl, ConversionContext context)
    {
        // Determine target type
        string targetType;
        var retTypeInfo = context.SemanticModel?.GetTypeInfo(convDecl.Type);
        if (retTypeInfo.HasValue && retTypeInfo.Value.Type != null)
            targetType = context.MapType(retTypeInfo.Value.Type);
        else
            targetType = context.MapTypeFromSyntax(convDecl.Type);

        // Build method name: toDouble, toInt, etc.
        string methodName = targetType.Length > 0
            ? $"to{char.ToUpper(targetType[0])}{targetType[1..]}"
            : "toObject";

        var javaMethod = new JavaMethodDeclaration
        {
            Name = methodName,
            Modifiers = JavaModifiers.Public | JavaModifiers.Static,
            ReturnType = targetType,
            LeadingComment = convDecl.ImplicitOrExplicitKeyword.IsKind(SyntaxKind.ExplicitKeyword)
                ? "// C# explicit conversion operator"
                : "// C# implicit conversion operator"
        };

        // Parameters
        foreach (var param in convDecl.ParameterList.Parameters)
        {
            var javaParam = ConvertParameter(param, context);
            if (javaParam != null)
                javaMethod.Parameters.Add(javaParam);
        }

        // Body
        if (convDecl.Body != null)
        {
            var stmtTransformer = new Transformers.Statement.StatementTransformer();
            javaMethod.Body = stmtTransformer.TransformBlock(convDecl.Body, context);
        }
        else if (convDecl.ExpressionBody != null)
        {
            javaMethod.Body = Transformers.Expression.ExpressionTransformerFacade.Instance.Transform(convDecl.ExpressionBody.Expression, context);
            javaMethod.IsBodyExpression = true;
        }

        // Promote class-level type params used in the signature to method-level type params
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
            "==" => "valueEquals",
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
            ">>>" => "unsignedRightShift",
            _ => opToken
        };
    }
}
