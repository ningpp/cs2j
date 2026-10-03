using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExtensionMethodResolve;

/// <summary>
/// Builds the <c>global::</c>-qualified host name syntax for an explicit static
/// call (design §4.1, EM-GEN-HOST): rendered from the CONSTRUCTED
/// <c>Unreduced.ContainingType</c> symbol (nested generic hosts therefore carry
/// their type arguments, e.g. <c>global::Ns.Outer&lt;T&gt;.Ext</c>) via
/// <see cref="SymbolDisplayFormat.FullyQualifiedFormat"/> and
/// <c>SyntaxFactory.ParseName</c>. String concatenation is forbidden.
/// </summary>
internal static class ExtensionHostSyntaxFactory
{
    private static readonly SymbolDisplayFormat QualifiedHostFormat =
        SymbolDisplayFormat.FullyQualifiedFormat
            .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Included);

    /// <summary>
    /// Build the qualified host name (e.g. <c>global::Ns.Outer&lt;T&gt;.Ext</c>)
    /// under which the unreduced static method is invoked.
    /// </summary>
    public static NameSyntax BuildHostName(IMethodSymbol unreduced)
    {
        ArgumentNullException.ThrowIfNull(unreduced);

        var host = unreduced.ContainingType;
        var text = host.ToDisplayString(QualifiedHostFormat);
        return SyntaxFactory.ParseName(text);
    }
}
