using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.Initializers;

/// <summary>
/// C24 target-typed <c>new()</c> emitter (design §4 IN-9/IN-10): re-emits an
/// explicit <see cref="ObjectCreationExpressionSyntax"/> from
/// <see cref="TargetTypeInfo"/> (nullable targets stripped to the underlying
/// type), then hands the site to the same plan pipeline.
///
/// M0 is an idle shell: <see cref="Rewrite"/> returns the input unchanged.
/// </summary>
internal sealed class TargetTypedNewRewriter
{
    /// <summary>M0: no rewrite (returns the input root unchanged).</summary>
    public CompilationUnitSyntax Rewrite(CompilationUnitSyntax root) => root;
}