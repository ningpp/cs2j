using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.GenericPreprocess;

/// <summary>Naming conventions and type-parameter detection shared by generic-deepen (C02).</summary>
public static class GenericDeepenSyntaxFacts
{
    public const string AnchorPrefix = "__cs2j_token_";
    public const string E10SentinelPrefix = "__cs2j_typeof_";
    public const string HolderPrefix = "__Cs2jGenericHolder_";
    public const string GeneratedFileStem = "_Cs2jGenericHolders";

    public static string AnchorVariableName(string typeParamName) => AnchorPrefix + typeParamName;

    public static string E10SentinelVariableName(string typeParamName) => E10SentinelPrefix + typeParamName;

    /// <summary>Holder class name for a containing type, e.g. "Outer`1+Inner" → "Outer_1_Inner".</summary>
    public static string HolderTypeName(string containingTypeMetadataName)
        => HolderPrefix + containingTypeMetadataName
            .Replace('`', '_')
            .Replace('+', '_');

    /// <summary>
    /// Type-parameter detection shared with the E10 generic pass: semantic-first, falling back
    /// to the conservative syntactic heuristic (single uppercase letter, or "T"-prefixed name
    /// following the CA1715 convention) only when the model cannot resolve the symbol.
    /// </summary>
    public static bool IsTypeParameterType(SemanticModel? model, TypeSyntax? type)
    {
        if (type == null) return false;

        if (model != null)
        {
            try
            {
                var symbol = model.GetSymbolInfo(type).Symbol;
                if (symbol is ITypeParameterSymbol) return true;
                if (symbol is INamedTypeSymbol) return false;
                // Unresolved identifier: fall through to the syntactic heuristic.
            }
            catch (ArgumentException)
            {
                // Node not part of this model's tree (e.g. already-rewritten node).
            }
        }

        return LooksLikeTypeParameterName(type);
    }

    internal static bool LooksLikeTypeParameterName(TypeSyntax? type)
    {
        if (type is not IdentifierNameSyntax id) return false;
        var name = id.Identifier.Text;
        if (IsPredefinedTypeName(name)) return false;
        // Single uppercase letter (T, K, V, U).
        if (name.Length == 1 && char.IsLetter(name[0])) return true;
        // Multi-char names following the TXxx convention (TKey, TValue, TItem).
        return name.Length >= 2 && name[0] == 'T' && char.IsUpper(name[1]);
    }

    internal static bool IsPredefinedTypeName(string name) => name switch
    {
        "bool" or "byte" or "sbyte" or "char" or "decimal" or "double" or "float"
        or "int" or "uint" or "long" or "ulong" or "short" or "ushort"
        or "object" or "string" or "void" or "dynamic" or "var"
        or "Boolean" or "Byte" or "SByte" or "Char" or "Decimal" or "Double"
        or "Single" or "Int32" or "UInt32" or "Int64" or "UInt64"
        or "Int16" or "UInt16" or "Object" or "String" or "Void"
        => true,
        _ => false,
    };
}
