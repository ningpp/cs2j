using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;

namespace CSharpToJava.Core.Transformers.Type;

/// <summary>
/// Maps a C# enum to a Java enum. Ordinal-aligned default-int enums map to a
/// plain Java enum. Enums with an explicit non-int underlying type or with
/// member constants that do not match their ordinal positions are emitted as a
/// "value enum": each constant carries its C# value and exposes getValue() /
/// fromValue() so enum↔numeric casts (including wrap-around increments and
/// byte/short/long domains) keep C# semantics. [Flags] enums are still lowered
/// to classes by a C#→C# preprocessor and fail fast here.
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

        var javaEnum = new JavaEnumDeclaration
        {
            Name = enumDecl.Identifier.Text,
            Modifiers = ConvertModifiers(enumDecl.Modifiers)
        };
        javaEnum.LeadingComment = context.GetDeclarationComments(enumDecl, enumSymbol).ToCombinedComment();

        var members = GetOrderedMembers(enumSymbol, enumDecl);
        var valueEnum = IsValueEnum(enumSymbol, members);
        var storage = StorageTypeName(enumSymbol?.EnumUnderlyingType?.SpecialType
            ?? SpecialType.System_Int32);

        foreach (var (name, value) in members)
        {
            javaEnum.Values.Add(valueEnum
                ? $"{name}({RenderLiteral(value, storage)})"
                : name);
        }

        if (valueEnum)
            BuildValueBody(javaEnum, storage);

        return javaEnum;
    }

    /// <summary>
    /// Java primitive type used to store a C# enum's underlying numeric domain.
    /// </summary>
    public static string StorageTypeName(SpecialType underlying) => underlying switch
    {
        SpecialType.System_SByte or SpecialType.System_Byte => "byte",
        SpecialType.System_Int16 or SpecialType.System_UInt16 => "short",
        SpecialType.System_Int64 or SpecialType.System_UInt64 => "long",
        SpecialType.System_Char => "char",
        _ => "int"
    };

    /// <summary>
    /// True when the enum cannot rely on Java ordinal()/values()[i] and must be
    /// emitted with an explicit value field (non-int underlying or any member
    /// constant that differs from its declaration index).
    /// </summary>
    public static bool IsValueEnum(INamedTypeSymbol? enumSymbol)
        => IsValueEnum(enumSymbol, GetOrderedMembers(enumSymbol, null));

    private static bool IsValueEnum(
        INamedTypeSymbol? enumSymbol,
        IReadOnlyList<(string Name, object Value)> members)
    {
        if (enumSymbol?.TypeKind != TypeKind.Enum)
            return false;
        var storage = StorageTypeName(enumSymbol.EnumUnderlyingType?.SpecialType
            ?? SpecialType.System_Int32);
        if (storage != "int")
            return true;
        for (var i = 0; i < members.Count; i++)
        {
            if (Convert.ToInt64(members[i].Value) != i)
                return true;
        }
        return false;
    }

    /// <summary>Enum members in source declaration order with constant values.</summary>
    public static IReadOnlyList<(string Name, object Value)> GetOrderedMembers(
        INamedTypeSymbol? enumSymbol,
        EnumDeclarationSyntax? enumDecl)
    {
        var fields = enumSymbol?.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(f => f is { IsConst: true, HasConstantValue: true })
            .ToList();
        if (fields is { Count: > 0 })
        {
            fields.Sort((a, b) =>
                a.DeclaringSyntaxReferences.FirstOrDefault()?.Span.Start
                    .CompareTo(b.DeclaringSyntaxReferences.FirstOrDefault()?.Span.Start) ?? 0);
            return fields
                .Select(f => (f.Name, f.ConstantValue ?? 0))
                .ToList();
        }

        // Semantic model unavailable: syntax-only fallback assumes 0..n-1.
        return (enumDecl?.Members.OfType<EnumMemberDeclarationSyntax>()
                .Select((m, i) => (m.Identifier.Text, (object)i))
                .ToList())
            ?? new List<(string, object)>();
    }

    private static void BuildValueBody(JavaEnumDeclaration javaEnum, string storage)
    {
        var name = javaEnum.Name;
        javaEnum.Fields.Add(new JavaFieldDeclaration
        {
            Modifiers = JavaModifiers.Private | JavaModifiers.Final,
            Type = storage,
            Name = "value"
        });
        javaEnum.Constructors.Add(new JavaConstructorDeclaration
        {
            Modifiers = JavaModifiers.Private,
            ClassName = name,
            Parameters = { new JavaParameter(storage, "value") },
            Body = "this.value = value;"
        });
        javaEnum.Methods.Add(new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ReturnType = storage,
            Name = "getValue",
            Body = "return this.value;"
        });
        javaEnum.Methods.Add(new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public | JavaModifiers.Static,
            ReturnType = name,
            Name = "fromValue",
            Parameters = { new JavaParameter(storage, "value") },
            Body =
                $"for ({name} candidate : values()) {{\n" +
                "    if (candidate.value == value) return candidate;\n" +
                "}\n" +
                $"throw new IllegalArgumentException(\"No enum constant {name} for value \" + value);"
        });
    }

    private static string RenderLiteral(object raw, string storage)
    {
        var signed = Convert.ToInt64(raw);
        switch (storage)
        {
            case "byte":
            case "short":
            case "char":
                return $"({storage}){signed}";
            case "long":
                var unsigned = Convert.ToUInt64(raw);
                return unsigned > long.MaxValue
                    ? $"(long)0x{unsigned:X}L"
                    : $"{(long)unsigned}L";
            default: // int
                return signed is < int.MinValue or > int.MaxValue
                    ? $"(int){signed}L"
                    : signed.ToString();
        }
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
