using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using CSharpToJava.Core.ExpressionDesugar;

namespace CSharpToJava.Core.ExpressionDesugar.Initializers;

/// <summary>
/// C24 semantic site inventory and binding classification (design §2 decisions
/// 2/7/8/10/11/12), M1 main path.
///
/// Every <see cref="ObjectCreationExpressionSyntax"/> with an initializer and
/// every <see cref="ImplicitObjectCreationExpressionSyntax"/> is classified
/// from the full-compile <c>IOperation</c> tree: object-initializer items are
/// property/field writes, collection items are instance single-argument
/// <c>Add</c> invocations, bare <c>new()</c> is explicitized from its
/// ConvertedType. M2 shapes (nested create/member-init, dictionary index,
/// complex/multi-arg/extension/params Add) and M2-only contexts are preserved
/// unchanged without a diagnostic. Diagnostics CS2J0531/0533/0535 are
/// Error-class under strict options (the family entry then keeps the whole file
/// atomically unchanged) and Warnings otherwise.
/// </summary>
internal static class InitializerBindingAnalyzer
{
    /// <summary>
    /// Classifies every trigger site and returns the per-site plans in source
    /// order. Temp names for statement-lifted sequence sites are pre-allocated
    /// here (deterministic, post-order inner-before-outer) so the rewriter only
    /// consumes them.
    /// </summary>
    public static List<InitializerPlan> Analyze(RuleAnalysisContext context)
    {
        var plans = new List<InitializerPlan>();
        var model = context.SemanticModel;
        if (model == null)
            return plans;

        foreach (var node in context.Root.DescendantNodes())
        {
            switch (node)
            {
                case ImplicitObjectCreationExpressionSyntax implicitCreation:
                    plans.Add(AnalyzeSite(
                        context, model, implicitCreation, isImplicit: true));
                    break;

                case ObjectCreationExpressionSyntax { Initializer: not null } creation:
                    plans.Add(AnalyzeSite(
                        context, model, creation, isImplicit: false));
                    break;
            }
        }

        AllocateTempNames(context, plans);
        return plans;
    }

    private static InitializerPlan AnalyzeSite(
        RuleAnalysisContext context,
        SemanticModel model,
        BaseObjectCreationExpressionSyntax node,
        bool isImplicit)
    {
        var stats = context.Statistics.Initializers;
        var depth = node.Ancestors().OfType<InitializerExpressionSyntax>().Count();
        var plan = new InitializerPlan
        {
            Node = node,
            IsTargetTyped = isImplicit,
            Depth = depth,
        };

        CountSite(stats, node, isImplicit);

        // C36 coexistence (see InitializerRewriter: C36 keys its plans by
        // original node reference): a site nested inside a collection
        // expression is kept byte-identical and stays out of the rewriter map,
        // so the enclosing C36 site is never rebuilt. Disjoint sites in the
        // same file still rewrite normally.
        if (InitializerSyntaxFacts.IsInsideCollectionExpression(node))
        {
            stats.AddSkip(UnsupportedSentinel);
            stats.CoexistenceDeferredSites++;
            return Skip(plan, InitializerOutcome.DeferredForCoexistence);
        }

        // Shared family criterion: expression-tree lambdas are skipped with the
        // CS2J0542 Info (statement lifting would escape the lambda).
        if (ExpressionDesugarSyntaxFacts.IsInExpressionTree(model, node))
        {
            Report(context, InitializerDiagnosticCodes.CS2J0542,
                InitializerDiagnostics.ExpressionTreeMessage, node);
            return Skip(plan, InitializerOutcome.SkippedDiagnostic,
                InitializerDiagnosticCodes.CS2J0542);
        }

        // ---- Target type (explicit type or target-typed ConvertedType) ----
        ITypeSymbol? createdType;
        TargetTypeInfo? targetType = null;
        if (isImplicit)
        {
            var converted = model.GetTypeInfo(node).ConvertedType;
            if (converted == null || converted.TypeKind == TypeKind.Error)
            {
                Report(context, InitializerDiagnosticCodes.CS2J0535,
                    InitializerDiagnostics.TargetTypeMissingMessage, node);
                return Skip(plan, InitializerOutcome.SkippedDiagnostic,
                    InitializerDiagnosticCodes.CS2J0535);
            }

            targetType = new TargetTypeInfo
            {
                ConvertedType = converted,
                UnderlyingType = converted,
                IsNullable = converted.NullableAnnotation == NullableAnnotation.Annotated,
            };
            createdType = converted;
        }
        else
        {
            createdType = model.GetTypeInfo(node).Type;
        }

        plan = CloneWith(plan, createdType, targetType);

        // ---- Bare target-typed new() (IN-9): zero-temp explicitization ----
        if (node.Initializer == null)
        {
            Report(context, InitializerDiagnosticCodes.CS2J0536,
                string.Format(
                    InitializerDiagnostics.TargetTypedNewExpandedMessage,
                    createdType?.ToDisplayString() ?? "?", string.Empty),
                node);
            plan.Outcome = InitializerOutcome.TargetTypedNewExplicit;
            return plan;
        }

        // ---- Context classification (M1 supports the main-path positions) ----
        var decision = InitializerContextLowering.Classify(node);
        plan = CloneWith(plan, createdType, targetType, decision.Kind);
        if (!decision.Liftable)
            return Skip(plan, InitializerOutcome.UnsupportedContext);

        // ---- Item-level semantic classification ----
        var operation = model.GetOperation(node) as IObjectCreationOperation;
        var items = (operation?.Initializer as IObjectOrCollectionInitializerOperation)
            ?.Initializers.ToList();

        var isObjectInitializer = node.Initializer.IsKind(
            SyntaxKind.ObjectInitializerExpression);
        string? skipCode = null;

        // Empty initializers carry no member semantics. An empty `{ }` always
        // parses as an object initializer, even on a collection type (C36 emits
        // `new List<T>(){}` for its statement-lifted temps), so the created type
        // decides: a plain object folds to `new C(args)`, an enumerable
        // collection has nothing to Add and is preserved (keeps C36's output
        // byte-stable on a second run).
        if (items is { Count: 0 })
        {
            if (isObjectInitializer
                && !IsEnumerableCollectionType(model, plan.CreatedType))
            {
                plan.Outcome = InitializerOutcome.ZeroTempExplicit;
                Report(context, InitializerDiagnosticCodes.CS2J0530,
                    string.Format(InitializerDiagnostics.ObjectExpandedMessage, 0, 0, 0, 0),
                    node);
                return plan;
            }

            return Skip(plan, InitializerOutcome.UnsupportedContext);
        }

        if (isObjectInitializer)
        {
            skipCode = ClassifyObjectItems(context, stats, plan, items);
        }
        else if (node.Initializer.IsKind(SyntaxKind.CollectionInitializerExpression))
        {
            skipCode = ClassifyCollectionItems(context, stats, plan, items);
        }
        else
        {
            // Complex-element / unknown initializer kind: M2.
            return Skip(plan, InitializerOutcome.UnsupportedContext);
        }

        if (skipCode != null)
            return Skip(plan, InitializerOutcome.SkippedDiagnostic, skipCode);

        plan.Outcome = isObjectInitializer
            ? InitializerOutcome.ObjectSequence
            : InitializerOutcome.CollectionSequence;
        stats.AddContextLowered(plan.Context);

        if (isObjectInitializer)
        {
            var propertyCount = plan.Members.OfType<PropertySetPlan>().Count();
            var fieldCount = plan.Members.OfType<FieldSetPlan>().Count();
            var indexerCount = plan.Members.OfType<IndexSetPlan>().Count();
            Report(context, InitializerDiagnosticCodes.CS2J0530,
                string.Format(
                    InitializerDiagnostics.ObjectExpandedMessage,
                    plan.Members.Count, propertyCount, fieldCount, indexerCount),
                node);
        }
        else
        {
            var adds = plan.Members.OfType<CollectionAddPlan>().ToList();
            var fluentCount = adds.Count(
                a => a.AddMethod is { ReturnsVoid: false });
            Report(context, InitializerDiagnosticCodes.CS2J0532,
                string.Format(
                    InitializerDiagnostics.CollectionExpandedMessage,
                    adds.Count, adds.Count, 0, 0, 0, fluentCount, 0),
                node);
        }

        return plan;
    }

    /// <summary>
    /// Classifies object-initializer items. Returns a skip code when a member is
    /// not writable (CS2J0531); returns a non-null sentinel for M2-only item
    /// shapes, or null when every item is a writable property/field write.
    /// </summary>
    private static string? ClassifyObjectItems(
        RuleAnalysisContext context,
        InitializerStatistics stats,
        InitializerPlan plan,
        IReadOnlyList<IOperation>? items)
    {
        if (items == null)
            return InitializerDiagnosticCodes.CS2J0531;

        var order = 0;
        foreach (var item in items)
        {
            if (item is not ISimpleAssignmentOperation assignment)
                return UnsupportedSentinel; // nested member init P = { … } (M2)

            switch (assignment.Target)
            {
                case IPropertyReferenceOperation property when property.Property.IsIndexer:
                    stats.DictionaryIndexInitializerSites++;
                    stats.MemberAssignments++;
                    stats.MemberIndexerAssignments++;
                    return UnsupportedSentinel; // [k] = v (M2)

                case IPropertyReferenceOperation property:
                {
                    if (property.Property.SetMethod is not { IsInitOnly: false }
                        || property.Property.IsStatic)
                    {
                        Report(context, InitializerDiagnosticCodes.CS2J0531,
                            string.Format(
                                InitializerDiagnostics.MemberNotWritableMessage,
                                property.Property.Name,
                                property.Property.ContainingType?.ToDisplayString() ?? "?",
                                property.Property.SetMethod is { IsInitOnly: true }
                                    ? "init-only accessor"
                                    : property.Property.SetMethod == null
                                        ? "no set accessor"
                                        : "static member"),
                            plan.Node);
                        return InitializerDiagnosticCodes.CS2J0531;
                    }

                    if (IsNestedCreation(assignment.Value))
                        return UnsupportedSentinel; // P = new T { … } (M2)

                    stats.MemberAssignments++;
                    stats.MemberPropertyAssignments++;
                    plan.Members.Add(new PropertySetPlan
                    {
                        Syntax = (ExpressionSyntax)item.Syntax!,
                        SourceOrder = order++,
                        Property = property.Property,
                        Value = (ExpressionSyntax)assignment.Value.Syntax!,
                    });
                    break;
                }

                case IFieldReferenceOperation field:
                {
                    if (field.Field.IsReadOnly || field.Field.IsConst || field.Field.IsStatic)
                    {
                        Report(context, InitializerDiagnosticCodes.CS2J0531,
                            string.Format(
                                InitializerDiagnostics.MemberNotWritableMessage,
                                field.Field.Name,
                                field.Field.ContainingType?.ToDisplayString() ?? "?",
                                "read-only field (baseline CS0191)"),
                            plan.Node);
                        return InitializerDiagnosticCodes.CS2J0531;
                    }

                    if (IsNestedCreation(assignment.Value))
                        return UnsupportedSentinel;

                    stats.MemberAssignments++;
                    stats.MemberFieldAssignments++;
                    plan.Members.Add(new FieldSetPlan
                    {
                        Syntax = (ExpressionSyntax)item.Syntax!,
                        SourceOrder = order++,
                        Field = field.Field,
                        Value = (ExpressionSyntax)assignment.Value.Syntax!,
                    });
                    break;
                }

                default:
                    Report(context, InitializerDiagnosticCodes.CS2J0531,
                        string.Format(
                            InitializerDiagnostics.MemberNotWritableMessage,
                            item.Syntax?.ToString() ?? "?", "?",
                            "unresolved member"),
                        plan.Node);
                    return InitializerDiagnosticCodes.CS2J0531;
            }
        }

        return null;
    }

    /// <summary>
    /// Classifies collection-initializer items. M1 handles instance
    /// single-argument <c>Add</c> only (fluent non-void returns included: the
    /// value is discarded); multi-arg/extension/params/resolvable-fallback
    /// shapes are M2 and preserved without a diagnostic. Unresolvable Add
    /// bindings report CS2J0533.
    /// </summary>
    private static string? ClassifyCollectionItems(
        RuleAnalysisContext context,
        InitializerStatistics stats,
        InitializerPlan plan,
        IReadOnlyList<IOperation>? items)
    {
        if (items == null)
            return InitializerDiagnosticCodes.CS2J0533;

        var order = 0;
        foreach (var item in items)
        {
            if (item is IInvalidOperation
                || item is not IInvocationOperation invocation)
            {
                Report(context, InitializerDiagnosticCodes.CS2J0533,
                    string.Format(
                        InitializerDiagnostics.AddUnresolvedMessage,
                        plan.CreatedType?.ToDisplayString() ?? "?"),
                    plan.Node);
                return InitializerDiagnosticCodes.CS2J0533;
            }

            stats.AddCalls++;
            var target = invocation.TargetMethod;

            var hasParamsParameter = target.Parameters is { Length: > 0 } parameters
                && parameters[parameters.Length - 1].IsParams;

            if (invocation.Arguments.Length != 1
                || target.IsExtensionMethod
                || hasParamsParameter)
            {
                // M2: multi-arg / extension / params. Counted, preserved.
                if (target.IsExtensionMethod)
                    stats.AddExtension++;
                else if (hasParamsParameter)
                    stats.AddParams++;
                else
                    stats.AddMulti++;
                return UnsupportedSentinel;
            }

            stats.AddSingle++;
            if (!target.ReturnsVoid)
                stats.AddFluent++;

            plan.Members.Add(new CollectionAddPlan
            {
                Syntax = (ExpressionSyntax)item.Syntax!,
                SourceOrder = order++,
                AddMethod = target,
                AddKind = AddKind.Single,
                Arguments = new[] { (ExpressionSyntax)invocation.Arguments[0].Value.Syntax! },
            });
        }

        return null;
    }

    private static bool IsNestedCreation(IOperation value)
        => value is IObjectCreationOperation { Initializer: not null };

    /// <summary>
    /// True when the created type is a collection (implements the non-generic
    /// <see cref="System.Collections.IEnumerable"/>), i.e. an empty <c>{ }</c>
    /// is an empty collection initializer rather than an object initializer.
    /// </summary>
    private static bool IsEnumerableCollectionType(SemanticModel model, ITypeSymbol? type)
    {
        if (type == null)
            return false;
        if (type.TypeKind == TypeKind.Array)
            return true;

        var enumerable = model.Compilation
            .GetTypeByMetadataName("System.Collections.IEnumerable");
        return enumerable != null
               && type.AllInterfaces.Any(
                   i => SymbolEqualityComparer.Default.Equals(i, enumerable));
    }

    // ---- Statistics / plan helpers -----------------------------------

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

    private static void AllocateTempNames(
        RuleAnalysisContext context,
        List<InitializerPlan> plans)
    {
        var sequencePlans = plans
            .Where(p => p.Outcome is InitializerOutcome.ObjectSequence
                or InitializerOutcome.CollectionSequence)
            .ToList();
        if (sequencePlans.Count == 0)
            return;

        // Post-order: an enclosing site is ordered after every site it
        // contains; disjoint sites follow source order (mirrors C36 CE-8).
        sequencePlans.Sort(ComparePostOrder);

        var allocator = new ExpressionDesugarTempNameAllocator(context.Root);
        var order = 0;
        foreach (var plan in sequencePlans)
        {
            plan.TempName = allocator.Allocate();
            plan.LiftOrder = order++;
        }
    }

    private static int ComparePostOrder(InitializerPlan left, InitializerPlan right)
    {
        if (ReferenceEquals(left, right))
            return 0;

        if (left.Node.Span.Contains(right.Node.Span))
            return 1;
        if (right.Node.Span.Contains(left.Node.Span))
            return -1;

        return left.Node.SpanStart.CompareTo(right.Node.SpanStart);
    }

    private static InitializerPlan Skip(
        InitializerPlan plan,
        InitializerOutcome outcome,
        string? code = null)
    {
        plan.Outcome = outcome;
        if (code != null)
            plan.SkipCode = code;
        return plan;
    }

    private static InitializerPlan CloneWith(
        InitializerPlan plan,
        ITypeSymbol? createdType,
        TargetTypeInfo? targetType,
        InitializerContextKind context = InitializerContextKind.Unknown)
    {
        var clone = new InitializerPlan
        {
            Node = plan.Node,
            IsTargetTyped = plan.IsTargetTyped,
            Depth = plan.Depth,
            CreatedType = createdType,
            TargetType = targetType,
            Context = context,
            Outcome = plan.Outcome,
        };
        clone.Members.AddRange(plan.Members);
        return clone;
    }

    private static void Report(
        RuleAnalysisContext context, string code, string message, SyntaxNode node)
    {
        var strict = (context.Options.Initializers ?? InitializerOptions.Default).Strict;
        context.Statistics.Initializers.AddSkip(code);
        context.Report(
            InitializerDiagnostics.ResolveSeverity(code, strict), code, message, node);
    }

    /// <summary>
    /// Sentinel skip code for M2-only shapes: preserved unchanged with an
    /// explained annotation and no diagnostic (the M2 milestone owns them).
    /// </summary>
    public const string UnsupportedSentinel = "__m2__";
}
