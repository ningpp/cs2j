using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.DefaultParameterStripper;

/// <summary>
/// Pass 3: re-parses the output syntax tree and asserts no residual
/// ParameterSyntax.Default exists in managed contexts (methods,
/// constructors, local functions). Excludes delegate, indexer,
/// operator, partial, extern, and property accessor contexts.
/// </summary>
public static class DefaultParameterVerifier
{
    public static List<DefaultParameterStripDiagnostic> Verify(string sourceCode)
    {
        var errors = new List<DefaultParameterStripDiagnostic>();
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetRoot();

        // Check methods (excluding delegate, explicit interface impl still checked)
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            // Skip partial / extern — they are intentionally not stripped
            if (method.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)
                || m.IsKind(SyntaxKind.ExternKeyword)))
                continue;

            CheckParameters(method.ParameterList.Parameters, "method", method.Identifier.Text, errors);
        }

        // Check constructors
        foreach (var ctor in root.DescendantNodes().OfType<ConstructorDeclarationSyntax>())
        {
            CheckParameters(ctor.ParameterList.Parameters, "constructor", ".ctor", errors);
        }

        // Check local functions
        foreach (var local in root.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
        {
            if (local.ParameterList != null)
                CheckParameters(local.ParameterList.Parameters, "local function", local.Identifier.Text, errors);
        }

        // Note: DelegateDeclarationSyntax, IndexerDeclarationSyntax,
        // OperatorDeclarationSyntax, ConversionOperatorDeclarationSyntax
        // are NOT checked — their defaults are intentionally preserved (spec §4.1).

        return errors;
    }

    private static void CheckParameters(
        SeparatedSyntaxList<ParameterSyntax> parameters,
        string context, string memberName,
        List<DefaultParameterStripDiagnostic> errors)
    {
        foreach (var param in parameters)
        {
            if (param.Default != null)
            {
                errors.Add(new DefaultParameterStripDiagnostic(
                    DefaultParameterStripSeverity.Error,
                    $"Residual default value found in {context} '{memberName}' parameter '{param.Identifier.Text}'"));
            }
        }
    }
}
