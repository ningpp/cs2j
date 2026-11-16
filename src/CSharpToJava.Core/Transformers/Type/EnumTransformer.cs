using Microsoft.CodeAnalysis;
using System.Text;
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
/// byte/short/long domains) keep C# semantics. [Flags] enums have explicit
/// bit values, so they naturally take the value-enum form.
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

        var javaEnum = new JavaEnumDeclaration
        {
            Name = enumDecl.Identifier.Text,
            Modifiers = ConvertModifiers(enumDecl.Modifiers)
        };
        javaEnum.LeadingComment = context.GetDeclarationComments(enumDecl, enumSymbol).ToCombinedComment();

        var members = GetOrderedMembers(enumSymbol, enumDecl);
        var valueEnum = IsValueEnum(enumSymbol, members);
        var underlying = enumSymbol?.EnumUnderlyingType?.SpecialType ?? SpecialType.System_Int32;
        var storage = StorageTypeName(underlying);

        // C# value enums can hold values that are not declared members (flags
        // combinations, (E)0x7FFF casts). A Java enum cannot materialize those,
        // so value enums are emitted as an immutable value class instead.
        if (valueEnum)
            return BuildValueEnumClass(enumDecl, members, storage, underlying, javaEnum.LeadingComment);

        foreach (var (name, _) in members)
            javaEnum.Values.Add(name);

        return javaEnum;
    }

    /// <summary>
    /// Emits a value enum as `final class` with static constants and value
    /// semantics. fromValue returns the canonical constant for declared members
    /// and a fresh instance for any other numeric value, matching C# enum
    /// semantics (including [Flags] combinations).
    /// </summary>
    private static JavaTypeDeclaration BuildValueEnumClass(
        EnumDeclarationSyntax enumDecl,
        IReadOnlyList<(string Name, object Value)> members,
        string storage,
        SpecialType underlying,
        string? leadingComment)
    {
        var name = enumDecl.Identifier.Text;
        var javaClass = new JavaClassDeclaration
        {
            Name = name,
            Modifiers = ConvertModifiers(enumDecl.Modifiers) | JavaModifiers.Final,
            LeadingComment = leadingComment
        };

        javaClass.Fields.Add(new JavaFieldDeclaration
        {
            Modifiers = JavaModifiers.Private | JavaModifiers.Final,
            Type = "String",
            Name = "__cs2jName"
        });
        javaClass.Fields.Add(new JavaFieldDeclaration
        {
            Modifiers = JavaModifiers.Private | JavaModifiers.Final,
            Type = "int",
            Name = "__cs2jOrdinal"
        });
        javaClass.Fields.Add(new JavaFieldDeclaration
        {
            Modifiers = JavaModifiers.Private | JavaModifiers.Final,
            Type = storage,
            Name = "__cs2jValue"
        });

        for (var i = 0; i < members.Count; i++)
        {
            var (memberName, value) = members[i];
            javaClass.Fields.Add(new JavaFieldDeclaration
            {
                Modifiers = JavaModifiers.Public | JavaModifiers.Static | JavaModifiers.Final,
                Type = name,
                Name = memberName,
                Initializer = $"new {name}(\"{memberName}\", {i}, {RenderLiteral(value, storage)})"
            });
        }

        var allValues = string.Join(", ", members.Select(m => m.Name));
        javaClass.Fields.Add(new JavaFieldDeclaration
        {
            Modifiers = JavaModifiers.Private | JavaModifiers.Static | JavaModifiers.Final,
            Type = name + "[]",
            Name = "__cs2jValues",
            Initializer = $"new {name}[] {{ {allValues} }}"
        });

        javaClass.Constructors.Add(new JavaConstructorDeclaration
        {
            Modifiers = JavaModifiers.Private,
            ClassName = name,
            Parameters =
            {
                new JavaParameter("String", "name"),
                new JavaParameter("int", "ordinal"),
                new JavaParameter(storage, "value"),
            },
            Body = "this.__cs2jName = name;\nthis.__cs2jOrdinal = ordinal;\nthis.__cs2jValue = value;"
        });

        javaClass.Methods.Add(new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ReturnType = storage,
            Name = "getValue",
            Body = "return this.__cs2jValue;"
        });
        javaClass.Methods.Add(new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ReturnType = "int",
            Name = "ordinal",
            Body = "return this.__cs2jOrdinal;"
        });
        javaClass.Methods.Add(new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ReturnType = "String",
            Name = "name",
            Body = "return this.__cs2jName;"
        });
        javaClass.Methods.Add(new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ReturnType = "String",
            Name = "toString",
            Body = "return this.__cs2jName;"
        });
        javaClass.Methods.Add(new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public | JavaModifiers.Static,
            ReturnType = "io.github.ningpp.compat.TypeCode",
            Name = "getUnderlyingTypeCode",
            Body = $"return io.github.ningpp.compat.TypeCode.{UnderlyingTypeCodeName(underlying)};"
        });
        javaClass.Methods.Add(new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public | JavaModifiers.Static,
            ReturnType = name + "[]",
            Name = "values",
            Body = "return __cs2jValues.clone();"
        });
        javaClass.Methods.Add(new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public | JavaModifiers.Static,
            ReturnType = name,
            Name = "valueOf",
            Parameters = { new JavaParameter("String", "name") },
            Body =
                $"for ({name} candidate : __cs2jValues)\n" +
                "{\n" +
                "    if (candidate.__cs2jName.equals(name)) return candidate;\n" +
                "}\n" +
                $"throw new IllegalArgumentException(\"No enum constant {name}.\" + name);"
        });

        var fromValue = new StringBuilder();
        foreach (var (memberName, value) in members)
            fromValue.AppendLine($"if (value == {RenderLiteral(value, storage)}) return {memberName};");
        // 值类枚举成员可能含名为 String 的常量（XPathResultType/LexKind）：类内
        // 该静态字段会遮蔽 java.lang.String，`String.valueOf(...)` 会被解析成
        // 字段上的 valueOf，进而把 int 实参塞给 valueOf(String)。全名限定避开遮蔽。
        fromValue.AppendLine(
            $"return new {name}(java.lang.String.valueOf(value), -1, value);");

        javaClass.Methods.Add(new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public | JavaModifiers.Static,
            ReturnType = name,
            Name = "fromValue",
            Parameters = { new JavaParameter(storage, "value") },
            Body = fromValue.ToString().TrimEnd()
        });

        javaClass.Methods.Add(new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ReturnType = "boolean",
            Name = "equals",
            Parameters = { new JavaParameter("Object", "other") },
            Body =
                $"return other instanceof {name} && (({name}) other).__cs2jValue == this.__cs2jValue;"
        });
        javaClass.Methods.Add(new JavaMethodDeclaration
        {
            Modifiers = JavaModifiers.Public,
            ReturnType = "int",
            Name = "hashCode",
            Body = storage == "long"
                ? "return Long.hashCode(this.__cs2jValue);"
                : "return Integer.hashCode((int) this.__cs2jValue);"
        });

        return javaClass;
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
    /// compat TypeCode constant name for a C# enum's underlying type, emitted as
    /// the value-enum class's getUnderlyingTypeCode() marker so Java-side
    /// Type.GetTypeCode() matches C# (byte/sbyte/uint/ulong are otherwise
    /// indistinguishable from their Java storage primitive).
    /// </summary>
    public static string UnderlyingTypeCodeName(SpecialType underlying) => underlying switch
    {
        SpecialType.System_SByte => "SByte",
        SpecialType.System_Byte => "Byte",
        SpecialType.System_Int16 => "Int16",
        SpecialType.System_UInt16 => "UInt16",
        SpecialType.System_Int32 => "Int32",
        SpecialType.System_UInt32 => "UInt32",
        SpecialType.System_Int64 => "Int64",
        SpecialType.System_UInt64 => "UInt64",
        SpecialType.System_Char => "Char",
        _ => "Int32"
    };

    /// <summary>
    /// True when the enum cannot rely on Java ordinal()/values()[i] and must be
    /// emitted with an explicit value field (non-int underlying or any member
    /// constant that differs from its declaration index).
    /// </summary>
    public static bool IsValueEnum(INamedTypeSymbol? enumSymbol)
        => IsValueEnum(enumSymbol, GetOrderedMembers(enumSymbol, null));

    /// <summary>
    /// Metadata flags enums supplied by compat as plain <c>int</c> constants
    /// (BindingFlags/MethodAttributes/...). They have no Java value-class emitted by
    /// this converter, so casts must stay numeric — synthesizing getValue()/fromValue()
    /// is uncompilable (the compat holder exposes int fields, not instances).
    /// </summary>
    private static readonly HashSet<string> IntConstantMetadataEnums = new(StringComparer.Ordinal)
    {
        "System.Reflection.BindingFlags",
        "System.Reflection.MethodAttributes",
        "System.Reflection.FieldAttributes",
        "System.Reflection.PropertyAttributes",
        "System.Reflection.ParameterAttributes",
        "System.Reflection.CallingConventions",
        "System.Reflection.TypeAttributes",
        // 数字/解析面元数据 flags 枚举：compat 以 int 常量提供（NumberStyles 直挂
        // compat.NumberStyles；DateTimeStyles/StringSplitOptions 的常量宿主是
        // compat.IntegerHelper，其常量与 int 常量同层）。它们的成员值非序数对齐
        // （[Flags] 位值），若不列入会按 value enum 发出 getValue()/fromValue()，
        // 而 compat 宿主只有 int 常量 → javac 无法取消引用int / 找不到符号
        // fromValue(int)（真实链 XmlConvert.ToDecimal 等全量 Parse 站点）。
        "System.Globalization.NumberStyles",
        "System.Globalization.DateTimeStyles",
        "System.StringSplitOptions",
        // trimming/反射分析 flags 枚举：compat 以 int 常量提供（见
        // compat/DynamicallyAccessedMemberTypes.java）。成员值非序数对齐，
        // 若不列入会按 value enum 发出 `values()[547]`（真实链 dotnet-text-json
        // JsonSerializer.${MembersAccessedOnRead/Write} 4 处找不到符号 类）。
        "System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes",
    };

    public static bool IsIntConstantMetadataEnum(INamedTypeSymbol? enumSymbol)
        => enumSymbol is { TypeKind: TypeKind.Enum }
           && enumSymbol.DeclaringSyntaxReferences.Length == 0
           && IntConstantMetadataEnums.Contains(enumSymbol.ToDisplayString());

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
            // Prefer source declaration order via DeclaringSyntaxReferences.Span.Start
            // (works for source-defined enums). For metadata-only enums with no
            // syntax references, fall back to ConstantValue ordering so that a
            // plain ordinal-aligned enum (0..n-1) is not misclassified as a value
            // enum due to arbitrary GetMembers() iteration order.
            bool hasSyntaxRefs = fields.Any(f => f.DeclaringSyntaxReferences.Length > 0);
            if (hasSyntaxRefs)
            {
                fields.Sort((a, b) =>
                    a.DeclaringSyntaxReferences.FirstOrDefault()?.Span.Start
                        .CompareTo(b.DeclaringSyntaxReferences.FirstOrDefault()?.Span.Start) ?? 0);
            }
            else
            {
                fields.Sort((a, b) =>
                    Convert.ToInt64(a.ConstantValue ?? 0)
                        .CompareTo(Convert.ToInt64(b.ConstantValue ?? 0)));
            }
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

    private static string RenderLiteral(object raw, string storage)
    {
        // Roslyn reports ulong constants as ulong and long.MinValue as long; both
        // overflow Convert.ToInt64/ToUInt64 in one direction, which used to abort
        // the whole long/ulong enum emission (SampleEnumInt64/UInt64 vanished).
        var signed = ToInt64Bits(raw);
        switch (storage)
        {
            case "byte":
            case "short":
            case "char":
                return $"({storage}){signed}";
            case "long":
                if (raw is ulong unsignedValue)
                {
                    return unsignedValue > long.MaxValue
                        ? $"(long)0x{unsignedValue:X}L"
                        : $"{signed}L";
                }
                return $"{signed}L";
            default: // int
                return signed is < int.MinValue or > int.MaxValue
                    ? $"(int){signed}L"
                    : signed.ToString();
        }
    }

    private static long ToInt64Bits(object raw) => raw switch
    {
        ulong unsigned => unchecked((long)unsigned),
        _ => Convert.ToInt64(raw),
    };

    private static ulong ToUInt64Bits(object raw) => raw switch
    {
        ulong unsigned => unsigned,
        long signed => unchecked((ulong)signed),
        _ => unchecked((ulong)Convert.ToInt64(raw)),
    };

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
