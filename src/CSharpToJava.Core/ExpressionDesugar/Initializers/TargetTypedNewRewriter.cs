using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.Initializers;

/// <summary>
/// C24 target-typed <c>new()</c> emission (design §4 IN-9/IN-10): re-emits an
/// explicit <see cref="ObjectCreationExpressionSyntax"/> from the resolved
/// <c>ConvertedType</c>. The probe (plan §6.4) shows the converted type is
/// already the underlying (annotation-free) type for nullable targets; the type
/// is rendered global-qualified so the synthesized name never depends on
/// <c>using</c> directives (same discipline as C36).
/// </summary>
internal static class TargetTypedNewRewriter
{
    /// <summary>Renders a global-qualified type syntax from a symbol.</summary>
    public static TypeSyntax BuildTypeSyntax(ITypeSymbol? type)
        => type == null
            ? SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword))
            : SyntaxFactory.ParseTypeName(
                type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
}
