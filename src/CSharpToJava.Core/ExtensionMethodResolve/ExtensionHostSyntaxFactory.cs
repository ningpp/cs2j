using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExtensionMethodResolve;

/// <summary>
/// Builds the <c>global::</c>-qualified host name syntax for an explicit static
/// call (design §4.1): from <c>Unreduced.ContainingType</c> via
/// <see cref="SymbolDisplayFormat.FullyQualifiedFormat"/> + ParseName, including
/// constructed type arguments for nested generic hosts (EM-GEN-HOST). String
/// concatenation is forbidden.
///
/// M0 shell: the API is declared for review but unused (no rules rewrite yet).
/// </summary>
internal static class ExtensionHostSyntaxFactory
{
    /// <summary>
    /// Build the qualified host name (e.g. <c>global::Ns.Outer&lt;T&gt;.Ext</c>)
    /// under which the unreduced static method is invoked. Lands in M1.
    /// </summary>
    public static NameSyntax BuildHostName(IMethodSymbol unreduced)
        => throw new NotSupportedException("ExtensionHostSyntaxFactory.BuildHostName lands in M1 (EM-CALL/EM-GEN-HOST).");
}
