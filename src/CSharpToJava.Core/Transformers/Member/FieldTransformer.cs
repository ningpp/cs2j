using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.Transformers.Member;

/// <summary>
/// 字段转换器
/// </summary>
public class FieldTransformer : IMemberTransformer
{
    public JavaSyntaxNode Transform(MemberDeclarationSyntax node, ConversionContext context)
    {
        if (node is not FieldDeclarationSyntax fieldDecl)
        {
            throw new ArgumentException($"Expected FieldDeclarationSyntax, got {node.GetType()}");
        }

        // Use TransformAll and return first result (for interface compatibility)
        var all = TransformAll(fieldDecl, context).ToList();
        return all.Count > 0 ? all[0] : throw new InvalidOperationException("Field declaration has no variables");
    }

    /// <summary>
    /// Transforms all variables in a field declaration (handles multi-variable declarations like: int x, y, z;)
    /// </summary>
    public IEnumerable<JavaFieldDeclaration> TransformAll(FieldDeclarationSyntax fieldDecl, ConversionContext context)
    {
        var typeInfo = context.SemanticModel?.GetTypeInfo(fieldDecl.Declaration.Type);
        var javaType = typeInfo.HasValue && typeInfo.Value.Type != null ? context.MapType(typeInfo.Value.Type) : "Object";
        var modifiers = ConvertModifiers(fieldDecl.Modifiers);

        // Handle const/readonly modifiers
        if (fieldDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword)))
            modifiers |= JavaModifiers.Static | JavaModifiers.Final;
        if (fieldDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword)))
            modifiers |= JavaModifiers.Final;
        if (fieldDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.FixedKeyword)))
            context.Diagnostics.Error("Java doesn't support fixed-size buffers. Field needs manual conversion.", fieldDecl.GetLocation());

        foreach (var variable in fieldDecl.Declaration.Variables)
        {
            var javaField = new JavaFieldDeclaration
            {
                Type = javaType,
                Name = ConversionContext.EscapeJavaKeyword(variable.Identifier.Text),
                Modifiers = modifiers
            };

            if (variable.Initializer != null)
            {
                var exprTransformer = new Transformers.Expression.ExpressionTransformer();
                javaField.Initializer = exprTransformer.Transform(variable.Initializer.Value, context);
            }

            yield return javaField;
        }
    }

    private JavaModifiers ConvertModifiers(SyntaxTokenList modifiers)
    {
        JavaModifiers result = JavaModifiers.None;

        foreach (var modifier in modifiers)
        {
            result |= modifier.Kind() switch
            {
                SyntaxKind.PublicKeyword => JavaModifiers.Public,
                SyntaxKind.ProtectedKeyword => JavaModifiers.Protected,
                SyntaxKind.PrivateKeyword => JavaModifiers.Private,
                SyntaxKind.InternalKeyword => JavaModifiers.Public,
                SyntaxKind.StaticKeyword => JavaModifiers.Static,
                SyntaxKind.ReadOnlyKeyword => JavaModifiers.Final,
                SyntaxKind.ConstKeyword => JavaModifiers.Static | JavaModifiers.Final,
                SyntaxKind.VolatileKeyword => JavaModifiers.Volatile,
                SyntaxKind.UnsafeKeyword => JavaModifiers.None,
                SyntaxKind.NewKeyword => JavaModifiers.None,
                _ => JavaModifiers.None
            };
        }

        // C# "protected internal" maps to Protected | Public via individual keyword rules.
        // Java doesn't allow both; "protected" is the most restrictive useful choice.
        if ((result & JavaModifiers.Protected) != 0 && (result & JavaModifiers.Public) != 0)
            result &= ~JavaModifiers.Public;

        return result;
    }
}
