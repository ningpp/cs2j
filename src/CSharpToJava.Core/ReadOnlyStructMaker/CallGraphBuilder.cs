using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ReadOnlyStructMaker;

/// <summary>
/// Detects cross-method/cross-file mutations of struct fields via ref/out parameters
/// and external public field assignments.
/// </summary>
internal sealed class CallGraphBuilder
{
    private readonly SemanticModel _model;

    public CallGraphBuilder(SemanticModel model) => _model = model;

    /// <summary>
    /// Check if any field of the struct is modified through a ref/out parameter
    /// within the same compilation unit.
    /// </summary>
    public bool IsModifiedThroughRef(INamedTypeSymbol structSymbol, SyntaxNode root)
    {
        var structName = structSymbol.Name;

        // Find all parameters of this struct type with ref/out modifiers
        foreach (var param in root.DescendantNodes().OfType<ParameterSyntax>())
        {
            var hasRefOrOut = param.Modifiers.Any(m =>
                m.IsKind(SyntaxKind.RefKeyword) || m.IsKind(SyntaxKind.OutKeyword));
            if (!hasRefOrOut) continue;

            // Check if parameter type matches our struct
            var paramSymbol = _model.GetDeclaredSymbol(param);
            if (paramSymbol?.Type is not INamedTypeSymbol paramType) continue;
            if (paramType.Name != structName) continue;

            // Check if any member of this parameter is assigned within the containing method
            var containingMethod = param.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
            if (containingMethod == null) continue;

            var paramName = param.Identifier.Text;
            if (IsParameterFieldModified(containingMethod, paramName))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Check if non-private fields of the struct are assigned from outside the struct definition.
    /// This covers public, internal, protected, and protected-internal fields.
    /// </summary>
    public bool HasExternalPublicFieldAssignment(INamedTypeSymbol structSymbol, SyntaxNode root)
    {
        var accessibleFields = structSymbol.GetMembers().OfType<IFieldSymbol>()
            .Where(f => f.DeclaredAccessibility != Accessibility.Private && f.DeclaredAccessibility != Accessibility.NotApplicable && !f.IsStatic && !f.IsConst)
            .Select(f => f.Name).ToHashSet();

        if (accessibleFields.Count == 0) return false;

        var structName = structSymbol.Name;

        // Find assignments to fields of variables typed as this struct, outside the struct itself
        foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.Left is not MemberAccessExpressionSyntax memberAccess) continue;

            var fieldSymbol = _model.GetSymbolInfo(assignment.Left).Symbol;
            if (fieldSymbol is not IFieldSymbol field) continue;
            if (!accessibleFields.Contains(field.Name)) continue;
            if (field.ContainingType?.Name != structName) continue;

            // Check if this assignment is outside the struct definition
            var containingStruct = assignment.Ancestors().OfType<StructDeclarationSyntax>().FirstOrDefault();
            if (containingStruct == null || containingStruct.Identifier.Text != structName)
                return true;
        }

        // Also check ++/-- on accessible fields from outside
        foreach (var unary in root.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>()
            .Concat(root.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>()
                .Select(p => (PostfixUnaryExpressionSyntax?)null!).Where(_ => false)))
        {
            var operand = unary.Operand;
            if (operand is not MemberAccessExpressionSyntax) continue;

            var fieldSymbol = _model.GetSymbolInfo(operand).Symbol;
            if (fieldSymbol is not IFieldSymbol field) continue;
            if (!accessibleFields.Contains(field.Name)) continue;
            if (field.ContainingType?.Name != structName) continue;

            var containingStruct = unary.Ancestors().OfType<StructDeclarationSyntax>().FirstOrDefault();
            if (containingStruct == null || containingStruct.Identifier.Text != structName)
                return true;
        }

        return false;
    }

    private static bool IsParameterFieldModified(BaseMethodDeclarationSyntax method, string paramName)
    {
        // Check assignments like: param.Field = value
        foreach (var assignment in method.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.Left is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Expression is IdentifierNameSyntax id &&
                id.Identifier.Text == paramName)
                return true;
        }

        // Check ++/-- like: param.Field++
        foreach (var postfix in method.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>())
        {
            if (postfix.Operand is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Expression is IdentifierNameSyntax id &&
                id.Identifier.Text == paramName)
                return true;
        }

        foreach (var prefix in method.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>())
        {
            if (prefix.Kind() is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression &&
                prefix.Operand is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Expression is IdentifierNameSyntax id &&
                id.Identifier.Text == paramName)
                return true;
        }

        // Check compound assignments like: param.Field += value
        foreach (var assignment in method.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.Kind() != SyntaxKind.SimpleAssignmentExpression &&
                assignment.Left is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Expression is IdentifierNameSyntax id &&
                id.Identifier.Text == paramName)
                return true;
        }

        return false;
    }
}
