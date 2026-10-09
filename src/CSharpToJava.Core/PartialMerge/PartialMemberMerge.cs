using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.PartialMerge;

/// <summary>
/// Member collection and M3 partial-pair collapse (design §4.5). Members keep the
/// "part order × declaration order" policy with NO text-key dedup of ordinary
/// members, but M3 adds symbol-paired collapse: when the SemanticModel shows a
/// partial member's definition and implementation parts both in source, the
/// definition node is dropped and the implementation node survives rebuilt
/// (modifier/attribute/doc merge, <c>partial</c> token stripped). Pairing always
/// goes through <c>PartialDefinitionPart</c>/<c>PartialImplementationPart</c>;
/// textual signature guessing is forbidden.
/// </summary>
internal static class PartialMemberMerge
{
    public static IEnumerable<MemberDeclarationSyntax> CollectMembers(
        TypeGroupPlan group,
        PartialMemberCollapse? collapse)
    {
        var plan = collapse?.ForGroup(group);

        foreach (var part in group.Parts)
        {
            foreach (var member in part.Declaration.Members)
            {
                // Nested partial types that are themselves groups are replaced by
                // their merged node (lookup supplied by the analyzer, depth-first).
                if (NestedOverrides.TryGetValue(member.ToFullString().Trim(), out var nested))
                {
                    // Non-primary part of a nested group: the merged node is emitted
                    // once at the nested primary's slot; this member disappears.
                    if (nested.Remove)
                    {
                        continue;
                    }

                    yield return nested.Merged;
                    continue;
                }

                if (plan != null)
                {
                    // M3: definition side of a source pair (or a removed classic
                    // declaration under AssumeNoGeneratedPartial) disappears.
                    if (plan.DroppedMembers.Contains(member))
                    {
                        continue;
                    }

                    // M3: implementation side survives as the rebuilt single
                    // member. The ORIGINAL implementation is annotated BEFORE the
                    // rebuild so call-site annotations ride shared green nodes
                    // into the collapsed member.
                    if (plan.ImplementationToDefinition.TryGetValue(member, out var definition))
                    {
                        yield return collapse.BuildCollapsedMemberFor(group, member, definition);
                        continue;
                    }
                }

                yield return collapse is null ? member : collapse.AnnotateRemovedCallSites(member);
            }
        }
    }

    /// <summary>
    /// Keyed (for M1) by the nested declaration's trimmed full text; analyzer fills
    /// it depth-first. If collisions appear in real fixtures, switch to an
    /// annotation-based key.
    /// </summary>
    public static Dictionary<string, NestedOverride> NestedOverrides { get; }
        = new(StringComparer.Ordinal);
}

/// <summary>
/// What to do with a member that is a part of a nested partial group.
/// Remove marks the non-primary parts (merged node is emitted at the primary slot).
/// </summary>
internal sealed record NestedOverride(TypeDeclarationSyntax Merged, bool Remove);

/// <summary>Per-group M3 decisions consumed while collecting members.</summary>
internal sealed class GroupCollapsePlan
{
    /// <summary>
    /// In-source pairs: implementation-part node → its definition-part node.
    /// The collapsed node is built lazily at collection time (annotations first).
    /// </summary>
    public Dictionary<MemberDeclarationSyntax, MemberDeclarationSyntax> ImplementationToDefinition { get; }
        = new();

    /// <summary>Definition-part nodes (and removed classics) that must not survive.</summary>
    public HashSet<MemberDeclarationSyntax> DroppedMembers { get; } = new();
}

/// <summary>
/// Whole-analysis M3 state (design §4.5): symbol-driven pairing for every active
/// group, C#13 coverage (methods incl. constructors/generics/static, properties,
/// indexers, events), and the opt-in classic-declaration + call-site removal.
/// </summary>
internal sealed class PartialMemberCollapse
{
    private readonly CSharpCompilation _compilation;
    private readonly PartialMergeOptions _options;
    private readonly MutablePartialMergeStats _stats;
    private readonly List<PartialMergeDiagnostic> _diagnostics;
    private readonly string _sourceRoot;

    // Keyed by plan OBJECT IDENTITY on purpose: TypeGroupPlan is a record whose
    // synthesized value equality includes the mutable M3 flags, so hashing it as
    // a value would make lookups miss once IsMemberOnlyRewrite (or a rollback
    // flag) flips after PrepareGroup.
    private readonly Dictionary<TypeGroupPlan, GroupCollapsePlan> _groupPlans =
        new(ReferenceEqualityComparer.Instance);

    // Classic methods tentatively removable under the opt-in flag; finalized once
    // every call site has been classified.
    private readonly List<ClassicRemovalCandidate> _classicCandidates = new();

    // Block-level invocation statements finalized for removal. Identity is only
    // relied upon while walking ORIGINAL source trees (where red wrappers are
    // stable); AnnotateRemovedCallSites stamps them with CallSiteAnnotation
    // BEFORE grafting, so the post-graft stripper matches by annotation.
    private readonly HashSet<ExpressionStatementSyntax> _removedStatements = new();

    /// <summary>
    /// Annotation carried on statements that must disappear (design §4.5): green
    /// nodes survive Roslyn grafting (WithMembers/rewriters), red wrappers do not.
    /// </summary>
    public static readonly SyntaxAnnotation CallSiteAnnotation =
        new("_cs2j_partial_call_site");

    public PartialMemberCollapse(
        CSharpCompilation compilation,
        PartialMergeOptions options,
        MutablePartialMergeStats stats,
        List<PartialMergeDiagnostic> diagnostics,
        string sourceRoot)
    {
        _compilation = compilation;
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
        _sourceRoot = sourceRoot;
    }

    public GroupCollapsePlan? ForGroup(TypeGroupPlan group)
        => _groupPlans.TryGetValue(group, out var plan) ? plan : null;

    /// <summary>
    /// Classifies every member of one type unit. In-source pairs collapse
    /// unconditionally (M3); declaration-only classics are only tentatively
    /// removed — <see cref="FinalizeClassicRemovals"/> makes the final decision
    /// after call-site classification.
    /// </summary>
    public GroupCollapsePlan PrepareGroup(TypeGroupPlan group)
    {
        var plan = new GroupCollapsePlan();
        _groupPlans.Add(group, plan);

        // member node -> declared partial-part symbol (definition or implementation).
        var definitions = new Dictionary<ISymbol, MemberDeclarationSyntax>(SymbolEqualityComparer.Default);
        var implementations = new Dictionary<ISymbol, MemberDeclarationSyntax>(SymbolEqualityComparer.Default);

        foreach (var part in group.Parts)
        {
            var semanticModel = _compilation.GetSemanticModel(part.Declaration.SyntaxTree);
            foreach (var member in part.Declaration.Members)
            {
                var symbol = GetMemberPartialSymbol(semanticModel, member);
                if (symbol is null)
                {
                    continue;
                }

                if (IsPartialDefinition(symbol))
                {
                    definitions[symbol] = member;
                }
                else if (GetPartialDefinition(symbol) is { } definition)
                {
                    implementations[definition] = member;
                }
            }
        }

        foreach (var (definitionSymbol, definitionNode) in definitions)
        {
            if (implementations.TryGetValue(definitionSymbol, out var implementationNode))
            {
                plan.ImplementationToDefinition[implementationNode] = definitionNode;
                plan.DroppedMembers.Add(definitionNode);
                _stats.MembersCollapsed++;
                continue;
            }

            // No in-source implementation part.
            if (definitionSymbol is IMethodSymbol method
                && method.MethodKind == MethodKind.Ordinary
                && definitionNode is MethodDeclarationSyntax methodNode
                && IsClassicDeclarationOnly(method, methodNode))
            {
                if (_options.AssumeNoGeneratedPartial)
                {
                    _classicCandidates.Add(new ClassicRemovalCandidate(group, plan, method, methodNode));
                }

                // Default flag-off: v1 keeps the declaration (design §4.5 M3).
                continue;
            }

            // Extended partial member (modern method/property/indexer/event/ctor)
            // without an implementation is a baseline compile error (CS8795/CS9248/
            // CS9275), so a clean group never reaches here. Keep both members and
            // diagnose loudly rather than emitting a half-merged shape.
            _diagnostics.Add(new PartialMergeDiagnostic(
                PartialMergeSeverity.Warning,
                PartialMergeCodes.ExtendedPartialMissingImpl,
                "extended partial member '" + definitionSymbol.ToDisplayString() +
                "' has no in-source implementation part; member left untouched",
                TypeFullName: group.TypeFullName));
        }

        // Implementation without a source definition part is likewise a baseline
        // error on clean trees; never drop it silently.
        foreach (var (definitionSymbol, node) in implementations)
        {
            if (definitions.ContainsKey(definitionSymbol))
            {
                continue;
            }

            _diagnostics.Add(new PartialMergeDiagnostic(
                PartialMergeSeverity.Warning,
                PartialMergeCodes.ExtendedPartialMissingImpl,
                "partial implementation '" + definitionSymbol.ToDisplayString() +
                "' has no in-source definition part; member left untouched",
                FilePath: node.SyntaxTree.FilePath,
                TypeFullName: group.TypeFullName));
        }

        return plan;
    }

    /// <summary>
    /// Scans every source tree for calls to the tentatively removed classic
    /// methods. A call is removable only when it is the direct expression of an
    /// expression statement directly inside a block; any other shape (expression
    /// body, initializer, embedded statement, argument, …) aborts that method's
    /// whole removal (CS2J2019) — no half-finished products.
    /// </summary>
    public void FinalizeClassicRemovals()
    {
        if (!_options.AssumeNoGeneratedPartial || _classicCandidates.Count == 0)
        {
            return;
        }

        var sourceTrees = _compilation.SyntaxTrees
            .Where(tree =>
                tree.FilePath.StartsWith(_sourceRoot, StringComparison.OrdinalIgnoreCase)
                && !tree.FilePath.Contains("__cs2j", StringComparison.Ordinal))
            .ToList();

        foreach (var candidate in _classicCandidates)
        {
            var statements = new HashSet<ExpressionStatementSyntax>();
            var notRemovable = false;

            foreach (var tree in sourceTrees)
            {
                var semanticModel = _compilation.GetSemanticModel(tree);
                foreach (var invocation in tree.GetRoot().DescendantNodes()
                             .OfType<InvocationExpressionSyntax>())
                {
                    var invoked = semanticModel.GetSymbolInfo(invocation).Symbol;
                    if (invoked is null
                        || !SymbolEqualityComparer.Default.Equals(invoked, candidate.Symbol))
                    {
                        continue;
                    }

                    if (invocation.Parent is ExpressionStatementSyntax statement
                        && statement.Expression == invocation
                        && statement.Parent is BlockSyntax)
                    {
                        statements.Add(statement);
                        continue;
                    }

                    // Any non-removable shape (expression body, initializer,
                    // embedded statement, argument, …) aborts the method's WHOLE
                    // removal — never drop a declaration while a call survives.
                    notRemovable = true;
                    var lineSpan = invocation.GetLocation().GetLineSpan();
                    _diagnostics.Add(new PartialMergeDiagnostic(
                        PartialMergeSeverity.Warning,
                        PartialMergeCodes.PartialCallSiteNotRemovable,
                        "classic partial method '" + candidate.Symbol.ToDisplayString() +
                        "' has a call site that cannot be removed under " +
                        nameof(PartialMergeOptions.AssumeNoGeneratedPartial) +
                        " (only block-level expression statements are removable); " +
                        "the declaration and all its calls are kept",
                        FilePath: lineSpan.Path,
                        Line: lineSpan.StartLinePosition.Line + 1,
                        Column: lineSpan.StartLinePosition.Character + 1,
                        TypeFullName: candidate.Group.TypeFullName));
                    break;
                }

                if (notRemovable)
                {
                    break;
                }
            }

            if (notRemovable)
            {
                continue;
            }

            candidate.Plan.DroppedMembers.Add(candidate.Node);
            _stats.PartialDeclarationsRemoved++;
            _stats.PartialCallSitesRemoved += statements.Count;
            foreach (var statement in statements)
            {
                _removedStatements.Add(statement);
            }
        }
    }

    /// <summary>
    /// Builds the collapsed member for an in-source pair at collection time:
    /// annotations are stamped on the original implementation first, then the
    /// modifier/attribute/doc rebuild runs (its shared body green keeps them).
    /// </summary>
    public MemberDeclarationSyntax BuildCollapsedMemberFor(
        TypeGroupPlan group,
        MemberDeclarationSyntax implementation,
        MemberDeclarationSyntax definition)
        => BuildCollapsedMember(group, definition, AnnotateRemovedCallSites(implementation));

    /// <summary>
    /// Stamps finalized statements on an ORIGINAL (or freshly rebuilt) member
    /// with <see cref="CallSiteAnnotation"/> before the node is grafted into a
    /// merged declaration. Called per member at collection time; identity against
    /// <see cref="_removedStatements"/> is valid here because the rewriter runs on
    /// the source-tree node rather than on a grafted wrapper.
    /// </summary>
    public MemberDeclarationSyntax AnnotateRemovedCallSites(MemberDeclarationSyntax member)
    {
        if (_removedStatements.Count == 0)
        {
            return member;
        }

        return (MemberDeclarationSyntax)new CallSiteAnnotator(_removedStatements).Visit(member)!;
    }

    /// <summary>Strips finalized call statements from a freshly built merged node.</summary>
    public TypeDeclarationSyntax StripCallSites(TypeDeclarationSyntax merged)
    {
        if (_removedStatements.Count == 0)
        {
            return merged;
        }

        return (TypeDeclarationSyntax)new CallSiteStatementStripper().Visit(merged)!;
    }

    // ---- symbol helpers ----

    /// <summary>
    /// Returns the partial-part symbol declared by a member node (definition or
    /// implementation), or null for ordinary members. Event field declarations
    /// resolve through their single variable declarator.
    /// </summary>
    private static ISymbol? GetMemberPartialSymbol(
        SemanticModel semanticModel,
        MemberDeclarationSyntax member)
    {
        ISymbol? symbol = member switch
        {
            MethodDeclarationSyntax or ConstructorDeclarationSyntax
                or PropertyDeclarationSyntax or IndexerDeclarationSyntax or EventDeclarationSyntax
                => semanticModel.GetDeclaredSymbol(member),
            EventFieldDeclarationSyntax eventField => eventField.Declaration.Variables.Count == 1
                ? semanticModel.GetDeclaredSymbol(eventField.Declaration.Variables[0])
                : null,
            _ => null,
        };

        if (symbol is null)
        {
            return null;
        }

        return IsPartialDefinition(symbol) || GetPartialDefinition(symbol) is not null
            ? symbol
            : null;
    }

    private static ISymbol? GetPartialDefinition(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.PartialDefinitionPart,
        IPropertySymbol property => property.PartialDefinitionPart,
        IEventSymbol @event => @event.PartialDefinitionPart,
        _ => null,
    };

    private static bool IsPartialDefinition(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.IsPartialDefinition,
        IPropertySymbol property => property.IsPartialDefinition,
        IEventSymbol @event => @event.IsPartialDefinition,
        _ => false,
    };

    /// <summary>
    /// Classic (C#3) declaration-only shape: private implicit, void, no out
    /// parameter, none of the extended-form modifiers (design §4.5 rule 1).
    /// </summary>
    private static bool IsClassicDeclarationOnly(
        IMethodSymbol method,
        MethodDeclarationSyntax node)
    {
        if (!method.ReturnsVoid
            || method.DeclaredAccessibility != Accessibility.Private
            || method.Parameters.Any(parameter => parameter.RefKind == RefKind.Out)
            || method.IsVirtual || method.IsOverride || method.IsAbstract
            || method.IsSealed || method.IsExtern)
        {
            return false;
        }

        foreach (var modifier in node.Modifiers)
        {
            if (modifier.Kind() is SyntaxKind.PublicKeyword
                or SyntaxKind.PrivateKeyword
                or SyntaxKind.ProtectedKeyword
                or SyntaxKind.InternalKeyword
                or SyntaxKind.VirtualKeyword
                or SyntaxKind.OverrideKeyword
                or SyntaxKind.SealedKeyword
                or SyntaxKind.NewKeyword
                or SyntaxKind.AbstractKeyword
                or SyntaxKind.ExternKeyword
                or SyntaxKind.UnsafeKeyword)
            {
                return false;
            }
        }

        return true;
    }

    // ---- collapsed node construction ----

    private static readonly SyntaxKind[] MemberModifierOrder =
    [
        SyntaxKind.PublicKeyword,
        SyntaxKind.PrivateKeyword,
        SyntaxKind.ProtectedKeyword,
        SyntaxKind.InternalKeyword,
        SyntaxKind.StaticKeyword,
        SyntaxKind.NewKeyword,
        SyntaxKind.VirtualKeyword,
        SyntaxKind.AbstractKeyword,
        SyntaxKind.SealedKeyword,
        SyntaxKind.OverrideKeyword,
        SyntaxKind.ReadOnlyKeyword,
        SyntaxKind.ExternKeyword,
        SyntaxKind.UnsafeKeyword,
        SyntaxKind.AsyncKeyword,
    ];

    private static MemberDeclarationSyntax BuildCollapsedMember(
        TypeGroupPlan group,
        MemberDeclarationSyntax definition,
        MemberDeclarationSyntax implementation)
    {
        // Modifiers: union of both parts minus `partial` (the compiler requires
        // matching accessibility on both parts, CS8799), canonical order.
        var modifierKinds = definition.Modifiers
            .Concat(implementation.Modifiers)
            .Select(token => token.Kind())
            .Where(kind => kind != SyntaxKind.PartialKeyword)
            .Distinct()
            .OrderBy(kind =>
            {
                var index = Array.IndexOf(MemberModifierOrder, kind);
                return index < 0 ? MemberModifierOrder.Length : index;
            })
            .ThenBy(kind => kind.ToString(), StringComparer.Ordinal)
            .Select(kind => SyntaxFactory.Token(kind)
                .WithTrailingTrivia(SyntaxFactory.Space))
            .ToList();
        var modifiers = SyntaxFactory.TokenList(modifierKinds);

        // Attributes: implementation side first (design §4.5), definition side
        // appended; non-AllowMultiple duplicates are CS0579 baseline errors.
        // Moved lists keep a single separating space between one another (their
        // original trivia belonged to the source member line); member-level
        // leading trivia is rebuilt separately.
        var attributeLists = implementation.AttributeLists.Concat(definition.AttributeLists)
            .Select(list => list
                .WithoutTrivia()
                .WithTrailingTrivia(SyntaxFactory.Space))
            .ToList();
        var attributes = SyntaxFactory.List(attributeLists);

        MemberDeclarationSyntax rebuilt = implementation switch
        {
            BaseMethodDeclarationSyntax method => method
                .WithModifiers(modifiers)
                .WithAttributeLists(attributes),
            BasePropertyDeclarationSyntax property => property
                .WithModifiers(modifiers)
                .WithAttributeLists(attributes),
            _ => implementation,
        };

        rebuilt = MergeAccessorAttributes(rebuilt, definition);

        // Documentation follows D6: implementation docs verbatim, definition docs
        // appended in a sourced remarks block. Trivia must be read from the
        // ORIGINAL implementation node: WithModifiers drops the `partial` token
        // that owned the member's leading trivia.
        var definitionPart = group.Parts.First(part =>
            part.Declaration.Members.Contains(definition));
        var trivia = PartialDocCommentMerger.BuildMemberLeadingTrivia(
            implementation, definition, definitionPart.RelativePath);
        return rebuilt.WithLeadingTrivia(trivia);
    }

    /// <summary>
    /// Accessor-level merge (properties/indexers/events): definition-side
    /// accessor attributes are appended to the matching implementation accessor;
    /// implementation accessor modifiers/body are authoritative.
    /// </summary>
    private static MemberDeclarationSyntax MergeAccessorAttributes(
        MemberDeclarationSyntax rebuilt,
        MemberDeclarationSyntax definition)
    {
        if (rebuilt is not BasePropertyDeclarationSyntax rebuiltProperty
            || definition is not BasePropertyDeclarationSyntax definitionProperty
            || rebuiltProperty.AccessorList is null
            || definitionProperty.AccessorList is null)
        {
            return rebuilt;
        }

        var accessors = new List<AccessorDeclarationSyntax>();
        foreach (var rebuiltAccessor in rebuiltProperty.AccessorList.Accessors)
        {
            var definitionAccessor = definitionProperty.AccessorList.Accessors
                .FirstOrDefault(accessor => accessor.IsKind(rebuiltAccessor.Kind()));
            if (definitionAccessor is null)
            {
                accessors.Add(rebuiltAccessor);
                continue;
            }

            var mergedAccessorAttributes = rebuiltAccessor.AttributeLists
                .Concat(definitionAccessor.AttributeLists)
                .Select(list => list
                    .WithoutTrivia()
                    .WithTrailingTrivia(SyntaxFactory.Space))
                .ToList();
            accessors.Add(rebuiltAccessor.WithAttributeLists(
                SyntaxFactory.List(mergedAccessorAttributes)));
        }

        var newAccessorList = rebuiltProperty.AccessorList.WithAccessors(
            SyntaxFactory.List(accessors));

        return rebuilt switch
        {
            PropertyDeclarationSyntax property => property.WithAccessorList(newAccessorList),
            IndexerDeclarationSyntax indexer => indexer.WithAccessorList(newAccessorList),
            EventDeclarationSyntax @event => @event.WithAccessorList(newAccessorList),
            _ => rebuilt,
        };
    }

    private sealed record ClassicRemovalCandidate(
        TypeGroupPlan Group,
        GroupCollapsePlan Plan,
        IMethodSymbol Symbol,
        MethodDeclarationSyntax Node);

    /// <summary>
    /// Runs on ORIGINAL source-tree members before grafting and stamps finalized
    /// statements with <see cref="CallSiteAnnotation"/>. Node identity against the
    /// original-tree set is stable pre-graft.
    /// </summary>
    private sealed class CallSiteAnnotator : CSharpSyntaxRewriter
    {
        private readonly HashSet<ExpressionStatementSyntax> _targets;

        public CallSiteAnnotator(HashSet<ExpressionStatementSyntax> targets)
        {
            _targets = targets;
        }

        public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
        {
            var visited = base.VisitExpressionStatement(node);
            if (visited is null || !_targets.Contains(node))
            {
                return visited;
            }

            return ((ExpressionStatementSyntax)visited)
                .WithAdditionalAnnotations(CallSiteAnnotation);
        }
    }

    /// <summary>
    /// Removes annotated invocation statements from the assembled merged node.
    /// Matching is by <see cref="CallSiteAnnotation"/>: annotations live on green
    /// nodes and survive grafting, while red wrapper identity does not.
    /// </summary>
    private sealed class CallSiteStatementStripper : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
        {
            if (node.HasAnnotation(CallSiteAnnotation))
            {
                return null;
            }

            return base.VisitExpressionStatement(node);
        }
    }
}
