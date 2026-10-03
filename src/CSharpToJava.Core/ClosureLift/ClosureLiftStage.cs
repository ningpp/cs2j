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
    /// Cross-stage plan transport (design §4.1/§4.1.1): NamedMethodLiftStage
    /// freezes the static-lambda plans (M1) and DelegateSiteRewriteStage
    /// consumes them. Non-positional on purpose — the C32 positional
    /// deconstruction in existing stages stays untouched. One bag per
    /// Preprocess call; never carried across files or runs.
    /// </summary>
    public ClosureLiftPlanBag? PlanBag { get; init; }
}

/// <summary>
/// Per-file shared plan bag handed to every stage of one Preprocess run.
/// Mutable container by design: plans are frozen once (NamedMethodLiftStage)
/// and read-only afterwards (DelegateSiteRewriteStage).
/// </summary>
internal sealed class ClosureLiftPlanBag
{
    public List<StaticLambdaPlan> StaticLambdas { get; } = new();
}

internal sealed record ClosureStageResult(
    CompilationUnitSyntax Root,
    bool Changed);

internal interface IClosureLiftStage
{
    string Id { get; }

    ClosureStageResult Apply(ClosureStageInput input);
}
