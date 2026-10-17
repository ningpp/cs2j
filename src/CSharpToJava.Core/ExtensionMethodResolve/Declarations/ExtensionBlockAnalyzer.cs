using CSharpToJava.Core.MemberDesugar.Operators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CSharpToJava.Core.ExtensionMethodResolve.Declarations;

/// <summary>
/// Index of the C#14 extension-block containers (<c>TypeKind.Extension</c>)
/// declared by referenced metadata assemblies: the member names they expose and
/// whether any declares an operator member. A call site can only bind to such a
/// container through one of these names (or, for operator nodes, only when an
/// operator member exists), so a node that fails the name test can never be an
/// external extension site — the report-only boundary scan skips it without a
/// semantic query.
/// </summary>
internal sealed class ExtensionBlockMetadataIndex
{
    /// <summary>True when at least one referenced assembly declares an extension container.</summary>
    public required bool Present { get; init; }

    /// <summary>Member names exposed by the metadata extension containers.</summary>
    public required IReadOnlySet<string> MemberNames { get; init; }

    /// <summary>True when any metadata extension container declares an operator member.</summary>
    public required bool HasOperatorMembers { get; init; }

    /// <summary>
    /// True when a metadata extension container exposes at least one member a
    /// call site could bind to (a named member or an operator).
    /// </summary>
    public bool CanBindMember => Present && (MemberNames.Count > 0 || HasOperatorMembers);

    /// <summary>
    /// True when the accessed member name of an invocation / member access /
    /// member binding node appears among the names exposed by metadata
    /// extension containers. A node shape with no identifier to inspect (e.g. a
    /// delegate invocation) is conservatively treated as a possible match.
    /// </summary>
    public bool NamedMemberCouldMatch(SyntaxNode node)
    {
        var name = node switch
        {
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
            InvocationExpressionSyntax invocation => InvokedMemberName(invocation),
            _ => null,
        };
        return name is null || MemberNames.Contains(name);
    }

    private static string? InvokedMemberName(InvocationExpressionSyntax invocation)
        => invocation.Expression switch
        {
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
            SimpleNameSyntax simple => simple.Identifier.ValueText,
            _ => null,
        };
}

/// <summary>
/// Single-tree C49 analysis (M1): enumerates extension blocks, validates them
/// semantically, builds member/site plans, and synthesizes C19
/// <see cref="ExtensionConditionalPlan"/>s for liftable null-conditional block
/// method/property sites. Unsupported members (indexers, other kinds) get
/// CS2J1209; non-liftable sites are paired diagnostics and stay sugar.
/// </summary>
internal sealed class ExtensionBlockAnalyzer
{
    private readonly SemanticModel _model;
    private readonly ExtensionBlockOptions _options;
    private readonly ExtensionBlockMetadataIndex? _metadataIndex;
    private readonly bool _hasBlocks;
    private readonly ExtensionTreeSyntaxIndex? _syntaxIndex;
    private readonly ExtensionTempAllocator _allocator;
    private readonly List<ExtensionDiagnostic> _diagnostics = new();
    private readonly ExtensionBlockStatistics _stats = new();

    /// <summary>
    /// <paramref name="metadataIndex"/> indexes the extension-block containers
    /// declared by referenced assemblies. Null (the conservative default for
    /// callers that cannot supply it) runs the external boundary scan
    /// unfiltered; a non-null index that cannot expose a bindable member skips
    /// it entirely, otherwise the scan only queries nodes whose accessed name
    /// (or operator shape) could belong to such a container.
    /// <paramref name="hasBlocks"/> is the caller's pre-computed
    /// <c>extension</c>-block declaration probe for this very tree: a
    /// definitively false value lets <see cref="BuildBlockPlans"/> skip its
    /// whole-tree block enumeration (there is none to find).
    /// <paramref name="syntaxIndex"/> supplies the shared document-order node
    /// walk for the boundary scan, replacing its own sweep.
    /// </summary>
    public ExtensionBlockAnalyzer(
        SemanticModel model,
        ExtensionBlockOptions options,
        ExtensionBlockMetadataIndex? metadataIndex = null,
        bool hasBlocks = true,
        ExtensionTreeSyntaxIndex? syntaxIndex = null)
    {
        _model = model;
        _options = options;
        _metadataIndex = metadataIndex;
        _hasBlocks = hasBlocks;
        _syntaxIndex = syntaxIndex;
        _allocator = new ExtensionTempAllocator(model.SyntaxTree.GetCompilationUnitRoot());
    }

    public BlockAnalysisResult Analyze()
    {
        var root = _model.SyntaxTree.GetCompilationUnitRoot();
        _stats.FilesScanned = 1;

        var blockPlans = BuildBlockPlans(root);

        var materialized = blockPlans.ToDictionary(b => b.Block, b => b);
        var membersByOriginal = new Dictionary<ISymbol, (MemberPlan Plan, BlockPlan Block)>();
        foreach (var block in blockPlans)
        {
            foreach (var member in block.Members)
            {
                var symbol = member switch
                {
                    MethodMemberPlan m => (ISymbol)m.Symbol,
                    PropertyMemberPlan p => p.Symbol,
                    OperatorMemberPlan o => o.Symbol,
                    StaticMethodMemberPlan sm => sm.Symbol,
                    StaticPropertyMemberPlan sp => sp.Symbol,
                    _ => null,
                };
                if (symbol is not null)
                {
                    membersByOriginal[symbol.OriginalDefinition] = (member, block);
                }
            }
        }

        var methodSites = new List<MethodSitePlan>();
        var readSites = new List<PropertyReadSitePlan>();
        var writeSites = new List<PropertyWriteSitePlan>();
        var compoundSites = new List<PropertyCompoundSitePlan>();
        var binaryOperatorSites = new List<BinaryOperatorSitePlan>();
        var unaryOperatorSites = new List<UnaryOperatorSitePlan>();
        var operatorCompoundSites = new List<OperatorCompoundSitePlan>();
        var staticMethodSites = new List<StaticMethodSitePlan>();
        var staticReadSites = new List<StaticPropertyReadSitePlan>();
        var staticWriteSites = new List<StaticPropertyWriteSitePlan>();
        var conditionalPlans = new List<ExtensionConditionalPlan>();
        var conditionalSeams = new List<ConditionalSeamNode>();
        var hostOverrides = new Dictionary<ConditionalSpineCall, NameSyntax>();
        var deferred = new List<DeferredSitePlan>();
        var blockGroups = new List<ExtensionGroupPlan>();
        var externalSites = new List<ExternalExtensionSite>();
        var blockMarkers = new List<BlockExpressionTreeMarker>();

        var conditionalsCovered = new HashSet<ConditionalAccessExpressionSyntax>();

        var buildContext = new ConditionalPlanFactory.BuildContext(
            _model,
            _allocator,
            hostOverrides,
            conditionalSeams,
            ResolveMember);

        (MemberPlan Plan, BlockPlan Block)? ResolveMember(ISymbol symbol)
            => membersByOriginal.TryGetValue(symbol, out var hit) ? hit : null;

        // Perf elision (provably output-identical): when no extension-block
        // member was materialized, `membersByOriginal` is empty, so every site
        // loop below is a no-op — each of its membership tests keys on that
        // (empty) dictionary and therefore always `continue`s. Iterating an
        // empty sequence skips the per-node GetSymbolInfo sweep entirely.
        var siteNodes = membersByOriginal.Count > 0
            ? root.DescendantNodes()
            : Enumerable.Empty<SyntaxNode>();

        // ---- Invocations: block methods (instance + static) ----
        foreach (var invocation in siteNodes.OfType<InvocationExpressionSyntax>())
        {
            if (_model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol
                {
                    MethodKind: MethodKind.Ordinary,
                } method
                || method.ContainingType.TypeKind != TypeKind.Extension
                || !membersByOriginal.TryGetValue(method.OriginalDefinition, out var entry))
            {
                continue;
            }

            var block = entry.Block;

            switch (entry.Plan)
            {
                case StaticMethodMemberPlan staticMember:
                    AddStaticMethodSite(
                        invocation, method, staticMember, block,
                        staticMethodSites, deferred, blockMarkers);
                    break;

                case MethodMemberPlan methodMember:
                    // Member-binding invocations are owned by the conditional
                    // pipeline (single/multi-segment chains).
                    if (invocation.Expression is MemberBindingExpressionSyntax)
                    {
                        continue;
                    }

                    AddInstanceMethodSite(
                        invocation, method, methodMember, block,
                        methodSites, deferred, blockMarkers);
                    break;
            }
        }

        // ---- Member access: block properties, instance + static (read/write/compound) ----
        foreach (var access in siteNodes.OfType<MemberAccessExpressionSyntax>())
        {
            // Sites inside null-conditional chains are built by the
            // conditional pipeline (seams), not by read/write site plans.
            if (access.Ancestors().OfType<ConditionalAccessExpressionSyntax>().Any())
            {
                continue;
            }

            if (_model.GetSymbolInfo(access).Symbol is not IPropertySymbol property
                || property.ContainingType.TypeKind != TypeKind.Extension
                || !membersByOriginal.TryGetValue(property.OriginalDefinition, out var entry))
            {
                continue;
            }

            var block = entry.Block;

            switch (entry.Plan)
            {
                case StaticPropertyMemberPlan staticProperty:
                    AddStaticPropertySite(
                        access, property, staticProperty, block,
                        staticReadSites, staticWriteSites, deferred);
                    break;

                case PropertyMemberPlan propertyMember:
                    AddInstancePropertySite(
                        access, property, propertyMember, block,
                        readSites, writeSites, compoundSites, deferred);
                    break;
            }
        }

        // ---- EMD-OP: binary operator sites (a + b) ----
        foreach (var binary in siteNodes.OfType<BinaryExpressionSyntax>())
        {
            if (binary.IsKind(SyntaxKind.LogicalAndExpression)
                || binary.IsKind(SyntaxKind.LogicalOrExpression))
            {
                continue;
            }

            if (_model.GetSymbolInfo(binary).Symbol is not IMethodSymbol
                {
                    MethodKind: MethodKind.UserDefinedOperator,
                } op
                || op.ContainingType.TypeKind != TypeKind.Extension
                || !membersByOriginal.TryGetValue(op.OriginalDefinition, out var opEntry)
                || opEntry.Plan is not OperatorMemberPlan operatorMember)
            {
                continue;
            }

            // M3 decision 15: operator sites inside Expression<TDelegate> are
            // skipped with a paired diagnostic (whole-plan skip).
            if (ExtensionExpressionTreeFacts.IsInExpressionTreeLambda(binary, _model))
            {
                SkipOperatorInExpressionTree(binary, op, opEntry.Block, deferred);
                continue;
            }

            var isLifted = _model.GetOperation(binary) is IBinaryOperation { IsLifted: true };
            var annotation = new SyntaxAnnotation("C49", "BlockBinaryOperatorSite");
            binaryOperatorSites.Add(new BinaryOperatorSitePlan
            {
                Expression = binary,
                BoundSymbol = op,
                Member = operatorMember,
                Block = opEntry.Block,
                IsLifted = isLifted,
                LeftNullable = IsNullableValueType(binary.Left),
                RightNullable = IsNullableValueType(binary.Right),
                ResultTypeDisplay = GetResultTypeDisplay(binary),
                Annotation = annotation,
            });
            _stats.OperatorBinarySitesRewritten++;
        }

        // ---- EMD-OP: unary operator sites (-a) ----
        foreach (var unary in siteNodes.OfType<PrefixUnaryExpressionSyntax>())
        {
            if (_model.GetSymbolInfo(unary).Symbol is not IMethodSymbol
                {
                    MethodKind: MethodKind.UserDefinedOperator,
                } op
                || op.ContainingType.TypeKind != TypeKind.Extension
                || !membersByOriginal.TryGetValue(op.OriginalDefinition, out var opEntry)
                || opEntry.Plan is not OperatorMemberPlan operatorMember)
            {
                continue;
            }

            if (ExtensionExpressionTreeFacts.IsInExpressionTreeLambda(unary, _model))
            {
                SkipOperatorInExpressionTree(unary, op, opEntry.Block, deferred);
                continue;
            }

            var isLifted = _model.GetOperation(unary) is IUnaryOperation { IsLifted: true };
            var annotation = new SyntaxAnnotation("C49", "BlockUnaryOperatorSite");
            unaryOperatorSites.Add(new UnaryOperatorSitePlan
            {
                Expression = unary,
                BoundSymbol = op,
                Member = operatorMember,
                Block = opEntry.Block,
                IsLifted = isLifted,
                OperandNullable = IsNullableValueType(unary.Operand),
                ResultTypeDisplay = GetResultTypeDisplay(unary),
                Annotation = annotation,
            });
            _stats.OperatorUnarySitesRewritten++;
        }

        // ---- EMD-OP: compound assignment sites (a += b) ----
        foreach (var assignment in siteNodes
                     .OfType<AssignmentExpressionSyntax>()
                     .Where(a => !a.IsKind(SyntaxKind.SimpleAssignmentExpression)))
        {
            if (_model.GetOperation(assignment) is not ICompoundAssignmentOperation compound
                || compound.OperatorMethod is not
                {
                    MethodKind: MethodKind.UserDefinedOperator,
                } op
                || op.ContainingType.TypeKind != TypeKind.Extension
                || !membersByOriginal.TryGetValue(op.OriginalDefinition, out var opEntry)
                || opEntry.Plan is not OperatorMemberPlan operatorMember)
            {
                continue;
            }

            if (ExtensionExpressionTreeFacts.IsInExpressionTreeLambda(assignment, _model))
            {
                SkipOperatorInExpressionTree(assignment, op, opEntry.Block, deferred);
                continue;
            }

            var simpleLvalue = OperatorFacts.IsSimpleLvalue(assignment.Left);
            var atStatement = assignment.Parent is ExpressionStatementSyntax parentStatement
                               && parentStatement.Expression == assignment;
            if (!simpleLvalue && !atStatement)
            {
                // Complex receiver outside a statement: cannot single-evaluate.
                deferred.Add(new DeferredSitePlan
                {
                    Site = assignment,
                    IsProperty = false,
                    Code = ExtensionMethodCodes.ReducedMethodGroup,
                    Detail = ExtensionBlockMessages.OperatorCompoundDeferred,
                });
                AddDiagnostic(ExtensionSeverity.Warning, ExtensionMethodCodes.ReducedMethodGroup,
                    ExtensionBlockMessages.OperatorCompoundDeferred, assignment);
                opEntry.Block.HasDeferredSites = true;
                continue;
            }

            var annotation = new SyntaxAnnotation("C49", "BlockOperatorCompoundSite");
            operatorCompoundSites.Add(new OperatorCompoundSitePlan
            {
                Assignment = assignment,
                BoundSymbol = op,
                Member = operatorMember,
                Block = opEntry.Block,
                IsLifted = compound.IsLifted,
                SimpleLvalue = simpleLvalue,
                LeftNullable = IsNullableValueType(assignment.Left),
                RightNullable = IsNullableValueType(assignment.Right),
                ResultTypeDisplay = GetResultTypeDisplay(assignment),
                Annotation = annotation,
            });
            _stats.OperatorCompoundSitesRewritten++;
        }

        // ---- Block method groups: r.M / Type.M delegate conversion (M3) ----
        foreach (var access in siteNodes.OfType<MemberAccessExpressionSyntax>())
        {
            if (access.Parent is InvocationExpressionSyntax { Expression: { } invoked } && invoked == access)
            {
                continue;
            }

            var symbolInfo = _model.GetSymbolInfo(access);
            var candidateMethods = new List<IMethodSymbol>();
            if (symbolInfo.Symbol is IMethodSymbol resolved)
            {
                candidateMethods.Add(resolved);
            }
            else
            {
                // A method group whose delegate conversion fails (e.g.
                // CS1661 signature mismatch) exposes candidates instead
                // of a resolved symbol; they still must pair with a
                // diagnostic.
                candidateMethods.AddRange(
                    symbolInfo.CandidateSymbols.OfType<IMethodSymbol>());
            }

            foreach (var groupMethod in candidateMethods)
            {
                if (groupMethod.MethodKind != MethodKind.Ordinary
                    || groupMethod.ContainingType.TypeKind != TypeKind.Extension
                    || !membersByOriginal.TryGetValue(groupMethod.OriginalDefinition, out var groupEntry))
                {
                    continue;
                }

                AddBlockGroupSite(access, groupMethod, groupEntry, blockGroups, deferred);
            }
        }

        // ---- Outermost null-conditional chains: M3 dispatch ----
        foreach (var conditional in root.DescendantNodes()
                     .OfType<ConditionalAccessExpressionSyntax>()
                     .Where(c => !c.Ancestors().OfType<ConditionalAccessExpressionSyntax>().Any()))
        {
            if (conditionalsCovered.Contains(conditional))
            {
                continue;
            }

            var handled = TryBuildConditional(
                conditional,
                buildContext,
                conditionalsCovered,
                conditionalPlans,
                deferred);

            if (!handled)
            {
                // Unsupported/null shape (e.g. r?.P?.X): keep the sugar with
                // one paired diagnostic so the residual scanner sees the span.
                foreach (var nested in conditional.DescendantNodesAndSelf()
                             .OfType<ConditionalAccessExpressionSyntax>())
                {
                    conditionalsCovered.Add(nested);
                }

                DeferConditional(deferred, conditional, isProperty: true);
            }
        }

        // ---- External metadata boundary (decision appendix 5) ----
        // The metadata index proves which container members are reachable: a
        // non-bindable index (or a node whose accessed name / operator shape is
        // not exposed by any container) can never yield an external site, so it
        // is skipped without a semantic query. A null index keeps the original
        // unfiltered scan.
        if (_metadataIndex is not { CanBindMember: false })
        {
            ScanExternalMetadata(root, membersByOriginal, externalSites);
        }

        return new BlockAnalysisResult
        {
            BlockPlans = blockPlans,
            MethodSites = methodSites,
            ReadSites = readSites,
            WriteSites = writeSites,
            CompoundSites = compoundSites,
            BinaryOperatorSites = binaryOperatorSites,
            UnaryOperatorSites = unaryOperatorSites,
            OperatorCompoundSites = operatorCompoundSites,
            StaticMethodSites = staticMethodSites,
            StaticPropertyReadSites = staticReadSites,
            StaticPropertyWriteSites = staticWriteSites,
            ConditionalPlans = conditionalPlans,
            ConditionalSeams = conditionalSeams,
            ConditionalHostOverrides = hostOverrides,
            DeferredSites = deferred,
            ExternalSites = externalSites,
            BlockGroups = blockGroups,
            BlockExpressionTreeMarkers = blockMarkers,
            Diagnostics = _diagnostics,
            Statistics = _stats,
        };
    }

    /// <summary>
    /// Dispatches one outermost conditional to the matching factory build.
    /// Returns false only when no recognizable liftable shape exists.
    /// </summary>
    private bool TryBuildConditional(
        ConditionalAccessExpressionSyntax conditional,
        ConditionalPlanFactory.BuildContext context,
        HashSet<ConditionalAccessExpressionSyntax> covered,
        List<ExtensionConditionalPlan> plans,
        List<DeferredSitePlan> deferred)
    {
        var whenNotNull = conditional.WhenNotNull;

        // r?.P
        if (whenNotNull is MemberBindingExpressionSyntax binding
            && TryGetBlockMember(_model.GetSymbolInfo(binding).Symbol, context,
                out var propertySymbol, out var propertyEntry)
            && propertySymbol is IPropertySymbol boundProperty
            && propertyEntry!.Value.Plan is PropertyMemberPlan boundMember)
        {
            MarkChainCovered(conditional, covered);
            var build = ConditionalPlanFactory.BuildPropertyPlan(
                conditional, binding, boundProperty, boundMember,
                propertyEntry.Value.Block, context);
            CollectConditional(build, conditional, isProperty: true,
                plans, deferred, propertyEntry.Value.Block);
            return true;
        }

        // r?.M()
        if (whenNotNull is InvocationExpressionSyntax { Expression: MemberBindingExpressionSyntax } origin
            && _model.GetSymbolInfo(origin).Symbol is IMethodSymbol
            {
                MethodKind: MethodKind.Ordinary,
            } originMethod
            && context.Resolve(originMethod.OriginalDefinition) is
            {
                Plan: MethodMemberPlan originMember,
            } originHit)
        {
            MarkChainCovered(conditional, covered);
            var build = ConditionalPlanFactory.BuildMethodPlan(
                conditional, origin, originMethod,
                (MethodMemberPlan)originHit.Plan, originHit.Block, context);
            CollectConditional(build, conditional, isProperty: false,
                plans, deferred, originHit.Block);
            return true;
        }

        // r?.M().P (member-access wrapping a member-binding invocation).
        if (whenNotNull is MemberAccessExpressionSyntax methodTail
            && FindBindingInvocationUnder(methodTail) is { } methodOrigin
            && _model.GetSymbolInfo(methodOrigin).Symbol is IMethodSymbol
            {
                MethodKind: MethodKind.Ordinary,
            } tailMethod
            && context.Resolve(tailMethod.OriginalDefinition) is
            {
                Plan: MethodMemberPlan,
            } tailHit)
        {
            MarkChainCovered(conditional, covered);
            var build = ConditionalPlanFactory.BuildMethodPlan(
                conditional, methodOrigin, tailMethod,
                (MethodMemberPlan)tailHit.Plan, tailHit.Block, context);
            CollectConditional(build, conditional, isProperty: false,
                plans, deferred, tailHit.Block);
            return true;
        }

        // r?.P.B (member-access wrapping a member-binding property).
        if (whenNotNull is MemberAccessExpressionSyntax propertyTail
            && FindBindingPropertyUnder(propertyTail) is { } propertyBinding
            && _model.GetSymbolInfo(propertyBinding).Symbol is IPropertySymbol prefixProperty
            && context.Resolve(prefixProperty.OriginalDefinition) is
            {
                Plan: PropertyMemberPlan prefixMember,
            } prefixHit)
        {
            MarkChainCovered(conditional, covered);
            var build = ConditionalPlanFactory.BuildPropertyPlan(
                conditional, propertyBinding, prefixProperty,
                (PropertyMemberPlan)prefixHit.Plan, prefixHit.Block, context);
            CollectConditional(build, conditional, isProperty: true,
                plans, deferred, prefixHit.Block);
            return true;
        }

        // r?.P = v / r?.P += v (statement writes).
        if (whenNotNull is AssignmentExpressionSyntax assignment
            && FindWriteBinding(assignment.Left) is { } writeBinding
            && _model.GetSymbolInfo(writeBinding).Symbol is IPropertySymbol writtenProperty
            && context.Resolve(writtenProperty.OriginalDefinition) is
            {
                Plan: PropertyMemberPlan writtenMember,
            } writtenHit)
        {
            // ++/-- shapes never reach here; an increment expression hanging
            // off the conditional is Roslyn CS1059 and stays sugar paired.
            MarkChainCovered(conditional, covered);
            var build = ConditionalPlanFactory.BuildPropertyWritePlan(
                conditional, assignment, writtenProperty,
                (PropertyMemberPlan)writtenHit.Plan, writtenHit.Block,
                assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)
                    ? SyntaxKind.None
                    : assignment.Kind(),
                context);
            CollectConditional(build, conditional, isProperty: true,
                plans, deferred, writtenHit.Block);
            return true;
        }

        // Postfix/prefix ++/-- directly over a conditional (r?.P++) is CS1059:
        // no build; keep sugar with an explicit paired marker.
        if (ContainsExtensionBinding(conditional))
        {
            MarkChainCovered(conditional, covered);
            DeferIncrement(conditional, deferred);
            return true;
        }

        return false;
    }

    private static void MarkChainCovered(
        ConditionalAccessExpressionSyntax outer,
        HashSet<ConditionalAccessExpressionSyntax> covered)
    {
        foreach (var nested in outer.DescendantNodesAndSelf()
                     .OfType<ConditionalAccessExpressionSyntax>())
        {
            covered.Add(nested);
        }
    }

    private static bool TryGetBlockMember(
        ISymbol? symbol,
        ConditionalPlanFactory.BuildContext context,
        out ISymbol? boundSymbol,
        out (MemberPlan Plan, BlockPlan Block)? entry)
    {
        boundSymbol = symbol;
        if (symbol is null)
        {
            entry = null;
            return false;
        }

        entry = context.Resolve(symbol.OriginalDefinition);
        return entry is not null;
    }

    private static InvocationExpressionSyntax? FindBindingInvocationUnder(ExpressionSyntax root)
    {
        return root.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(i => i.Expression is MemberBindingExpressionSyntax);
    }

    private static MemberBindingExpressionSyntax? FindBindingPropertyUnder(ExpressionSyntax root)
    {
        var invocation = FindBindingInvocationUnder(root);
        return root.DescendantNodesAndSelf()
            .OfType<MemberBindingExpressionSyntax>()
            .FirstOrDefault(b => invocation is null
                || !b.AncestorsAndSelf().Any(n => n == invocation));
    }

    private static MemberBindingExpressionSyntax? FindWriteBinding(ExpressionSyntax left)
        => left switch
        {
            MemberBindingExpressionSyntax direct => direct,
            MemberAccessExpressionSyntax { Expression: MemberBindingExpressionSyntax prefix } => prefix,
            _ => left.DescendantNodes().OfType<MemberBindingExpressionSyntax>().FirstOrDefault(),
        };

    private bool ContainsExtensionBinding(ConditionalAccessExpressionSyntax conditional)
        => conditional.WhenNotNull.DescendantNodesAndSelf()
            .OfType<MemberBindingExpressionSyntax>()
            .Any(b => _model.GetSymbolInfo(b).Symbol is { ContainingType.TypeKind: TypeKind.Extension });

    // ---- Invocation site builders (extracted M1 logic + M2 static sites) ----

    private void AddStaticMethodSite(
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        StaticMethodMemberPlan staticMember,
        BlockPlan block,
        List<StaticMethodSitePlan> sites,
        List<DeferredSitePlan> deferred,
        List<BlockExpressionTreeMarker> blockMarkers)
    {
        // EMD-STATIC sites are type-name qualified: the MemberAccess left side
        // must bind to a type symbol. Bare/static-other shapes are not planned
        // (ordinary member resolution covers them after materialization).
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess
            || _model.GetSymbolInfo(memberAccess.Expression).Symbol is not ITypeSymbol)
        {
            return;
        }

        var n = block.BlockTypeParameters.Count;
        var blockTypeArguments = method.ContainingType.TypeArguments.Take(n).ToList();

        var annotation = new SyntaxAnnotation("C49", "BlockStaticMethodSite");
        sites.Add(new StaticMethodSitePlan
        {
            Invocation = invocation,
            BoundSymbol = method,
            Member = staticMember,
            Block = block,
            SourceName = memberAccess.Name,
            BlockTypeArguments = blockTypeArguments,
            Annotation = annotation,
        });
        _stats.StaticMethodSitesRewritten++;

        // M3 decision 15: block METHOD sites inside Expression<TDelegate> are
        // rewritten and asserted to rebind to the generated host member.
        if (ExtensionExpressionTreeFacts.IsInExpressionTreeLambda(invocation, _model))
        {
            blockMarkers.Add(new BlockExpressionTreeMarker(
                annotation, block, method, invocation, IsStatic: true));
        }
    }

    private void AddInstanceMethodSite(
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        MethodMemberPlan methodMember,
        BlockPlan block,
        List<MethodSitePlan> sites,
        List<DeferredSitePlan> deferred,
        List<BlockExpressionTreeMarker> blockMarkers)
    {
        SimpleNameSyntax sourceName;
        ExpressionSyntax receiverExpression;
        switch (invocation.Expression)
        {
            case MemberAccessExpressionSyntax sourceMemberAccess:
                sourceName = sourceMemberAccess.Name;
                receiverExpression = sourceMemberAccess.Expression;
                break;
            case IdentifierNameSyntax sourceIdentifier:
                // Bare member call inside the block: implicit receiver is
                // the block receiver itself.
                sourceName = sourceIdentifier;
                receiverExpression = SyntaxFactory.IdentifierName(block.Receiver.Name);
                break;
            default:
                return;
        }

        var annotation = new SyntaxAnnotation("C49", "BlockMethodSite");
        sites.Add(new MethodSitePlan
        {
            Invocation = invocation,
            BoundSymbol = method,
            Member = methodMember,
            Block = block,
            SourceName = sourceName,
            Receiver = receiverExpression,
            Annotation = annotation,
        });
        _stats.MethodSitesRewritten++;

        // M3 decision 15: method sites in expression trees are rewritten and
        // asserted (EM-EXPRTREE mirror).
        if (ExtensionExpressionTreeFacts.IsInExpressionTreeLambda(invocation, _model))
        {
            blockMarkers.Add(new BlockExpressionTreeMarker(
                annotation, block, method, invocation, IsStatic: false));
        }
    }

    // ---- Property site builders (extracted M1 logic + M2 static sites) ----

    private void AddStaticPropertySite(
        MemberAccessExpressionSyntax access,
        IPropertySymbol property,
        StaticPropertyMemberPlan staticMember,
        BlockPlan block,
        List<StaticPropertyReadSitePlan> readSites,
        List<StaticPropertyWriteSitePlan> writeSites,
        List<DeferredSitePlan> deferred)
    {
        // Left side must be a type symbol.
        if (_model.GetSymbolInfo(access.Expression).Symbol is not ITypeSymbol)
        {
            return;
        }

        var n = block.BlockTypeParameters.Count;
        var blockTypeArguments = property.ContainingType.TypeArguments.Take(n).ToList();

        // Inc/dec (Vec.Marker++) on a type-qualified static property: M2
        // retains the sugar; a read-site rewrite here would produce a
        // non-lvalue get_Marker()++ invocation.
        if ((access.Parent is PrefixUnaryExpressionSyntax prefix && prefix.Operand == access)
            || (access.Parent is PostfixUnaryExpressionSyntax postfix && postfix.Operand == access))
        {
            deferred.Add(new DeferredSitePlan
            {
                Site = (ExpressionSyntax)access.Parent!,
                IsProperty = true,
                Code = ExtensionMethodCodes.ReducedMethodGroup,
                Detail = ExtensionBlockMessages.StaticPropertyCompoundDeferred,
            });
            AddDiagnostic(ExtensionSeverity.Warning, ExtensionMethodCodes.ReducedMethodGroup,
                ExtensionBlockMessages.StaticPropertyCompoundDeferred, access);
            block.HasDeferredSites = true;
            return;
        }

        if (access.Parent is AssignmentExpressionSyntax { Left: { } left } assignment && left == access)
        {
            if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
            {
                var annotation = new SyntaxAnnotation("C49", "BlockStaticWriteSite");
                writeSites.Add(new StaticPropertyWriteSitePlan
                {
                    Assignment = assignment,
                    Target = access,
                    BoundSymbol = property,
                    Member = staticMember,
                    Block = block,
                    BlockTypeArguments = blockTypeArguments,
                    Annotation = annotation,
                });
                _stats.StaticPropertyWriteSitesRewritten++;
                return;
            }

            // Compound/inc-dec on a type-qualified static property: M2 retains
            // the sugar (single-eval semantics out of M2 scope for statics).
            deferred.Add(new DeferredSitePlan
            {
                Site = assignment,
                IsProperty = true,
                Code = ExtensionMethodCodes.ReducedMethodGroup,
                Detail = ExtensionBlockMessages.StaticPropertyCompoundDeferred,
            });
            AddDiagnostic(ExtensionSeverity.Warning, ExtensionMethodCodes.ReducedMethodGroup,
                ExtensionBlockMessages.StaticPropertyCompoundDeferred, assignment);
            block.HasDeferredSites = true;
            return;
        }

        var readAnnotation = new SyntaxAnnotation("C49", "BlockStaticReadSite");
        readSites.Add(new StaticPropertyReadSitePlan
        {
            Access = access,
            BoundSymbol = property,
            Member = staticMember,
            Block = block,
            BlockTypeArguments = blockTypeArguments,
            Annotation = readAnnotation,
        });
        _stats.StaticPropertyReadSitesRewritten++;
    }

    private void AddInstancePropertySite(
        MemberAccessExpressionSyntax access,
        IPropertySymbol property,
        PropertyMemberPlan propertyMember,
        BlockPlan block,
        List<PropertyReadSitePlan> readSites,
        List<PropertyWriteSitePlan> writeSites,
        List<PropertyCompoundSitePlan> compoundSites,
        List<DeferredSitePlan> deferred)
    {
        if (access.Parent is AssignmentExpressionSyntax { Left: { } left } assignment && left == access)
        {
            if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
            {
                var annotation = new SyntaxAnnotation("C49", "BlockWriteSite");
                writeSites.Add(new PropertyWriteSitePlan
                {
                    Assignment = assignment,
                    Target = access,
                    BoundSymbol = property,
                    Member = propertyMember,
                    Block = block,
                    Annotation = annotation,
                });
                _stats.PropertyWriteSitesRewritten++;
            }
            else if (assignment.Parent is ExpressionStatementSyntax statement
                     && statement.Expression == assignment)
            {
                AddCompoundPlan(statement, access, property, propertyMember, block,
                    assignment.Kind(), unaryKind: SyntaxKind.None, compoundSites);
            }
            else
            {
                DeferCompound(deferred, access);
                block.HasDeferredSites = true;
            }

            return;
        }

        if (IsIncDecAccess(access, out var incStatement, out var unaryKind))
        {
            AddCompoundPlan(incStatement, access, property, propertyMember, block,
                SyntaxKind.None, unaryKind, compoundSites);
            return;
        }

        if (ExtensionExpressionTreeFacts.IsInExpressionTreeLambda(access, _model))
        {
            _stats.SitesSkippedExpressionTree++;
            deferred.Add(new DeferredSitePlan
            {
                Site = access,
                IsProperty = true,
                Code = ExtensionMethodCodes.ReducedMethodGroup,
                Detail = ExtensionBlockMessages.ExpressionTreeSiteSkipped,
            });
            AddDiagnostic(ExtensionSeverity.Warning, ExtensionMethodCodes.ReducedMethodGroup,
                ExtensionBlockMessages.ExpressionTreeSiteSkipped, access);
            block.HasDeferredSites = true;
            return;
        }

        var readAnnotation = new SyntaxAnnotation("C49", "BlockReadSite");
        readSites.Add(new PropertyReadSitePlan
        {
            Site = access,
            BoundSymbol = property,
            Member = propertyMember,
            Block = block,
            Receiver = access.Expression,
            Annotation = readAnnotation,
        });
        _stats.PropertyReadSitesRewritten++;
    }

    private List<BlockPlan> BuildBlockPlans(CompilationUnitSyntax root)
    {
        var plans = new List<BlockPlan>();

        // The caller's declaration probe already proved this tree declares no
        // `extension` block, so the enumeration below is provably empty; skip
        // the whole-tree walk.
        if (!_hasBlocks)
        {
            return plans;
        }

        foreach (var block in ExtensionBlockSyntaxFacts.EnumerateBlocks(root))
        {
            _stats.BlocksScanned++;

            var host = _model.GetDeclaredSymbol(block.Parent!) as INamedTypeSymbol;
            if (host is null || !host.IsStatic)
            {
                // Only blocks hosted by a static class are in M1 scope.
                _stats.UnsupportedMembers++;
                AddDiagnostic(
                    ExtensionSeverity.Error,
                    ExtensionBlockCodes.Unsupported,
                    ExtensionBlockMessages.UnsupportedMember("extension block outside a static class"),
                    block);
                continue;
            }

            var receiverSyntax = ExtensionBlockSyntaxFacts.GetReceiverParameter(block);
            var receiverType = receiverSyntax?.Type is not null
                ? (_model.GetTypeInfo(receiverSyntax.Type).Type
                   ?? (_model.GetSymbolInfo(receiverSyntax.Type).Symbol as ITypeSymbol))
                : null;
            if (receiverSyntax is null || receiverType is null)
            {
                _stats.UnsupportedMembers++;
                AddDiagnostic(
                    ExtensionSeverity.Error,
                    ExtensionBlockCodes.Unsupported,
                    ExtensionBlockMessages.UnsupportedMember("extension block without a typed receiver parameter"),
                    block);
                continue;
            }

            if (receiverType is IPointerTypeSymbol)
            {
                // Probe-pinned (CS0214/CS1103): pointer receivers have no
                // legal materialization; the whole block is left unchanged.
                _stats.UnsupportedReceivers++;
                AddDiagnostic(
                    ExtensionSeverity.Error,
                    ExtensionBlockCodes.UnsupportedReceiver,
                    ExtensionBlockMessages.UnsupportedReceiver(
                        "pointer receiver ('" + receiverType.ToDisplayString() + "') is rejected by Roslyn CS0214/CS1103"),
                    block);
                continue;
            }

            var receiver = new BlockReceiverInfo(
                receiverType,
                receiverSyntax.Identifier.ValueText,
                GetRefKind(receiverSyntax),
                GetScopedKind(receiverSyntax));

            var members = new List<MemberPlan>();
            var rejected = false;

            foreach (var member in ExtensionBlockSyntaxFacts.GetMembers(block))
            {
                switch (member)
                {
                    case MethodDeclarationSyntax method:
                    {
                        if (_model.GetDeclaredSymbol(method) is not IMethodSymbol
                            {
                                MethodKind: MethodKind.Ordinary,
                            } methodSymbol
                            || methodSymbol.ContainingType.TypeKind != TypeKind.Extension)
                        {
                            RejectMember(block, "method (unrecognized symbol)");
                            rejected = true;
                            break;
                        }

                        if (methodSymbol.IsStatic)
                        {
                            if (!_options.EnableStaticMembers)
                            {
                                RejectMember(block, "static method (EMD-STATIC disabled)");
                                rejected = true;
                                break;
                            }

                            members.Add(new StaticMethodMemberPlan
                            {
                                Declaration = method,
                                Name = methodSymbol.Name,
                                Symbol = methodSymbol,
                                MemberTypeParameters = method.TypeParameterList?.Parameters
                                    .ToList() ?? new List<TypeParameterSyntax>(),
                                MemberConstraints = method.ConstraintClauses.ToList(),
                            });
                            break;
                        }

                        if (!_options.EnableMethods)
                        {
                            RejectMember(block, "method (EMD-METHOD disabled)");
                            rejected = true;
                            break;
                        }

                        members.Add(new MethodMemberPlan
                        {
                            Declaration = method,
                            Name = methodSymbol.Name,
                            Symbol = methodSymbol,
                            MemberTypeParameters = method.TypeParameterList?.Parameters
                                .ToList() ?? new List<TypeParameterSyntax>(),
                            MemberConstraints = method.ConstraintClauses.ToList(),
                        });
                        break;
                    }

                    case PropertyDeclarationSyntax property:
                    {
                        if (_model.GetDeclaredSymbol(property) is not IPropertySymbol propertySymbol
                            || propertySymbol.ContainingType.TypeKind != TypeKind.Extension)
                        {
                            RejectMember(block, "property (unrecognized symbol)");
                            rejected = true;
                            break;
                        }

                        if (propertySymbol.IsStatic)
                        {
                            if (!_options.EnableStaticMembers)
                            {
                                RejectMember(block, "static property (EMD-STATIC disabled)");
                                rejected = true;
                                break;
                            }

                            members.Add(new StaticPropertyMemberPlan
                            {
                                Declaration = property,
                                Name = propertySymbol.Name,
                                Symbol = propertySymbol,
                                PropertyDeclaration = property,
                                HasGet = propertySymbol.GetMethod is not null,
                                HasSet = propertySymbol.SetMethod is not null,
                            });
                            break;
                        }

                        if (!_options.EnableProperties)
                        {
                            RejectMember(block, "property (EMD-PROP disabled)");
                            rejected = true;
                            break;
                        }

                        members.Add(new PropertyMemberPlan
                        {
                            Declaration = property,
                            Name = propertySymbol.Name,
                            Symbol = propertySymbol,
                            PropertyDeclaration = property,
                            HasGet = propertySymbol.GetMethod is not null,
                            HasSet = propertySymbol.SetMethod is not null,
                        });
                        break;
                    }

                    case OperatorDeclarationSyntax operatorDeclaration:
                    {
                        if (_model.GetDeclaredSymbol(operatorDeclaration) is not IMethodSymbol
                            {
                                MethodKind: MethodKind.UserDefinedOperator,
                            } operatorSymbol
                            || operatorSymbol.ContainingType.TypeKind != TypeKind.Extension)
                        {
                            RejectMember(block, "operator (unrecognized symbol)");
                            rejected = true;
                            break;
                        }

                        if (!_options.EnableOperators)
                        {
                            RejectMember(block, "operator (EMD-OP disabled)");
                            rejected = true;
                            break;
                        }

                        if (!OperatorNames.IsSupportedOperatorName(operatorSymbol.Name))
                        {
                            RejectMember(block, "operator '" + operatorSymbol.Name + "' (not in the C14 supported set)");
                            rejected = true;
                            break;
                        }

                        members.Add(new OperatorMemberPlan
                        {
                            Declaration = operatorDeclaration,
                            Name = operatorSymbol.Name,
                            Symbol = operatorSymbol,
                            OperatorDeclaration = operatorDeclaration,
                            GeneratedName = OperatorNames.ConvertOperatorName(operatorSymbol.Name),
                        });
                        break;
                    }

                    case IndexerDeclarationSyntax:
                        _stats.IndexersRejected++;
                        AddDiagnostic(
                            ExtensionSeverity.Error,
                            ExtensionBlockCodes.Unsupported,
                            ExtensionBlockMessages.BlockIndexer,
                            member);
                        rejected = true;
                        break;

                    default:
                        RejectMember(block, member.GetType().Name);
                        rejected = true;
                        break;
                }
            }

            if (rejected)
            {
                continue;
            }

            var blockPlan = new BlockPlan
            {
                Block = block,
                Host = host,
                Receiver = receiver,
                BlockTypeParameters = block.TypeParameterList?.Parameters
                    .ToList() ?? new List<TypeParameterSyntax>(),
                BlockConstraints = block.ConstraintClauses.ToList(),
                Members = members,
                BlockAnnotation = new SyntaxAnnotation("C49", "BlockDeclaration"),
            };
            plans.Add(blockPlan);
            _stats.BlocksMaterialized++;

            foreach (var member in members)
            {
                switch (member)
                {
                    case MethodMemberPlan:
                        _stats.MethodsEmitted++;
                        break;
                    case PropertyMemberPlan property:
                        if (property.HasGet)
                        {
                            _stats.PropertyGettersEmitted++;
                        }

                        if (property.HasSet)
                        {
                            _stats.PropertySettersEmitted++;
                        }

                        break;
                    case OperatorMemberPlan:
                        _stats.OperatorsEmitted++;
                        break;
                    case StaticMethodMemberPlan:
                        _stats.StaticMethodsEmitted++;
                        break;
                    case StaticPropertyMemberPlan staticProperty:
                        if (staticProperty.HasGet)
                        {
                            _stats.StaticPropertyGettersEmitted++;
                        }

                        if (staticProperty.HasSet)
                        {
                            _stats.StaticPropertySettersEmitted++;
                        }

                        break;
                }
            }

            if (_options.ReportInfo)
            {
                AddDiagnostic(
                    ExtensionSeverity.Info,
                    ExtensionMethodCodes.Info,
                    $"extension block materialized into host '{host.ToDisplayString()}' with {members.Count} member(s)",
                    block);
            }
        }

        return plans;
    }

    private static bool IsIncDecAccess(
        MemberAccessExpressionSyntax access,
        out ExpressionStatementSyntax statement,
        out SyntaxKind unaryKind)
    {
        var parent = access.Parent;
        var operandMatches = parent switch
        {
            PrefixUnaryExpressionSyntax prefix => prefix.Operand == access,
            PostfixUnaryExpressionSyntax postfix => postfix.Operand == access,
            _ => false,
        };

        statement = operandMatches
            && parent!.Parent is ExpressionStatementSyntax expressionStatement
            && expressionStatement.Expression == parent
            ? expressionStatement
            : null!;
        unaryKind = statement is null ? SyntaxKind.None : statement.Expression.Kind();
        return statement is not null;
    }

    private void AddCompoundPlan(
        ExpressionStatementSyntax statement,
        MemberAccessExpressionSyntax target,
        IPropertySymbol property,
        PropertyMemberPlan propertyMember,
        BlockPlan block,
        SyntaxKind compoundKind,
        SyntaxKind unaryKind,
        List<PropertyCompoundSitePlan> plans)
    {
        var complex = !IsSimpleReceiver(target.Expression);
        string? receiverTemp = null;
        if (complex)
        {
            receiverTemp = _allocator.Allocate("recv");
        }

        plans.Add(new PropertyCompoundSitePlan
        {
            Statement = statement,
            Target = target,
            BoundSymbol = property,
            Member = propertyMember,
            Block = block,
            CompoundKind = compoundKind,
            UnaryKind = unaryKind,
            HoistReceiver = complex,
            ReceiverTemp = receiverTemp,
            StatementAnnotation = new SyntaxAnnotation("C49", "BlockCompoundStatement"),
        });
        _stats.CompoundSitesRewritten++;
    }

    private static void DeferCompound(List<DeferredSitePlan> deferred, SyntaxNode site)
        => deferred.Add(new DeferredSitePlan
        {
            Site = site,
            IsProperty = true,
            Code = ExtensionMethodCodes.NullConditional,
            Detail = "Compound assignment to an extension property is only supported at a statement position.",
        });

    private void DeferConditional(
        List<DeferredSitePlan> deferred,
        ConditionalAccessExpressionSyntax conditional,
        bool isProperty)
    {
        var severity = _options.Strict ? ExtensionSeverity.Error : ExtensionSeverity.Warning;
        var message = ExtensionBlockMessages.ConditionalDeferred(isProperty);
        deferred.Add(new DeferredSitePlan
        {
            Site = conditional,
            IsProperty = isProperty,
            Code = ExtensionMethodCodes.NullConditional,
            Detail = message,
        });
        AddDiagnostic(severity, ExtensionMethodCodes.NullConditional, message, conditional);
        _stats.ConditionalsDeferred++;
    }

    private void CollectConditional(
        ConditionalPlanFactory.BuildResult result,
        ConditionalAccessExpressionSyntax outer,
        bool isProperty,
        List<ExtensionConditionalPlan> plans,
        List<DeferredSitePlan> deferred,
        BlockPlan block)
    {
        if (result.Plan is { } plan)
        {
            plans.Add(plan);
            if (isProperty)
            {
                _stats.PropertyConditionalsLifted++;
            }
            else
            {
                _stats.MethodConditionalsLifted++;
            }

            return;
        }

        var severity = _options.Strict ? ExtensionSeverity.Error : ExtensionSeverity.Warning;
        var message = result.SkipReason ?? ExtensionBlockMessages.ConditionalDeferred(isProperty);
        deferred.Add(new DeferredSitePlan
        {
            Site = outer,
            IsProperty = isProperty,
            Code = ExtensionMethodCodes.NullConditional,
            Detail = message,
        });
        AddDiagnostic(severity, ExtensionMethodCodes.NullConditional, message, outer);
        _stats.ConditionalsDeferred++;
        block.HasDeferredSites = true;
    }

    private void RejectMember(ExtensionBlockDeclarationSyntax block, string what)
    {
        _stats.UnsupportedMembers++;
        AddDiagnostic(
            ExtensionSeverity.Error,
            ExtensionBlockCodes.Unsupported,
            ExtensionBlockMessages.UnsupportedMember(what),
            block);
    }

    // ---- M3 helper: operator ET skip ----

    private void SkipOperatorInExpressionTree(
        SyntaxNode site,
        IMethodSymbol op,
        BlockPlan block,
        List<DeferredSitePlan> deferred)
    {
        var severity = _options.Strict ? ExtensionSeverity.Error : ExtensionSeverity.Warning;
        deferred.Add(new DeferredSitePlan
        {
            Site = site,
            IsProperty = false,
            Code = ExtensionMethodCodes.ReducedMethodGroup,
            Detail = ExtensionBlockMessages.ExpressionTreeOperatorSkipped,
        });
        AddDiagnostic(severity, ExtensionMethodCodes.ReducedMethodGroup,
            ExtensionBlockMessages.ExpressionTreeOperatorSkipped, site);
        block.HasDeferredSites = true;
        _stats.ExpressionTreeOperatorsSkipped++;
    }

    // ---- M3 helper: r?.P++ CS1059 pairing ----

    private void DeferIncrement(
        ConditionalAccessExpressionSyntax conditional,
        List<DeferredSitePlan> deferred)
    {
        var severity = _options.Strict ? ExtensionSeverity.Error : ExtensionSeverity.Warning;
        deferred.Add(new DeferredSitePlan
        {
            Site = conditional,
            IsProperty = false,
            Code = ExtensionMethodCodes.NullConditional,
            Detail = ExtensionBlockMessages.ConditionalIncrementDeferred,
        });
        AddDiagnostic(severity, ExtensionMethodCodes.NullConditional,
            ExtensionBlockMessages.ConditionalIncrementDeferred, conditional);
        _stats.ConditionalsDeferred++;
    }

    // ---- M3 helper: block method group delegate conversion ----

    private void AddBlockGroupSite(
        MemberAccessExpressionSyntax access,
        IMethodSymbol method,
        (MemberPlan Plan, BlockPlan Block) entry,
        List<ExtensionGroupPlan> blockGroups,
        List<DeferredSitePlan> deferred)
    {
        var severity = _options.Strict ? ExtensionSeverity.Error : ExtensionSeverity.Warning;

        // Multiple overload candidates at one access produce only one
        // site plan/diagnostic.
        if (blockGroups.Any(g => g.MemberAccess == access)
            || deferred.Any(d => d.Site == access))
        {
            return;
        }

        void Skip(string detail)
        {
            deferred.Add(new DeferredSitePlan
            {
                Site = access,
                IsProperty = false,
                Code = ExtensionMethodCodes.ReducedMethodGroup,
                Detail = detail,
            });
            AddDiagnostic(severity, ExtensionMethodCodes.ReducedMethodGroup, detail, access);
            entry.Block.HasDeferredSites = true;
        }

        var isStatic = method.IsStatic;
        if (isStatic)
        {
            if (entry.Plan is not StaticMethodMemberPlan)
            {
                Skip("Block method group: the resolved block member is not a static method; sugar retained.");
                return;
            }

            // Type-name qualified only.
            if (_model.GetSymbolInfo(access.Expression).Symbol is not ITypeSymbol)
            {
                return;
            }
        }
        else if (entry.Plan is not MethodMemberPlan)
        {
            Skip("Block method group: the resolved block member is not an instance method; sugar retained.");
            return;
        }

        if (ExtensionExpressionTreeFacts.IsInExpressionTreeLambda(access, _model))
        {
            Skip("Block method group inside an Expression<TDelegate> is not rewritten in C49 M3; sugar retained.");
            return;
        }

        if (entry.Block.Receiver.RefKind != RefKind.None)
        {
            Skip("Block method group on a ref/in/out receiver block cannot become a capture lambda in C49 M3; sugar retained.");
            return;
        }

        if (method.ReturnsByRef || method.ReturnsByRefReadonly)
        {
            Skip("Block method group returning by ref cannot become a capture lambda in C49 M3; sugar retained.");
            return;
        }

        if (_model.GetTypeInfo(access).ConvertedType is not INamedTypeSymbol
            {
                TypeKind: TypeKind.Delegate,
                DelegateInvokeMethod: { } delegateInvoke,
            } convertedType)
        {
            Skip("Block method group is not converted to a delegate type at this site; sugar retained.");
            return;
        }

        var methodParameters = method.Parameters;
        if (methodParameters.Length != delegateInvoke.Parameters.Length)
        {
            Skip("Block method group parameter count does not match the delegate Invoke signature; sugar retained.");
            return;
        }

        for (var i = 0; i < methodParameters.Length; i++)
        {
            if (!SymbolEqualityComparer.Default.Equals(
                    methodParameters[i].Type, delegateInvoke.Parameters[i].Type))
            {
                Skip("Block method group parameter types do not match the delegate Invoke signature; sugar retained.");
                return;
            }
        }

        if (!SymbolEqualityComparer.Default.Equals(method.ReturnType, delegateInvoke.ReturnType))
        {
            Skip("Block method group return type does not match the delegate Invoke signature; sugar retained.");
            return;
        }

        ExtensionAnchor? anchor = null;
        var directCapture = false;
        string? tempName = null;
        SyntaxAnnotation? anchorAnnotation = null;

        if (!isStatic)
        {
            var receiver = access.Expression;
            if (IsSimpleReceiver(receiver))
            {
                directCapture = true;
            }
            else
            {
                var resolvedAnchor = ExtensionTempLift.ResolveAnchor(access, _model);
                if (resolvedAnchor.Kind == ExtensionAnchorKind.None
                    || !ExtensionTempLift.IsAtDominantExpressionPosition(access))
                {
                    Skip("Block method group receiver cannot be safely captured at this position; sugar retained.");
                    return;
                }

                anchor = resolvedAnchor;
                tempName = _allocator.Allocate("gr");
                anchorAnnotation = new SyntaxAnnotation("C49", "BlockGroupAnchor");
            }
        }

        var lambdaNames = Enumerable.Range(0, methodParameters.Length)
            .Select(_ => _allocator.Allocate("p"))
            .ToList();

        // Full explicit type argument list: n block args + m member args.
        var blockCount = entry.Block.BlockTypeParameters.Count;
        var allTypeArguments = method.ContainingType.TypeArguments.Take(blockCount)
            .Concat(method.TypeArguments)
            .ToList();
        SimpleNameSyntax? targetName = null;
        if (allTypeArguments.Count > 0)
        {
            targetName = SyntaxFactory.GenericName(
                SyntaxFactory.Identifier(method.Name),
                SyntaxFactory.TypeArgumentList(SyntaxFactory.SeparatedList(
                    allTypeArguments.Select(t => (TypeSyntax)SyntaxFactory.ParseTypeName(
                        t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))))));
        }

        blockGroups.Add(new ExtensionGroupPlan
        {
            MemberAccess = access,
            Reduced = method,
            Unreduced = method,
            DelegateType = convertedType,
            IsInExpressionTree = false,
            DirectCapture = directCapture,
            Anchor = anchor,
            TempName = tempName,
            LambdaParameterNames = lambdaNames,
            AnchorAnnotation = anchorAnnotation,
            HostOverride = ExtensionHostSyntaxFactory.BuildHostName(entry.Block.Host),
            NoReceiver = isStatic,
            TargetName = targetName,
        });
        _stats.GroupsRewritten++;
    }

    // ---- M3 helper: external metadata boundary ----

    private void ScanExternalMetadata(
        CompilationUnitSyntax root,
        Dictionary<ISymbol, (MemberPlan Plan, BlockPlan Block)> membersByOriginal,
        List<ExternalExtensionSite> externalSites)
    {
        var seenSpans = new HashSet<int>();
        var filter = _metadataIndex;

        // The boundary scan inspects only invocation / member-access /
        // member-binding shapes (plus the operator shapes, iff some referenced
        // container declares an operator member — which is exactly when the
        // shared walk was asked to collect them). The historical
        // `DescendantNodesAndSelf()` sweep produced these in document order;
        // the shared index carries the very same nodes in the very same order,
        // so the collected sites, the span-dedup and the emitted diagnostics
        // are identical.
        var nodes = _syntaxIndex is { } index
            ? (IEnumerable<SyntaxNode>)index.BoundaryNodes
            : root.DescendantNodesAndSelf();

        foreach (var node in nodes)
        {
            ISymbol? symbol;
            switch (node)
            {
                // Named members: an extension container can only be reached
                // through a member whose name it exposes, so any other name is
                // skipped without binding.
                case InvocationExpressionSyntax
                    or MemberAccessExpressionSyntax
                    or MemberBindingExpressionSyntax:
                    if (filter is not null && !filter.NamedMemberCouldMatch(node))
                    {
                        continue;
                    }

                    symbol = _model.GetSymbolInfo(node).Symbol;
                    break;

                // Operator members carry no syntax name; only query when some
                // metadata container declares one.
                case BinaryExpressionSyntax
                    or PrefixUnaryExpressionSyntax
                    or AssignmentExpressionSyntax:
                    if (filter is { HasOperatorMembers: false })
                    {
                        continue;
                    }

                    symbol = _model.GetSymbolInfo(node).Symbol;
                    break;

                default:
                    continue;
            }

            if (symbol is not (IMethodSymbol or IPropertySymbol)
                || symbol.ContainingType?.TypeKind != TypeKind.Extension)
            {
                continue;
            }

            if (membersByOriginal.ContainsKey(symbol.OriginalDefinition))
            {
                continue;
            }

            // Source-declared but block absent (rejected blocks): NOT external.
            if (symbol.DeclaringSyntaxReferences.Length > 0)
            {
                continue;
            }

            if (!seenSpans.Add(node.SpanStart))
            {
                continue;
            }

            var assemblyName = symbol.ContainingAssembly?.Name ?? "unknown";
            externalSites.Add(new ExternalExtensionSite(node, symbol, assemblyName));
            AddDiagnostic(
                ExtensionSeverity.Warning,
                ExtensionBlockCodes.ExternalMetadata,
                ExtensionBlockMessages.ExternalMetadataSite(assemblyName, symbol.ToDisplayString()),
                node);
            _stats.ExternalMetadataSites++;
        }
    }

    private void AddDiagnostic(
        ExtensionSeverity severity,
        string code,
        string message,
        SyntaxNode site)
        => _diagnostics.Add(new ExtensionDiagnostic(
            severity,
            ExtensionRule.Ext14,
            code,
            message,
            _model.SyntaxTree.FilePath,
            site.GetLocation().GetLineSpan().StartLinePosition.Line + 1));

    private static RefKind GetRefKind(ParameterSyntax parameter)
    {
        if (parameter.Modifiers.Any(SyntaxKind.RefKeyword))
        {
            // "ref readonly" (probe-pinned ordering; CS9190 otherwise).
            return parameter.Modifiers.Any(SyntaxKind.ReadOnlyKeyword)
                ? RefKind.RefReadOnlyParameter
                : RefKind.Ref;
        }

        if (parameter.Modifiers.Any(SyntaxKind.OutKeyword))
        {
            return RefKind.Out;
        }

        if (parameter.Modifiers.Any(SyntaxKind.InKeyword))
        {
            return RefKind.In;
        }

        return RefKind.None;
    }

    /// <summary>
    /// Syntax-level nullability (model GetTypeInfo.Type, not the converted
    /// operation type): true for the T? operand shape of lifted operators.
    /// </summary>
    private bool IsNullableValueType(ExpressionSyntax expression)
        => _model.GetTypeInfo(expression).Type is INamedTypeSymbol named
           && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;

    /// <summary>Minimal display of the site's result type (for lifted null fallback casts).</summary>
    private string GetResultTypeDisplay(ExpressionSyntax site)
    {
        var type = _model.GetTypeInfo(site).Type;
        return type is null
            ? "object"
            : type.ToMinimalDisplayString(_model, site.SpanStart);
    }

    private static ScopedKind GetScopedKind(ParameterSyntax parameter)
    {
        if (!parameter.Modifiers.Any(SyntaxKind.ScopedKeyword))
        {
            return ScopedKind.None;
        }

        return parameter.Modifiers.Any(SyntaxKind.RefKeyword)
            ? ScopedKind.ScopedRef
            : ScopedKind.ScopedValue;
    }

    /// <summary>
    /// Simple receiver for inlining: bare local/parameter/this reference
    /// (no per-evaluation side effects). Mirrors the property-use criterion.
    /// </summary>
    internal static bool IsSimpleReceiver(ExpressionSyntax receiver)
    {
        if (receiver is ThisExpressionSyntax)
        {
            return true;
        }

        if (receiver is IdentifierNameSyntax)
        {
            return true;
        }

        return false;
    }
}
