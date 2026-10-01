using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.CollectionExpr;

/// <summary>
/// Emission for C36 (CE-2/3/4/7/9; M1/M2): zero-temp
/// <c>new T[]{...}</c> / <c>new C&lt;T&gt;(){...}</c> and the temp +
/// Add/AddRange/foreach statement sequences (array targets close with
/// <c>ToArray()</c>). Every element must be emitted explicitly —
/// OfType filtering is forbidden.
///
/// M0: skeleton only; never invoked on an execution path. Entry points return
/// null/empty so accidental early wiring fails loudly at the call site instead
/// of producing a partial tree.
/// </summary>
internal static class CollectionExprEmitter
{
    /// <summary>M1 CE-2: zero-temp array creation (empty site → new T[]{}).</summary>
    public static ExpressionSyntax? TryBuildZeroTempArray(CollectionExprPlan plan)
        => null;

    /// <summary>M1 CE-3/4: zero-temp mutable collection initializer.</summary>
    public static ExpressionSyntax? TryBuildZeroTempCollection(CollectionExprPlan plan)
        => null;

    /// <summary>M1 CE-7: statement sequence for spread-bearing sites; array targets ToArray()-close.</summary>
    public static IReadOnlyList<StatementSyntax> BuildStatementSequence(CollectionExprPlan plan)
        => Array.Empty<StatementSyntax>();
}
