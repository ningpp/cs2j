using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.NumericBoundary.Rules.Nullable;

/// <summary>
/// Static facts/codegen helpers for C22 (design §4.5): per-role temporary name
/// prefixes (<c>__cs2j_n_*</c>), the rule's own syntax annotations, whole-scope
/// collision avoidance, and the liftable-context/anchor whitelist skeletons.
/// M0 ships the pure helpers and the allocator only; Pass1/Pass2 (Rewriter/
/// Expansion/Lifter) land in M1 and consume these facts unchanged.
/// </summary>
internal static class NullableLiftSyntaxFacts
{
    /// <summary>Every node C22 produced (formatter span selection).</summary>
    public const string RewriteAnnotationKind = "Cs2jNullableLiftRewrite";

    /// <summary>Per-site hoist marker: lifted temporary declarations carry it.</summary>
    public const string HoistAnnotationKind = "Cs2jNullableLiftHoist";

    /// <summary>
    /// C22 guard marker (decision 18): every synthesized <c>?:</c> guard
    /// conditional carries it inside this rule's own trees. The downstream
    /// C15 checked sweep runs on a re-parsed model (annotations do not survive
    /// <c>RebindModel</c>), so it recognizes the guard structurally; the
    /// marker is kept for in-tree identification and diagnostics.
    /// </summary>
    public const string GuardAnnotationKind = "Cs2jNullableLiftGuard";

    public static readonly SyntaxAnnotation RewriteAnnotation = new(RewriteAnnotationKind);
    public static readonly SyntaxAnnotation HoistAnnotation = new(HoistAnnotationKind);
    public static readonly SyntaxAnnotation GuardAnnotation = new(GuardAnnotationKind);

    /// <summary>Common prefix of every C22 hoisted temporary (design §4.5).</summary>
    public const string TempNamePrefix = "__cs2j_n_";

    // ── role names (decision 2 / §4.5) ──

    /// <summary>Left operand / receiver of a lifted site (<c>__cs2j_n_a&lt;N&gt;</c>).</summary>
    public const string RoleLeft = "a";

    /// <summary>Right operand (<c>__cs2j_n_b&lt;N&gt;</c>).</summary>
    public const string RoleRight = "b";

    /// <summary>Postfix old value (<c>__cs2j_n_o&lt;N&gt;</c>, decision 15).</summary>
    public const string RoleOld = "o";

    /// <summary>Get-value-or-default default argument (<c>__cs2j_n_d&lt;N&gt;</c>, decision 10).</summary>
    public const string RoleDefault = "d";

    /// <summary>M3 decision 17: complex RMW target value read once (<c>__cs2j_n_t&lt;N&gt;</c>).</summary>
    public const string RoleTarget = "t";

    /// <summary>M3 decision 17: effectful RMW receiver read once (<c>__cs2j_n_r&lt;N&gt;</c>).</summary>
    public const string RoleReceiver = "r";

    /// <summary>M3 decision 17: indexer/array index argument read once (<c>__cs2j_n_i&lt;N&gt;_&lt;k&gt;</c>).</summary>
    public const string RoleIndex = "i";

    /// <summary>Parentheses-stripped view of an expression.</summary>
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
    /// initializers and attribute arguments — none of those can host a hoisted
    /// temporary declaration (decision 2 / §4.5). M1 Pass2 consumes this; M0
    /// exposes it as a pure predicate skeleton.
    /// </summary>
    public static StatementSyntax? FindAnchorStatement(SyntaxNode node)
        => node.Ancestors().OfType<StatementSyntax>().FirstOrDefault();

    /// <summary>
    /// True when <paramref name="anchor"/> is an unconditional statement that
    /// can host hoisted declarations directly before it (same whitelist spirit
    /// as C17 decision 14 / E03 lifter): expression statements, local
    /// declarations (outside for/using/fixed initializers), return/throw and
    /// yield-return statements. M1 may extend the set with evidence.
    /// </summary>
    public static bool IsWhitelistedAnchor(StatementSyntax anchor)
    {
        switch (anchor)
        {
            case ExpressionStatementSyntax expressionStatement:
                return expressionStatement.Parent is not ForStatementSyntax;
            case LocalDeclarationStatementSyntax declaration:
                return declaration.Parent is not
                    (ForStatementSyntax or UsingStatementSyntax or FixedStatementSyntax);
            case ReturnStatementSyntax:
            case ThrowStatementSyntax:
                return true;
            case YieldStatementSyntax yield:
                return yield.ReturnOrBreakKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.YieldKeyword);
            default:
                return false;
        }
    }

    /// <summary>
    /// True when <paramref name="node"/> sits in a context C22 can lift within
    /// (no anonymous-function/local-function/query boundary, and the path to the
    /// anchor crosses no conditional/coalescing/short-circuit arm or
    /// interpolated string). Pure skeleton for M0; M1 adds anchor-existence and
    /// ordering checks (mirrors <c>IncrementTargetSyntaxFacts.IsBlockedPath</c>).
    /// </summary>
    public static bool IsLiftableContext(SyntaxNode node, StatementSyntax anchor)
    {
        foreach (var ancestor in node.Ancestors()
                     .TakeWhile(ancestor => ancestor != anchor))
        {
            switch (ancestor)
            {
                case Microsoft.CodeAnalysis.CSharp.Syntax.AnonymousFunctionExpressionSyntax:
                case Microsoft.CodeAnalysis.CSharp.Syntax.LocalFunctionStatementSyntax:
                case Microsoft.CodeAnalysis.CSharp.Syntax.QueryExpressionSyntax:
                case Microsoft.CodeAnalysis.CSharp.Syntax.InitializerExpressionSyntax:
                case Microsoft.CodeAnalysis.CSharp.Syntax.ArrowExpressionClauseSyntax:
                case Microsoft.CodeAnalysis.CSharp.Syntax.ConditionalExpressionSyntax:
                case Microsoft.CodeAnalysis.CSharp.Syntax.SwitchExpressionSyntax:
                case Microsoft.CodeAnalysis.CSharp.Syntax.InterpolationSyntax:
                    return false;
                case Microsoft.CodeAnalysis.CSharp.Syntax.BinaryExpressionSyntax binary when binary.Kind() is
                    Microsoft.CodeAnalysis.CSharp.SyntaxKind.LogicalAndExpression
                    or Microsoft.CodeAnalysis.CSharp.SyntaxKind.LogicalOrExpression
                    or Microsoft.CodeAnalysis.CSharp.SyntaxKind.CoalesceExpression:
                    return false;
            }
        }

        return true;
    }

    public static string SiteText(SyntaxNode node)
    {
        var text = node.ToString().Replace('\r', ' ').Replace('\n', ' ');
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 80 ? text : text[..77] + "...";
    }

    /// <summary>
    /// Allocates <c>__cs2j_n_{role}&lt;N&gt;</c> temporary names, reserving
    /// every identifier token already present in the scope/tree so user
    /// identifiers can never collide (same discipline as the C17/C34/C15
    /// allocators). M0 constructs it from a syntax root (whole file); M1 may
    /// narrow the reserved set to the enclosing scope.
    /// </summary>
    public sealed class TempNameAllocator
    {
        private readonly HashSet<string> _used;

        public TempNameAllocator(SyntaxNode root)
        {
            _used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var token in root.DescendantTokens())
            {
                if (token.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.IdentifierToken))
                {
                    _used.Add(token.Text);
                }
            }
        }

        /// <summary>Left operand / receiver temporary (<c>__cs2j_n_a&lt;site&gt;</c>).</summary>
        public string Left(int site) => Allocate(RoleLeft, site);

        /// <summary>Right operand temporary (<c>__cs2j_n_b&lt;site&gt;</c>).</summary>
        public string Right(int site) => Allocate(RoleRight, site);

        /// <summary>Postfix old-value temporary (<c>__cs2j_n_o&lt;site&gt;</c>).</summary>
        public string Old(int site) => Allocate(RoleOld, site);

        /// <summary>GOV default-argument temporary (<c>__cs2j_n_d&lt;site&gt;</c>).</summary>
        public string Default(int site) => Allocate(RoleDefault, site);

        /// <summary>Complex RMW target-value temporary (<c>__cs2j_n_t&lt;site&gt;</c>, M3 decision 17).</summary>
        public string Target(int site) => Allocate(RoleTarget, site);

        /// <summary>Complex RMW receiver temporary (<c>__cs2j_n_r&lt;site&gt;</c>, M3 decision 17).</summary>
        public string Receiver(int site) => Allocate(RoleReceiver, site);

        /// <summary>Complex RMW index-argument temporary (<c>__cs2j_n_i&lt;k&gt;&lt;site&gt;</c>, M3 decision 17).</summary>
        public string Index(int site, int argument) => Allocate(RoleIndex + argument, site);

        /// <summary>Role-qualified allocation for tests/other roles.</summary>
        public string Allocate(string role, int site)
        {
            var baseName = TempNamePrefix + role + site;
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
