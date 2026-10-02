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
/// tree, and the <c>var</c> declarations Pass2 splices in.
/// </summary>
internal sealed record PendingNullableLift(
    SyntaxAnnotation Marker,
    int Order,
    StatementSyntax OriginalAnchor,
    List<LocalDeclarationStatementSyntax> Declarations);

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
    private readonly NullableLiftSyntaxFacts.TempNameAllocator _names;
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
        _names = new NullableLiftSyntaxFacts.TempNameAllocator(model.SyntaxTree.GetRoot());
    }

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

        // M2 domains: enum bit-operation backstop (NL-9) and any other
        // custom-struct binding — no rewrite, no diagnostic, no residual.
        if (classification.Domain is NullableValueDomain.Enum
            or NullableValueDomain.Other
            or NullableValueDomain.NotNullable)
        {
            return visited;
        }

        return RewriteOwnedBinary(node, visited, classification, operation) ?? visited;
    }

    public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
    {
        var visited = (PrefixUnaryExpressionSyntax)base.VisitPrefixUnaryExpression(node)!;

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

        // NL-9 enum `~` backstop is M2.
        if (classification.Domain is NullableValueDomain.Enum
            or NullableValueDomain.Other
            or NullableValueDomain.NotNullable)
        {
            return visited;
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
            {
                var leftPositionNullable =
                    classification.LeftNullable
                    || NullableLiftAnalyzer.IsNullConstant(operation.LeftOperand);
                var rightPositionNullable =
                    classification.RightNullable
                    || NullableLiftAnalyzer.IsNullConstant(operation.RightOperand);

                if (leftPositionNullable && rightPositionNullable)
                {
                    rewritten = NullableLiftExpansion.LiftedEqualityBothNullable(
                        original.IsKind(SyntaxKind.NotEqualsExpression),
                        leftAccess, rightAccess, underlying);
                    _stats.LiftedEqualityBothNullableSites++;
                }
                else
                {
                    var nullableAccess = leftPositionNullable ? leftAccess : rightAccess;
                    var otherAccess = leftPositionNullable ? rightAccess : leftAccess;
                    rewritten = NullableLiftExpansion.LiftedEqualityOneSided(
                        original.IsKind(SyntaxKind.NotEqualsExpression),
                        nullableAccess, otherAccess, underlying);
                    _stats.LiftedEqualityOneSidedSites++;
                }

                break;
            }

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

    // ── single-evaluation operand hoisting (decision 2) ──

    private (bool Ok, OperandAccess Access) PrepareOperand(
        ExpressionSyntax site,
        ExpressionSyntax originalOperand,
        ExpressionSyntax visitedOperand,
        IOperation? operandOperation,
        bool sideNullable,
        bool isLeft)
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

        var name = isLeft
            ? _names.Left(hoist.SiteNumber)
            : _names.Right(hoist.SiteNumber);
        hoist.Declarations.Add(VarDeclaration(name, visitedOperand));
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
                hoist.Marker, hoist.SiteNumber, hoist.Anchor, hoist.Declarations));
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
    }
}
