using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.DefaultParameterStripper.ParamsCollections;

/// <summary>
/// Stable identity for a non-array params declaration that the rule decided to
/// KEEP, used to pair output-tree residuals with their skip diagnostics
/// (PC-10). Keyed by containing type, member name and the params parameter's
/// type text (whitespace-normalized so formatted output matches the original).
/// </summary>
internal static class ParamsResidualKey
{
    public static string Build(SyntaxNode declaration, ParameterSyntax paramsParameter)
    {
        var memberName = declaration switch
        {
            MethodDeclarationSyntax method => method.Identifier.Text,
            ConstructorDeclarationSyntax => ".ctor",
            LocalFunctionStatementSyntax local => local.Identifier.Text,
            DelegateDeclarationSyntax delegateDeclaration => delegateDeclaration.Identifier.Text,
            TypeDeclarationSyntax typeDeclaration => typeDeclaration.Identifier.Text,
            _ => "?",
        };

        var containingType = declaration.Ancestors()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault()?.Identifier.Text ?? "";

        var typeText = (paramsParameter.Type?.ToString() ?? "").Replace(" ", "");
        return $"{containingType}.{memberName}({typeText})";
    }
}

/// <summary>
/// C48 residual gate (T17, PC-10): re-scans the rewritten output tree and
/// asserts every surviving non-array <c>params</c> modifier is paired with an
/// explicit skip decision (CS2J0601/0602/0606/0607/0608/0610/0612 families —
/// unsupported types, unresolvable plans, broken initializer patterns, builder
/// blocks, ref-struct elements, slot conflicts, incomplete virtual families,
/// scope/char-face exclusions, xUnit test methods). Unpaired residuals emit
/// CS2J0619 (Error in strict mode).
///
/// Classic array params (strategy A) are legal residuals — Java maps them
/// directly to varargs — and are never flagged.
///
/// Idempotency is implied by the same gate: materialized declarations carry no
/// params modifier, so a second Strip of the output finds no params-collection
/// candidate (the E08 defaults scan is likewise clean), and the file is
/// returned byte-identical with <c>Changed=false</c>.
/// </summary>
internal static class ParamsResidualVerifier
{
    public static List<DefaultParameterStripDiagnostic> Verify(
        SyntaxNode outputRoot,
        IReadOnlyCollection<string> toleratedResiduals,
        bool strict)
    {
        var tolerated = toleratedResiduals as IReadOnlySet<string>
            ?? new HashSet<string>(toleratedResiduals, StringComparer.Ordinal);
        var diagnostics = new List<DefaultParameterStripDiagnostic>();

        foreach (var parameterList in outputRoot.DescendantNodes().OfType<ParameterListSyntax>())
        {
            var parameters = parameterList.Parameters;
            if (parameters.Count == 0)
            {
                continue;
            }

            var last = parameters[^1];
            if (!last.Modifiers.Any(m => m.IsKind(SyntaxKind.ParamsKeyword)))
            {
                continue;
            }

            if (last.Type is ArrayTypeSyntax)
            {
                continue; // strategy A: classic params T[] is a legal residual
            }

            if (parameterList.Parent is not { } declaration)
            {
                continue;
            }

            var key = ParamsResidualKey.Build(declaration, last);
            if (tolerated.Contains(key))
            {
                continue;
            }

            diagnostics.Add(ParamsDiagnostics.Create(
                ParamsDiagnostics.ResidualGate,
                string.Format(ParamsDiagnostics.ResidualGateMessage, key),
                strict));
        }

        return diagnostics;
    }
}
