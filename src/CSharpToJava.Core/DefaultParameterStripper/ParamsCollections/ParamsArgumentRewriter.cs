using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CSharpToJava.Core.DefaultParameterStripper.ParamsCollections;

/// <summary>
/// C48 call-site core (T7): rewrites compiler-synthesized params expansion
/// arguments (ArgumentKind ParamArray / ParamCollection) into explicit
/// collection constructions, per design §2.2.
///
/// Dispatch per IArgumentOperation (M0 probe Q3):
///   Explicit            → form B single collection argument — zero-change
///                         passthrough (PC-6; guards against double wrapping).
///   ParamArray          → K1 classic array — gated by ExpandArrayCallSites
///                         (D8): off keeps the comma form (Java varargs path).
///   ParamCollection     → K2/K4/K5 explicit construction via
///                         <see cref="CollectionConstructionEmitter"/>.
///   DeclaredDefault     → E08 completion position, never a params slot.
///
/// Slot alignment reuses the E08 slots model: fixed parameters are filled
/// positionally first (named arguments claim their slot by name), and every
/// trailing positional argument at or beyond the params slot index belongs to
/// the expansion segment. The IOperation conclusion (element count of the
/// synthesized value) is cross-checked against the syntax segment; any
/// mismatch is CS2J0610 and leaves the site untouched (defensive, no guessing).
///
/// Ctor initializers and primary-constructor base calls resolve through
/// IConstructorBodyOperation.Initializer → IExpressionStatementOperation →
/// inner IInvocationOperation (M0 probe Q11) — the same site shape as plain
/// invocations.
/// </summary>
internal sealed class ParamsArgumentRewriter
{
    private readonly SemanticModel _model;
    private readonly ParamsCollectionsOptions _options;
    private readonly ParamsCollectionsResult _plans;
    private readonly List<DefaultParameterStripDiagnostic> _diagnostics;

    public ParamsArgumentRewriter(
        SemanticModel model,
        ParamsCollectionsOptions options,
        ParamsCollectionsResult plans,
        List<DefaultParameterStripDiagnostic> diagnostics)
    {
        _model = model;
        _options = options;
        _plans = plans;
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Rewrites one params call site. <paramref name="originalSiteNode"/> must
    /// be a node that still belongs to the semantic model's tree (the hook
    /// passes the pre-rewrite node); <paramref name="argumentList"/> is the
    /// current — possibly E08-completed — argument list. Returns the rewritten
    /// list, or null when the site is untouched.
    /// </summary>
    public ArgumentListSyntax? RewriteCallSite(SyntaxNode originalSiteNode, ArgumentListSyntax argumentList)
    {
        try
        {
            var site = ResolveOperation(originalSiteNode);
            if (site == null)
            {
                return null;
            }

            return RewriteCore(site.Value.Target, site.Value.Arguments, originalSiteNode, argumentList);
        }
        catch (ArgumentException)
        {
            // The node is not part of the semantic model's tree (it was created
            // by an earlier rewrite step — e.g. re-visiting an emitted
            // `new List<T>(){...}` argument). Nothing to do.
            return null;
        }
        catch (InvalidOperationException)
        {
            // Defensive: partially bound operation trees on erroneous input.
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Operation resolution
    // ------------------------------------------------------------------

    private (IMethodSymbol Target, ImmutableArray<IArgumentOperation> Arguments)? ResolveOperation(SyntaxNode siteNode)
        => siteNode switch
        {
            InvocationExpressionSyntax invocation
                => _model.GetOperation(invocation) is IInvocationOperation op
                    ? (op.TargetMethod, op.Arguments)
                    : null,
            ObjectCreationExpressionSyntax creation
                => _model.GetOperation(creation) is IObjectCreationOperation created && created.Constructor != null
                    ? (created.Constructor, created.Arguments)
                    : null,
            ImplicitObjectCreationExpressionSyntax implicitCreation
                => _model.GetOperation(implicitCreation) is IObjectCreationOperation implicitCreated && implicitCreated.Constructor != null
                    ? (implicitCreated.Constructor, implicitCreated.Arguments)
                    : null,
            ConstructorInitializerSyntax initializer
                => initializer.Parent is ConstructorDeclarationSyntax ctor
                    ? UnwrapInitializerOperation(_model.GetOperation(ctor))
                    : null,
            PrimaryConstructorBaseTypeSyntax primaryBase
                => primaryBase.Parent is TypeDeclarationSyntax typeDeclaration
                    ? UnwrapInitializerOperation(_model.GetOperation(typeDeclaration))
                    : null,
            _ => null,
        };

    /// <summary>
    /// Ctor initializer path (probe Q11): IConstructorBodyOperation.Initializer
    /// is an IExpressionStatementOperation wrapping the inner IInvocationOperation
    /// (including the implicit base() call, which carries no params slot and
    /// falls through harmlessly).
    /// </summary>
    private static (IMethodSymbol Target, ImmutableArray<IArgumentOperation> Arguments)? UnwrapInitializerOperation(
        IOperation? operation)
    {
        if (operation is not IConstructorBodyOperation body)
        {
            return null;
        }

        var initializer = body.Initializer;
        while (initializer is IExpressionStatementOperation expressionStatement)
        {
            initializer = expressionStatement.Operation;
        }

        return initializer is IInvocationOperation invocation
            ? (invocation.TargetMethod, invocation.Arguments)
            : null;
    }

    // ------------------------------------------------------------------
    // Core rewrite
    // ------------------------------------------------------------------

    private ArgumentListSyntax? RewriteCore(
        IMethodSymbol target,
        ImmutableArray<IArgumentOperation> arguments,
        SyntaxNode siteNode,
        ArgumentListSyntax argumentList)
    {
        // 1. Locate the params slot on the resolved target (D1: the compiler's
        //    overload resolution is the only truth source).
        var paramsSlotIndex = -1;
        IParameterSymbol? paramsParameter = null;
        for (var i = 0; i < target.Parameters.Length; i++)
        {
            if (target.Parameters[i].IsParams)
            {
                paramsSlotIndex = i;
                paramsParameter = target.Parameters[i];
                break;
            }
        }

        if (paramsParameter == null)
        {
            return null;
        }

        var paramsArgument = arguments.LastOrDefault(a => a.Parameter?.IsParams == true);

        // 2. Syntax slots: fixed parameters first (named arguments claim slots
        //    by name), trailing positional arguments at/beyond the params slot
        //    form the expansion segment.
        var expansionSegmentMaybe = CollectExpansionSegment(argumentList, target, paramsSlotIndex, siteNode);
        if (expansionSegmentMaybe == null)
        {
            return null; // unmappable argument (0610 already emitted)
        }

        var (expansionStart, expansionSegment) = expansionSegmentMaybe.Value;
        var operationKind = paramsArgument?.ArgumentKind;

        // 3. Operation/syntax agreement. The compiler is the only authority on
        //    which syntax arguments belong to the params slot (probe Q3): a
        //    single user argument — positional OR named — bound to the params
        //    slot is ArgumentKind.Explicit (form B), while only synthesized
        //    expansions report ParamArray/ParamCollection.
        if (operationKind == ArgumentKind.Explicit)
        {
            // Form B passthrough (PC-6/D3 — zero change, reference identity
            // preserved). More than one trailing positional cannot be Explicit
            // on compilable input; defensive 0610 otherwise.
            if (expansionSegment.Count > 1)
            {
                return SkipWithConflict(siteNode);
            }

            _plans.Statistics.ParamsCallSitesPassthrough++;
            _plans.Statistics.RecordSkipped(ParamsDiagnostics.SingleCollectionPassthrough);
            return null;
        }

        if (operationKind is not (ArgumentKind.ParamArray or ArgumentKind.ParamCollection))
        {
            // Synthesized position expected here; anything else (including
            // DeclaredDefault on a params slot, which legal input cannot
            // produce) is a defensive conflict.
            if (expansionSegment.Count == 0 && operationKind is null)
            {
                return null; // nothing observable to rewrite
            }

            return SkipWithConflict(siteNode);
        }

        // ParamArray / ParamCollection: empty form (form C) or expansion (form A).
        if (expansionSegment.Count > 0 && paramsArgument != null)
        {
            var elementCount = CountExpansionElements(paramsArgument);
            if (elementCount >= 0 && elementCount != expansionSegment.Count)
            {
                return SkipWithConflict(siteNode);
            }
        }

        // 4. Classify the params target from the call-site (constructed) type
        //    symbol so generic slots carry their concrete element type. The
        //    site position lets the M2 K4 gate bind the real Add symbol via a
        //    speculative collection initializer at this exact location.
        var bindingSite = SafeBindingSite(siteNode);
        var classification = ParamsTargetClassifier.Classify(
            paramsParameter.Type, _model.Compilation, bindingSite);

        // 5. Gates: K1 is D8-gated (T9); non-array kinds must belong to a
        //    materialize-strategy plan collected from the declaring file.
        if (classification.Kind == ParamsParamKind.Array)
        {
            if (!_options.ExpandArrayCallSites)
            {
                _plans.Statistics.RecordSkipped(ParamsDiagnostics.ArrayCallSite);
                return null;
            }
        }
        else if (!_plans.TryGetDeclaration(target, out var plan) || plan == null)
        {
            // No collect-phase plan: the declaration side is not under C48
            // control (metadata/BCL target, or an uncollected file) — keep the
            // site and never guess (D1 discipline).
            _diagnostics.Add(ParamsDiagnostics.Create(
                ParamsDiagnostics.SymbolResolutionFailed,
                string.Format(ParamsDiagnostics.SymbolResolutionFailedMessage, target.ToDisplayString()),
                _options.Strict));
            _plans.Statistics.RecordSkipped(ParamsDiagnostics.SymbolResolutionFailed);
            return null;
        }
        else if (plan.Strategy == ParamsDeclStrategy.KeepParams)
        {
            // Declaration kept (unsupported / builder / scope / char face):
            // its skip diagnostic was emitted once at collect time; call sites
            // stay in the comma form for the same reason.
            return null;
        }

        // 5b. Site-level defensive skip: the call-site classification proved
        //     the shape cannot be emitted (M2: K4 Add unbindable at this site).
        if (classification.SkipReason is string siteSkipReason)
        {
            _diagnostics.Add(ParamsDiagnostics.Create(
                siteSkipReason,
                ParamsDiagnostics.BuildSkipMessage(classification, target.ToDisplayString()),
                _options.Strict));
            _plans.Statistics.RecordSkipped(siteSkipReason);
            return null;
        }

        // 5c. CS2J0611 edge (M2 probe §13): a SYNTHESIZED expansion segment
        //     (ParamArray/ParamCollection — a single Explicit collection
        //     argument is form B and already passed through above) whose
        //     element is itself a collection expression or spread, e.g.
        //     Groups([1,2],[3]) on params int[][]. C36 owns collection-
        //     expression lowering and must run first (after it the elements
        //     are ordinary new[]{…} expressions); wrapping here would
        //     double-process. The site is kept comma-form with a directional
        //     diagnostic. A bare `..x` argument is baseline-illegal in C# 13
        //     (parses as Range, CS0029) — unreachable on compilable input.
        if (expansionSegment.Count > 0)
        {
            var nestedElement = expansionSegment.FirstOrDefault(a =>
                a.Expression.DescendantNodesAndSelf()
                    .Any(n => n is CollectionExpressionSyntax or SpreadElementSyntax));
            if (nestedElement != null)
            {
                _diagnostics.Add(ParamsDiagnostics.Create(
                    ParamsDiagnostics.NestedCollectionExpression,
                    string.Format(
                        ParamsDiagnostics.NestedCollectionExpressionMessage,
                        siteNode.ToString(), nestedElement.Expression.ToString()),
                    _options.Strict));
                _plans.Statistics.RecordSkipped(ParamsDiagnostics.NestedCollectionExpression);
                return null;
            }
        }

        // 6. Emit. K3 builders need the bound factory symbol (M2 PC-9):
        //    the compiler's own ConstructMethod first (probe §13 — present on
        //    every compilable K3 site including the empty form, generic arity
        //    and ReadOnlySpan<KeyValuePair<K,V>>), then the symbol-driven C36
        //    analyzer with this site's actually-fed element types. Both fail →
        //    the CS2J0605 residual (never a guessed factory).
        (IMethodSymbol Factory, ITypeSymbol ElementType)? builderFactory = null;
        if (classification.Kind == ParamsParamKind.Builder)
        {
            builderFactory = ResolveBuilderFactory(classification, paramsParameter, paramsArgument, expansionSegment);
            if (builderFactory == null)
            {
                var builderType = classification.BuilderInfo?.BuilderType.ToDisplayString() ?? "<unknown>";
                var factoryName = classification.BuilderInfo?.FactoryMethodName ?? "<unknown>";
                _diagnostics.Add(ParamsDiagnostics.Create(
                    ParamsDiagnostics.CollectionExpanded,
                    string.Format(ParamsDiagnostics.CollectionBuilderUnresolvedMessage,
                        classification.ParameterType.ToDisplayString(), builderType, factoryName),
                    _options.Strict));
                _plans.Statistics.RecordSkipped(ParamsDiagnostics.CollectionExpanded);
                return null;
            }
        }

        ExpressionSyntax replacement;
        if (expansionSegment.Count == 0)
        {
            replacement = classification.Kind == ParamsParamKind.Builder && builderFactory != null
                ? CollectionConstructionEmitter.EmitBuilderFactory(
                    builderFactory.Value.Factory, builderFactory.Value.ElementType,
                    Array.Empty<ExpressionSyntax>())
                : CollectionConstructionEmitter.EmitEmpty(classification);
            _plans.Statistics.ParamsCallSitesEmpty++;
        }
        else
        {
            var elements = expansionSegment.Select(a => a.Expression).ToList();
            replacement = classification.Kind == ParamsParamKind.Builder && builderFactory != null
                ? CollectionConstructionEmitter.EmitBuilderFactory(
                    builderFactory.Value.Factory, builderFactory.Value.ElementType, elements)
                : CollectionConstructionEmitter.EmitExpansion(classification, elements);
            _plans.Statistics.ParamsCallSitesExpanded++;
        }

        switch (classification.Kind)
        {
            case ParamsParamKind.Array:
                _plans.Statistics.ArrayCallSitesExpanded++;
                break;
            case ParamsParamKind.Span:
                _plans.Statistics.SpanAsArraySites++;
                _diagnostics.Add(ParamsDiagnostics.Create(
                    ParamsDiagnostics.SpanAsArray,
                    string.Format(
                        "params type '{0}' expansion is emitted as an explicit array (implicit T[] conversion; C06 owns the final Span shape)",
                        classification.ParameterType.ToDisplayString()),
                    _options.Strict));
                break;
            case ParamsParamKind.Builder:
                // M2 PC-9: B.Create(new T[]{…}) from the compiler-bound /
                // symbol-resolved factory (0607 retired on this path).
                _plans.Statistics.BuilderFactorySites++;
                _diagnostics.Add(ParamsDiagnostics.Create(
                    ParamsDiagnostics.CollectionExpanded,
                    string.Format(
                        "params expansion of '{0}' is emitted through builder factory '{1}'",
                        classification.ParameterType.ToDisplayString(),
                        replacement.ToString()),
                    _options.Strict));
                break;
            case ParamsParamKind.Interface or ParamsParamKind.Initializable:
                if (classification.Kind == ParamsParamKind.Initializable
                    && classification.ResolvedAddMethod != null
                    && expansionSegment.Count > 0)
                {
                    // M2 PC-3: every expanded K4 site was bound to a real Add
                    // symbol (compiler-bound speculative probe or symbolic
                    // collection-pattern rule) — no add/put guessing.
                    _plans.Statistics.InitializerAddBoundSites++;
                }

                _diagnostics.Add(ParamsDiagnostics.Create(
                    ParamsDiagnostics.CollectionExpanded,
                    string.Format(
                        "params expansion of '{0}' is emitted as '{1}'",
                        classification.ParameterType.ToDisplayString(),
                        replacement.ToString()),
                    _options.Strict));
                break;
        }

        return BuildRewrittenArgumentList(argumentList, expansionStart, target, paramsSlotIndex, replacement);
    }

    /// <summary>
    /// Builds the M2 semantic binding site for classifier-level Add binding,
    /// or null when the site node does not belong to the model's tree (the
    /// classifier then uses the symbolic collection-pattern fallback).
    /// </summary>
    private ParamsBindingSite? SafeBindingSite(SyntaxNode siteNode)
    {
        try
        {
            if (siteNode.SyntaxTree != _model.SyntaxTree)
            {
                return null;
            }

            return new ParamsBindingSite(_model, siteNode.SpanStart);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves the K3 builder factory for one params call site (M2 PC-9).
    /// Primary source: the compiler's own answer —
    /// <see cref="ICollectionExpressionOperation.ConstructMethod"/> on the
    /// synthesized params value (probe §13: bound on the empty form too, with
    /// generic arity already constructed). Fallback: the symbol-driven C36
    /// CE-10 analyzer fed with this segment's actually-converted element
    /// types. Returns null only when both paths fail (CS2J0605 residual).
    /// </summary>
    private (IMethodSymbol Factory, ITypeSymbol ElementType)? ResolveBuilderFactory(
        ParamsTargetClassification classification,
        IParameterSymbol paramsParameter,
        IArgumentOperation? paramsArgument,
        SeparatedSyntaxList<ArgumentSyntax> expansionSegment)
    {
        if (paramsArgument?.Value is ICollectionExpressionOperation collectionExpression)
        {
            var accepted = ParamsCollectionBinding.AcceptConstructFactory(
                collectionExpression.ConstructMethod);
            if (accepted != null)
            {
                return accepted;
            }
        }

        if (classification.BuilderInfo == null
            || paramsParameter.Type is not INamedTypeSymbol targetType)
        {
            return null;
        }

        var elementFedTypes = expansionSegment
            .Select(argument =>
            {
                try
                {
                    var typeInfo = _model.GetTypeInfo(argument.Expression);
                    return typeInfo.ConvertedType ?? typeInfo.Type;
                }
                catch (ArgumentException)
                {
                    // Segment node from a tree this model does not own — the
                    // C36 analyzer treats a null fed type as "no constraint".
                    return (ITypeSymbol?)null;
                }
            })
            .ToList();

        return ParamsCollectionBinding.ResolveFactory(
            _model.Compilation, targetType, classification.BuilderInfo, elementFedTypes);
    }

    // ------------------------------------------------------------------
    // Slot alignment (E08 slots model)
    // ------------------------------------------------------------------

    /// <summary>
    /// Maps the argument list onto parameter slots and returns the trailing
    /// positional expansion segment — its start index and the arguments from
    /// that index on (empty segment with a start of <c>Arguments.Count</c>
    /// when nothing trails the params slot). Returns null when an argument
    /// cannot be mapped (0610 emitted, site kept).
    /// </summary>
    private (int Start, SeparatedSyntaxList<ArgumentSyntax> Segment)? CollectExpansionSegment(
        ArgumentListSyntax argumentList,
        IMethodSymbol target,
        int paramsSlotIndex,
        SyntaxNode siteNode)
    {
        // Extension methods: an unreduced target symbol carries the receiver as
        // parameters[0]; an instance-form call site has no matching syntax
        // argument, so positional filling must start one slot later. Reduced
        // symbols (MethodKind.ReducedExtension) and static-form calls need no
        // adjustment. Any residual mismatch is caught by the element-count
        // cross-check in RewriteCore.
        var receiverSkip = GetReceiverSkipCount(target, siteNode);

        var nextPositional = receiverSkip;
        var expansionStart = -1;
        var arguments = argumentList.Arguments;

        for (var i = 0; i < arguments.Count; i++)
        {
            var arg = arguments[i];
            if (arg.NameColon != null)
            {
                var index = IndexOfParamName(target, arg.NameColon.Name.Identifier.Text);
                if (index < 0)
                {
                    SkipWithConflict(siteNode);
                    return null;
                }

                if (index >= nextPositional)
                {
                    nextPositional = index + 1;
                }

                continue;
            }

            if (nextPositional < paramsSlotIndex)
            {
                nextPositional++;
                continue;
            }

            if (expansionStart < 0)
            {
                expansionStart = i;
            }
        }

        if (expansionStart < 0)
        {
            return (arguments.Count, default); // empty segment: nothing trailing the params slot
        }

        var segment = new SeparatedSyntaxList<ArgumentSyntax>();
        for (var i = expansionStart; i < arguments.Count; i++)
        {
            if (arguments[i].NameColon != null)
            {
                // Named arguments cannot follow a params expansion segment in
                // compilable input (baseline-illegal naming order); defensive.
                SkipWithConflict(siteNode);
                return null;
            }

            segment = segment.Add(arguments[i]);
        }

        return (expansionStart, segment);
    }

    private int GetReceiverSkipCount(IMethodSymbol target, SyntaxNode siteNode)
    {
        if (!target.IsExtensionMethod || target.MethodKind == MethodKind.ReducedExtension)
        {
            return 0;
        }

        // Unreduced extension symbol. A static-form call site
        // (ExtClass.M(receiver, ...) ) passes the receiver as a syntax
        // argument; an instance-form call site (receiver.M(...) ) does not.
        return IsInstanceFormCall(siteNode) ? 1 : 0;
    }

    /// <summary>
    /// True when the call site invokes the extension method through an instance
    /// receiver (<c>x.M(...)</c> / <c>x?.M(...)</c>) rather than the static form
    /// (<c>ExtClass.M(x, ...)</c>). The member-access left side of a static form
    /// resolves to the extension's containing type.
    /// </summary>
    private bool IsInstanceFormCall(SyntaxNode siteNode)
    {
        if (siteNode is not InvocationExpressionSyntax invocation)
        {
            return false;
        }

        switch (invocation.Expression)
        {
            case MemberBindingExpressionSyntax:
                // ?.M(...) — always an instance-form receiver.
                return true;
            case MemberAccessExpressionSyntax memberAccess:
                try
                {
                    return _model.GetSymbolInfo(memberAccess.Expression).Symbol is not INamedTypeSymbol;
                }
                catch (ArgumentException)
                {
                    return false;
                }

            default:
                // IdentifierName / GenericName — plain or static-form call.
                return false;
        }
    }

    private static int IndexOfParamName(IMethodSymbol target, string name)
    {
        for (var i = 0; i < target.Parameters.Length; i++)
        {
            if (string.Equals(target.Parameters[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Element count of the synthesized expansion value, for the
    /// operation-vs-syntax cross-check. -1 when the value shape is unexpected
    /// (defensive).
    /// </summary>
    private static int CountExpansionElements(IArgumentOperation argument)
        => argument.Value switch
        {
            IArrayCreationOperation arrayCreation => arrayCreation.Initializer.ElementValues.Length,
            ICollectionExpressionOperation collectionExpression => collectionExpression.Elements.Length,
            _ => -1,
        };

    // ------------------------------------------------------------------
    // Output assembly
    // ------------------------------------------------------------------

    /// <summary>
    /// Rebuilds the argument list: the arguments before the expansion start
    /// keep their nodes (and trivia); everything from the expansion start on is
    /// replaced by the single emitted construction argument. The replacement is
    /// named when any remaining argument is named (keeps the list valid
    /// regardless of argument order), positional otherwise.
    /// </summary>
    private static ArgumentListSyntax BuildRewrittenArgumentList(
        ArgumentListSyntax argumentList,
        int expansionStart,
        IMethodSymbol target,
        int paramsSlotIndex,
        ExpressionSyntax replacement)
    {
        var arguments = argumentList.Arguments;
        var kept = new List<ArgumentSyntax>(Math.Min(expansionStart, arguments.Count));
        var hasNamed = false;

        for (var i = 0; i < arguments.Count && i < expansionStart; i++)
        {
            hasNamed |= arguments[i].NameColon != null;
            kept.Add(arguments[i]);
        }

        ArgumentSyntax newArgument;
        if (hasNamed && paramsSlotIndex < target.Parameters.Length)
        {
            newArgument = SyntaxFactory.Argument(
                SyntaxFactory.NameColon(SyntaxFactory.IdentifierName(target.Parameters[paramsSlotIndex].Name)),
                default,
                replacement);
        }
        else
        {
            newArgument = SyntaxFactory.Argument(replacement);
        }

        kept.Add(newArgument);
        return SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(kept));
    }

    // ------------------------------------------------------------------
    // Diagnostics
    // ------------------------------------------------------------------

    private ArgumentListSyntax? SkipWithConflict(SyntaxNode siteNode)
    {
        _diagnostics.Add(ParamsDiagnostics.Create(
            ParamsDiagnostics.SlotConflict,
            string.Format(ParamsDiagnostics.SlotConflictMessage, siteNode.ToString()),
            _options.Strict));
        _plans.Statistics.RecordSkipped(ParamsDiagnostics.SlotConflict);
        return null;
    }
}
