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
    /// <summary>
    /// Renders the explicit construction type from a symbol. Tuple targets use
    /// their underlying <c>ValueTuple&lt;...&gt;</c> definition: <c>new (T1, T2)</c>
    /// is rejected by the compiler (CS8181), while the target-typed
    /// <c>new(...)</c> it replaces binds the tuple's constructor.
    /// </summary>
    public static TypeSyntax BuildTypeSyntax(
        ITypeSymbol? type, SemanticModel? model = null, int position = -1)
    {
        if (type == null)
        {
            return SyntaxFactory.PredefinedType(
                SyntaxFactory.Token(SyntaxKind.ObjectKeyword));
        }

        if (type is INamedTypeSymbol { IsTupleType: true } tuple)
        {
            // Roslyn exposes a tuple symbol's ValueTuple<T...> form through
            // TupleUnderlyingType, but constructing that definition still
            // renders as a tuple. Build the explicit generic name instead:
            // `new (T1, T2)(...)` is CS8181, `new ValueTuple<T1, T2>(...)` is
            // the same target-typed construction.
            if (tuple.TypeArguments.Length is >= 1 and <= 8)
            {
                var arguments = string.Join(
                    ", ",
                    tuple.TypeArguments.Select(
                        argument => ExpressionDesugarSyntaxFacts
                            .RenderProjectVisibleType(argument, model, position)
                            .ToString()));
                return SyntaxFactory.ParseTypeName(
                    "global::System.ValueTuple<" + arguments + ">");
            }
        }

        return ExpressionDesugarSyntaxFacts.RenderProjectVisibleType(
            type, model, position);
    }
}
