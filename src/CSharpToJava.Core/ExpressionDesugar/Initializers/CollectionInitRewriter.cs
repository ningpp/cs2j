using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.Initializers;

/// <summary>
/// C24 collection-initializer emitter (design §4 IN-3/IN-6/IN-7): constructor
/// arguments preserved verbatim, single/multi-argument, extension, params and
/// fluent <c>Add</c> calls emitted from the bound <c>AddMethod</c>.
///
/// M0 is an idle shell: <see cref="Rewrite"/> returns the input unchanged.
/// </summary>
internal sealed class CollectionInitRewriter
{
    /// <summary>M0: no rewrite (returns the input root unchanged).</summary>
    public CompilationUnitSyntax Rewrite(CompilationUnitSyntax root) => root;
}