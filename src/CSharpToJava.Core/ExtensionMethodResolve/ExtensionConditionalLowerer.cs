using CSharpToJava.Core.IndexRange;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExtensionMethodResolve;

/// <summary>
/// Result of lowering all liftable null-conditional chains of one file.
/// </summary>
internal sealed record ConditionalLoweringOutput(
    CompilationUnitSyntax Root,
    List<TempLifter.PendingTemp> Pending,
    List<LambdaExpansion> LambdaExpansions,
    List<VoidAnchorExpansion> VoidExpansions);

/// <summary>
/// EM-COND lowering (post main-rewrite pass): for each liftable outermost
/// chain, rebuilds every segment as explicit static calls guarded by a
/// single-evaluation temp.
///
/// Value shape: <c>var t0 = receiver; var t1 = t0 != null ? Host.Go(t0) :
/// default(R0?); result = t1 != null ? Host.Chain(t1) : default(R1?);</c>
/// Void shape: same temps, then <c>if (tk != null) Host.V(tk);</c> replacing
/// the enclosing expression statement / arrow / expression lambda.
/// </summary>
internal static class ExtensionConditionalLowerer
{
    private static readonly SymbolDisplayFormat QualifiedTypeFormat =
        SymbolDisplayFormat.FullyQualifiedFormat
            .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Included);

    public static ConditionalLoweringOutput Lower(
        CompilationUnitSyntax rootAfterRewrite,
        IReadOnlyList<ExtensionConditionalPlan> liftablePlans,
        IReadOnlyDictionary<SyntaxNode, SyntaxAnnotation> sharedAnchorAnnotations)
    {
        var pending = new List<TempLifter.PendingTemp>();
        var lambdaDecls = new Dictionary<SyntaxNode, List<StatementSyntax>>();
        var lambdaModes = new Dictionary<SyntaxNode, LambdaBodyMode>();
        var voidInputs = new Dictionary<SyntaxNode, (List<StatementSyntax> Decls, ExpressionSyntax Condition, ExpressionSyntax Branch)>();
        var replacements = new List<(SyntaxNode Old, SyntaxNode New)>();
        var order = 0;

        foreach (var plan in liftablePlans.OrderBy(p => p.SpanStart))
        {
            var outerCur = rootAfterRewrite.GetAnnotatedNodes(plan.SiteAnnotation)
                .OfType<ConditionalAccessExpressionSyntax>()
                .FirstOrDefault();
            if (outerCur is null)
            {
                continue;
            }

            var segmentsCur = LocateSegments(outerCur);
            if (segmentsCur.Count != plan.Segments.Count)
            {
                continue;
            }

            var branches = new List<ExpressionSyntax>();
            for (var i = 0; i < plan.Segments.Count; i++)
            {
                var segmentPlan = plan.Segments[i];
                var (_, segmentRoot, _, spineCur) = segmentsCur[i];
                var receiverTemp = plan.TempNames[i];
                var branch = LowerSegment(
                    segmentRoot,
                    spineCur,
                    segmentPlan.SpineCalls,
                    receiverTemp);
                branches.Add(branch);
            }

            // Receiver temp + one temp per intermediate segment result.
            var declarations = new List<StatementSyntax>
            {
                TempLifter.DeclareVar(plan.TempNames[0], outerCur.Expression.WithoutTrivia()),
            };
            for (var i = 0; i + 1 < plan.Segments.Count; i++)
            {
                var segmentType = plan.Segments[i].ResultType!;
                declarations.Add(TempLifter.DeclareVar(
                    plan.TempNames[i + 1],
                    BuildConditional(plan.TempNames[i], branches[i], segmentType)));
            }

            var finalTemp = plan.TempNames[^1];
            var anchorNode = plan.Anchor.Node;

            if (plan.IsVoid)
            {
                voidInputs[anchorNode] = (
                    declarations,
                    BuildGuard(finalTemp),
                    branches[^1]);
            }
            else
            {
                var finalExpression = BuildConditional(finalTemp, branches[^1], plan.ResultType!);
                replacements.Add((outerCur, finalExpression));

                switch (plan.Anchor.Kind)
                {
                    case ExtensionAnchorKind.Statement:
                    case ExtensionAnchorKind.ArrowClause:
                        foreach (var declaration in declarations)
                        {
                            pending.Add(new TempLifter.PendingTemp(declaration, anchorNode, ++order));
                        }

                        break;
                    case ExtensionAnchorKind.ExpressionLambda:
                        if (!lambdaDecls.TryGetValue(anchorNode, out var list))
                        {
                            list = new List<StatementSyntax>();
                            lambdaDecls[anchorNode] = list;
                            lambdaModes[anchorNode] = LambdaBodyMode.Return;
                        }

                        list.AddRange(declarations);
                        break;
                }
            }
        }

        var current = replacements.Count == 0
            ? rootAfterRewrite
            : rootAfterRewrite.ReplaceNodes(
                replacements.Select(r => (SyntaxNode)r.Old),
                (original, _) => replacements.Single(r => r.Old == original).New);

        var lambdaExpansions = lambdaDecls
            .Select(kv => new LambdaExpansion(
                sharedAnchorAnnotations[kv.Key],
                kv.Value,
                lambdaModes[kv.Key]))
            .ToList();

        var voidExpansions = voidInputs
            .Select(kv => new VoidAnchorExpansion(
                sharedAnchorAnnotations[kv.Key],
                kv.Value.Decls,
                kv.Value.Condition,
                kv.Value.Branch))
            .ToList();

        return new ConditionalLoweringOutput(current, pending, lambdaExpansions, voidExpansions);
    }

    /// <summary>Structural current-tree mirror of the binder's segment walk.</summary>
    private static List<(ConditionalAccessExpressionSyntax Conditional, ExpressionSyntax SegmentRoot,
        InvocationExpressionSyntax Origin, List<InvocationExpressionSyntax> Spine)> LocateSegments(
        ConditionalAccessExpressionSyntax outer)
    {
        var result = new List<(ConditionalAccessExpressionSyntax, ExpressionSyntax, InvocationExpressionSyntax, List<InvocationExpressionSyntax>)>();
        var conditional = outer;
        while (true)
        {
            ExpressionSyntax segmentRoot;
            ConditionalAccessExpressionSyntax? next = null;
            if (conditional.WhenNotNull is ConditionalAccessExpressionSyntax nested)
            {
                segmentRoot = nested.Expression;
                next = nested;
            }
            else
            {
                segmentRoot = conditional.WhenNotNull;
            }

            var origin = FindOrigin(segmentRoot);
            if (origin is null)
            {
                break;
            }

            var spine = WalkSpine(segmentRoot, origin);
            result.Add((conditional, segmentRoot, origin, spine));

            if (next is null)
            {
                break;
            }

            conditional = next;
        }

        return result;
    }

    private static InvocationExpressionSyntax? FindOrigin(ExpressionSyntax segmentRoot)
    {
        if (segmentRoot is InvocationExpressionSyntax { Expression: MemberBindingExpressionSyntax } self)
        {
            return self;
        }

        return segmentRoot.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(i => i.Expression is MemberBindingExpressionSyntax);
    }

    private static List<InvocationExpressionSyntax> WalkSpine(
        ExpressionSyntax segmentRoot,
        InvocationExpressionSyntax origin)
    {
        var spine = new List<InvocationExpressionSyntax> { origin };
        var current = origin;
        while (current != segmentRoot)
        {
            if (current.Parent is MemberAccessExpressionSyntax { Expression: { } prefix } memberAccess
                && prefix == current
                && memberAccess.Parent is InvocationExpressionSyntax nextInvocation
                && nextInvocation.Expression == memberAccess)
            {
                current = nextInvocation;
                spine.Add(current);
                continue;
            }

            break;
        }

        return spine;
    }

    /// <summary>Replace one segment's spine invocations with explicit static calls.</summary>
    private static ExpressionSyntax LowerSegment(
        ExpressionSyntax segmentRoot,
        List<InvocationExpressionSyntax> spineCur,
        IReadOnlyList<ConditionalSpineCall> spinePlan,
        string receiverTemp)
    {
        if (spineCur.Count == 1 && spineCur[0] == segmentRoot)
        {
            return BuildSpineCall(spineCur[0], spinePlan[0], SyntaxFactory.IdentifierName(receiverTemp));
        }

        return (ExpressionSyntax)segmentRoot.ReplaceNodes(
            spineCur,
            (original, rewritten) =>
            {
                var index = spineCur.IndexOf(original);
                var receiver = index == 0
                    ? (ExpressionSyntax)SyntaxFactory.IdentifierName(receiverTemp)
                    : ((MemberAccessExpressionSyntax)rewritten.Expression).Expression;
                return BuildSpineCall(rewritten, spinePlan[index], receiver);
            });
    }

    private static InvocationExpressionSyntax BuildSpineCall(
        InvocationExpressionSyntax currentInvocation,
        ConditionalSpineCall call,
        ExpressionSyntax receiver)
    {
        var firstParameter = call.Unreduced.Parameters.Length > 0
            ? call.Unreduced.Parameters[0]
            : null;
        var methodName = currentInvocation.Expression switch
        {
            MemberBindingExpressionSyntax binding => binding.Name,
            MemberAccessExpressionSyntax access => access.Name,
            _ => throw new InvalidOperationException("Unexpected spine invocation shape."),
        };

        return ExtensionStaticCallFactory.Build(
            call.Unreduced,
            methodName.WithoutTrivia(),
            receiver,
            call.ReceiverRefKind,
            firstParameter?.ScopedKind ?? ScopedKind.None,
            currentInvocation.ArgumentList.Arguments,
            new[] { ExtensionCallRewriter.RewrittenAnnotation });
    }

    private static ExpressionSyntax BuildGuard(string temp)
        => SyntaxFactory.BinaryExpression(
            SyntaxKind.NotEqualsExpression,
            SyntaxFactory.IdentifierName(temp),
            SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));

    private static ExpressionSyntax BuildConditional(string temp, ExpressionSyntax branch, ITypeSymbol resultType)
        => SyntaxFactory.ConditionalExpression(
            BuildGuard(temp),
            branch,
            SyntaxFactory.DefaultExpression(
                SyntaxFactory.ParseTypeName(resultType.ToDisplayString(QualifiedTypeFormat))));
}
