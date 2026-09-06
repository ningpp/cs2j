using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.DefaultParameterStripper;

internal sealed class DefaultParameterCatalog
{
    private static readonly SymbolDisplayFormat s_qualifiedFormat =
        SymbolDisplayFormat.FullyQualifiedFormat;

    private readonly HashSet<string> _existingSignatures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingOverloads = new(StringComparer.Ordinal);

    public void Collect(SyntaxTree tree, SemanticModel model)
    {
        var root = tree.GetRoot();
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            RegisterExistingSignature(model, method);
        foreach (var ctor in root.DescendantNodes().OfType<ConstructorDeclarationSyntax>())
            RegisterExistingSignature(model, ctor);
    }

    public bool HasExistingSignature(ISymbol containingSymbol, string memberName, string paramTypeString)
        => _existingSignatures.Contains(BuildKey(containingSymbol, memberName, paramTypeString));

    public bool HasPendingOverload(ISymbol containingSymbol, string memberName, string paramTypeString)
        => _pendingOverloads.Contains(BuildKey(containingSymbol, memberName, paramTypeString));

    public void RegisterPendingOverload(ISymbol containingSymbol, string memberName, string paramTypeString)
        => _pendingOverloads.Add(BuildKey(containingSymbol, memberName, paramTypeString));

    public static string BuildParamTypeString(
        SemanticModel model,
        SeparatedSyntaxList<ParameterSyntax> parameters,
        int cutAt)
    {
        var parts = new List<string>(cutAt);
        for (var i = 0; i < cutAt && i < parameters.Count; i++)
        {
            var paramInfo = model.GetSymbolInfo(parameters[i].Type!);
            if (paramInfo.Symbol is ITypeSymbol typeSymbol)
                parts.Add(typeSymbol.ToDisplayString(s_qualifiedFormat));
            else
                parts.Add(parameters[i].Type!.ToString());
        }
        return string.Join(", ", parts);
    }

    public static string BuildFullParamTypeString(
        SemanticModel model,
        SeparatedSyntaxList<ParameterSyntax> parameters)
        => BuildParamTypeString(model, parameters, parameters.Count);

    private void RegisterExistingSignature(SemanticModel model, MemberDeclarationSyntax member)
    {
        var symbol = model.GetDeclaredSymbol(member);
        if (symbol == null) return;
        var name = member switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text,
            ConstructorDeclarationSyntax => ".ctor",
            _ => null,
        };
        if (name == null) return;
        var parameters = member switch
        {
            MethodDeclarationSyntax m => m.ParameterList.Parameters,
            ConstructorDeclarationSyntax c => c.ParameterList.Parameters,
            _ => (SeparatedSyntaxList<ParameterSyntax>?)null,
        };
        if (parameters == null) return;
        var key = BuildKey(symbol.ContainingType ?? symbol, name,
            BuildFullParamTypeString(model, parameters.Value));
        _existingSignatures.Add(key);
    }

    private static string BuildKey(ISymbol containingSymbol, string memberName, string paramTypeString)
    {
        var containingKey = containingSymbol == null
            ? "<global>"
            : containingSymbol.ToDisplayString(s_qualifiedFormat);
        return $"{containingKey}::{memberName}({paramTypeString})";
    }
}
