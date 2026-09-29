using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.PatternDesugar;

/// <summary>
/// Deterministic per-file name scope: prefix → prefix_0, prefix_1, … Same input
/// always produces the same names. Shared by C03 and C37 components so merged
/// worktrees never collide.
/// </summary>
internal sealed class NameScope
{
    private readonly Dictionary<string, int> _counters = new(StringComparer.Ordinal);

    public string Next(string prefix)
    {
        var n = _counters.TryGetValue(prefix, out var current) ? current : 0;
        _counters[prefix] = n + 1;
        return $"{prefix}_{n}";
    }
}

/// <summary>Collects lowering plans and explicit (PD010/PD011) node-level failures.</summary>
internal sealed class PatternDesugarPlanBuilder
{
    public PatternDesugarPlanBuilder(PatternDesugarOptions options, string? filePath)
    {
        Options = options;
        FilePath = filePath;
    }

    public PatternDesugarOptions Options { get; }
    public string? FilePath { get; }
    public NameScope Names { get; } = new();
    public Dictionary<SwitchStatementSyntax, SwitchLoweringPlan> SwitchPlans { get; } = new();
    public Dictionary<TryStatementSyntax, CatchLoweringPlan> CatchPlans { get; } = new();
    public List<PatternDesugarDiagnostic> Diagnostics { get; } = new();
    public int UnsupportedSkipped { get; private set; }

    /// <summary>True once any node reported PD010/PD011 — the file's residual recheck is then skipped.</summary>
    public bool HasExplicitNodeErrors { get; private set; }

    public void ReportUnsupported(string code, string message, SyntaxNode node)
    {
        UnsupportedSkipped++;
        HasExplicitNodeErrors = true;
        var severity = code == PatternDesugarCodes.GotoCaseInLoweredSwitch || Options.Strict
            ? PatternDesugarSeverity.Error
            : PatternDesugarSeverity.Warning;
        var span = node.GetLocation().GetLineSpan();
        var where = span.IsValid
            ? $" ({FilePath ?? span.Path}:{span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1})"
            : "";
        Diagnostics.Add(new PatternDesugarDiagnostic(code, severity, message + where, FilePath));
    }

    /// <summary>Counter hook for PD010s reported through the reduction context.</summary>
    public void NoteReductionNodeError()
    {
        UnsupportedSkipped++;
        HasExplicitNodeErrors = true;
    }
}

/// <summary>
/// One statement-side component (C03: switch statements + try/catch; C37 adds
/// is-expressions and switch-expression arms with the same interface).
/// </summary>
internal interface IPatternDesugarComponent
{
    void Analyze(CompilationUnitSyntax root, SemanticModel? model, PatternDesugarPlanBuilder builder);

    /// <summary>null = this component does not handle the node.</summary>
    SyntaxNode? Rewrite(SyntaxNode node, PatternDesugarRewriteContext ctx);
}

internal sealed class SwitchLoweringPlan
{
    public required SwitchStatementSyntax Switch { get; init; }
    public required bool HasPatternLabels { get; init; }
    public required string? GoverningTempName { get; init; }
    public required ITypeSymbol? GoverningType { get; init; }
    public required bool NeedsEndLabel { get; init; }
    public required string EndLabelName { get; init; }
    public required LoweringScope Scope { get; init; }
    public required IReadOnlyList<SectionPlan> Sections { get; init; }

    internal sealed class SectionPlan
    {
        public required bool IsDefault { get; init; }
        public required IReadOnlyList<LabelPlan> Labels { get; init; }
        public required IReadOnlyList<StatementSyntax> Statements { get; init; }
    }

    internal sealed class LabelPlan
    {
        /// <summary>Pattern source (CasePatternSwitchLabelSyntax); null otherwise.</summary>
        public required PatternSyntax? Pattern { get; init; }

        /// <summary>Bare constant label value (`case 1:`); null otherwise. Never
        /// wrapped in a synthesized ConstantPatternSyntax — that would detach the
        /// node from the semantic model.</summary>
        public required ExpressionSyntax? ConstantValue { get; init; }

        /// <summary>Guard preserved verbatim (D7 gate applies); null when absent.</summary>
        public required ExpressionSyntax? When { get; init; }
    }
}

internal sealed class CatchLoweringPlan
{
    public enum LowerStrategy
    {
        /// <summary>Single catch clause with a filter (concrete type): keep the clause, wrap the body.</summary>
        PreserveBody,

        /// <summary>Multiple clauses / type-parameter catch: merge into one Exception catch chain.</summary>
        MergeChain,
    }

    public required TryStatementSyntax Try { get; init; }
    public required LowerStrategy Strategy { get; init; }
    public required IReadOnlyList<CatchClausePlan> Clauses { get; init; }
    public required string? SynthExceptionName { get; init; }
    public required LoweringScope Scope { get; init; }

    internal sealed class CatchClausePlan
    {
        public required CatchClauseSyntax Clause { get; init; }
        public required string? OriginalVariableName { get; init; }
        public required bool IsTypeParameter { get; init; }
    }
}

/// <summary>Shared analysis rules used by components and the rewriter.</summary>
internal static class PatternDesugarAnalysis
{
    /// <summary>
    /// A break belongs to the switch when the switch is its nearest enclosing
    /// switch/loop along the original ancestor chain (D12).
    /// </summary>
    public static bool BreakBelongsToSwitch(BreakStatementSyntax brk, SwitchStatementSyntax owner)
    {
        foreach (var ancestor in brk.Ancestors())
        {
            if (ReferenceEquals(ancestor, owner)) return true;
            if (ancestor is SwitchStatementSyntax or WhileStatementSyntax or DoStatementSyntax
                or ForStatementSyntax or ForEachStatementSyntax)
                return false;
        }
        return false;
    }

    /// <summary>
    /// Governing types Java's switch statement cannot handle: the D4 non-integer
    /// set (StatementTransformer L33-38) plus object/dynamic/type parameters
    /// (Java switch accepts only primitives, strings and enums).
    /// </summary>
    public static bool IsNonIntegerGoverning(ITypeSymbol? type)
        => type?.SpecialType is SpecialType.System_Boolean
           or SpecialType.System_Int64
           or SpecialType.System_UInt64
           or SpecialType.System_Single
           or SpecialType.System_Double
           or SpecialType.System_Decimal
           or SpecialType.System_Object
           || type is { TypeKind: TypeKind.Dynamic or TypeKind.TypeParameter };

    /// <summary>GetTypeInfo that tolerates error types and detached nodes.</summary>
    public static ITypeSymbol? SafeTypeInfo(SemanticModel? model, ExpressionSyntax expression)
    {
        if (model == null)
            return null;
        try
        {
            var type = model.GetTypeInfo(expression).Type;
            return type is IErrorTypeSymbol ? null : type;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static readonly HashSet<string> PrimitiveKeywords = new(StringComparer.Ordinal)
    {
        "bool", "byte", "char", "decimal", "double", "float", "int", "long",
        "sbyte", "short", "uint", "ulong", "ushort",
    };

    /// <summary>
    /// Binding shape rules. Value types and type parameters use the self-compare
    /// form (`(v = (T)x) == v`; `!= null` does not compile for them). Type
    /// parameters additionally cast through object (`(T)(object)x`), because a
    /// direct cast from the governing type to an unconstrained T is CS0030.
    /// Without a semantic model, fall back to the primitive-keyword heuristic.
    /// </summary>
    public static BindingInfo DescribeBinding(TypeSyntax type, SemanticModel? model)
    {
        if (model != null)
        {
            var symbol = model.GetTypeInfo(type).Type;
            var isTypeParameter = symbol?.TypeKind == TypeKind.TypeParameter;
            var selfCompare = symbol == null || symbol.IsValueType || isTypeParameter;
            return new BindingInfo(type, selfCompare, isTypeParameter);
        }
        var primitive = type is PredefinedTypeSyntax predefined
            && PrimitiveKeywords.Contains(predefined.Keyword.ValueText);
        return new BindingInfo(type, primitive, CastThroughObject: false);
    }

    /// <summary>
    /// Collects every designation a label pattern introduces (recursively):
    /// typed designations become hoisted assign-in-condition bindings, untyped
    /// ones (var / type-less recursive) become eager `var x = gov;` prologues.
    /// </summary>
    public static void CollectBindings(
        PatternSyntax pattern,
        LoweringScope scope,
        SemanticModel? model,
        Func<string, string> newName)
    {
        switch (pattern)
        {
            case DeclarationPatternSyntax declaration:
                if (declaration.Designation is SingleVariableDesignationSyntax single)
                {
                    scope.AssignBindings.TryAdd(
                        single.Identifier.ValueText,
                        DescribeBinding(declaration.Type, model));
                }
                break;

            case VarPatternSyntax varPattern:
                if (varPattern.Designation is SingleVariableDesignationSyntax varSingle
                    && !scope.EagerBindings.Contains(varSingle.Identifier.ValueText))
                {
                    scope.EagerBindings.Add(varSingle.Identifier.ValueText);
                }
                break;

            case RecursivePatternSyntax recursive:
                if (recursive.Type != null)
                {
                    if (recursive.Designation is SingleVariableDesignationSyntax recursiveSingle)
                    {
                        scope.AssignBindings.TryAdd(
                            recursiveSingle.Identifier.ValueText,
                            DescribeBinding(recursive.Type, model));
                    }
                    else if (recursive.PropertyPatternClause != null)
                    {
                        // Member access needs a name; register a deterministic synthetic.
                        var info = DescribeBinding(recursive.Type, model);
                        scope.RegisterSynthetic(
                            recursive, recursive.Type, info.SelfCompare, newName);
                    }
                }
                else if (recursive.Designation is SingleVariableDesignationSyntax typeless
                         && !scope.EagerBindings.Contains(typeless.Identifier.ValueText))
                {
                    scope.EagerBindings.Add(typeless.Identifier.ValueText);
                }

                if (recursive.PropertyPatternClause != null)
                {
                    foreach (var sub in recursive.PropertyPatternClause.Subpatterns)
                        CollectBindings(sub.Pattern, scope, model, newName);
                }
                break;

            case BinaryPatternSyntax binary:
                CollectBindings(binary.Left, scope, model, newName);
                CollectBindings(binary.Right, scope, model, newName);
                break;

            case UnaryPatternSyntax unary:
                CollectBindings(unary.Pattern, scope, model, newName);
                break;

            case ParenthesizedPatternSyntax parenthesized:
                CollectBindings(parenthesized.Pattern, scope, model, newName);
                break;
        }
    }
}

internal sealed class SwitchStatementComponent : IPatternDesugarComponent
{
    public void Analyze(CompilationUnitSyntax root, SemanticModel? model, PatternDesugarPlanBuilder builder)
    {
        foreach (var sw in root.DescendantNodes().OfType<SwitchStatementSyntax>())
            AnalyzeSwitch(sw, model, builder);
    }

    public SyntaxNode? Rewrite(SyntaxNode node, PatternDesugarRewriteContext ctx)
    {
        if (node is not SwitchStatementSyntax sw || !ctx.SwitchPlans.TryGetValue(sw, out var plan))
            return null;

        var rewriter = ctx.Rewriter;
        ExpressionSyntax Governing() => plan.GoverningTempName != null
            ? SyntaxFactory.IdentifierName(plan.GoverningTempName)
            : plan.Switch.Expression.WithoutTrivia();

        var statements = new List<StatementSyntax>();
        if (plan.GoverningTempName != null)
        {
            statements.Add(SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.IdentifierName("var"),
                    SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(
                        SyntaxFactory.Identifier(plan.GoverningTempName),
                        null,
                        SyntaxFactory.EqualsValueClause(plan.Switch.Expression.WithoutTrivia()))))));
        }

        // Hoisted bindings: plain declarations for assign-in-condition bindings,
        // eager assignments for var / type-less designations (revised D11).
        foreach (var (name, binding) in plan.Scope.AssignBindings)
        {
            statements.Add(SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(
                    binding.Type.WithoutTrivia(),
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(name))))));
        }
        foreach (var eager in plan.Scope.EagerBindings)
        {
            statements.Add(SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.IdentifierName("var"),
                    SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(
                        SyntaxFactory.Identifier(eager),
                        null,
                        SyntaxFactory.EqualsValueClause(Governing()))))));
        }

        // Build the else-if chain bottom-up so default (last section) becomes else.
        StatementSyntax? chain = null;
        for (var i = plan.Sections.Count - 1; i >= 0; i--)
        {
            var section = plan.Sections[i];
            var visited = section.Statements
                .Select(s => (StatementSyntax)rewriter.Visit(s)!)
                .ToList();

            // A section-final `break` needs no jump: the if/else-if chain is
            // exclusive, so a branch that runs out of statements has already left
            // the switch. Eliding it keeps the output free of goto/label unless a
            // mid-section break actually requires one (D12).
            if (visited.Count > 0
                && section.Statements[^1] is BreakStatementSyntax trailingBreak
                && PatternDesugarAnalysis.BreakBelongsToSwitch(trailingBreak, plan.Switch))
            {
                visited.RemoveAt(visited.Count - 1);
            }

            var body = SyntaxFactory.Block(visited);

            if (section.IsDefault)
            {
                chain = body;
                continue;
            }

            var labelConditions = new List<ExpressionSyntax>();
            foreach (var label in section.Labels)
            {
                if (label.Pattern == null && label.ConstantValue == null) continue;
                var reductionContext = ctx.ReductionContext(plan.Scope);
                var reduction = label.ConstantValue != null
                    ? PatternReducer.ReduceConstant(
                        label.ConstantValue, Governing(), plan.GoverningType, reductionContext)
                    : PatternReducer.Reduce(
                        label.Pattern!, Governing(), plan.GoverningType, reductionContext);
                if (!reduction.Supported)
                    return null; // analyzer pre-checked every label; defensive only
                var labelCondition = reduction.Condition!;
                if (label.When != null)
                {
                    // Guard joins its own label, parenthesized as a whole (D3/D7).
                    labelCondition = SyntaxFactory.BinaryExpression(SyntaxKind.LogicalAndExpression,
                        labelCondition,
                        SyntaxFactory.ParenthesizedExpression(label.When.WithoutTrivia()));
                }
                labelConditions.Add(labelCondition);
            }

            ExpressionSyntax condition;
            if (labelConditions.Count == 1)
            {
                condition = labelConditions[0];
            }
            else
            {
                // Multi-label section: parenthesized disjunction (D3).
                condition = SyntaxFactory.ParenthesizedExpression(labelConditions.Aggregate(
                    (l, r) => (ExpressionSyntax)SyntaxFactory.BinaryExpression(
                        SyntaxKind.LogicalOrExpression, l, r)));
            }

            chain = SyntaxFactory.IfStatement(condition, body,
                chain != null ? SyntaxFactory.ElseClause(chain) : null);
        }

        if (chain == null)
            return null;

        statements.Add(chain);

        if (plan.NeedsEndLabel)
        {
            statements.Add(SyntaxFactory.LabeledStatement(
                SyntaxFactory.Identifier(plan.EndLabelName), SyntaxFactory.EmptyStatement()));
            ctx.Statistics.EndLabelsIntroduced++;
        }

        if (plan.HasPatternLabels)
            ctx.Statistics.PatternSwitchesRewritten++;
        else
            ctx.Statistics.NonIntegerSwitchesRewritten++;
        if (plan.GoverningTempName != null)
            ctx.Statistics.GoverningTempsIntroduced++;

        var block = SyntaxFactory.Block(statements);
        return block
            .WithOpenBraceToken(block.OpenBraceToken.WithLeadingTrivia(sw.GetLeadingTrivia()))
            .WithCloseBraceToken(block.CloseBraceToken.WithTrailingTrivia(sw.GetTrailingTrivia()));
    }

    private static void AnalyzeSwitch(SwitchStatementSyntax sw, SemanticModel? model, PatternDesugarPlanBuilder builder)
    {
        var sections = sw.Sections.ToList();
        if (sections.Count == 0)
            return;

        var hasPatternLabels = sections.Any(s => s.Labels.Any(l => l is CasePatternSwitchLabelSyntax));

        // A default label in a non-final section would need an unreachable else-if —
        // degenerate (CS0162) input; refuse explicitly instead of dropping code.
        for (var i = 0; i < sections.Count - 1; i++)
        {
            if (sections[i].Labels.Any(l => l is DefaultSwitchLabelSyntax))
            {
                builder.ReportUnsupported(PatternDesugarCodes.UnsupportedPattern,
                    "PD010: default label in a non-final switch section is not lowered", sw);
                return;
            }
        }

        ITypeSymbol? governingType = null;
        if (model != null)
        {
            governingType = model.GetTypeInfo(sw.Expression).Type;
            if (governingType is IErrorTypeSymbol)
                governingType = null;
        }

        var isNonIntegerGoverning = !hasPatternLabels
            && governingType != null
            && PatternDesugarAnalysis.IsNonIntegerGoverning(governingType);
        if (!hasPatternLabels && !isNonIntegerGoverning)
            return; // int/char/string/enum switch with plain labels: converter handles 1:1 (D15)

        // goto case/default cannot survive if-chain lowering (D13).
        if (sw.Sections.SelectMany(s => s.Statements)
            .SelectMany(st => st.DescendantNodesAndSelf())
            .OfType<GotoStatementSyntax>()
            .Any(g => g.Kind() is SyntaxKind.GotoCaseStatement or SyntaxKind.GotoDefaultStatement))
        {
            builder.ReportUnsupported(PatternDesugarCodes.GotoCaseInLoweredSwitch,
                "PD011: goto case/default inside a pattern switch must stay on a real switch (E14 territory)", sw);
            return;
        }

        // D7: guards are preserved verbatim; any is-pattern inside a guard would
        // reach the converter's expression-side fallbacks — refuse until C37.
        foreach (var label in sections.SelectMany(s => s.Labels).OfType<CasePatternSwitchLabelSyntax>())
        {
            if (label.WhenClause != null
                && label.WhenClause.Condition.DescendantNodesAndSelf().Any(n => n is IsPatternExpressionSyntax))
            {
                builder.ReportUnsupported(PatternDesugarCodes.UnsupportedPattern,
                    "PD010: is-pattern inside a when guard is reduced by C37", label.WhenClause);
                return;
            }
        }

        // D2: hoist the governing expression unless it is a simple local/parameter read.
        string? governingTemp = null;
        var inlineable = sw.Expression is IdentifierNameSyntax identifier
            && (model == null
                || model.GetSymbolInfo(identifier).Symbol is ILocalSymbol or IParameterSymbol);
        if (!inlineable)
            governingTemp = builder.Names.Next("__cs2jGov");

        var subject = (ExpressionSyntax)(governingTemp != null
            ? SyntaxFactory.IdentifierName(governingTemp)
            : sw.Expression.WithoutTrivia());

        var scope = new LoweringScope();

        // Trial reduction: any unsupported label refuses the whole switch (D6)
        // before any rewriting happens. Bindings are collected first so the trial
        // sees the same scope the rewrite will see.
        var trialContext = new PatternReductionContext(
            model, builder.Options, builder.Diagnostics, builder.FilePath, builder.Names.Next, scope, builder);
        foreach (var section in sections)
        {
            foreach (var label in section.Labels)
            {
                switch (label)
                {
                    case CasePatternSwitchLabelSyntax patternLabel:
                        PatternDesugarAnalysis.CollectBindings(
                            patternLabel.Pattern, scope, model, builder.Names.Next);
                        if (!PatternReducer.Reduce(
                                patternLabel.Pattern, subject, governingType, trialContext).Supported)
                        {
                            return;
                        }
                        break;
                    case CaseSwitchLabelSyntax constantLabel:
                        if (!PatternReducer.ReduceConstant(
                                constantLabel.Value, subject, governingType, trialContext).Supported)
                        {
                            return;
                        }
                        break;
                }
            }
        }

        // Only breaks that are NOT a section's final statement need a jump target.
        var needsEndLabel = sections.Any(s =>
        {
            foreach (var statement in s.Statements)
            {
                var belongs = statement.DescendantNodesAndSelf()
                    .OfType<BreakStatementSyntax>()
                    .Any(b => PatternDesugarAnalysis.BreakBelongsToSwitch(b, sw));
                if (!belongs)
                    continue;
                if (!ReferenceEquals(statement, s.Statements[^1]))
                    return true;
                if (statement is not BreakStatementSyntax)
                    return true; // e.g. `if (x) break;` as the final statement
            }
            return false;
        });

        builder.SwitchPlans[sw] = new SwitchLoweringPlan
        {
            Switch = sw,
            HasPatternLabels = hasPatternLabels,
            GoverningTempName = governingTemp,
            GoverningType = governingType,
            NeedsEndLabel = needsEndLabel,
            EndLabelName = builder.Names.Next("__cs2jSwitchEnd"),
            Scope = scope,
            Sections = sections.Select(s => new SwitchLoweringPlan.SectionPlan
            {
                IsDefault = s.Labels.Any(l => l is DefaultSwitchLabelSyntax),
                Labels = s.Labels.Select(l => new SwitchLoweringPlan.LabelPlan
                {
                    Pattern = (l as CasePatternSwitchLabelSyntax)?.Pattern,
                    ConstantValue = (l as CaseSwitchLabelSyntax)?.Value,
                    When = (l as CasePatternSwitchLabelSyntax)?.WhenClause?.Condition,
                }).ToList(),
                Statements = s.Statements.ToList(),
            }).ToList(),
        };
    }
}

internal sealed class CatchClauseComponent : IPatternDesugarComponent
{
    public void Analyze(CompilationUnitSyntax root, SemanticModel? model, PatternDesugarPlanBuilder builder)
    {
        foreach (var tryNode in root.DescendantNodes().OfType<TryStatementSyntax>())
            AnalyzeTry(tryNode, model, builder);
    }

    public SyntaxNode? Rewrite(SyntaxNode node, PatternDesugarRewriteContext ctx)
    {
        if (node is not TryStatementSyntax tryNode || !ctx.CatchPlans.TryGetValue(tryNode, out var plan))
            return null;

        var rewriter = ctx.Rewriter;

        if (plan.Strategy == CatchLoweringPlan.LowerStrategy.PreserveBody)
        {
            // D8 strategy ①: keep the catch clause (type + variable name), wrap the
            // body in `if (filter) { … } else throw;` — the else is a bare statement.
            var clause = plan.Clauses[0].Clause;
            var guarded = SyntaxFactory.IfStatement(
                clause.Filter!.FilterExpression.WithoutTrivia(),
                SyntaxFactory.Block(clause.Block.Statements
                    .Select(s => (StatementSyntax)rewriter.Visit(s)!).ToList()),
                SyntaxFactory.ElseClause(SyntaxFactory.ThrowStatement()));
            ctx.Statistics.CatchFiltersRewritten++;
            return tryNode.WithCatches(SyntaxFactory.List(new[]
            {
                clause.WithFilter(null).WithBlock(SyntaxFactory.Block(guarded)),
            }));
        }

        // D8 strategy ②: merged chain over one synthesized Exception parameter.
        // Each branch's original variable name is a HOISTED declaration assigned in
        // the condition (`Exception ex;` + `__cs2jEx_0 is T && ((ex = (T)__cs2jEx_0)
        // != null)`) — pattern variables cannot be redeclared across if/else-if.
        var subject = SyntaxFactory.IdentifierName(plan.SynthExceptionName!);
        var reductionContext = ctx.ReductionContext(plan.Scope);

        // Hoisted per-name declarations (one per distinct original variable name).
        var hoists = plan.Scope.AssignBindings
            .Select(kv => (StatementSyntax)SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(
                    kv.Value.Type.WithoutTrivia(),
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(kv.Key))))))
            .ToList();

        StatementSyntax chain = SyntaxFactory.ThrowStatement();
        var filtersLowered = 0;
        var typeParamsLowered = 0;
        for (var i = plan.Clauses.Count - 1; i >= 0; i--)
        {
            var clausePlan = plan.Clauses[i];
            var clause = clausePlan.Clause;
            var body = SyntaxFactory.Block(clause.Block.Statements
                .Select(s => (StatementSyntax)rewriter.Visit(s)!).ToList());

            if (clause.Declaration == null)
            {
                chain = body; // typeless catch: terminal else without a rethrow
                continue;
            }

            ExpressionSyntax condition;
            if (clausePlan.OriginalVariableName != null
                && plan.Scope.AssignBindings.TryGetValue(
                    clausePlan.OriginalVariableName, out var binding))
            {
                condition = PatternReducer.AssignBindingCondition(
                    subject, binding.Type, clausePlan.OriginalVariableName,
                    binding.SelfCompare, binding.CastThroughObject);
            }
            else
            {
                condition = PatternReducer.IsTypeTest(subject, clause.Declaration.Type);
            }

            if (clause.Filter != null)
            {
                condition = SyntaxFactory.BinaryExpression(SyntaxKind.LogicalAndExpression,
                    condition,
                    SyntaxFactory.ParenthesizedExpression(clause.Filter.FilterExpression.WithoutTrivia()));
                filtersLowered++;
            }
            if (clausePlan.IsTypeParameter)
                typeParamsLowered++;

            chain = SyntaxFactory.IfStatement(condition, body, SyntaxFactory.ElseClause(chain));
        }

        ctx.Statistics.CatchFiltersRewritten += filtersLowered;
        ctx.Statistics.CatchTypeParamRewritten += typeParamsLowered;

        var mergedCatch = SyntaxFactory.CatchClause(
            SyntaxFactory.CatchDeclaration(
                SyntaxFactory.ParseTypeName("System.Exception"),
                SyntaxFactory.Identifier(plan.SynthExceptionName!)),
            null,
            SyntaxFactory.Block(hoists.Concat(new[] { chain }).ToList()));
        return tryNode.WithCatches(SyntaxFactory.List(new[] { mergedCatch }));
    }

    private static void AnalyzeTry(TryStatementSyntax tryNode, SemanticModel? model, PatternDesugarPlanBuilder builder)
    {
        var clauses = tryNode.Catches.ToList();
        if (clauses.Count == 0)
            return;

        var hasFilter = clauses.Any(c => c.Filter != null);
        var typeParamClauses = model == null
            ? new List<CatchClauseSyntax>()
            : clauses
                .Where(c => c.Declaration != null
                            && model.GetTypeInfo(c.Declaration.Type).Type is ITypeParameterSymbol)
                .ToList();

        if (!hasFilter && typeParamClauses.Count == 0)
            return; // plain concrete catches: converter handles 1:1

        // D7: filters are preserved verbatim — refuse is-patterns inside them until C37.
        foreach (var filter in clauses.Where(c => c.Filter != null).Select(c => c.Filter))
        {
            if (filter!.FilterExpression.DescendantNodesAndSelf().Any(n => n is IsPatternExpressionSyntax))
            {
                builder.ReportUnsupported(PatternDesugarCodes.UnsupportedPattern,
                    "PD010: is-pattern inside a catch filter is reduced by C37", filter);
                return;
            }
        }

        if (clauses.Count == 1 && typeParamClauses.Count == 0)
        {
            builder.CatchPlans[tryNode] = new CatchLoweringPlan
            {
                Try = tryNode,
                Strategy = CatchLoweringPlan.LowerStrategy.PreserveBody,
                SynthExceptionName = null,
                Scope = new LoweringScope(),
                Clauses = new[]
                {
                    new CatchLoweringPlan.CatchClausePlan
                    {
                        Clause = clauses[0],
                        OriginalVariableName = OriginalVariableName(clauses[0]),
                        IsTypeParameter = false,
                    },
                },
            };
            return;
        }

        // Hoisted bindings for every named clause variable (same name across
        // clauses shares one declaration — condition assignment re-binds it).
        var scope = new LoweringScope();
        foreach (var clause in clauses)
        {
            var name = OriginalVariableName(clause);
            if (name != null && clause.Declaration != null)
            {
                scope.AssignBindings.TryAdd(name,
                    PatternDesugarAnalysis.DescribeBinding(clause.Declaration.Type, model));
            }
        }

        builder.CatchPlans[tryNode] = new CatchLoweringPlan
        {
            Try = tryNode,
            Strategy = CatchLoweringPlan.LowerStrategy.MergeChain,
            Clauses = clauses.Select(c => new CatchLoweringPlan.CatchClausePlan
            {
                Clause = c,
                OriginalVariableName = OriginalVariableName(c),
                IsTypeParameter = typeParamClauses.Contains(c),
            }).ToList(),
            SynthExceptionName = builder.Names.Next("__cs2jEx"),
            Scope = scope,
        };
    }

    private static string? OriginalVariableName(CatchClauseSyntax clause)
        => clause.Declaration == null
           || clause.Declaration.Identifier.IsKind(SyntaxKind.None)
           || string.IsNullOrEmpty(clause.Declaration.Identifier.ValueText)
            ? null
            : clause.Declaration.Identifier.ValueText;
}
