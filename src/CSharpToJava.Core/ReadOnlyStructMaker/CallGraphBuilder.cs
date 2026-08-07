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
    /// Check if non-private fields of the struct are accessed by ref/out from outside the
    /// struct definition. Such fields cannot be converted to properties because properties
    /// don't support ref/out access (CS0206).
    /// </summary>
    public bool HasExternalRefFieldAccess(INamedTypeSymbol structSymbol, SyntaxNode root)
    {
        var accessibleFields = structSymbol.GetMembers().OfType<IFieldSymbol>()
            .Where(f => f.DeclaredAccessibility != Accessibility.Private && f.DeclaredAccessibility != Accessibility.NotApplicable && !f.IsStatic && !f.IsConst)
            .Select(f => f.Name).ToHashSet();

        if (accessibleFields.Count == 0) return false;

        var structName = structSymbol.Name;
        var compilation = _model.Compilation;

        var rootModel = compilation.GetSemanticModel(root.SyntaxTree);
        if (HasExternalRefAccessInTree(root, accessibleFields, structName, rootModel))
            return true;

        var currentTree = root.SyntaxTree;
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (tree == currentTree) continue;
            var otherRoot = tree.GetRoot();
            var otherModel = compilation.GetSemanticModel(tree);
            if (HasExternalRefAccessInTree(otherRoot, accessibleFields, structName, otherModel))
                return true;
        }

        return false;
    }

    private static bool HasExternalRefAccessInTree(
        SyntaxNode root, HashSet<string> accessibleFields, string structName, SemanticModel model)
    {
        foreach (var argument in root.DescendantNodes().OfType<ArgumentSyntax>())
        {
            if (!argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) &&
                !argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
                continue;

            // Unwrap ! (SuppressNullableWarningExpression) if present: ref obj.Field! → obj.Field
            var expression = argument.Expression;
            if (expression is PostfixUnaryExpressionSyntax suppress &&
                suppress.IsKind(SyntaxKind.SuppressNullableWarningExpression))
                expression = suppress.Operand;

            if (expression is not MemberAccessExpressionSyntax memberAccess)
                continue;

            var fieldSymbol = model.GetSymbolInfo(memberAccess).Symbol;
            if (fieldSymbol is not IFieldSymbol field) continue;
            if (!accessibleFields.Contains(field.Name)) continue;
            if (field.ContainingType?.Name != structName) continue;

            var containingStruct = argument.Ancestors().OfType<StructDeclarationSyntax>().FirstOrDefault();
            if (containingStruct == null || containingStruct.Identifier.Text != structName)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Check if non-private fields of the struct are assigned from outside the struct definition.
    /// This covers public, internal, protected, and protected-internal fields.
    /// Searches both the current file and all other files in the compilation.
    /// </summary>
    public bool HasExternalPublicFieldAssignment(INamedTypeSymbol structSymbol, SyntaxNode root)
    {
        var accessibleFields = structSymbol.GetMembers().OfType<IFieldSymbol>()
            .Where(f => f.DeclaredAccessibility != Accessibility.Private && f.DeclaredAccessibility != Accessibility.NotApplicable && !f.IsStatic && !f.IsConst)
            .Select(f => f.Name).ToHashSet();

        if (accessibleFields.Count == 0) return false;

        var structName = structSymbol.Name;
        var compilation = _model.Compilation;

        // Check within the current file - use the semantic model for the correct syntax tree
        var rootModel = compilation.GetSemanticModel(root.SyntaxTree);
        if (HasExternalAssignmentInTree(root, accessibleFields, structName, rootModel))
            return true;

        // Check across other files in the compilation
        var currentTree = root.SyntaxTree;
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (tree == currentTree) continue;
            var otherRoot = tree.GetRoot();
            var otherModel = compilation.GetSemanticModel(tree);
            if (HasExternalAssignmentInTree(otherRoot, accessibleFields, structName, otherModel))
                return true;
        }

        return false;
    }

    private static bool HasExternalAssignmentInTree(
        SyntaxNode root, HashSet<string> accessibleFields, string structName, SemanticModel model)
    {
        // Find object initializers: new StructType { Field = value, ... }
        // These set fields externally and require property conversion for readonly structs.
        foreach (var objectCreation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            // Check if this creates the target struct type by comparing type name text
            // Use GetTypeInfo first, then fall back to GetSymbolInfo
            var typeInfo = model.GetTypeInfo(objectCreation.Type);
            string? typeName = typeInfo.Type?.Name;

            // If GetTypeInfo fails, try GetSymbolInfo
            if (typeName == null)
            {
                var symbolInfo = model.GetSymbolInfo(objectCreation.Type);
                typeName = symbolInfo.Symbol?.Name;
            }

            // Final fallback: use the type syntax text (strip generic arguments)
            if (typeName == null)
            {
                typeName = objectCreation.Type.ToString();
                int genericIndex = typeName.IndexOf('<');
                if (genericIndex > 0)
                    typeName = typeName.Substring(0, genericIndex);
            }

            if (typeName != structName)
                continue;

            // Check if there's an initializer
            var initializer = objectCreation.Initializer;
            if (initializer == null)
                continue;

            // Check if any of the initializers set an accessible field
            bool hasFieldAssignment = false;
            foreach (var expr in initializer.Expressions)
            {
                if (expr is AssignmentExpressionSyntax initAssignment &&
                    initAssignment.Left is IdentifierNameSyntax initFieldName &&
                    accessibleFields.Contains(initFieldName.Identifier.Text))
                {
                    hasFieldAssignment = true;
                    break;
                }
            }

            if (hasFieldAssignment)
            {
                // Check if this initializer is outside the struct definition
                var containingStruct = objectCreation.Ancestors().OfType<StructDeclarationSyntax>().FirstOrDefault();
                if (containingStruct == null || containingStruct.Identifier.Text != structName)
                {
                    return true;
                }
            }
        }

        // Find assignments to fields of variables typed as this struct, outside the struct itself
        foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.Left is not MemberAccessExpressionSyntax) continue;

            var fieldSymbol = model.GetSymbolInfo(assignment.Left).Symbol;
            if (fieldSymbol is not IFieldSymbol field) continue;
            if (!accessibleFields.Contains(field.Name)) continue;
            if (field.ContainingType?.Name != structName) continue;

            // Check if this assignment is outside the struct definition
            var containingStruct = assignment.Ancestors().OfType<StructDeclarationSyntax>().FirstOrDefault();
            if (containingStruct == null || containingStruct.Identifier.Text != structName)
                return true;
        }

        // Check ref/out access to accessible fields from outside (e.g. MemoryMarshal.CreateSpan(ref obj.Field, n))
        // Such patterns cannot be converted to properties because properties don't support ref access.
        foreach (var argument in root.DescendantNodes().OfType<ArgumentSyntax>())
        {
            if (!argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) &&
                !argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
                continue;

            // Unwrap ! (SuppressNullableWarningExpression) if present: ref obj.Field! → obj.Field
            var expression = argument.Expression;
            if (expression is PostfixUnaryExpressionSyntax suppress &&
                suppress.IsKind(SyntaxKind.SuppressNullableWarningExpression))
                expression = suppress.Operand;

            if (expression is not MemberAccessExpressionSyntax memberAccess)
                continue;

            var fieldSymbol = model.GetSymbolInfo(memberAccess).Symbol;
            if (fieldSymbol is not IFieldSymbol field) continue;
            if (!accessibleFields.Contains(field.Name)) continue;
            if (field.ContainingType?.Name != structName) continue;

            var containingStruct = argument.Ancestors().OfType<StructDeclarationSyntax>().FirstOrDefault();
            if (containingStruct == null || containingStruct.Identifier.Text != structName)
                return true;
        }

        // Also check ++/-- on accessible fields from outside
        foreach (var unary in root.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>())
        {
            var operand = unary.Operand;
            if (operand is not MemberAccessExpressionSyntax) continue;

            var fieldSymbol = model.GetSymbolInfo(operand).Symbol;
            if (fieldSymbol is not IFieldSymbol field) continue;
            if (!accessibleFields.Contains(field.Name)) continue;
            if (field.ContainingType?.Name != structName) continue;

            var containingStruct = unary.Ancestors().OfType<StructDeclarationSyntax>().FirstOrDefault();
            if (containingStruct == null || containingStruct.Identifier.Text != structName)
                return true;
        }

        foreach (var unary in root.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>())
        {
            if (unary.Kind() is not (SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression))
                continue;
            var operand = unary.Operand;
            if (operand is not MemberAccessExpressionSyntax) continue;

            var fieldSymbol = model.GetSymbolInfo(operand).Symbol;
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
