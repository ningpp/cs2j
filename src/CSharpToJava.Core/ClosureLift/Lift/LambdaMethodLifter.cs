using CSharpToJava.Core.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// Everything the <see cref="LambdaMethodLifter"/> needs to migrate one lambda
/// body into its display-class instance method. The planner freezes every map
/// (design §4.1); the stage fills the received-span coordinate dictionaries.
/// </summary>
internal sealed class LambdaMigrationContext
{
    public required LambdaMethodPlan Plan { get; init; }
    public required ResolvedLambdaSignature Signature { get; init; }
    public required LambdaExpressionSyntax ReceivedLambda { get; init; }

    /// <summary>Original name → relative field expression ("_cs2jCap_x" /
    /// "_cs2jParent._cs2jCap_x"); covers routine captures, ancestor captures
    /// and own promoted parameters/locals (D4/D5).</summary>
    public required IReadOnlyDictionary<string, string> FieldAccessByName { get; init; }

    /// <summary>Received C32 accesses `__cs2jClosureN._cs2jCap_x` → relative field expression.</summary>
    public required IReadOnlyDictionary<(string Instance, string Field), string> CollapseByInstanceField { get; init; }

    /// <summary>Host instance-member names used by the own body (D7).</summary>
    public required IReadOnlyCollection<string> InstanceMemberNames { get; init; }

    /// <summary>Explicit `this` occurs in the own body.</summary>
    public required bool CapturesThis { get; init; }

    /// <summary>The chain-top class carries the `_cs2jThis` field.</summary>
    public required bool HasHostThis { get; init; }

    /// <summary>`_cs2jParent.` repetitions to the chain top ("" at the top).</summary>
    public required string HostThisPath { get; init; }

    /// <summary>Own parameters promoted to fields (entry-assigned).</summary>
    public required IReadOnlySet<string> PromotedParameterNames { get; init; }

    /// <summary>Own-body locals promoted to fields (declaration converted to assignment).</summary>
    public required IReadOnlySet<string> PromotedLocalNames { get; init; }

    /// <summary>Direct child-group lambda sites (RECEIVED span coordinates).</summary>
    public required IReadOnlyDictionary<(int Start, int Kind), NestedDelegateSite> ChildSites { get; init; }

    /// <summary>Child group instance name → chain-qualified child type display.</summary>
    public required IReadOnlyDictionary<string, string> ChildTypeByInstance { get; init; }

    /// <summary>Planned static nested sites (RECEIVED span coordinates, M1).</summary>
    public required IReadOnlyDictionary<(int Start, int Kind), StaticLambdaPlan> StaticPlans { get; init; }

    /// <summary>D12 frozen named-method parameter slots (names; attributes come
    /// from the received lambda at build time).</summary>
    public required IReadOnlyList<ParameterSlot> ParameterSlots { get; init; }
}

/// <summary>
/// C45 M2/M3 body migration (design §4: Lift/LambdaMethodLifter; D4/D9/D21/D22/D5/D7).
/// Builds the display-class instance method `Lambda_&lt;k&gt;` from the RECEIVED-tree
/// lambda: the body moves verbatim (block or expression shape; lock/using/await
/// statements keep their structure, D22) with these rewrites applied during the
/// move:
///   ① C32 field accesses `__cs2jClosure&lt;n&gt;._cs2jCap_x` collapse to the field
///      access relative to the home class — bare at the chain top,
///      `_cs2jParent(.…)._cs2jCap_x` inside nested classes (D4);
///   ② captured reads/writes (D5/D4) become field accesses matched by ORIGINAL
///      NAME — CS0136 forbids shadowing a captured name inside the lambda body;
///   ③ explicit `this` (D7) becomes `_cs2jThis` (chain-prefixed when nested);
///   ④ implicit-this instance-member references become `_cs2jThis.Member`;
///   ⑤ nested planned lambdas are substituted with explicit delegate
///      constructions while the body still hangs off the received tree, and
///      their child display instances are created at the enclosing block entry
///      (foreach bodies naturally make this per-iteration, D4);
///   ⑥ own parameters captured by nested lambdas are entry-assigned to fields
///      and own-body locals captured by nested lambdas become field assignments
///      at their declaration sites.
/// Expression-bodied lambdas wrap into a block whenever ⑤/⑥ needs statements.
/// `base` access never reaches the lifter (CS2J4509 freezes the file in the
/// planner); quoted/non-planned nested lambdas stay verbatim subtrees.
/// </summary>
internal static class LambdaMethodLifter
{
    public static MethodDeclarationSyntax BuildMethod(LambdaMigrationContext context)
    {
        var plan = context.Plan;
        var signature = context.Signature;
        var receivedLambda = context.ReceivedLambda;

        var parameters = ParameterNormalizer.BuildMethodParameters(
            receivedLambda, signature, context.ParameterSlots);

        var modifiers = new List<SyntaxToken>
        {
            SyntaxFactory.Token(SyntaxKind.InternalKeyword),
        };
        if (StaticLambdaHandler.UsesPointerSyntax(receivedLambda))
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.UnsafeKeyword));
        if (plan.IsAsync)
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.AsyncKeyword));

        var returnType = signature.ReturnTypeDisplay is { } type
            ? SyntaxFactory.ParseTypeName(type)
            : SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword));
        var isVoid = signature.ReturnTypeDisplay == null;

        var rewriter = new MigratedBodyRewriter(context);
        var entryAssignments = BuildEntryAssignments(context);

        BlockSyntax? blockBody = null;
        ArrowExpressionClauseSyntax? expressionBody = null;

        switch (receivedLambda)
        {
            case ParenthesizedLambdaExpressionSyntax { Block: { } parenthesizedBlock }:
                blockBody = FinalizeBlock(
                    (BlockSyntax)rewriter.Visit(parenthesizedBlock)!, entryAssignments, rewriter, isOuter: true);
                break;
            case SimpleLambdaExpressionSyntax { Block: { } simpleBlock }:
                blockBody = FinalizeBlock(
                    (BlockSyntax)rewriter.Visit(simpleBlock)!, entryAssignments, rewriter, isOuter: true);
                break;
            default:
                var sourceBody = receivedLambda switch
                {
                    ParenthesizedLambdaExpressionSyntax p => (SyntaxNode?)p.Block ?? p.ExpressionBody,
                    SimpleLambdaExpressionSyntax s => (SyntaxNode?)s.Block ?? s.ExpressionBody,
                    _ => throw new InvalidOperationException(
                        "C45: lambda without a block or expression body cannot be lifted."),
                };

                if (sourceBody is BlockSyntax sourceBlock)
                {
                    blockBody = FinalizeBlock(
                        (BlockSyntax)rewriter.Visit(sourceBlock)!, entryAssignments, rewriter, isOuter: true);
                    break;
                }

                var visitedExpression = (ExpressionSyntax)rewriter.Visit((ExpressionSyntax)sourceBody)!;
                var levelDeclarations = rewriter.TakeExpressionLevelDeclarations();
                if (entryAssignments.Count == 0 && levelDeclarations.Count == 0)
                {
                    expressionBody = SyntaxFactory.ArrowExpressionClause(visitedExpression);
                }
                else
                {
                    var statements = new List<StatementSyntax>();
                    statements.AddRange(entryAssignments);
                    statements.AddRange(levelDeclarations);
                    statements.Add(isVoid
                        ? SyntaxFactory.ExpressionStatement(visitedExpression)
                        : SyntaxFactory.ReturnStatement(visitedExpression));
                    blockBody = SyntaxFactory.Block(statements);
                }

                break;
        }

        var declaration = BuildDeclaration(
            plan.MethodName, modifiers, returnType, parameters, blockBody, expressionBody);
        return declaration.WithAdditionalAnnotations(ClosureLiftAnnotations.LiftedMethod(plan.MethodName));
    }

    /// <summary>
    /// Prepends promoted-parameter entry assignments to the outermost migrated
    /// block; per-block child instance declarations were spliced by the rewriter.
    /// </summary>
    private static BlockSyntax FinalizeBlock(
        BlockSyntax visited,
        List<StatementSyntax> entryAssignments,
        MigratedBodyRewriter rewriter,
        bool isOuter)
    {
        if (!isOuter || entryAssignments.Count == 0)
            return visited;

        return visited.WithStatements(
            SyntaxFactory.List(entryAssignments.Concat(visited.Statements)));
    }

    /// <summary>`_cs2jCap_p = p;` — promoted parameter entry assignments.</summary>
    private static List<StatementSyntax> BuildEntryAssignments(LambdaMigrationContext context)
    {
        var result = new List<StatementSyntax>();
        foreach (var name in context.PromotedParameterNames)
        {
            if (!context.FieldAccessByName.TryGetValue(name, out var fieldAccess))
                continue;

            result.Add(SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.ParseExpression(fieldAccess),
                    SyntaxFactory.IdentifierName(name))));
        }

        return result;
    }

    private sealed class MigratedBodyRewriter : CSharpSyntaxRewriter
    {
        private readonly LambdaMigrationContext _context;
        private readonly Stack<BlockSyntax> _blockStack = new();
        private readonly Dictionary<BlockSyntax, List<string>> _instancesByBlock = new();
        private readonly List<string> _expressionLevelInstances = new();
        private bool _outermostBlockDone;

        public MigratedBodyRewriter(LambdaMigrationContext context)
        {
            _context = context;
        }

        /// <summary>Child instance declarations recorded while visiting an expression body.</summary>
        public List<StatementSyntax> TakeExpressionLevelDeclarations()
            => _expressionLevelInstances
                .Distinct(StringComparer.Ordinal)
                .Select(name => DisplayClassSyntaxEmitter.BuildNestedInstanceDeclaration(
                    name, _context.ChildTypeByInstance[name]))
                .ToList<StatementSyntax>();

        public override SyntaxNode? VisitBlock(BlockSyntax node)
        {
            var isOutermost = !_outermostBlockDone;
            _outermostBlockDone = true;
            _blockStack.Push(node);

            var visited = (BlockSyntax?)base.VisitBlock(node);

            _blockStack.Pop();

            if (visited == null || !_instancesByBlock.TryGetValue(node, out var instances))
                return visited;

            var declarations = instances
                .Select(name => (StatementSyntax)DisplayClassSyntaxEmitter.BuildNestedInstanceDeclaration(
                    name, _context.ChildTypeByInstance[name]))
                .ToList();

            return visited.WithStatements(
                SyntaxFactory.List(declarations.Concat(visited.Statements)));
        }

        /// <summary>① `__cs2jClosureN._cs2jCap_x` → relative field expression (D4).</summary>
        public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            if (node.Expression is IdentifierNameSyntax { Identifier.ValueText: { } root }
                && node.Name is IdentifierNameSyntax { Identifier.ValueText: { } field }
                && _context.CollapseByInstanceField.TryGetValue((root, field), out var collapsed))
            {
                return SyntaxFactory.ParseExpression(collapsed).WithTriviaFrom(node);
            }

            return base.VisitMemberAccessExpression(node);
        }

        public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
            => ReplaceNestedOrIsolate(node);

        public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
            => ReplaceNestedOrIsolate(node);

        private SyntaxNode? ReplaceNestedOrIsolate(LambdaExpressionSyntax node)
        {
            // Direct child-group instance site: delegate creation + per-block
            // child display instance (the whole nested body is dropped here — it
            // migrates into the child class as its own Lambda_k method).
            if (_context.ChildSites.TryGetValue((node.SpanStart, node.RawKind), out var child))
            {
                RecordChildInstance(child.InstanceName);
                return DisplayClassSyntaxEmitter
                    .BuildNestedDelegateCreation(child.DelegateTypeDisplay, child.InstanceName, child.MethodName)
                    .WithTriviaFrom(node);
            }

            // Nested PLANNED-STATIC site (M1 substitution discipline).
            if (_context.StaticPlans.TryGetValue((node.SpanStart, node.RawKind), out var staticPlan))
                return StaticLambdaHandler.BuildSiteReplacement(staticPlan).WithTriviaFrom(node);

            // Any other nested lambda (quoted expression trees, documented
            // residual buckets) stays a verbatim subtree: never rewrite inside
            // a lambda the pass does not own.
            return node;
        }

        private void RecordChildInstance(string instanceName)
        {
            if (_blockStack.Count > 0)
            {
                var block = _blockStack.Peek();
                if (!_instancesByBlock.TryGetValue(block, out var list))
                {
                    list = new List<string>();
                    _instancesByBlock[block] = list;
                }

                if (!list.Contains(instanceName))
                    list.Add(instanceName);
            }
            else if (!_expressionLevelInstances.Contains(instanceName))
            {
                _expressionLevelInstances.Add(instanceName);
            }
        }

        public override SyntaxNode? VisitThisExpression(ThisExpressionSyntax node)
        {
            if (_context.HasHostThis)
            {
                return SyntaxFactory.ParseExpression(
                        _context.HostThisPath + ClosureLiftNaming.ThisFieldName)
                    .WithTriviaFrom(node);
            }

            return base.VisitThisExpression(node);
        }

        public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
        {
            var declarators = node.Declaration.Variables;
            var promoted = declarators
                .Where(v => _context.PromotedLocalNames.Contains(v.Identifier.ValueText))
                .ToList();
            if (promoted.Count == 0)
                return base.VisitLocalDeclarationStatement(node);

            var statements = new List<StatementSyntax>();
            var survivors = new List<VariableDeclaratorSyntax>();
            foreach (var declarator in declarators)
            {
                var visitedInitializer = declarator.Initializer == null
                    ? null
                    : (EqualsValueClauseSyntax?)Visit(declarator.Initializer);

                if (!_context.PromotedLocalNames.Contains(declarator.Identifier.ValueText))
                {
                    survivors.Add(visitedInitializer == null
                        ? declarator
                        : declarator.WithInitializer(visitedInitializer));
                    continue;
                }

                if (visitedInitializer?.Value is { } initialValue
                    && _context.FieldAccessByName.TryGetValue(declarator.Identifier.ValueText, out var fieldAccess))
                {
                    statements.Add(SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            SyntaxFactory.ParseExpression(fieldAccess),
                            initialValue)));
                }
                // Declaration without initializer: the field exists on the class
                // already; nothing to emit.
            }

            if (survivors.Count > 0)
            {
                statements.Insert(0, node.WithDeclaration(
                    node.Declaration.WithVariables(SyntaxFactory.SeparatedList(survivors))));
            }

            return statements.Count switch
            {
                0 => SyntaxFactory.EmptyStatement(),
                1 => statements[0],
                _ => SyntaxFactory.Block(statements),
            };
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (IsVariableAccessPosition(node))
            {
                // ② captured/own-state access → (chain-prefixed) field access.
                if (_context.FieldAccessByName.TryGetValue(node.Identifier.ValueText, out var fieldAccess))
                    return SyntaxFactory.ParseExpression(fieldAccess).WithTriviaFrom(node);

                // ④ implicit-this instance member → `<path>_cs2jThis.Member`.
                if (_context.HasHostThis
                    && _context.InstanceMemberNames.Contains(node.Identifier.ValueText))
                {
                    return SyntaxFactory.ParseExpression(
                            _context.HostThisPath + ClosureLiftNaming.ThisFieldName + "." + node.Identifier.Text)
                        .WithTriviaFrom(node);
                }
            }

            return base.VisitIdentifierName(node);
        }

        /// <summary>Positions that are ordinary variable accesses (read or write).</summary>
        private static bool IsVariableAccessPosition(IdentifierNameSyntax node)
        {
            if (node.Parent is NameColonSyntax || CaptureClassification.IsInsideNameOf(node))
                return false;
            if (node.Parent is MemberAccessExpressionSyntax member && member.Name == node)
                return false;
            if (node.Parent is MemberBindingExpressionSyntax binding && binding.Name == node)
                return false;
            if (node.Parent is NameEqualsSyntax)
                return false;

            // Type contexts: typeof(T), sizeof(T), default(T), new T(), (T)x,
            // x as T, x is T u, T[] — a name in these positions denotes a type.
            if (node.Parent is TypeOfExpressionSyntax
                or SizeOfExpressionSyntax
                or DefaultExpressionSyntax
                or ArrayTypeSyntax
                or GenericNameSyntax
                or QualifiedNameSyntax
                or TypeArgumentListSyntax
                or DeclarationPatternSyntax)
            {
                return false;
            }

            if (node.Parent is CastExpressionSyntax cast && cast.Type == node)
                return false;
            if (node.Parent is ObjectCreationExpressionSyntax creation && creation.Type == node)
                return false;
            if (node.Parent is BinaryExpressionSyntax binary
                && (binary.IsKind(SyntaxKind.IsExpression) || binary.IsKind(SyntaxKind.AsExpression))
                && binary.Right == node)
            {
                return false;
            }

            return true;
        }
    }

    private static MethodDeclarationSyntax BuildDeclaration(
        string methodName,
        List<SyntaxToken> modifiers,
        TypeSyntax? returnType,
        List<ParameterSyntax> parameters,
        BlockSyntax? block,
        ArrowExpressionClauseSyntax? expressionBody)
    {
        var declaration = SyntaxFactory.MethodDeclaration(
            SyntaxFactory.List<AttributeListSyntax>(),
            SyntaxFactory.TokenList(modifiers),
            returnType,
            explicitInterfaceSpecifier: null,
            SyntaxFactory.Identifier(methodName),
            typeParameterList: null,
            SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters)),
            SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(),
            block,
            expressionBody);

        if (block is null)
            declaration = declaration.WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

        return declaration;
    }
}
