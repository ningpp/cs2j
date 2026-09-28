using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.GenericPreprocess;

/// <summary>Post-rewrite gate (spec §4.5): counts shapes that must be gone after C02.</summary>
public static class GenericDeepenResidualScanner
{
    public static Dictionary<string, int> Scan(Compilation rewritten)
    {
        var residuals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var tree in rewritten.SyntaxTrees)
        {
            var model = rewritten.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                switch (node)
                {
                    case FieldDeclarationSyntax field when field.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))
                            && ReferencesVisibleTypeParameter(model, field.Declaration.Type):
                        Add(residuals, "static-field");
                        break;
                    case CatchDeclarationSyntax catchDeclaration when IsTypeParameter(model, catchDeclaration.Type):
                        Add(residuals, "catch-type-param");
                        break;
                    case DeclarationPatternSyntax declaration when IsTypeParameter(model, declaration.Type):
                        Add(residuals, "type-param-pattern");
                        break;
                }
            }
        }

        return residuals;
    }

    private static void Add(Dictionary<string, int> residuals, string rule)
    {
        residuals.TryGetValue(rule, out var current);
        residuals[rule] = current + 1;
    }

    private static bool IsTypeParameter(SemanticModel model, TypeSyntax? type)
        => model.GetSymbolInfo(type ?? SyntaxFactory.ParseTypeName("int")).Symbol is ITypeParameterSymbol;

    private static bool ReferencesVisibleTypeParameter(SemanticModel model, TypeSyntax type)
    {
        foreach (var identifier in type.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (model.GetSymbolInfo(identifier).Symbol is ITypeParameterSymbol) return true;
        }

        return false;
    }
}
