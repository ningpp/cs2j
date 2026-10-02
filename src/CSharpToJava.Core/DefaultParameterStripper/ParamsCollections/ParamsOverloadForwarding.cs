using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.DefaultParameterStripper.ParamsCollections;

/// <summary>
/// Bundles the C48 context the E08 overload builder needs while filling empty
/// params slots of forwarding overloads (PC-7). Null when the rule is disabled
/// — every builder path then behaves exactly as before C48.
/// </summary>
/// <param name="Options">The enabled params-collections options.</param>
/// <param name="Diagnostics">The current strip's diagnostic sink.</param>
/// <param name="Statistics">C48 statistics accumulator.</param>
internal sealed record ParamsOverloadContext(
    ParamsCollectionsOptions Options,
    List<DefaultParameterStripDiagnostic> Diagnostics,
    ParamsStatistics Statistics);

/// <summary>
/// Fills the empty params slot of an E08 forwarding overload with a
/// kind-appropriate empty-collection shape (T15, PC-7).
///
/// Classic array params keep the builder's historical <c>new T[0]</c> emission
/// (anchored by existing E08 tests; the compiler lowers it to the same
/// Array.Empty singleton). Non-array kinds take the adjudication-5 shapes:
/// K2/K5 → <c>global::System.Array.Empty&lt;T&gt;()</c>, K4 →
/// <c>new C&lt;T&gt;()</c>, K3 builder and unsupported types cannot be filled
/// safely — the forwarding argument is omitted (the slot stays omittable
/// because the declaration keeps its params modifier) and the matching skip
/// diagnostic is emitted.
/// </summary>
internal static class ParamsOverloadForwarding
{
    /// <summary>
    /// Builds the forwarding expression for a non-array params slot, or null
    /// when the slot must stay omitted (skip diagnostic already emitted).
    /// </summary>
    public static ExpressionSyntax? BuildEmptySlotExpression(
        ParameterSyntax parameter,
        SemanticModel model,
        ParamsOverloadContext context)
    {
        ITypeSymbol? typeSymbol;
        try
        {
            typeSymbol = model.GetSymbolInfo(parameter.Type).Symbol as ITypeSymbol;
        }
        catch (ArgumentException)
        {
            typeSymbol = null;
        }

        if (typeSymbol == null)
        {
            context.Diagnostics.Add(ParamsDiagnostics.Create(
                ParamsDiagnostics.SymbolResolutionFailed,
                string.Format(
                    ParamsDiagnostics.SymbolResolutionFailedMessage,
                    parameter.Type?.ToString() ?? "<unknown>"),
                context.Options.Strict));
            context.Statistics.RecordSkipped(ParamsDiagnostics.SymbolResolutionFailed);
            return null;
        }

        var classification = ParamsTargetClassifier.Classify(typeSymbol, model.Compilation);
        if (classification.Kind == ParamsParamKind.Array)
        {
            // Unreachable from the builder (arrays take the historical branch);
            // fall back to the array shape for safety.
            return CollectionConstructionEmitter.NewArray(
                classification.ElementType!, Array.Empty<ExpressionSyntax>());
        }

        if (classification.SkipReason is string skipReason)
        {
            context.Diagnostics.Add(ParamsDiagnostics.Create(
                skipReason,
                ParamsDiagnostics.BuildSkipMessage(classification, parameter.Type?.ToString() ?? "<unknown>"),
                context.Options.Strict));
            context.Statistics.RecordSkipped(skipReason);
            return null;
        }

        if (classification.Kind == ParamsParamKind.Builder)
        {
            var builderType = classification.BuilderInfo?.BuilderType.ToDisplayString() ?? "<unknown>";
            var factoryName = classification.BuilderInfo?.FactoryMethodName ?? "<unknown>";
            context.Diagnostics.Add(ParamsDiagnostics.Create(
                ParamsDiagnostics.CollectionBuilderBlocked,
                string.Format(
                    ParamsDiagnostics.CollectionBuilderBlockedMessage,
                    classification.ParameterType.ToDisplayString(), builderType, factoryName),
                context.Options.Strict));
            context.Statistics.RecordSkipped(ParamsDiagnostics.CollectionBuilderBlocked);
            return null;
        }

        context.Diagnostics.Add(ParamsDiagnostics.Create(
            ParamsDiagnostics.OverloadEmptySlotFilled,
            string.Format(
                ParamsDiagnostics.OverloadEmptySlotFilledMessage,
                parameter.Identifier.Text,
                classification.EmptyState == ParamsEmptyStateShape.ArrayEmpty
                    ? "System.Array.Empty<T>()"
                    : $"new {classification.CanonicalConcreteType?.ToDisplayString() ?? "?"}()"),
            context.Options.Strict));

        return CollectionConstructionEmitter.EmitEmpty(classification);
    }
}
