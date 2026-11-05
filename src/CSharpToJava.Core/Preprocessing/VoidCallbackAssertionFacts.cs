using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.Preprocessing;

/// <summary>
/// Shared syntax/semantic facts about assertion APIs whose callback argument is
/// executed as a <b>void</b> delegate.
///
/// <c>xUnit</c>/<c>MSTest</c> expose both <c>Action</c> and <c>Func&lt;object&gt;</c>
/// overloads; the callback's value is discarded no matter which one the compiler
/// binds. During preprocessing the test framework metadata is usually absent
/// (the pass compilation only references the project itself), so the converted
/// delegate type cannot be resolved and the callback would be mistaken for a
/// value-returning lambda, emitting an illegal <c>return …</c> tail (CS8030 /
/// a Java "return value from void lambda" error).
///
/// The decision is therefore made by the invoked method's NAME, which keeps it
/// deterministic across both the expression-desugar and the ref/out/in passes.
/// </summary>
internal static class VoidCallbackAssertionFacts
{
    /// <summary>
    /// Assertion APIs whose callback argument is executed as a void delegate:
    /// the callback's value is discarded no matter which overload (Action or
    /// Func&lt;object&gt;) the compiler binds.
    /// </summary>
    private static readonly HashSet<string> VoidCallbackAssertionMethods =
        new(StringComparer.Ordinal)
        {
            "ThrowsException",
            "ThrowsExceptionAsync",
            "Throws",
            "ThrowsAsync",
            "DoesNotThrow",
            "DoesNotThrowAsync",
            "Catch",
            "CatchAsync",
        };

    /// <summary>
    /// True when <paramref name="lambda"/> is an argument of an invocation whose
    /// method name belongs to <see cref="VoidCallbackAssertionMethods"/> (e.g.
    /// <c>Assert.ThrowsException&lt;T&gt;(() =&gt; new Foo { X = 1 })</c>).
    /// Casts/parentheses around the lambda are skipped; other arguments before
    /// the lambda do not matter because the enclosing invocation is inspected.
    /// </summary>
    internal static bool IsVoidCallbackAssertionArgument(
        AnonymousFunctionExpressionSyntax lambda, SemanticModel? model)
    {
        var invocation = EnclosingInvocation(lambda);
        if (invocation == null)
            return false;

        var name = ResolveInvocationMethodName(invocation, model);
        return name != null && VoidCallbackAssertionMethods.Contains(name);
    }

    private static InvocationExpressionSyntax? EnclosingInvocation(
        AnonymousFunctionExpressionSyntax lambda)
    {
        SyntaxNode? node = lambda.Parent;
        while (node is ParenthesizedExpressionSyntax or CastExpressionSyntax)
            node = node.Parent;

        return node is ArgumentSyntax
        {
            Parent.Parent: InvocationExpressionSyntax invocation,
        }
            ? invocation
            : null;
    }

    /// <summary>
    /// Bound method symbol first (handles generic assertion methods); syntactic
    /// method spelling is the fallback when the call does not bind.
    /// </summary>
    private static string? ResolveInvocationMethodName(
        InvocationExpressionSyntax invocation, SemanticModel? model)
    {
        if (model?.GetSymbolInfo(invocation).Symbol is IMethodSymbol method)
            return method.Name;

        return invocation.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            GenericNameSyntax generic => generic.Identifier.Text,
            _ => null,
        };
    }
}
