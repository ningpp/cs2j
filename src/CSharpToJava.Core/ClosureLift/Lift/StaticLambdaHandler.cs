using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// C45 M1 syntax shapes (design §4: Lift/StaticLambdaHandler, D10/D12/D22):
///   - host-level `private static` method with the delegate-derived explicit
///     signature; the lambda body (block or expression) moves verbatim —
///     lock/using/await statements keep their structure (D22);
///   - the explicit delegate creation replacing the lambda node (D8):
///     `new &lt;DelType&gt;(Cs2jLambda_&lt;member&gt;_&lt;k&gt;)`.
/// No display class, no instance, no cached delegate field (D11).
/// </summary>
internal static class StaticLambdaHandler
{
    /// <summary>
    /// Builds the named method from the resolved signature and the received
    /// lambda. `unsafe` is added when the moved body uses pointer syntax —
    /// the method leaves the (possibly) unsafe member context it came from.
    /// Nested PLANNED lambdas inside the body are substituted with their
    /// explicit delegate constructions while the body still lives in the
    /// received tree (SpanStart coordinates valid); SyntaxFactory-composed
    /// copies would re-materialize descendants with fresh positions, so the
    /// substitution must happen BEFORE the move (D8).
    /// </summary>
    public static MethodDeclarationSyntax BuildMethod(
        string methodName,
        ResolvedLambdaSignature signature,
        LambdaExpressionSyntax lambda,
        IReadOnlyDictionary<(int Start, int Kind), StaticLambdaPlan> plans)
    {
        var parameters = syntaxParameters(lambda, signature);

        var modifiers = new List<SyntaxToken>
        {
            SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
            SyntaxFactory.Token(SyntaxKind.StaticKeyword),
        };
        if (UsesPointerSyntax(lambda))
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.UnsafeKeyword));
        if (lambda.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword))
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.AsyncKeyword));

        var returnType = signature.ReturnTypeDisplay is { } type
            ? SyntaxFactory.ParseTypeName(type)
            : SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword));

        var substituter = new NestedSiteSubstituter(plans);

        MethodDeclarationSyntax declaration;
        if (lambda is ParenthesizedLambdaExpressionSyntax { Block: { } parenthesizedBlock })
        {
            declaration = BuildDeclaration(
                methodName, modifiers, returnType, parameters,
                SyntaxFactory.Block(substituter.VisitList(parenthesizedBlock.Statements)),
                expressionBody: null);
        }
        else if (lambda is SimpleLambdaExpressionSyntax { Block: { } simpleBlock })
        {
            declaration = BuildDeclaration(
                methodName, modifiers, returnType, parameters,
                SyntaxFactory.Block(substituter.VisitList(simpleBlock.Statements)),
                expressionBody: null);
        }
        else
        {
            var expressionBody = lambda switch
            {
                ParenthesizedLambdaExpressionSyntax p => p.ExpressionBody,
                SimpleLambdaExpressionSyntax s => s.ExpressionBody,
                _ => throw new InvalidOperationException(
                    "C45 M1: lambda without a block or expression body cannot be lifted."),
            };
            var substituted = substituter.Visit(expressionBody);
            declaration = BuildDeclaration(
                methodName, modifiers, returnType, parameters,
                block: null,
                SyntaxFactory.ArrowExpressionClause(
                    substituted as ExpressionSyntax
                    ?? throw new InvalidOperationException("C45 M1: expression body substitution failed.")));
        }

        return declaration.WithAdditionalAnnotations(ClosureLiftAnnotations.LiftedMethod(methodName));
    }

    /// <summary>`new &lt;DelType&gt;(Cs2jLambda_&lt;member&gt;_&lt;k&gt;)` (D8).</summary>
    public static ObjectCreationExpressionSyntax BuildSiteReplacement(StaticLambdaPlan plan)
        => SyntaxFactory.ObjectCreationExpression(
                SyntaxFactory.ParseTypeName(plan.DelegateTypeDisplay))
            .WithArgumentList(SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName(plan.MethodName)))));

    /// <summary>
    /// Replaces nested planned lambdas inside a to-be-moved body. Positions
    /// are valid received-tree coordinates because the walked nodes still
    /// hang off the received tree.
    /// </summary>
    private sealed class NestedSiteSubstituter : CSharpSyntaxRewriter
    {
        private readonly IReadOnlyDictionary<(int Start, int Kind), StaticLambdaPlan> _plans;

        public NestedSiteSubstituter(IReadOnlyDictionary<(int Start, int Kind), StaticLambdaPlan> plans)
        {
            _plans = plans;
        }

        public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
            => ReplaceOrVisit(node);

        public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
            => ReplaceOrVisit(node);

        private SyntaxNode? ReplaceOrVisit(LambdaExpressionSyntax node)
        {
            if (_plans.TryGetValue((node.SpanStart, node.RawKind), out var plan))
                return BuildSiteReplacement(plan).WithTriviaFrom(node);

            // The SPECIFIC base visit: base.Visit(node) would re-dispatch into
            // this override and loop forever.
            return node is SimpleLambdaExpressionSyntax simple
                ? base.VisitSimpleLambdaExpression(simple)
                : node is ParenthesizedLambdaExpressionSyntax parenthesized
                    ? base.VisitParenthesizedLambdaExpression(parenthesized)
                    : base.Visit(node);
        }
    }

    private static MethodDeclarationSyntax BuildDeclaration(
        string methodName,
        List<SyntaxToken> modifiers,
        TypeSyntax? returnType,
        List<ParameterSyntax> parameters,
        BlockSyntax? block,
        ArrowExpressionClauseSyntax? expressionBody)
    {
        var declaration = SyntaxFactory.MethodDeclaration(
            SyntaxFactory.List<AttributeListSyntax>(),
            SyntaxFactory.TokenList(modifiers),
            returnType,
            explicitInterfaceSpecifier: null,
            SyntaxFactory.Identifier(methodName),
            typeParameterList: null,
            SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters)),
            SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(),
            block,
            expressionBody);

        if (block is null)
            declaration = declaration.WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

        return declaration;
    }

    /// <summary>
    /// Parameter pairing: the lambda's own parameter supplies the name (D12
    /// unnamed/discard synthesis is M3 — the stage refuses nameless or
    /// colliding `_` parameters before reaching here), the delegate's
    /// InvokeMethod supplies the explicit type.
    /// </summary>
    private static List<ParameterSyntax> syntaxParameters(
        LambdaExpressionSyntax lambda,
        ResolvedLambdaSignature signature)
    {
        IReadOnlyList<ParameterSyntax> lambdaParameters = lambda switch
        {
            ParenthesizedLambdaExpressionSyntax p => p.ParameterList.Parameters,
            SimpleLambdaExpressionSyntax s => new SyntaxList<ParameterSyntax>(s.Parameter),
            _ => default,
        };

        var result = new List<ParameterSyntax>();
        for (var i = 0; i < signature.Parameters.Count; i++)
        {
            var resolved = signature.Parameters[i];
            var name = lambdaParameters.Count > i
                ? lambdaParameters[i].Identifier.ValueText
                : resolved.Name;
            result.Add(SyntaxFactory.Parameter(SyntaxFactory.Identifier(name))
                .WithType(SyntaxFactory.ParseTypeName(resolved.TypeDisplay)));
        }

        return result;
    }

    /// <summary>Pointer usage must keep an unsafe context after the move.</summary>
    private static bool UsesPointerSyntax(LambdaExpressionSyntax lambda)
    {
        var body = lambda switch
        {
            ParenthesizedLambdaExpressionSyntax p => (SyntaxNode?)p.Block ?? p.ExpressionBody,
            SimpleLambdaExpressionSyntax s => (SyntaxNode?)s.Block ?? s.ExpressionBody,
            _ => null,
        };

        return body is not null
            && body.DescendantNodes().Any(n =>
                n is PointerTypeSyntax
                or FunctionPointerTypeSyntax
                or FixedStatementSyntax
                || (n is PrefixUnaryExpressionSyntax unary
                    && (unary.IsKind(SyntaxKind.PointerIndirectionExpression)
                        || unary.IsKind(SyntaxKind.AddressOfExpression)))
                || (n is MemberAccessExpressionSyntax access
                    && access.IsKind(SyntaxKind.PointerMemberAccessExpression)));
    }
}
