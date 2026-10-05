using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ThreadStaticRewrite;

/// <summary>
/// Post-rewrite residual gate. M1 counts the ThreadStaticAttribute instances left on
/// field symbols in the rewritten compilation; M2 adds reconciliation against the skip set.
/// </summary>
public static class ThreadStaticResidualScanner
{
    public const string ThreadStaticAttributeMetadataName = "System.ThreadStaticAttribute";

    public static (int Count, IReadOnlyList<Location> Locations) Scan(Compilation rewrittenCompilation)
    {
        var attributeSymbol = rewrittenCompilation.GetTypeByMetadataName(ThreadStaticAttributeMetadataName);
        if (attributeSymbol == null)
        {
            return (0, Array.Empty<Location>());
        }

        var locations = new List<Location>();
        foreach (var tree in rewrittenCompilation.SyntaxTrees)
        {
            var model = rewrittenCompilation.GetSemanticModel(tree);
            foreach (var field in tree.GetRoot().DescendantNodes().OfType<FieldDeclarationSyntax>())
            {
                foreach (var declarator in field.Declaration.Variables)
                {
                    if (model.GetDeclaredSymbol(declarator) is not IFieldSymbol fieldSymbol) continue;
                    foreach (var data in fieldSymbol.GetAttributes())
                    {
                        if (data.AttributeClass == null
                            || !SymbolEqualityComparer.Default.Equals(data.AttributeClass, attributeSymbol))
                        {
                            continue;
                        }

                        locations.Add(data.ApplicationSyntaxReference?.GetSyntax().GetLocation()
                            ?? declarator.Identifier.GetLocation());
                    }
                }
            }
        }

        return (locations.Count, locations);
    }
}
