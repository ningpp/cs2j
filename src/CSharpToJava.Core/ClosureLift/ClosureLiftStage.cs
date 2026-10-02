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
    List<ClosureLiftDiagnostic> Diagnostics);

internal sealed record ClosureStageResult(
    CompilationUnitSyntax Root,
    bool Changed);

internal interface IClosureLiftStage
{
    string Id { get; }

    ClosureStageResult Apply(ClosureStageInput input);
}
