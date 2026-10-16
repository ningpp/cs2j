using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.NumericBoundary;

/// <summary>
/// Token-level profile of the numeric-boundary trigger syntax present in one
/// file tree. Built with a single descendant-token pass and used as a
/// <b>necessary-condition screen</b>: when a flag is false the corresponding
/// family rule provably has no site in this tree, so the rule's semantic
/// traversals can be skipped whole-cluster.
/// <para>
/// 每个 flag 都是「该规则产生站点/诊断的必要语法条件」：
/// <list type="bullet">
/// <item><see cref="HasCheckedOrUncheckedKeyword"/>: C15 only rewrites inside an
/// explicit <c>checked</c>/<c>unchecked</c> scope (or a <c>/checked</c> project);
/// no keyword ⇒ the sweep and its NB-13 residual gate are no-ops.</item>
/// <item><see cref="HasIncrementOrDecrement"/>: C17 only classifies <c>++</c>/<c>--</c>
/// nodes; no token ⇒ <c>FindAllSites</c> is empty.</item>
/// <item><see cref="HasConstKeyword"/>: C34's const-decimal downgrade planning only
/// enumerates <c>const</c> field/local declarators; no token ⇒ empty plan set.</item>
/// </list>
/// </para>
/// </summary>
internal readonly struct NumericBoundaryFileProfile
{
    private NumericBoundaryFileProfile(
        bool hasCheckedOrUncheckedKeyword,
        bool hasIncrementOrDecrement,
        bool hasConstKeyword,
        bool hasAnonymousFunction)
    {
        HasCheckedOrUncheckedKeyword = hasCheckedOrUncheckedKeyword;
        HasIncrementOrDecrement = hasIncrementOrDecrement;
        HasConstKeyword = hasConstKeyword;
        HasAnonymousFunction = hasAnonymousFunction;
    }

    public bool HasCheckedOrUncheckedKeyword { get; }
    public bool HasIncrementOrDecrement { get; }
    public bool HasConstKeyword { get; }

    /// <summary>
    /// A lambda/anonymous-method token is present (a superset: <c>=&gt;</c> also
    /// appears in expression-bodied members and property patterns). Only used to
    /// skip C22's expression-lambda linearizer, which is a no-op without any
    /// anonymous-function syntax.
    /// </summary>
    public bool HasAnonymousFunction { get; }

    public static NumericBoundaryFileProfile Scan(CompilationUnitSyntax root)
    {
        var hasChecked = false;
        var hasIncrement = false;
        var hasConst = false;
        var hasLambda = false;
        foreach (var token in root.DescendantTokens())
        {
            switch (token.Kind())
            {
                case SyntaxKind.CheckedKeyword:
                case SyntaxKind.UncheckedKeyword:
                    hasChecked = true;
                    break;
                case SyntaxKind.PlusPlusToken:
                case SyntaxKind.MinusMinusToken:
                    hasIncrement = true;
                    break;
                case SyntaxKind.ConstKeyword:
                    hasConst = true;
                    break;
                case SyntaxKind.EqualsGreaterThanToken:
                case SyntaxKind.DelegateKeyword:
                    hasLambda = true;
                    break;
            }

            if (hasChecked && hasIncrement && hasConst && hasLambda)
            {
                break;
            }
        }

        return new NumericBoundaryFileProfile(hasChecked, hasIncrement, hasConst, hasLambda);
    }

    /// <summary>
    /// Ordinal text probe over the raw source (no syntax tree). Every trigger
    /// token appears literally in the file, so a hit is necessary; extra hits
    /// from comments/strings only widen the flag (never a false negative), so
    /// this is a strictly cheaper, equally sound lower bound for the initial
    /// tree — the syntax <see cref="Scan"/> is reserved for trees a rule has
    /// just synthesized (e.g. the <c>checked</c> shells C22 emits).
    /// </summary>
    public static NumericBoundaryFileProfile FromSourceText(string sourceCode)
    {
        // "unchecked" contains "checked", so one probe covers both keywords.
        var hasChecked = sourceCode.Contains("checked", StringComparison.Ordinal);
        var hasIncrement =
            sourceCode.Contains("++", StringComparison.Ordinal)
            || sourceCode.Contains("--", StringComparison.Ordinal);
        var hasConst = sourceCode.Contains("const", StringComparison.Ordinal);
        var hasLambda =
            sourceCode.Contains("=>", StringComparison.Ordinal)
            || sourceCode.Contains("delegate", StringComparison.Ordinal);
        return new NumericBoundaryFileProfile(hasChecked, hasIncrement, hasConst, hasLambda);
    }
}
