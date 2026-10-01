using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.MemberDesugar.Operators;
using CSharpToJava.Core.Preprocessing;
using NB = CSharpToJava.Core.NumericBoundary.NumericBoundarySyntaxFacts;
using NBOp = Microsoft.CodeAnalysis.Operations;

namespace CSharpToJava.Core.NumericBoundary;

/// <summary>
/// Syntax rewriter for numeric-boundary (C15, M2/M3):
/// NB-1/NB-2 strip <c>unchecked</c> shells/blocks; NB-3/4 rewrite checked
/// binary/unary integer arithmetic; NB-5 rewrites checked compound assignments
/// (with narrow out-conversions); NB-7 rewrites checked explicit integer
/// conversions; NB-8 rewrites ++/-- (postfix old-value hoisting); NB-9/NB-10
/// remove <c>checked</c> shells/normalize blocks to bare blocks; NB-6
/// materializes constants inside explicit keyword scopes; NB-12 reports
/// unsupported sites with CS2J5320-5325. Anonymous-function boundaries reset
/// to the project default context (lambdas do not inherit checked blocks;
/// local functions do).
/// </summary>
internal sealed class CheckedExprRewriter : CSharpSyntaxRewriter
{
    private readonly NumericBoundaryAnalyzer _analyzer;
    private readonly NumericBoundaryStatistics _stats;
    private readonly List<NumericBoundaryDiagnostic> _diagnostics;
    private readonly List<HoistPlan> _hoistPlans = new();
    private int _tempCounter;

    public CheckedExprRewriter(
        NumericBoundaryAnalyzer analyzer,
        NumericBoundaryStatistics stats,
        List<NumericBoundaryDiagnostic> diagnostics,
        bool enableDecimalOps = false)
    {
        _analyzer = analyzer;
        _stats = stats;
        _diagnostics = diagnostics;
        _enableDecimalOps = enableDecimalOps;
    }

    private readonly bool _enableDecimalOps;

    /// <summary>True when at least one node was structurally replaced/rewritten.</summary>
    public bool MadeChanges { get; private set; }

    // ── NB-9/NB-1: checked/unchecked expression shells ──

    public override SyntaxNode? VisitCheckedExpression(CheckedExpressionSyntax node)
    {
        var isCheckedShell = node.IsKind(SyntaxKind.CheckedExpression);

        // Decision 4 (checked shells only): user-defined op_Checked* variants
        // stay for member-desugar; the voucher exempts them from NB-13.
        if (isCheckedShell && _analyzer.IsCheckedUserOperatorVariant(node.Expression))
        {
            // The shell stays for member-desugar, but nested built-in arithmetic
            // in its arguments still rewrites under the checked context.
            ExpressionSyntax visitedVoucherInner;
            using (_analyzer.PushContext(isChecked: true))
            {
                visitedVoucherInner = (ExpressionSyntax)Visit(node.Expression)!;
            }

            _stats.CheckedUserOpShellsDeferred++;
            return node.WithExpression(visitedVoucherInner);
        }

        // NB-6: a constant that compiles inside the shell folds to the exact
        // target-type literal (unchecked truncation or checked non-overflow).
        if (_analyzer.TryMaterializeConstant(node.Expression, out var materialized))
        {
            _stats.ConstantsMaterialized++;
            MadeChanges = true;
            return materialized
                .WithTriviaFrom(node)
                .WithAdditionalAnnotations(NB.RewriteAnnotation);
        }

        ExpressionSyntax visitedInner;
        using (_analyzer.PushContext(isCheckedShell))
        {
            visitedInner = (ExpressionSyntax)Visit(node.Expression)!;
        }

        if (!isCheckedShell)
        {
            _stats.UncheckedExpressionsStripped++;
        }
        else
        {
            _stats.CheckedNonIntegerShellsStripped++;
        }

        return StripShell(node, visitedInner);
    }

    // ── NB-10/NB-2: checked/unchecked blocks ──

    public override SyntaxNode? VisitCheckedStatement(CheckedStatementSyntax node)
    {
        var isCheckedBlock = node.IsKind(SyntaxKind.CheckedStatement);

        BlockSyntax visitedBlock;
        using (_analyzer.PushContext(isCheckedBlock))
        {
            visitedBlock = (BlockSyntax)Visit(node.Block)!;
        }

        var bareBlock = visitedBlock.WithLeadingTrivia(
            node.GetLeadingTrivia().Concat(visitedBlock.GetLeadingTrivia()));

        if (isCheckedBlock)
        {
            // NB-10: keyword gone; braces always stay (checked block → bare block).
            MadeChanges = true;
            return bareBlock.WithAdditionalAnnotations(NB.RewriteAnnotation);
        }

        // NB-2: unchecked { … } → statements spliced when safe.
        MadeChanges = true;
        _stats.UncheckedBlocksUnwrapped++;

        if (MustKeepBraces(node, visitedBlock))
        {
            return bareBlock;
        }

        return bareBlock.WithAdditionalAnnotations(NB.UnwrapBlockAnnotation);
    }

    // ── anonymous functions reset the checked context (lambdas do not inherit) ──

    public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
    {
        using (_analyzer.PushAnonymousFunctionBoundary())
        {
            return base.VisitSimpleLambdaExpression(node);
        }
    }

    public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
    {
        using (_analyzer.PushAnonymousFunctionBoundary())
        {
            return base.VisitParenthesizedLambdaExpression(node);
        }
    }

    public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node)
    {
        using (_analyzer.PushAnonymousFunctionBoundary())
        {
            return base.VisitAnonymousMethodExpression(node);
        }
    }

    // ── statement lists (NB-2 splice chokepoint) ──

    public override SyntaxNode? VisitBlock(BlockSyntax node)
        => node.WithStatements(SyntaxFactory.List(SpliceStatements(node.Statements)));

    public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
        => node.WithStatements(SyntaxFactory.List(SpliceStatements(node.Statements)));

    private IEnumerable<StatementSyntax> SpliceStatements(SyntaxList<StatementSyntax> statements)
    {
        foreach (var statement in statements)
        {
            var visited = Visit(statement);
            if (visited is not StatementSyntax visitedStatement)
            {
                continue;
            }

            if (visitedStatement is BlockSyntax spliced
                && spliced.HasAnnotations(NB.UnwrapBlockAnnotationKind))
            {
                var list = spliced.Statements.ToList();
                if (list.Count > 0)
                {
                    list[0] = list[0].WithLeadingTrivia(
                        spliced.GetLeadingTrivia().Concat(list[0].GetLeadingTrivia()));
                    list[^1] = list[^1].WithTrailingTrivia(
                        list[^1].GetTrailingTrivia().Concat(spliced.GetTrailingTrivia()));
                }

                foreach (var splicedStatement in list)
                {
                    yield return splicedStatement;
                }

                continue;
            }

            yield return visitedStatement;
        }
    }

    // ── NB-3: checked binary arithmetic ──

    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        var visited = (ExpressionSyntax)base.VisitBinaryExpression(node)!;

        // NB-6 folds constants in either checkedness: inside an explicit
        // keyword scope and inside a /checked project (the folded value may be
        // a truncation inside an unchecked block that must survive stripping).
        if (!_analyzer.IsInCheckedContext
            && !_analyzer.IsWithinExplicitNumericScope
            && !_analyzer.ProjectDefaultChecked)
        {
            return visited;
        }

        if (TryMaterializeSite(node, out var materialized))
        {
            return materialized;
        }

        if (!_analyzer.IsInCheckedContext)
        {
            return visited;
        }

        // Compile-time folded arithmetic either materialized above or stays folded:
        // never a runtime helper call.
        if (_analyzer.HasCompileTimeConstantValue(node))
        {
            return visited;
        }

        var operation = _analyzer.GetOperation(node);
        var binary = operation as NBOp.IBinaryOperation
            ?? (operation as NBOp.IConversionOperation)?.Operand as NBOp.IBinaryOperation;
        if (binary is not { OperatorMethod: null })
        {
            return visited;
        }

        var errorCode = _analyzer.ClassifyCheckedSiteError(node, binary, binary.Type);
        if (errorCode != null)
        {
            ReportError(node, errorCode);
            return visited;
        }

        if (!NB.TryGetArithmeticDomain(binary.Type, out var domain))
        {
            // decimal/float/double/enum/other: checked has no effect (NB-11).
            return visited;
        }

        string? method = node.Kind() switch
        {
            SyntaxKind.AddExpression => "Add",
            SyntaxKind.SubtractExpression => "Subtract",
            SyntaxKind.MultiplyExpression => "Multiply",
            _ => null, // / % and bitwise/shift never need a helper (decision 3)
        };
        if (method == null)
        {
            return visited;
        }

        var visitedBinary = (BinaryExpressionSyntax)visited;
        var call = NB.HelperCall(
            method + "Checked" + NB.DomainSuffix(domain),
            visitedBinary.Left,
            visitedBinary.Right);

        _stats.CheckedBinarySites++;
        RecordHelper(domain);
        return Finalize(node, call);
    }

    // ── NB-4 / NB-8 prefix: unary minus and prefix ++/-- ──

    public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
    {
        if (node.IsKind(SyntaxKind.PreIncrementExpression)
            || node.IsKind(SyntaxKind.PreDecrementExpression))
        {
            return RewriteIncrement(node, node.Operand, isPostfix: false);
        }

        var visited = (ExpressionSyntax)base.VisitPrefixUnaryExpression(node)!;
        if (!_analyzer.IsInCheckedContext
            && !_analyzer.IsWithinExplicitNumericScope
            && !_analyzer.ProjectDefaultChecked)
        {
            return visited;
        }

        if (TryMaterializeSite(node, out var materialized))
        {
            return materialized;
        }

        if (!_analyzer.IsInCheckedContext)
        {
            return visited;
        }

        // Constant negation materialized above (or stays folded): no runtime helper.
        if (_analyzer.HasCompileTimeConstantValue(node))
        {
            return visited;
        }

        var operation = _analyzer.GetOperation(node);
        var unary = operation as NBOp.IUnaryOperation
            ?? (operation as NBOp.IConversionOperation)?.Operand as NBOp.IUnaryOperation;
        if (unary is not { OperatorMethod: null }
            || node.Kind() != SyntaxKind.UnaryMinusExpression)
        {
            return visited;
        }

        var errorCode = _analyzer.ClassifyCheckedSiteError(node, unary, unary.Type);
        if (errorCode != null)
        {
            ReportError(node, errorCode);
            return visited;
        }

        if (!NB.TryGetArithmeticDomain(unary.Type, out var domain))
        {
            return visited;
        }

        // -uint promotes to long and can never overflow; C# keeps it a plain
        // long negation (probe-pinned). The uint operand is wrapped in an
        // implicit uint→long conversion inside the unary operation, so peel it.
        var operandType = unary.Operand is NBOp.IConversionOperation operandConversion
            ? operandConversion.Operand?.Type
            : unary.Operand?.Type;
        if (domain is NB.NumericDomain.UInt32 or NB.NumericDomain.UInt64
            || (domain == NB.NumericDomain.Int64
                && operandType?.SpecialType == SpecialType.System_UInt32))
        {
            return visited;
        }

        var call = NB.HelperCall(
            "NegateChecked" + NB.DomainSuffix(domain),
            ((PrefixUnaryExpressionSyntax)visited).Operand);

        _stats.CheckedUnarySites++;
        RecordHelper(domain);
        return Finalize(node, call);
    }

    // ── NB-8 postfix ++/-- ──

    public override SyntaxNode? VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
        => RewriteIncrement(node, node.Operand, isPostfix: true);

    private ExpressionSyntax RewriteIncrement(ExpressionSyntax node, ExpressionSyntax targetSyntax, bool isPostfix)
    {
        // Children must be visited exactly once; do it explicitly here.
        var visitedTarget = (ExpressionSyntax)Visit(targetSyntax)!;

        if (!_analyzer.IsInCheckedContext)
        {
            // Re-attach the visited operand without further changes.
            return ReattachIncrement(node, visitedTarget, isPostfix);
        }

        var operation = _analyzer.GetOperation(node);
        if (operation is not NBOp.IIncrementOrDecrementOperation increment)
        {
            return ReattachIncrement(node, visitedTarget, isPostfix);
        }

        if (increment.OperatorMethod != null)
        {
            // User-defined ++/--: never a helper; member-desugar owns it.
            return ReattachIncrement(node, visitedTarget, isPostfix);
        }

        var type = increment.Type;
        var errorCode = _analyzer.ClassifyCheckedSiteError(node, increment, type);
        if (errorCode != null)
        {
            // NB-12: keep the node (with visited children); the file is rolled back wholesale.
            ReportError(node, errorCode);
            return ReattachIncrement(node, visitedTarget, isPostfix);
        }

        if (type is not null && NB.IsFloatingPointOrDecimalType(type))
        {
            ReportError(node, PreprocessDiagnosticCodes.NumericBoundaryComplexIncrement);
            return ReattachIncrement(node, visitedTarget, isPostfix);
        }

        if (_analyzer.ClassifyTarget(targetSyntax) == NumericBoundaryAnalyzer.TargetShape.Complex)
        {
            ReportError(node, PreprocessDiagnosticCodes.NumericBoundaryComplexIncrement);
            return ReattachIncrement(node, visitedTarget, isPostfix);
        }

        var decrement = isPostfix
            ? node.IsKind(SyntaxKind.PostDecrementExpression)
            : node.IsKind(SyntaxKind.PreDecrementExpression);
        var arithmeticMethod = (decrement ? "Subtract" : "Add") + "Checked";

        ExpressionSyntax newValue;
        NB.NumericDomain domain;
        var targetText = visitedTarget.ToFullString();

        if (type is not null && type.TypeKind == TypeKind.Enum)
        {
            // enum ++ follows the underlying domain; the return-to-enum cast is
            // itself never checked (decision 9). Narrow-backed enums normalize
            // through Int32.
            var underlying = ((INamedTypeSymbol)type).EnumUnderlyingType;
            if (underlying == null || !TryGetIncrementDomain(underlying, out domain))
            {
                ReportError(node, PreprocessDiagnosticCodes.NumericBoundaryUnsupportedType);
                return ReattachIncrement(node, visitedTarget, isPostfix);
            }

            var enumCast = (ExpressionSyntax)SyntaxFactory.CastExpression(
                SyntaxFactory.ParseTypeName(
                    type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
                NB.HelperCall(
                    arithmeticMethod + NB.DomainSuffix(domain),
                    NB.CastToDomain(domain, ParseTarget(targetText)),
                    NB.LiteralOne()));
            newValue = enumCast;
        }
        else if (type is not null && NB.IsNarrowIntegerSpecialType(type.SpecialType))
        {
            // byte/short/char ++: promoted Int32 arithmetic + checked narrow conversion.
            domain = NB.NumericDomain.Int32;
            var conversionName = NB.TryGetCheckedConversionName(type.SpecialType, SpecialType.System_Int32)
                ?? throw new InvalidOperationException("narrow increment must have a checked conversion name");
            newValue = NB.HelperCall(
                conversionName,
                NB.HelperCall(
                    arithmeticMethod + NB.DomainSuffix(domain),
                    ParseTarget(targetText),
                    NB.LiteralOne()));
        }
        else if (type is not null && NB.TryGetArithmeticDomain(type, out domain))
        {
            newValue = NB.HelperCall(
                arithmeticMethod + NB.DomainSuffix(domain),
                ParseTarget(targetText),
                NB.LiteralOne());
        }
        else
        {
            ReportError(node, PreprocessDiagnosticCodes.NumericBoundaryUnsupportedType);
            return ReattachIncrement(node, visitedTarget, isPostfix);
        }

        _stats.CheckedIncrementSites++;
        RecordHelper(domain);

        if (isPostfix)
        {
            var placement = ClassifyPostfixPlacement(node);
            if (placement == PostfixPlacement.Standalone)
            {
                // Value discarded: the plain assignment is equivalent.
                var standalone = SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    ParseTarget(targetText),
                    newValue);
                return Finalize(node, standalone);
            }

            if (placement == PostfixPlacement.Error)
            {
                ReportError(node, PreprocessDiagnosticCodes.NumericBoundaryComplexIncrement);
                return ReattachIncrement(node, visitedTarget, isPostfix);
            }

            // Value consumed: hoist `var __cs2j_nb_n = target;` + the assignment
            // to the head of the enclosing statement; the postfix evaluates to
            // the old value (decision 9).
            var tempName = NB.TempNamePrefix + _tempCounter++;
            var marker = new SyntaxAnnotation(NB.PostfixHoistAnnotationKind);
            _hoistPlans.Add(new HoistPlan(
                marker,
                tempName,
                ParseTarget(targetText),
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    ParseTarget(targetText),
                    newValue)));

            MadeChanges = true;
            return SyntaxFactory.IdentifierName(tempName)
                .WithTriviaFrom(node)
                .WithAdditionalAnnotations(marker, NB.RewriteAnnotation);
        }

        // Prefix: value is the new value, evaluation stays in place.
        var assignment = SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            ParseTarget(targetText),
            newValue);
        return Finalize(node, assignment);
    }

    /// <summary>Reattach the (possibly rewritten) operand to the original ++/-- node.</summary>
    private static ExpressionSyntax ReattachIncrement(
        ExpressionSyntax node, ExpressionSyntax visitedOperand, bool isPostfix)
        => isPostfix
            ? ((PostfixUnaryExpressionSyntax)node).WithOperand(visitedOperand)
            : ((PrefixUnaryExpressionSyntax)node).WithOperand(visitedOperand);

    private static bool TryGetIncrementDomain(ITypeSymbol underlying, out NB.NumericDomain domain)
    {
        if (NB.IsNarrowIntegerSpecialType(underlying.SpecialType))
        {
            domain = NB.NumericDomain.Int32;
            return true;
        }

        return NB.TryGetArithmeticDomain(underlying, out domain);
    }

    private enum PostfixPlacement
    {
        Standalone,
        Hoist,
        Error,
    }

    private PostfixPlacement ClassifyPostfixPlacement(ExpressionSyntax node)
    {
        // Direct for-statement incrementor: value discarded.
        if (node.Ancestors().OfType<ForStatementSyntax>().FirstOrDefault() is { } forStatement
            && forStatement.Incrementors.Any(inc => UnwrapParentheses(inc) == node))
        {
            return PostfixPlacement.Standalone;
        }

        var enclosing = node.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
        switch (enclosing)
        {
            case ExpressionStatementSyntax expressionStatement:
                return UnwrapParentheses(expressionStatement.Expression) == node
                    ? PostfixPlacement.Standalone
                    : PostfixPlacement.Hoist;
            case ReturnStatementSyntax:
            case ThrowStatementSyntax:
            case YieldStatementSyntax { ReturnOrBreakKeyword.RawKind: (int)SyntaxKind.YieldKeyword }:
                return PostfixPlacement.Hoist;
            case LocalDeclarationStatementSyntax declaration:
                // Explicit using/fixed/for initializers cannot receive hoisted
                // statements in place; everything else lives in a statement list
                // or global-statement position and is handled by ApplyHoists.
                return declaration.Parent is ForStatementSyntax
                    or UsingStatementSyntax
                    or FixedStatementSyntax
                    ? PostfixPlacement.Error
                    : PostfixPlacement.Hoist;
            default:
                return PostfixPlacement.Error;
        }
    }

    // ── NB-5: checked compound assignments ──

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        var visited = (ExpressionSyntax)base.VisitAssignmentExpression(node)!;
        if (!_analyzer.IsInCheckedContext || !OperatorRule.IsCompoundKind(node))
        {
            return visited;
        }

        var operation = _analyzer.GetOperation(node);
        var compound = operation as NBOp.ICompoundAssignmentOperation
            ?? (operation as NBOp.IConversionOperation)?.Operand as NBOp.ICompoundAssignmentOperation;
        if (compound is not { OperatorMethod: null })
        {
            return visited;
        }

        var errorCode = _analyzer.ClassifyCheckedSiteError(node, compound, compound.Type);
        if (errorCode != null)
        {
            ReportError(node, errorCode);
            return visited;
        }

        var targetType = compound.Target.Type;
        if (targetType == null)
        {
            return visited;
        }

        if (NB.IsFloatingPointOrDecimalType(targetType))
        {
            // C34 (decimal-ops) owns decimal compound sites once enabled; the
            // checked shell is stripped above and the bare site reaches C34.
            if (_enableDecimalOps && targetType.SpecialType == SpecialType.System_Decimal)
            {
                return visited;
            }

            ReportError(node, PreprocessDiagnosticCodes.NumericBoundaryComplexIncrement);
            return visited;
        }

        if (targetType.TypeKind == TypeKind.Enum)
        {
            // enum += E does not compile.
            ReportError(node, PreprocessDiagnosticCodes.NumericBoundaryComplexIncrement);
            return visited;
        }

        if (_analyzer.ClassifyTarget(node.Left) == NumericBoundaryAnalyzer.TargetShape.Complex)
        {
            ReportError(node, PreprocessDiagnosticCodes.NumericBoundaryComplexIncrement);
            return visited;
        }

        string? method = node.Kind() switch
        {
            SyntaxKind.AddAssignmentExpression => "Add",
            SyntaxKind.SubtractAssignmentExpression => "Subtract",
            SyntaxKind.MultiplyAssignmentExpression => "Multiply",
            _ => null, // /= %= and bitwise/shift compounds stay as-is (decision 3)
        };
        if (method == null)
        {
            return visited;
        }

        // byte/sbyte/short/ushort/char compound arithmetic is evaluated in the
        // promoted Int32 domain; the checked out-conversion is appended below.
        NB.NumericDomain arithmeticDomain;
        if (NB.IsNarrowIntegerSpecialType(targetType.SpecialType))
        {
            arithmeticDomain = NB.NumericDomain.Int32;
        }
        else if (!NB.TryGetArithmeticDomain(compound.Type, out arithmeticDomain))
        {
            ReportError(node, PreprocessDiagnosticCodes.NumericBoundaryUnsupportedType);
            return visited;
        }

        var visitedAssignment = (AssignmentExpressionSyntax)visited;
        var leftText = visitedAssignment.Left.ToFullString();
        ExpressionSyntax result = NB.HelperCall(
            method + "Checked" + NB.DomainSuffix(arithmeticDomain),
            ParseTarget(leftText),
            visitedAssignment.Right);
        RecordHelper(arithmeticDomain);

        if (NB.IsNarrowIntegerSpecialType(targetType.SpecialType))
        {
            // x += y on a narrow target is x = (T)(x op y): the out-conversion
            // is checked too (NB-5).
            var conversionName = NB.TryGetCheckedConversionName(
                targetType.SpecialType,
                DomainSpecialType(arithmeticDomain));
            if (conversionName == null)
            {
                return visited;
            }

            result = NB.HelperCall(conversionName, result);
            _stats.CheckedConversionSites++;
            RecordHelper(arithmeticDomain);
        }
        else if (!NB.TryGetArithmeticDomain(targetType, out _))
        {
            ReportError(node, PreprocessDiagnosticCodes.NumericBoundaryUnsupportedType);
            return visited;
        }

        _stats.CompoundAssignments++;
        var rewritten = SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            ParseTarget(leftText),
            result);
        return Finalize(node, rewritten);
    }

    private static SpecialType DomainSpecialType(NB.NumericDomain domain) => domain switch
    {
        NB.NumericDomain.Int32 => SpecialType.System_Int32,
        NB.NumericDomain.Int64 => SpecialType.System_Int64,
        NB.NumericDomain.UInt32 => SpecialType.System_UInt32,
        NB.NumericDomain.UInt64 => SpecialType.System_UInt64,
        _ => throw new InvalidOperationException(),
    };

    // ── NB-7: checked explicit integer conversions; NB-6 constant casts ──

    public override SyntaxNode? VisitCastExpression(CastExpressionSyntax node)
    {
        var visited = (ExpressionSyntax)base.VisitCastExpression(node)!;
        if (!_analyzer.IsInCheckedContext
            && !_analyzer.IsWithinExplicitNumericScope
            && !_analyzer.ProjectDefaultChecked)
        {
            return visited;
        }

        if (TryMaterializeSite(node, out var materialized))
        {
            return materialized;
        }

        if (!_analyzer.IsInCheckedContext)
        {
            return visited;
        }

        if (_analyzer.GetOperation(node) is not NBOp.IConversionOperation { OperatorMethod: null } conversion
            || conversion.Conversion.IsIdentity)
        {
            return visited;
        }

        var target = conversion.Type;
        var source = conversion.Operand?.Type;
        if (target == null || source == null)
        {
            return visited;
        }

        // Enum conversions never overflow-check (decision 4).
        if (target.TypeKind == TypeKind.Enum || source.TypeKind == TypeKind.Enum)
        {
            return visited;
        }

        var errorCode = _analyzer.ClassifyCheckedConversionError(node, conversion);
        if (errorCode != null)
        {
            ReportError(node, errorCode);
            return visited;
        }

        if (!NB.IsIntegralType(target) || !NB.IsIntegralType(source))
        {
            return visited;
        }

        var name = NB.TryGetCheckedConversionName(target.SpecialType, source.SpecialType);
        if (name == null)
        {
            // Widening conversion: the bare cast is the checked behavior already.
            return visited;
        }

        var call = NB.HelperCall(name, ((CastExpressionSyntax)visited).Expression);
        _stats.CheckedConversionSites++;
        RecordHelper(NB.SourceDomain(source.SpecialType));
        return Finalize(node, call);
    }

    // ── NB-6: implicit constant conversions inside explicit keyword scopes ──

    public override SyntaxNode? VisitLiteralExpression(LiteralExpressionSyntax node)
    {
        var visited = (ExpressionSyntax)base.VisitLiteralExpression(node)!;
        if (!_analyzer.IsWithinExplicitNumericScope && !_analyzer.ProjectDefaultChecked)
        {
            return visited;
        }

        // e.g. `byte b = 200 + 100;` inside unchecked { } (or inside a /checked
        // project): the folded constant conversion must become a target-correct
        // literal once the shells/blocks are gone, otherwise the intermediate
        // tree (whose csproj may still carry /checked) would fail to compile.
        if (_analyzer.GetOperation(node) is NBOp.IConversionOperation
            {
                Conversion.IsIdentity: false
            } implicitConversion
            && NumericBoundaryAnalyzer.IsNumericType(implicitConversion.Type)
            && _analyzer.TryMaterializeConstant(node, out var materialized))
        {
            _stats.ConstantsMaterialized++;
            return Finalize(node, materialized);
        }

        return visited;
    }

    private bool TryMaterializeSite(ExpressionSyntax node, out ExpressionSyntax materialized)
    {
        materialized = null!;
        if ((_analyzer.IsWithinExplicitNumericScope || _analyzer.ProjectDefaultChecked)
            && _analyzer.TryMaterializeConstant(node, out materialized)
            && !string.Equals(
                materialized.ToString().Trim(),
                node.ToString().Trim(),
                StringComparison.Ordinal))
        {
            // (The textual-equality guard keeps idempotency: a second pass over
            // an already materialized node must not count as a change.)
            _stats.ConstantsMaterialized++;
            MadeChanges = true;
            materialized = materialized
                .WithTriviaFrom(node)
                .WithAdditionalAnnotations(NB.RewriteAnnotation);
            return true;
        }

        materialized = null!;
        return false;
    }

    // ── postfix hoist post-pass ──

    /// <summary>
    /// After the full traversal, splice every hoisted postfix group into the
    /// statement list that hosts the enclosing statement:
    /// <c>var __cs2j_nb_n = target; target = &lt;new&gt;; … original statement …</c>.
    /// Declarations and assignments interleave in evaluation (source) order so
    /// several postfix sites in one statement keep C# sequencing semantics
    /// (decision 9). Embedded/global statement slots receive a wrapping bare
    /// block instead.
    /// </summary>
    public SyntaxNode ApplyHoists(SyntaxNode root)
    {
        if (_hoistPlans.Count == 0)
        {
            return root;
        }

        var placeholders = root
            .GetAnnotatedNodes(NB.PostfixHoistAnnotationKind)
            .OfType<IdentifierNameSyntax>()
            .ToList();
        if (placeholders.Count == 0)
        {
            return root;
        }

        var planByPlaceholder = new Dictionary<SyntaxNode, HoistPlan>();
        foreach (var placeholder in placeholders)
        {
            var plan = _hoistPlans.FirstOrDefault(p => placeholder.HasAnnotation(p.Marker));
            if (plan != null)
            {
                planByPlaceholder[placeholder] = plan;
            }
        }

        // Group placeholders by their enclosing statement (new tree).
        var groups = planByPlaceholder
            .GroupBy(pair => pair.Key.Ancestors().OfType<StatementSyntax>().FirstOrDefault())
            .Where(g => g.Key != null)
            .Select(g => new HoistGroup(
                g.Key!,
                g.OrderBy(p => p.Key.SpanStart).Select(p => p.Value).ToList()))
            .ToList();

        var plansByStatement = groups.ToDictionary(g => g.Statement);

        // Rebuild every parent hosting one or more groups directly, so no
        // post-traversal splice pass is required.
        var parents = groups
            .Select(g => g.Statement.Parent)
            .Where(p => p != null)
            .Distinct()
            .Cast<SyntaxNode>()
            .ToList();

        return root.ReplaceNodes(parents, (parent, _) =>
        {
            if (parent is BlockSyntax parentBlock)
            {
                return parentBlock.WithStatements(SyntaxFactory.List(
                    ExpandStatements(parentBlock.Statements, plansByStatement)));
            }

            if (parent is SwitchSectionSyntax parentSection)
            {
                return parentSection.WithStatements(SyntaxFactory.List(
                    ExpandStatements(parentSection.Statements, plansByStatement)));
            }

            // Embedded/global statement slot (if/while/for/using/GlobalStatement…):
            // the parent holds exactly one group-bearing statement; wrap it.
            var group = groups.Single(g => g.Statement.Parent == parent);
            var block = WrapInBlock(group);
            return parent.ReplaceNode(group.Statement, block);
        });
    }

    private sealed record HoistGroup(StatementSyntax Statement, List<HoistPlan> Plans);

    private static IEnumerable<StatementSyntax> ExpandStatements(
        SyntaxList<StatementSyntax> statements,
        IReadOnlyDictionary<StatementSyntax, HoistGroup> plansByStatement)
    {
        foreach (var statement in statements)
        {
            if (plansByStatement.TryGetValue(statement, out var group))
            {
                foreach (var hoisted in BuildHoistSequence(group, statement))
                {
                    yield return hoisted;
                }
            }
            else
            {
                yield return statement;
            }
        }
    }

    private static IEnumerable<StatementSyntax> BuildHoistSequence(
        HoistGroup group, StatementSyntax originalStatement)
    {
        foreach (var plan in group.Plans)
        {
            yield return SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.IdentifierName("var")
                        .WithTrailingTrivia(SyntaxFactory.Space),
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator(plan.TempName)
                            .WithInitializer(SyntaxFactory.EqualsValueClause(plan.TargetRead)))))
                .WithAdditionalAnnotations(NB.RewriteAnnotation);

            yield return SyntaxFactory.ExpressionStatement(plan.Assignment)
                .WithAdditionalAnnotations(NB.RewriteAnnotation);
        }

        yield return originalStatement.WithoutLeadingTrivia()
            .WithLeadingTrivia(SyntaxFactory.ElasticMarker);
    }

    private static BlockSyntax WrapInBlock(HoistGroup group)
    {
        var statement = group.Statement;
        var sequence = new List<StatementSyntax>();
        foreach (var plan in group.Plans)
        {
            sequence.Add(SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.IdentifierName("var")
                        .WithTrailingTrivia(SyntaxFactory.Space),
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator(plan.TempName)
                            .WithInitializer(SyntaxFactory.EqualsValueClause(plan.TargetRead))))));
            sequence.Add(SyntaxFactory.ExpressionStatement(plan.Assignment));
        }

        sequence.Add(statement.WithoutLeadingTrivia());
        return SyntaxFactory.Block(sequence)
            .WithLeadingTrivia(statement.GetLeadingTrivia())
            .WithAdditionalAnnotations(NB.RewriteAnnotation);
    }

    // ── helpers ──

    private void RecordHelper(NB.NumericDomain domain)
    {
        var key = NB.DomainSuffix(domain);
        _stats.HelpersByDomain[key] = _stats.HelpersByDomain.GetValueOrDefault(key) + 1;
    }

    private void ReportError(SyntaxNode site, string code)
    {
        var message = code switch
        {
            PreprocessDiagnosticCodes.NumericBoundaryUnsupportedType =>
                "checked arithmetic/conversion uses an unsupported numeric type " +
                "(Half/Int128/UInt128/nint/nuint/IntPtr); numeric-boundary cannot express this in Java yet.",
            PreprocessDiagnosticCodes.NumericBoundaryFloatingToInteger =>
                "checked floating-point/decimal to integer conversion cannot be translated 1:1 " +
                "(NaN/overflow semantics differ between .NET and Java).",
            PreprocessDiagnosticCodes.NumericBoundaryDynamic =>
                "dynamically bound checked site cannot be classified to an integer static domain.",
            PreprocessDiagnosticCodes.NumericBoundaryExpressionTree =>
                "checked site inside an expression-tree lambda (Expression<TDelegate>) cannot be rewritten.",
            PreprocessDiagnosticCodes.NumericBoundaryComplexIncrement =>
                "complex ++/-- or compound target in checked context (property/indexer/event/decimal, " +
                "or a postfix in an expression context that cannot host a hoisted temporary).",
            PreprocessDiagnosticCodes.NumericBoundaryNullableLifted =>
                "nullable lifted checked arithmetic cannot be translated yet.",
            _ => "unsupported checked site.",
        };

        var (line, column) = NumericBoundaryAnalyzer.GetLineColumn(site);
        _diagnostics.Add(new NumericBoundaryDiagnostic(
            NumericBoundarySeverity.Error, code, message, line, column));
        _stats.ErrorsByReason[code] = _stats.ErrorsByReason.GetValueOrDefault(code) + 1;
    }

    private ExpressionSyntax Finalize(SyntaxNode original, ExpressionSyntax replacement)
    {
        MadeChanges = true;
        return replacement
            .WithTriviaFrom(original)
            .WithAdditionalAnnotations(NB.RewriteAnnotation);
    }

    private ExpressionSyntax StripShell(CheckedExpressionSyntax node, ExpressionSyntax visitedInner)
    {
        MadeChanges = true;
        return NB.ParenthesizeIfNeeded(visitedInner)
            .WithTriviaFrom(node)
            .WithAdditionalAnnotations(NB.RewriteAnnotation);
    }

    private static ExpressionSyntax ParseTarget(string targetText)
        => SyntaxFactory.ParseExpression(targetText.Trim());

    private static ExpressionSyntax UnwrapParentheses(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression;
    }

    private static bool MustKeepBraces(CheckedStatementSyntax node, BlockSyntax block)
    {
        if (block.Statements.Any(s => s.IsKind(SyntaxKind.LocalDeclarationStatement)))
        {
            return true;
        }

        return node.Parent switch
        {
            BlockSyntax => false,
            SwitchSectionSyntax => false,
            _ => true,
        };
    }

    private sealed record HoistPlan(
        SyntaxAnnotation Marker,
        string TempName,
        ExpressionSyntax TargetRead,
        ExpressionSyntax Assignment);
}
