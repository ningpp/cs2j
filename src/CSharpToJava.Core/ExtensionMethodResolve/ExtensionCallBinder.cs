using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExtensionMethodResolve;

/// <summary>
/// Single-file analysis (EM-BIND): consumes <see cref="SemanticModel"/>
/// facts and classifies reduced extension sites. SemanticModel is the single
/// source of truth (design §2); syntax shape never selects a site, it only
/// classifies the proven bound symbol.
///
/// M2 coverage:
/// - ordinary member-access reduced calls, including <c>this ref</c>/<c>this
/// /// in</c> receivers (EM-REF: ref/in argument emission, CS2J1204 only when
///   the argument lvalue cannot be proven);
/// - null-conditional reduced chains (<c>r?.M()</c>/<c>r?.Go()?.Chain()</c>):
///   EM-COND lift plans (single-evaluation temps + conditional static calls);
///   CS2J1205 for shapes proven not safely liftable; assert-only inside
///   expression trees;
/// - non-call member access reduced method groups (EM-GROUP): capture-lambda
///   plans when the delegate target and safe receiver capture are provable;
///   CS2J1207 for ambiguous/unmatched/interleaved shapes;
/// - expression-tree flag for the identity assertion in the rewriter;
/// - C#14 extension blocks stay an unconditional syntax gate (CS2J1206).
/// </summary>
internal sealed class ExtensionCallBinder
{
    public const string ExternalRewriteDisabledReason = "external-metadata-rewrite-disabled";
    public const string ExprTreeReportOnlyReason = "expression-tree-report-only";
    public const string NullConditionalInExpressionTreeReason = "null-conditional-in-expression-tree";
    public const string NullConditionalLinqOwnedReason = "null-conditional-linq-owned";

    /// <summary>M4 EM-LINQ: user extension site shares a fluent chain with a LINQ-owned call.</summary>
    public const string MixedLinqChainReason = "mixed-linq-chain-conservative-skip";

    /// <summary>M4: site not selected because its host is overridden to skip.</summary>
    public const string HostOverrideSkipReason = "host-override-skip";

    private readonly List<ExtensionCallBinding> _bindings = new();
    private readonly List<ExtensionConditionalPlan> _conditionalPlans = new();
    private readonly List<ExtensionGroupPlan> _groupPlans = new();
    private readonly List<InvocationExpressionSyntax> _refMismatchSites = new();
    private readonly List<InvocationExpressionSyntax> _exprTreeReportOnlySites = new();
    private readonly List<InvocationExpressionSyntax> _mixedChainSkippedSites = new();
    private readonly List<InvocationExpressionSyntax> _overrideSkippedSites = new();
    private readonly List<ExtensionDiagnostic> _diagnostics = new();
    private readonly ExtensionMethodResolveStatistics _statistics = new();
    private readonly ExtensionTempAllocator _allocator;

    public ExtensionCallBinder(SemanticModel model, ExtensionMethodResolveOptions options, bool suppressExtensionBlockGate = false, IReadOnlySet<string>? extensionMethodNames = null)
    {
        Model = model;
        Options = options;
        SuppressExtensionBlockGate = suppressExtensionBlockGate;
        ExtensionMethodNames = extensionMethodNames;
        _allocator = new ExtensionTempAllocator(model.SyntaxTree.GetCompilationUnitRoot());
    }

    /// <summary>
    /// Names of every extension method reachable from the compilation (source
    /// declarations with a <c>this</c> first parameter plus metadata
    /// <see cref="IMethodSymbol.IsExtensionMethod"/> members), or null to keep
    /// the unfiltered historical sweep. A reduced extension site is only ever
    /// reachable through one of these names, so a node whose accessed name is
    /// absent can be skipped without a semantic query.
    /// </summary>
    public IReadOnlySet<string>? ExtensionMethodNames { get; }

    /// <summary>
    /// When true, the unconditional CS2J1206 syntax gate for extension blocks
    /// is suppressed because C49 materialization takes over for this tree.
    /// Every other behavior is unchanged.
    /// </summary>
    public bool SuppressExtensionBlockGate { get; }

    public SemanticModel Model { get; }

    public ExtensionMethodResolveOptions Options { get; }

    public IReadOnlyList<ExtensionCallBinding> Bindings => _bindings;
    public IReadOnlyList<ExtensionConditionalPlan> ConditionalPlans => _conditionalPlans;
    public IReadOnlyList<ExtensionGroupPlan> GroupPlans => _groupPlans;
    public IReadOnlyList<InvocationExpressionSyntax> RefMismatchSites => _refMismatchSites;
    public IReadOnlyList<InvocationExpressionSyntax> ExprTreeReportOnlySites => _exprTreeReportOnlySites;
    public IReadOnlyList<InvocationExpressionSyntax> MixedChainSkippedSites => _mixedChainSkippedSites;
    public IReadOnlyList<InvocationExpressionSyntax> OverrideSkippedSites => _overrideSkippedSites;
    public IReadOnlyList<ExtensionDiagnostic> Diagnostics => _diagnostics;
    public ExtensionMethodResolveStatistics Statistics => _statistics;

    /// <summary>Analyze one syntax tree (bindings/diagnostics sorted by span).</summary>
    public void Analyze()
    {
        var root = Model.SyntaxTree.GetCompilationUnitRoot();

        // EM-EXT14: syntax-driven (see M1 probe ②): block members bind as
        // Ordinary inside a TypeKind.Extension container, so binding alone
        // would silently miss every block site. C49 materialization suppresses
        // this gate for the tree (it owns the blocks instead).
        if (!SuppressExtensionBlockGate)
        {
          foreach (var block in root.DescendantNodes()
                     .Where(n => n.IsKind(SyntaxKind.ExtensionBlockDeclaration)))
        {
            AddDiagnostic(
                ExtensionSeverity.Error,
                ExtensionRule.Ext14,
                ExtensionMethodCodes.CSharp14ExtensionBlock,
                "C# 14 'extension' block (extension property/operator/static extension member) is not supported "
                + "by the classic static-class + 'this' parameter form; it will be handled by C49. "
                + "The file is left unchanged.",
                block.GetLocation());
            AddSkipReason(ExtensionMethodCodes.CSharp14ExtensionBlock);
        }
        }

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            // A reduced extension invocation is always receiver-style (member
            // access / member binding); any other shape can never bind as
            // ReducedExtension, and the unbound-classification branch below is
            // receiver-style only, so it is safe to skip without a query.
            if (invocation.Expression is not (MemberAccessExpressionSyntax or MemberBindingExpressionSyntax))
            {
                continue;
            }

            // Name prefilter: only applied when the caller does not request per-
            // site detail (ReportSiteDetail is exactly the CLI verbose flag). A
            // receiver-style call whose accessed name is not an extension-method
            // name can only ever produce the Info-level "receiver-style call
            // unbound" trace, and Info diagnostics are filtered from the printed
            // output unless verbose — so this skip is observationally a no-op
            // when detail is off, and is disabled when detail is on.
            if (ExtensionMethodNames is not null
                && !Options.ReportSiteDetail
                && !InvokedNameCouldBeExtensionName(invocation))
            {
                continue;
            }

            var info = Model.GetSymbolInfo(invocation);
            if (info.Symbol is IMethodSymbol
                {
                    MethodKind: MethodKind.ReducedExtension,
                    ReducedFrom: { } unreducedSymbol,
                } reducedSymbol)
            {
                _statistics.CallsitesBoundReduced++;

                // EM-COND owns every invocation inside a WhenNotNull segment
                // (spine and argument-position calls alike).
                if (IsNullConditionalReducedSite(invocation))
                {
                    continue;
                }

                ClassifyBound(invocation, reducedSymbol, unreducedSymbol);
                continue;
            }

            if (info.Symbol is null && IsReceiverStyleCall(invocation))
            {
                ClassifyUnbound(invocation, info);
            }
        }

        // EM-COND: outermost conditionals only; nested ?. chains are segments
        // of the outer plan.
        foreach (var conditional in root.DescendantNodes()
                     .OfType<ConditionalAccessExpressionSyntax>()
                     .Where(c => !c.Ancestors().OfType<ConditionalAccessExpressionSyntax>().Any()))
        {
            AnalyzeConditionalChain(conditional);
        }

        // EM-GROUP: member accesses that are not the callee of an invocation.
        foreach (var memberAccess in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (memberAccess.Parent is InvocationExpressionSyntax parentInvocation
                && parentInvocation.Expression == memberAccess)
            {
                continue;
            }

            // AnalyzeMethodGroup produces no diagnostic/statistic for an access
            // that cannot be a reduced extension method group, so a name-filtered
            // skip is exactly equivalent.
            if (ExtensionMethodNames is not null
                && !ExtensionMethodNames.Contains(memberAccess.Name.Identifier.ValueText))
            {
                continue;
            }

            AnalyzeMethodGroup(memberAccess);
        }

        _bindings.Sort((a, b) => a.Invocation.SpanStart.CompareTo(b.Invocation.SpanStart));
        _conditionalPlans.Sort((a, b) => a.SpanStart.CompareTo(b.SpanStart));
        _groupPlans.Sort((a, b) => a.SpanStart.CompareTo(b.SpanStart));
        _diagnostics.Sort((a, b) => (a.Line ?? 0).CompareTo(b.Line ?? 0));
    }

    // ── Ordinary reduced invocations (EM-CALL/EM-REF/EM-EXPRTREE) ───────────

    private void ClassifyBound(
        InvocationExpressionSyntax invocation,
        IMethodSymbol reduced,
        IMethodSymbol unreduced)
    {
        var host = unreduced.ContainingType;
        var hostDisplayName = host.ToDisplayString();

        if (Options.LinqHostSet.Contains(hostDisplayName))
        {
            _statistics.LinqOwnedSkipped++;
            return;
        }

        var hostOverride = Options.HostOverrides?.TryGetAction(hostDisplayName);

        // M4 overrides (skip): the reduced site is never selected; an EM001
        // Info trace always records who asked for the skip.
        if (hostOverride == ExtensionHostOverrideAction.Skip)
        {
            _overrideSkippedSites.Add(invocation);
            _statistics.HostOverrideSitesSkipped++;
            AddSkipReason(HostOverrideSkipReason);
            if (Options.ReportInfo)
            {
                AddDiagnostic(
                    ExtensionSeverity.Info,
                    ExtensionRule.Linq,
                    ExtensionMethodCodes.Info,
                    $"Extension call '{invocation.ToString()}' (host {hostDisplayName}) skipped by host overrides (action=skip); site left unchanged.",
                    invocation.GetLocation());
            }

            return;
        }

        // M4 EM-LINQ mixed-chain finalization: a user extension node on a
        // fluent member-access chain that also contains a LINQ-owned call is
        // conservatively left as sugar together with the whole chain. An
        // isolated explicitization turns the node into a non-member-access
        // child; LinqRewriter's receiver backtracking
        // (LinqRewriter.TryVisitInvocationExpression: member-access cast + the
        // `while (c.Expression is MemberAccessExpressionSyntax)` chain walk)
        // stops there, silently changing LINQ processing. Force overrides.
        var inMixedLinqChain = IsInMixedLinqChain(invocation);
        if (inMixedLinqChain)
        {
            if (hostOverride == ExtensionHostOverrideAction.Force)
            {
                _statistics.HostOverrideSitesForced++;
            }
            else
            {
                _mixedChainSkippedSites.Add(invocation);
                _statistics.MixedLinqChainSites++;
                AddSkipReason(MixedLinqChainReason);
                if (Options.ReportInfo)
                {
                    AddDiagnostic(
                        ExtensionSeverity.Info,
                        ExtensionRule.Linq,
                        ExtensionMethodCodes.Info,
                        $"User extension call '{invocation.ToString()}' shares a member-access chain with a LINQ-owned call; "
                        + "the whole chain is conservatively left as sugar (mixed-linq-chain-conservative-skip; design M4 §1). "
                        + "Use a host force override to rewrite this site explicitly.",
                        invocation.GetLocation());
                }

                return;
            }
        }

        var receiver = (invocation.Expression as MemberAccessExpressionSyntax)?.Expression;
        var firstParameter = unreduced.Parameters.Length > 0 ? unreduced.Parameters[0] : null;
        var receiverRefKind = firstParameter?.RefKind ?? RefKind.None;

        if (receiverRefKind != RefKind.None)
        {
            _statistics.RefReceiverSites++;

            // in accepts every expression (rvalues included); ref/scoped-ref
            // needs a provably writable lvalue the source itself compiled with.
            var provable = receiverRefKind == RefKind.In
                || (receiver is not null
                    && ExtensionRefReceiverFacts.IsProvableWritableRefReceiver(receiver, Model));
            if (!provable)
            {
                _refMismatchSites.Add(invocation);
                AddDiagnostic(
                    WarningOrError(),
                    ExtensionRule.Ref,
                    ExtensionMethodCodes.RefMismatch,
                    $"Extension call '{invocation.ToString()}' takes a '{receiverRefKind.ToString().ToLowerInvariant()}' receiver but the argument cannot be proven a writable variable; site left unchanged.",
                    invocation.GetLocation());
                AddSkipReason(ExtensionMethodCodes.RefMismatch);
                return;
            }
        }

        var isSourceHost = host.Locations.Length > 0 && host.Locations[0].IsInSource;
        ReceiverClassification classification;
        ExternalHostMappingKind? mappingKind = null;

        if (isSourceHost)
        {
            classification = Options.IsSupportLibraryHost?.Invoke(host) == true
                ? ReceiverClassification.InCompilationSupport
                : ReceiverClassification.InCompilationUser;
            _statistics.InCompilationHostSites++;
            if (classification == ReceiverClassification.InCompilationSupport)
            {
                _statistics.InCompilationSupportHostSites++;
            }
            else
            {
                _statistics.InCompilationUserHostSites++;
            }
        }
        else
        {
            classification = ReceiverClassification.ExternalMetadata;
            _statistics.ExternalHostSites++;

            // EM-EXTERNAL grading (M3): the CLI adapter injects a
            // TypeMappings-backed grader; Core only consumes the label. A null
            // grader means grading is unavailable (Core-only tests), so the M1/M2
            // silent-rewrite behavior is preserved.
            mappingKind = Options.ExternalHostMapper?.Invoke(host);
            if (mappingKind == ExternalHostMappingKind.Mapped)
            {
                _statistics.ExternalHostMappedSites++;
            }
            else if (mappingKind == ExternalHostMappingKind.Unknown)
            {
                _statistics.ExternalHostUnmappedSites++;
                AddDiagnostic(
                    WarningOrError(),
                    ExtensionRule.External,
                    ExtensionMethodCodes.ExternalHostNoMapping,
                    $"External metadata extension host '{hostDisplayName}' has no Java landing in TypeMappings; "
                    + $"call site '{invocation.ToString()}' is still rewritten to an explicit static call, "
                    + "and the downstream Java conversion has no mapped target for the host.",
                    invocation.GetLocation());
            }

            if (!Options.RewriteExternalMetadataHosts)
            {
                // M4 force override explicitly opts this metadata host back in.
                if (hostOverride == ExtensionHostOverrideAction.Force)
                {
                    _statistics.HostOverrideSitesForced++;
                }
                else
                {
                    AddSkipReason(ExternalRewriteDisabledReason);
                    if (Options.ReportInfo)
                    {
                        AddDiagnostic(
                            ExtensionSeverity.Info,
                            ExtensionRule.External,
                            ExtensionMethodCodes.Info,
                            $"Metadata-host extension call '{invocation.ToString()}' (host {hostDisplayName}) skipped because RewriteExternalMetadataHosts=false.",
                            invocation.GetLocation());
                    }

                    return;
                }
            }
        }

        var isInExpressionTree = ExtensionExpressionTreeFacts
            .IsInExpressionTreeLambda(invocation, Model);
        if (isInExpressionTree)
        {
            _statistics.ExpressionTreeSitesChecked++;

            if (Options.ExpressionTreePolicy == ExpressionTreeRewritePolicy.ReportOnly)
            {
                _exprTreeReportOnlySites.Add(invocation);
                AddSkipReason(ExprTreeReportOnlyReason);
                if (Options.ReportInfo)
                {
                    AddDiagnostic(
                        ExtensionSeverity.Info,
                        ExtensionRule.ExprTree,
                        ExtensionMethodCodes.Info,
                        $"Expression-tree extension call '{invocation.ToString()}' left as sugar (ExpressionTreePolicy=ReportOnly).",
                        invocation.GetLocation());
                }

                return;
            }
        }

        var explicitTypeArguments = invocation.Expression is MemberAccessExpressionSyntax
        {
            Name: GenericNameSyntax genericName,
        } && genericName.TypeArgumentList.Arguments.Count > 0;

        // EM001 per-site triage detail (verbose-only at the CLI surface):
        // host full name, classification, mapping grade, generic/ref/ET flags.
        if (Options.ReportInfo && Options.ReportSiteDetail)
        {
            var mappingText = classification == ReceiverClassification.ExternalMetadata
                ? (mappingKind?.ToString() ?? "grading-unavailable")
                : "-";
            AddDiagnostic(
                ExtensionSeverity.Info,
                ExtensionRule.Bind,
                ExtensionMethodCodes.Info,
                $"EM001 '{invocation.ToString()}': host={hostDisplayName}, classification={classification}, "
                + $"mapping={mappingText}, explicitTypeArgs={explicitTypeArguments}, genericHost={host.IsGenericType}, "
                + $"receiverRef={receiverRefKind.ToString().ToLowerInvariant()}, inExpressionTree={isInExpressionTree}.",
                invocation.GetLocation());
        }

        _bindings.Add(new ExtensionCallBinding(
            invocation,
            reduced,
            unreduced,
            classification,
            receiverRefKind,
            isInExpressionTree,
            explicitTypeArguments));
    }

    private void ClassifyUnbound(InvocationExpressionSyntax invocation, SymbolInfo info)
    {
        // Dynamic dispatch never binds extension methods.
        if (TryGetReceiverExpression(invocation) is { } receiverExpression
            && Model.GetTypeInfo(receiverExpression).Type?.TypeKind == TypeKind.Dynamic)
        {
            return;
        }

        var extensionCandidates = info.CandidateSymbols
            .OfType<IMethodSymbol>()
            .Where(m => m.IsExtensionMethod || m.MethodKind == MethodKind.ReducedExtension)
            .ToList();

        if (info.CandidateReason == CandidateReason.Ambiguous
            || (info.CandidateReason == CandidateReason.OverloadResolutionFailure
                && extensionCandidates.Select(DescribeCandidateHost).Distinct(StringComparer.Ordinal).Count() >= 2))
        {
            var candidates = extensionCandidates
                .Select(DescribeCandidateHost)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            AddDiagnostic(
                WarningOrError(),
                ExtensionRule.Bind,
                ExtensionMethodCodes.Ambiguous,
                $"Ambiguous extension call '{invocation.ToString()}': candidates [{string.Join("; ", candidates)}]; site left unchanged.",
                invocation.GetLocation());
            AddSkipReason(ExtensionMethodCodes.Ambiguous);
            return;
        }

        if (extensionCandidates.Count > 0)
        {
            // CS2J1290 (design §4.2-5): in single-file/loose-directory mode the
            // compilation has no project/NuGet reference set, so a failed bind
            // with extension candidates cannot be distinguished from a true
            // reference gap. Conservatively leave the site unchanged and warn;
            // this code is never strict-escalated (design §6).
            if (Options.SingleFileIncompleteReferences)
            {
                _statistics.SingleFileDeferredSites++;
                AddDiagnostic(
                    ExtensionSeverity.Warning,
                    ExtensionRule.Bind,
                    ExtensionMethodCodes.SingleFileIncompleteBinding,
                    $"Single-file mode: extension-like call '{invocation.ToString()}' cannot be conclusively bound "
                    + $"({info.CandidateReason}) because project/NuGet references are unavailable; "
                    + "site conservatively left unchanged. Run via a project/solution to enable full binding.",
                    invocation.GetLocation());
                AddSkipReason(ExtensionMethodCodes.SingleFileIncompleteBinding);
                return;
            }

            AddDiagnostic(
                WarningOrError(),
                ExtensionRule.Bind,
                ExtensionMethodCodes.Unbound,
                $"Extension-like call '{invocation.ToString()}' failed to bind ({info.CandidateReason}); references may be incomplete. Site left unchanged.",
                invocation.GetLocation());
            AddSkipReason(ExtensionMethodCodes.Unbound);
        }
        else if (Options.ReportInfo)
        {
            AddDiagnostic(
                ExtensionSeverity.Info,
                ExtensionRule.Bind,
                ExtensionMethodCodes.Info,
                $"Receiver-style call '{invocation.ToString()}' unbound ({info.CandidateReason}); not selected.",
                invocation.GetLocation());
        }
    }

    // ── EM-COND ──────────────────────────────────────────────────────────────

    private void AnalyzeConditionalChain(ConditionalAccessExpressionSyntax outer)
    {
        var segments = BuildSegments(outer);
        if (segments.Count == 0 || segments[0].SpineCalls.Count == 0)
        {
            return;
        }

        var spineCalls = segments.SelectMany(s => s.SpineCalls).ToList();
        var plan = new ExtensionConditionalPlan
        {
            OuterConditional = outer,
            Segments = segments,
            SiteAnnotation = new SyntaxAnnotation("C19", "ExtensionConditionalSite"),
            AnchorAnnotation = new SyntaxAnnotation("C19", "ExtensionConditionalAnchor"),
            TempNames = Array.Empty<string>(),
            Liftable = true,
            ResultType = null,
            IsVoid = false,
            Anchor = ExtensionAnchor.None,
        };

        // LINQ-owned spine: mixed or pure LINQ chains stay sugar for E16/C26;
        // gate 1 exempts them via the host set, no CS2J1205.
        if (spineCalls.Any(c => Options.LinqHostSet.Contains(c.Unreduced.ContainingType.ToDisplayString())))
        {
            _statistics.LinqOwnedSkipped++;
            AddSkipReason(NullConditionalLinqOwnedReason);
            if (Options.ReportInfo)
            {
                AddDiagnostic(
                    ExtensionSeverity.Info,
                    ExtensionRule.Linq,
                    ExtensionMethodCodes.Info,
                    $"Null-conditional extension chain '{outer.ToString()}' contains a LINQ host; site left unchanged.",
                    outer.GetLocation());
            }

            return;
        }

        // Foreign reduced call inside an owned segment (e.g. in an argument):
        // lifting only the spine would not converge the segment, and temp
        // hoisting across argument positions would reorder evaluation. Each
        // segment owns exactly its segmentRoot subtree (segment roots are
        // disjoint siblings along the nested WhenNotNull chain), so the check
        // runs per segment to catch foreign calls in deeper segments too.
        // Segment roots are disjoint siblings (nested conditional's Expression
        // vs WhenNotNull), so segmentRoot.DescendantNodesAndSelf is exactly the
        // physical region owned by the segment.
        var spineSet = spineCalls.Select(c => c.Invocation).ToHashSet();
        var foreign = segments
            .SelectMany(segment => segment.Root.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>()
                .Where(i => !spineSet.Contains(i)))
            .FirstOrDefault(i => Model.GetSymbolInfo(i).Symbol
                is IMethodSymbol { MethodKind: MethodKind.ReducedExtension });
        if (foreign is not null)
        {
            MarkConditionalDeferred(
                plan,
                "a nested extension call inside the chain is not on its spine (argument position)",
                outer);
            _conditionalPlans.Add(plan);
            return;
        }

        // ref/in receiver on a ?. chain (probe: such sources don't compile in
        // the legal shapes, but bind facts may still surface under errors).
        var refSpineCall = spineCalls.FirstOrDefault(c => c.ReceiverRefKind != RefKind.None);
        if (refSpineCall is not null)
        {
            MarkConditionalDeferred(
                plan,
                $"spine call requires a '{refSpineCall.ReceiverRefKind.ToString().ToLowerInvariant()}' receiver",
                outer);
            _conditionalPlans.Add(plan);
            return;
        }

        var resultType = segments[^1].ResultType;
        var isVoid = resultType is null || resultType.SpecialType == SpecialType.System_Void;
        if (isVoid)
        {
            resultType = null;
        }

        // Expression tree: assertion-only zone (no new locals, no lift).
        if (ExtensionExpressionTreeFacts.IsInExpressionTreeLambda(outer, Model))
        {
            _statistics.ConditionalChainsDeferred++;
            AddSkipReason(NullConditionalInExpressionTreeReason);
            if (Options.ReportInfo)
            {
                AddDiagnostic(
                    ExtensionSeverity.Info,
                    ExtensionRule.ExprTree,
                    ExtensionMethodCodes.Info,
                    $"Null-conditional extension chain '{outer.ToString()}' is inside an expression tree; assert-only, site unchanged.",
                    outer.GetLocation());
            }

            _conditionalPlans.Add(plan with
            {
                Liftable = false,
                SkipCode = NullConditionalInExpressionTreeReason,
                IsVoid = isVoid,
                ResultType = resultType,
            });
            return;
        }

        var anchor = ExtensionTempLift.ResolveAnchor(outer, Model);
        var dominant = ExtensionTempLift.IsAtDominantExpressionPosition(outer);
        var voidContext = isVoid && (anchor.Kind == ExtensionAnchorKind.Statement && dominant
            || anchor.Kind == ExtensionAnchorKind.ArrowClause && anchor.ReturnsVoid
            || anchor.Kind == ExtensionAnchorKind.ExpressionLambda && anchor.ReturnsVoid);
        var valueContext = !isVoid && dominant
            && anchor.Kind is ExtensionAnchorKind.Statement
                or ExtensionAnchorKind.ArrowClause
                or ExtensionAnchorKind.ExpressionLambda;

        if (anchor.Kind == ExtensionAnchorKind.None)
        {
            MarkConditionalDeferred(
                plan,
                "no statement anchor (field/property initializer, query clause, attribute, ...)",
                outer);
            _conditionalPlans.Add(plan with { IsVoid = isVoid, ResultType = resultType });
            return;
        }

        if (!voidContext && !valueContext)
        {
            MarkConditionalDeferred(
                plan,
                "site is not the dominant expression of its statement (argument/interleaved position)",
                outer);
            _conditionalPlans.Add(plan with { IsVoid = isVoid, ResultType = resultType, Anchor = anchor });
            return;
        }

        var tempCount = 1 + Math.Max(0, segments.Count - 1);
        var tempNames = Enumerable.Range(0, tempCount)
            .Select(_ => _allocator.Allocate("c"))
            .ToList();

        _conditionalPlans.Add(plan with
        {
            IsVoid = isVoid,
            ResultType = resultType,
            Anchor = anchor,
            TempNames = tempNames,
        });
        _statistics.ConditionalChainsLifted++;
    }

    /// <summary>
    /// Build the ordered segment list of an outermost conditional. Segment
    /// walking rule: <c>r?.Go()?.Chain()</c> is outer conditional with
    /// WhenNotNull = inner conditional; the outer segment's branch root is the
    /// inner conditional's Expression (the member-binding origin invocation).
    /// </summary>
    private List<ExtensionConditionalSegment> BuildSegments(ConditionalAccessExpressionSyntax outer)
    {
        var segments = new List<ExtensionConditionalSegment>();
        var conditional = outer;
        while (true)
        {
            ExpressionSyntax segmentRoot;
            ConditionalAccessExpressionSyntax? next = null;
            if (conditional.WhenNotNull is ConditionalAccessExpressionSyntax nested)
            {
                segmentRoot = nested.Expression;
                next = nested;
            }
            else
            {
                segmentRoot = conditional.WhenNotNull;
            }

            var origin = FindOriginInvocation(segmentRoot);
            if (origin is null)
            {
                break;
            }

            var spine = BuildSpineCalls(segmentRoot, origin);
            var segmentType = Model.GetTypeInfo(conditional).Type
                ?? Model.GetTypeInfo(conditional).ConvertedType;
            segments.Add(new ExtensionConditionalSegment(conditional, segmentRoot, origin, spine, segmentType));

            if (next is null)
            {
                break;
            }

            conditional = next;
        }

        return segments;
    }

    /// <summary>The member-binding invocation of a segment (<c>.M()</c>; deepest in <c>.Go().N()</c>).</summary>
    private static InvocationExpressionSyntax? FindOriginInvocation(ExpressionSyntax segmentRoot)
    {
        if (segmentRoot is InvocationExpressionSyntax self
            && self.Expression is MemberBindingExpressionSyntax)
        {
            return self;
        }

        return segmentRoot.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(i => i.Expression is MemberBindingExpressionSyntax);
    }

    private List<ConditionalSpineCall> BuildSpineCalls(
        ExpressionSyntax segmentRoot,
        InvocationExpressionSyntax origin)
    {
        var calls = new List<ConditionalSpineCall>();
        var current = origin;
        while (true)
        {
            if (Model.GetSymbolInfo(current).Symbol is IMethodSymbol
                {
                    MethodKind: MethodKind.ReducedExtension,
                } reduced
                && reduced.ReducedFrom is { } unreduced)
            {
                calls.Add(new ConditionalSpineCall(
                    current,
                    reduced,
                    unreduced,
                    unreduced.Parameters.Length > 0 ? unreduced.Parameters[0].RefKind : RefKind.None,
                    current == origin));
            }

            if (current == segmentRoot)
            {
                break;
            }

            if (current.Parent is MemberAccessExpressionSyntax { Expression: { } prefix } memberAccess
                && prefix == current
                && memberAccess.Parent is InvocationExpressionSyntax nextInvocation
                && nextInvocation.Expression == memberAccess)
            {
                current = nextInvocation;
                continue;
            }

            break;
        }

        return calls;
    }

    /// <summary>
    /// Physical segment ownership: an invocation belongs to the outermost
    /// chain when it is within its span and NOT inside any descendant
    /// conditional's WhenNotNull (the next segment).
    /// </summary>
    private static bool BelongsToChain(ConditionalAccessExpressionSyntax outer, SyntaxNode node)
    {
        if (!outer.Span.Contains(node.Span))
        {
            return false;
        }

        foreach (var descendant in outer.DescendantNodes().OfType<ConditionalAccessExpressionSyntax>())
        {
            if (descendant.WhenNotNull.Span.Contains(node.Span))
            {
                return false;
            }
        }

        return true;
    }

    private void MarkConditionalDeferred(
        ExtensionConditionalPlan plan,
        string detail,
        ConditionalAccessExpressionSyntax site)
    {
        _statistics.ConditionalChainsDeferred++;
        AddDiagnostic(
            WarningOrError(),
            ExtensionRule.Cond,
            ExtensionMethodCodes.NullConditional,
            $"Null-conditional extension call '{site.ToString()}' is not lifted in v1 ({detail}); site left unchanged.",
            site.GetLocation());
        AddSkipReason(ExtensionMethodCodes.NullConditional);
        plan.SkipCode = ExtensionMethodCodes.NullConditional;
        plan.SkipDetail = detail;
        plan.Liftable = false;
    }

    // ── EM-GROUP ─────────────────────────────────────────────────────────────

    private void AnalyzeMethodGroup(MemberAccessExpressionSyntax memberAccess)
    {
        var info = Model.GetSymbolInfo(memberAccess);

        IMethodSymbol? chosenReduced = info.Symbol as IMethodSymbol;
        List<IMethodSymbol> reducedCandidates;

        if (chosenReduced is { MethodKind: MethodKind.ReducedExtension })
        {
            reducedCandidates = new List<IMethodSymbol> { chosenReduced };
        }
        else
        {
            chosenReduced = null;

            // nameof(r.M) and other non-conversion contexts: no rewrite, no
            // CS2J1207 — there is no delegate conversion taking place.
            if (info.CandidateReason == CandidateReason.MemberGroup)
            {
                return;
            }

            // A failed delegate conversion (CS0123/CS1113) exposes the
            // extension either as a ReducedExtension candidate or as its
            // ordinary static extension form.
            reducedCandidates = info.CandidateSymbols
                .OfType<IMethodSymbol>()
                .Where(m => (m.MethodKind == MethodKind.ReducedExtension && m.ReducedFrom is not null)
                    || (m.MethodKind == MethodKind.Ordinary && m.IsExtensionMethod))
                .ToList();
            if (reducedCandidates.Count == 0)
            {
                return;
            }
        }

        var distinct = reducedCandidates
            .Select(m => (m.ReducedFrom ?? m).OriginalDefinition.ToDisplayString())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var chosen = reducedCandidates[0];
        var unreduced = chosen.ReducedFrom ?? chosen;

        var isInExpressionTree = ExtensionExpressionTreeFacts
            .IsInExpressionTreeLambda(memberAccess, Model);

        var basePlan = new ExtensionGroupPlan
        {
            MemberAccess = memberAccess,
            Reduced = chosen,
            Unreduced = unreduced,
            DelegateType = null!,
            IsInExpressionTree = isInExpressionTree,
            DirectCapture = false,
            Anchor = null,
            LambdaParameterNames = Array.Empty<string>(),
        };

        // Multiple distinct extension candidates: CS0121 ambiguity.
        if (distinct.Count >= 2)
        {
            AddDeferredGroup(
                basePlan,
                "ambiguous extension method group: candidates [" +
                string.Join("; ", reducedCandidates.Select(c => (c.ReducedFrom ?? c).ContainingType.ToDisplayString())) + "]");
            return;
        }

        if (Options.LinqHostSet.Contains(unreduced.ContainingType.ToDisplayString()))
        {
            _statistics.LinqOwnedSkipped++;
            return;
        }

        // Unresolved conversion (CS0123/CS1113/...): Roslyn could not bind the
        // group to one delegate — never guess.
        if (chosenReduced is null)
        {
            AddDeferredGroup(
                basePlan,
                $"extension method group failed delegate conversion ({info.CandidateReason})");
            return;
        }

        if (unreduced.Parameters.Length > 0 && unreduced.Parameters[0].RefKind != RefKind.None)
        {
            AddDeferredGroup(
                basePlan,
                "extension method group with a ref/in receiver cannot be captured by a value lambda");
            return;
        }

        if (isInExpressionTree)
        {
            AddDeferredGroup(
                basePlan,
                "extension method groups are not supported inside expression trees");
            return;
        }

        var delegateType = Model.GetTypeInfo(memberAccess).ConvertedType as INamedTypeSymbol;
        var invoke = delegateType?.DelegateInvokeMethod;
        if (delegateType is null || invoke is null || delegateType.TypeKind != TypeKind.Delegate)
        {
            AddDeferredGroup(
                basePlan,
                "no unique delegate target type is available at the conversion site");
            return;
        }

        if (!MatchesDelegateSignature(unreduced, invoke))
        {
            AddDeferredGroup(
                basePlan,
                $"extension method '{unreduced.Name}' signature does not match delegate {delegateType.ToDisplayString()}");
            return;
        }

        var receiver = memberAccess.Expression;
        var direct = ExtensionTempLift.IsDirectCaptureReceiver(receiver, Model);

        ExtensionAnchor? anchor = null;
        string? tempName = null;
        SyntaxAnnotation? anchorAnnotation = null;

        if (!direct)
        {
            // Hoisting the receiver is interleaved with other evaluations in
            // argument/event/conditional positions; only dominant positions
            // preserve evaluation order.
            if (!ExtensionTempLift.IsAtDominantExpressionPosition(memberAccess))
            {
                AddDeferredGroup(
                    basePlan with { DelegateType = delegateType },
                    "receiver requires a single-evaluation temp but the site is not the dominant expression of its statement");
                return;
            }

            anchor = ExtensionTempLift.ResolveAnchor(memberAccess, Model);
            if (anchor.Kind == ExtensionAnchorKind.None)
            {
                AddDeferredGroup(
                    basePlan with { DelegateType = delegateType },
                    "no statement anchor for the single-evaluation receiver temp (field initializer/query/...)");
                return;
            }

            // A hoisted group inside a liftable conditional would evaluate its
            // receiver unconditionally before the null guard.
            var enclosingConditional = memberAccess.Ancestors()
                .OfType<ConditionalAccessExpressionSyntax>()
                .FirstOrDefault(c => _conditionalPlans.Any(p =>
                    p.Liftable && p.OuterConditional == c && BelongsToChain(c, memberAccess)));
            if (enclosingConditional is not null)
            {
                var enclosingPlan = _conditionalPlans.First(p =>
                    p.Liftable && p.OuterConditional == enclosingConditional && BelongsToChain(enclosingConditional, memberAccess));
                enclosingPlan.Liftable = false;
                enclosingPlan.SkipCode = ExtensionMethodCodes.NullConditional;
                enclosingPlan.SkipDetail = "a hoisted method-group receiver shares its evaluation scope";
                _statistics.ConditionalChainsLifted--;
                _statistics.ConditionalChainsDeferred++;
                AddDiagnostic(
                    WarningOrError(),
                    ExtensionRule.Cond,
                    ExtensionMethodCodes.NullConditional,
                    $"Null-conditional extension call '{enclosingConditional.ToString()}' is not lifted ({enclosingPlan.SkipDetail}); site left unchanged.",
                    enclosingConditional.GetLocation());
                AddSkipReason(ExtensionMethodCodes.NullConditional);

                AddDeferredGroup(
                    basePlan with { DelegateType = delegateType },
                    "receiver temp would be hoisted across a null-conditional guard");
                return;
            }

            tempName = _allocator.Allocate("g");
            anchorAnnotation = new SyntaxAnnotation("C19", "ExtensionGroupAnchor");
        }

        var parameterNames = invoke.Parameters
            .Select(_ => _allocator.Allocate("p"))
            .ToList();

        _groupPlans.Add(basePlan with
        {
            DelegateType = delegateType,
            DirectCapture = direct,
            Anchor = anchor,
            TempName = tempName,
            AnchorAnnotation = anchorAnnotation,
            LambdaParameterNames = parameterNames,
        });
        _statistics.GroupSitesRewritten++;
    }

    private void AddDeferredGroup(ExtensionGroupPlan plan, string detail)
    {
        _statistics.GroupSitesDeferred++;
        AddDiagnostic(
            WarningOrError(),
            ExtensionRule.Group,
            ExtensionMethodCodes.ReducedMethodGroup,
            $"Reduced extension method group '{plan.MemberAccess.ToString()}' is not rewritten in v1 ({detail}); site left unchanged.",
            plan.MemberAccess.GetLocation());
        AddSkipReason(ExtensionMethodCodes.ReducedMethodGroup);
        _groupPlans.Add(plan with { SkipCode = ExtensionMethodCodes.ReducedMethodGroup, SkipDetail = detail });
    }

    private static bool MatchesDelegateSignature(IMethodSymbol unreduced, IMethodSymbol invoke)
    {
        if (unreduced.Parameters.Length - 1 != invoke.Parameters.Length)
        {
            return false;
        }

        for (var i = 1; i < unreduced.Parameters.Length; i++)
        {
            var methodParameter = unreduced.Parameters[i];
            var delegateParameter = invoke.Parameters[i - 1];
            if (methodParameter.RefKind != delegateParameter.RefKind)
            {
                return false;
            }

            if (!SymbolEqualityComparer.Default.Equals(methodParameter.Type, delegateParameter.Type))
            {
                return false;
            }
        }

        return true;
    }

    // ── M4 EM-LINQ mixed chains ──────────────────────────────────────────────

    /// <summary>
    /// True when <paramref name="invocation"/> (a bound reduced call whose
    /// host is NOT in the LINQ host set) is linked through fluent
    /// member-access receivers to at least one LINQ-owned reduced call.
    /// Argument-position calls (e.g. inside lambdas) are not on the receiver
    /// spine and therefore do not mix.
    /// </summary>
    private bool IsInMixedLinqChain(InvocationExpressionSyntax invocation)
    {
        // Upward: `a.B().C()` — walk fluent continuations whose receiver is
        // the current invocation (parenthesized receivers are transparent).
        SyntaxNode current = invocation;
        while (true)
        {
            var probe = current;
            while (probe.Parent is ParenthesizedExpressionSyntax)
            {
                probe = probe.Parent;
            }

            if (probe.Parent is not MemberAccessExpressionSyntax memberUp
                || memberUp.Expression != probe
                || memberUp.Parent is not InvocationExpressionSyntax next
                || next.Expression != memberUp)
            {
                break;
            }

            if (IsReducedLinqHost(next))
            {
                return true;
            }

            current = next;
        }

        // Downward: follow the receiver chain of member-access invocations.
        var receiver = (invocation.Expression as MemberAccessExpressionSyntax)?.Expression;
        while (receiver is not null)
        {
            while (receiver is ParenthesizedExpressionSyntax parenthesized)
            {
                receiver = parenthesized.Expression;
            }

            if (receiver is not InvocationExpressionSyntax inner)
            {
                break;
            }

            if (IsReducedLinqHost(inner))
            {
                return true;
            }

            receiver = (inner.Expression as MemberAccessExpressionSyntax)?.Expression;
        }

        return false;
    }

    private bool IsReducedLinqHost(InvocationExpressionSyntax invocation)
    {
        if (Model.GetSymbolInfo(invocation).Symbol is IMethodSymbol
            {
                MethodKind: MethodKind.ReducedExtension,
                ReducedFrom: { } reducedFrom,
            })
        {
            return Options.LinqHostSet.Contains(reducedFrom.ContainingType.ToDisplayString());
        }

        return false;
    }

    // ── Shared helpers ───────────────────────────────────────────────────────

    private ExtensionSeverity WarningOrError()
        => Options.Strict ? ExtensionSeverity.Error : ExtensionSeverity.Warning;

    private string DescribeCandidateHost(IMethodSymbol candidate)
    {
        var host = (candidate.ReducedFrom ?? candidate).ContainingType;
        var namespaceName = host.ContainingNamespace?.ToDisplayString();
        return string.IsNullOrEmpty(namespaceName)
            ? host.ToDisplayString()
            : $"{host.ToDisplayString()} (imported from namespace {namespaceName})";
    }

    private static bool IsReceiverStyleCall(InvocationExpressionSyntax invocation)
        => invocation.Expression is MemberAccessExpressionSyntax or MemberBindingExpressionSyntax;

    /// <summary>
    /// True when the invoked member name of a receiver-style invocation is a
    /// reachable extension-method name.
    /// </summary>
    private bool InvokedNameCouldBeExtensionName(InvocationExpressionSyntax invocation)
        => invocation.Expression switch
        {
            MemberBindingExpressionSyntax binding =>
                ExtensionMethodNames!.Contains(binding.Name.Identifier.ValueText),
            MemberAccessExpressionSyntax access =>
                ExtensionMethodNames!.Contains(access.Name.Identifier.ValueText),
            _ => true,
        };

    private static ExpressionSyntax? TryGetReceiverExpression(InvocationExpressionSyntax invocation)
        => (invocation.Expression as MemberAccessExpressionSyntax)?.Expression;

    /// <summary>
    /// True when the invocation belongs to a null-conditional WhenNotNull
    /// segment (<c>r?.M()</c> / <c>r?.M().N()</c>); <c>(r?.M()).N()</c> stays
    /// a normal site.
    /// </summary>
    private static bool IsNullConditionalReducedSite(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is MemberBindingExpressionSyntax)
        {
            return true;
        }

        return invocation.Ancestors()
            .OfType<ConditionalAccessExpressionSyntax>()
            .Any(conditional => conditional.WhenNotNull.Span.Contains(invocation.Span));
    }

    private void AddSkipReason(string reason)
    {
        _statistics.SitesSkippedByReason.TryGetValue(reason, out var current);
        _statistics.SitesSkippedByReason[reason] = current + 1;
    }

    private void AddDiagnostic(
        ExtensionSeverity severity,
        ExtensionRule rule,
        string code,
        string message,
        Location location)
    {
        var lineSpan = location.GetLineSpan();
        _diagnostics.Add(new ExtensionDiagnostic(
            severity,
            rule,
            code,
            message,
            Model.SyntaxTree.FilePath,
            lineSpan.Span.Start.Line + 1));
    }
}
