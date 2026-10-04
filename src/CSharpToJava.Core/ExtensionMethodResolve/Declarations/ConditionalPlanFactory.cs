using CSharpToJava.Core.MemberDesugar;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExtensionMethodResolve.Declarations;

/// <summary>
/// Builds synthetic C19 <see cref="ExtensionConditionalPlan"/>s for liftable
/// null-conditional block sites — <c>r?.M()</c> (EMD-METHOD) and
/// <c>r?.P</c> (EMD-PROP). Plans lower through the existing
/// <see cref="ExtensionConditionalLowerer"/> with an explicit block-parent
/// host override; property sites additionally register a seam transform
/// (member-binding property → member-binding <c>get_X()</c> invocation).
/// </summary>
internal static class ConditionalPlanFactory
{
    internal sealed record BuildResult(
        ExtensionConditionalPlan? Plan,
        string? SkipReason);

    public static BuildResult BuildMethodPlan(
        ConditionalAccessExpressionSyntax outer,
        InvocationExpressionSyntax origin,
        IMethodSymbol bound,
        MethodMemberPlan member,
        BlockPlan block,
        SemanticModel model,
        ExtensionTempAllocator allocator,
        IDictionary<ConditionalSpineCall, NameSyntax> hostOverrides)
    {
        var liftable = IsLiftable(outer, block, model);
        if (!liftable.Liftable)
        {
            return new BuildResult(null, liftable.SkipReason);
        }

        var temp = allocator.Allocate("mc");
        var spineCall = new ConditionalSpineCall(
            origin,
            bound,
            bound,
            block.Receiver.RefKind,
            IsOrigin: true);

        var segment = new ExtensionConditionalSegment(
            outer,
            outer.WhenNotNull,
            origin,
            new[] { spineCall },
            bound.ReturnType);

        var plan = new ExtensionConditionalPlan
        {
            OuterConditional = outer,
            Segments = new[] { segment },
            IsVoid = bound.ReturnsVoid,
            ResultType = bound.ReturnsVoid ? null : bound.ReturnType,
            Liftable = true,
            Anchor = liftable.Anchor,
            TempNames = new[] { temp },
            SiteAnnotation = new SyntaxAnnotation("C19", "ExtensionConditionalSite"),
            AnchorAnnotation = new SyntaxAnnotation("C19", "ExtensionConditionalAnchor"),
        };

        hostOverrides[spineCall] = ExtensionHostSyntaxFactory.BuildHostName(block.Host);
        return new BuildResult(plan, null);
    }

    public static BuildResult BuildPropertyPlan(
        ConditionalAccessExpressionSyntax outer,
        MemberBindingExpressionSyntax binding,
        IPropertySymbol bound,
        PropertyMemberPlan member,
        BlockPlan block,
        SemanticModel model,
        ExtensionTempAllocator allocator,
        IDictionary<ConditionalSpineCall, NameSyntax> hostOverrides,
        IList<ConditionalSeamNode> seams)
    {
        var liftable = IsLiftable(outer, block, model);
        if (!liftable.Liftable)
        {
            return new BuildResult(null, liftable.SkipReason);
        }

        var temp = allocator.Allocate("pc");
        var getterName = AccessorsToMethods.GetterName(member.Name);

        // Future-tree invocation member-binding (seam replacement node).
        var replacementBinding = SyntaxFactory.MemberBindingExpression(
            SyntaxFactory.Token(SyntaxKind.DotToken),
            SyntaxFactory.IdentifierName(getterName));
        var replacement = SyntaxFactory.InvocationExpression(
            replacementBinding,
            SyntaxFactory.ArgumentList());

        var spineCall = new ConditionalSpineCall(
            replacement,
            bound.GetMethod!,
            bound.GetMethod!,
            block.Receiver.RefKind,
            IsOrigin: true);

        var segment = new ExtensionConditionalSegment(
            outer,
            replacement,
            replacement,
            new[] { spineCall },
            bound.Type);

        var plan = new ExtensionConditionalPlan
        {
            OuterConditional = outer,
            Segments = new[] { segment },
            IsVoid = false,
            ResultType = bound.Type,
            Liftable = true,
            Anchor = liftable.Anchor,
            TempNames = new[] { temp },
            SiteAnnotation = new SyntaxAnnotation("C19", "ExtensionConditionalSite"),
            AnchorAnnotation = new SyntaxAnnotation("C19", "ExtensionConditionalAnchor"),
        };

        var seamAnnotation = new SyntaxAnnotation("C49", "ConditionalPropertySeam");
        seams.Add(new ConditionalSeamNode(binding, replacement, seamAnnotation, plan));
        hostOverrides[spineCall] = ExtensionHostSyntaxFactory.BuildHostName(block.Host);
        return new BuildResult(plan, null);
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
