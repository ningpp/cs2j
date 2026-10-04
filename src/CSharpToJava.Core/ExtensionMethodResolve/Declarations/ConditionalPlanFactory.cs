using CSharpToJava.Core.MemberDesugar;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExtensionMethodResolve.Declarations;

/// <summary>
/// Builds synthetic C19 <see cref="ExtensionConditionalPlan"/>s for liftable
/// null-conditional block sites (M3 coverage):
/// <list type="bullet">
/// <item><c>r?.M()</c>, including multi-segment <c>r?.M()?.N()</c>;</item>
/// <item><c>r?.P</c> and read chains <c>r?.P.B</c>, <c>r?.M().P</c>
/// (extension prefix plus classic or extended tails);</item>
/// <item>statement-position writes <c>r?.P = v</c> and compounds
/// <c>r?.P += v</c>.</item>
/// </list>
/// Plans lower through the existing <see cref="ExtensionConditionalLowerer"/>;
/// extension property shapes are made spine-walkable by seam transforms
/// (member-binding/access/assignment → member-binding getter/setter
/// invocations, with explicit block-parent host overrides).
/// </summary>
internal static class ConditionalPlanFactory
{
    internal sealed record BuildResult(
        ExtensionConditionalPlan? Plan,
        string? SkipReason);

    /// <summary>Shared inputs for one analyzer's builds.</summary>
    internal sealed record BuildContext(
        SemanticModel Model,
        ExtensionTempAllocator Allocator,
        IDictionary<ConditionalSpineCall, NameSyntax> HostOverrides,
        IList<ConditionalSeamNode> Seams,
        Func<ISymbol, (MemberPlan Plan, BlockPlan Block)?> Resolve)
    {
        public (PropertyMemberPlan Member, BlockPlan Block)? ResolveProperty(IPropertySymbol symbol)
            => Resolve(symbol.OriginalDefinition) is { Plan: PropertyMemberPlan property } hit
                ? (property, hit.Block)
                : null;
    }

    /// <summary>Pending seam specification: registered with the plan once it exists.</summary>
    private sealed record SeamSpec(SyntaxNode Original, SyntaxNode Replacement, SyntaxAnnotation Annotation);

    // ── Method-invocation conditionals ──────────────────────────────────────

    public static BuildResult BuildMethodPlan(
        ConditionalAccessExpressionSyntax outer,
        InvocationExpressionSyntax firstOrigin,
        IMethodSymbol firstBound,
        MethodMemberPlan firstMember,
        BlockPlan firstBlock,
        BuildContext context)
    {
        var liftable = IsLiftable(outer, firstBlock, context.Model);
        if (!liftable.Liftable)
        {
            return new BuildResult(null, liftable.SkipReason);
        }

        var segments = new List<ExtensionConditionalSegment>();
        var seamSpecs = new List<SeamSpec>();
        var cond = outer;

        // Total segments in the ?. spine; all must resolve to member-binding
        // method invocations or the whole chain stays sugar.
        var expectedSegments = 1;
        var probe = (ExpressionSyntax)outer.WhenNotNull;
        while (probe is ConditionalAccessExpressionSyntax nestedProbe)
        {
            expectedSegments++;
            probe = nestedProbe.WhenNotNull;
        }

        var chainComplete = false;
        while (true)
        {
            ExpressionSyntax segmentRoot;
            ConditionalAccessExpressionSyntax? next = null;
            if (cond.WhenNotNull is ConditionalAccessExpressionSyntax nested)
            {
                segmentRoot = nested.Expression;
                next = nested;
            }
            else
            {
                segmentRoot = cond.WhenNotNull;
            }

            var deepestInvocation = FindMemberBindingInvocation(segmentRoot);
            if (deepestInvocation is null)
            {
                break;
            }

            IMethodSymbol boundMethod;
            BlockPlan methodBlock;
            if (segments.Count == 0)
            {
                boundMethod = firstBound;
                methodBlock = firstBlock;
                if (deepestInvocation != firstOrigin)
                {
                    break;
                }
            }
            else if (context.Model.GetSymbolInfo(deepestInvocation).Symbol is IMethodSymbol
                     {
                         MethodKind: MethodKind.Ordinary,
                     } nextMethod
                     && context.Resolve(nextMethod.OriginalDefinition) is
                     {
                         Plan: MethodMemberPlan,
                     } nextHit)
            {
                boundMethod = nextMethod;
                methodBlock = nextHit.Block;
            }
            else
            {
                break;
            }

            // Bottom-up: member-binding method invocation, then extended
            // property tail accesses.
            var methodName = MethodNameOf(deepestInvocation);
            var futureExpr = BuildMemberBindingInvocation(
                methodName, deepestInvocation.ArgumentList);
            var calls = new List<ConditionalSpineCall>
            {
                MakeCall(context, futureExpr, boundMethod, methodBlock, IsOrigin: true),
            };

            SyntaxNode current = deepestInvocation;
            while (current.Parent is MemberAccessExpressionSyntax tailAccess
                   && tailAccess.Expression == current
                   && tailAccess != segmentRoot)
            {
                if (context.Model.GetSymbolInfo(tailAccess).Symbol is IPropertySymbol tailProperty
                    && context.ResolveProperty(tailProperty) is { } tailHit
                    && !tailHit.Block.HasDeferredSites)
                {
                    var tailGetter = AccessorsToMethods.GetterName(tailHit.Member.Name);
                    var wrapped = SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            futureExpr,
                            SyntaxFactory.IdentifierName(tailGetter)));
                    calls.Add(MakeCall(context, wrapped,
                        tailHit.Member.Symbol.GetMethod!, tailHit.Block, IsOrigin: false));
                    futureExpr = wrapped;
                    current = tailAccess;
                    continue;
                }

                break;
            }

            if (calls.Count > 1)
            {
                seamSpecs.Add(new SeamSpec(
                    current,
                    futureExpr,
                    new SyntaxAnnotation("C49", "ConditionalPropertySeam")));
            }

            segments.Add(new ExtensionConditionalSegment(
                cond,
                segmentRoot,
                calls[0].Invocation,
                calls,
                boundMethod.ReturnType));

            if (next is null)
            {
                chainComplete = true;
                break;
            }

            cond = next;
        }

        if (!chainComplete || segments.Count != expectedSegments)
        {
            return new BuildResult(null, ExtensionBlockMessages.ConditionalDeferred(false));
        }

        return FinalizeChain(context, outer, segments, seamSpecs, liftable, forcedVoid: false, tempKind: "mc");
    }

    // ── Property-prefix conditionals ────────────────────────────────────────

    public static BuildResult BuildPropertyPlan(
        ConditionalAccessExpressionSyntax outer,
        MemberBindingExpressionSyntax firstBinding,
        IPropertySymbol firstBound,
        PropertyMemberPlan firstMember,
        BlockPlan firstBlock,
        BuildContext context)
    {
        var liftable = IsLiftable(outer, firstBlock, context.Model);
        if (!liftable.Liftable)
        {
            return new BuildResult(null, liftable.SkipReason);
        }

        var segmentRoot = outer.WhenNotNull;

        // Locate the innermost extension MemberBinding.
        MemberBindingExpressionSyntax? deepestBinding = null;
        if (segmentRoot is MemberBindingExpressionSyntax selfBinding)
        {
            deepestBinding = selfBinding;
        }
        else if (segmentRoot is MemberAccessExpressionSyntax access)
        {
            var inner = (ExpressionSyntax)access.Expression;
            while (inner is MemberAccessExpressionSyntax innerAccess)
            {
                inner = innerAccess.Expression;
            }

            deepestBinding = inner as MemberBindingExpressionSyntax;
        }

        if (deepestBinding != firstBinding
            || context.Model.GetSymbolInfo(deepestBinding).Symbol is not IPropertySymbol)
        {
            return new BuildResult(null, ExtensionBlockMessages.ConditionalDeferred(true));
        }

        // Inner op: member-binding property → member-binding get_P() invocation.
        var innerGetter = AccessorsToMethods.GetterName(firstMember.Name);
        var futureExpr = BuildMemberBindingInvocation(
            SyntaxFactory.IdentifierName(innerGetter),
            SyntaxFactory.ArgumentList());
        var calls = new List<ConditionalSpineCall>
        {
            MakeCall(context, futureExpr, firstBound.GetMethod!, firstBlock, IsOrigin: true),
        };

        // Walk outward through MemberAccess tails.
        var current = (ExpressionSyntax)deepestBinding;
        while (current.Parent is MemberAccessExpressionSyntax tailAccess
               && tailAccess.Expression == current
               && tailAccess != segmentRoot)
        {
            if (context.Model.GetSymbolInfo(tailAccess).Symbol is IPropertySymbol tailProperty
                && context.ResolveProperty(tailProperty) is { } tailHit
                && !tailHit.Block.HasDeferredSites)
            {
                var tailGetter = AccessorsToMethods.GetterName(tailHit.Member.Name);
                var wrapped = SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        futureExpr,
                        SyntaxFactory.IdentifierName(tailGetter)));
                calls.Add(MakeCall(context, wrapped,
                    tailHit.Member.Symbol.GetMethod!, tailHit.Block, IsOrigin: false));
                futureExpr = wrapped;
                current = tailAccess;
                continue;
            }

            break;
        }

        // One seam covering the full prefix.
        var seamSpecs = new List<SeamSpec>
        {
            new(current,
                futureExpr,
                new SyntaxAnnotation("C49", "ConditionalPropertySeam")),
        };

        var segment = new ExtensionConditionalSegment(
            outer,
            segmentRoot,
            calls[0].Invocation,
            calls,
            firstBound.Type);
        var segments = new List<ExtensionConditionalSegment> { segment };

        return FinalizeChain(context, outer, segments, seamSpecs, liftable, forcedVoid: false, tempKind: "pc");
    }

    // ── Writes / compounds (statement position) ─────────────────────────────

    public static BuildResult BuildPropertyWritePlan(
        ConditionalAccessExpressionSyntax outer,
        AssignmentExpressionSyntax assignment,
        IPropertySymbol bound,
        PropertyMemberPlan member,
        BlockPlan block,
        SyntaxKind compoundKind,
        BuildContext context)
    {
        var compound = compoundKind != SyntaxKind.None;
        if (outer.Parent is not ExpressionStatementSyntax)
        {
            return new BuildResult(null,
                ExtensionBlockMessages.ConditionalWriteDeferred(compound ? "compound assignment" : "assignment"));
        }

        var liftable = IsLiftable(outer, block, context.Model);
        if (!liftable.Liftable)
        {
            return new BuildResult(null, liftable.SkipReason);
        }

        var setterName = AccessorsToMethods.SetterName(member.Name);
        var setBinding = SyntaxFactory.MemberBindingExpression(
            SyntaxFactory.Token(SyntaxKind.DotToken),
            SyntaxFactory.IdentifierName(setterName));

        var extras = new List<(InvocationExpressionSyntax Invocation, ConditionalSpineCall Call)>();

        ExpressionSyntax valueExpression;
        if (compound)
        {
            var getterName = AccessorsToMethods.GetterName(member.Name);
            var read = BuildMemberBindingInvocation(
                SyntaxFactory.IdentifierName(getterName),
                SyntaxFactory.ArgumentList());
            var readCall = MakeCall(context, read, bound.GetMethod!, block, IsOrigin: false);
            extras.Add((read, readCall));

            var binaryKind = CompoundBinaryMapping.BinaryKind(compoundKind);
            valueExpression = SyntaxFactory.BinaryExpression(
                binaryKind, read, assignment.Right.WithoutTrivia());
        }
        else
        {
            valueExpression = assignment.Right.WithoutTrivia();
        }

        var future = SyntaxFactory.InvocationExpression(
            setBinding,
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(valueExpression))));
        var setCall = MakeCall(context, future, bound.SetMethod!, block, IsOrigin: false);

        var segment = new ExtensionConditionalSegment(
            outer,
            future,
            future,
            new[] { setCall }.Concat(extras.Select(e => e.Call)).ToList(),
            bound.Type)
        {
            ExtraSpine = extras,
        };
        var segments = new List<ExtensionConditionalSegment> { segment };

        var seamSpecs = new List<SeamSpec>
        {
            new(assignment,
                future,
                new SyntaxAnnotation("C49", "ConditionalPropertySeam")),
        };

        return FinalizeChain(context, outer, segments, seamSpecs, liftable, forcedVoid: true, tempKind: "pc");
    }

    // ── Shared assembly ─────────────────────────────────────────────────────

    private static BuildResult FinalizeChain(
        BuildContext context,
        ConditionalAccessExpressionSyntax outer,
        List<ExtensionConditionalSegment> segments,
        List<SeamSpec> seamSpecs,
        LiftVerdict liftable,
        bool forcedVoid,
        string tempKind)
    {
        if (segments.Count == 0)
        {
            return new BuildResult(null, ExtensionBlockMessages.ConditionalDeferred(false));
        }

        // Receiver temp + one temp per intermediate segment result.
        var tempNames = Enumerable.Range(0, segments.Count)
            .Select(_ => context.Allocator.Allocate(tempKind))
            .ToList();

        var finalType = context.Model.GetTypeInfo(outer).Type;
        // Match the classic C19 factory: when the chain's static type is
        // T? (value type), the plan carries T itself (the lowered false
        // branch is default(T); the ternary's int result converts to int?).
        if (finalType is INamedTypeSymbol nullable
            && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            finalType = nullable.TypeArguments[0];
        }

        var isVoid = forcedVoid;

        var plan = new ExtensionConditionalPlan
        {
            OuterConditional = outer,
            Segments = segments,
            IsVoid = isVoid,
            ResultType = isVoid ? null : finalType ?? segments[^1].ResultType,
            Liftable = true,
            Anchor = liftable.Anchor,
            TempNames = tempNames,
            SiteAnnotation = new SyntaxAnnotation("C19", "ExtensionConditionalSite"),
            AnchorAnnotation = new SyntaxAnnotation("C19", "ExtensionConditionalAnchor"),
        };

        foreach (var spec in seamSpecs)
        {
            context.Seams.Add(new ConditionalSeamNode(
                spec.Original, spec.Replacement, spec.Annotation, plan));
        }

        return new BuildResult(plan, null);
    }

    private static ConditionalSpineCall MakeCall(
        BuildContext context,
        InvocationExpressionSyntax node,
        IMethodSymbol symbol,
        BlockPlan block,
        bool IsOrigin)
    {
        var call = new ConditionalSpineCall(
            node,
            symbol,
            symbol,
            block.Receiver.RefKind,
            IsOrigin);
        context.HostOverrides[call] = ExtensionHostSyntaxFactory.BuildHostName(block.Host);
        return call;
    }

    private static SimpleNameSyntax MethodNameOf(InvocationExpressionSyntax invocation)
        => invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name,
            MemberBindingExpressionSyntax memberBinding => memberBinding.Name,
            _ => throw new InvalidOperationException(
                "Conditional block method origin must be a member-access or member-binding invocation."),
        };

    private static InvocationExpressionSyntax BuildMemberBindingInvocation(
        SimpleNameSyntax name,
        ArgumentListSyntax argumentList)
    {
        var binding = SyntaxFactory.MemberBindingExpression(
            SyntaxFactory.Token(SyntaxKind.DotToken),
            name.WithoutTrivia());
        return SyntaxFactory.InvocationExpression(binding, argumentList);
    }

    private static InvocationExpressionSyntax? FindMemberBindingInvocation(ExpressionSyntax root)
    {
        if (root is InvocationExpressionSyntax { Expression: MemberBindingExpressionSyntax } self)
        {
            return self;
        }

        return root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(i => i.Expression is MemberBindingExpressionSyntax);
    }

    private sealed record LiftVerdict(
        bool Liftable,
        ExtensionAnchor Anchor,
        string? SkipReason);

    private static LiftVerdict IsLiftable(
        ConditionalAccessExpressionSyntax outer,
        BlockPlan block,
        SemanticModel model)
    {
        if (ExtensionExpressionTreeFacts.IsInExpressionTreeLambda(outer, model))
        {
            return new LiftVerdict(false, ExtensionAnchor.None, ExtensionBlockMessages.ConditionalDeferred(false));
        }

        var anchor = ExtensionTempLift.ResolveAnchor(outer, model);
        if (anchor.Kind == ExtensionAnchorKind.None)
        {
            return new LiftVerdict(false, anchor, ExtensionBlockMessages.ConditionalDeferred(false));
        }

        if (!ExtensionTempLift.IsAtDominantExpressionPosition(outer))
        {
            return new LiftVerdict(false, anchor, ExtensionBlockMessages.ConditionalDeferred(false));
        }

        if (block.Receiver.RefKind != RefKind.None)
        {
            return new LiftVerdict(false, anchor, ExtensionBlockMessages.ConditionalDeferred(false));
        }

        return new LiftVerdict(true, anchor, null);
    }
}
