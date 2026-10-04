using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CSharpToJava.Core.ExpressionDesugar.NullConditional;

/// <summary>Diagnostic codes owned by the C35 rule (design §6, CS2J0550–CS2J0567).</summary>
internal static class NullConditionalDiagnosticCodes
{
    public const string CS2J0550 = "CS2J0550";
    public const string CS2J0551 = "CS2J0551";
    public const string CS2J0552 = "CS2J0552";
    public const string CS2J0553 = "CS2J0553";
    public const string CS2J0554 = "CS2J0554";
    public const string CS2J0555 = "CS2J0555";
    public const string CS2J0557 = "CS2J0557";
    public const string CS2J0558 = "CS2J0558";
    public const string CS2J0559 = "CS2J0559";
    public const string CS2J0560 = "CS2J0560";
    public const string CS2J0564 = "CS2J0564";
    public const string CS2J0565 = "CS2J0565";
    public const string CS2J0566 = "CS2J0566";
    public const string CS2J0567 = "CS2J0567";
}

/// <summary>Message templates for the C35 diagnostic codes.</summary>
internal static class NullConditionalDiagnostics
{
    public const string SiteMessage =
        "Null-conditional access site explicitized.";
    public const string IndexSiteMessage =
        "Conditional index (?[]) site explicitized.";
    public const string VoidSiteMessage =
        "Null-conditional statement rewritten to an explicit null-check if form.";
    public const string InvokeSiteMessage =
        "Delegate/event conditional Invoke site explicitized.";
    public const string LiftedSiteMessage =
        "Value-type terminal lifted to Nullable<T> (explicit T? cast).";
    public const string UnliftableMessage =
        "Null-conditional site cannot be lowered here (no statement anchor for a " +
        "segment temporary): '{0}'. Rewrite it with an explicit null check or move " +
        "the expression into a method body.";
    public const string AnchorlessVoidMessage =
        "Void conditional invocation has no statement anchor (e.g. expression-bodied " +
        "member); add an explicit null check or convert the member to a block body.";
    public const string DynamicMessage =
        "The receiver or terminal type of this null-conditional expression is " +
        "'dynamic' or cannot be resolved; static types are required.";
    public const string RefContextMessage =
        "Null-conditional expressions are not allowed in ref/out contexts " +
        "(defensive mirror of the baseline compiler error).";
    public const string UnknownTerminalMessage =
        "Unrecognized WhenNotNull terminal '{0}'; the site cannot be lowered safely.";
    public const string ExpressionTreeMessage =
        "Null-conditional site inside an expression-tree lambda skipped (?." +
        "/?[] is rejected by the baseline compiler).";
    public const string ResidualMessage =
        "{0} unpaired null-conditional node(s) remain in the output without an " +
        "explanatory diagnostic.";
    public const string CoalesceCompositionMessage =
        "Null-conditional site composed with null-coalesce (??) product.";

    /// <summary>Error under strict options, Warning otherwise.</summary>
    public static ExpressionDesugarSeverity ResolveStrictError(bool strict)
        => strict ? ExpressionDesugarSeverity.Error : ExpressionDesugarSeverity.Warning;
}

/// <summary>Per-file product of the C35 analysis.</summary>
internal sealed class NullConditionalAnalysis
{
    private readonly Dictionary<ConditionalAccessExpressionSyntax, ChainPlan> _byRoot;

    public NullConditionalAnalysis(IReadOnlyList<ChainPlan> plans)
    {
        Plans = plans;
        _byRoot = new Dictionary<ConditionalAccessExpressionSyntax, ChainPlan>();
        foreach (var plan in plans)
            _byRoot[plan.RootOriginal] = plan;
    }

    public IReadOnlyList<ChainPlan> Plans { get; }

    public bool TryGetPlan(
        ConditionalAccessExpressionSyntax root,
        out ChainPlan plan)
        => _byRoot.TryGetValue(root, out plan!);
}

/// <summary>
/// Builds one <see cref="ChainPlan"/> per outermost conditional access
/// (design §4.1): chain roots are CAs that are not the direct WhenNotNull of a
/// parent CA; dependent CA nodes are covered by their root plan. Read-only;
/// every decision comes from SemanticModel/IOperation.
/// </summary>
internal static class ChainSegmentAnalyzer
{
    public static NullConditionalAnalysis Analyze(RuleAnalysisContext context)
    {
        var root = context.Root;
        var model = context.SemanticModel;
        var options = context.Options.NullConditional!;
        var stats = context.Statistics.NullConditional;
        stats.FilesScanned = 1;

        var allocator = new NullConditionalTempNameAllocator(root);
        var plans = new List<ChainPlan>();

        foreach (var ca in root.DescendantNodes()
                     .OfType<ConditionalAccessExpressionSyntax>())
        {
            if (!IsChainRoot(ca))
                continue;

            plans.Add(BuildPlan(ca, context, model!, options, allocator));
        }

        // Segment temp names are allocated lazily, in chain/segment order,
        // only for lowered shapes needing temps (so inline output is free of
        // __ed_s names and numbering stays compact).
        foreach (var plan in plans)
        {
            if (plan.Outcome != ChainOutcome.Lowered)
                continue;
            if (plan.Shape is NullConditionalLoweringShape.Inline)
                continue;

            foreach (var segment in plan.Segments)
                segment.TempName = allocator.Allocate();
        }

        return new NullConditionalAnalysis(plans);
    }

    /// <summary>
    /// A CA is a chain root unless it is the direct <c>WhenNotNull</c> of a
    /// parent CA. CAs inside a parent CA's Expression (e.g. <c>(a?.b)?.c</c>)
    /// or inside arguments are independent roots.
    /// </summary>
    private static bool IsChainRoot(ConditionalAccessExpressionSyntax ca)
        => !(ca.Parent is ConditionalAccessExpressionSyntax parentCa
             && parentCa.WhenNotNull == ca);

    private static ChainPlan BuildPlan(
        ConditionalAccessExpressionSyntax root,
        RuleAnalysisContext context,
        SemanticModel model,
        NullConditionalOptions options,
        NullConditionalTempNameAllocator allocator)
    {
        // ---- Segment sequence (one CA per ?./?[]) ----
        var segments = new List<ChainSegment>();
        var current = root;
        while (true)
        {
            var receiverType = SafeReceiverType(model, current);
            segments.Add(new ChainSegment
            {
                Ca = current,
                ReceiverOriginal = current.Expression,
                ReceiverType = receiverType,
            });

            if (current.WhenNotNull is ConditionalAccessExpressionSyntax inner)
            {
                current = inner;
                continue;
            }

            break;
        }

        var terminalNode = current.WhenNotNull;
        var terminal = ClassifyTerminal(root, terminalNode, model);
        var resultType = model.GetTypeInfo(root).Type;
        var terminalValueType = model.GetOperation(terminalNode)?.Type
                                ?? model.GetTypeInfo(terminalNode).Type;

        var castToNullable = NeedsNullableCast(terminalValueType, resultType);
        TypeSyntax? castType = null;
        if (castToNullable && resultType != null)
            castType = SyntaxFactory.ParseTypeName(
                resultType.ToDisplayString());

        var contextKind = ClassifyContext(root);
        var anchor = ExpressionDesugarTempLifter.FindAnchor(root);
        var coalesceComposition = NullConditionalSyntaxFacts.IsE03TempInitializer(root);

        var plan = new ChainPlan
        {
            RootOriginal = root,
            Segments = segments,
            Terminal = terminal,
            ContextKind = contextKind,
            Shape = NullConditionalLoweringShape.TempTernary,
            Anchor = anchor,
            ResultType = resultType,
            TerminalValueType = terminalValueType,
            CastToNullable = castToNullable,
            NullableCastType = castType,
            Outcome = ChainOutcome.Lowered,
            SkipCode = null,
            IsCoalesceComposition = coalesceComposition,
        };

        ApplyDecisions(plan, context);
        context.Statistics.NullConditional.AddChainLength(plan.Length);
        return plan;
    }

    /// <summary>Type of a CA's Expression receiver, tolerating unresolved sites.</summary>
    private static ITypeSymbol? SafeReceiverType(
        SemanticModel model,
        ConditionalAccessExpressionSyntax ca)
    {
        var type = model.GetTypeInfo(ca.Expression).Type;
        if (type != null)
            return type;
        return model.GetOperation(ca.Expression)?.Type;
    }

    private static ChainTerminal ClassifyTerminal(
        ConditionalAccessExpressionSyntax root,
        ExpressionSyntax terminalNode,
        SemanticModel model)
    {
        if (terminalNode is InvocationExpressionSyntax invocation)
        {
            var invocationOp = model.GetOperation(invocation) as IInvocationOperation;
            var method = invocationOp?.TargetMethod;
            if (method != null
                && method.ContainingType.TypeKind == TypeKind.Delegate
                && (method.Name == "Invoke"
                    || method.Name == "BeginInvoke"
                    || method.Name == "EndInvoke"))
            {
                var isEvent = model.GetSymbolInfo(root.Expression).Symbol
                              is IEventSymbol;
                return new ChainTerminal
                {
                    Kind = ChainTerminalKind.Invoke,
                    ExpressionOriginal = terminalNode,
                    IsEvent = isEvent,
                };
            }

            return new ChainTerminal
            {
                Kind = ChainTerminalKind.Call,
                ExpressionOriginal = terminalNode,
                IsEvent = false,
            };
        }

        if (terminalNode is ElementBindingExpressionSyntax)
            return new ChainTerminal
            {
                Kind = ChainTerminalKind.Index,
                ExpressionOriginal = terminalNode,
                IsEvent = false,
            };

        if (terminalNode is AssignmentExpressionSyntax)
            return new ChainTerminal
            {
                Kind = ChainTerminalKind.Assign,
                ExpressionOriginal = terminalNode,
                IsEvent = false,
            };

        if (NullConditionalSyntaxFacts.FindRootBinding(terminalNode)
            is MemberBindingExpressionSyntax)
        {
            return new ChainTerminal
            {
                Kind = ChainTerminalKind.Member,
                ExpressionOriginal = terminalNode,
                IsEvent = false,
            };
        }

        return new ChainTerminal
        {
            Kind = ChainTerminalKind.Unknown,
            ExpressionOriginal = terminalNode,
            IsEvent = false,
        };
    }

    private static NullConditionalContextKind ClassifyContext(
        ConditionalAccessExpressionSyntax root)
    {
        var current = root.Parent;
        while (current is ParenthesizedExpressionSyntax)
            current = current.Parent;

        if (current is ExpressionStatementSyntax)
            return NullConditionalContextKind.VoidStatement;

        // Expression-bodied void method: public void M() => a?.M2();
        if (current is ArrowExpressionClauseSyntax { Parent: MethodDeclarationSyntax method }
            && method.ReturnType is PredefinedTypeSyntax predefined
            && predefined.Keyword.Kind() == SyntaxKind.VoidKeyword)
        {
            return NullConditionalContextKind.VoidStatement;
        }

        return NullConditionalContextKind.Value;
    }

    /// <summary>
    /// True when the terminal non-null type is a non-nullable value type and
    /// the chain result is <c>Nullable&lt;T&gt;</c> (design NC-9).
    /// </summary>
    private static bool NeedsNullableCast(
        ITypeSymbol? terminalType,
        ITypeSymbol? resultType)
    {
        if (terminalType is not { IsValueType: true })
            return false;
        if (terminalType is INamedTypeSymbol terminalNamed
            && terminalNamed.OriginalDefinition.SpecialType
               == SpecialType.System_Nullable_T)
        {
            return false;
        }

        return resultType is INamedTypeSymbol resultNamed
               && resultNamed.OriginalDefinition.SpecialType
                  == SpecialType.System_Nullable_T;
    }

    /// <summary>
    /// Runs the guard/shape decision table: defensive errors first, then
    /// deferrals (C51 / not-yet-landed shapes), then supported lowering.
    /// Reports diagnostics and mutates plan outcome/shape.
    /// </summary>
    private static void ApplyDecisions(
        ChainPlan plan,
        RuleAnalysisContext context)
    {
        var options = context.Options.NullConditional!;
        var strict = options.Strict;
        var root = plan.RootOriginal;

        // ---- Defensive / error guards ----
        if (NullConditionalSyntaxFacts.IsInRefContext(root))
        {
            Preserve(plan, NullConditionalDiagnosticCodes.CS2J0560);
            Report(context,
                NullConditionalDiagnostics.ResolveStrictError(strict),
                NullConditionalDiagnosticCodes.CS2J0560,
                NullConditionalDiagnostics.RefContextMessage,
                root);
            return;
        }

        if (!options.RewriteInExpressionTrees
            && ExpressionDesugarSyntaxFacts.IsInExpressionTree(
                context.SemanticModel, root))
        {
            Preserve(plan, NullConditionalDiagnosticCodes.CS2J0565);
            context.Statistics.NullConditional.AddSkip(
                NullConditionalDiagnosticCodes.CS2J0565);
            ReportInfo(context,
                NullConditionalDiagnosticCodes.CS2J0565,
                NullConditionalDiagnostics.ExpressionTreeMessage,
                root);
            return;
        }

        if (HasDynamicOrError(plan))
        {
            Preserve(plan, NullConditionalDiagnosticCodes.CS2J0559);
            Report(context,
                NullConditionalDiagnostics.ResolveStrictError(strict),
                NullConditionalDiagnosticCodes.CS2J0559,
                NullConditionalDiagnostics.DynamicMessage,
                root);
            return;
        }

        if (plan.Terminal.Kind == ChainTerminalKind.Unknown)
        {
            Preserve(plan, NullConditionalDiagnosticCodes.CS2J0564);
            Report(context,
                NullConditionalDiagnostics.ResolveStrictError(strict),
                NullConditionalDiagnosticCodes.CS2J0564,
                string.Format(
                    NullConditionalDiagnostics.UnknownTerminalMessage,
                    plan.Terminal.ExpressionOriginal.ToString()),
                root);
            return;
        }

        // ---- Assignment terminal deferred to C51 ----
        if (plan.Terminal.Kind == ChainTerminalKind.Assign)
        {
            plan.Outcome = ChainOutcome.Preserved;
            plan.SkipCode = null;
            context.Statistics.NullConditional.DeferredForC51Sites++;
            return;
        }

        var anchor = plan.Anchor;
        var isVoid = plan.ContextKind == NullConditionalContextKind.VoidStatement;

        // ---- Void/statement context: segment temps + nested if (NC-3 void, NC-5 void, NC-8) ----
        if (isVoid)
        {
            if (anchor is ExpressionStatementSyntax)
            {
                LowerStatementIf(plan, context);
            }
            else
            {
                // No spliceable expression-statement anchor (arrow-bodied void,
                // field initializer...): keep the site with a pointer.
                var code = anchor == null
                    ? NullConditionalDiagnosticCodes.CS2J0557
                    : NullConditionalDiagnosticCodes.CS2J0558;
                Preserve(plan, code);
                Report(context,
                    NullConditionalDiagnostics.ResolveStrictError(strict),
                    code,
                    anchor == null
                        ? string.Format(
                            NullConditionalDiagnostics.UnliftableMessage,
                            plan.RootOriginal.ToString())
                        : NullConditionalDiagnostics.AnchorlessVoidMessage,
                    root);
            }

            return;
        }

        // ---- Multi-segment value: guarded prefetch + nested ternary (NC-2) ----
        if (plan.Segments.Count > 1)
        {
            if (anchor is LocalDeclarationStatementSyntax or ReturnStatementSyntax)
            {
                LowerGuardedBlock(plan, context);
            }
            else
            {
                Preserve(plan, NullConditionalDiagnosticCodes.CS2J0557);
                Report(context,
                    NullConditionalDiagnostics.ResolveStrictError(strict),
                    NullConditionalDiagnosticCodes.CS2J0557,
                    string.Format(
                        NullConditionalDiagnostics.UnliftableMessage,
                        plan.RootOriginal.ToString()),
                    root);
            }

            return;
        }

        // ---- Single-segment value ----
        switch (plan.Terminal.Kind)
        {
            case ChainTerminalKind.Member:
                DecideSingleMember(plan, context);
                break;

            case ChainTerminalKind.Call:
            case ChainTerminalKind.Index:
            case ChainTerminalKind.Invoke:
                if (anchor == null)
                {
                    Preserve(plan, NullConditionalDiagnosticCodes.CS2J0557);
                    Report(context,
                        NullConditionalDiagnostics.ResolveStrictError(strict),
                        NullConditionalDiagnosticCodes.CS2J0557,
                        string.Format(
                            NullConditionalDiagnostics.UnliftableMessage,
                            plan.RootOriginal.ToString()),
                        root);
                    break;
                }

                LowerTempTernary(plan, context);
                break;
        }
    }

    /// <summary>NC-1 single-segment member: inline whitelist, else segment temp.</summary>
    private static void DecideSingleMember(ChainPlan plan, RuleAnalysisContext context)
    {
        var strict = context.Options.NullConditional!.Strict;
        var receiver = plan.RootOriginal.Expression;
        var whitelist = NullConditionalSyntaxFacts.IsInlineWhitelistReceiver(
            receiver, context.SemanticModel);

        var arguments = plan.Terminal.ExpressionOriginal
            .DescendantNodesAndSelf()
            .OfType<ArgumentSyntax>();

        if (plan.Anchor == null)
        {
            // Anchor-free: only the zero-declaration inline form is possible.
            if (whitelist
                && NullConditionalSyntaxFacts.AreArgumentsInlineSafe(
                    arguments, context.SemanticModel))
            {
                LowerInline(plan, context);
            }
            else
            {
                Preserve(plan, NullConditionalDiagnosticCodes.CS2J0557);
                Report(context,
                    NullConditionalDiagnostics.ResolveStrictError(strict),
                    NullConditionalDiagnosticCodes.CS2J0557,
                    string.Format(
                        NullConditionalDiagnostics.UnliftableMessage,
                        plan.RootOriginal.ToString()),
                    plan.RootOriginal);
            }

            return;
        }

        if (whitelist)
            LowerInline(plan, context);
        else
            LowerTempTernary(plan, context);
    }

    private static bool HasDynamicOrError(ChainPlan plan)
    {
        foreach (var segment in plan.Segments)
        {
            if (segment.ReceiverType is IErrorTypeSymbol
                or { TypeKind: TypeKind.Dynamic })
            {
                return true;
            }
        }

        if (plan.ResultType is IErrorTypeSymbol
            or { TypeKind: TypeKind.Dynamic })
        {
            return true;
        }

        if (plan.TerminalValueType is IErrorTypeSymbol
            or { TypeKind: TypeKind.Dynamic })
        {
            return true;
        }

        return false;
    }

    private static void LowerInline(ChainPlan plan, RuleAnalysisContext context)
    {
        plan.Outcome = ChainOutcome.Lowered;
        plan.Shape = NullConditionalLoweringShape.Inline;
        AccumulateLowered(plan, context);
    }

    private static void LowerTempTernary(ChainPlan plan, RuleAnalysisContext context)
    {
        plan.Outcome = ChainOutcome.Lowered;
        plan.Shape = NullConditionalLoweringShape.TempTernary;
        AccumulateLowered(plan, context);
        context.Statistics.NullConditional.SegmentTempsLifted += plan.Segments.Count;
    }

    private static void LowerStatementIf(ChainPlan plan, RuleAnalysisContext context)
    {
        plan.Outcome = ChainOutcome.Lowered;
        plan.Shape = NullConditionalLoweringShape.StatementIf;
        AccumulateLowered(plan, context);
        var stats = context.Statistics.NullConditional;
        stats.VoidStatementSites++;
        stats.SegmentTempsLifted += plan.Segments.Count;
    }

    private static void LowerGuardedBlock(ChainPlan plan, RuleAnalysisContext context)
    {
        plan.Outcome = ChainOutcome.Lowered;
        plan.Shape = NullConditionalLoweringShape.GuardedBlock;
        AccumulateLowered(plan, context);
        context.Statistics.NullConditional.SegmentTempsLifted += plan.Segments.Count;
    }

    private static void AccumulateLowered(ChainPlan plan, RuleAnalysisContext context)
    {
        var stats = context.Statistics.NullConditional;

        switch (plan.Terminal.Kind)
        {
            case ChainTerminalKind.Member:
            case ChainTerminalKind.Call:
                stats.ConditionalAccessSites++;
                break;

            case ChainTerminalKind.Index:
                stats.ConditionalIndexSites++;
                break;

            case ChainTerminalKind.Invoke:
                stats.InvokeSites++;
                if (plan.Terminal.IsEvent)
                    stats.EventSites++;
                else
                    stats.DelegateSites++;
                break;
        }

        if (plan.Shape == NullConditionalLoweringShape.Inline)
            stats.InlinedSites++;

        ReportInfo(context,
            NullConditionalDiagnosticCodes.CS2J0550,
            NullConditionalDiagnostics.SiteMessage,
            plan.RootOriginal);

        if (plan.CastToNullable)
        {
            stats.LiftedValueTypeSites++;
            ReportInfo(context,
                NullConditionalDiagnosticCodes.CS2J0554,
                NullConditionalDiagnostics.LiftedSiteMessage,
                plan.RootOriginal);
        }

        if (plan.IsCoalesceComposition)
        {
            stats.CoalesceCompositionSites++;
            ReportInfo(context,
                NullConditionalDiagnosticCodes.CS2J0567,
                NullConditionalDiagnostics.CoalesceCompositionMessage,
                plan.RootOriginal);
        }
    }

    private static void Preserve(ChainPlan plan, string code)
    {
        plan.Outcome = ChainOutcome.Preserved;
        plan.SkipCode = code;
    }

    private static void Report(
        RuleAnalysisContext context,
        ExpressionDesugarSeverity severity,
        string code,
        string message,
        SyntaxNode? node)
    {
        context.Report(severity, code, message, node);
        if (severity != ExpressionDesugarSeverity.Info)
            context.Statistics.NullConditional.AddSkip(code);
    }

    private static void ReportInfo(
        RuleAnalysisContext context,
        string code,
        string message,
        SyntaxNode? node)
        => context.Report(ExpressionDesugarSeverity.Info, code, message, node);
}
