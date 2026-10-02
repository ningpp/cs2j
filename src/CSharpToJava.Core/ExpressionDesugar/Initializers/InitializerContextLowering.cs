using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.ExpressionDesugar.Initializers;

/// <summary>
/// C24 expression-position statement lowering (design §4 IN-11 / decision 11):
/// recognition of the nearest enclosing writable statement and synthesis of
/// the temp-declaration / if-else guard / using / argument-interleaving forms.
///
/// This is the C24 counterpart of C36's minimal <c>CollectionContextLowering</c>;
/// the shared parts are extracted up to <c>Shared/</c> append-only when the
/// family reviews it (design §3.2-4, decision 4). M0 is an idle shell.
/// </summary>
internal static class InitializerContextLowering
{
    /// <summary>
    /// M0: no lowering is performed yet, so no site is considered lowerable.
    /// M2 computes the real decision from the enclosing statement chain.
    /// </summary>
    public static bool CanLowerAt(SyntaxNode site, out string reason)
    {
        reason = string.Empty;
        return false;
    }
}