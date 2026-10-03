using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// Input shared by every closure-lift stage. C32 registers exactly one stage
/// (HolderLiftStage); C45 appends DisplayClassConsolidation / NamedMethodLift /
/// DelegateSiteRewrite stages to the same ordered pipeline without a new pass
/// id or registry change (design §3.4/§4.8).
/// </summary>
internal sealed record ClosureStageInput(
    CompilationUnitSyntax Root,
    SemanticModel Model,
    ClosureLiftOptions Options,
    ClosureLiftNaming Naming,
    ClosureLiftStatistics Statistics,
    List<ClosureLiftDiagnostic> Diagnostics)
{
    /// <summary>
    /// Cross-stage plan transport (design §4.1/§4.1.1): HolderLiftStage freezes
    /// the mutable-capture display-class groups (C32) and NamedMethodLiftStage
    /// the static-lambda plans (M1); DelegateSiteRewriteStage consumes both plus
    /// the instance-lambda plans frozen by DisplayClassConsolidationStage (M2).
    /// Non-positional on purpose — the C32 positional deconstruction in existing
    /// stages stays untouched. One bag per Preprocess call; never carried across
    /// files or runs.
    /// </summary>
    public ClosureLiftPlanBag? PlanBag { get; init; }
}

/// <summary>
/// Per-file shared plan bag handed to every stage of one Preprocess run.
/// Mutable container by design: plans are frozen once and read-only afterwards.
/// </summary>
internal sealed class ClosureLiftPlanBag
{
    public List<StaticLambdaPlan> StaticLambdas { get; } = new();

    /// <summary>C32 display-class groups (design §4.1.1 D2 additive evolution);
    /// registered by HolderLiftStage so the M2 consolidation can merge
    /// read-only/this captures into the same classes instead of re-deriving
    /// the mutable classification (single FindWriteSites口径, C32 裁决 4).</summary>
    public List<DisplayClassPlan> HolderGroups { get; } = new();

    /// <summary>Capturing-lambda sites (M2): replaced by
    /// `new &lt;DelType&gt;(&lt;instance&gt;.Lambda_&lt;k&gt;)` in DelegateSiteRewriteStage.</summary>
    public List<DelegateSitePlan> InstanceLambdas { get; } = new();
}

internal sealed record ClosureStageResult(
    CompilationUnitSyntax Root,
    bool Changed);

internal interface IClosureLiftStage
{
    string Id { get; }

    ClosureStageResult Apply(ClosureStageInput input);
}
