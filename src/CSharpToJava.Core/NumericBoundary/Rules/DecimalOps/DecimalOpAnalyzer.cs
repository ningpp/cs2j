using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.NumericBoundary.Rules.DecimalOps;

/// <summary>
/// Planning helpers for C34: hoist-anchor classification (DC-11, same whitelist
/// discipline as the C15 postfix hoist), collision-free temporary names, the
/// const-decimal downgrade fixpoint (DC-12) and E09 support-surface detection.
/// </summary>
internal static class DecimalOpAnalyzer
{
    public enum AnchorKind
    {
        /// <summary><c>R.P += x;</c> — the compound is the whole expression statement.</summary>
        Standalone,

        /// <summary>Value is consumed (return/throw/yield/local initializer); hoist + value temp.</summary>
        Consumed,

        /// <summary>No safe hoist anchor — CS2J5365.</summary>
        None,
    }

    /// <summary>
    /// Classify the hoist anchor for a complex-target compound assignment.
    /// Conditions, loop/using/fixed initializers, lambdas, local functions,
    /// query clauses and initializers cannot host hoisted statements without
    /// changing evaluation/capture semantics.
    /// </summary>
    public static AnchorKind ClassifyAnchor(SyntaxNode compoundNode)
    {
        // Member-level expression-bodied sites (`public decimal M(...) => r.P += x;`)
        // have no enclosing statement: the arrow clause is the anchor. Its body
        // is converted to a block with the hoisted prelude; value-returning
        // members finish with `return <value>`, void members with the write.
        var arrow = compoundNode.Ancestors()
            .TakeWhile(static ancestor => ancestor is not StatementSyntax)
            .OfType<ArrowExpressionClauseSyntax>()
            .FirstOrDefault();
        if (arrow != null)
        {
            var scopeBlocked = compoundNode.Ancestors()
                .TakeWhile(ancestor => ancestor != arrow)
                .Any(static ancestor => ancestor is
                    AnonymousFunctionExpressionSyntax
                    or LocalFunctionStatementSyntax
                    or QueryExpressionSyntax
                    or InitializerExpressionSyntax);
            if (scopeBlocked)
            {
                return AnchorKind.None;
            }

            return ArrowYieldsValue(arrow) ? AnchorKind.Consumed : AnchorKind.Standalone;
        }

        var enclosing = compoundNode.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
        if (enclosing == null)
        {
            return AnchorKind.None;
        }

        // Anything between the site and the anchor statement that introduces a
        // new evaluation scope forbids hoisting into that statement.
        var blocked = compoundNode.Ancestors()
            .TakeWhile(ancestor => ancestor != enclosing)
            .Any(ancestor => ancestor is
                AnonymousFunctionExpressionSyntax
                or LocalFunctionStatementSyntax
                or QueryExpressionSyntax
                or InitializerExpressionSyntax
                or ArrowExpressionClauseSyntax);

        if (blocked)
        {
            return AnchorKind.None;
        }

        switch (enclosing)
        {
            case ExpressionStatementSyntax expressionStatement:
                return UnwrapParentheses(expressionStatement.Expression) == compoundNode
                    ? AnchorKind.Standalone
                    : AnchorKind.None;
            case ReturnStatementSyntax:
            case ThrowStatementSyntax:
            case YieldStatementSyntax { ReturnOrBreakKeyword.RawKind: (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.YieldKeyword }:
                return AnchorKind.Consumed;
            case LocalDeclarationStatementSyntax declaration:
                return declaration.Parent is ForStatementSyntax
                    or UsingStatementSyntax
                    or FixedStatementSyntax
                    ? AnchorKind.None
                    : AnchorKind.Consumed;
            default:
                return AnchorKind.None;
        }
    }

    /// <summary>
    /// Whether an expression-bodied member yields a value (its block ends in
    /// <c>return</c>) or is void (block ends with an expression statement).
    /// </summary>
    public static bool ArrowYieldsValue(ArrowExpressionClauseSyntax arrow)
    {
        switch (arrow.Parent)
        {
            case MethodDeclarationSyntax method:
                return method.ReturnType is not Microsoft.CodeAnalysis.CSharp.Syntax.PredefinedTypeSyntax
                    { Keyword.RawKind: (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.VoidKeyword };
            case AccessorDeclarationSyntax accessor:
                return accessor.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.GetAccessorDeclaration);
            case PropertyDeclarationSyntax:
            case IndexerDeclarationSyntax:
            case OperatorDeclarationSyntax:
            case ConversionOperatorDeclarationSyntax:
                return true;
            default:
                return false;
        }
    }

    public static ExpressionSyntax UnwrapParentheses(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }

    // ── const-decimal downgrade planning (DC-12) ──

    /// <summary>
    /// Compute the set of const-decimal declarators that must be downgraded:
    /// seed = initializer holds a built-in decimal operator C34 rewrites;
    /// cascade = another const decimal references a seeded symbol. Pure
    /// literal constants stay <c>const</c>.
    /// </summary>
    public static HashSet<SyntaxNode> PlanConstDowngrades(SemanticModel model, SyntaxNode root)
    {
        var declarators = DecimalTypeFacts
            .EnumerateConstDecimalDeclarators(model, root)
            .ToList();

        var seeded = new HashSet<SyntaxNode>();
        var seededSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        foreach (var (declarator, symbol, value) in declarators)
        {
            if (value.DescendantNodesAndSelf().Any(node =>
                    DecimalTypeFacts.IsOwnedDecimalOperatorNode(model, node)))
            {
                seeded.Add(declarator);
                seededSymbols.Add(symbol);
            }
        }

        var declaratorBySymbol = new Dictionary<ISymbol, SyntaxNode>(SymbolEqualityComparer.Default);
        foreach (var (declarator, symbol, _) in declarators)
        {
            declaratorBySymbol[symbol] = declarator;
        }

        // Fixpoint over const-to-const references in both directions:
        //  * a const whose initializer references a downgraded const must itself
        //    be downgraded (it reads a runtime value, e.g. E = Other.K);
        //  * every const referenced BY a downgraded initializer is downgraded
        //    too (e.g. B = A * 2m drags A with it).
        var grew = true;
        while (grew)
        {
            grew = false;
            foreach (var (declarator, symbol, value) in declarators)
            {
                var references = value.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                    .Select(name => model.GetSymbolInfo(name).Symbol)
                    .Where(referenced => referenced != null)
                    .ToHashSet(SymbolEqualityComparer.Default);

                var downgrade = false;
                if (!seeded.Contains(declarator))
                {
                    downgrade = references.Any(seededSymbols.Contains);
                }

                if (seeded.Contains(declarator))
                {
                    foreach (var referenced in references)
                    {
                        if (declaratorBySymbol.TryGetValue(referenced!, out var other)
                            && seeded.Add(other))
                        {
                            seededSymbols.Add(referenced!);
                            grew = true;
                        }
                    }
                }

                if (downgrade && seeded.Add(declarator))
                {
                    seededSymbols.Add(symbol);
                    grew = true;
                }
            }
        }

        return seeded;
    }

    // ── E09 support surface ──

    public static bool HasUnsignedMathSupport(Compilation compilation, string methodName)
    {
        var helper = compilation.GetTypeByMetadataName("Cs2jUnsigned.UnsignedMath");
        return helper != null
            && helper.GetMembers(methodName).Any(m => m.Kind == SymbolKind.Method);
    }
}

/// <summary>
/// Allocates <c>__cs2j_dec_*</c> temporary names, reserving every identifier
/// token already present in the tree so user identifiers can never collide.
/// </summary>
internal sealed class TempNameAllocator
{
    private readonly HashSet<string> _used;
    private int _argIndex;

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

    public string Receiver() => Allocate(DecimalOpSyntaxFacts.ReceiverTempName);

    public string NextArgument() => Allocate($"{DecimalOpSyntaxFacts.ArgTempPrefix}{_argIndex++}");

    public string Value() => Allocate(DecimalOpSyntaxFacts.ValueTempName);

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
