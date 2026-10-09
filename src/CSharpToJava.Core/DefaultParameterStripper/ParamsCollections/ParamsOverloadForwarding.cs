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
/// <c>new C&lt;T&gt;()</c>, K3 → <c>B.Create(new T[]{})</c> (M2 PC-9, Emit
/// policy; the factory resolves symbolically with no fed elements). Under the
/// Error gray-release policy — and for genuinely unbindable factories — the
/// forwarding argument is omitted (the slot stays omittable because the
/// declaration keeps its params modifier) and the matching skip diagnostic is
/// emitted (CS2J0607 / CS2J0605 residual).
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
            return BuildBuilderEmptySlot(parameter, model, context, classification);
        }

        var emptyShapeDescription = classification.EmptyState == ParamsEmptyStateShape.ArrayEmpty
            ? "System.Array.Empty<T>()"
            : $"new {classification.CanonicalConcreteType?.ToDisplayString() ?? "?"}()";
        var emptyExpression = CollectionConstructionEmitter.EmitEmpty(classification);

        context.Diagnostics.Add(ParamsDiagnostics.Create(
            ParamsDiagnostics.OverloadEmptySlotFilled,
            string.Format(
                ParamsDiagnostics.OverloadEmptySlotFilledMessage,
                parameter.Identifier.Text,
                emptyShapeDescription),
            context.Options.Strict));

        return emptyExpression;
    }

    /// <summary>
    /// K3 empty-slot dispatch (M2 PC-9): under the Error gray-release policy
    /// the historical CS2J0607 block applies; under Emit the factory is
    /// resolved symbolically (no fed elements — same gate as the declaration
    /// decision) and the slot fills with <c>B.Create(new T[]{})</c> paired
    /// with a CS2J0613 info. An unbindable factory reports the CS2J0605
    /// residual and the slot stays omitted.
    /// </summary>
    private static ExpressionSyntax? BuildBuilderEmptySlot(
        ParameterSyntax parameter,
        SemanticModel model,
        ParamsOverloadContext context,
        ParamsTargetClassification classification)
    {
        var builderType = classification.BuilderInfo?.BuilderType.ToDisplayString() ?? "<unknown>";
        var factoryName = classification.BuilderInfo?.FactoryMethodName ?? "<unknown>";

        if (context.Options.Builder == CollectionBuilderPolicy.Error
            || classification.BuilderInfo == null
            || classification.ParameterType is not INamedTypeSymbol targetType)
        {
            context.Diagnostics.Add(ParamsDiagnostics.Create(
                ParamsDiagnostics.CollectionBuilderBlocked,
                string.Format(
                    ParamsDiagnostics.CollectionBuilderBlockedMessage,
                    classification.ParameterType.ToDisplayString(), builderType, factoryName),
                context.Options.Strict));
            context.Statistics.RecordSkipped(ParamsDiagnostics.CollectionBuilderBlocked);
            return null;
        }

        var resolved = ParamsCollectionBinding.ResolveFactory(
            model.Compilation, targetType, classification.BuilderInfo,
            Array.Empty<ITypeSymbol?>());
        if (resolved == null)
        {
            context.Diagnostics.Add(ParamsDiagnostics.Create(
                ParamsDiagnostics.CollectionExpanded,
                string.Format(
                    ParamsDiagnostics.CollectionBuilderUnresolvedMessage,
                    classification.ParameterType.ToDisplayString(), builderType, factoryName),
                context.Options.Strict));
            context.Statistics.RecordSkipped(ParamsDiagnostics.CollectionExpanded);
            return null;
        }

        var builderExpression = CollectionConstructionEmitter.EmitBuilderFactory(
            resolved.Value.Factory, resolved.Value.ElementType,
            Array.Empty<ExpressionSyntax>());

        context.Statistics.BuilderFactorySites++;
        context.Diagnostics.Add(ParamsDiagnostics.Create(
            ParamsDiagnostics.OverloadEmptySlotFilled,
            string.Format(
                ParamsDiagnostics.OverloadEmptySlotFilledMessage,
                parameter.Identifier.Text,
                builderExpression.ToString()),
            context.Options.Strict));

        return builderExpression;
    }
}
