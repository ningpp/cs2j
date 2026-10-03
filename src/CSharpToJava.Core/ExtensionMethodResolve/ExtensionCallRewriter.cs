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
    private readonly Func<InvocationExpressionSyntax, IMethodSymbol, bool>? _forceBrokenRewrite;

    private readonly List<ExpressionTreeSiteMarker> _expressionTreeSites = new();
    private readonly Dictionary<ExtensionGroupPlan, ExpressionSyntax> _groupReceivers = new();

    /// <summary>Expression emitted by the M4 rollback drill hook (unresolvable host → CS0246 at the site).</summary>
    private const string BrokenRewriteExpressionText = "global::__CS2J_M4_ROLLBACK_PROBE__.ProbeBroken()";

    public ExtensionCallRewriter(
        IReadOnlyDictionary<SyntaxNode, ExtensionCallBinding> bindingsByNode,
        IReadOnlyDictionary<SyntaxNode, ExtensionGroupPlan> groupsByNode,
        HashSet<SyntaxNode>? rolledBackSites = null,
        IReadOnlyDictionary<SyntaxAnnotation, ExtensionCallBinding>? bindingsByAnnotation = null,
        IReadOnlyDictionary<SyntaxAnnotation, ExtensionGroupPlan>? groupsByAnnotation = null,
        Func<InvocationExpressionSyntax, IMethodSymbol, bool>? forceBrokenRewrite = null)
    {
        _bindingsByNode = bindingsByNode;
        _groupsByNode = groupsByNode;
        _rolledBackSites = rolledBackSites;
        _bindingsByAnnotation = bindingsByAnnotation
            ?? new Dictionary<SyntaxAnnotation, ExtensionCallBinding>();
        _groupsByAnnotation = groupsByAnnotation
            ?? new Dictionary<SyntaxAnnotation, ExtensionGroupPlan>();
        _forceBrokenRewrite = forceBrokenRewrite;
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

        // M4 rollback drill seam: deterministic non-compiling replacement at
        // the selected site (CS0246 location attributes to this file).
        if (_forceBrokenRewrite?.Invoke(binding.Invocation, binding.Unreduced) == true)
        {
            return ParseBrokenRewrite()
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia());
        }

        var rewritten = invocation
            .WithLeadingTrivia(node.GetLeadingTrivia())
            .WithTrailingTrivia(node.GetTrailingTrivia());

        // Roslyn parser quirk: an alias-qualified name (`global::…`) at the
        // left edge of an interpolation hole expression is rejected with
        // CS0103 ("global" not found), including through postfix chains,
        // binary/unary prefixes, casts and awaits. Parentheses are the
        // supported surface: `{(global::Host.M(r, args))}`.
        if (IsAtInterpolationHoleLeftEdge(node))
        {
            return SyntaxFactory.ParenthesizedExpression(rewritten.WithoutTrivia())
                .WithLeadingTrivia(rewritten.GetLeadingTrivia())
                .WithTrailingTrivia(rewritten.GetTrailingTrivia());
        }

        return rewritten;
    }

    /// <summary>
    /// Whether <paramref name="node"/> starts (possibly through postfix/unary
    /// chains where it stays the leading subexpression) an enclosing
    /// interpolation hole expression.
    /// </summary>
    private static bool IsAtInterpolationHoleLeftEdge(ExpressionSyntax node)
    {
        var current = node;
        while (true)
        {
            var parent = current.Parent;
            switch (parent)
            {
                case null:
                    return false;
                case InterpolationSyntax interpolation when interpolation.Expression == current:
                    return true;
                case InvocationExpressionSyntax invocation when invocation.Expression == current:
                case MemberAccessExpressionSyntax memberAccess when memberAccess.Expression == current:
                case ElementAccessExpressionSyntax elementAccess when elementAccess.Expression == current:
                case ConditionalAccessExpressionSyntax conditional when conditional.WhenNotNull == current:
                case PostfixUnaryExpressionSyntax postfix when postfix.Operand == current:
                case PrefixUnaryExpressionSyntax prefix when prefix.Operand == current:
                case CastExpressionSyntax cast when cast.Expression == current:
                case AwaitExpressionSyntax awaited when awaited.Expression == current:
                case BinaryExpressionSyntax binary when binary.Left == current:
                    current = (ExpressionSyntax)parent;
                    continue;
                default:
                    return false;
            }
        }
    }

    private static InvocationExpressionSyntax ParseBrokenRewrite()
        => (InvocationExpressionSyntax)SyntaxFactory.ParseExpression(BrokenRewriteExpressionText)
            .WithAdditionalAnnotations(RewrittenAnnotation);

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
