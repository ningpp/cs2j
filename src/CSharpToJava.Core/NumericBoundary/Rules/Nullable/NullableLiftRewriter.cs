using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Preprocessing;
using NB = CSharpToJava.Core.NumericBoundary.NumericBoundarySyntaxFacts;
using NBOp = Microsoft.CodeAnalysis.Operations;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.NumericBoundary.Rules.Nullable;

/// <summary>
/// One hoisted site: the marker carried by the placeholder expression in the
/// post-Pass1 tree, the bottom-up visit order (inner sites first — the
/// execution order of the hoisted initializers), the anchor in the ORIGINAL
/// tree, the <c>var</c> declarations Pass2 splices in, and (NL-10 postfix)
/// the write statements spliced after the declarations. When
/// <see cref="ConsumesAnchor"/> is set the site IS a standalone postfix
/// expression statement: Pass2 replaces the whole anchor with the spliced
/// statements instead of keeping the placeholder statement.
/// </summary>
internal sealed record PendingNullableLift(
    SyntaxAnnotation Marker,
    int Order,
    StatementSyntax OriginalAnchor,
    List<LocalDeclarationStatementSyntax> Declarations,
    List<StatementSyntax>? WriteStatements = null,
    bool ConsumesAnchor = false);

/// <summary>
/// C22 M1 Pass 1 (design §4.3-4.5 / decisions 2-8): bottom-up syntax rewriter
/// over the ORIGINAL bound tree. Each lifted built-in <c>Nullable&lt;T&gt;</c>
/// site in an M1-owned domain is replaced by its explicit guard
/// (<see cref="NullableLiftExpansion"/>); every unstable operand is read
/// exactly once into a <c>__cs2j_n_*</c> temp whose declaration is hoisted
/// before the site's anchor statement by <see cref="NullableLiftLifter"/>.
/// <para>
/// Error routing (whole file rolls back via NB-12): CS2J5354 expression tree,
/// CS2J5352 dynamic, CS2J5353 user-defined lifted, CS2J5356 C41 special
/// numeric, CS2J5355 unstable operand in an un-hoistable context. Enum-domain
/// lifted sites (NL-9), increments/compounds (NL-10/11), GOV (NL-6) and
/// conversions (NL-7/8) are M2 and pass through untouched in M1.
/// </para>
/// </summary>
internal sealed class NullableLiftRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly NullableLiftStatistics _stats;
    private readonly List<NumericBoundaryDiagnostic> _diagnostics;
    private readonly NullableLiftAnalyzer _analyzer;
    private NullableLiftSyntaxFacts.TempNameAllocator? _names;
    private readonly List<PendingNullableLift> _pending = new();
    private int _sequence;

    private HoistState? _currentHoist;

    public NullableLiftRewriter(
        SemanticModel model,
        NumericBoundaryOptions options,
        NullableLiftStatistics stats,
        List<NumericBoundaryDiagnostic> diagnostics,
        NullableLiftAnalyzer analyzer)
    {
        _model = model;
        _stats = stats;
        _diagnostics = diagnostics;
        _analyzer = analyzer;
    }

    /// <summary>
    /// Temporary-name allocator, created on first use: reserving every identifier
    /// already in the tree is a full token pass that only hoisting sites need.
    /// </summary>
    private NullableLiftSyntaxFacts.TempNameAllocator Names =>
        _names ??= new NullableLiftSyntaxFacts.TempNameAllocator(_model.SyntaxTree.GetRoot());

    public bool HasErrorDiagnostic { get; private set; }

    public bool MadeChanges { get; private set; }

    public IReadOnlyList<PendingNullableLift> Pending => _pending;

    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        var visited = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;

        // Raw `x == null` / `null != x` over a T? receiver binds NON-lifted.
        var nullEquality = _analyzer.ClassifyNullLiteralEquality(node);
        if (nullEquality != null)
        {
            if (_analyzer.IsWithinExpressionTree(node))
            {
                ReportSiteError(node,
                    PreprocessDiagnosticCodes.NullableLiftExpressionTree,
                    NullableLiftDiagnostics.ReasonExpressionTree,
                    NullableLiftDiagnostics.ExpressionTree(NullableLiftSyntaxFacts.SiteText(node)));
                return visited;
            }

            var rewritten = RewriteNullLiteralEquality(node, visited, nullEquality);
            return rewritten ?? visited;
        }

        if (_model.GetOperation(node) is not NBOp.IBinaryOperation operation)
        {
            return visited;
        }

        // Dynamic dispatch with nullable involvement (decision 22): catch even
        // when the binary is not a statically-bound lifted call.
        if (!operation.IsLifted)
        {
            if (IsDynamicNullableSite(operation, node))
            {
                ReportSiteError(node,
                    PreprocessDiagnosticCodes.NullableLiftDynamic,
                    NullableLiftDiagnostics.ReasonDynamic,
                    NullableLiftDiagnostics.Dynamic(NullableLiftSyntaxFacts.SiteText(node)));
            }

            return visited;
        }

        // Error gate order: expression tree → dynamic → user-defined → C41.
        if (_analyzer.IsWithinExpressionTree(node))
        {
            ReportSiteError(node,
                PreprocessDiagnosticCodes.NullableLiftExpressionTree,
                NullableLiftDiagnostics.ReasonExpressionTree,
                NullableLiftDiagnostics.ExpressionTree(NullableLiftSyntaxFacts.SiteText(node)));
            return visited;
        }

        if (NullableLiftAnalyzer.IsDynamicOperation(operation))
        {
            ReportSiteError(node,
                PreprocessDiagnosticCodes.NullableLiftDynamic,
                NullableLiftDiagnostics.ReasonDynamic,
                NullableLiftDiagnostics.Dynamic(NullableLiftSyntaxFacts.SiteText(node)));
            return visited;
        }

        var classification = _analyzer.ClassifyBinary(node);
        if (classification == null)
        {
            return visited;
        }

        // C41 special numerics (Int128/nint/…) bind user-defined operator
        // methods but are routed to their own CS2J5356 class ahead of the
        // generic user-defined gate (decision 20).
        if (classification.Domain == NullableValueDomain.SpecialNumeric)
        {
            ReportSiteError(node,
                PreprocessDiagnosticCodes.NullableLiftSpecialNumeric,
                NullableLiftDiagnostics.ReasonSpecialNumeric,
                NullableLiftDiagnostics.SpecialNumeric(NullableLiftSyntaxFacts.SiteText(node)));
            return visited;
        }

        if (classification.Flavor == LiftedBinaryFlavor.UserDefined)
        {
            ReportSiteError(node,
                PreprocessDiagnosticCodes.NullableLiftUserDefinedOperator,
                NullableLiftDiagnostics.ReasonUserDefinedOperator,
                NullableLiftDiagnostics.UserDefinedOperator(NullableLiftSyntaxFacts.SiteText(node)));
            return visited;
        }

        // NL-9 (decision 12): enum-domain built-in lifted bit operations and
        // equality get the E04-shaped backstop here (E04 only handles stable
        // operands; unstable ones reach this rule). Custom-struct bindings
        // (Other) stay untouched.
        if (classification.Domain is NullableValueDomain.Other
            or NullableValueDomain.NotNullable)
        {
            return visited;
        }

        if (classification.Domain == NullableValueDomain.Enum)
        {
            return RewriteEnumBinary(node, visited, classification, operation) ?? visited;
        }

        return RewriteOwnedBinary(node, visited, classification, operation) ?? visited;
    }

    public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
    {
        var visited = (PrefixUnaryExpressionSyntax)base.VisitPrefixUnaryExpression(node)!;

        // NL-10: ++x / --x bind as IIncrementOrDecrementOperation (NOT an
        // IUnaryOperation); the IUnary path below handles - + ~ ! only.
        if (_model.GetOperation(node) is NBOp.IIncrementOrDecrementOperation { IsPostfix: false } prefixIncrement)
        {
            var targetClassification = ClassifyGatedRmwSite(node);
            if (targetClassification == null)
            {
                return visited;
            }

            return RewriteIncrement(
                node, visited, visited.Operand, prefixIncrement, targetClassification, isPostfix: false)
                ?? visited;
        }

        if (_model.GetOperation(node) is not NBOp.IUnaryOperation operation)
        {
            return visited;
        }

        if (!operation.IsLifted)
        {
            if (IsDynamicNullableSite(operation, node))
            {
                ReportSiteError(node,
                    PreprocessDiagnosticCodes.NullableLiftDynamic,
                    NullableLiftDiagnostics.ReasonDynamic,
                    NullableLiftDiagnostics.Dynamic(NullableLiftSyntaxFacts.SiteText(node)));
            }

            return visited;
        }

        if (_analyzer.IsWithinExpressionTree(node))
        {
            ReportSiteError(node,
                PreprocessDiagnosticCodes.NullableLiftExpressionTree,
                NullableLiftDiagnostics.ReasonExpressionTree,
                NullableLiftDiagnostics.ExpressionTree(NullableLiftSyntaxFacts.SiteText(node)));
            return visited;
        }

        if (NullableLiftAnalyzer.IsDynamicOperation(operation))
        {
            ReportSiteError(node,
                PreprocessDiagnosticCodes.NullableLiftDynamic,
                NullableLiftDiagnostics.ReasonDynamic,
                NullableLiftDiagnostics.Dynamic(NullableLiftSyntaxFacts.SiteText(node)));
            return visited;
        }

        var classification = _analyzer.ClassifyUnary(node);
        if (classification == null)
        {
            return visited;
        }

        if (classification.Domain == NullableValueDomain.SpecialNumeric)
        {
            ReportSiteError(node,
                PreprocessDiagnosticCodes.NullableLiftSpecialNumeric,
                NullableLiftDiagnostics.ReasonSpecialNumeric,
                NullableLiftDiagnostics.SpecialNumeric(NullableLiftSyntaxFacts.SiteText(node)));
            return visited;
        }

        if (classification.Flavor == LiftedUnaryFlavor.UserDefined)
        {
            ReportSiteError(node,
                PreprocessDiagnosticCodes.NullableLiftUserDefinedOperator,
                NullableLiftDiagnostics.ReasonUserDefinedOperator,
                NullableLiftDiagnostics.UserDefinedOperator(NullableLiftSyntaxFacts.SiteText(node)));
            return visited;
        }

        // NL-9 enum `~` backstop (decision 12); Other custom-struct unary
        // operators stay untouched.
        if (classification.Domain is NullableValueDomain.Other
            or NullableValueDomain.NotNullable)
        {
            return visited;
        }

        if (classification.Domain == NullableValueDomain.Enum)
        {
            return RewriteEnumUnary(node, visited, operation) ?? visited;
        }

        _currentHoist = null;
        var underlying = ResolveUnderlying(operation.Operand?.Type, operation.Type);
        if (underlying == null)
        {
            return visited;
        }

        var (operandOk, operandAccess) = PrepareOperand(
            node, originalOperand: node.Operand, visitedOperand: visited.Operand,
            operandOperation: operation.Operand,
            sideNullable: classification.OperandNullable, isLeft: true);
        if (!operandOk)
        {
            return visited;
        }

        ExpressionSyntax rewritten = classification.Flavor == LiftedUnaryFlavor.BoolNot
            ? NullableLiftExpansion.LiftedBoolNot(operandAccess)
            : NullableLiftExpansion.LiftedUnaryGuard(
                node.Kind(), node.OperatorToken.Kind(), operandAccess, underlying);

        if (classification.Flavor == LiftedUnaryFlavor.BoolNot)
        {
            _stats.LiftedBoolLogicNotSites++;
        }
        else
        {
            _stats.LiftedUnarySites++;
        }

        return FinalizeSite(node, rewritten, _currentHoist);
    }

    public override SyntaxNode? VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
    {
        var visited = (PostfixUnaryExpressionSyntax)base.VisitPostfixUnaryExpression(node)!;

        if (_model.GetOperation(node) is not NBOp.IIncrementOrDecrementOperation { IsPostfix: true } postfixIncrement)
        {
            return visited;
        }

        var targetClassification = ClassifyGatedRmwSite(node);
        if (targetClassification == null)
        {
            return visited;
        }

        return RewriteIncrement(
            node, visited, visited.Operand, postfixIncrement, targetClassification, isPostfix: true)
            ?? visited;
    }

    // ── NL-11 lifted compound assignment (decision 16) ──

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        var visited = (AssignmentExpressionSyntax)base.VisitAssignmentExpression(node)!;

        if (_model.GetOperation(node) is not NBOp.ICompoundAssignmentOperation compound)
        {
            return visited;
        }

        var targetClassification = ClassifyGatedRmwSite(node);
        if (targetClassification == null)
        {
            return visited;
        }

        return RewriteCompound(node, visited, compound, targetClassification) ?? visited;
    }

    // ── NL-10/11 site rendering ──

    /// <summary>
    /// Shared error gate for lifted <c>++/--</c> and compound-assignment
    /// sites (decisions 15-17/20-22). Returns the classification for a
    /// C22-owned site, null for pass-through; site errors are reported here
    /// (the whole file then rolls back via NB-12). M3: the expression-tree
    /// and dynamic gates run BEFORE the lifted short-circuit because a
    /// nullable-involved dynamic compound (<c>int? a; a += d;</c>) binds a
    /// non-lifted <see cref="NBOp.ICompoundAssignmentOperation"/>.
    /// </summary>
    private LiftedTargetClassification? ClassifyGatedRmwSite(ExpressionSyntax node)
    {
        var bound = _model.GetOperation(node);

        // Expression-tree / dynamic dispatch must be caught even when the
        // operation binds non-lifted (decision 22): gate order mirrors the
        // binary/unary visitors — expression tree, then dynamic.
        if (_analyzer.IsWithinExpressionTree(node))
        {
            ReportSiteError(node,
                PreprocessDiagnosticCodes.NullableLiftExpressionTree,
                NullableLiftDiagnostics.ReasonExpressionTree,
                NullableLiftDiagnostics.ExpressionTree(NullableLiftSyntaxFacts.SiteText(node)));
            return null;
        }

        if (bound is { } && IsDynamicNullableRmwSite(bound, node))
        {
            ReportSiteError(node,
                PreprocessDiagnosticCodes.NullableLiftDynamic,
                NullableLiftDiagnostics.ReasonDynamic,
                NullableLiftDiagnostics.Dynamic(NullableLiftSyntaxFacts.SiteText(node)));
            return null;
        }

        var classification = _analyzer.ClassifyLiftedTarget(node);
        if (classification is not { IsLifted: true })
        {
            return null;
        }

        if (classification.Domain == NullableValueDomain.SpecialNumeric)
        {
            ReportSiteError(node,
                PreprocessDiagnosticCodes.NullableLiftSpecialNumeric,
                NullableLiftDiagnostics.ReasonSpecialNumeric,
                NullableLiftDiagnostics.SpecialNumeric(NullableLiftSyntaxFacts.SiteText(node)));
            return null;
        }

        if (classification.IsUserDefined)
        {
            ReportSiteError(node,
                PreprocessDiagnosticCodes.NullableLiftUserDefinedOperator,
                NullableLiftDiagnostics.ReasonUserDefinedOperator,
                NullableLiftDiagnostics.UserDefinedOperator(NullableLiftSyntaxFacts.SiteText(node)));
            return null;
        }

        // Decision 17 (M3 closure): get/set properties (incl. static),
        // positional get/set indexers and true array elements are owned via
        // the single-evaluation target-temp seam; events, conditional ?.
        // targets, ref-returning accessors, qualified fields and named/ref
        // indexer arguments stay CS2J5355.
        if (classification.TargetShape == NumericBoundaryAnalyzer.TargetShape.Complex
            && classification.ComplexKind == null)
        {
            ReportSiteError(node,
                PreprocessDiagnosticCodes.NullableLiftComplexTarget,
                NullableLiftDiagnostics.ReasonUnliftableContext,
                NullableLiftDiagnostics.UnliftableContext(NullableLiftSyntaxFacts.SiteText(node)));
            return null;
        }

        // Custom-struct (non-enum) built-in shapes are not reachable for
        // lifted RMW; stay defensive and pass them through untouched.
        if (classification.Domain is NullableValueDomain.Other
            or NullableValueDomain.NotNullable)
        {
            return null;
        }

        return classification;
    }

    /// <summary>
    /// Decision 22 at an RMW site: dynamic dispatch with Nullable&lt;T&gt;
    /// involvement. A nullable-involved compound binds non-lifted when either
    /// side is <c>dynamic</c> (<c>a += d</c>/<c>d += a</c>); pure
    /// non-nullable dynamic RMW belongs to other families and stays
    /// pass-through here.
    /// </summary>
    private bool IsDynamicNullableRmwSite(IOperation operation, ExpressionSyntax node)
    {
        if (!NullableLiftAnalyzer.IsDynamicOperation(operation))
        {
            return false;
        }

        static bool IsNullableType(ITypeSymbol? type) => NullableLiftAnalyzer.IsNullableType(type);

        switch (operation)
        {
            case NBOp.ICompoundAssignmentOperation compound:
                return IsNullableType(compound.Target?.Type)
                    || IsNullableType(compound.Value?.Type)
                    || node is AssignmentExpressionSyntax assignment
                        && (IsNullableType(_model.GetTypeInfo(assignment.Left).Type)
                            || IsNullableType(_model.GetTypeInfo(assignment.Right).Type));
            case NBOp.IIncrementOrDecrementOperation increment:
                return IsNullableType(increment.Target?.Type)
                    || IsNullableType(_model.GetTypeInfo(
                        node is PrefixUnaryExpressionSyntax pre ? pre.Operand
                        : node is PostfixUnaryExpressionSyntax post ? post.Operand
                        : node).Type);
            default:
                return false;
        }
    }

    /// <summary>
    /// NL-10 lifted <c>++/--</c> on a simple target (decision 15):
    /// <list type="bullet">
    /// <item>postfix always reads the old value once into
    /// <c>__cs2j_n_oN</c>, splices the write before the anchor, and the
    /// site expression becomes the old-value temp;</item>
    /// <item>prefix renders an assignment expression
    /// <c>x = x.HasValue ? (T?)(x.Value + 1) : (T?)null</c> — parenthesized
    /// in value position, bare as a standalone statement;</item>
    /// <item>enum targets add the E04 cast family <c>(E?)(E)((U)s.Value + 1)</c>.</item>
    /// </list>
    /// </summary>
    private ExpressionSyntax? RewriteIncrement(
        ExpressionSyntax original,
        ExpressionSyntax visited,
        ExpressionSyntax visitedTarget,
        NBOp.IIncrementOrDecrementOperation increment,
        LiftedTargetClassification classification,
        bool isPostfix)
    {
        var underlying = ResolveUnderlying(increment.Target?.Type, increment.Type);
        if (underlying == null)
        {
            return null;
        }

        _currentHoist = null;
        var standalone = original.Parent is ExpressionStatementSyntax expressionStatement
            && expressionStatement.Expression == original;
        var isDecrement = original.Kind() is SyntaxKind.PreDecrementExpression
            or SyntaxKind.PostDecrementExpression;

        if (classification.ComplexKind is { } complexKind)
        {
            return RewriteComplexIncrement(
                original, visitedTarget, classification, complexKind,
                underlying, isPostfix, isDecrement, standalone);
        }

        string? oldName = null;
        OperandAccess sourceAccess;
        HoistState? hoist = null;

        if (isPostfix)
        {
            // The old value is read exactly once even in a standalone
            // statement: one uniform shape keeps target evaluation honest
            // (fields included) and matches decision 15's spelling.
            hoist = EnsureHoist(original);
            if (hoist == null)
            {
                return null;
            }

            oldName = Names.Old(hoist.SiteNumber);
            AddTempDeclaration(
                hoist, VarDeclaration(oldName, visitedTarget), NullableLiftSyntaxFacts.RoleOld);
            sourceAccess = OperandAccess.Direct(IdentifierName(oldName), isNullablePosition: true);
        }
        else
        {
            sourceAccess = OperandAccess.Direct(visitedTarget, isNullablePosition: true);
        }

        var guard = classification.Domain == NullableValueDomain.Enum
                && underlying is INamedTypeSymbol enumType
            ? NullableLiftExpansion.EnumIncrementGuard(sourceAccess, isDecrement, enumType)
            : NullableLiftExpansion.IncrementGuard(sourceAccess, isDecrement, underlying);

        var assignment = AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            visitedTarget,
            Token(SyntaxKind.EqualsToken),
            guard);

        if (isPostfix)
        {
            hoist!.WriteStatements.Add(ExpressionStatement(assignment)
                .WithAdditionalAnnotations(
                    NB.RewriteAnnotation,
                    new SyntaxAnnotation(NullableLiftSyntaxFacts.RewriteAnnotationKind)));
            hoist.ConsumesAnchor = standalone;
            _stats.LiftedIncrementPostfixSites++;

            // The postfix expression's value is the OLD nullable value.
            var placeholder = IdentifierName(oldName!);
            return FinalizeSite(original, placeholder, hoist);
        }

        _stats.LiftedIncrementPrefixSites++;
        ExpressionSyntax rewritten = standalone
            ? assignment
            : ParenthesizedExpression(assignment);
        return FinalizeSite(original, rewritten, hoist);
    }

    /// <summary>
    /// NL-11 lifted compound assignment on a simple target (decision 16):
    /// <c>x = (x.HasValue &amp;&amp; __y.HasValue ? (T?)(x.Value op __y.Value) : (T?)null)</c>.
    /// An unstable RHS is read once via the ordinary b-role hoist; the simple
    /// target's repeated reads are legal. <c>bool? &amp;= |=</c> reuse the
    /// three-valued guards, <c>^=</c> is plain null propagation and
    /// <c>E?</c> bit compounds reuse the NL-9 backstop.
    /// </summary>
    private ExpressionSyntax? RewriteCompound(
        AssignmentExpressionSyntax original,
        AssignmentExpressionSyntax visited,
        NBOp.ICompoundAssignmentOperation compound,
        LiftedTargetClassification classification)
    {
        if (!TryGetCompoundOperators(original.Kind(), out var binaryKind, out var operatorKind))
        {
            return null;
        }

        var underlying = ResolveUnderlying(compound.Target?.Type, compound.Type);
        if (underlying == null)
        {
            return null;
        }

        _currentHoist = null;

        var standalone = original.Parent is ExpressionStatementSyntax expressionStatement
            && expressionStatement.Expression == original;

        if (classification.ComplexKind is { } complexKind)
        {
            return RewriteComplexCompound(
                original, visited, compound, classification, complexKind,
                underlying, binaryKind, operatorKind, standalone);
        }

        var targetAccess = OperandAccess.Direct(visited.Left, isNullablePosition: true);

        var (rightOk, rightAccess) = PrepareOperand(
            original, original.Right, visited.Right, compound.Value,
            sideNullable: RightSideIsNullable(original, compound), isLeft: false);
        if (!rightOk)
        {
            return null;
        }

        var guard = BuildCompoundGuard(
            binaryKind, operatorKind, targetAccess, rightAccess,
            classification.Domain, underlying);
        if (guard == null)
        {
            return null;
        }

        _stats.LiftedIncrementCompoundSites++;

        var assignment = AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            visited.Left,
            Token(SyntaxKind.EqualsToken),
            guard);

        ExpressionSyntax rewritten = standalone
            ? assignment
            : ParenthesizedExpression(assignment);
        return FinalizeSite(original, rewritten, _currentHoist);
    }

    /// <summary>
    /// Domain dispatch shared by simple and complex (M3 decision 17) lifted
    /// compound guards.
    /// </summary>
    private ExpressionSyntax? BuildCompoundGuard(
        SyntaxKind binaryKind,
        SyntaxKind operatorKind,
        OperandAccess targetAccess,
        OperandAccess rightAccess,
        NullableValueDomain domain,
        ITypeSymbol underlying)
    {
        switch (domain)
        {
            case NullableValueDomain.Bool when binaryKind == SyntaxKind.BitwiseAndExpression:
                return NullableLiftExpansion.LiftedBoolAnd(targetAccess, rightAccess);
            case NullableValueDomain.Bool when binaryKind == SyntaxKind.BitwiseOrExpression:
                return NullableLiftExpansion.LiftedBoolOr(targetAccess, rightAccess);
            case NullableValueDomain.Bool when binaryKind == SyntaxKind.ExclusiveOrExpression:
                // Decision 8: bool? ^ is plain lifted null propagation.
                return NullableLiftExpansion.LiftedBinaryGuard(
                    SyntaxKind.ExclusiveOrExpression, SyntaxKind.CaretToken,
                    targetAccess, rightAccess, underlying);
            case NullableValueDomain.Enum when underlying is INamedTypeSymbol enumType:
                return NullableLiftExpansion.EnumBackstopBinary(
                    binaryKind, operatorKind, targetAccess, rightAccess, enumType);
            case NullableValueDomain.Integer
                or NullableValueDomain.Unsigned
                or NullableValueDomain.Floating
                or NullableValueDomain.Decimal:
                return NullableLiftExpansion.LiftedBinaryGuard(
                    binaryKind, operatorKind, targetAccess, rightAccess, underlying);
            default:
                return null;
        }
    }

    // ── M3 decision 17: complex RMW targets (property/indexer/array) ──

    /// <summary>
    /// One C22-owned complex target: two fresh access builders — the READ
    /// (getter/element load, emitted once into the <c>t</c> initializer) and
    /// the WRITE LHS (setter/element store of the guard result). Receiver and
    /// index expressions have already been materialized into <c>r</c>/<c>i</c>
    /// temporaries, so every builder call is side-effect free.
    /// </summary>
    private sealed record ComplexTargetAccess(
        Func<ExpressionSyntax> BuildRead,
        Func<ExpressionSyntax> BuildWrite);

    /// <summary>
    /// NL-10 lifted <c>++/--</c> on a complex target (M3 decision 17): the
    /// target is read exactly once into <c>__cs2j_n_t&lt;N&gt;</c>, the guard
    /// runs over the temp, and the getter result is written back through a
    /// freshly built setter access. Postfix yields the temp (the old value)
    /// and splices the write before the anchor; prefix is a parenthesized
    /// assignment expression whose value is the new nullable.
    /// </summary>
    private ExpressionSyntax? RewriteComplexIncrement(
        ExpressionSyntax original,
        ExpressionSyntax visitedTarget,
        LiftedTargetClassification classification,
        RmwComplexKind complexKind,
        ITypeSymbol underlying,
        bool isPostfix,
        bool isDecrement,
        bool standalone)
    {
        var hoist = EnsureHoist(original);
        if (hoist == null)
        {
            return null;
        }

        var access = BuildComplexTargetAccess(
            hoist, classification.Target, visitedTarget, complexKind);
        if (access == null)
        {
            return null;
        }

        var targetName = Names.Target(hoist.SiteNumber);
        AddTempDeclaration(
            hoist, VarDeclaration(targetName, access.BuildRead()),
            NullableLiftSyntaxFacts.RoleTarget);
        var sourceAccess = OperandAccess.Direct(IdentifierName(targetName), isNullablePosition: true);

        var guard = classification.Domain == NullableValueDomain.Enum
                && underlying is INamedTypeSymbol enumType
            ? NullableLiftExpansion.EnumIncrementGuard(sourceAccess, isDecrement, enumType)
            : NullableLiftExpansion.IncrementGuard(sourceAccess, isDecrement, underlying);

        var assignment = AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            access.BuildWrite(),
            Token(SyntaxKind.EqualsToken),
            guard);

        _stats.ComplexRmwSites++;

        if (isPostfix)
        {
            hoist.WriteStatements.Add(ExpressionStatement(assignment)
                .WithAdditionalAnnotations(
                    NB.RewriteAnnotation,
                    new SyntaxAnnotation(NullableLiftSyntaxFacts.RewriteAnnotationKind)));
            hoist.ConsumesAnchor = standalone;
            _stats.LiftedIncrementPostfixSites++;
            return FinalizeSite(original, IdentifierName(targetName), hoist);
        }

        _stats.LiftedIncrementPrefixSites++;
        ExpressionSyntax rewritten = standalone ? assignment : ParenthesizedExpression(assignment);
        return FinalizeSite(original, rewritten, hoist);
    }

    /// <summary>
    /// NL-11 lifted compound assignment on a complex target (M3 decision 17).
    /// Evaluation order matches the C# spec for property/indexer/array
    /// compounds: instance temp (<c>r</c>) → index-argument temps (<c>i</c>) →
    /// RHS temp (<c>b</c>) → single getter read (<c>t</c>) → guard → setter.
    /// </summary>
    private ExpressionSyntax? RewriteComplexCompound(
        AssignmentExpressionSyntax original,
        AssignmentExpressionSyntax visited,
        NBOp.ICompoundAssignmentOperation compound,
        LiftedTargetClassification classification,
        RmwComplexKind complexKind,
        ITypeSymbol underlying,
        SyntaxKind binaryKind,
        SyntaxKind operatorKind,
        bool standalone)
    {
        var hoist = EnsureHoist(original);
        if (hoist == null)
        {
            return null;
        }

        var access = BuildComplexTargetAccess(
            hoist, classification.Target, visited.Left, complexKind);
        if (access == null)
        {
            return null;
        }

        var (rightOk, rightAccess) = PrepareOperand(
            original, original.Right, visited.Right, compound.Value,
            sideNullable: RightSideIsNullable(original, compound), isLeft: false);
        if (!rightOk)
        {
            return null;
        }

        var targetName = Names.Target(hoist.SiteNumber);
        AddTempDeclaration(
            hoist, VarDeclaration(targetName, access.BuildRead()),
            NullableLiftSyntaxFacts.RoleTarget);
        var targetAccess = OperandAccess.Direct(IdentifierName(targetName), isNullablePosition: true);

        var guard = BuildCompoundGuard(
            binaryKind, operatorKind, targetAccess, rightAccess,
            classification.Domain, underlying);
        if (guard == null)
        {
            return null;
        }

        _stats.LiftedIncrementCompoundSites++;
        _stats.ComplexRmwSites++;

        var assignment = AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            access.BuildWrite(),
            Token(SyntaxKind.EqualsToken),
            guard);

        ExpressionSyntax rewritten = standalone ? assignment : ParenthesizedExpression(assignment);
        return FinalizeSite(original, rewritten, hoist);
    }

    /// <summary>
    /// Materialize a complex RMW target (decision 17): effectful receivers and
    /// every indexer/array index argument are read once into <c>r</c>/<c>i</c>
    /// temps in source order; the returned builders emit fresh getter/setter
    /// access expressions over those temps.
    /// </summary>
    private ComplexTargetAccess? BuildComplexTargetAccess(
        HoistState hoist,
        ExpressionSyntax originalTarget,
        ExpressionSyntax visitedTarget,
        RmwComplexKind complexKind)
    {
        originalTarget = NullableLiftSyntaxFacts.UnwrapParens(originalTarget);
        visitedTarget = UnwrapVisitedParens(visitedTarget);

        switch (complexKind)
        {
            case RmwComplexKind.Property:
            {
                if (originalTarget is IdentifierNameSyntax originalId)
                {
                    // Implicit-receiver property (instance or static): the
                    // identifier names the property itself.
                    var name = originalId.Identifier.Text;
                    return new ComplexTargetAccess(
                        () => IdentifierName(name),
                        () => IdentifierName(name));
                }

                if (originalTarget is MemberAccessExpressionSyntax originalMember
                    && visitedTarget is MemberAccessExpressionSyntax visitedMember
                    && originalMember.Kind() == SyntaxKind.SimpleMemberAccessExpression)
                {
                    var property = _model.GetSymbolInfo(originalMember).Symbol as IPropertySymbol;
                    var receiver = BuildReceiverMaterializer(
                        hoist,
                        originalMember.Expression,
                        visitedMember.Expression,
                        property?.IsStatic == true);
                    if (receiver == null)
                    {
                        return null;
                    }

                    var name = property?.Name ?? originalMember.Name.Identifier.Text;
                    return new ComplexTargetAccess(
                        () => BuildPropertyAccess(receiver(), name),
                        () => BuildPropertyAccess(receiver(), name));
                }

                return null;
            }

            case RmwComplexKind.Indexer:
            case RmwComplexKind.ArrayElement:
            {
                if (originalTarget is not ElementAccessExpressionSyntax originalAccess
                    || visitedTarget is not ElementAccessExpressionSyntax visitedAccess)
                {
                    return null;
                }

                var receiver = BuildReceiverMaterializer(
                    hoist,
                    originalAccess.Expression,
                    visitedAccess.Expression,
                    isStatic: false);
                if (receiver == null)
                {
                    return null;
                }

                var indexNames = new List<string>();
                for (var i = 0; i < originalAccess.ArgumentList.Arguments.Count; i++)
                {
                    var visitedArgument = visitedAccess.ArgumentList.Arguments[i].Expression;
                    var indexName = Names.Index(hoist.SiteNumber, i);
                    AddTempDeclaration(
                        hoist, VarDeclaration(indexName, visitedArgument),
                        NullableLiftSyntaxFacts.RoleIndex);
                    indexNames.Add(indexName);
                }

                return new ComplexTargetAccess(
                    () => BuildElementAccess(receiver(), indexNames),
                    () => BuildElementAccess(receiver(), indexNames));
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Receiver materializer for complex targets: static type receivers and
    /// side-effect-free receiver chains are rebuilt per access; an effectful
    /// receiver is hoisted once into <c>__cs2j_n_r&lt;N&gt;</c>.
    /// </summary>
    private Func<ExpressionSyntax>? BuildReceiverMaterializer(
        HoistState hoist,
        ExpressionSyntax originalReceiver,
        ExpressionSyntax visitedReceiver,
        bool isStatic)
    {
        if (isStatic || _analyzer.IsStableReceiver(originalReceiver))
        {
            static ExpressionSyntax CopyTrivial(ExpressionSyntax expression) => expression switch
            {
                ThisExpressionSyntax => ThisExpression(),
                BaseExpressionSyntax => BaseExpression(),
                IdentifierNameSyntax identifier => IdentifierName(identifier.Identifier.Text),
                // Qualified/type/field chains are side-effect free; reuse the
                // bound green node (fresh red wrappers are created per access).
                _ => expression,
            };

            return () => CopyTrivial(visitedReceiver);
        }

        var receiverName = Names.Receiver(hoist.SiteNumber);
        AddTempDeclaration(
            hoist, VarDeclaration(receiverName, visitedReceiver),
            NullableLiftSyntaxFacts.RoleReceiver);
        return () => IdentifierName(receiverName);
    }

    private static MemberAccessExpressionSyntax BuildPropertyAccess(
        ExpressionSyntax receiver, string name) =>
        MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            receiver,
            Token(SyntaxKind.DotToken),
            IdentifierName(name));

    private static ElementAccessExpressionSyntax BuildElementAccess(
        ExpressionSyntax receiver, IReadOnlyList<string> indexNames)
    {
        var arguments = SeparatedList(
            indexNames.Select(name => Argument(IdentifierName(name))));
        return ElementAccessExpression(
            receiver,
            BracketedArgumentList(arguments));
    }

    private static ExpressionSyntax UnwrapVisitedParens(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }

    /// <summary>
    /// Nullable position of a compound's RHS as WRITTEN in source: a null
    /// constant or an expression whose natural type is <c>T?</c> participates
    /// in the two-nullable guard; a bare RHS whose natural type is the
    /// underlying <c>T</c> (e.g. <c>x -= 1</c>) is a non-nullable one-sided
    /// position. The bound <see cref="NBOp.ICompoundAssignmentOperation.Value"/>
    /// already carries the lifted in-conversion (<c>1</c> types as <c>int?</c>),
    /// so the operation type alone cannot distinguish the two.
    /// </summary>
    private bool RightSideIsNullable(
        AssignmentExpressionSyntax original,
        NBOp.ICompoundAssignmentOperation compound)
    {
        if (NullableLiftAnalyzer.IsNullConstant(compound.Value))
        {
            return true;
        }

        return NullableLiftAnalyzer.IsNullableType(_model.GetTypeInfo(original.Right).Type);
    }

    private static bool TryGetCompoundOperators(
        SyntaxKind assignmentKind,
        out SyntaxKind binaryKind,
        out SyntaxKind operatorKind)
    {
        switch (assignmentKind)
        {
            case SyntaxKind.AddAssignmentExpression:
                binaryKind = SyntaxKind.AddExpression;
                operatorKind = SyntaxKind.PlusToken;
                return true;
            case SyntaxKind.SubtractAssignmentExpression:
                binaryKind = SyntaxKind.SubtractExpression;
                operatorKind = SyntaxKind.MinusToken;
                return true;
            case SyntaxKind.MultiplyAssignmentExpression:
                binaryKind = SyntaxKind.MultiplyExpression;
                operatorKind = SyntaxKind.AsteriskToken;
                return true;
            case SyntaxKind.DivideAssignmentExpression:
                binaryKind = SyntaxKind.DivideExpression;
                operatorKind = SyntaxKind.SlashToken;
                return true;
            case SyntaxKind.ModuloAssignmentExpression:
                binaryKind = SyntaxKind.ModuloExpression;
                operatorKind = SyntaxKind.PercentToken;
                return true;
            case SyntaxKind.LeftShiftAssignmentExpression:
                binaryKind = SyntaxKind.LeftShiftExpression;
                operatorKind = SyntaxKind.LessThanLessThanToken;
                return true;
            case SyntaxKind.RightShiftAssignmentExpression:
                binaryKind = SyntaxKind.RightShiftExpression;
                operatorKind = SyntaxKind.GreaterThanGreaterThanToken;
                return true;
            case SyntaxKind.AndAssignmentExpression:
                binaryKind = SyntaxKind.BitwiseAndExpression;
                operatorKind = SyntaxKind.AmpersandToken;
                return true;
            case SyntaxKind.OrAssignmentExpression:
                binaryKind = SyntaxKind.BitwiseOrExpression;
                operatorKind = SyntaxKind.BarToken;
                return true;
            case SyntaxKind.ExclusiveOrAssignmentExpression:
                binaryKind = SyntaxKind.ExclusiveOrExpression;
                operatorKind = SyntaxKind.CaretToken;
                return true;
            default:
                binaryKind = default;
                operatorKind = default;
                return false;
        }
    }

    // ── site rendering ──

    private ExpressionSyntax? RewriteNullLiteralEquality(
        BinaryExpressionSyntax original,
        BinaryExpressionSyntax visited,
        NullLiteralEqualityClassification classification)
    {
        _currentHoist = null;
        var binaryOperation = (_model.GetOperation(original) as NBOp.IBinaryOperation)!;
        var isLeft = classification.NullableSide == original.Left;
        var visitedSide = isLeft ? visited.Left : visited.Right;
        var sideOperation = isLeft ? binaryOperation.LeftOperand : binaryOperation.RightOperand;

        var (ok, access) = PrepareOperand(
            original,
            originalOperand: isLeft ? original.Left : original.Right,
            visitedOperand: visitedSide,
            operandOperation: sideOperation,
            sideNullable: true, isLeft);
        if (!ok)
        {
            return null;
        }

        var rewritten = NullableLiftExpansion.NullLiteralEquality(access, classification.IsNotEquals);
        _stats.LiftedEqualityNullLiteralSites++;
        return FinalizeSite(original, rewritten, _currentHoist);
    }

    private ExpressionSyntax? RewriteOwnedBinary(
        BinaryExpressionSyntax original,
        BinaryExpressionSyntax visited,
        LiftedBinaryClassification classification,
        NBOp.IBinaryOperation operation)
    {
        _currentHoist = null;
        var underlying = ResolveBinaryUnderlying(operation);
        if (underlying == null)
        {
            return null;
        }

        // Anchor-free but single-evaluation-safe shape: the nullable side is
        // an explicit `c ? (T?)a : (T?)null` conditional. The lifted
        // comparison distributes INTO the arms, so the live value is read
        // exactly once without a hoisted temporary:
        //   (c ? (T?)a : (T?)null) == k  →  c ? a == k : false
        //   (... != k)                   →  c ? a != k : true
        //   relational                   →  c ? a <op> k : false
        var distributed = TryDistributeNullableConditional(
            original, visited, classification, operation, underlying);
        if (distributed != null)
        {
            return FinalizeSite(original, distributed, hoist: null);
        }

        var (leftOk, leftAccess) = PrepareOperand(
            original, original.Left, visited.Left, operation.LeftOperand,
            sideNullable: classification.LeftNullable, isLeft: true);
        if (!leftOk)
        {
            return null;
        }

        var (rightOk, rightAccess) = PrepareOperand(
            original, original.Right, visited.Right, operation.RightOperand,
            sideNullable: classification.RightNullable, isLeft: false);
        if (!rightOk)
        {
            return null;
        }

        ExpressionSyntax rewritten;
        switch (classification.Flavor)
        {
            case LiftedBinaryFlavor.Arithmetic:
                rewritten = NullableLiftExpansion.LiftedBinaryGuard(
                    original.Kind(), original.OperatorToken.Kind(),
                    leftAccess, rightAccess, underlying);
                _stats.LiftedBinarySites++;
                break;

            case LiftedBinaryFlavor.Relational:
                rewritten = NullableLiftExpansion.LiftedRelationalGuard(
                    original.Kind(), original.OperatorToken.Kind(),
                    leftAccess, rightAccess, underlying);
                _stats.LiftedRelationalSites++;
                break;

            case LiftedBinaryFlavor.Equality:
                rewritten = BuildLiftedEquality(
                    original, operation, classification, leftAccess, rightAccess, underlying);
                break;

            case LiftedBinaryFlavor.BoolAnd:
                rewritten = NullableLiftExpansion.LiftedBoolAnd(leftAccess, rightAccess);
                _stats.LiftedBoolLogicAndSites++;
                break;

            case LiftedBinaryFlavor.BoolOr:
                rewritten = NullableLiftExpansion.LiftedBoolOr(leftAccess, rightAccess);
                _stats.LiftedBoolLogicOrSites++;
                break;

            case LiftedBinaryFlavor.BoolXor:
                // Decision 8: ^ is plain lifted null propagation.
                rewritten = NullableLiftExpansion.LiftedBinaryGuard(
                    SyntaxKind.ExclusiveOrExpression, SyntaxKind.CaretToken,
                    leftAccess, rightAccess, underlying);
                _stats.LiftedBoolLogicXorSites++;
                break;

            default:
                return null;
        }

        return FinalizeSite(original, rewritten, _currentHoist);
    }

    /// <summary>
    /// Anchor-free distribution for the one-sided lifted comparison whose
    /// nullable operand is an explicit source conditional
    /// <c>c ? (T?)v : (T?)null</c>. The lifted comparison is pushed INTO the
    /// arms, reading the live value exactly once without any hoisted temp:
    /// <code>
    /// (c ? (T?)v : (T?)null) == k  →  (c ? v == k : false)
    /// (c ? (T?)v : (T?)null) != k  →  (c ? v != k : true)
    /// relational (&lt; &gt; &lt;= &gt;=)  →  (c ? v &lt;op&gt; k : false)
    /// </code>
    /// The other operand must be a stable reference: in the distributed form
    /// it is read only on the live branch, the same conditional-read
    /// discipline as the regular one-sided guard
    /// (<c>a.HasValue &amp;&amp; a.Value == b</c> short-circuits b). Unstable
    /// operands fall through to the hoist path instead. The conditional
    /// subtree must be untouched by inner rewrites so its arm conversions can
    /// be verified against this semantic model.
    /// </summary>
    private ExpressionSyntax? TryDistributeNullableConditional(
        BinaryExpressionSyntax original,
        BinaryExpressionSyntax visited,
        LiftedBinaryClassification classification,
        NBOp.IBinaryOperation operation,
        ITypeSymbol underlying)
    {
        if (classification.Flavor is not
            (LiftedBinaryFlavor.Equality or LiftedBinaryFlavor.Relational))
        {
            return null;
        }

        // One-sided only: exactly one T? position (raw `== null` comparisons
        // never reach this method).
        if (classification.LeftNullable == classification.RightNullable)
        {
            return null;
        }

        var nullableIsLeft = classification.LeftNullable;
        var originalNullableSide = nullableIsLeft ? original.Left : original.Right;
        var visitedNullableSide = nullableIsLeft ? visited.Left : visited.Right;
        var originalOther = nullableIsLeft ? original.Right : original.Left;
        var visitedOther = nullableIsLeft ? visited.Right : visited.Left;

        if (visitedNullableSide != originalNullableSide)
        {
            return null;
        }

        if (NullableLiftSyntaxFacts.UnwrapParens(visitedNullableSide)
            is not ConditionalExpressionSyntax conditional)
        {
            return null;
        }

        var liveValue = MatchNullableValueCast(conditional.WhenTrue, underlying);
        if (liveValue == null || !IsNullArm(conditional.WhenFalse))
        {
            return null;
        }

        if (!_analyzer.IsStableReference(originalOther))
        {
            return null;
        }

        var isNotEquals = original.IsKind(SyntaxKind.NotEqualsExpression);
        var nullArm = classification.Flavor == LiftedBinaryFlavor.Equality && isNotEquals
            ? LiteralExpression(SyntaxKind.TrueLiteralExpression)
            : LiteralExpression(SyntaxKind.FalseLiteralExpression);

        var comparison = BinaryExpression(
            original.Kind(),
            liveValue,
            Token(original.OperatorToken.Kind()),
            visitedOther.WithoutTrivia());

        var distributed = ParenthesizedExpression(ConditionalExpression(
                conditional.Condition.WithoutTrivia(),
                comparison,
                nullArm)
            .WithAdditionalAnnotations(NullableLiftSyntaxFacts.GuardAnnotation));

        if (classification.Flavor == LiftedBinaryFlavor.Relational)
        {
            _stats.LiftedRelationalSites++;
        }
        else
        {
            _stats.LiftedEqualityOneSidedSites++;
        }

        return distributed;
    }

    /// <summary>
    /// Matches a <c>(T?)v</c> cast arm: the cast must target
    /// <c>Nullable&lt;<paramref name="underlying"/>&gt;</c> and <c>v</c> must
    /// convert to exactly <paramref name="underlying"/>. Returns the bare
    /// live expression <c>v</c> on success.
    /// </summary>
    private ExpressionSyntax? MatchNullableValueCast(
        ExpressionSyntax arm, ITypeSymbol underlying)
    {
        if (NullableLiftSyntaxFacts.UnwrapParens(arm) is not CastExpressionSyntax cast)
        {
            return null;
        }

        if (!NullableLiftAnalyzer.TryUnwrapNullable(_model.GetTypeInfo(cast).Type, out var castUnderlying)
            || !SymbolEqualityComparer.Default.Equals(castUnderlying, underlying))
        {
            return null;
        }

        var inner = NullableLiftSyntaxFacts.UnwrapParens(cast.Expression);
        var innerConverted = _model.GetTypeInfo(inner).ConvertedType;
        if (!SymbolEqualityComparer.Default.Equals(innerConverted, underlying))
        {
            return null;
        }

        return inner;
    }

    /// <summary>Matches a <c>null</c> or <c>(T?)null</c> conditional arm.</summary>
    private static bool IsNullArm(ExpressionSyntax arm)
    {
        var expression = NullableLiftSyntaxFacts.UnwrapParens(arm);
        if (expression.IsKind(SyntaxKind.NullLiteralExpression))
        {
            return true;
        }

        return expression is CastExpressionSyntax cast
            && NullableLiftSyntaxFacts.UnwrapParens(cast.Expression)
                .IsKind(SyntaxKind.NullLiteralExpression);
    }

    /// <summary>
    /// Shared NL-4 equality rendering for numeric and enum domains: selects
    /// the two-nullable vs one-sided form and fills the matching buckets.
    /// </summary>
    private ExpressionSyntax BuildLiftedEquality(
        BinaryExpressionSyntax original,
        NBOp.IBinaryOperation operation,
        LiftedBinaryClassification classification,
        OperandAccess leftAccess,
        OperandAccess rightAccess,
        ITypeSymbol underlying)
    {
        var leftPositionNullable =
            classification.LeftNullable
            || NullableLiftAnalyzer.IsNullConstant(operation.LeftOperand);
        var rightPositionNullable =
            classification.RightNullable
            || NullableLiftAnalyzer.IsNullConstant(operation.RightOperand);

        if (leftPositionNullable && rightPositionNullable)
        {
            var expression = NullableLiftExpansion.LiftedEqualityBothNullable(
                original.IsKind(SyntaxKind.NotEqualsExpression),
                leftAccess, rightAccess, underlying);
            _stats.LiftedEqualityBothNullableSites++;
            return expression;
        }

        var nullableAccess = leftPositionNullable ? leftAccess : rightAccess;
        var otherAccess = leftPositionNullable ? rightAccess : leftAccess;
        var oneSided = NullableLiftExpansion.LiftedEqualityOneSided(
            original.IsKind(SyntaxKind.NotEqualsExpression),
            nullableAccess, otherAccess, underlying);
        _stats.LiftedEqualityOneSidedSites++;
        return oneSided;
    }

    /// <summary>
    /// NL-9 enum-domain lifted binary: bitwise <c>&amp; | ^</c> renders the
    /// E04-shaped double cast backstop (counted as
    /// <see cref="NullableLiftStatistics.NullableEnumBackstopSites"/>); lifted
    /// enum <c>== !=</c> uses the regular NL-4 equality formula over the enum
    /// underlying. Relational flavors do not bind on enums in compilable input.
    /// </summary>
    private ExpressionSyntax? RewriteEnumBinary(
        BinaryExpressionSyntax original,
        BinaryExpressionSyntax visited,
        LiftedBinaryClassification classification,
        NBOp.IBinaryOperation operation)
    {
        var enumType = ResolveBinaryUnderlying(operation) as INamedTypeSymbol;
        if (enumType is not { TypeKind: TypeKind.Enum })
        {
            return null;
        }

        _currentHoist = null;

        var (leftOk, leftAccess) = PrepareOperand(
            original, original.Left, visited.Left, operation.LeftOperand,
            sideNullable: classification.LeftNullable, isLeft: true);
        if (!leftOk)
        {
            return null;
        }

        var (rightOk, rightAccess) = PrepareOperand(
            original, original.Right, visited.Right, operation.RightOperand,
            sideNullable: classification.RightNullable, isLeft: false);
        if (!rightOk)
        {
            return null;
        }

        ExpressionSyntax rewritten;
        if (classification.Flavor == LiftedBinaryFlavor.Arithmetic)
        {
            rewritten = NullableLiftExpansion.EnumBackstopBinary(
                original.Kind(), original.OperatorToken.Kind(),
                leftAccess, rightAccess, enumType);
            _stats.NullableEnumBackstopSites++;
        }
        else if (classification.Flavor == LiftedBinaryFlavor.Equality)
        {
            rewritten = BuildLiftedEquality(
                original, operation, classification, leftAccess, rightAccess, enumType);
        }
        else
        {
            return null;
        }

        return FinalizeSite(original, rewritten, _currentHoist);
    }

    /// <summary>NL-9 lifted enum unary <c>~</c> backstop.</summary>
    private ExpressionSyntax? RewriteEnumUnary(
        PrefixUnaryExpressionSyntax original,
        PrefixUnaryExpressionSyntax visited,
        NBOp.IUnaryOperation operation)
    {
        var enumType = ResolveUnderlying(operation.Operand?.Type, operation.Type) as INamedTypeSymbol;
        if (enumType is not { TypeKind: TypeKind.Enum }
            || original.Kind() != SyntaxKind.BitwiseNotExpression)
        {
            return null;
        }

        _currentHoist = null;

        var (ok, access) = PrepareOperand(
            original, original.Operand, visited.Operand, operation.Operand,
            sideNullable: true, isLeft: true);
        if (!ok)
        {
            return null;
        }

        var rewritten = NullableLiftExpansion.EnumBackstopNot(access, enumType);
        _stats.NullableEnumBackstopSites++;
        return FinalizeSite(original, rewritten, _currentHoist);
    }

    // ── NL-6 GetValueOrDefault (decision 10) ──

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;

        if (!_analyzer.IsGetValueOrDefault(node, out var hasDefaultArgument))
        {
            return visited;
        }

        if (_model.GetOperation(node) is not NBOp.IInvocationOperation invocation)
        {
            return visited;
        }

        if (_analyzer.IsWithinExpressionTree(node))
        {
            ReportSiteError(node,
                PreprocessDiagnosticCodes.NullableLiftExpressionTree,
                NullableLiftDiagnostics.ReasonExpressionTree,
                NullableLiftDiagnostics.ExpressionTree(NullableLiftSyntaxFacts.SiteText(node)));
            return visited;
        }

        if (NullableLiftAnalyzer.IsDynamicOperation(invocation))
        {
            ReportSiteError(node,
                PreprocessDiagnosticCodes.NullableLiftDynamic,
                NullableLiftDiagnostics.ReasonDynamic,
                NullableLiftDiagnostics.Dynamic(NullableLiftSyntaxFacts.SiteText(node)));
            return visited;
        }

        var underlying = (invocation.TargetMethod.ContainingType as INamedTypeSymbol)
            ?.TypeArguments.FirstOrDefault();
        if (underlying == null || node.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return visited;
        }

        _currentHoist = null;
        var visitedMember = (MemberAccessExpressionSyntax)visited.Expression;
        var (receiverOk, receiverAccess) = PrepareOperand(
            node, memberAccess.Expression, visitedMember.Expression, invocation.Instance,
            sideNullable: true, isLeft: true);
        if (!receiverOk)
        {
            return visited;
        }

        ExpressionSyntax rewritten;
        if (!hasDefaultArgument)
        {
            rewritten = NullableLiftExpansion.GetValueOrDefault(receiverAccess, underlying);
        }
        else
        {
            var originalArgument = node.ArgumentList.Arguments[0].Expression;
            var visitedArgument = visited.ArgumentList.Arguments[0].Expression;
            var argumentOperation = invocation.Arguments.FirstOrDefault().Value;
            var (argumentOk, argumentAccess) = PrepareOperand(
                node, originalArgument, visitedArgument, argumentOperation,
                sideNullable: false, isLeft: false, defaultRole: true);
            if (!argumentOk)
            {
                return visited;
            }

            rewritten = NullableLiftExpansion.GetValueOrDefault(receiverAccess, argumentAccess, underlying);
        }

        _stats.GetValueOrDefaultSites++;
        return FinalizeSite(node, rewritten, _currentHoist);
    }

    // ── NL-7/NL-8 nullable conversions (decision 13) ──

    public override SyntaxNode? VisitCastExpression(CastExpressionSyntax node)
    {
        var visited = (CastExpressionSyntax)base.VisitCastExpression(node)!;

        var classification = _analyzer.ClassifyConversion(node);
        if (classification.Quadrant is NullableConversionQuadrant.None
            or NullableConversionQuadrant.Wrap)
        {
            return visited;
        }

        if (_analyzer.IsWithinExpressionTree(node))
        {
            ReportSiteError(node,
                PreprocessDiagnosticCodes.NullableLiftExpressionTree,
                NullableLiftDiagnostics.ReasonExpressionTree,
                NullableLiftDiagnostics.ExpressionTree(NullableLiftSyntaxFacts.SiteText(node)));
            return visited;
        }

        if (_model.GetOperation(node) is NBOp.IConversionOperation conversion
            && NullableLiftAnalyzer.IsDynamicOperation(conversion))
        {
            ReportSiteError(node,
                PreprocessDiagnosticCodes.NullableLiftDynamic,
                NullableLiftDiagnostics.ReasonDynamic,
                NullableLiftDiagnostics.Dynamic(NullableLiftSyntaxFacts.SiteText(node)));
            return visited;
        }

        // C41 special numerics route to CS2J5356 ahead of the user-defined gate.
        if (NullableLiftAnalyzer.ClassifyUnderlying(classification.SourceUnderlying)
                == NullableValueDomain.SpecialNumeric
            || NullableLiftAnalyzer.ClassifyUnderlying(classification.TargetUnderlying)
                == NullableValueDomain.SpecialNumeric)
        {
            ReportSiteError(node,
                PreprocessDiagnosticCodes.NullableLiftSpecialNumeric,
                NullableLiftDiagnostics.ReasonSpecialNumeric,
                NullableLiftDiagnostics.SpecialNumeric(NullableLiftSyntaxFacts.SiteText(node)));
            return visited;
        }

        if (classification.Quadrant == NullableConversionQuadrant.UserDefined)
        {
            // Plain (non-lifted) user-defined conversion — neither side is
            // Nullable<T>, so there is no lifted three-valued guard for C22 to
            // explicitize. The cast stays source syntax for the later C14
            // member-desugar pass (which lowers user-defined casts to the
            // operator contract); touching it here would only duplicate that
            // work. Genuinely lifted user conversions (S? → T?/T) keep the
            // deferred CS2J5353 gate until C14 lands.
            if (!classification.SourceNullable && !classification.TargetNullable)
            {
                return visited;
            }

            ReportSiteError(node,
                PreprocessDiagnosticCodes.NullableLiftUserDefinedOperator,
                NullableLiftDiagnostics.ReasonUserDefinedOperator,
                NullableLiftDiagnostics.UserDefinedOperator(NullableLiftSyntaxFacts.SiteText(node)));
            return visited;
        }

        if (_model.GetOperation(node) is not NBOp.IConversionOperation boundConversion)
        {
            return visited;
        }

        _currentHoist = null;
        var (ok, sourceAccess) = PrepareOperand(
            node, node.Expression, visited.Expression, boundConversion.Operand,
            sideNullable: classification.SourceNullable, isLeft: true);
        if (!ok)
        {
            return visited;
        }

        ExpressionSyntax rewritten;
        switch (classification.Quadrant)
        {
            case NullableConversionQuadrant.Unwrap:
            case NullableConversionQuadrant.UnwrapAndConvert:
                // ② T?→T becomes .Value; ④ S?→T becomes (T)x.Value (null throws).
                rewritten = NullableLiftExpansion.UnwrapValue(
                    sourceAccess, boundConversion.Type!, classification.SourceUnderlying);
                _stats.UnwrapConversions++;
                break;

            case NullableConversionQuadrant.Lifted:
            {
                // For an S → T? (non-nullable source) lifted conversion the
                // classifier's SourceUnderlying is null (S is not T?); the
                // bound conversion operand carries S itself.
                var sourceUnderlying = classification.SourceNullable
                    ? classification.SourceUnderlying
                    : boundConversion.Operand?.Type;
                if (sourceUnderlying == null)
                {
                    return visited;
                }

                rewritten = NullableLiftExpansion.LiftedConversionGuard(
                    sourceAccess,
                    sourceUnderlying,
                    classification.TargetUnderlying!,
                    classification.SourceNullable);
                _stats.LiftedConversions++;
                break;
            }

            default:
                return visited;
        }

        return FinalizeSite(node, rewritten, _currentHoist);
    }

    // ── single-evaluation operand hoisting (decision 2) ──

    private (bool Ok, OperandAccess Access) PrepareOperand(
        ExpressionSyntax site,
        ExpressionSyntax originalOperand,
        ExpressionSyntax visitedOperand,
        IOperation? operandOperation,
        bool sideNullable,
        bool isLeft,
        bool defaultRole = false)
    {
        if (NullableLiftAnalyzer.IsNullConstant(operandOperation))
        {
            return (true, OperandAccess.Null);
        }

        // Stability must be queried against the ORIGINAL operand node bound to
        // this semantic model: along a rebuilt ancestor chain even an unchanged
        // reused child (e.g. a parameter identifier) reports a detached syntax
        // tree, while a freshly synthesized inner guard only exists on the
        // original node as the inner binary it replaced.
        if (_analyzer.IsStableReference(originalOperand))
        {
            return (true, OperandAccess.Direct(visitedOperand, sideNullable));
        }

        var hoist = EnsureHoist(site);
        if (hoist == null)
        {
            return (false, default);
        }

        var name = defaultRole
            ? Names.Default(hoist.SiteNumber)
            : isLeft
                ? Names.Left(hoist.SiteNumber)
                : Names.Right(hoist.SiteNumber);
        var role = defaultRole
            ? NullableLiftSyntaxFacts.RoleDefault
            : isLeft
                ? NullableLiftSyntaxFacts.RoleLeft
                : NullableLiftSyntaxFacts.RoleRight;
        AddTempDeclaration(hoist, VarDeclaration(name, visitedOperand), role);
        return (true, OperandAccess.Direct(IdentifierName(name), sideNullable));
    }

    private HoistState? EnsureHoist(ExpressionSyntax site)
    {
        if (_currentHoist != null)
        {
            return _currentHoist;
        }

        var anchor = NullableLiftSyntaxFacts.FindAnchorStatement(site);
        if (anchor == null
            || !NullableLiftSyntaxFacts.IsWhitelistedAnchor(anchor)
            || !NullableLiftSyntaxFacts.IsLiftableContext(site, anchor)
            || !CanHostDeclarations(anchor))
        {
            ReportSiteError(site,
                PreprocessDiagnosticCodes.NullableLiftComplexTarget,
                NullableLiftDiagnostics.ReasonUnliftableContext,
                NullableLiftDiagnostics.UnliftableContext(NullableLiftSyntaxFacts.SiteText(site)));
            return null;
        }

        _currentHoist = new HoistState(
            ++_sequence,
            anchor,
            new SyntaxAnnotation(NullableLiftSyntaxFacts.HoistAnnotationKind));
        return _currentHoist;
    }

    /// <summary>
    /// True when the anchor's syntactic slot can host the hoisted
    /// declarations: a statement list (block/switch section/global) or a
    /// single embedded statement (if/while/do/for/foreach/using/lock/fixed/
    /// else/labeled) that Pass2 wraps in a bare block.
    /// </summary>
    private static bool CanHostDeclarations(StatementSyntax anchor)
    {
        switch (anchor.Parent)
        {
            case BlockSyntax:
            case SwitchSectionSyntax:
            case GlobalStatementSyntax:
            case IfStatementSyntax ifStatement when ifStatement.Statement == anchor:
            case ElseClauseSyntax elseClause when elseClause.Statement == anchor:
            case WhileStatementSyntax whileStatement when whileStatement.Statement == anchor:
            case DoStatementSyntax doStatement when doStatement.Statement == anchor:
            case ForStatementSyntax forStatement when forStatement.Statement == anchor:
            case ForEachStatementSyntax forEach when forEach.Statement == anchor:
            case ForEachVariableStatementSyntax forEachVariable when forEachVariable.Statement == anchor:
            case UsingStatementSyntax usingStatement when usingStatement.Statement == anchor:
            case LockStatementSyntax lockStatement when lockStatement.Statement == anchor:
            case FixedStatementSyntax fixedStatement when fixedStatement.Statement == anchor:
            case LabeledStatementSyntax labeled when labeled.Statement == anchor:
                return true;
            default:
                return false;
        }
    }

    private ExpressionSyntax FinalizeSite(
        ExpressionSyntax original,
        ExpressionSyntax rewritten,
        HoistState? hoist)
    {
        MadeChanges = true;

        if (hoist != null)
        {
            _pending.Add(new PendingNullableLift(
                hoist.Marker, hoist.SiteNumber, hoist.Anchor, hoist.Declarations,
                hoist.WriteStatements.Count == 0 ? null : hoist.WriteStatements,
                hoist.ConsumesAnchor));
            _stats.TempsLifted += hoist.Declarations.Count;
            rewritten = rewritten.WithAdditionalAnnotations(
                hoist.Marker,
                new SyntaxAnnotation(NullableLiftSyntaxFacts.HoistAnnotationKind));
        }

        return rewritten
            .WithTriviaFrom(original)
            .WithAdditionalAnnotations(
                NB.RewriteAnnotation,
                new SyntaxAnnotation(NullableLiftSyntaxFacts.RewriteAnnotationKind));
    }

    // ── helpers ──

    private bool IsDynamicNullableSite(NBOp.IBinaryOperation operation, BinaryExpressionSyntax node)
    {
        if (!NullableLiftAnalyzer.IsDynamicOperation(operation))
        {
            return false;
        }

        return NullableLiftAnalyzer.IsNullableType(operation.Type)
            || NullableLiftAnalyzer.IsNullableType(operation.LeftOperand?.Type)
            || NullableLiftAnalyzer.IsNullableType(operation.RightOperand?.Type)
            || NullableLiftAnalyzer.IsNullableType(_model.GetTypeInfo(node.Left).Type)
            || NullableLiftAnalyzer.IsNullableType(_model.GetTypeInfo(node.Right).Type);
    }

    private bool IsDynamicNullableSite(NBOp.IUnaryOperation operation, PrefixUnaryExpressionSyntax node)
    {
        if (!NullableLiftAnalyzer.IsDynamicOperation(operation))
        {
            return false;
        }

        return NullableLiftAnalyzer.IsNullableType(operation.Type)
            || NullableLiftAnalyzer.IsNullableType(operation.Operand?.Type)
            || NullableLiftAnalyzer.IsNullableType(_model.GetTypeInfo(node.Operand).Type);
    }

    private static ITypeSymbol? ResolveBinaryUnderlying(NBOp.IBinaryOperation operation)
    {
        if (NullableLiftAnalyzer.TryUnwrapNullable(operation.LeftOperand?.Type, out var left) && left != null)
        {
            return left;
        }

        if (NullableLiftAnalyzer.TryUnwrapNullable(operation.RightOperand?.Type, out var right) && right != null)
        {
            return right;
        }

        return NullableLiftAnalyzer.TryUnwrapNullable(operation.Type, out var result) ? result : null;
    }

    private static ITypeSymbol? ResolveUnderlying(ITypeSymbol? first, ITypeSymbol? second)
    {
        if (NullableLiftAnalyzer.TryUnwrapNullable(first, out var underlying) && underlying != null)
        {
            return underlying;
        }

        return NullableLiftAnalyzer.TryUnwrapNullable(second, out underlying) ? underlying : null;
    }

    /// <summary>
    /// Adds one hoisted declaration and tallies its role (a/b/o/d plus the
    /// M3 decision-17 t/r/i complex-RMW roles) in the per-rule statistics;
    /// the total <see cref="NullableLiftStatistics.TempsLifted"/> moves once
    /// per declaration in <see cref="FinalizeSite"/>.
    /// </summary>
    private void AddTempDeclaration(
        HoistState hoist, LocalDeclarationStatementSyntax declaration, string role)
    {
        hoist.Declarations.Add(declaration);
        switch (role)
        {
            case NullableLiftSyntaxFacts.RoleLeft: _stats.TempsLeftRole++; break;
            case NullableLiftSyntaxFacts.RoleRight: _stats.TempsRightRole++; break;
            case NullableLiftSyntaxFacts.RoleOld: _stats.TempsOldRole++; break;
            case NullableLiftSyntaxFacts.RoleDefault: _stats.TempsDefaultRole++; break;
            case NullableLiftSyntaxFacts.RoleTarget: _stats.TempsTargetRole++; break;
            case NullableLiftSyntaxFacts.RoleReceiver: _stats.TempsReceiverRole++; break;
            case string r when r.StartsWith(NullableLiftSyntaxFacts.RoleIndex, StringComparison.Ordinal):
                _stats.TempsIndexRole++;
                break;
        }
    }

    private static LocalDeclarationStatementSyntax VarDeclaration(
        string name, ExpressionSyntax initializer) =>
        LocalDeclarationStatement(
                VariableDeclaration(
                    IdentifierName("var").WithTrailingTrivia(ElasticSpace),
                    SingletonSeparatedList(
                        VariableDeclarator(name)
                            .WithInitializer(EqualsValueClause(initializer)))))
            .WithAdditionalAnnotations(
                NB.RewriteAnnotation,
                new SyntaxAnnotation(NullableLiftSyntaxFacts.RewriteAnnotationKind));

    private void ReportSiteError(
        ExpressionSyntax site, string code, string reason, string message)
    {
        HasErrorDiagnostic = true;
        var span = site.GetLocation().GetLineSpan();
        _diagnostics.Add(new NumericBoundaryDiagnostic(
            NumericBoundarySeverity.Error,
            code,
            message,
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1));
        _stats.DeferredByReason[$"{code}:{reason}"] =
            _stats.DeferredByReason.TryGetValue($"{code}:{reason}", out var count)
                ? count + 1
                : 1;
    }

    private sealed record HoistState(
        int SiteNumber,
        StatementSyntax Anchor,
        SyntaxAnnotation Marker)
    {
        public List<LocalDeclarationStatementSyntax> Declarations { get; } = new();

        /// <summary>NL-10 postfix: read-modify-write statements spliced after <see cref="Declarations"/>.</summary>
        public List<StatementSyntax> WriteStatements { get; } = new();

        /// <summary>NL-10 standalone postfix: Pass2 drops the placeholder anchor statement.</summary>
        public bool ConsumesAnchor { get; set; }
    }
}
