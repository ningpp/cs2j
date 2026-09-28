using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.RefOutInPreprocessor;

/// <summary>
/// Type-full-name → holder short name. Shared between Core rewriter
/// and CLI support-file generator to prevent mismatched names.
/// </summary>
public static class RefOutInMangling
{
    public static string Mangle(ITypeSymbol type)
    {
        var fullName = type.ToDisplayString(
            new SymbolDisplayFormat(
                typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
                genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
                miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes));
        return MangleString(fullName);
    }

    internal static string MangleString(string fullName)
    {
        var sb = new System.Text.StringBuilder(fullName.Length);
        foreach (var ch in fullName)
        {
            sb.Append(ch switch
            {
                '.' => '_',
                '<' => '_',
                '>' => '_',
                ',' => '_',
                ' ' => '_',
                '[' => '_',
                ']' => '_',
                '`' => '_',
                _ => ch,
            });
        }
        var result = sb.ToString();
        // Collapse multiple underscores
        while (result.Contains("__"))
            result = result.Replace("__", "_");
        return result.Trim('_');
    }

    public static string HolderName(ITypeSymbol type)
        => "RefStructByRef_" + Mangle(type);
}
