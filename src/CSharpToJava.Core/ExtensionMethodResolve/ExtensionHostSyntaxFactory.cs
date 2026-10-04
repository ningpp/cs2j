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

        return BuildHostName(unreduced.ContainingType);
    }

    /// <summary>
    /// Build the qualified host name from an explicit host symbol. C49 uses
    /// this overload for materialized extension blocks, whose host is the
    /// block parent class — never the synthetic <c>TypeKind.Extension</c>
    /// container of the bound member.
    /// </summary>
    public static NameSyntax BuildHostName(INamedTypeSymbol host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var text = host.ToDisplayString(QualifiedHostFormat);
        return SyntaxFactory.ParseName(text);
    }
}
