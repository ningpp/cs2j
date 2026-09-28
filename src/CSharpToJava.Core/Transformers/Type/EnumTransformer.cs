using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.Transformers.Type;

/// <summary>
/// Maps a C# enum directly to a plain Java enum: constant names only, in
/// declaration order. [Flags] enums, enums with explicit member values and
/// non-int underlying types must be lowered by a C#→C# preprocessor and fail
/// fast here.
/// </summary>
public class EnumTransformer : ITypeTransformer
{
    public JavaTypeDeclaration Transform(TypeDeclarationSyntax node, ConversionContext context)
    {
        // EnumDeclarationSyntax is not a TypeDeclarationSyntax; enums are always
        // converted through TransformEnum.
        throw new ArgumentException("Use TransformEnum method with EnumDeclarationSyntax");
    }

    public JavaTypeDeclaration TransformEnum(EnumDeclarationSyntax enumDecl, ConversionContext context)
    {
        var enumSymbol = context.GetDeclaredSymbol(enumDecl) as INamedTypeSymbol;

        if (IsFlagsEnum(enumDecl, enumSymbol))
        {
            throw new NotSupportedException(
                $"CS2J normalization required: [Flags] enum '{enumDecl.Identifier.Text}' must be lowered by a preprocessor before Java conversion.");
        }

        if (enumDecl.BaseList != null)
        {
            throw new NotSupportedException(
                $"CS2J normalization required: enum '{enumDecl.Identifier.Text}' declares an explicit underlying type; only default-int enums map directly to Java enums.");
        }

        if (enumDecl.Members.OfType<EnumMemberDeclarationSyntax>().Any(m => m.EqualsValue != null))
        {
            throw new NotSupportedException(
                $"CS2J normalization required: enum '{enumDecl.Identifier.Text}' has members with explicit values; Java enums only carry ordinal values.");
        }

        var javaEnum = new JavaEnumDeclaration
        {
            Name = enumDecl.Identifier.Text,
            Modifiers = ConvertModifiers(enumDecl.Modifiers)
        };
        javaEnum.LeadingComment = context.GetDeclarationComments(enumDecl, enumSymbol).ToCombinedComment();

        foreach (var enumMember in enumDecl.Members.OfType<EnumMemberDeclarationSyntax>())
            javaEnum.Values.Add(enumMember.Identifier.Text);

        return javaEnum;
    }

    private static bool IsFlagsEnum(EnumDeclarationSyntax enumDecl, INamedTypeSymbol? enumSymbol)
    {
        if (enumSymbol?.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString() is "System.FlagsAttribute" or "System.Flags") == true)
        {
            return true;
        }

        return enumDecl.AttributeLists
            .SelectMany(al => al.Attributes)
            .Any(a => a.Name.ToString() is "Flags" or "FlagsAttribute"
                or "System.Flags" or "System.FlagsAttribute");
    }

    private static JavaModifiers ConvertModifiers(SyntaxTokenList modifiers)
    {
        JavaModifiers result = JavaModifiers.None;
        foreach (var modifier in modifiers)
        {
            result |= modifier.Kind() switch
            {
                SyntaxKind.PublicKeyword => JavaModifiers.Public,
                SyntaxKind.PrivateKeyword => JavaModifiers.Private,
                SyntaxKind.InternalKeyword => JavaModifiers.Public,
                SyntaxKind.ProtectedKeyword => JavaModifiers.Protected,
                _ => JavaModifiers.None
            };
        }
        // C# default access for enums is internal/file-scoped; Java defaults to package-private.
        bool hasAccessModifier = modifiers.Any(m =>
            m.IsKind(SyntaxKind.PublicKeyword) ||
            m.IsKind(SyntaxKind.PrivateKeyword) ||
            m.IsKind(SyntaxKind.ProtectedKeyword) ||
            m.IsKind(SyntaxKind.InternalKeyword));
        if (!hasAccessModifier)
            result |= JavaModifiers.Public;
        return result;
    }
}
