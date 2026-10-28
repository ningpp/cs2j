using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// The C01 ref-out-in pass synthesizes ByRef accessor scaffolding of the exact
/// shape <c>ByRef&lt;T&gt;.Create&lt;T&gt;(() =&gt; x, v =&gt; x = v)</c> so a by-ref
/// parameter/local can be modelled without <c>ref</c>. Those two lambdas are
/// infrastructure, not user closures: closure-lift must neither analyse their
/// capture (the synthetic accessor parameter has no declarator in the model),
/// neither plan holders for them, nor lift them to static methods.
///
/// The decision is semantic (invocation target is
/// <c>Cs2jByRef.ByRef&lt;T&gt;.Create</c>). Residual gates build a
/// single-tree compilation without the support library, so the target does not
/// bind there; the exact synthesized syntax is used as a fallback in that
/// reference-limited case only.
/// </summary>
internal static class ByRefAccessorLambda
{
    public static bool IsByRefCreateArgument(
        SemanticModel? model,
        LambdaExpressionSyntax lambda)
    {
        if (lambda.Parent is not ArgumentSyntax { Parent: ArgumentListSyntax argumentList }
            || argumentList.Parent is not InvocationExpressionSyntax invocation)
        {
            return false;
        }

        if (model is not null && model.GetSymbolInfo(invocation).Symbol is { } symbol)
            return IsCs2jByRefCreate(symbol);

        return IsSynthesizedShape(invocation, argumentList);
    }

    /// <summary>True when the symbol is a factory method on <c>Cs2jByRef.ByRef&lt;T&gt;</c> named <c>Create</c>.</summary>
    internal static bool IsCs2jByRefCreate(ISymbol? symbol)
        => symbol is IMethodSymbol { Name: "Create" } method
           && method.ContainingType is { Name: "ByRef", IsGenericType: true } type
           && type.Arity == 1
           && type.ContainingNamespace?.ToDisplayString() == "Cs2jByRef";

    /// <summary>
    /// Reference-limited residual model fallback: `ByRef&lt;T&gt;.Create&lt;T&gt;(λ, λ)` —
    /// a two-lambda factory invocation whose receiver spells the support type.
    /// </summary>
    private static bool IsSynthesizedShape(
        InvocationExpressionSyntax invocation,
        ArgumentListSyntax argumentList)
        => argumentList.Arguments.Count == 2
           && argumentList.Arguments.All(
               static argument => argument.Expression is AnonymousFunctionExpressionSyntax)
           && invocation.Expression is MemberAccessExpressionSyntax access
           && access.Name.Identifier.ValueText == "Create"
           && access.Expression is GenericNameSyntax { Identifier.ValueText: "ByRef" } generic
           && generic.TypeArgumentList.Arguments.Count == 1;
}
