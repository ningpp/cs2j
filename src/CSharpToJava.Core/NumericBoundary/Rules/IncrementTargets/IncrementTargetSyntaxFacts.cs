using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.NumericBoundary.Rules.IncrementTargets;

/// <summary>
/// Static facts/codegen helpers for C17 (design §4.3/§B): annotation kinds,
/// per-site temporary names with whole-file collision avoidance, the
/// value-consumed hoist whitelist (decision 14) and small trivia helpers.
/// </summary>
internal static class IncrementTargetSyntaxFacts
{
    /// <summary>Every node C17 produced (formatter span selection).</summary>
    public const string RewriteAnnotationKind = "Cs2jIncrementTargetsRewrite";

    /// <summary>Per-site hoist marker: placeholders carry the plan instance.</summary>
    public const string HoistAnnotationKind = "Cs2jIncrementTargetsHoist";

    public const string ReceiverTempPrefix = "__cs2j_inc_r";
    public const string ArgTempPrefix = "__cs2j_inc_a";
    public const string IndexTempPrefix = "__cs2j_inc_i";
    public const string OldTempPrefix = "__cs2j_inc_o";
    public const string DiscardTempPrefix = "__cs2j_inc_d";

    /// <summary>IT-9 ref-local alias prefix (design §4.4): explicit <c>ref T</c>.</summary>
    public const string RefTempPrefix = "__cs2j_inc_f";

    /// <summary>Parentheses stripped view of an expression.</summary>
    public static ExpressionSyntax UnwrapParens(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }

    /// <summary>
    /// Nearest enclosing statement of a site. Null inside field/property/ctor
    /// initializers, attribute arguments and expression-tree-shaped member
    /// initializers — none of those can host a hoisted statement.
    /// </summary>
    public static StatementSyntax? FindAnchorStatement(SyntaxNode node)
        => node.Ancestors().OfType<StatementSyntax>().FirstOrDefault();

    /// <summary>
    /// Decision-14 blocked positions between a site and its anchor statement:
    /// anonymous functions, local functions, queries, object/collection/array
    /// initializers, arrow expression bodies, <c>?:</c>/<c>??</c>/<c>&amp;&amp;</c>/
    /// <c>||</c> expressions, switch expressions and string interpolations.
    /// Conditions of if/while/do/for are covered by the anchor statement kind
    /// (the site's nearest statement is the control statement itself).
    /// </summary>
    public static bool IsBlockedPath(SyntaxNode site, StatementSyntax anchor)
    {
        foreach (var ancestor in site.Ancestors().TakeWhile(ancestor => ancestor != anchor))
        {
            switch (ancestor)
            {
                case AnonymousFunctionExpressionSyntax:
                case LocalFunctionStatementSyntax:
                case QueryExpressionSyntax:
                case InitializerExpressionSyntax:
                case ArrowExpressionClauseSyntax:
                case ConditionalExpressionSyntax:
                case SwitchExpressionSyntax:
                case InterpolationSyntax:
                    return true;
                case BinaryExpressionSyntax binary when binary.Kind() is
                    SyntaxKind.LogicalAndExpression
                    or SyntaxKind.LogicalOrExpression
                    or SyntaxKind.CoalesceExpression:
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whitelisted unconditional statement anchors for value-consumed sites
    /// (decision 14): expression statements, return/throw/yield-return and
    /// local declarations outside for/using/fixed initializers.
    /// </summary>
    public static bool IsWhitelistedAnchor(StatementSyntax anchor)
    {
        switch (anchor)
        {
            // A for-statement incrementor is an ExpressionStatement but a hoist
            // block is illegal there; its side effects also re-loop, so v1
            // refuses the anchor (decision 14 conservative gate).
            case ExpressionStatementSyntax expressionStatement:
                return expressionStatement.Parent is not ForStatementSyntax;
            case ReturnStatementSyntax:
            case ThrowStatementSyntax:
                return true;
            case YieldStatementSyntax yield:
                return yield.ReturnOrBreakKeyword.IsKind(SyntaxKind.YieldKeyword);
            case LocalDeclarationStatementSyntax declaration:
                return declaration.Parent is not
                    (ForStatementSyntax or UsingStatementSyntax or FixedStatementSyntax);
            default:
                return false;
        }
    }

    /// <summary>The site IS the whole expression statement (parens allowed).</summary>
    public static bool IsStandaloneExpression(ExpressionSyntax site, ExpressionStatementSyntax statement)
        => UnwrapParens(statement.Expression) == site;

    public static LocalDeclarationStatementSyntax VarDeclaration(
        string name, ExpressionSyntax initializer) =>
        LocalDeclarationStatement(
                VariableDeclaration(
                    IdentifierName("var").WithTrailingTrivia(Space),
                    SingletonSeparatedList(
                        VariableDeclarator(name)
                            .WithInitializer(EqualsValueClause(initializer)))))
            .WithAdditionalAnnotations(
                NumericBoundarySyntaxFacts.RewriteAnnotation,
                new SyntaxAnnotation(RewriteAnnotationKind));

    public static ExpressionStatementSyntax WriteStatement(ExpressionSyntax expression) =>
        ExpressionStatement(expression)
            .WithAdditionalAnnotations(
                NumericBoundarySyntaxFacts.RewriteAnnotation,
                new SyntaxAnnotation(RewriteAnnotationKind));

    /// <summary>
    /// IT-9 ref-local alias declaration: <c>ref T name = ref initializer;</c>.
    /// The type is always explicit (design §4.4 — <c>ref var</c> is never
    /// emitted); the initializer is the single-evaluation READ of the
    /// ref-returning property/indexer/method target.
    /// </summary>
    public static LocalDeclarationStatementSyntax RefLocalDeclaration(
        string name, TypeSyntax aliasType, ExpressionSyntax initializer) =>
        LocalDeclarationStatement(
                VariableDeclaration(
                    RefType(
                        Token(SyntaxKind.RefKeyword).WithTrailingTrivia(Space),
                        aliasType.WithTrailingTrivia(Space)),
                    SingletonSeparatedList(
                        VariableDeclarator(name)
                            .WithInitializer(EqualsValueClause(
                                RefExpression(
                                    Token(SyntaxKind.RefKeyword).WithTrailingTrivia(Space),
                                    initializer))))))
            .WithAdditionalAnnotations(
                NumericBoundarySyntaxFacts.RewriteAnnotation,
                new SyntaxAnnotation(RewriteAnnotationKind));

    public static string SiteText(SyntaxNode node)
    {
        var text = node.ToString().Replace('\r', ' ').Replace('\n', ' ');
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 80 ? text : text[..77] + "...";
    }

    /// <summary>
    /// Allocates <c>__cs2j_inc_*</c> temporary names, reserving every
    /// identifier token already present in the tree so user identifiers can
    /// never collide (same discipline as the C34/C15 allocators).
    /// </summary>
    public sealed class TempNameAllocator
    {
        private readonly HashSet<string> _used;

        public TempNameAllocator(SyntaxNode root)
        {
            _used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in root.DescendantTokens())
            {
                if (token.IsKind(SyntaxKind.IdentifierToken))
                {
                    _used.Add(token.Text);
                }
            }
        }

        public string Receiver(int site) => Allocate($"{ReceiverTempPrefix}{site}");

        public string Argument(int site, int index) =>
            Allocate($"{ArgTempPrefix}{site}_{index}");

        public string Index(int site, int dimension) =>
            Allocate($"{IndexTempPrefix}{site}_{dimension}");

        public string Old(int site) => Allocate($"{OldTempPrefix}{site}");

        public string Discard(int site) => Allocate($"{DiscardTempPrefix}{site}");

        /// <summary>IT-9 ref-local alias name for a site.</summary>
        public string Ref(int site) => Allocate($"{RefTempPrefix}{site}");

        private string Allocate(string baseName)
        {
            if (_used.Add(baseName))
            {
                return baseName;
            }

            for (var suffix = 1; ; suffix++)
            {
                var candidate = baseName + "_" + suffix;
                if (_used.Add(candidate))
                {
                    return candidate;
                }
            }
        }
    }
}
