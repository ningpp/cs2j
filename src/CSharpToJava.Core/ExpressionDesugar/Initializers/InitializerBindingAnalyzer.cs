using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using CSharpToJava.Core.ExpressionDesugar;

namespace CSharpToJava.Core.ExpressionDesugar.Initializers;

/// <summary>Analyze product: site plans plus the shared temp maps the rewriter needs.</summary>
internal sealed class InitializerAnalysis
{
    public required IReadOnlyList<InitializerPlan> Plans { get; init; }

    /// <summary>Conditional/coalesce owner node -&gt; definite-assignment result temp.</summary>
    public required IReadOnlyDictionary<SyntaxNode, string> OwnerTemps { get; init; }

    /// <summary>Hoisted preceding argument -&gt; temp (argument-position sites).</summary>
    public required IReadOnlyDictionary<ArgumentSyntax, string> HoistedArguments { get; init; }
}

/// <summary>
/// C24 semantic site inventory and binding classification (design §2 decisions
/// 2/5/6/7/8/9/10/11/12), M1+M2 main path.
///
/// Every top-level <see cref="ObjectCreationExpressionSyntax"/> with an
/// initializer and every <see cref="ImplicitObjectCreationExpressionSyntax"/> is
/// classified from the full-compile <c>IOperation</c> tree: object items are
/// property/field writes, dictionary index writes, nested creations and nested
/// member initializers; collection items are bound <c>Add</c> invocations
/// (single/multi/extension/params/fluent). Sites nested inside an enclosing
/// initializer are lowered inline by their parent and never classified as
/// top-level. Diagnostics CS2J0531/0533/0535/0537/0538/0541 are Error-class under
/// strict options (the family entry keeps the whole file atomically unchanged)
/// and Warnings otherwise; CS2J0539/0540 are verbose Info.
/// </summary>
internal static class InitializerBindingAnalyzer
{
    /// <summary>Sentinel skip code for shapes intentionally preserved (C36 coexistence).</summary>
    public const string UnsupportedSentinel = "__m2__";

    private sealed class AnalyzeState
    {
        public required ExpressionDesugarTempNameAllocator Allocator { get; init; }
        public List<InitializerPlan> All { get; } = new();
        public Dictionary<SyntaxNode, string> OwnerTemps { get; } = new();
        public Dictionary<ArgumentSyntax, string> Hoisted { get; } = new();
        public HashSet<ArgumentSyntax> DeclaredHoists { get; } = new();
    }

    public static InitializerAnalysis Analyze(RuleAnalysisContext context)
    {
        var state = new AnalyzeState
        {
            Allocator = new ExpressionDesugarTempNameAllocator(context.Root),
        };
        var model = context.SemanticModel;
        if (model == null)
        {
            return new InitializerAnalysis
            {
                Plans = state.All,
                OwnerTemps = state.OwnerTemps,
                HoistedArguments = state.Hoisted,
            };
        }

        // C36 coexistence gate, verified empirically (Roslyn rewriter returns
        // FRESH node instances for untouched siblings once any node in the file
        // is replaced — see the identity probe in the C24 unit tests). C36 keys
        // its site plans by ORIGINAL node reference on the shared progressive
        // tree, so ANY C24 rewrite in a file that also contains a C36
        // collection-expression site makes every C36 plan lookup miss (C36
        // leaves its syntax behind -> CS2J0589 -> whole-file revert). Until the
        // family keys plans stably (annotation/span; registered as family tech
        // debt), such a file is deferred whole: sites are inventoried, no
        // semantic classification, no rewrite, no annotation, no diagnostic.
        var hasC36Face = context.Root.DescendantNodes()
            .Any(static n => n is CollectionExpressionSyntax);

        foreach (var node in context.Root.DescendantNodes())
        {
            BaseObjectCreationExpressionSyntax? creation = node switch
            {
                ImplicitObjectCreationExpressionSyntax implicitCreation => implicitCreation,
                ObjectCreationExpressionSyntax { Initializer: not null } explicitCreation => explicitCreation,
                _ => null,
            };

            if (creation == null || InitializerSyntaxFacts.IsNestedInInitializer(creation))
                continue;

            if (hasC36Face)
            {
                var deferred = BuildDeferredPlan(context, creation);
                state.All.Add(deferred);
                continue;
            }

            var plan = BuildPlan(context, model, creation, state, isNested: false);
            if (plan.Outcome is InitializerOutcome.ObjectSequence
                or InitializerOutcome.CollectionSequence)
            {
                AllocateTemps(plan, state);
            }
        }

        AssignLiftOrders(state.All);
        return new InitializerAnalysis
        {
            Plans = state.All,
            OwnerTemps = state.OwnerTemps,
            HoistedArguments = state.Hoisted,
        };
    }

    /// <summary>
    /// Site inventory entry for a file deferred whole by the C36 coexistence
    /// gate (see <see cref="Analyze"/>): counted and marked so the rewriter
    /// leaves it byte-identical; no semantic work and no diagnostic.
    /// </summary>
    private static InitializerPlan BuildDeferredPlan(
        RuleAnalysisContext context, BaseObjectCreationExpressionSyntax node)
    {
        var stats = context.Statistics.Initializers;
        var isImplicit = node is ImplicitObjectCreationExpressionSyntax;

        var plan = new InitializerPlan
        {
            Node = node,
            IsTargetTyped = isImplicit,
            Depth = node.Ancestors().OfType<InitializerExpressionSyntax>().Count(),
        };

        CountSite(stats, node, isImplicit);
        stats.AddSkip(UnsupportedSentinel);
        stats.CoexistenceDeferredSites++;
        plan.Outcome = InitializerOutcome.DeferredForCoexistence;
        return plan;
    }

    // ------------------------------------------------------------------
    // Core per-site plan builder (recursive for nested creations).
    // ------------------------------------------------------------------

    private static InitializerPlan BuildPlan(
        RuleAnalysisContext context,
        SemanticModel model,
        BaseObjectCreationExpressionSyntax node,
        AnalyzeState state,
        bool isNested)
    {
        var stats = context.Statistics.Initializers;
        var isImplicit = node is ImplicitObjectCreationExpressionSyntax;

        var plan = new InitializerPlan
        {
            Node = node,
            IsTargetTyped = isImplicit,
            IsNested = isNested,
            Depth = node.Ancestors().OfType<InitializerExpressionSyntax>().Count(),
        };
        state.All.Add(plan);

        if (!isNested)
            CountSite(stats, node, isImplicit);

        // C36 coexistence is handled as a whole-file gate in Analyze: a file
        // carrying any collection-expression syntax never reaches this point.

        // Shared family criterion: expression-tree lambdas are skipped (CS2J0542).
        if (!isNested && ExpressionDesugarSyntaxFacts.IsInExpressionTree(model, node))
        {
            Report(context, InitializerDiagnosticCodes.CS2J0542,
                InitializerDiagnostics.ExpressionTreeMessage, node);
            plan.Outcome = InitializerOutcome.SkippedDiagnostic;
            plan.SkipCode = InitializerDiagnosticCodes.CS2J0542;
            return plan;
        }

        // ---- Created type (explicit or target-typed ConvertedType) ----
        if (isImplicit)
        {
            var converted = model.GetTypeInfo(node).ConvertedType;
            if (converted == null || converted.TypeKind == TypeKind.Error)
            {
                Report(context, InitializerDiagnosticCodes.CS2J0535,
                    InitializerDiagnostics.TargetTypeMissingMessage, node);
                plan.Outcome = InitializerOutcome.SkippedDiagnostic;
                plan.SkipCode = InitializerDiagnosticCodes.CS2J0535;
                return plan;
            }

            plan.TargetType = new TargetTypeInfo
            {
                ConvertedType = converted,
                UnderlyingType = converted,
                IsNullable = converted.NullableAnnotation == NullableAnnotation.Annotated,
            };
            plan.CreatedType = converted;
        }
        else
        {
            plan.CreatedType = model.GetTypeInfo(node).Type;
        }

        // ---- dynamic receiver / dynamic Add (CS2J0541) ----
        if (IsDynamicSite(model.GetOperation(node), plan.CreatedType))
        {
            Report(context, InitializerDiagnosticCodes.CS2J0541,
                InitializerDiagnostics.DynamicReceiverMessage, node);
            plan.Outcome = InitializerOutcome.SkippedDiagnostic;
            plan.SkipCode = InitializerDiagnosticCodes.CS2J0541;
            return plan;
        }

        // ---- Bare target-typed new() (IN-9): zero-temp explicitization ----
        if (node.Initializer == null)
        {
            if (!isNested)
            {
                Report(context, InitializerDiagnosticCodes.CS2J0536,
                    string.Format(
                        InitializerDiagnostics.TargetTypedNewExpandedMessage,
                        plan.CreatedType?.ToDisplayString() ?? "?", string.Empty),
                    node);
            }

            plan.Outcome = InitializerOutcome.TargetTypedNewExplicit;
            return plan;
        }

        var operation = model.GetOperation(node) as IObjectCreationOperation;
        var items = (operation?.Initializer as IObjectOrCollectionInitializerOperation)
            ?.Initializers.ToList();
        var isObjectInitializer = node.Initializer.IsKind(
            SyntaxKind.ObjectInitializerExpression);

        // Empty initializers carry no member semantics (see M1 landing record): a
        // plain object folds to `new C(args)`; an enumerable collection keeps
        // its `{ }` (C36 byte-stability).
        if (items is { Count: 0 })
        {
            if (isObjectInitializer
                && !IsEnumerableCollectionType(model, plan.CreatedType))
            {
                plan.Outcome = InitializerOutcome.ZeroTempExplicit;
                if (!isNested)
                {
                    Report(context, InitializerDiagnosticCodes.CS2J0530,
                        string.Format(
                            InitializerDiagnostics.ObjectExpandedMessage, 0, 0, 0, 0),
                        node);
                }

                return plan;
            }

            plan.Outcome = InitializerOutcome.UnsupportedContext;
            return plan;
        }

        // ---- Context classification ----
        var decision = InitializerContextLowering.Classify(node);
        var chain = InitializerContextLowering.OwnerChain(node);
        // Owner-driven lowering is only safe when the site IS the innermost
        // branch/right expression; an indirect site (argument inside a branch)
        // cannot be hoisted without breaking the guard -> CS2J0538.
        var directOwner = chain.Count > 0 && chain[^1] switch
        {
            ConditionalExpressionSyntax conditional
                => conditional.WhenTrue == node || conditional.WhenFalse == node,
            BinaryExpressionSyntax coalesce => coalesce.Right == node,
            _ => false,
        };
        var ownerHandled = !isNested && directOwner;
        plan.Context = ownerHandled
            ? (chain[^1] is BinaryExpressionSyntax
                ? InitializerContextKind.NullCoalescing
                : InitializerContextKind.Ternary)
            : decision.Kind;
        plan.IsOwnerHandled = ownerHandled;

        if (!isNested && chain.Count > 0 && !ownerHandled)
        {
            Report(context, InitializerDiagnosticCodes.CS2J0538,
                string.Format(
                    InitializerDiagnostics.NotLiftableMessage,
                    "a site nested inside a conditional/coalescing branch"),
                node);
            plan.Outcome = InitializerOutcome.UnsupportedContext;
            plan.SkipCode = InitializerDiagnosticCodes.CS2J0538;
            return plan;
        }

        if (ownerHandled)
        {
            plan.ContextOwner = chain[^1];
            plan.OwnerChain.AddRange(chain);
        }
        else if (!isNested && !decision.Liftable)
        {
            var code = decision.Kind is InitializerContextKind.ConstructorChain
                or InitializerContextKind.LambdaTarget
                ? InitializerDiagnosticCodes.CS2J0537
                : InitializerDiagnosticCodes.CS2J0538;
            var message = code == InitializerDiagnosticCodes.CS2J0537
                ? string.Format(
                    InitializerDiagnostics.UnsupportedContextMessage,
                    decision.Kind.ToString())
                : string.Format(
                    InitializerDiagnostics.NotLiftableMessage,
                    decision.Kind.ToString());
            Report(context, code, message, node);
            plan.Outcome = InitializerOutcome.UnsupportedContext;
            plan.SkipCode = code;
            return plan;
        }

        // ---- Argument interleaving: hoist side-effecting preceding arguments ----
        if (!isNested && !ownerHandled && decision.Kind == InitializerContextKind.Argument)
            PlanHoists(node, state, plan);

        // ---- Item classification (temps are allocated after success) ----
        var skipCode = ClassifyItems(
            context, stats, plan, items, model, state,
            isObjectInitializer, plan.CreatedType);

        if (skipCode != null)
        {
            plan.Outcome = InitializerOutcome.SkippedDiagnostic;
            plan.SkipCode = skipCode;
            return plan;
        }

        plan.Outcome = isObjectInitializer
            ? InitializerOutcome.ObjectSequence
            : InitializerOutcome.CollectionSequence;
        stats.AddContextLowered(plan.Context);

        if (!isNested && plan.Members.OfType<IndexSetPlan>().Any())
            stats.DictionaryIndexInitializerSites++;

        if (!isNested)
            ReportSiteDiagnostics(context, plan, isObjectInitializer);

        return plan;
    }

    // ------------------------------------------------------------------
    // Item classification (object + collection, recursive for nesting).
    // ------------------------------------------------------------------

    private static string? ClassifyItems(
        RuleAnalysisContext context,
        InitializerStatistics stats,
        InitializerPlan plan,
        IReadOnlyList<IOperation>? items,
        SemanticModel model,
        AnalyzeState state,
        bool isObjectInitializer,
        ITypeSymbol? ownerType)
    {
        if (items == null)
        {
            return isObjectInitializer
                ? InitializerDiagnosticCodes.CS2J0531
                : InitializerDiagnosticCodes.CS2J0533;
        }

        var order = 0;
        foreach (var item in items)
        {
            string? skip;
            switch (item)
            {
                case IMemberInitializerOperation memberInit:
                    skip = AddNestedMemberInit(
                        context, stats, plan, memberInit, model, state, ref order);
                    break;

                case ISimpleAssignmentOperation assignment:
                    skip = AddObjectMember(
                        context, stats, plan, assignment, model, state, ref order);
                    break;

                case IInvocationOperation invocation:
                    skip = AddCollectionItem(
                        context, stats, plan, invocation, model, state, ref order);
                    break;

                case IInvalidOperation:
                case IDynamicInvocationOperation:
                    Report(context, InitializerDiagnosticCodes.CS2J0533,
                        string.Format(
                            InitializerDiagnostics.AddUnresolvedMessage,
                            ownerType?.ToDisplayString() ?? "?"),
                        plan.Node);
                    return InitializerDiagnosticCodes.CS2J0533;

                default:
                    return isObjectInitializer
                        ? UnsupportedMember(context, plan, item)
                        : InitializerDiagnosticCodes.CS2J0533;
            }

            if (skip != null)
                return skip;
        }

        return null;
    }

    private static string? AddNestedMemberInit(
        RuleAnalysisContext context,
        InitializerStatistics stats,
        InitializerPlan plan,
        IMemberInitializerOperation memberInit,
        SemanticModel model,
        AnalyzeState state,
        ref int order)
    {
        var memberName = MemberNameOf(memberInit.InitializedMember);
        stats.NestedMemberInitSites++;

        var innerItems = memberInit.Initializer.Initializers.ToList();
        var inner = new List<InitializerMemberPlan>();
        var innerPlan = new InitializerPlan { Node = plan.Node, Members = inner };

        var skip = ClassifyItems(
            context, stats, innerPlan, innerItems, model, state,
            !IsEnumerableMember(model, memberInit.InitializedMember), null);
        if (skip != null)
            return skip;

        plan.Members.Add(new NestedMemberInitPlan
        {
            Syntax = (ExpressionSyntax)memberInit.Syntax!,
            SourceOrder = order++,
            MemberName = memberName,
            Members = inner,
        });

        Report(context, InitializerDiagnosticCodes.CS2J0540,
            string.Format(InitializerDiagnostics.NestedMemberGetterMessage,
                memberName ?? "?"),
            plan.Node);
        return null;
    }

    private static string? AddObjectMember(
        RuleAnalysisContext context,
        InitializerStatistics stats,
        InitializerPlan plan,
        ISimpleAssignmentOperation assignment,
        SemanticModel model,
        AnalyzeState state,
        ref int order)
    {
        switch (assignment.Target)
        {
            case IPropertyReferenceOperation property when property.Property.IsIndexer:
            {
                if (property.Property.SetMethod is not { IsInitOnly: false }
                    || property.Property.IsStatic)
                {
                    return ReportNotWritable(context, plan, property.Property);
                }

                stats.MemberAssignments++;
                stats.MemberIndexerAssignments++;
                plan.Members.Add(new IndexSetPlan
                {
                    Syntax = (ExpressionSyntax)assignment.Syntax!,
                    SourceOrder = order++,
                    Indexer = property.Property,
                    Arguments = IndexKeyExpressions(assignment),
                    Value = (ExpressionSyntax)assignment.Value.Syntax!,
                });
                return null;
            }

            case IPropertyReferenceOperation property:
            {
                if (property.Property.SetMethod is not { IsInitOnly: false }
                    || property.Property.IsStatic)
                {
                    return ReportNotWritable(context, plan, property.Property);
                }

                var nested = TryNested(context, model, assignment.Value, state);
                if (nested != null)
                {
                    stats.NestedCreateSites++;
                    plan.Members.Add(new NestedCreatePlan
                    {
                        Syntax = (ExpressionSyntax)assignment.Syntax!,
                        SourceOrder = order++,
                        MemberName = property.Property.Name,
                        Nested = nested,
                    });
                    return nested.Outcome is InitializerOutcome.SkippedDiagnostic
                        ? nested.SkipCode
                        : null;
                }

                stats.MemberAssignments++;
                stats.MemberPropertyAssignments++;
                plan.Members.Add(new PropertySetPlan
                {
                    Syntax = (ExpressionSyntax)assignment.Syntax!,
                    SourceOrder = order++,
                    Property = property.Property,
                    Value = (ExpressionSyntax)assignment.Value.Syntax!,
                });
                return null;
            }

            case IFieldReferenceOperation field:
            {
                if (field.Field.IsReadOnly || field.Field.IsConst || field.Field.IsStatic)
                {
                    return ReportNotWritable(context, plan, field.Field);
                }

                var nested = TryNested(context, model, assignment.Value, state);
                if (nested != null)
                {
                    stats.NestedCreateSites++;
                    plan.Members.Add(new NestedCreatePlan
                    {
                        Syntax = (ExpressionSyntax)assignment.Syntax!,
                        SourceOrder = order++,
                        MemberName = field.Field.Name,
                        Nested = nested,
                    });
                    return nested.Outcome is InitializerOutcome.SkippedDiagnostic
                        ? nested.SkipCode
                        : null;
                }

                stats.MemberAssignments++;
                stats.MemberFieldAssignments++;
                plan.Members.Add(new FieldSetPlan
                {
                    Syntax = (ExpressionSyntax)assignment.Syntax!,
                    SourceOrder = order++,
                    Field = field.Field,
                    Value = (ExpressionSyntax)assignment.Value.Syntax!,
                });
                return null;
            }

            default:
                return UnsupportedMember(context, plan, assignment);
        }
    }

    private static string? AddCollectionItem(
        RuleAnalysisContext context,
        InitializerStatistics stats,
        InitializerPlan plan,
        IInvocationOperation invocation,
        SemanticModel model,
        AnalyzeState state,
        ref int order)
    {
        stats.AddCalls++;
        var target = invocation.TargetMethod;
        var hasParams = target.Parameters is { Length: > 0 } parameters
            && parameters[parameters.Length - 1].IsParams;

        AddKind kind;
        if (target.IsExtensionMethod)
        {
            kind = AddKind.Extension;
            stats.AddExtension++;
        }
        else if (hasParams)
        {
            kind = AddKind.Params;
            stats.AddParams++;
        }
        else if (invocation.Arguments.Length > 1)
        {
            kind = AddKind.Multi;
            stats.AddMulti++;
        }
        else
        {
            kind = AddKind.Single;
            stats.AddSingle++;
        }

        if (!target.ReturnsVoid)
            stats.AddFluent++;

        var addPlan = new CollectionAddPlan
        {
            Syntax = (ExpressionSyntax)invocation.Syntax!,
            SourceOrder = order++,
            AddMethod = target,
            AddKind = kind,
            Arguments = ElementArguments(invocation.Syntax),
        };

        // Nested creations used as Add arguments are lowered inline.
        foreach (var argument in addPlan.Arguments)
        {
            var nested = TryNestedBySyntax(context, model, argument, state);
            if (nested == null)
                continue;

            if (nested.Outcome is InitializerOutcome.SkippedDiagnostic)
                return nested.SkipCode;

            addPlan.NestedPlans.Add(nested);
        }

        plan.Members.Add(addPlan);
        return null;
    }

    /// <summary>Builds a nested plan for a creation argument that is a complex-element part.</summary>
    private static InitializerPlan? TryNestedBySyntax(
        RuleAnalysisContext context,
        SemanticModel model,
        ExpressionSyntax argument,
        AnalyzeState state)
    {
        BaseObjectCreationExpressionSyntax? creation = argument switch
        {
            ImplicitObjectCreationExpressionSyntax implicitCreation => implicitCreation,
            ObjectCreationExpressionSyntax { Initializer: not null } explicitCreation => explicitCreation,
            _ => null,
        };

        return creation == null
            ? null
            : BuildPlan(context, model, creation, state, isNested: true);
    }

    private static IReadOnlyList<ExpressionSyntax> ElementArguments(SyntaxNode? elementSyntax)
        => elementSyntax switch
        {
            InitializerExpressionSyntax initializer
                when initializer.IsKind(SyntaxKind.ComplexElementInitializerExpression)
                => initializer.Expressions.ToArray(),
            ExpressionSyntax expression => new[] { expression },
            _ => Array.Empty<ExpressionSyntax>(),
        };

    private static InitializerPlan? TryNested(
        RuleAnalysisContext context,
        SemanticModel model,
        IOperation value,
        AnalyzeState state)
    {
        BaseObjectCreationExpressionSyntax? creation = value.Syntax switch
        {
            ImplicitObjectCreationExpressionSyntax implicitCreation => implicitCreation,
            ObjectCreationExpressionSyntax { Initializer: not null } explicitCreation => explicitCreation,
            _ => null,
        };

        return creation == null
            ? null
            : BuildPlan(context, model, creation, state, isNested: true);
    }

    private static string UnsupportedMember(
        RuleAnalysisContext context, InitializerPlan plan, IOperation item)
    {
        Report(context, InitializerDiagnosticCodes.CS2J0531,
            string.Format(
                InitializerDiagnostics.MemberNotWritableMessage,
                item.Syntax?.ToString() ?? "?", "?", "unresolved member"),
            plan.Node);
        return InitializerDiagnosticCodes.CS2J0531;
    }

    private static string ReportNotWritable(
        RuleAnalysisContext context, InitializerPlan plan, ISymbol member)
    {
        var setMethod = (member as IPropertySymbol)?.SetMethod;
        string reason;
        if (setMethod is { IsInitOnly: true })
            reason = "init-only accessor";
        else if (setMethod == null)
            reason = "no set accessor";
        else if (member.IsStatic)
            reason = "static member";
        else if (member is IFieldSymbol { IsReadOnly: true })
            reason = "read-only field (baseline CS0191)";
        else
            reason = "not writable";

        Report(context, InitializerDiagnosticCodes.CS2J0531,
            string.Format(
                InitializerDiagnostics.MemberNotWritableMessage,
                member.Name,
                member.ContainingType?.ToDisplayString() ?? "?",
                reason),
            plan.Node);
        return InitializerDiagnosticCodes.CS2J0531;
    }

    private static void ReportSiteDiagnostics(
        RuleAnalysisContext context,
        InitializerPlan plan,
        bool isObjectInitializer)
    {
        if (isObjectInitializer)
        {
            var propertyCount = plan.Members.OfType<PropertySetPlan>().Count();
            var fieldCount = plan.Members.OfType<FieldSetPlan>().Count();
            var nestedCreate = plan.Members.OfType<NestedCreatePlan>().Count();
            var nestedMember = plan.Members.OfType<NestedMemberInitPlan>().Count();
            var indexerCount = plan.Members.OfType<IndexSetPlan>().Count();
            Report(context, InitializerDiagnosticCodes.CS2J0530,
                string.Format(
                    InitializerDiagnostics.ObjectExpandedMessage,
                    plan.Members.Count, propertyCount, fieldCount, indexerCount),
                plan.Node);

            if (indexerCount > 0)
            {
                Report(context, InitializerDiagnosticCodes.CS2J0534,
                    string.Format(InitializerDiagnostics.DictionaryIndexExpandedMessage,
                        indexerCount),
                    plan.Node);
            }
        }
        else
        {
            var adds = plan.Members.OfType<CollectionAddPlan>().ToList();
            var fluentCount = adds.Count(a => a.AddMethod is { ReturnsVoid: false });
            Report(context, InitializerDiagnosticCodes.CS2J0532,
                string.Format(
                    InitializerDiagnostics.CollectionExpandedMessage,
                    adds.Count, adds.Count, 0, 0, 0, fluentCount, 0),
                plan.Node);

            foreach (var add in adds)
            {
                Report(context, InitializerDiagnosticCodes.CS2J0539,
                    string.Format(
                        InitializerDiagnostics.AddClassifiedMessage,
                        add.AddKind, add.Arguments.Count, string.Empty),
                    plan.Node);
            }
        }
    }

    // ------------------------------------------------------------------
    // Helpers.
    // ------------------------------------------------------------------

    private static void PlanHoists(
        SyntaxNode node, AnalyzeState state, InitializerPlan plan)
    {
        var argument = node.Ancestors().OfType<ArgumentSyntax>().FirstOrDefault();
        if (argument?.Parent is not ArgumentListSyntax list)
            return;

        plan.ArgumentList = list;
        var index = list.Arguments.IndexOf(argument);
        for (var i = 0; i < index; i++)
        {
            var previous = list.Arguments[i];
            if (!HasObservableSideEffect(previous.Expression))
                continue;

            var declare = state.DeclaredHoists.Add(previous);
            plan.HoistRequests.Add((i, previous, declare));
        }
    }

    /// <summary>
    /// Deterministic temp allocation (M2): owner result temps first, then hoist
    /// temps, then the receiver, then nested temps in member source order. Only
    /// successful (sequence) sites allocate, so non-strict skipped sites leave
    /// no numbering holes.
    /// </summary>
    private static void AllocateTemps(InitializerPlan plan, AnalyzeState state)
    {
        if (plan.IsOwnerHandled)
            foreach (var owner in plan.OwnerChain)
                state.OwnerTemps.TryAdd(owner, state.Allocator.Allocate());

        if (plan.ArgumentList != null)
        {
            foreach (var request in plan.HoistRequests)
            {
                if (!state.Hoisted.TryGetValue(request.Argument, out var temp))
                {
                    temp = state.Allocator.Allocate();
                    state.Hoisted[request.Argument] = temp;
                }

                plan.HoistedArguments.Add(
                    new HoistedArgument(request.Index, temp, request.Declare));
            }
        }

        if (plan.Outcome is InitializerOutcome.ObjectSequence
            or InitializerOutcome.CollectionSequence)
        {
            plan.TempName = state.Allocator.Allocate();
        }

        if (plan.IsOwnerHandled && plan.OwnerChain.Count > 0)
            plan.ResultTempName = state.OwnerTemps[plan.OwnerChain[0]];

        foreach (var member in plan.Members)
            AllocateMemberTemps(member, state);
    }

    private static void AllocateMemberTemps(
        InitializerMemberPlan member, AnalyzeState state)
    {
        switch (member)
        {
            case NestedCreatePlan nested:
                AllocateTemps(nested.Nested, state);
                break;

            case NestedMemberInitPlan nestedMember:
                nestedMember.GetterTempName = state.Allocator.Allocate();
                foreach (var inner in nestedMember.Members)
                    AllocateMemberTemps(inner, state);
                break;

            case CollectionAddPlan add:
                foreach (var nested in add.NestedPlans)
                    AllocateTemps(nested, state);
                break;
        }
    }

    private static bool IsDynamicSite(IOperation? operation, ITypeSymbol? createdType)
    {
        if (createdType is IDynamicTypeSymbol)
            return true;

        return ContainsDynamic(operation, 0);
    }

    private static bool ContainsDynamic(IOperation? operation, int depth)
    {
        if (operation == null || depth > 4)
            return false;

        switch (operation)
        {
            case IDynamicInvocationOperation:
            case IDynamicMemberReferenceOperation:
            case IDynamicObjectCreationOperation:
                return true;
        }

        // Dynamic index access has no dedicated interface in Roslyn 5.0.
        if (operation.Kind.ToString().StartsWith("Dynamic", StringComparison.Ordinal))
            return true;

        foreach (var child in operation.ChildOperations)
        {
            if (ContainsDynamic(child, depth + 1))
                return true;
        }

        return false;
    }

    private static bool HasObservableSideEffect(ExpressionSyntax expression) => expression switch
    {
        InvocationExpressionSyntax => true,
        ObjectCreationExpressionSyntax => true,
        ImplicitObjectCreationExpressionSyntax => true,
        AssignmentExpressionSyntax => true,
        AwaitExpressionSyntax => true,
        ThrowExpressionSyntax => true,
        ElementAccessExpressionSyntax => true,
        PrefixUnaryExpressionSyntax prefix =>
            prefix.OperatorToken.IsKind(SyntaxKind.PlusPlusToken)
            || prefix.OperatorToken.IsKind(SyntaxKind.MinusMinusToken),
        PostfixUnaryExpressionSyntax postfix =>
            postfix.OperatorToken.IsKind(SyntaxKind.PlusPlusToken)
            || postfix.OperatorToken.IsKind(SyntaxKind.MinusMinusToken),
        _ => false,
    };

    private static string? MemberNameOf(IOperation member) => member switch
    {
        IPropertyReferenceOperation property => property.Property.Name,
        IFieldReferenceOperation field => field.Field.Name,
        _ => null,
    };

    private static bool IsEnumerableMember(SemanticModel model, IOperation member)
    {
        var enumerable = model.Compilation.GetTypeByMetadataName("System.Collections.IEnumerable");
        if (enumerable == null || member.Type == null)
            return false;

        return member.Type.AllInterfaces.Any(
            i => SymbolEqualityComparer.Default.Equals(i, enumerable));
    }

    private static IReadOnlyList<ExpressionSyntax> IndexKeyExpressions(
        ISimpleAssignmentOperation assignment)
    {
        if (assignment.Syntax is not AssignmentExpressionSyntax syntax)
            return Array.Empty<ExpressionSyntax>();

        return syntax.Left switch
        {
            ImplicitElementAccessSyntax implicitElement =>
                implicitElement.ArgumentList.Arguments
                    .Select(a => a.Expression).ToArray(),
            ElementAccessExpressionSyntax element =>
                element.ArgumentList.Arguments.Select(a => a.Expression).ToArray(),
            _ => Array.Empty<ExpressionSyntax>(),
        };
    }

    private static bool IsEnumerableCollectionType(SemanticModel model, ITypeSymbol? type)
    {
        if (type == null)
            return false;
        if (type.TypeKind == TypeKind.Array)
            return true;

        var enumerable = model.Compilation.GetTypeByMetadataName("System.Collections.IEnumerable");
        return enumerable != null
               && type.AllInterfaces.Any(
                   i => SymbolEqualityComparer.Default.Equals(i, enumerable));
    }

    private static void AssignLiftOrders(List<InitializerPlan> plans)
    {
        var groups = plans
            .Where(p => !p.IsNested
                && p.Outcome is InitializerOutcome.ObjectSequence
                or InitializerOutcome.CollectionSequence)
            .GroupBy(AnchorUnit)
            .OrderBy(g => g.Key.SpanStart)
            .ToList();

        var order = 0;
        foreach (var group in groups)
        {
            foreach (var plan in group)
                plan.LiftOrder = order;
            order++;
        }
    }

    private static SyntaxNode AnchorUnit(InitializerPlan plan)
    {
        if (!plan.IsOwnerHandled || plan.ContextOwner == null)
            return plan.Node;

        var chain = InitializerContextLowering.OwnerChain(plan.Node);
        return chain.Count > 0 ? chain[0] : plan.ContextOwner;
    }

    private static void CountSite(
        InitializerStatistics stats,
        BaseObjectCreationExpressionSyntax node,
        bool isImplicit)
    {
        if (isImplicit)
        {
            stats.TargetTypedNewSites++;
            if (node.Initializer == null)
                stats.TargetTypedNewBareSites++;
            else
                stats.TargetTypedNewWithInitializerSites++;
        }

        if (node.Initializer == null)
            return;

        switch (node.Initializer.Kind())
        {
            case SyntaxKind.ObjectInitializerExpression:
                stats.ObjectInitializerSites++;
                break;
            case SyntaxKind.CollectionInitializerExpression:
                stats.CollectionInitializerSites++;
                break;
        }
    }

    private static void Report(
        RuleAnalysisContext context, string code, string message, SyntaxNode node)
    {
        var strict = (context.Options.Initializers ?? InitializerOptions.Default).Strict;
        context.Statistics.Initializers.AddSkip(code);
        context.Report(
            InitializerDiagnostics.ResolveSeverity(code, strict), code, message, node);
    }
}
