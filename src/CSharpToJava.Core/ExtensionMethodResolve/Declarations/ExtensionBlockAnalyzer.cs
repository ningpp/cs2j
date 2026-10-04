using CSharpToJava.Core.MemberDesugar.Operators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CSharpToJava.Core.ExtensionMethodResolve.Declarations;

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
    private readonly ExtensionTempAllocator _allocator;
    private readonly List<ExtensionDiagnostic> _diagnostics = new();
    private readonly ExtensionBlockStatistics _stats = new();

    public ExtensionBlockAnalyzer(SemanticModel model, ExtensionBlockOptions options)
    {
        _model = model;
        _options = options;
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

        var conditionalsCovered = new HashSet<ConditionalAccessExpressionSyntax>();

        // ---- Invocations: block methods (instance + static) ----
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
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
                        staticMethodSites, deferred);
                    break;

                case MethodMemberPlan methodMember:
                    AddInstanceMethodSite(
                        invocation, method, methodMember, block,
                        conditionalsCovered, conditionalPlans, hostOverrides, deferred,
                        methodSites);
                    break;
            }
        }

        // ---- Member access: block properties, instance + static (read/write/compound) ----
        foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
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
        foreach (var binary in root.DescendantNodes().OfType<BinaryExpressionSyntax>())
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
        foreach (var unary in root.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>())
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
        foreach (var assignment in root.DescendantNodes()
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

        // ---- Block method groups without invocation: x.M as a delegate (M1: defer) ----
        foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (access.Parent is InvocationExpressionSyntax { Expression: { } invoked } && invoked == access)
            {
                continue;
            }

            if (_model.GetSymbolInfo(access).Symbol is not IMethodSymbol
                {
                    MethodKind: MethodKind.Ordinary,
                } groupMethod
                || groupMethod.ContainingType.TypeKind != TypeKind.Extension
                || !membersByOriginal.TryGetValue(groupMethod.OriginalDefinition, out var groupEntry))
            {
                continue;
            }

            deferred.Add(new DeferredSitePlan
            {
                Site = access,
                IsProperty = false,
                Code = ExtensionMethodCodes.ReducedMethodGroup,
                Detail = "Block method group conversion is not rewritten in C49 M1; sugar retained.",
            });
            AddDiagnostic(ExtensionSeverity.Warning, ExtensionMethodCodes.ReducedMethodGroup,
                "Block method group conversion is not rewritten in C49 M1; sugar retained.", access);
            groupEntry.Block.HasDeferredSites = true;
        }

        // ---- Outermost null-conditional chains: property bindings & extended shapes ----
        foreach (var conditional in root.DescendantNodes()
                     .OfType<ConditionalAccessExpressionSyntax>()
                     .Where(c => !c.Ancestors().OfType<ConditionalAccessExpressionSyntax>().Any()))
        {
            if (conditionalsCovered.Contains(conditional))
            {
                continue;
            }

            switch (conditional.WhenNotNull)
            {
                case MemberBindingExpressionSyntax binding
                    when _model.GetSymbolInfo(binding).Symbol is IPropertySymbol boundProperty
                         && boundProperty.ContainingType.TypeKind == TypeKind.Extension
                         && membersByOriginal.TryGetValue(boundProperty.OriginalDefinition, out var entry)
                         && entry.Plan is PropertyMemberPlan boundMember:
                {
                    conditionalsCovered.Add(conditional);
                    var propertyBuild = ConditionalPlanFactory.BuildPropertyPlan(
                        conditional, binding, boundProperty, boundMember, entry.Block,
                        _model, _allocator, hostOverrides, conditionalSeams);
                    CollectConditional(propertyBuild, conditional, isProperty: true,
                        conditionalPlans, deferred, entry.Block);
                    break;
                }

                // r?.ExtProp.B and other extended property shapes: defer.
                case MemberAccessExpressionSyntax { Expression: MemberBindingExpressionSyntax prefix }
                    when _model.GetSymbolInfo(prefix).Symbol is IPropertySymbol extendedProperty
                         && extendedProperty.ContainingType.TypeKind == TypeKind.Extension:
                {
                    conditionalsCovered.Add(conditional);
                    DeferConditional(deferred, conditional, isProperty: true);
                    if (membersByOriginal.TryGetValue(extendedProperty.OriginalDefinition, out var extEntry))
                    {
                        extEntry.Block.HasDeferredSites = true;
                    }

                    break;
                }
            }
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
            Diagnostics = _diagnostics,
            Statistics = _stats,
        };
    }

    // ---- Invocation site builders (extracted M1 logic + M2 static sites) ----

    private void AddStaticMethodSite(
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        StaticMethodMemberPlan staticMember,
        BlockPlan block,
        List<StaticMethodSitePlan> sites,
        List<DeferredSitePlan> deferred)
    {
        // EMD-STATIC sites are type-name qualified: the MemberAccess left side
        // must bind to a type symbol. Bare/static-other shapes are not planned
        // (ordinary member resolution covers them after materialization).
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess
            || _model.GetSymbolInfo(memberAccess.Expression).Symbol is not ITypeSymbol)
        {
            return;
        }

        if (ExtensionExpressionTreeFacts.IsInExpressionTreeLambda(invocation, _model))
        {
            _stats.SitesSkippedExpressionTree++;
            deferred.Add(new DeferredSitePlan
            {
                Site = invocation,
                IsProperty = false,
                Code = ExtensionMethodCodes.ReducedMethodGroup,
                Detail = ExtensionBlockMessages.ExpressionTreeSiteSkipped,
            });
            AddDiagnostic(ExtensionSeverity.Warning, ExtensionMethodCodes.ReducedMethodGroup,
                ExtensionBlockMessages.ExpressionTreeSiteSkipped, invocation);
            block.HasDeferredSites = true;
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
    }

    private void AddInstanceMethodSite(
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        MethodMemberPlan methodMember,
        BlockPlan block,
        HashSet<ConditionalAccessExpressionSyntax> conditionalsCovered,
        List<ExtensionConditionalPlan> conditionalPlans,
        IDictionary<ConditionalSpineCall, NameSyntax> hostOverrides,
        List<DeferredSitePlan> deferred,
        List<MethodSitePlan> sites)
    {
        // r?.M() member-binding invocation → conditional plan / defer.
        if (invocation.Expression is MemberBindingExpressionSyntax
            && FindOutermostConditional(invocation) is { } outer)
        {
            conditionalsCovered.Add(outer);
            var build = ConditionalPlanFactory.BuildMethodPlan(
                outer, invocation, method, methodMember, block,
                _model, _allocator, hostOverrides);
            CollectConditional(build, outer, isProperty: false,
                conditionalPlans, deferred, block);
            return;
        }

        if (ExtensionExpressionTreeFacts.IsInExpressionTreeLambda(invocation, _model))
        {
            _stats.SitesSkippedExpressionTree++;
            deferred.Add(new DeferredSitePlan
            {
                Site = invocation,
                IsProperty = false,
                Code = ExtensionMethodCodes.ReducedMethodGroup,
                Detail = ExtensionBlockMessages.ExpressionTreeSiteSkipped,
            });
            AddDiagnostic(ExtensionSeverity.Warning, ExtensionMethodCodes.ReducedMethodGroup,
                ExtensionBlockMessages.ExpressionTreeSiteSkipped, invocation);
            block.HasDeferredSites = true;
            return;
        }

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

    private static ConditionalAccessExpressionSyntax? FindOutermostConditional(SyntaxNode site)
    {
        var conditional = site.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault();
        return conditional;
    }
}
