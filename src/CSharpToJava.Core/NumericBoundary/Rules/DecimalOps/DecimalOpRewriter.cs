using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Preprocessing;
using NB = CSharpToJava.Core.NumericBoundary.NumericBoundarySyntaxFacts;
using NBOp = Microsoft.CodeAnalysis.Operations;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.NumericBoundary.Rules.DecimalOps;

/// <summary>
/// C34 single bottom-up pass: reduces built-in <see cref="decimal"/> operator
/// sites to explicit BCL calls (DC-1~DC-11), marks const-decimal downgrades
/// (DC-12, applied by <see cref="ApplyConstDowngrades"/>) and drives statistics.
/// Lifted (Nullable&lt;decimal&gt;) and user-defined operator sites are never
/// touched. Any Error diagnostic rolls the whole file back in the driver.
/// </summary>
internal sealed class DecimalOpRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly NumericBoundaryOptions _options;
    private readonly DecimalOpStatistics _stats;
    private readonly List<NumericBoundaryDiagnostic> _diagnostics;
    private readonly HashSet<SyntaxNode> _downgradeTargets;
    private readonly List<HoistPlan> _hoistPlans = new();
    private TempNameAllocator? _names;

    public DecimalOpRewriter(
        SemanticModel model,
        NumericBoundaryOptions options,
        DecimalOpStatistics stats,
        List<NumericBoundaryDiagnostic> diagnostics,
        HashSet<SyntaxNode>? downgradeTargets = null)
    {
        _model = model;
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
        _downgradeTargets = downgradeTargets ?? new HashSet<SyntaxNode>();
    }

    /// <summary>
    /// Temporary-name allocator, created on first use: it reserves every
    /// identifier already present in the tree (a full token pass), which is pure
    /// overhead for the many files that never hoist a complex compound target.
    /// </summary>
    private TempNameAllocator Names => _names ??= new TempNameAllocator(_model.SyntaxTree.GetRoot());

    public bool MadeChanges { get; private set; }

    // ── DC-12 markers: tag const-decimal declarators planned for downgrade ──

    public override SyntaxNode? VisitVariableDeclarator(VariableDeclaratorSyntax node)
    {
        var visited = (VariableDeclaratorSyntax)base.VisitVariableDeclarator(node)!;
        return _downgradeTargets.Contains(node)
            ? visited.WithAdditionalAnnotations(new SyntaxAnnotation(DecimalOpSyntaxFacts.ConstDowngradeAnnotationKind))
            : visited;
    }

    // ── DC-1~DC-7: binary arithmetic and comparisons ──

    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        var visited = (ExpressionSyntax)base.VisitBinaryExpression(node)!;

        var arithmeticMethod = DecimalOpSyntaxFacts.BinaryArithmeticMethod(node.Kind());
        var isComparison = DecimalOpSyntaxFacts.IsComparisonKind(node.Kind());
        if (arithmeticMethod == null && !isComparison)
        {
            return visited;
        }

        var siteText = SiteText(node);

        if (DecimalTypeFacts.IsWithinExpressionTree(_model, node) && IsDecimalOperatorSymbol(node))
        {
            ReportError(node, PreprocessDiagnosticCodes.DecimalOpsExpressionTree,
                DecimalOpDiagnostics.ExpressionTree(siteText));
            return visited;
        }

        var classification = DecimalTypeFacts.ClassifyOperation(UnwrapConversion(_model.GetOperation(node)));
        switch (classification)
        {
            case DecimalTypeFacts.SiteClassification.Lifted:
                _stats.AddDeferred(DecimalOpDiagnostics.ReasonLifted);
                return visited;
            case DecimalTypeFacts.SiteClassification.UserOperator:
                _stats.AddDeferred(DecimalOpDiagnostics.ReasonUserOperator);
                return visited;
            case DecimalTypeFacts.SiteClassification.Dynamic:
                ReportError(node, PreprocessDiagnosticCodes.DecimalOpsDynamic,
                    DecimalOpDiagnostics.Dynamic(siteText));
                return visited;
            case DecimalTypeFacts.SiteClassification.NotOwned:
                return visited;
        }

        var visitedBinary = (BinaryExpressionSyntax)visited;
        if (!TryPromote(node.Left, visitedBinary.Left, siteText, out var left))
        {
            return visited;
        }

        if (!TryPromote(node.Right, visitedBinary.Right, siteText, out var right))
        {
            return visited;
        }

        ExpressionSyntax rewritten;
        if (isComparison)
        {
            rewritten = DecimalOpSyntaxFacts.DecimalComparison(node.Kind(), left, right);
            if (node.Kind() is SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression)
            {
                _stats.EqualityComparisonSites++;
            }
            else
            {
                _stats.RelationalComparisonSites++;
            }
        }
        else
        {
            rewritten = DecimalOpSyntaxFacts.DecimalStaticCall(arithmeticMethod!, left, right);
            _stats.AddBinary(BinaryBucketKey(arithmeticMethod!));
        }

        return Finalize(node, rewritten);
    }

    // ── DC-8: unary minus (Negate) and unary plus (strip) ──

    public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
    {
        var visited = (ExpressionSyntax)base.VisitPrefixUnaryExpression(node)!;

        if (node.Kind() is not (SyntaxKind.UnaryMinusExpression or SyntaxKind.UnaryPlusExpression))
        {
            return visited;
        }

        var siteText = SiteText(node);
        if (DecimalTypeFacts.IsWithinExpressionTree(_model, node) && IsDecimalOperatorSymbol(node))
        {
            ReportError(node, PreprocessDiagnosticCodes.DecimalOpsExpressionTree,
                DecimalOpDiagnostics.ExpressionTree(siteText));
            return visited;
        }

        var classification = DecimalTypeFacts.ClassifyOperation(UnwrapConversion(_model.GetOperation(node)));
        switch (classification)
        {
            case DecimalTypeFacts.SiteClassification.Lifted:
                _stats.AddDeferred(DecimalOpDiagnostics.ReasonLifted);
                return visited;
            case DecimalTypeFacts.SiteClassification.UserOperator:
                _stats.AddDeferred(DecimalOpDiagnostics.ReasonUserOperator);
                return visited;
            case DecimalTypeFacts.SiteClassification.Dynamic:
                ReportError(node, PreprocessDiagnosticCodes.DecimalOpsDynamic,
                    DecimalOpDiagnostics.Dynamic(siteText));
                return visited;
            case DecimalTypeFacts.SiteClassification.NotOwned:
                return visited;
        }

        var visitedUnary = (PrefixUnaryExpressionSyntax)visited;
        if (node.Kind() == SyntaxKind.UnaryPlusExpression)
        {
            // Unary + is an identity: strip it.
            _stats.UnaryPlusStrippedSites++;
            return Finalize(node, visitedUnary.Operand);
        }

        _stats.UnaryNegateSites++;
        return Finalize(node, DecimalOpSyntaxFacts.DecimalNegateCall(visitedUnary.Operand));
    }

    // ── DC-10/DC-11: compound assignments ──

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        var visited = (ExpressionSyntax)base.VisitAssignmentExpression(node)!;

        var method = DecimalOpSyntaxFacts.CompoundArithmeticMethod(node.Kind());
        if (method == null)
        {
            return visited;
        }

        var siteText = SiteText(node);
        if (DecimalTypeFacts.IsWithinExpressionTree(_model, node) && IsDecimalOperatorSymbol(node))
        {
            ReportError(node, PreprocessDiagnosticCodes.DecimalOpsExpressionTree,
                DecimalOpDiagnostics.ExpressionTree(siteText));
            return visited;
        }

        var operation = UnwrapConversion(_model.GetOperation(node));
        var classification = DecimalTypeFacts.ClassifyOperation(operation);
        switch (classification)
        {
            case DecimalTypeFacts.SiteClassification.Lifted:
                _stats.AddDeferred(DecimalOpDiagnostics.ReasonLifted);
                return visited;
            case DecimalTypeFacts.SiteClassification.UserOperator:
                _stats.AddDeferred(DecimalOpDiagnostics.ReasonUserOperator);
                return visited;
            case DecimalTypeFacts.SiteClassification.Dynamic:
                ReportError(node, PreprocessDiagnosticCodes.DecimalOpsDynamic,
                    DecimalOpDiagnostics.Dynamic(siteText));
                return visited;
            case DecimalTypeFacts.SiteClassification.NotOwned:
                // Event +=/-= and every non-decimal compound fall through here.
                return visited;
        }

        var visitedAssignment = (AssignmentExpressionSyntax)visited;
        if (!TryPromote(node.Right, visitedAssignment.Right, siteText, out var promotedRight))
        {
            return visited;
        }

        if (DecimalTypeFacts.ClassifyTargetShape(_model, node.Left) == DecimalTypeFacts.TargetShape.Simple)
        {
            // DC-10: read and write in place.
            var rewritten = AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                visitedAssignment.Left,
                DecimalOpSyntaxFacts.DecimalStaticCall(method, visitedAssignment.Left, promotedRight));
            _stats.SimpleCompoundSites++;
            return Finalize(node, rewritten);
        }

        // DC-11: complex target requires single-evaluation hoisting.
        var anchor = DecimalOpAnalyzer.ClassifyAnchor(node);
        if (anchor == DecimalOpAnalyzer.AnchorKind.None)
        {
            ReportError(node, PreprocessDiagnosticCodes.DecimalOpsNoAnchor,
                DecimalOpDiagnostics.NoAnchor(siteText));
            _stats.AddDeferred(DecimalOpDiagnostics.ReasonNoAnchor);
            return visited;
        }

        return RewriteComplexCompound(
            node,
            visitedAssignment,
            method!,
            promotedRight,
            anchor == DecimalOpAnalyzer.AnchorKind.Standalone,
            siteText);
    }

    private ExpressionSyntax RewriteComplexCompound(
        AssignmentExpressionSyntax original,
        AssignmentExpressionSyntax visited,
        string method,
        ExpressionSyntax promotedRight,
        bool standalone,
        string siteText)
    {
        var prefix = new List<StatementSyntax>();
        var originalLeft = original.Left;
        var visitedLeft = visited.Left;

        // Standalone sites are expanded inside their own bare block, so each
        // site allocates from a fresh counter (identical receivers reuse the
        // canonical temp name without redeclaration conflicts). Consumed sites
        // hoist into the enclosing block and keep using the global allocator.
        var names = standalone
            ? new TempNameAllocator((CompilationUnitSyntax)_model.SyntaxTree.GetRoot())
            : Names;

        ExpressionSyntax receiverAccess;
        SimpleNameSyntax? memberName = null;
        BracketedArgumentListSyntax? elementArguments = null;

        switch (visitedLeft)
        {
            case MemberAccessExpressionSyntax member:
            {
                memberName = member.Name;
                if (originalLeft is MemberAccessExpressionSyntax originalMember
                    && (DecimalTypeFacts.IsStaticMember(_model, originalLeft)
                        || originalMember.Expression is ThisExpressionSyntax or BaseExpressionSyntax))
                {
                    receiverAccess = member.Expression;
                }
                else
                {
                    var temp = names.Receiver();
                    _stats.AddTemp(DecimalOpDiagnostics.TempRoleReceiver);
                    prefix.Add(VarDeclaration(temp, member.Expression));
                    receiverAccess = IdentifierName(temp);
                }

                break;
            }

            case ElementAccessExpressionSyntax element:
            {
                if (originalLeft is ElementAccessExpressionSyntax originalElement
                    && originalElement.Expression is ThisExpressionSyntax or BaseExpressionSyntax)
                {
                    receiverAccess = element.Expression;
                }
                else
                {
                    var temp = names.Receiver();
                    _stats.AddTemp(DecimalOpDiagnostics.TempRoleReceiver);
                    prefix.Add(VarDeclaration(temp, element.Expression));
                    receiverAccess = IdentifierName(temp);
                }

                var rebuiltArguments = new List<ArgumentSyntax>();
                var argIndex = 0;
                foreach (var argument in element.ArgumentList.Arguments)
                {
                    var argTemp = names.NextArgument();
                    _stats.AddTemp(DecimalOpDiagnostics.TempRoleIndexArg);
                    prefix.Add(VarDeclaration(argTemp, argument.Expression));
                    rebuiltArguments.Add(Argument(argument.NameColon, argument.RefKindKeyword, IdentifierName(argTemp)));
                    argIndex++;
                }

                elementArguments = BracketedArgumentList(SeparatedList(rebuiltArguments));
                break;
            }

            default:
                // Should be unreachable: complex classification only for the shapes above.
                ReportError(original, PreprocessDiagnosticCodes.DecimalOpsNoAnchor,
                    DecimalOpDiagnostics.NoAnchor(siteText));
                return visited;
        }

        var readAccess = BuildAccess(receiverAccess, memberName, elementArguments);
        var writeAccess = BuildAccess(receiverAccess, memberName, elementArguments);

        var writeStatement = ExpressionStatement(AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            writeAccess,
            DecimalOpSyntaxFacts.DecimalStaticCall(method, readAccess, promotedRight)))
            .WithAdditionalAnnotations(NB.RewriteAnnotation);

        LocalDeclarationStatementSyntax? valueRead = null;
        string valueName;
        if (standalone)
        {
            // Placeholder is dropped together with the original expression statement.
            valueName = names.Value();
        }
        else
        {
            valueName = names.Value();
            _stats.AddTemp(DecimalOpDiagnostics.TempRoleValue);
            valueRead = VarDeclaration(valueName, BuildAccess(receiverAccess, memberName, elementArguments))
                .WithAdditionalAnnotations(NB.RewriteAnnotation);
        }

        var marker = new SyntaxAnnotation(DecimalOpSyntaxFacts.HoistAnnotationKind);
        _hoistPlans.Add(new HoistPlan(marker, standalone, prefix, writeStatement, valueRead));

        _stats.ComplexCompoundSites++;
        MadeChanges = true;
        return IdentifierName(valueName)
            .WithTriviaFrom(original)
            .WithAdditionalAnnotations(marker, NB.RewriteAnnotation, DecimalOpSyntaxFacts.RewriteAnnotation);
    }

    private static ExpressionSyntax BuildAccess(
        ExpressionSyntax receiverAccess,
        SimpleNameSyntax? memberName,
        BracketedArgumentListSyntax? elementArguments)
    {
        if (memberName != null)
        {
            return MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                receiverAccess,
                memberName);
        }

        return ElementAccessExpression(receiverAccess, elementArguments!);
    }

    // ── operand promotion (DC-9) ──

    private bool TryPromote(
        ExpressionSyntax originalOperand,
        ExpressionSyntax visitedOperand,
        string siteText,
        out ExpressionSyntax promoted)
    {
        var domain = DecimalTypeFacts.ClassifyOperand(_model, originalOperand);
        if (domain == DecimalTypeFacts.OperandDomain.Other)
        {
            var typeName = _model.GetTypeInfo(originalOperand).Type?.ToDisplayString() ?? "unknown";
            ReportError(originalOperand, PreprocessDiagnosticCodes.DecimalOpsUnsupportedOperand,
                DecimalOpDiagnostics.UnsupportedOperand(siteText, typeName));
            promoted = visitedOperand;
            return false;
        }

        var needsUnsignedHelper = domain is
            DecimalTypeFacts.OperandDomain.NativeUInt32
            or DecimalTypeFacts.OperandDomain.NativeUInt64
            or DecimalTypeFacts.OperandDomain.WrappedUInt32
            or DecimalTypeFacts.OperandDomain.WrappedUInt64;
        if (needsUnsignedHelper)
        {
            var method = domain is DecimalTypeFacts.OperandDomain.NativeUInt32
                or DecimalTypeFacts.OperandDomain.WrappedUInt32
                ? "ToDecimalUInt32"
                : "ToDecimalUInt64";
            if (!DecimalOpAnalyzer.HasUnsignedMathSupport(_model.Compilation, method))
            {
                var severity = _options.Strict
                    ? NumericBoundarySeverity.Error
                    : NumericBoundarySeverity.Warning;
                Report(originalOperand, severity, PreprocessDiagnosticCodes.DecimalOpsUnsignedSupportMissing,
                    DecimalOpDiagnostics.UnsignedSupportMissing("Cs2jUnsigned.UnsignedMath." + method));
                if (severity == NumericBoundarySeverity.Error)
                {
                    promoted = visitedOperand;
                    return false;
                }
            }
        }

        promoted = DecimalOpSyntaxFacts.PromoteOperand(domain, visitedOperand);
        switch (domain)
        {
            case DecimalTypeFacts.OperandDomain.LiteralInteger:
            case DecimalTypeFacts.OperandDomain.SignedOrNarrowInt:
                _stats.CastPromotionSites++;
                break;
            case DecimalTypeFacts.OperandDomain.NativeUInt32:
            case DecimalTypeFacts.OperandDomain.WrappedUInt32:
                _stats.ToDecimalUInt32PromotionSites++;
                break;
            case DecimalTypeFacts.OperandDomain.NativeUInt64:
            case DecimalTypeFacts.OperandDomain.WrappedUInt64:
                _stats.ToDecimalUInt64PromotionSites++;
                break;
        }

        return true;
    }

    private bool IsDecimalOperatorSymbol(SyntaxNode node)
    {
        var symbol = _model.GetSymbolInfo(node).Symbol as IMethodSymbol;
        return DecimalTypeFacts.IsBuiltInDecimalOperator(symbol);
    }

    private static IOperation? UnwrapConversion(IOperation? operation) =>
        operation is NBOp.IConversionOperation conversion ? conversion.Operand : operation;

    // ── hoist post-pass (DC-11) ──

    public SyntaxNode ApplyHoists(SyntaxNode root)
    {
        if (_hoistPlans.Count == 0)
        {
            return root;
        }

        var placeholders = root
            .GetAnnotatedNodes(DecimalOpSyntaxFacts.HoistAnnotationKind)
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

        var groups = planByPlaceholder
            .GroupBy(pair => pair.Key.Ancestors().OfType<StatementSyntax>().FirstOrDefault())
            .Where(g => g.Key != null)
            .Select(g => new HoistGroup(
                g.Key!,
                g.OrderBy(p => p.Key.SpanStart).Select(p => p.Value).ToList()))
            .ToList();

        // Expression-bodied member anchors: placeholders with no enclosing
        // statement live directly under an ArrowExpressionClauseSyntax.
        var arrowGroups = planByPlaceholder
            .Where(pair => pair.Key.Ancestors().OfType<StatementSyntax>().FirstOrDefault() == null)
            .GroupBy(pair => pair.Key.Ancestors().OfType<ArrowExpressionClauseSyntax>().FirstOrDefault())
            .Where(g => g.Key != null)
            .Select(g => new ArrowHoistGroup(
                g.Key!,
                g.OrderBy(p => p.Key.SpanStart).Select(p => p.Value).ToList()))
            .ToList();

        var plansByStatement = groups.ToDictionary(g => g.Statement);
        var parents = groups
            .Select(g => g.Statement.Parent)
            .Concat(arrowGroups.Select(g => (SyntaxNode?)g.Arrow.Parent))
            .Where(p => p != null)
            .Distinct()
            .Cast<SyntaxNode>()
            .ToList();

        return root.ReplaceNodes(parents, (parent, _) =>
        {
            if (parent is BlockSyntax parentBlock)
            {
                return parentBlock.WithStatements(List(ExpandStatements(parentBlock.Statements, plansByStatement)));
            }

            if (parent is SwitchSectionSyntax parentSection)
            {
                return parentSection.WithStatements(List(ExpandStatements(parentSection.Statements, plansByStatement)));
            }

            var arrowGroup = arrowGroups.FirstOrDefault(g => g.Arrow.Parent == parent);
            if (arrowGroup != null)
            {
                return ConvertArrowMember(arrowGroup);
            }

            // Embedded statement slot: consumed anchor only (a standalone compound
            // is itself an expression statement living in a block/section).
            var group = groups.Single(g => g.Statement.Parent == parent);
            var block = WrapInBlock(group);
            return parent.ReplaceNode(group.Statement, block);
        });
    }

    private sealed record HoistGroup(StatementSyntax Statement, List<HoistPlan> Plans);

    private sealed record ArrowHoistGroup(ArrowExpressionClauseSyntax Arrow, List<HoistPlan> Plans);

    /// <summary>
    /// Convert an expression-bodied member holding hoisted compounds into a
    /// block-bodied one: prelude temps, the writes, then either a value read
    /// plus <c>return</c> (value-returning members) or just the writes (void).
    /// </summary>
    private static SyntaxNode ConvertArrowMember(ArrowHoistGroup group)
    {
        var statements = new List<StatementSyntax>();
        var yieldsValue = DecimalOpAnalyzer.ArrowYieldsValue(group.Arrow);

        if (!yieldsValue)
        {
            // Void members discard the arrow expression; each standalone plan
            // expands into its own bare block (per-site temp scope).
            foreach (var plan in group.Plans)
            {
                var scoped = new List<StatementSyntax>(plan.PreludeStatements) { plan.WriteStatement };
                statements.Add(Block(scoped)
                    .WithAdditionalAnnotations(NB.RewriteAnnotation, DecimalOpSyntaxFacts.RewriteAnnotation));
            }
        }
        else
        {
            foreach (var plan in group.Plans)
            {
                statements.AddRange(plan.PreludeStatements);
            }

            foreach (var plan in group.Plans)
            {
                statements.Add(plan.WriteStatement);
            }
            foreach (var plan in group.Plans)
            {
                if (plan.ValueReadStatement != null)
                {
                    statements.Add(plan.ValueReadStatement);
                }
            }

            statements.Add(ReturnStatement(group.Arrow.Expression)
                .WithAdditionalAnnotations(NB.RewriteAnnotation));
        }

        var block = Block(statements)
            .WithLeadingTrivia(group.Arrow.GetLeadingTrivia())
            .WithAdditionalAnnotations(NB.RewriteAnnotation, DecimalOpSyntaxFacts.RewriteAnnotation);
        var none = Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.None);

        return group.Arrow.Parent switch
        {
            MethodDeclarationSyntax method =>
                method.WithExpressionBody(null).WithBody(block).WithSemicolonToken(none),
            OperatorDeclarationSyntax @operator =>
                @operator.WithExpressionBody(null).WithBody(block).WithSemicolonToken(none),
            ConversionOperatorDeclarationSyntax conversion =>
                conversion.WithExpressionBody(null).WithBody(block).WithSemicolonToken(none),
            AccessorDeclarationSyntax accessor =>
                accessor.WithExpressionBody(null).WithBody(block).WithSemicolonToken(none),
            PropertyDeclarationSyntax property =>
                property.WithExpressionBody(null)
                    .WithSemicolonToken(none)
                    .WithAccessorList(AccessorList(SingletonList(
                        AccessorDeclaration(Microsoft.CodeAnalysis.CSharp.SyntaxKind.GetAccessorDeclaration)
                            .WithBody(block)
                            .WithSemicolonToken(none)))),
            IndexerDeclarationSyntax indexer =>
                indexer.WithExpressionBody(null)
                    .WithSemicolonToken(none)
                    .WithAccessorList(AccessorList(SingletonList(
                        AccessorDeclaration(Microsoft.CodeAnalysis.CSharp.SyntaxKind.GetAccessorDeclaration)
                            .WithBody(block)
                            .WithSemicolonToken(none)))),
            _ => group.Arrow.Parent!,
        };
    }

    private static IEnumerable<StatementSyntax> ExpandStatements(
        SyntaxList<StatementSyntax> statements,
        IReadOnlyDictionary<StatementSyntax, HoistGroup> plansByStatement)
    {
        foreach (var statement in statements)
        {
            if (!plansByStatement.TryGetValue(statement, out var group))
            {
                yield return statement;
                continue;
            }

            // Plans arrive in source order (group sorts placeholders by span).
            if (group.Plans.All(p => p.Standalone))
            {
                // Standalone compounds replace the original expression
                // statement; the hoisted sequence gets its own bare block so
                // per-site temporary names never collide across sibling sites.
                var scoped = new List<StatementSyntax>();
                foreach (var plan in group.Plans)
                {
                    scoped.AddRange(plan.PrefixStatements);
                }

                yield return Block(scoped)
                    .WithLeadingTrivia(statement.GetLeadingTrivia())
                    .WithAdditionalAnnotations(NB.RewriteAnnotation, DecimalOpSyntaxFacts.RewriteAnnotation);
                continue;
            }

            foreach (var plan in group.Plans)
            {
                foreach (var prefix in plan.PrefixStatements)
                {
                    yield return prefix;
                }
            }

            yield return statement.WithoutLeadingTrivia()
                .WithLeadingTrivia(ElasticMarker);
        }
    }

    private static BlockSyntax WrapInBlock(HoistGroup group)
    {
        var sequence = new List<StatementSyntax>();
        foreach (var plan in group.Plans)
        {
            sequence.AddRange(plan.PrefixStatements);
        }

        sequence.Add(group.Statement.WithoutLeadingTrivia());
        return Block(sequence)
            .WithLeadingTrivia(group.Statement.GetLeadingTrivia())
            .WithAdditionalAnnotations(NB.RewriteAnnotation, DecimalOpSyntaxFacts.RewriteAnnotation);
    }

    // ── DC-13 residual gate ──

    /// <summary>
    /// Rescan the output tree: every user-scope node still binding a built-in
    /// <see cref="decimal"/> operator is a violation (CS2J5367, strict → Error).
    /// Lifted and user-operator sites are whitelisted, as are sites inside
    /// expression-tree lambdas (handled upstream).
    /// </summary>
    public static void ApplyResidualGate(
        SemanticModel model,
        SyntaxNode root,
        NumericBoundaryOptions options,
        DecimalOpStatistics stats,
        List<NumericBoundaryDiagnostic> diagnostics)
    {
        var residuals = new List<string>();
        foreach (var node in root.DescendantNodes())
        {
            var isCandidate = node switch
            {
                BinaryExpressionSyntax binary =>
                    DecimalOpSyntaxFacts.BinaryArithmeticMethod(binary.Kind()) != null
                    || DecimalOpSyntaxFacts.IsComparisonKind(binary.Kind()),
                PrefixUnaryExpressionSyntax prefix =>
                    prefix.Kind() is SyntaxKind.UnaryMinusExpression or SyntaxKind.UnaryPlusExpression,
                AssignmentExpressionSyntax assignment =>
                    DecimalOpSyntaxFacts.CompoundArithmeticMethod(assignment.Kind()) != null,
                _ => false,
            };
            if (!isCandidate || DecimalTypeFacts.IsWithinExpressionTree(model, node))
            {
                continue;
            }

            var operation = node switch
            {
                BinaryExpressionSyntax b => model.GetOperation(b),
                PrefixUnaryExpressionSyntax u => model.GetOperation(u),
                AssignmentExpressionSyntax a => model.GetOperation(a),
                _ => null,
            };
            operation = operation is NBOp.IConversionOperation conversion ? conversion.Operand : operation;

            if (DecimalTypeFacts.ClassifyOperation(operation) == DecimalTypeFacts.SiteClassification.Owned)
            {
                stats.ResidualDecimalOperators++;
                if (residuals.Count < 5)
                {
                    residuals.Add(SiteText(node));
                }
            }
        }

        if (residuals.Count == 0)
        {
            return;
        }

        var severity = options.Strict ? NumericBoundarySeverity.Error : NumericBoundarySeverity.Warning;
        diagnostics.Add(new NumericBoundaryDiagnostic(
            severity,
            PreprocessDiagnosticCodes.DecimalOpsResidualOperator,
            DecimalOpDiagnostics.ResidualOperator(string.Join(", ", residuals))));
    }

    // ── small helpers ──

    private static LocalDeclarationStatementSyntax VarDeclaration(string name, ExpressionSyntax initializer) =>
        LocalDeclarationStatement(
                VariableDeclaration(
                    IdentifierName("var").WithTrailingTrivia(Space),
                    SingletonSeparatedList(
                        VariableDeclarator(name)
                            .WithInitializer(EqualsValueClause(initializer)))))
            .WithAdditionalAnnotations(NB.RewriteAnnotation, DecimalOpSyntaxFacts.RewriteAnnotation);

    private ExpressionSyntax Finalize(SyntaxNode original, ExpressionSyntax replacement)
    {
        MadeChanges = true;
        return replacement
            .WithTriviaFrom(original)
            .WithAdditionalAnnotations(NB.RewriteAnnotation, DecimalOpSyntaxFacts.RewriteAnnotation);
    }

    private static string BinaryBucketKey(string method) => method switch
    {
        "Add" => "add",
        "Subtract" => "sub",
        "Multiply" => "mul",
        "Divide" => "div",
        "Remainder" => "rem",
        _ => method.ToLowerInvariant(),
    };

    private void ReportError(SyntaxNode site, string code, string message) =>
        Report(site, NumericBoundarySeverity.Error, code, message);

    private void Report(SyntaxNode site, NumericBoundarySeverity severity, string code, string message)
    {
        var (line, column) = DecimalTypeFacts.GetLineColumn(site);
        _diagnostics.Add(new NumericBoundaryDiagnostic(severity, code, message, line, column));
    }

    private static string SiteText(SyntaxNode node)
    {
        var text = node.ToString().Replace('\r', ' ').Replace('\n', ' ');
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 80 ? text : text[..77] + "...";
    }

    private sealed record HoistPlan(
        SyntaxAnnotation Marker,
        bool Standalone,
        List<StatementSyntax> PreludeStatements,
        ExpressionStatementSyntax WriteStatement,
        LocalDeclarationStatementSyntax? ValueReadStatement)
    {
        public IEnumerable<StatementSyntax> PrefixStatements
        {
            get
            {
                foreach (var statement in PreludeStatements)
                {
                    yield return statement;
                }

                yield return WriteStatement;
                if (ValueReadStatement != null)
                {
                    yield return ValueReadStatement;
                }
            }
        }
    }
}
