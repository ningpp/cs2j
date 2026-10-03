using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExtensionMethodResolve;

/// <summary>
/// One expression-tree site marked for the post-rewrite identity assertion:
/// the marker annotation tracks the emitted invocation across later tree
/// edits; <see cref="OriginalInvocation"/> identifies the sugar node to roll
/// back when the assertion fails.
/// </summary>
internal sealed record ExpressionTreeSiteMarker(
    SyntaxAnnotation Annotation,
    IMethodSymbol Unreduced,
    InvocationExpressionSyntax OriginalInvocation);

/// <summary>
/// Bottom-up rewriter (EM-CALL/EM-GEN/EM-GEN-HOST/EM-REF/EM-GROUP):
/// <list type="bullet">
/// <item>bound reduced calls <c>r.M(args)</c> →
/// <c>global::Host.M[&lt;ta&gt;]([ref/in] r, args)</c>, reusing the source
/// receiver and name nodes (evaluation order/trivia preserved, EM-CHAIN);</item>
/// <item>rewritable reduced method groups <c>r.M</c> →
/// <c>(p...) =&gt; global::Host.M(r[, p...])</c> capture lambdas (EM-GROUP).
/// The receiver is either captured directly (local/parameter/this) or read
/// from a single-evaluation temp inserted by the orchestrator;</item>
/// </list>
/// Null-conditional chains are NOT handled here: the orchestrator lowers them
/// after this pass (their spine nodes must stay identifiable up to that
/// point). Expression-tree sites carry a per-site marker annotation for the
/// identity assertion/rollback loop.
/// </summary>
internal sealed class ExtensionCallRewriter : CSharpSyntaxRewriter
{
    /// <summary>Annotation placed on every emitted invocation (residual pairing / region formatting).</summary>
    public static readonly SyntaxAnnotation RewrittenAnnotation = new("C19", "ExtensionMethodRewrittenSite");

    private readonly IReadOnlyDictionary<SyntaxNode, ExtensionCallBinding> _bindingsByNode;
    private readonly IReadOnlyDictionary<SyntaxNode, ExtensionGroupPlan> _groupsByNode;
    private readonly HashSet<SyntaxNode>? _rolledBackSites;
    private readonly IReadOnlyDictionary<SyntaxAnnotation, ExtensionCallBinding> _bindingsByAnnotation;
    private readonly IReadOnlyDictionary<SyntaxAnnotation, ExtensionGroupPlan> _groupsByAnnotation;

    private readonly List<ExpressionTreeSiteMarker> _expressionTreeSites = new();
    private readonly Dictionary<ExtensionGroupPlan, ExpressionSyntax> _groupReceivers = new();

    public ExtensionCallRewriter(
        IReadOnlyDictionary<SyntaxNode, ExtensionCallBinding> bindingsByNode,
        IReadOnlyDictionary<SyntaxNode, ExtensionGroupPlan> groupsByNode,
        HashSet<SyntaxNode>? rolledBackSites = null,
        IReadOnlyDictionary<SyntaxAnnotation, ExtensionCallBinding>? bindingsByAnnotation = null,
        IReadOnlyDictionary<SyntaxAnnotation, ExtensionGroupPlan>? groupsByAnnotation = null)
    {
        _bindingsByNode = bindingsByNode;
        _groupsByNode = groupsByNode;
        _rolledBackSites = rolledBackSites;
        _bindingsByAnnotation = bindingsByAnnotation
            ?? new Dictionary<SyntaxAnnotation, ExtensionCallBinding>();
        _groupsByAnnotation = groupsByAnnotation
            ?? new Dictionary<SyntaxAnnotation, ExtensionGroupPlan>();
    }

    public IReadOnlyList<ExpressionTreeSiteMarker> ExpressionTreeSites => _expressionTreeSites;

    /// <summary>Visited receiver expressions per temp-captured group (moved out of the old subtree).</summary>
    public IReadOnlyDictionary<ExtensionGroupPlan, ExpressionSyntax> GroupReceivers => _groupReceivers;

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var visited = (InvocationExpressionSyntax?)base.VisitInvocationExpression(node) ?? node;

        if (!TryGetBinding(node, out var binding)
            || _rolledBackSites?.Contains(binding.Invocation) == true)
        {
            return visited;
        }

        var visitedAccess = (MemberAccessExpressionSyntax)visited.Expression;
        var receiver = visitedAccess.Expression;
        var firstParameter = binding.Unreduced.Parameters.Length > 0
            ? binding.Unreduced.Parameters[0]
            : null;

        var annotations = new List<SyntaxAnnotation> { RewrittenAnnotation };
        ExpressionTreeSiteMarker? etMarker = null;
        if (binding.IsInExpressionTree)
        {
            etMarker = new ExpressionTreeSiteMarker(
                new SyntaxAnnotation("C19", "ExtensionExprTreeSite"),
                binding.Unreduced,
                binding.Invocation);
            annotations.Add(etMarker.Annotation);
            _expressionTreeSites.Add(etMarker);
        }

        var invocation = ExtensionStaticCallFactory.Build(
            binding.Unreduced,
            node.GetMemberAccessName(),
            receiver,
            binding.ReceiverRefKind,
            firstParameter?.ScopedKind ?? ScopedKind.None,
            visited.ArgumentList.Arguments,
            annotations);

        return invocation
            .WithLeadingTrivia(node.GetLeadingTrivia())
            .WithTrailingTrivia(node.GetTrailingTrivia());
    }

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var visited = (MemberAccessExpressionSyntax?)base.VisitMemberAccessExpression(node) ?? node;

        if (!TryGetGroup(node, out var plan) || !plan.Rewritable)
        {
            return visited;
        }

        var receiver = plan is { DirectCapture: true }
            ? visited.Expression
            : SyntaxFactory.IdentifierName(plan.TempName!);
        if (!plan.DirectCapture)
        {
            _groupReceivers[plan] = visited.Expression;
        }

        var invoke = plan.DelegateType.DelegateInvokeMethod!;
        var parameters = new List<ParameterSyntax>();
        var delegateArguments = new List<ArgumentSyntax>();
        for (var i = 0; i < invoke.Parameters.Length; i++)
        {
            var delegateParameter = invoke.Parameters[i];
            var parameterName = plan.LambdaParameterNames[i];
            var parameterType = (TypeSyntax)SyntaxFactory.ParseTypeName(
                delegateParameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat
                    .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Included)));
            var parameter = SyntaxFactory.Parameter(
                default,
                BuildParameterModifiers(delegateParameter.RefKind),
                parameterType.WithTrailingTrivia(SyntaxFactory.ElasticSpace),
                SyntaxFactory.Identifier(parameterName),
                null);
            parameters.Add(parameter);

            delegateArguments.Add(SyntaxFactory
                .Argument(SyntaxFactory.IdentifierName(parameterName))
                .WithRefKindKeyword(BuildArgumentModifier(delegateParameter.RefKind)));
        }

        var body = ExtensionStaticCallFactory.Build(
            plan.Unreduced,
            node.Name.WithoutTrivia(),
            receiver,
            RefKind.None,
            ScopedKind.None,
            SyntaxFactory.SeparatedList(delegateArguments),
            new SyntaxAnnotation[] { RewrittenAnnotation });

        var lambda = SyntaxFactory.ParenthesizedLambdaExpression(
            default,
            SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters)),
            SyntaxFactory.Token(SyntaxKind.EqualsGreaterThanToken),
            body)
            .WithLeadingTrivia(node.GetLeadingTrivia())
            .WithTrailingTrivia(node.GetTrailingTrivia());

        return lambda;
    }

    private bool TryGetBinding(InvocationExpressionSyntax node, out ExtensionCallBinding binding)
    {
        if (_bindingsByNode.TryGetValue(node, out binding!))
        {
            return true;
        }

        foreach (var annotation in node.GetAnnotations("C19"))
        {
            if (_bindingsByAnnotation.TryGetValue(annotation, out binding!))
            {
                return true;
            }
        }

        binding = default!;
        return false;
    }

    private bool TryGetGroup(MemberAccessExpressionSyntax node, out ExtensionGroupPlan plan)
    {
        if (_groupsByNode.TryGetValue(node, out plan!))
        {
            return true;
        }

        foreach (var annotation in node.GetAnnotations("C19"))
        {
            if (_groupsByAnnotation.TryGetValue(annotation, out plan!))
            {
                return true;
            }
        }

        plan = default!;
        return false;
    }

    private static SyntaxTokenList BuildParameterModifiers(RefKind refKind)
        => refKind switch
        {
            RefKind.Ref or RefKind.Out => SyntaxFactory.TokenList(
                SyntaxFactory.Token(default(SyntaxTriviaList), SyntaxKind.RefKeyword, SyntaxFactory.TriviaList(SyntaxFactory.ElasticSpace))),
            RefKind.In => SyntaxFactory.TokenList(
                SyntaxFactory.Token(default(SyntaxTriviaList), SyntaxKind.InKeyword, SyntaxFactory.TriviaList(SyntaxFactory.ElasticSpace))),
            _ => default,
        };

    private static SyntaxToken BuildArgumentModifier(RefKind refKind)
        => refKind switch
        {
            RefKind.Ref or RefKind.Out => SyntaxFactory.Token(SyntaxKind.RefKeyword),
            RefKind.In => SyntaxFactory.Token(SyntaxKind.InKeyword),
            _ => SyntaxFactory.Token(SyntaxKind.None),
        };
}

internal static class ExtensionInvocationSyntaxExtensions
{
    /// <summary>Source method name node (IdentifierName or explicit GenericName).</summary>
    public static SimpleNameSyntax GetMemberAccessName(this InvocationExpressionSyntax invocation)
        => ((MemberAccessExpressionSyntax)invocation.Expression).Name;
}
