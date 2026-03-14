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
        var javaType = typeInfo.HasValue && typeInfo.Value.Type != null
            ? context.MapType(typeInfo.Value.Type)
            : context.MapTypeFromSyntax(fieldDecl.Declaration.Type);
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
                // Java cannot auto-box int to Double/Float (only int→Integer is supported).
                // When a boxed Double/Float field is initialized with an int literal, widen it.
                if (javaType == "Double" && IsIntegerLiteralString(javaField.Initializer))
                    javaField.Initializer += ".0";
                else if (javaType == "Float" && IsIntegerLiteralString(javaField.Initializer))
                    javaField.Initializer += "f";
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

    /// <summary>
    /// Returns true when <paramref name="s"/> is a bare integer literal string (possibly negative),
    /// e.g. "0", "1", "-1", "42". Used to detect int literals that need widening to Double/Float.
    /// </summary>
    private static bool IsIntegerLiteralString(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (s.StartsWith("-") || s.StartsWith("+")) s = s.Substring(1).Trim();
        return s.Length > 0 && s.All(char.IsDigit);
    }
}
