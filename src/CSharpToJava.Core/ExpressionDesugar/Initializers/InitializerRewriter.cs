using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.ExpressionDesugar;

namespace CSharpToJava.Core.ExpressionDesugar.Initializers;

/// <summary>
/// Bottom-up rewriter for C24 (design §4.1 step 2). Object/collection
/// initializer sites in an M1-supported context become a
/// <see cref="ExpressionDesugarTempLifter.PendingTemp"/> statement sequence
/// registered at the shared anchor (<c>var __ed_n = new T(args);</c> followed by
/// the ordered member/Add statements) and the site is replaced by the temp
/// reference. Bare target-typed <c>new()</c> is explicitized in place with no
/// temp (works in any context, including field/property initializers).
///
/// All semantic decisions come from Analyze-built plans keyed by ORIGINAL
/// nodes; rendering uses the already-visited children (C47 discipline).
/// Skipped plans keep the node annotated with the shared explained marker.
/// </summary>
internal sealed class InitializerRewriter : CSharpSyntaxRewriter
{
    private readonly IReadOnlyDictionary<BaseObjectCreationExpressionSyntax, InitializerPlan> _plans;

    public InitializerRewriter(
        IReadOnlyDictionary<BaseObjectCreationExpressionSyntax, InitializerPlan> plans)
    {
        _plans = plans;
    }

    /// <summary>Statement sequences collected for the family TempLifter.</summary>
    public List<ExpressionDesugarTempLifter.PendingTemp> Pending { get; } = new();

    /// <summary>Anchor node -> annotation map consumed by TempLifter.Apply.</summary>
    public Dictionary<SyntaxNode, SyntaxAnnotation> AnchorAnnotations { get; } = new();

    /// <summary>Number of sites actually rewritten (drives the family dirty gate).</summary>
    public int RewriteCount { get; private set; }

    public override SyntaxNode? VisitObjectCreationExpression(
        ObjectCreationExpressionSyntax node)
    {
        var visited = (ObjectCreationExpressionSyntax)
            base.VisitObjectCreationExpression(node)!;
        return Apply(node, visited);
    }

    public override SyntaxNode? VisitImplicitObjectCreationExpression(
        ImplicitObjectCreationExpressionSyntax node)
    {
        var visited = (ImplicitObjectCreationExpressionSyntax)
            base.VisitImplicitObjectCreationExpression(node)!;
        return Apply(node, visited);
    }

    private SyntaxNode Apply(
        BaseObjectCreationExpressionSyntax original,
        BaseObjectCreationExpressionSyntax visited)
    {
        // No plan: either not a C24 trigger (plain `new C()`, a creation owned
        // elsewhere) or a site deferred for C36 coexistence. Both must stay
        // byte-identical — returning `visited` untouched keeps the node's
        // reference identity so C36's original-node-keyed plans still resolve.
        if (!_plans.TryGetValue(original, out var plan))
            return visited;

        switch (plan.Outcome)
        {
            case InitializerOutcome.TargetTypedNewExplicit:
            case InitializerOutcome.ZeroTempExplicit:
                RewriteCount++;
                return ObjectInitRewriter.BuildConstruction(plan, visited);

            case InitializerOutcome.ObjectSequence:
            case InitializerOutcome.CollectionSequence:
                return LowerWithTemp(plan, visited);

            default:
                // SkippedDiagnostic / UnsupportedContext: preserved site, paired
                // by the explained annotation (diagnostic when one was emitted).
                return visited.WithAdditionalAnnotations(
                    ExpressionDesugarSyntaxFacts.ExplainedAnnotation);
        }
    }

    /// <summary>
    /// Registers the construction + ordered member statements at the shared
    /// anchor and returns the <c>__ed_n</c> reference. A missing anchor is a
    /// defensive preserve (Analyze proved liftability, so this is a structural
    /// gap) — the site is kept explained, never half-lowered.
    /// </summary>
    private SyntaxNode LowerWithTemp(
        InitializerPlan plan, BaseObjectCreationExpressionSyntax visited)
    {
        var anchor = ExpressionDesugarTempLifter.FindAnchor(plan.Node);
        if (anchor == null || plan.TempName == null)
        {
            return visited.WithAdditionalAnnotations(
                ExpressionDesugarSyntaxFacts.ExplainedAnnotation);
        }

        var statements = new List<StatementSyntax>
        {
            ExpressionDesugarTempLifter.DeclareVar(
                plan.TempName, ObjectInitRewriter.BuildConstruction(plan, visited)),
        };

        var initializer = visited.Initializer;
        if (initializer != null)
        {
            var elementIndex = 0;
            foreach (var member in plan.Members)
            {
                if (elementIndex >= initializer.Expressions.Count)
                    break;

                var element = initializer.Expressions[elementIndex++];
                statements.Add(member is CollectionAddPlan
                    ? CollectionInitRewriter.BuildAddStatement(plan.TempName, element)
                    : ObjectInitRewriter.BuildMemberStatement(plan.TempName, member, element));
            }
        }

        Pending.Add(new ExpressionDesugarTempLifter.PendingTemp(
            statements, anchor, plan.LiftOrder));

        if (!AnchorAnnotations.ContainsKey(anchor))
        {
            AnchorAnnotations[anchor] =
                new SyntaxAnnotation(ExpressionDesugarTempLifter.AnchorAnnotationKind);
        }

        RewriteCount++;
        return SyntaxFactory.IdentifierName(plan.TempName);
    }

    /// <summary>
    /// Keeps anchor nodes locatable after their descendants are rewritten
    /// (same attachment discipline as the C36/C47 rewriters).
    /// </summary>
    public override SyntaxNode? Visit(SyntaxNode? node)
    {
        var result = base.Visit(node);
        if (node != null
            && result != null
            && AnchorAnnotations.TryGetValue(node, out var annotation))
        {
            result = result.WithAdditionalAnnotations(annotation);
        }

        return result;
    }
}
