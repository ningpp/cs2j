using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExtensionMethodResolve;

/// <summary>Where the bound extension host symbol comes from (design §4.2).</summary>
public enum ReceiverClassification
{
    /// <summary>Host declared in user source inside the merged compilation.</summary>
    InCompilationUser,

    /// <summary>Host declared in injected support-library source inside the merged compilation.</summary>
    InCompilationSupport,

    /// <summary>Host coming from a metadata reference (BCL/NuGet, no source location).</summary>
    ExternalMetadata,

    /// <summary>Host in the protected LINQ set (Enumerable/Queryable); sugar never touched.</summary>
    LinqOwned,
}

/// <summary>
/// M3 host-mapping grade for an <see cref="ReceiverClassification.ExternalMetadata"/>
/// host. Core only carries the label; the actual TypeMappings lookup is injected
/// by the CLI adapter (design §4.2-3: Core never depends on TypeMappings).
/// </summary>
public enum ExternalHostMappingKind
{
    /// <summary>
    /// A Java landing exists for the host itself or at least one of its extension
    /// methods; the rewritten static call is expected to resolve downstream.
    /// </summary>
    Mapped,

    /// <summary>
    /// No Java landing found in TypeMappings; CS2J1203 is reported
    /// (Warning, Error under strict) while the rewrite still happens.
    /// </summary>
    Unknown,
}

/// <summary>
/// One resolved reduced extension call site (EM-BIND). Produced solely from
/// <c>SemanticModel.GetSymbolInfo</c>; syntax shapes never participate in
/// selection. M0 declares the contract; the binder does not produce bindings yet.
/// </summary>
/// <param name="Invocation">The reduced-form invocation node <c>r.M(a)</c>.</param>
/// <param name="Reduced">The symbol as bound (<see cref="IMethodSymbol.MethodKind"/> == ReducedExtension).</param>
/// <param name="Unreduced"><see cref="IMethodSymbol.ReducedFrom"/> — the ordinary static method symbol.</param>
/// <param name="Classification">Host source/ownership classification.</param>
/// <param name="ReceiverRefKind">RefKind of the first (receiver) parameter on the unreduced method (EM-REF).</param>
/// <param name="IsInExpressionTree">Whether the node sits inside an Expression&lt;&gt; lambda (EM-EXPRTREE).</param>
/// <param name="ExplicitTypeArguments">Whether source gave explicit method type arguments (EM-GEN copy rule).</param>
public sealed record ExtensionCallBinding(
    InvocationExpressionSyntax Invocation,
    IMethodSymbol Reduced,
    IMethodSymbol Unreduced,
    ReceiverClassification Classification,
    RefKind ReceiverRefKind,
    bool IsInExpressionTree,
    bool ExplicitTypeArguments);
