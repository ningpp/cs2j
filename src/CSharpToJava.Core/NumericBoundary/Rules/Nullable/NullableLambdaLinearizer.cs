using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NBOp = Microsoft.CodeAnalysis.Operations;
using NB = CSharpToJava.Core.NumericBoundary.NumericBoundarySyntaxFacts;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.NumericBoundary.Rules.Nullable;

/// <summary>
/// C22 expression-lambda statement linearization (CS2J5355 eradication for the
/// DebuggerAttributes.cs predicate shapes):
/// <code>
/// p => c0 &amp;&amp; c1 &amp;&amp; … &amp;&amp; ck &amp;&amp; …
/// </code>
/// where lifted <c>Nullable&lt;T&gt;</c> sites live in component <c>ck</c>
/// (the leftmost component containing any such site) becomes
/// <code>
/// p =>
/// {
///     if (!(c0)) return false;
///     if (!(c1)) return false;
///     …
///     return ck &amp;&amp; …;
/// }
/// </code>
/// The guard components keep their original short-circuit evaluation order and
/// frequency; once control reaches <c>return ck &amp;&amp; …</c>, every
/// always-evaluated slot of that tail behaves exactly as the right arm of the
/// original conjunction. The lifted sites now have a whitelisted return
/// anchor inside a block scope that Pass2 can splice into. The rewrite is
/// applied speculatively together with the whole rule: if any site in the file
/// stays un-hoistable, the NB-12 file rollback restores the original bytes.
/// </summary>
internal static class NullableLambdaLinearizer
{
    public static CompilationUnitSyntax Linearize(
        CompilationUnitSyntax root,
        SemanticModel model,
        out bool changed)
    {
        changed = false;

        var replacements = new Dictionary<AnonymousFunctionExpressionSyntax, BlockSyntax>();
        foreach (var lambda in root.DescendantNodes().OfType<AnonymousFunctionExpressionSyntax>())
        {
            if (lambda.Body is not ExpressionSyntax body)
            {
                // Already a statement lambda — ordinary anchors exist inside.
                continue;
            }

            if (IsExpressionTreeArgument(lambda, model))
            {
                // Statement lambdas cannot be converted to Expression<TDelegate>.
                continue;
            }

            var components = FlattenConjunction(body);
            if (components.Count < 2)
            {
                continue;
            }

            var firstSiteComponent = FindFirstLiftedSiteComponent(lambda, components, model);
            if (firstSiteComponent < 0)
            {
                continue;
            }

            replacements[lambda] = BuildBlock(components, firstSiteComponent);
            changed = true;
        }

        if (!changed)
        {
            return root;
        }

        return (CompilationUnitSyntax)root.ReplaceNodes(
            replacements.Keys,
            (lambda, _) => lambda.WithBody(replacements[lambda]));
    }

    /// <summary>
    /// Flatten a left-associative <c>&amp;&amp;</c> chain into evaluation
    /// order (outer parens stripped). Returns a single-element list for a
    /// non-conjunction expression.
    /// </summary>
    private static List<ExpressionSyntax> FlattenConjunction(ExpressionSyntax body)
    {
        var components = new List<ExpressionSyntax>();
        Collect(body);
        return components;

        void Collect(ExpressionSyntax expression)
        {
            var unwrapped = NullableLiftSyntaxFacts.UnwrapParens(expression);
            if (unwrapped is BinaryExpressionSyntax binary
                && binary.IsKind(SyntaxKind.LogicalAndExpression))
            {
                Collect(binary.Left);
                Collect(binary.Right);
            }
            else
            {
                components.Add(expression);
            }
        }
    }

    /// <summary>
    /// Index of the leftmost conjunction component that contains a built-in
    /// lifted <c>Nullable&lt;T&gt;</c> operation this rule rewrites; -1 when
    /// the lambda body has none. Nested lambdas/local functions inside the
    /// body are opaque (their sites do not belong to this lambda).
    /// </summary>
    private static int FindFirstLiftedSiteComponent(
        AnonymousFunctionExpressionSyntax lambda,
        IReadOnlyList<ExpressionSyntax> components,
        SemanticModel model)
    {
        var first = -1;
        foreach (var node in EnumerateAnalysisNodes((ExpressionSyntax)lambda.Body))
        {
            // Mirror the rewriter's site entry: any lifted built-in/user
            // binary or unary (ClassifyBinary/ClassifyUnary gate on IsLifted).
            // Genuinely user-defined operators (CS2J5353) make the speculative
            // transform fail the file rollback exactly as without it.
            var operation = model.GetOperation(node);
            var isLiftedSite = operation switch
            {
                NBOp.IBinaryOperation binary => binary.IsLifted,
                NBOp.IUnaryOperation unary => unary.IsLifted,
                _ => false,
            };
            if (operation?.Syntax == null || !isLiftedSite)
            {
                continue;
            }

            for (var i = 0; i < components.Count; i++)
            {
                if (components[i].Span.Contains(operation.Syntax.Span))
                {
                    if (first < 0 || i < first)
                    {
                        first = i;
                    }

                    break;
                }
            }
        }

        return first;
    }

    private static IEnumerable<SyntaxNode> EnumerateAnalysisNodes(SyntaxNode body)
    {
        foreach (var node in body.DescendantNodesAndSelf(n =>
                     n is not AnonymousFunctionExpressionSyntax
                     and not LocalFunctionStatementSyntax))
        {
            yield return node;
        }
    }

    private static bool IsExpressionTreeArgument(
        AnonymousFunctionExpressionSyntax lambda,
        SemanticModel model)
    {
        var converted = model.GetTypeInfo(lambda).ConvertedType as INamedTypeSymbol;
        return converted is { IsAnonymousType: false }
            && converted.OriginalDefinition.Name == "Expression"
            && converted.OriginalDefinition.ContainingNamespace.ToDisplayString()
                == "System.Linq.Expressions"
            && converted.TypeArguments.Length == 1;
    }

    private static BlockSyntax BuildBlock(
        IReadOnlyList<ExpressionSyntax> components,
        int firstSiteComponent)
    {
        var statements = new List<StatementSyntax>();

        for (var i = 0; i < firstSiteComponent; i++)
        {
            statements.Add(IfStatement(
                PrefixUnaryExpression(
                    SyntaxKind.LogicalNotExpression,
                    ParenthesizedExpression(components[i])),
                ReturnStatement(LiteralExpression(SyntaxKind.FalseLiteralExpression))));
        }

        ExpressionSyntax tail = components[firstSiteComponent];
        for (var i = firstSiteComponent + 1; i < components.Count; i++)
        {
            tail = BinaryExpression(
                SyntaxKind.LogicalAndExpression,
                tail,
                components[i]);
        }

        statements.Add(ReturnStatement(tail));

        // Synthesized keyword tokens carry no whitespace: a raw ToFullString
        // round-trip would glue `return false` into the identifier
        // `returnfalse` (lexically legal, so the parse-only rebind gate
        // accepts it, but member lookup then fails and the lifted site binds
        // non-lifted). Canonicalize the whole new subtree.
        return Block(statements)
            .WithAdditionalAnnotations(
                NB.RewriteAnnotation,
                new SyntaxAnnotation(NullableLiftSyntaxFacts.RewriteAnnotationKind))
            .NormalizeWhitespace();
    }
}
