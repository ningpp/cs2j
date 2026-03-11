using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.PartialType;

/// <summary>
/// Handles merging of partial methods across multiple partial type parts.
/// Partial methods in C# consist of a definition (no body) and an optional implementation (with body).
/// Only the implementation should appear in the merged Java output.
/// </summary>
public class PartialMethodMerger
{
    private readonly DiagnosticCollector _diagnostics;

    public PartialMethodMerger(DiagnosticCollector diagnostics)
    {
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Processes partial methods and returns only the implementations (or definitions if no implementation exists).
    /// </summary>
    /// <param name="methods">All method declarations from all partial parts</param>
    /// <param name="semanticModel">The semantic model for symbol resolution</param>
    /// <returns>Method declarations to include in the merged output</returns>
    public IEnumerable<MethodDeclarationSyntax> ProcessPartialMethods(
        IEnumerable<MethodDeclarationSyntax> methods,
        SemanticModel semanticModel)
    {
        var methodMap = new Dictionary<string, List<MethodDeclarationSyntax>>();
        var processedMethods = new HashSet<string>();

        // Group methods by signature
        foreach (var method in methods)
        {
            var key = GetMethodSignatureKey(method, semanticModel);
            if (!methodMap.ContainsKey(key))
            {
                methodMap[key] = new List<MethodDeclarationSyntax>();
            }
            methodMap[key].Add(method);
        }

        // Process each group
        foreach (var (signature, methodList) in methodMap)
        {
            if (methodList.Count == 1)
            {
                // Single method - include as-is
                yield return methodList[0];
                processedMethods.Add(signature);
                continue;
            }

            // Multiple methods with same signature - check for partial methods
            var hasImplementation = false;
            MethodDeclarationSyntax? implementation = null;
            MethodDeclarationSyntax? definition = null;

            foreach (var method in methodList)
            {
                var symbol = semanticModel.GetDeclaredSymbol(method);
                if (symbol == null)
                    continue;

                if (HasImplementationBody(method))
                {
                    hasImplementation = true;
                    implementation = method;
                }
                else
                {
                    definition = method;
                }
            }

            if (hasImplementation && implementation != null)
            {
                // Include only the implementation
                yield return implementation;

                _diagnostics.Info(
                    $"Merged partial method: {signature}",
                    implementation.GetLocation());
            }
            else if (definition != null)
            {
                // No implementation - include the definition but emit a warning
                // This matches C# compiler behavior where unimplemented partial methods are omitted
                _diagnostics.Info(
                    $"Partial method '{signature}' has no implementation and will be omitted from Java output",
                    definition.GetLocation());
                // Don't yield - omit the method
            }
            else
            {
                // No clear definition/implementation - include first one
                yield return methodList[0];
            }

            processedMethods.Add(signature);
        }
    }

    /// <summary>
    /// Determines if a method has an implementation body.
    /// </summary>
    private static bool HasImplementationBody(MethodDeclarationSyntax method)
    {
        return method.Body != null || method.ExpressionBody != null;
    }

    /// <summary>
    /// Creates a unique key for a method based on its signature.
    /// </summary>
    private static string GetMethodSignatureKey(
        MethodDeclarationSyntax method,
        SemanticModel semanticModel)
    {
        var symbol = semanticModel.GetDeclaredSymbol(method);
        if (symbol == null)
        {
            // Fallback to syntax-based key
            var parameters = string.Join(",",
                method.ParameterList?.Parameters.Select(p => p.Type?.ToString() ?? "object")
                ?? Array.Empty<string>());
            return $"{method.Identifier.Text}({parameters})";
        }

        // Use symbol-based key that includes return type and parameter types
        var paramTypes = string.Join(",",
            symbol.Parameters.Select(p => p.Type.ToDisplayString()));
        return $"{symbol.Name}({paramTypes})";
    }

    /// <summary>
    /// Checks if a method symbol is a partial method definition.
    /// </summary>
    public static bool IsPartialMethodDefinition(IMethodSymbol? methodSymbol)
    {
        return methodSymbol?.IsPartialDefinition == true;
    }

    /// <summary>
    /// Gets the implementation part of a partial method, or null if it has no implementation.
    /// </summary>
    public static IMethodSymbol? GetPartialImplementation(IMethodSymbol methodSymbol)
    {
        if (methodSymbol.IsPartialDefinition)
        {
            return methodSymbol.PartialImplementationPart;
        }
        return null;
    }
}
