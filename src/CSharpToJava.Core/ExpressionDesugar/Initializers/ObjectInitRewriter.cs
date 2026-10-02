using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.Initializers;

/// <summary>
/// C24 object-initializer emitter (design §4 IN-2/IN-4/IN-5): construct
/// statement + ordered property/field/indexer assignment sequence, nested
/// creation and nested member initializers.
///
/// M0 is an idle shell: <see cref="Rewrite"/> returns the input unchanged, so
/// the rule is behaviorally transparent while the family registration lands.
/// </summary>
internal sealed class ObjectInitRewriter
{
    /// <summary>M0: no rewrite (returns the input root unchanged).</summary>
    public CompilationUnitSyntax Rewrite(CompilationUnitSyntax root) => root;
}