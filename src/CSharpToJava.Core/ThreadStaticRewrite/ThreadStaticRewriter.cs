using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CSharpToJava.Core.ThreadStaticRewrite;

/// <summary>Marks nodes produced by the thread-static rewriter (TS-1 declarations, TS-2..TS-5 sites).</summary>
internal static class ThreadStaticAnnotations
{
    public const string Kind = "Cs2jThreadStaticRewrite";

    public static SyntaxAnnotation Annotation { get; } = new(Kind);

    public static T Mark<T>(T node) where T : SyntaxNode => node.WithAdditionalAnnotations(Annotation);

    /// <summary>Annotates a synthesized/rewritten node with the owning plan id (rollback attribution).</summary>
    public static T Mark<T>(T node, string planId) where T : SyntaxNode
        => node.WithAdditionalAnnotations(new SyntaxAnnotation(Kind, planId));
}

/// <summary>
/// One <c>f.Value = &lt;init&gt;;</c> assignment to inject for the FirstThreadFaithful policy.
/// </summary>
internal sealed class ThreadStaticCctorAssignment
{
    public required ThreadStaticFieldPlan Plan { get; init; }

    /// <summary>Field name token as written inside the declaring type.</summary>
    public required string FieldName { get; init; }

    /// <summary>
    /// Initializer text already rewritten for same-file references (sites in
    /// other files are an exotic partial-type edge and keep their original text).
    /// </summary>
    public required string InitializerText { get; init; }

    /// <summary>True when the initializer expression lives in the current file.</summary>
    public required bool InitializerIsLocal { get; init; }
}

/// <summary>Static-constructor injection for one type declaration part (TS-10 / FirstThreadFaithful).</summary>
internal sealed class ThreadStaticCctorInjection
{
    /// <summary>Null = synthesize a static constructor in this part; otherwise prepend to its body.</summary>
    public ConstructorDeclarationSyntax? ExistingCctor { get; init; }

    public required List<ThreadStaticCctorAssignment> Assignments { get; init; }

    /// <summary>Original member index after which the synthesized cctor is emitted (last holder field).</summary>
    public int InsertAfterMemberIndex { get; init; }
}

/// <summary>
/// One TS-5 statement-level lift: <c>var __cs2j_ts_f = f.Value; …mutate temp…; f.Value = __cs2j_ts_f;</c>.
/// </summary>
internal sealed class ThreadStaticLiftGroup
{
    public required ExpressionStatementSyntax Anchor { get; init; }
    public required ThreadStaticFieldPlan Plan { get; init; }
    public required string TempName { get; init; }
    public required string AccessText { get; init; }
    public required int Order { get; init; }

    private LocalDeclarationStatementSyntax? _declaration;
    private ExpressionStatementSyntax? _writeback;

    public LocalDeclarationStatementSyntax Declaration
    {
        get
        {
            if (_declaration != null) return _declaration;
            var statement = SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.IdentifierName("var"),
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(TempName))
                            .WithInitializer(SyntaxFactory.EqualsValueClause(
                                SyntaxFactory.ParseExpression(AccessText + ".Value"))))));
            _declaration = ThreadStaticAnnotations.Mark(statement, Plan.Id);
            return _declaration;
        }
    }

    public ExpressionStatementSyntax Writeback => _writeback ??= ThreadStaticAnnotations.Mark(
        SyntaxFactory.ExpressionStatement(
            SyntaxFactory.ParseExpression(AccessText + ".Value = " + TempName)),
        Plan.Id);
}

/// <summary>
/// Applies active [ThreadStatic] plans to one syntax tree:
/// TS-1 declaration rewrite (+ multi-variable split, FirstThreadFaithful cctor injection),
/// TS-2 reads, TS-3 writes, TS-4 compound assignments and ++/--, TS-5 struct temp lifting.
/// Only semantic-model-bound nodes are accepted; there is no text replacement.
/// </summary>
internal sealed class ThreadStaticRewriter : CSharpSyntaxRewriter
{
    private const string ThreadStaticAttributeName = "System.ThreadStaticAttribute";
    internal const string TempPrefix = "__cs2j_ts_";

    private readonly Dictionary<SyntaxNode, ReferenceSite> _sitesByNode;
    private readonly Dictionary<SyntaxNode, ThreadStaticFieldPlan> _planBySiteNode;
    private readonly Dictionary<FieldDeclarationSyntax, IReadOnlyList<ThreadStaticFieldPlan>> _plansByDeclaration;
    private readonly IReadOnlyDictionary<TypeDeclarationSyntax, ThreadStaticCctorInjection> _cctorInjections;
    private readonly ThreadStaticOptions _options;
    private readonly ThreadStaticStatistics _stats;

    private readonly Dictionary<ExpressionStatementSyntax, List<ThreadStaticLiftGroup>> _groupsByAnchor = new();
    private readonly Dictionary<SyntaxNode, ThreadStaticLiftGroup> _liftCoveredNodes = new();
    private readonly HashSet<VariableDeclaratorSyntax> _faithfulStrippedDeclarators = new();
    private bool _suppressSiteCounters;

    public ThreadStaticRewriter(
        Dictionary<SyntaxNode, ReferenceSite> sitesByNode,
        Dictionary<SyntaxNode, ThreadStaticFieldPlan> planBySiteNode,
        Dictionary<FieldDeclarationSyntax, IReadOnlyList<ThreadStaticFieldPlan>> plansByDeclaration,
        IReadOnlyDictionary<TypeDeclarationSyntax, ThreadStaticCctorInjection> cctorInjections,
        ThreadStaticOptions options,
        ThreadStaticStatistics stats,
        CompilationUnitSyntax root)
    {
        _sitesByNode = sitesByNode;
        _planBySiteNode = planBySiteNode;
        _plansByDeclaration = plansByDeclaration;
        _cctorInjections = cctorInjections;
        _options = options;
        _stats = stats;

        if (options.InitializerPolicy == ThreadStaticInitializerPolicy.FirstThreadFaithful)
        {
            foreach (var planList in plansByDeclaration.Values)
            {
                foreach (var plan in planList)
                {
                    if (plan.Active && plan.InitializerKind == ThreadStaticInitializerKind.Inline)
                    {
                        _faithfulStrippedDeclarators.Add(plan.Declarator);
                    }
                }
            }
        }

        BuildLiftGroups(root);
    }

    /// <summary>
    /// Renders an inline initializer moved into the cctor (FirstThreadFaithful):
    /// reference sites inside it are rewritten to .Value, counters are withheld
    /// because the main declarator traversal already counted them once.
    /// </summary>
    internal string RenderInitializer(ThreadStaticCctorAssignment assignment)
    {
        if (!assignment.InitializerIsLocal)
        {
            return assignment.InitializerText;
        }

        var originalInitializer = assignment.Plan.Declarator.Initializer!.Value;
        _suppressSiteCounters = true;
        try
        {
            var visited = (ExpressionSyntax)Visit(originalInitializer)!;
            return visited.ToString();
        }
        finally
        {
            _suppressSiteCounters = false;
        }
    }

    // ---- TS-5: struct temp lift groups ----

    private void BuildLiftGroups(CompilationUnitSyntax root)
    {
        var mutations = _sitesByNode.Values
            .Where(s => s.Kind == ThreadStaticSiteKind.StructMutation && s.LiftAnchorSpan.HasValue)
            .OrderBy(s => s.SpanStart)
            .ToList();
        if (mutations.Count == 0) return;

        var allocator = new ThreadStaticTempNameAllocator(root);
        var groupsByKey = new Dictionary<(ExpressionStatementSyntax Anchor, string PlanId), ThreadStaticLiftGroup>();

        foreach (var site in mutations)
        {
            var anchorNode = root.FindNode(site.LiftAnchorSpan!.Value);
            var anchor = anchorNode as ExpressionStatementSyntax
                ?? anchorNode.AncestorsAndSelf().OfType<ExpressionStatementSyntax>().FirstOrDefault();
            if (anchor == null) continue;

            var plan = _planBySiteNode[site.Node];
            var key = (anchor, plan.Id);
            if (!groupsByKey.TryGetValue(key, out var group))
            {
                var name = allocator.Allocate(plan.Field.Name, anchor);
                group = new ThreadStaticLiftGroup
                {
                    Anchor = anchor,
                    Plan = plan,
                    TempName = name,
                    AccessText = site.Node.ToString(),
                    Order = groupsByKey.Count,
                };
                groupsByKey.Add(key, group);
                _stats.StructTempLifts++;

                if (!_groupsByAnchor.TryGetValue(anchor, out var list))
                {
                    list = new List<ThreadStaticLiftGroup>();
                    _groupsByAnchor[anchor] = list;
                }

                list.Add(group);
            }

            // Every access of the lifted field inside the anchor executes on the temp.
            // Lambdas/local functions nested in the statement are separate execution units.
            var boundary = LiftBoundary(anchor.Expression);
            foreach (var other in plan.Sites)
            {
                if (other.Node.SyntaxTree != root.SyntaxTree) continue;
                if (!anchor.Span.Contains(other.Node.Span)) continue;
                if (!Equals(LiftBoundary(other.Node), boundary)) continue;
                _liftCoveredNodes[other.Node] = group;
            }
        }

        foreach (var list in _groupsByAnchor.Values)
        {
            list.Sort((a, b) => a.Order.CompareTo(b.Order));
        }
    }

    private static SyntaxNode? LiftBoundary(SyntaxNode node)
        => node.AncestorsAndSelf()
            .FirstOrDefault(n => n is LambdaExpressionSyntax or LocalFunctionStatementSyntax);

    // ---- type declarations: field splits + cctor injection ----

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
        => RewriteTypeDeclaration(node, base.VisitClassDeclaration(node));

    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
        => RewriteTypeDeclaration(node, base.VisitStructDeclaration(node));

    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node)
        => RewriteTypeDeclaration(node, base.VisitRecordDeclaration(node));

    public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
        => RewriteTypeDeclaration(node, base.VisitInterfaceDeclaration(node));

    private SyntaxNode? RewriteTypeDeclaration(TypeDeclarationSyntax node, SyntaxNode? visitedBase)
    {
        if (visitedBase is not TypeDeclarationSyntax visited)
        {
            return visitedBase;
        }

        _cctorInjections.TryGetValue(node, out var injection);
        var synthesizedEmitted = false;

        var output = new List<MemberDeclarationSyntax>(visited.Members.Count);
        for (var i = 0; i < node.Members.Count; i++)
        {
            var originalMember = node.Members[i];
            var visitedMember = visited.Members[i];

            if (injection is { ExistingCctor: { } } && originalMember == injection.ExistingCctor)
            {
                output.Add(InjectIntoExistingCctor(
                    (ConstructorDeclarationSyntax)visitedMember, injection));
                continue;
            }

            if (originalMember is FieldDeclarationSyntax originalField
                && visitedMember is FieldDeclarationSyntax visitedFieldDeclaration
                && _plansByDeclaration.TryGetValue(originalField, out var plans))
            {
                output.AddRange(BuildFieldDeclarations(originalField, visitedFieldDeclaration, plans));
            }
            else
            {
                output.Add(visitedMember);
            }

            if (injection is { ExistingCctor: null } && i == injection.InsertAfterMemberIndex)
            {
                output.Add(BuildSynthesizedCctor(node, injection));
                synthesizedEmitted = true;
            }
        }

        // Defensive fallback (the chosen part always carries an initialized holder):
        // append the synthesized cctor at the end of the part.
        if (injection is { ExistingCctor: null } && !synthesizedEmitted)
        {
            output.Add(BuildSynthesizedCctor(node, injection));
        }

        return visited.WithMembers(SyntaxFactory.List(output));
    }

    private ConstructorDeclarationSyntax InjectIntoExistingCctor(
        ConstructorDeclarationSyntax visitedCctor,
        ThreadStaticCctorInjection injection)
    {
        var body = visitedCctor.Body ?? SyntaxFactory.Block();
        var injected = injection.Assignments
            .Select(a => ThreadStaticAnnotations.Mark(
                SyntaxFactory.ParseStatement(BuildAssignmentStatement(a)), a.Plan.Id));
        var statements = injected.Concat(body.Statements);
        return visitedCctor.WithBody(SyntaxFactory.Block(SyntaxFactory.List(statements)));
    }

    private ConstructorDeclarationSyntax BuildSynthesizedCctor(
        TypeDeclarationSyntax typeDeclaration,
        ThreadStaticCctorInjection injection)
    {
        var statements = injection.Assignments
            .Select(a => ThreadStaticAnnotations.Mark(
                SyntaxFactory.ParseStatement(BuildAssignmentStatement(a)), a.Plan.Id));
        return ThreadStaticAnnotations.Mark(
            SyntaxFactory.ConstructorDeclaration(typeDeclaration.Identifier)
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.StaticKeyword)))
                .WithParameterList(SyntaxFactory.ParameterList())
                .WithBody(SyntaxFactory.Block(SyntaxFactory.List(statements))))
            .WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);
    }

    private string BuildAssignmentStatement(ThreadStaticCctorAssignment assignment)
        => assignment.FieldName + ".Value = " + RenderInitializer(assignment) + ";";

    // ---- TS-1: faithful initializers are detached (they move into the cctor) ----

    public override SyntaxNode? VisitVariableDeclarator(VariableDeclaratorSyntax node)
    {
        var visited = (VariableDeclaratorSyntax?)base.VisitVariableDeclarator(node);
        if (visited != null && _faithfulStrippedDeclarators.Contains(node))
        {
            return visited.WithInitializer(null);
        }

        return visited;
    }

    // ---- TS-5 anchors: block splice + embedded-statement wrapping ----

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        var visited = (BlockSyntax?)base.VisitBlock(node);
        if (visited == null) return visited;

        var output = new List<StatementSyntax>(visited.Statements.Count);
        for (var i = 0; i < node.Statements.Count; i++)
        {
            var original = node.Statements[i];
            var visitedStatement = visited.Statements[i];
            if (original is ExpressionStatementSyntax expression
                && _groupsByAnchor.TryGetValue(expression, out var groups))
            {
                // Each anchor gets its own sibling block so the same base temp
                // name can be reused across independent statements.
                var statements = new List<StatementSyntax>();
                foreach (var group in groups) statements.Add(group.Declaration);
                statements.Add(visitedStatement);
                for (var g = groups.Count - 1; g >= 0; g--) statements.Add(groups[g].Writeback);
                output.Add(ThreadStaticAnnotations.Mark(SyntaxFactory.Block(SyntaxFactory.List(statements))));
            }
            else
            {
                output.Add(visitedStatement);
            }
        }

        return visited.WithStatements(SyntaxFactory.List(output));
    }

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        var visited = (ExpressionStatementSyntax?)base.VisitExpressionStatement(node);
        if (visited == null) return visited;

        // Direct children of a block are spliced flat by VisitBlock; every other
        // statement position (embedded if/while body, switch section, top-level)
        // gets a wrapping block carrying the temps.
        if (node.Parent is BlockSyntax) return visited;
        if (!_groupsByAnchor.TryGetValue(node, out var groups)) return visited;

        var statements = new List<StatementSyntax>();
        foreach (var group in groups) statements.Add(group.Declaration);
        statements.Add(visited);
        for (var g = groups.Count - 1; g >= 0; g--) statements.Add(groups[g].Writeback);
        return ThreadStaticAnnotations.Mark(SyntaxFactory.Block(SyntaxFactory.List(statements)));
    }

    // ---- TS-2..TS-5: reference sites ----

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        var visited = (ExpressionSyntax?)base.VisitIdentifierName(node) ?? node;
        return RewriteAccess(node, visited);
    }

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var visited = (ExpressionSyntax?)base.VisitMemberAccessExpression(node) ?? node;
        return RewriteAccess(node, visited);
    }

    public override SyntaxNode? VisitQualifiedName(QualifiedNameSyntax node)
    {
        var visited = (ExpressionSyntax?)base.VisitQualifiedName(node) ?? node;
        return RewriteAccess(node, visited);
    }

    private ExpressionSyntax RewriteAccess(SyntaxNode original, ExpressionSyntax visited)
    {
        if (!_sitesByNode.TryGetValue(original, out var site))
        {
            return visited;
        }

        if (_liftCoveredNodes.TryGetValue(original, out var lift))
        {
            // Inside a TS-5 lift block the whole field access executes on the temp.
            return ThreadStaticAnnotations.Mark(SyntaxFactory.IdentifierName(lift.TempName));
        }

        if (!_suppressSiteCounters)
        {
            switch (site.Kind)
            {
                case ThreadStaticSiteKind.Write:
                    _stats.WritesRewritten++;
                    break;
                case ThreadStaticSiteKind.Compound:
                    _stats.CompoundSitesRewritten++;
                    break;
                case ThreadStaticSiteKind.Increment:
                    _stats.IncrementSitesRewritten++;
                    break;
                default:
                    _stats.ReadsRewritten++;
                    break;
            }
        }

        return ThreadStaticAnnotations.Mark(SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            visited,
            SyntaxFactory.IdentifierName("Value")));
    }

    // ---- TS-1: field declarations (incl. multi-variable split) ----

    private IEnumerable<MemberDeclarationSyntax> BuildFieldDeclarations(
        FieldDeclarationSyntax originalField,
        FieldDeclarationSyntax visitedField,
        IReadOnlyList<ThreadStaticFieldPlan> plans)
    {
        var isFirst = true;
        for (var i = 0; i < originalField.Declaration.Variables.Count; i++)
        {
            var originalDeclarator = originalField.Declaration.Variables[i];
            var visitedDeclarator = visitedField.Declaration.Variables[i];
            var plan = plans.FirstOrDefault(p => p.Declarator == originalDeclarator);

            FieldDeclarationSyntax built;
            if (plan is { Active: true })
            {
                built = BuildHolderFieldDeclaration(originalField, visitedField, visitedDeclarator, plan);
                _stats.FieldsRewritten++;
                if (plan.InitializerKind == ThreadStaticInitializerKind.Inline)
                {
                    _stats.InitializersNormalized++;
                }
            }
            else
            {
                // Skipped (or non-hit) variable: keep its attributes and declarator verbatim.
                built = visitedField.WithDeclaration(
                    visitedField.Declaration.WithVariables(
                        SyntaxFactory.SingletonSeparatedList(visitedDeclarator)));
            }

            if (isFirst)
            {
                built = built.WithLeadingTrivia(visitedField.GetLeadingTrivia());
                isFirst = false;
            }
            else
            {
                built = built.WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);
            }

            yield return built;
        }
    }

    private FieldDeclarationSyntax BuildHolderFieldDeclaration(
        FieldDeclarationSyntax originalField,
        FieldDeclarationSyntax visitedField,
        VariableDeclaratorSyntax visitedDeclarator,
        ThreadStaticFieldPlan plan)
    {
        // Attribute lists and modifiers never contain reference sites, so they are taken
        // from the original declaration where symbol application references are identity-stable.
        // Only the initializer expression can contain rewritten sites, hence the visited declarator.
        var attributeLists = RemoveThreadStaticAttributes(originalField, plan);

        var modifiers = originalField.Modifiers;
        if (!modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword)))
        {
            modifiers = modifiers.Add(SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));
        }

        var typeText = originalField.Declaration.Type.ToString();
        var holderType = SyntaxFactory.ParseTypeName(
            "global::System.Threading.ThreadLocal<" + typeText + ">");

        var faithful = _options.InitializerPolicy == ThreadStaticInitializerPolicy.FirstThreadFaithful;
        var initializer = visitedDeclarator.Initializer?.Value;

        // EveryThread opt-in with an inline initializer: e is evaluated per thread
        // on first access. Everything else (FirstThreadFaithful, or no initializer)
        // uses the parameterless holder; faithful initializers move into the cctor.
        var holderInitializer = !faithful && initializer != null
            ? SyntaxFactory.ParseExpression(
                "new global::System.Threading.ThreadLocal<" + typeText + ">(() => " + initializer + ")")
            : SyntaxFactory.ParseExpression(
                "new global::System.Threading.ThreadLocal<" + typeText + ">()");

        var newDeclarator = SyntaxFactory.VariableDeclarator(
                SyntaxFactory.Identifier(visitedDeclarator.Identifier.ValueText))
            .WithInitializer(SyntaxFactory.EqualsValueClause(holderInitializer));

        var declaration = SyntaxFactory.FieldDeclaration(
            attributeLists,
            modifiers,
            SyntaxFactory.VariableDeclaration(holderType,
                SyntaxFactory.SingletonSeparatedList(newDeclarator)));

        return ThreadStaticAnnotations.Mark(declaration, plan.Id).WithTrailingTrivia(visitedField.GetTrailingTrivia());
    }

    /// <summary>Drops only the ThreadStatic attribute lists/entries; other attributes are preserved.</summary>
    private static SyntaxList<AttributeListSyntax> RemoveThreadStaticAttributes(
        FieldDeclarationSyntax originalField,
        ThreadStaticFieldPlan plan)
    {
        // On the original (unvisited) tree, ApplicationSyntaxReference.GetSyntax() returns
        // the exact AttributeSyntax nodes present in originalField, so reference identity is safe.
        var removed = new HashSet<AttributeSyntax>();
        foreach (var data in plan.Field.GetAttributes())
        {
            if (data.AttributeClass?.ToDisplayString() != ThreadStaticAttributeName) continue;
            if (data.ApplicationSyntaxReference?.GetSyntax() is AttributeSyntax attribute)
            {
                removed.Add(attribute);
            }
        }

        var keptLists = new List<AttributeListSyntax>();
        foreach (var list in originalField.AttributeLists)
        {
            var keptAttributes = list.Attributes.Where(a => !removed.Contains(a)).ToArray();
            if (keptAttributes.Length == 0) continue;
            keptLists.Add(keptAttributes.Length == list.Attributes.Count
                ? list
                : list.WithAttributes(SyntaxFactory.SeparatedList(keptAttributes)));
        }

        return SyntaxFactory.List(keptLists);
    }
}

/// <summary>
/// File-scoped allocator for deterministic TS-5 temp names
/// (<c>__cs2j_ts_&lt;field&gt;</c>, numeric suffix on collision).
/// Collisions follow C# scoping: locals/parameters whose scope encloses the
/// anchor force a suffix; sibling blocks (independent lifted statements) may
/// reuse the same base name.
/// </summary>
internal sealed class ThreadStaticTempNameAllocator
{
    private readonly List<(string Name, TextSpan ScopeSpan, int End)> _definitions;
    private readonly List<(string Name, SyntaxNode Anchor)> _allocated = new();

    public ThreadStaticTempNameAllocator(SyntaxNode root)
    {
        _definitions = CollectDefinitions(root);
    }

    public string Allocate(string fieldName, SyntaxNode anchor)
    {
        var baseName = ThreadStaticRewriter.TempPrefix + Sanitize(fieldName);
        if (!Conflicts(baseName, anchor))
        {
            _allocated.Add((baseName, anchor));
            return baseName;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = baseName + "_" + suffix;
            if (!Conflicts(candidate, anchor))
            {
                _allocated.Add((candidate, anchor));
                return candidate;
            }
        }
    }

    private bool Conflicts(string name, SyntaxNode anchor)
    {
        var anchorSpan = anchor.Span;
        foreach (var (otherName, scopeSpan, end) in _definitions)
        {
            // The name is in scope only where the anchor is inside the scope
            // region and the declaration textually precedes the anchor.
            if (otherName == name
                && end <= anchorSpan.Start
                && scopeSpan.Contains(anchorSpan))
            {
                return true;
            }
        }

        foreach (var (otherName, otherAnchor) in _allocated)
        {
            if (otherName == name && otherAnchor.Span.Contains(anchorSpan))
            {
                return true;
            }
        }

        return false;
    }

    private static List<(string Name, TextSpan ScopeSpan, int End)> CollectDefinitions(SyntaxNode root)
    {
        var result = new List<(string, TextSpan, int)>();

        foreach (var parameter in root.DescendantNodes().OfType<ParameterSyntax>())
        {
            if (parameter.Identifier.ValueText is not { Length: > 0 } parameterName) continue;
            var scope = ParameterScope(parameter);
            if (scope.HasValue)
            {
                result.Add((parameterName, scope.Value, parameter.Span.End));
            }
        }

        foreach (var declarator in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (declarator.Identifier.ValueText is not { Length: > 0 } variableName) continue;
            var scope = LocalScope(declarator);
            if (scope.HasValue)
            {
                result.Add((variableName, scope.Value, declarator.Span.End));
            }
        }

        foreach (var designation in root.DescendantNodes().OfType<SingleVariableDesignationSyntax>())
        {
            // Out variables declared directly in arguments (deconstruction locals
            // are already covered by their enclosing VariableDeclarator).
            if (designation.Identifier.ValueText is not { Length: > 0 } designationName) continue;
            if (designation.Ancestors().OfType<VariableDeclaratorSyntax>().Any()) continue;
            var block = designation.Ancestors().OfType<BlockSyntax>().FirstOrDefault();
            if (block != null)
            {
                result.Add((designationName, block.Span, designation.Span.End));
            }
        }

        foreach (var statement in root.DescendantNodes().OfType<ForEachStatementSyntax>())
        {
            if (statement.Identifier.ValueText is not { Length: > 0 } foreachName) continue;
            result.Add((foreachName, statement.Statement.Span, statement.Identifier.Span.End));
        }

        foreach (var catchDeclaration in root.DescendantNodes().OfType<CatchDeclarationSyntax>())
        {
            if (catchDeclaration.Identifier.ValueText is not { Length: > 0 } catchName) continue;
            if (catchDeclaration.Parent is CatchClauseSyntax { Block: { } catchBlock })
            {
                result.Add((catchName, catchBlock.Span, catchDeclaration.Span.End));
            }
        }

        return result;
    }

    private static TextSpan? ParameterScope(ParameterSyntax parameter)
    {
        var owner = parameter.Parent?.Parent;
        return owner switch
        {
            BaseMethodDeclarationSyntax { Body: { } methodBody } => methodBody.Span,
            AccessorDeclarationSyntax { Body: { } accessorBody } => accessorBody.Span,
            AnonymousFunctionExpressionSyntax { Block: { } lambdaBody } => lambdaBody.Span,
            LocalFunctionStatementSyntax { Body: { } functionBody } => functionBody.Span,
            _ => null,
        };
    }

    private static TextSpan? LocalScope(VariableDeclaratorSyntax declarator)
    {
        if (declarator.Parent is not VariableDeclarationSyntax declaration) return null;
        switch (declaration.Parent)
        {
            case FieldDeclarationSyntax:
            case EventFieldDeclarationSyntax:
                return null;
            case LocalDeclarationStatementSyntax:
                return declarator.Ancestors().OfType<BlockSyntax>().FirstOrDefault()?.Span;
            case ForStatementSyntax forStatement:
                return forStatement.Statement.Span;
            case UsingStatementSyntax usingStatement:
                return (usingStatement.Statement ?? usingStatement).Span;
            case FixedStatementSyntax fixedStatement:
                return (fixedStatement.Statement ?? fixedStatement).Span;
            case ArgumentSyntax:
                // out-variable: scope leaks to the enclosing block (visible after the declaration).
                return declarator.Ancestors().OfType<BlockSyntax>().FirstOrDefault()?.Span;
            default:
                // Unknown shape: be conservative and treat the enclosing block as the scope.
                return declarator.Ancestors().OfType<BlockSyntax>().FirstOrDefault()?.Span;
        }
    }

    private static string Sanitize(string name)
    {
        var trimmed = name.StartsWith("@", StringComparison.Ordinal) ? name[1..] : name;
        var builder = new System.Text.StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }

        return builder.Length == 0 ? "field" : builder.ToString();
    }
}
