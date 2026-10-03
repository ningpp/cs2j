using CSharpToJava.Core.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// C45 M2 body migration (design §4: Lift/LambdaMethodLifter; D9/D21/D22/D5/D7).
/// Builds the display-class instance method `Lambda_&lt;k&gt;` from the RECEIVED-tree
/// lambda: the body moves verbatim (block or expression shape; lock/using/await
/// statements keep their structure, D22) with these rewrites applied during the
/// move:
///   ① C32 field accesses `__cs2jClosure&lt;n&gt;._cs2jCap_x` collapse to implicit-this
///      field accesses `_cs2jCap_x` (the instance local is out of scope inside
///      the nested class; the method's `this` IS the display instance, §4.2);
///   ② read-only capture reads (D5) become field reads, matched by ORIGINAL
///      NAME — CS0136 forbids shadowing a captured name inside the lambda body,
///      so name matching is exact even though C32 rebuilt the body's nodes and
///      shifted absolute spans (external sites keep the original local, D5);
///   ③ explicit `this` (D7) becomes `_cs2jThis`;
///   ④ implicit-this instance-member references (D7) become `_cs2jThis.Member`.
/// `base` access never reaches the lifter (CS2J4509 freezes the file in the
/// planner); quoted subtrees inside a migrated body reference no external state
/// (the planner skips such lambdas, D18②), so ②–④ cannot hit them; nameof
/// arguments, member/binding NAME positions and initializer labels are never
/// variable reads and stay untouched.
/// </summary>
internal static class LambdaMethodLifter
{
    public static MethodDeclarationSyntax BuildMethod(
        LambdaMethodPlan plan,
        ResolvedLambdaSignature signature,
        LambdaExpressionSyntax receivedLambda,
        IReadOnlyDictionary<string, string> readOnlyFieldByOriginalName,
        IReadOnlyCollection<string> instanceMemberNames,
        bool capturesThis,
        ISet<string> instanceFieldNames,
        IReadOnlyDictionary<(int Start, int Kind), StaticLambdaPlan> staticPlans)
    {
        var parameters = StaticLambdaHandler.BuildSyntaxParameters(receivedLambda, signature);

        var modifiers = new List<SyntaxToken>
        {
            SyntaxFactory.Token(SyntaxKind.InternalKeyword),
        };
        if (StaticLambdaHandler.UsesPointerSyntax(receivedLambda))
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.UnsafeKeyword));
        if (plan.IsAsync)
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.AsyncKeyword));

        var returnType = signature.ReturnTypeDisplay is { } type
            ? SyntaxFactory.ParseTypeName(type)
            : SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword));

        var rewriter = new MigratedBodyRewriter(
            plan.InstanceName, instanceFieldNames, readOnlyFieldByOriginalName,
            instanceMemberNames, capturesThis, staticPlans);

        MethodDeclarationSyntax declaration;
        if (receivedLambda is ParenthesizedLambdaExpressionSyntax { Block: { } parenthesizedBlock })
        {
            declaration = BuildDeclaration(
                plan.MethodName, modifiers, returnType, parameters,
                SyntaxFactory.Block(rewriter.VisitList(parenthesizedBlock.Statements)),
                expressionBody: null);
        }
        else if (receivedLambda is SimpleLambdaExpressionSyntax { Block: { } simpleBlock })
        {
            declaration = BuildDeclaration(
                plan.MethodName, modifiers, returnType, parameters,
                SyntaxFactory.Block(rewriter.VisitList(simpleBlock.Statements)),
                expressionBody: null);
        }
        else
        {
            var expressionBody = receivedLambda switch
            {
                ParenthesizedLambdaExpressionSyntax p => p.ExpressionBody,
                SimpleLambdaExpressionSyntax s => s.ExpressionBody,
                _ => throw new InvalidOperationException(
                    "C45 M2: lambda without a block or expression body cannot be lifted."),
            };
            var substituted = rewriter.Visit(expressionBody);
            declaration = BuildDeclaration(
                plan.MethodName, modifiers, returnType, parameters,
                block: null,
                SyntaxFactory.ArrowExpressionClause(
                    substituted as ExpressionSyntax
                    ?? throw new InvalidOperationException("C45 M2: expression body substitution failed.")));
        }

        return declaration.WithAdditionalAnnotations(ClosureLiftAnnotations.LiftedMethod(plan.MethodName));
    }

    private sealed class MigratedBodyRewriter : CSharpSyntaxRewriter
    {
        private readonly string _instanceName;
        private readonly ISet<string> _instanceFieldNames;
        private readonly IReadOnlyDictionary<string, string> _readOnlyFieldByOriginalName;
        private readonly IReadOnlyCollection<string> _instanceMemberNames;
        private readonly bool _capturesThis;
        private readonly IReadOnlyDictionary<(int Start, int Kind), StaticLambdaPlan> _staticPlans;

        public MigratedBodyRewriter(
            string instanceName,
            ISet<string> instanceFieldNames,
            IReadOnlyDictionary<string, string> readOnlyFieldByOriginalName,
            IReadOnlyCollection<string> instanceMemberNames,
            bool capturesThis,
            IReadOnlyDictionary<(int Start, int Kind), StaticLambdaPlan> staticPlans)
        {
            _instanceName = instanceName;
            _instanceFieldNames = instanceFieldNames;
            _readOnlyFieldByOriginalName = readOnlyFieldByOriginalName;
            _instanceMemberNames = instanceMemberNames;
            _capturesThis = capturesThis;
            _staticPlans = staticPlans;
        }

        /// <summary>① `__cs2jClosure&lt;n&gt;._cs2jCap_x` → `_cs2jCap_x` (implicit this).</summary>
        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            if (node.Expression is IdentifierNameSyntax { Identifier.ValueText: { } root }
                && root == _instanceName
                && node.Name is IdentifierNameSyntax { Identifier.ValueText: { } field }
                && _instanceFieldNames.Contains(field))
            {
                return SyntaxFactory.IdentifierName(field).WithTriviaFrom(node);
            }

            return base.VisitMemberAccessExpression(node);
        }

        /// <summary>
        /// Nested PLANNED-STATIC lambda sites inside the moved body are replaced
        /// with their explicit delegate constructions here (M1's build-time
        /// substitution discipline): the site stage rewrites main-tree
        /// occurrences only, so copies relocated into this method must be
        /// resolved while their received-tree spans are still valid.
        /// </summary>
        public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
            => ReplaceNestedStaticOrVisit(node);

        public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
            => ReplaceNestedStaticOrVisit(node);

        private SyntaxNode? ReplaceNestedStaticOrVisit(LambdaExpressionSyntax node)
        {
            if (_staticPlans.TryGetValue((node.SpanStart, node.RawKind), out var staticPlan))
            {
                return StaticLambdaHandler.BuildSiteReplacement(staticPlan).WithTriviaFrom(node);
            }

            return node switch
            {
                SimpleLambdaExpressionSyntax simple => base.VisitSimpleLambdaExpression(simple),
                ParenthesizedLambdaExpressionSyntax parenthesized
                    => base.VisitParenthesizedLambdaExpression(parenthesized),
                _ => base.Visit(node),
            };
        }

        public override SyntaxNode? VisitThisExpression(ThisExpressionSyntax node)
        {
            if (_capturesThis)
                return SyntaxFactory.IdentifierName(ClosureLiftNaming.ThisFieldName).WithTriviaFrom(node);
            return base.VisitThisExpression(node);
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (IsVariableReadPosition(node))
            {
                // ② read-only capture read → field read.
                if (_readOnlyFieldByOriginalName.TryGetValue(node.Identifier.ValueText, out var field))
                    return SyntaxFactory.IdentifierName(field).WithTriviaFrom(node);

                // ④ implicit-this instance member → `_cs2jThis.Member`.
                if (_instanceMemberNames.Contains(node.Identifier.ValueText))
                {
                    return SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName(ClosureLiftNaming.ThisFieldName),
                        SyntaxFactory.IdentifierName(node.Identifier.Text)).WithTriviaFrom(node);
                }
            }

            return base.VisitIdentifierName(node);
        }

        /// <summary>Positions that are never variable reads of the enclosing scope.</summary>
        private static bool IsVariableReadPosition(IdentifierNameSyntax node)
        {
            if (node.Parent is NameColonSyntax || CaptureClassification.IsInsideNameOf(node))
                return false;
            if (node.Parent is MemberAccessExpressionSyntax member && member.Name == node)
                return false;
            if (node.Parent is MemberBindingExpressionSyntax binding && binding.Name == node)
                return false;
            if (node.Parent is NameEqualsSyntax)
                return false;

            // Type contexts: typeof(T), sizeof(T), default(T), new T(), (T)x,
            // x as T, x is T u, T[] — a name in these positions denotes a type.
            if (node.Parent is TypeOfExpressionSyntax
                or SizeOfExpressionSyntax
                or DefaultExpressionSyntax
                or ArrayTypeSyntax
                or GenericNameSyntax
                or QualifiedNameSyntax
                or TypeArgumentListSyntax
                or DeclarationPatternSyntax)
            {
                return false;
            }

            if (node.Parent is CastExpressionSyntax cast && cast.Type == node)
                return false;
            if (node.Parent is ObjectCreationExpressionSyntax creation && creation.Type == node)
                return false;
            if (node.Parent is BinaryExpressionSyntax binary
                && (binary.IsKind(SyntaxKind.IsExpression) || binary.IsKind(SyntaxKind.AsExpression))
                && binary.Right == node)
            {
                return false;
            }

            return true;
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
}
