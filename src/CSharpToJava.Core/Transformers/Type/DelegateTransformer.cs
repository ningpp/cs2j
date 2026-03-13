using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.Transformers.Type;

/// <summary>
/// Delegate transformer - converts C# delegate declarations to Java @FunctionalInterface interfaces.
/// Example:  delegate void ShowGraph(GeometryGraph g);
/// Becomes:  @FunctionalInterface public interface ShowGraph { void invoke(GeometryGraph g); }
/// </summary>
public class DelegateTransformer
{
    public JavaTypeDeclaration? TransformDelegate(DelegateDeclarationSyntax node, ConversionContext context)
    {
        var javaInterface = new JavaInterfaceDeclaration
        {
            Name = node.Identifier.Text,
            Modifiers = ConvertModifiers(node.Modifiers),
        };

        // Mark as @FunctionalInterface
        javaInterface.Annotations.Add(new JavaAnnotation("FunctionalInterface"));

        // Handle type parameters (generic delegates)
        foreach (var typeParam in node.TypeParameterList?.Parameters ?? Enumerable.Empty<TypeParameterSyntax>())
        {
            var jtp = new JavaTypeParameter(typeParam.Identifier.Text);
            javaInterface.TypeParameters.Add(jtp);
        }

        // Propagate type parameter constraints
        ApplyTypeParameterConstraints(node, javaInterface, context);

        // Build the single abstract method "invoke"
        var invokeMethod = new JavaMethodDeclaration
        {
            Name = "invoke",
            Modifiers = JavaModifiers.None, // interface methods are implicitly public abstract
            ReturnType = GetReturnType(node, context),
        };

        foreach (var param in node.ParameterList?.Parameters ?? Enumerable.Empty<ParameterSyntax>())
        {
            var typeInfo = context.SemanticModel?.GetTypeInfo(param.Type!);
            var javaType = typeInfo.HasValue && typeInfo.Value.Type != null
                ? context.MapType(typeInfo.Value.Type)
                : "Object";
            var paramName = ConversionContext.EscapeJavaKeyword(param.Identifier.Text);

            // Handle ref/out by wrapping with holder type
            foreach (var mod in param.Modifiers)
            {
                if (mod.IsKind(SyntaxKind.RefKeyword) || mod.IsKind(SyntaxKind.OutKeyword))
                {
                    javaType = GetHolderType(javaType);
                    break;
                }
                if (mod.IsKind(SyntaxKind.ParamsKeyword))
                {
                    // varargs - keep type but add ... indicator via IsVarArgs
                    var jp2 = new JavaParameter(javaType, paramName) { IsVarArgs = true };
                    invokeMethod.Parameters.Add(jp2);
                    goto nextParam;
                }
            }

            invokeMethod.Parameters.Add(new JavaParameter(javaType, paramName));
            nextParam:;
        }

        javaInterface.Methods.Add(invokeMethod);

        return javaInterface;
    }

    private string GetReturnType(DelegateDeclarationSyntax node, ConversionContext context)
    {
        if (node.ReturnType is PredefinedTypeSyntax predefined &&
            predefined.Keyword.IsKind(SyntaxKind.VoidKeyword))
        {
            return "void";
        }
        var typeInfo = context.SemanticModel?.GetTypeInfo(node.ReturnType);
        if (typeInfo.HasValue && typeInfo.Value.Type != null)
        {
            return context.MapType(typeInfo.Value.Type);
        }
        return "Object";
    }

    private JavaModifiers ConvertModifiers(SyntaxTokenList modifiers)
    {
        JavaModifiers result = JavaModifiers.None;
        foreach (var mod in modifiers)
        {
            result |= mod.Kind() switch
            {
                SyntaxKind.PublicKeyword => JavaModifiers.Public,
                SyntaxKind.InternalKeyword => JavaModifiers.Public,
                SyntaxKind.ProtectedKeyword => JavaModifiers.Protected,
                SyntaxKind.PrivateKeyword => JavaModifiers.Private,
                _ => JavaModifiers.None
            };
        }
        return result;
    }

    private void ApplyTypeParameterConstraints(DelegateDeclarationSyntax node, JavaInterfaceDeclaration javaInterface, ConversionContext context)
    {
        if (node.ConstraintClauses.Count == 0) return;

        foreach (var clause in node.ConstraintClauses)
        {
            var paramName = clause.Name.Identifier.Text;
            var jtp = javaInterface.TypeParameters.FirstOrDefault(tp => tp.Name == paramName);
            if (jtp == null) continue;

            foreach (var constraint in clause.Constraints)
            {
                if (constraint is TypeConstraintSyntax typeConstraint)
                {
                    var typeInfo = context.SemanticModel?.GetTypeInfo(typeConstraint.Type);
                    if (typeInfo.HasValue && typeInfo.Value.Type != null)
                    {
                        var bound = context.MapType(typeInfo.Value.Type);
                        if (!string.IsNullOrEmpty(bound) && bound != "Object")
                        {
                            jtp.Bounds.Add(bound);
                        }
                    }
                }
                // ClassConstraint (struct/class) and ConstructorConstraint have no Java equivalent
            }
        }
    }

    internal static string GetHolderType(string javaType)
    {
        return javaType switch
        {
            "int" => "IntHolder",
            "long" => "LongHolder",
            "double" => "DoubleHolder",
            "float" => "FloatHolder",
            "boolean" => "BoolHolder",
            "char" => "CharHolder",
            "short" => "ShortHolder",
            "byte" => "ByteHolder",
            _ => $"ObjectHolder<{javaType}>"
        };
    }
}
