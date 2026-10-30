using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ReadOnlyStructMaker;

/// <summary>
/// A property whose setter has private accessibility (either an explicit
/// <c>private set</c> accessor modifier or a property that is itself private and
/// therefore has an implicitly private setter).
/// </summary>
internal readonly record struct PrivateSetterProperty(
    PropertyDeclarationSyntax Property,
    AccessorDeclarationSyntax Setter);

/// <summary>
/// Syntactic facts about property setters shared by the analyzer and the L2
/// property-convert rewriter. Keeping the "plain backing-field write" definition in
/// one place guarantees the analyzer only classifies as convertible the setters the
/// rewriter can actually redirect to a backing field.
/// </summary>
internal static class PrivateSetterFacts
{
    /// <summary>
    /// All instance properties of <paramref name="node"/> that carry a private setter.
    /// <c>init</c> accessors are excluded: they are not setters and are never removed
    /// by the property-convert path.
    /// </summary>
    public static IReadOnlyList<PrivateSetterProperty> GetPrivateSetterProperties(
        StructDeclarationSyntax node)
    {
        var result = new List<PrivateSetterProperty>();
        foreach (var prop in node.Members.OfType<PropertyDeclarationSyntax>())
        {
            if (prop.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))) continue;
            var setter = prop.AccessorList?.Accessors
                .FirstOrDefault(a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
            if (setter == null) continue;
            if (!IsPrivateSetter(prop, setter)) continue;
            result.Add(new PrivateSetterProperty(prop, setter));
        }
        return result;
    }

    /// <summary>
    /// True when the property setter is private: either the setter itself carries
    /// <c>private</c>, or the property has no accessibility modifier at all (struct
    /// members default to private, so the setter's accessibility is private too).
    /// </summary>
    public static bool IsPrivateSetter(
        PropertyDeclarationSyntax property, AccessorDeclarationSyntax setter)
    {
        if (setter.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword)))
            return true;
        if (setter.Modifiers.Any(m => IsAccessibilityKeyword(m)))
            return false;
        // No accessor modifier: the setter inherits the property's accessibility.
        // Struct members with no accessibility modifier are implicitly private.
        return !property.Modifiers.Any(m =>
            m.IsKind(SyntaxKind.PublicKeyword) ||
            m.IsKind(SyntaxKind.InternalKeyword) ||
            m.IsKind(SyntaxKind.ProtectedKeyword));
    }

    private static bool IsAccessibilityKeyword(SyntaxToken token)
        => token.IsKind(SyntaxKind.PublicKeyword) ||
           token.IsKind(SyntaxKind.InternalKeyword) ||
           token.IsKind(SyntaxKind.ProtectedKeyword) ||
           token.IsKind(SyntaxKind.PrivateKeyword);

    /// <summary>Names of the struct's instance fields (static/const excluded).</summary>
    public static HashSet<string> GetInstanceFieldNames(StructDeclarationSyntax node)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in node.Members.OfType<FieldDeclarationSyntax>())
        {
            if (field.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword) ||
                                         m.IsKind(SyntaxKind.ConstKeyword)))
                continue;
            foreach (var variable in field.Declaration.Variables)
                names.Add(variable.Identifier.Text);
        }
        return names;
    }

    /// <summary>
    /// True when the setter body is exactly one plain assignment of <c>value</c> to an
    /// instance field of the struct:
    /// <code>
    /// set { _field = value; }
    /// set => _field = value;
    /// set { this._field = value; }
    /// </code>
    /// Anything else (validation, multiple writes, compound assignment, writing a
    /// property or a non-field storage) is not a plain backing-field write.
    /// </summary>
    public static bool TryGetPlainBackingFieldWrite(
        AccessorDeclarationSyntax setter,
        IReadOnlySet<string> instanceFieldNames,
        out string fieldName)
    {
        fieldName = string.Empty;

        AssignmentExpressionSyntax? assignment;
        if (setter.Body != null)
        {
            if (setter.Body.Statements.Count != 1) return false;
            if (setter.Body.Statements[0] is not ExpressionStatementSyntax
                {
                    Expression: AssignmentExpressionSyntax bodyAssignment
                })
            {
                return false;
            }
            assignment = bodyAssignment;
        }
        else if (setter.ExpressionBody != null)
        {
            assignment = setter.ExpressionBody.Expression as AssignmentExpressionSyntax;
            if (assignment == null) return false;
        }
        else
        {
            // Auto-property: the property itself is the storage, not a backing field.
            return false;
        }

        if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)) return false;
        if (assignment.Right is not IdentifierNameSyntax { Identifier.Text: "value" }) return false;

        var targetName = assignment.Left switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } ma
                when ma.Name is IdentifierNameSyntax name => name.Identifier.Text,
            _ => null,
        };
        if (targetName == null || !instanceFieldNames.Contains(targetName)) return false;

        fieldName = targetName;
        return true;
    }

    /// <summary>True for auto-properties (no accessor bodies), which stay get-only
    /// auto-properties after the private setter is removed.</summary>
    public static bool IsAutoProperty(AccessorDeclarationSyntax setter)
        => setter.Body == null && setter.ExpressionBody == null;
}
