using CSharpToJava.Core.Analysis;
using CSharpToJava.Core.IndexRange;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExtensionMethodResolve;

/// <summary>
/// Temp-lift infrastructure for EM-COND/EM-GROUP (single-evaluation
/// temporaries + conditional/static-call lowering). Reuses the design and the
/// actual <see cref="TempLifter"/> of the index/range pass for plain
/// statement/arrow insertion; anchor resolution, name allocation, expression
/// lambda expansion and void shapes are local to this pass.
/// </summary>
internal static class ExtensionTempLift
{
    /// <summary>
    /// Resolve the lift anchor of <paramref name="site"/>. Expression-tree
    /// lambdas never anchor (assert-only); expression lambdas anchor at the
    /// lambda (expanded to a block); block lambdas contribute their inner
    /// statements; query clauses and field initializers anchor nowhere.
    /// </summary>
    public static ExtensionAnchor ResolveAnchor(SyntaxNode site, SemanticModel model)
    {
        for (var current = site.Parent; current is not null; current = current.Parent)
        {
            if (current is QueryExpressionSyntax or QueryBodySyntax or QueryClauseSyntax)
            {
                return ExtensionAnchor.None;
            }

            if (current is LambdaExpressionSyntax lambda)
            {
                if (ExpressionTreeFacts.IsExpressionTreeLambda(model, lambda))
                {
                    return ExtensionAnchor.None;
                }

                if (lambda.Body is BlockSyntax)
                {
                    // Block lambda: keep walking, its statements provide anchors.
                    continue;
                }

                var invoke = (model.GetTypeInfo(lambda).ConvertedType as INamedTypeSymbol)
                    ?.DelegateInvokeMethod;
                return new ExtensionAnchor(
                    lambda,
                    ExtensionAnchorKind.ExpressionLambda,
                    invoke?.ReturnsVoid ?? false);
            }

            if (current is ArrowExpressionClauseSyntax arrow)
            {
                return new ExtensionAnchor(
                    arrow,
                    ExtensionAnchorKind.ArrowClause,
                    IsVoidArrow(arrow, model));
            }

            if (current is StatementSyntax statement)
            {
                return new ExtensionAnchor(statement, ExtensionAnchorKind.Statement, false);
            }
        }

        return ExtensionAnchor.None;
    }

    /// <summary>
    /// Dominant-expression position: hoisting a receiver temp before the
    /// anchor statement preserves evaluation order ONLY when the site IS the
    /// statement's value (local/using declarator, return/yield, arrow, lambda
    /// body, expression statement). Argument/event/initializer/ternary
    /// positions evaluate the receiver conditionally or interleaved with
    /// other operands — those sites must take the direct-capture path or be
    /// reported.
    /// </summary>
    public static bool IsAtDominantExpressionPosition(SyntaxNode site)
    {
        var parent = site.Parent;
        switch (parent)
        {
            case ExpressionStatementSyntax expressionStatement when expressionStatement.Expression == site:
                return true;
            case EqualsValueClauseSyntax { Value: { } value } equals when value == site
                && equals.Parent is VariableDeclaratorSyntax declarator
                && declarator.Parent is VariableDeclarationSyntax:
                return true;
            case ReturnStatementSyntax { Expression: { } returned } when returned == site:
                return true;
            case YieldStatementSyntax { Expression: { } yielded } when yielded == site:
                return true;
            case ArrowExpressionClauseSyntax { Expression: { } arrowExpression } when arrowExpression == site:
                return true;
            case LambdaExpressionSyntax { Body: ExpressionSyntax body } when body == site:
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Direct-capture receiver: <c>(p) => Host.M(r, p)</c> evaluates the
    /// receiver only when the lambda is invoked, so it preserves single
    /// evaluation only when the receiver expression is a bare local/parameter
    /// reference or <c>this</c> (no per-read side effects, no property/indexer
    /// re-evaluation).
    /// </summary>
    public static bool IsDirectCaptureReceiver(ExpressionSyntax receiver, SemanticModel model)
    {
        if (receiver is ThisExpressionSyntax)
        {
            return true;
        }

        if (receiver is IdentifierNameSyntax identifier)
        {
            var symbol = model.GetSymbolInfo(identifier).Symbol;
            return symbol is ILocalSymbol or IParameterSymbol;
        }

        return false;
    }

    /// <summary>Build <c>var name = initializer;</c> (delegates to the shared lifter).</summary>
    public static LocalDeclarationStatementSyntax DeclareVar(string name, ExpressionSyntax initializer)
        => TempLifter.DeclareVar(name, initializer);

    /// <summary>
    /// Expand one expression lambda into a block lambda: declarations, then
    /// return the (already rewritten) body expression — or an expression
    /// statement for a void-valued lambda.
    /// </summary>
    public static CompilationUnitSyntax ExpandExpressionLambdas(
        CompilationUnitSyntax root,
        IReadOnlyCollection<LambdaExpansion> expansions)
    {
        if (expansions.Count == 0)
        {
            return root;
        }

        var current = root;
        foreach (var expansion in expansions)
        {
            var annotated = current.GetAnnotatedNodes(expansion.AnchorAnnotation)
                .OfType<LambdaExpressionSyntax>()
                .FirstOrDefault();
            if (annotated is null || annotated.Body is not ExpressionSyntax body)
            {
                continue;
            }

            var statements = new List<StatementSyntax>(expansion.Declarations);
            statements.Add(expansion.Mode == LambdaBodyMode.Void
                ? SyntaxFactory.ExpressionStatement(body)
                : SyntaxFactory.ReturnStatement(body));
            var block = SyntaxFactory.Block(SyntaxFactory.List(statements));

            LambdaExpressionSyntax replaced = annotated switch
            {
                SimpleLambdaExpressionSyntax simple => simple.WithBody(block),
                ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.WithBody(block),
                _ => annotated,
            };

            current = current.ReplaceNode(annotated, replaced);
        }

        return (CompilationUnitSyntax)current;
    }

    /// <summary>
    /// Replace void-valued anchor statements/arrows with a block carrying the
    /// declarations and the <c>if (temp != null) ...</c> guard. Void chains
    /// never have intermediate segment temps.
    /// </summary>
    public static CompilationUnitSyntax ExpandVoidAnchors(
        CompilationUnitSyntax root,
        IReadOnlyCollection<VoidAnchorExpansion> expansions)
    {
        if (expansions.Count == 0)
        {
            return root;
        }

        var current = root;
        foreach (var expansion in expansions)
        {
            var annotated = current.GetAnnotatedNodes(expansion.AnchorAnnotation).FirstOrDefault();
            if (annotated is null)
            {
                continue;
            }

            var statements = new List<StatementSyntax>(expansion.Declarations)
            {
                SyntaxFactory.IfStatement(
                    expansion.Condition,
                    SyntaxFactory.Block(SyntaxFactory.ExpressionStatement(expansion.Branch))),
            };
            var block = SyntaxFactory.Block(SyntaxFactory.List(statements));

            if (annotated is ExpressionStatementSyntax statement)
            {
                current = current.ReplaceNode(statement, block);
            }
            else if (annotated is ArrowExpressionClauseSyntax arrow)
            {
                current = (CompilationUnitSyntax)ExpandVoidArrow(current, arrow, block);
            }
            else if (annotated is LambdaExpressionSyntax lambda)
            {
                LambdaExpressionSyntax replaced = lambda switch
                {
                    SimpleLambdaExpressionSyntax simple => simple.WithBody(block),
                    ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.WithBody(block),
                    _ => lambda,
                };
                current = current.ReplaceNode(lambda, replaced);
            }
        }

        return (CompilationUnitSyntax)current;
    }

    private static SyntaxNode ExpandVoidArrow(SyntaxNode current, ArrowExpressionClauseSyntax arrow, BlockSyntax block)
    {
        var member = arrow.Parent!;
        SyntaxNode updated = member switch
        {
            MethodDeclarationSyntax m => m.WithExpressionBody(null)
                .WithSemicolonToken(default).WithBody(block),
            LocalFunctionStatementSyntax l => l.WithExpressionBody(null)
                .WithSemicolonToken(default).WithBody(block),
            OperatorDeclarationSyntax o => o.WithExpressionBody(null)
                .WithSemicolonToken(default).WithBody(block),
            ConversionOperatorDeclarationSyntax c => c.WithExpressionBody(null)
                .WithSemicolonToken(default).WithBody(block),
            ConstructorDeclarationSyntax ctor => ctor.WithExpressionBody(null)
                .WithSemicolonToken(default).WithBody(block),
            DestructorDeclarationSyntax dtor => dtor.WithExpressionBody(null)
                .WithSemicolonToken(default).WithBody(block),
            AccessorDeclarationSyntax a => a.WithExpressionBody(null)
                .WithSemicolonToken(default).WithBody(block),
            PropertyDeclarationSyntax p => p.WithExpressionBody(null)
                .WithSemicolonToken(default)
                .WithAccessorList(MakeGetAccessorList(block)),
            IndexerDeclarationSyntax i => i.WithExpressionBody(null)
                .WithSemicolonToken(default)
                .WithAccessorList(MakeGetAccessorList(block)),
            _ => member,
        };

        return current.ReplaceNode(member, updated);
    }

    private static AccessorListSyntax MakeGetAccessorList(BlockSyntax block)
        => SyntaxFactory.AccessorList(
            SyntaxFactory.SingletonList(
                SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                    .WithBody(block)));

    private static bool IsVoidArrow(ArrowExpressionClauseSyntax arrow, SemanticModel model)
    {
        var member = arrow.Parent;
        switch (member)
        {
            case MethodDeclarationSyntax method:
                return model.GetDeclaredSymbol(method) is IMethodSymbol { ReturnsVoid: true };
            case LocalFunctionStatementSyntax localFunction:
                return model.GetDeclaredSymbol(localFunction) is IMethodSymbol { ReturnsVoid: true };
            case OperatorDeclarationSyntax:
            case ConversionOperatorDeclarationSyntax:
            case PropertyDeclarationSyntax:
            case IndexerDeclarationSyntax:
                return false;
            case AccessorDeclarationSyntax accessor:
                return accessor.IsKind(SyntaxKind.SetAccessorDeclaration)
                       || accessor.IsKind(SyntaxKind.AddAccessorDeclaration)
                       || accessor.IsKind(SyntaxKind.RemoveAccessorDeclaration)
                       || accessor.IsKind(SyntaxKind.InitAccessorDeclaration);
            default:
                return false;
        }
    }
}

/// <summary>How an expanded lambda body finishes.</summary>
internal enum LambdaBodyMode
{
    Return,
    Void,
}

/// <summary>One expression-lambda expansion pending after the main rewrite.</summary>
internal sealed record LambdaExpansion(
    SyntaxAnnotation AnchorAnnotation,
    IReadOnlyList<StatementSyntax> Declarations,
    LambdaBodyMode Mode);

/// <summary>One void statement/arrow expansion pending after the main rewrite.</summary>
internal sealed record VoidAnchorExpansion(
    SyntaxAnnotation AnchorAnnotation,
    IReadOnlyList<StatementSyntax> Declarations,
    ExpressionSyntax Condition,
    ExpressionSyntax Branch);

/// <summary>File-wide allocator for synthesized temps/lambda parameters (<c>_cs2j…</c> prefix, ruling ⑤).</summary>
internal sealed class ExtensionTempAllocator
{
    private readonly HashSet<string> _used;
    private int _counter;

    public ExtensionTempAllocator(SyntaxNode root)
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

    /// <summary>Allocates a fresh <c>_cs2j_em&lt;kind&gt;&lt;n&gt;</c> name.</summary>
    public string Allocate(string kind)
    {
        while (true)
        {
            _counter++;
            var name = $"_cs2j_em{kind}{_counter}";
            if (_used.Add(name))
            {
                return name;
            }
        }
    }
}
