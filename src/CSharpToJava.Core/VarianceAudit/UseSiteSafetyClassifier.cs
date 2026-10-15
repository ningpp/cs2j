using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.VarianceAudit;

/// <summary>Direction of a variance-dependent conversion edge (design D3a).</summary>
public enum VarianceConversionDirection
{
    None,
    Covariant,
    Contravariant,
    Array,
    Both,
    Unresolved,
}

/// <summary>
/// One conversion edge that depends on C# variance (or is a conservative candidate for it),
/// with the diagnostic code it maps to (design D3a/D8/§6).
/// </summary>
public sealed record VarianceConversionFinding(
    string Code,
    VarianceConversionDirection Direction,
    bool Resolved,
    UseFlow Flow,
    string SourceType,
    string TargetType,
    string Message,
    string? FilePath = null,
    int Line = 0,
    int Column = 0);

/// <summary>
/// Use-site safety classification (design D3②/D3a/D8/D11). Refines position flows with a
/// scope member-access summary, structurally detects variance implicit conversions over
/// assignment/initializer/return/argument/method-group edges (Roslyn public API only,
/// prefer false positives), attaches mapped-mutability facts (D9) and computes the
/// three-state conclusion per use site.
/// </summary>
internal sealed class UseSiteSafetyClassifier
{
    private readonly Compilation _compilation;
    private readonly VarianceAuditOptions _options;
    private readonly VarianceAuditStatistics _statistics;
    private readonly VarianceBaselineCache _baseline;
    private readonly TypeMutabilityTable _mutability = new();

    private Dictionary<string, SyntaxTree> _treesByPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Microsoft.CodeAnalysis.Text.SourceText> _textByPath =
        new(StringComparer.Ordinal);

    // One member-access summary per (model, enclosing member); a method carrying several
    // variant locals used to be re-bound once per local.
    private readonly Dictionary<(SemanticModel Model, SyntaxNode Container), MemberFlowSummary> _memberSummaries =
        new();

    public UseSiteSafetyClassifier(
        Compilation compilation,
        VarianceAuditOptions options,
        VarianceAuditStatistics statistics,
        VarianceBaselineCache baseline)
    {
        _compilation = compilation;
        _options = options;
        _statistics = statistics;
        _baseline = baseline;
    }

    public void Enrich(VarianceAuditAnalysis analysis)
    {
        _treesByPath = _compilation.SyntaxTrees
            .GroupBy(tree => tree.FilePath, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var hintNames = BuildVarianceHintNames(analysis);
        var delegateNames = BuildDelegateHintNames(analysis);
        var perTreeFindings = new ConcurrentBag<List<VarianceConversionFinding>>();
        Parallel.ForEach(
            _compilation.SyntaxTrees,
            () => new List<List<VarianceConversionFinding>>(),
            (tree, _, local) =>
            {
                var model = _compilation.GetSemanticModel(tree);
                if (!_baseline.HasErrors(model))
                {
                    var findings = new List<VarianceConversionFinding>();
                    new EdgeScanner(model, findings, hintNames, delegateNames).Scan();
                    local.Add(findings);
                }

                return local;
            },
            local =>
            {
                foreach (var findings in local)
                {
                    perTreeFindings.Add(findings);
                }
            });

        var allFindings = new List<VarianceConversionFinding>();
        foreach (var findings in perTreeFindings)
        {
            allFindings.AddRange(findings);
        }

        allFindings.Sort((a, b) => VarianceSiteInventory.Compare(
            a.FilePath, a.Line, a.Column, a.Code,
            b.FilePath, b.Line, b.Column, b.Code));
        analysis.ConversionSites.AddRange(allFindings);

        EnrichUseRecords(analysis, allFindings);
        FillStatistics(analysis, allFindings);
    }

    /// <summary>
    /// Short type names whose presence in a member makes the member worth binding for
    /// edge detection. Includes every variant type this very run already recorded at a
    /// use site (so the screen can never hide a variant type the audit recognises) plus
    /// the well-known variant BCL generic types.
    /// </summary>
    private static HashSet<string> BuildVarianceHintNames(VarianceAuditAnalysis analysis)
    {
        var names = new HashSet<string>(KnownVariantTypeNames, StringComparer.Ordinal);

        foreach (var declaration in analysis.DeclarationSites)
        {
            AddLastIdentifier(names, declaration.TypeName);
        }

        foreach (var use in analysis.UseSites)
        {
            AddGenericIdentifiers(names, use.TypeDisplayName);
            foreach (var argument in use.TypeArguments)
            {
                AddGenericIdentifiers(names, argument.ArgumentType);
            }
        }

        return names;
    }

    /// <summary>
    /// Short names of variant delegate types: the BCL Func/Action/etc. families plus
    /// every source-declared variant delegate. Used to decide which signature hints
    /// widen edge scanning to sibling members (method-group conversions).
    /// </summary>
    private static HashSet<string> BuildDelegateHintNames(VarianceAuditAnalysis analysis)
    {
        var names = new HashSet<string>(
            new[] { "Func", "Action", "Predicate", "Converter", "Comparison" },
            StringComparer.Ordinal);

        foreach (var declaration in analysis.DeclarationSites)
        {
            if (declaration.Container == VarianceContainer.Delegate)
            {
                AddLastIdentifier(names, declaration.TypeName);
            }
        }

        return names;
    }

    private static void AddLastIdentifier(HashSet<string> names, string? display)
    {
        if (string.IsNullOrEmpty(display))
        {
            return;
        }

        var dot = display.LastIndexOf('.');
        var name = dot >= 0 ? display[(dot + 1)..] : display;
        var tick = name.IndexOfAny(new[] { '`', '<' });
        if (tick >= 0)
        {
            name = name[..tick];
        }

        if (name.Length > 0)
        {
            names.Add(name);
        }
    }

    private static readonly Regex GenericIdentifierPattern = new(
        @"([A-Za-z_][A-Za-z0-9_]*)\s*<",
        RegexOptions.Compiled);

    private static void AddGenericIdentifiers(HashSet<string> names, string? display)
    {
        if (string.IsNullOrEmpty(display))
        {
            return;
        }

        foreach (Match match in GenericIdentifierPattern.Matches(display))
        {
            names.Add(match.Groups[1].Value);
        }
    }

    // ---- D3② scope member-access summary ----

    private void EnrichUseRecords(VarianceAuditAnalysis analysis, List<VarianceConversionFinding> findings)
    {
        var byKey = new Dictionary<(string, int, int), VarianceConversionFinding>();
        foreach (var finding in findings)
        {
            var key = (finding.FilePath ?? string.Empty, finding.Line, finding.Column);
            byKey.TryAdd(key, finding);
        }

        var enriched = new List<VarianceUseRecord>(analysis.UseSites.Count);
        foreach (var use in analysis.UseSites)
        {
            var flow = use.Flow;
            var unresolved = false;
            INamedTypeSymbol? named = null;
            var node = ResolveTypeSyntax(use.FilePath ?? string.Empty, use.Line, use.Column);
            if (node != null)
            {
                var model = _compilation.GetSemanticModel(node.SyntaxTree);
                named = model.GetTypeInfo(node).Type as INamedTypeSymbol;
                if (named != null && IsVariantClosed(named) && !use.BaselineHasErrors)
                {
                    (flow, unresolved) = RefineFlow(model, node, use.Flow);
                }
            }

            string? mapped = null;
            IReadOnlyList<string>? mutating = null;
            if (named != null)
            {
                var tableEntry = _mutability.Lookup(named);
                if (tableEntry != null)
                {
                    mapped = tableEntry.JavaType;
                    mutating = tableEntry.MutatingMembers;
                }
                else if (_options.CheckMappedMutability && _mutability.IsConservativelyMutable(named))
                {
                    mutating = TypeMutabilityTable.UnmappedMembers;
                }
            }

            var mutabilityExpansion = _options.CheckMappedMutability
                && flow == UseFlow.Producer && mutating is { Count: > 0 };

            var matched = byKey.TryGetValue((use.FilePath ?? string.Empty, use.Line, use.Column), out var finding);
            var classification = matched
                ? (finding!.Resolved
                   && finding.Direction is VarianceConversionDirection.Covariant
                       or VarianceConversionDirection.Contravariant
                       or VarianceConversionDirection.Array
                   && finding.Flow != UseFlow.Both
                    ? VarianceClassification.NeedsWildcard
                    : VarianceClassification.Unsafe)
                : mutabilityExpansion || unresolved
                    ? VarianceClassification.Unsafe
                    : VarianceClassification.Safe;

            enriched.Add(use with
            {
                Flow = flow,
                VarianceConversion = matched,
                Classification = classification,
                MappedJavaType = mapped,
                MutatingMembers = mutating,
                MemberResolutionIncomplete = unresolved,
            });
        }

        analysis.UseSites.Clear();
        analysis.UseSites.AddRange(enriched);
    }

    /// <summary>
    /// Resolves the <see cref="TypeSyntax"/> a use site was recorded from using its
    /// (file, line, column) anchor. At a shared span start several nested TypeSyntax
    /// nodes may line up; the inventory recorded the outermost position, so pick the
    /// outermost TypeSyntax starting exactly at the position.
    /// </summary>
    private TypeSyntax? ResolveTypeSyntax(string filePath, int line, int column)
    {
        if (!_treesByPath.TryGetValue(filePath, out var tree))
        {
            return null;
        }

        if (!_textByPath.TryGetValue(filePath, out var text))
        {
            text = tree.GetText();
            _textByPath[filePath] = text;
        }

        var lineIndex = line - 1;
        if (lineIndex < 0 || lineIndex >= text.Lines.Count)
        {
            return null;
        }

        var position = text.Lines[lineIndex].Start + (column - 1);
        if (position < 0 || position > tree.Length)
        {
            return null;
        }

        var token = tree.GetRoot().FindToken(position, findInsideTrivia: false);
        TypeSyntax? match = null;
        foreach (var candidate in token.Parent!.AncestorsAndSelf().OfType<TypeSyntax>())
        {
            if (candidate.SpanStart == position)
            {
                match = candidate;
            }
        }

        return match;
    }

    private (UseFlow Flow, bool Unresolved) RefineFlow(SemanticModel model, TypeSyntax node, UseFlow baseFlow)
    {
        var symbols = DeclaredSymbolsOf(node, model);
        if (symbols.Count == 0)
        {
            return (baseFlow, false);
        }

        var container = node.Ancestors().FirstOrDefault(a => a is MemberDeclarationSyntax)
                        ?? (SyntaxNode)node.SyntaxTree.GetRoot();
        var summary = GetMemberSummary(model, container);

        var anyConsumer = false;
        var anyProducer = false;
        var unresolved = false;
        foreach (var symbol in symbols)
        {
            if (summary.BySymbol.TryGetValue(symbol, out var bucket))
            {
                anyConsumer |= bucket.AnyConsumer;
                anyProducer |= bucket.AnyProducer;
                unresolved |= bucket.Unresolved;
            }
        }

        switch (baseFlow)
        {
            case UseFlow.Both:
                if (unresolved)
                {
                    return (UseFlow.Both, true);
                }

                if (anyConsumer)
                {
                    return (UseFlow.Both, false);
                }

                return anyProducer ? (UseFlow.Producer, false) : (UseFlow.Both, false);
            case UseFlow.Producer:
                return unresolved || anyConsumer ? (UseFlow.Both, unresolved) : (UseFlow.Producer, false);
            default:
                return (UseFlow.Consumer, unresolved);
        }
    }

    private MemberFlowSummary GetMemberSummary(SemanticModel model, SyntaxNode container)
    {
        var key = (model, container);
        if (_memberSummaries.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var summary = new MemberFlowSummary();
        foreach (var identifier in container.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            var symbol = model.GetSymbolInfo(identifier).Symbol;
            if (symbol == null)
            {
                continue;
            }

            var bucket = summary.BySymbol.TryGetValue(symbol, out var existing)
                ? existing
                : new SymbolFlowBucket();
            summary.BySymbol[symbol] = bucket;

            if (identifier.Parent is MemberAccessExpressionSyntax access && access.Expression == identifier)
            {
                var member = model.GetSymbolInfo(access).Symbol;
                if (member == null)
                {
                    bucket.Unresolved = true;
                    continue;
                }

                switch (MemberFlow(member))
                {
                    case UseFlow.Consumer:
                        bucket.AnyConsumer = true;
                        break;
                    case UseFlow.Producer:
                        bucket.AnyProducer = true;
                        break;
                    default:
                        bucket.AnyConsumer = true;
                        bucket.AnyProducer = true;
                        break;
                }
            }
            else if (identifier.Parent is ElementAccessExpressionSyntax element && element.Expression == identifier)
            {
                if (IsAssignmentTarget(element))
                {
                    bucket.AnyConsumer = true;
                }
                else
                {
                    bucket.AnyProducer = true;
                }
            }
            else if (identifier.Parent is ArgumentSyntax)
            {
                bucket.AnyConsumer = true;
            }
            else if (identifier.Parent is ReturnStatementSyntax or ArrowExpressionClauseSyntax)
            {
                bucket.AnyProducer = true;
            }
        }

        _memberSummaries[key] = summary;
        return summary;
    }

    private static List<ISymbol> DeclaredSymbolsOf(TypeSyntax node, SemanticModel model)
    {
        var symbols = new List<ISymbol>();
        switch (node.Parent)
        {
            case VariableDeclarationSyntax declaration:
                foreach (var declarator in declaration.Variables)
                {
                    if (model.GetDeclaredSymbol(declarator) is { } symbol)
                    {
                        symbols.Add(symbol);
                    }
                }

                break;
            case ParameterSyntax parameter:
                if (model.GetDeclaredSymbol(parameter) is { } parameterSymbol)
                {
                    symbols.Add(parameterSymbol);
                }

                break;
            case ForEachStatementSyntax forEach:
                if (model.GetDeclaredSymbol(forEach) is { } forEachSymbol)
                {
                    symbols.Add(forEachSymbol);
                }

                break;
        }

        return symbols;
    }

    private static UseFlow MemberFlow(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.ReturnsVoid ? UseFlow.Consumer : UseFlow.Producer,
        IPropertySymbol property when property.GetMethod != null && property.SetMethod == null => UseFlow.Producer,
        IPropertySymbol property when property.SetMethod != null && property.GetMethod == null => UseFlow.Consumer,
        _ => UseFlow.Both,
    };

    private static bool IsAssignmentTarget(SyntaxNode node)
    {
        if (node.Parent is AssignmentExpressionSyntax assignment && assignment.Left == node)
        {
            return true;
        }

        if (node.Parent is PrefixUnaryExpressionSyntax prefix
            && (prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression)))
        {
            return true;
        }

        return node.Parent is PostfixUnaryExpressionSyntax postfix
               && (postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression));
    }

    // ---- D3a structured conversion detection ----

    /// <summary>
    /// Per-tree edge scan. The callee symbol of an invocation/object-creation is resolved
    /// once even when the call carries many arguments, and the expensive
    /// <c>GetConversion</c> bind only runs for edges that could possibly involve
    /// variance (variant closed types, reference arrays, delegate targets /
    /// method groups).
    /// </summary>
    private sealed class EdgeScanner
    {
        private readonly SemanticModel _model;
        private readonly List<VarianceConversionFinding> _findings;
        private readonly IReadOnlySet<string> _hintNames;
        private readonly IReadOnlySet<string> _delegateNames;
        private readonly Dictionary<SyntaxNode, IMethodSymbol?> _methodByCall = new();

        public EdgeScanner(
            SemanticModel model,
            List<VarianceConversionFinding> findings,
            IReadOnlySet<string> hintNames,
            IReadOnlySet<string> delegateNames)
        {
            _model = model;
            _findings = findings;
            _hintNames = hintNames;
            _delegateNames = delegateNames;
        }

        /// <summary>
        /// Syntax-only pre-screen with method-ish scope granularity: an edge this
        /// scanner can report has a variant generic type, a reference array, or a
        /// delegate (often LINQ) on one side, and at least one of those shapes is
        /// visible inside the enclosing method/accessor/lambda/member. Scopes
        /// without any hint never get their bodies bound; all edge nodes inside a
        /// hint scope are examined (the target side of an edge may be invisible at
        /// the call site, e.g. an array-typed callee parameter).
        /// </summary>
        public void Scan()
        {
            var root = _model.SyntaxTree.GetRoot();
            var dirtyScopes = new HashSet<SyntaxNode>();
            foreach (var node in root.DescendantNodes())
            {
                if (!IsHint(node))
                {
                    continue;
                }

                // A hint in a member signature (parameter/return/base-list type) can be
                // the invisible target side of an edge in a sibling member of the same
                // type (e.g. `void M(Action<string> a)` and `N() => M(Handler)`), so a
                // signature hint dirties the whole enclosing type. Hints in bodies stay
                // scoped to their method/lambda/member.
                var member = EnclosingMember(node);
                if (member != null)
                {
                    dirtyScopes.Add(member);

                    // Only delegate-typed (Func/Action/source delegate) parameters and
                    // return types widen to the whole type: a method group passed to a
                    // sibling method is invisible at the call site. Collection/interface
                    // and array signatures stay member-scoped (they are far too common to
                    // bind every sibling member for).
                    if (IsInMemberSignature(node, member) && IsDelegateHint(node))
                    {
                        var enclosingType = member.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                        if (enclosingType != null)
                        {
                            dirtyScopes.Add(enclosingType);
                        }
                    }
                }
                else
                {
                    // Type-header hints (base lists, class constraints) mark the type itself.
                    var enclosingType = node.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                    dirtyScopes.Add(enclosingType
                                    ?? (SyntaxNode?)node.AncestorsAndSelf().OfType<DelegateDeclarationSyntax>().FirstOrDefault()
                                    ?? root);
                }
            }

            var seenNodes = new HashSet<SyntaxNode>();
            foreach (var scope in dirtyScopes)
            {
                foreach (var node in scope.DescendantNodes())
                {
                    // A nested local function/lambda can be its own dirty scope too;
                    // visit each edge node at most once.
                    if (!seenNodes.Add(node))
                    {
                        continue;
                    }

                    switch (node)
                    {
                        case VariableDeclaratorSyntax declarator
                            when declarator.Initializer != null && declarator.Parent is VariableDeclarationSyntax declaration:
                            TryAddEdge(
                                _model.GetTypeInfo(declaration.Type).Type, declaration.Type,
                                declarator.Initializer.Value, UseFlow.Both);
                            break;
                        case AssignmentExpressionSyntax assignment when assignment.IsKind(SyntaxKind.SimpleAssignmentExpression):
                            TryAddEdge(
                                _model.GetTypeInfo(assignment.Left).Type, assignment.Left,
                                assignment.Right, UseFlow.Both);
                            break;
                        case ReturnStatementSyntax statement when statement.Expression != null && !InsideLambda(statement):
                            TryAddEdge(
                                ReturnTypeOf(statement), statement,
                                statement.Expression, UseFlow.Producer);
                            break;
                        case ArgumentSyntax argument:
                            var target = ArgumentTargetType(argument);
                            if (target != null)
                            {
                                TryAddEdge(target, argument.Expression, argument.Expression, UseFlow.Consumer);
                            }

                            break;
                    }
                }
            }
        }

        private bool IsDelegateHint(SyntaxNode node)
        {
            var simpleName = node switch
            {
                GenericNameSyntax generic => generic.Identifier.ValueText,
                QualifiedNameSyntax qualified => RightmostName(qualified.Right),
                AliasQualifiedNameSyntax alias => RightmostName(alias.Name),
                _ => null,
            };
            return simpleName != null && _delegateNames.Contains(simpleName);
        }

        private static string? RightmostName(NameSyntax name)
        {
            while (name is QualifiedNameSyntax qualified)
            {
                name = qualified.Right;
            }

            return (name as SimpleNameSyntax)?.Identifier.ValueText;
        }

        private bool IsHint(SyntaxNode node)
        {
            switch (node)
            {
                // Only reference-element arrays can have C# array covariance;
                // byte[]/int[]/char[]/... buffers are noise and would dirty huge methods.
                // 'int[][]' is parsed flat (one element, two rank specifiers); its outer
                // element is itself an array, i.e. always a reference type.
                case ArrayTypeSyntax array when array.RankSpecifiers.Count >= 2:
                    return true;
                case ArrayTypeSyntax array:
                    return ArrayElementCouldBeReference(array.ElementType);
                case ImplicitArrayCreationExpressionSyntax:
                    return true;
                case GenericNameSyntax generic:
                    return _hintNames.Contains(generic.Identifier.ValueText);
                case InvocationExpressionSyntax invocation:
                    return _hintNames.Contains(InvocationName(invocation));
                // Dynamic receivers/arguments can carry an unresolved conversion into any
                // variant target; code using dynamic is rare, so scoping the whole member
                // that mentions it is cheap.
                case IdentifierNameSyntax identifier when identifier.Identifier.ValueText == "dynamic":
                    return true;
                default:
                    return false;
            }
        }

        private static bool ArrayElementCouldBeReference(TypeSyntax element)
        {
            switch (element)
            {
                // Jagged arrays: arrays themselves are reference types.
                case ArrayTypeSyntax:
                    return true;
                // 'T?' is a value Nullable only when T is a value type; recurse so
                // 'int[]?[]' (annotated reference element) still marks the scope.
                case NullableTypeSyntax nullable:
                    return ArrayElementCouldBeReference(nullable.ElementType);
                // Pointer-backed elements are never reference types.
                case PointerTypeSyntax:
                    return false;
                case PredefinedTypeSyntax predefined:
                    return predefined.Keyword.IsKind(SyntaxKind.StringKeyword)
                           || predefined.Keyword.IsKind(SyntaxKind.ObjectKeyword);
                // Tuples are value types; enum/struct/interface/class names cannot be
                // told apart syntactically, so stay conservative and include them.
                // 'dynamic' reaches here as an IdentifierName and is a reference type.
                case TupleTypeSyntax:
                    return false;
                default:
                    return true;
            }
        }

        private static string InvocationName(InvocationExpressionSyntax invocation)
        {
            return invocation.Expression switch
            {
                MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
                NameSyntax name => NameName(name),
                _ => string.Empty,
            };

            static string NameName(NameSyntax name)
            {
                while (name is QualifiedNameSyntax qualified)
                {
                    name = qualified.Right;
                }

                return (name as SimpleNameSyntax)?.Identifier.ValueText ?? string.Empty;
            }
        }

        private static SyntaxNode? EnclosingMember(SyntaxNode node)
        {
            foreach (var ancestor in node.AncestorsAndSelf())
            {
                if (ancestor is MemberDeclarationSyntax
                    or LocalFunctionStatementSyntax
                    or AnonymousFunctionExpressionSyntax)
                {
                    return ancestor;
                }
            }

            return null;
        }

        /// <summary>
        /// True when the hint sits in a member's signature (return type, parameter list,
        /// constraint clauses, property/event type, base/field type) rather than its body,
        /// expression body, accessors or initializer.
        /// </summary>
        private static bool IsInMemberSignature(SyntaxNode node, SyntaxNode member)
        {
            bool Inside(SyntaxNode? region)
                => region != null && node != region && region.Contains(node);

            switch (member)
            {
                case BaseMethodDeclarationSyntax method:
                    return !Inside(method.Body) && !Inside(method.ExpressionBody);
                case LocalFunctionStatementSyntax local:
                    return !Inside(local.Body) && !Inside(local.ExpressionBody);
                case AccessorDeclarationSyntax accessor:
                    return !Inside(accessor.Body) && !Inside(accessor.ExpressionBody);
                case AnonymousFunctionExpressionSyntax lambda:
                    return !Inside(lambda.Body) && !Inside(lambda.ExpressionBody);
                case PropertyDeclarationSyntax property:
                    return !Inside(property.ExpressionBody)
                           && !Inside(property.AccessorList)
                           && !Inside(property.Initializer?.Value);
                case IndexerDeclarationSyntax indexer:
                    return !Inside(indexer.ExpressionBody)
                           && !Inside(indexer.AccessorList);
                case EventDeclarationSyntax eventDeclaration:
                    return !Inside(eventDeclaration.AccessorList);
                case BaseFieldDeclarationSyntax:
                    return !node.AncestorsAndSelf().Any(n => n is EqualsValueClauseSyntax);
                case DelegateDeclarationSyntax:
                    return true;
                default:
                    return false;
            }
        }

        private IMethodSymbol? ResolveCallMethod(SyntaxNode callNode)
        {
            if (_methodByCall.TryGetValue(callNode, out var cached))
            {
                return cached;
            }

            var method = callNode switch
            {
                InvocationExpressionSyntax invocation => _model.GetSymbolInfo(invocation).Symbol as IMethodSymbol,
                ObjectCreationExpressionSyntax creation => _model.GetSymbolInfo(creation).Symbol as IMethodSymbol,
                _ => null,
            };
            _methodByCall[callNode] = method;
            return method;
        }

        private ITypeSymbol? ArgumentTargetType(ArgumentSyntax argument)
        {
            if (argument.Parent is not ArgumentListSyntax list)
            {
                return null;
            }

            var method = ResolveCallMethod(list.Parent);
            if (method == null || method.IsExtensionMethod)
            {
                return null;
            }

            if (argument.NameColon != null)
            {
                var name = argument.NameColon.Name.Identifier.ValueText;
                return method.Parameters.FirstOrDefault(parameter => parameter.Name == name)?.Type;
            }

            var index = list.Arguments.IndexOf(argument);
            return index >= 0 && index < method.Parameters.Length ? method.Parameters[index].Type : null;
        }

        private ITypeSymbol? ReturnTypeOf(ReturnStatementSyntax statement)
            => _model.GetEnclosingSymbol(statement.SpanStart) is IMethodSymbol method
                ? method.ReturnType
                : null;

        private void TryAddEdge(
            ITypeSymbol? targetType,
            SyntaxNode targetNode,
            ExpressionSyntax source,
            UseFlow flow)
        {
            if (targetType == null)
            {
                return;
            }

            var sourceType = _model.GetTypeInfo(source).Type;
            if (!EdgeMightBeVarianceRelated(targetType, sourceType))
            {
                return;
            }

            var conversion = _model.GetConversion(source);
            VarianceConversionDirection direction;
            bool resolved;
            if (conversion.IsUserDefined)
            {
                if (!IsVariantClosed(targetType) && !IsVariantClosed(sourceType))
                {
                    return;
                }

                direction = VarianceConversionDirection.Unresolved;
                resolved = false;
            }
            else if (sourceType == null)
            {
                if (conversion.IsMethodGroup && targetType is INamedTypeSymbol { TypeKind: TypeKind.Delegate } delegateType)
                {
                    direction = CompareMethodGroup(conversion.MethodSymbol, delegateType);
                    resolved = direction is VarianceConversionDirection.Covariant
                        or VarianceConversionDirection.Contravariant;
                }
                else
                {
                    return;
                }
            }
            else if (sourceType.TypeKind == TypeKind.Error || sourceType is IDynamicTypeSymbol)
            {
                if (!IsVariantClosed(targetType))
                {
                    return;
                }

                direction = VarianceConversionDirection.Unresolved;
                resolved = false;
            }
            else
            {
                direction = ComparePair(sourceType, targetType);
                resolved = direction is VarianceConversionDirection.Covariant
                    or VarianceConversionDirection.Contravariant
                    or VarianceConversionDirection.Array
                    or VarianceConversionDirection.Both;
                if (direction == VarianceConversionDirection.None)
                {
                    return;
                }
            }

            if (direction == VarianceConversionDirection.None)
            {
                return;
            }

            var code = CodeFor(direction, resolved, flow);
            var sourceText = sourceType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "<method group>";
            var targetText = targetType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            var span = targetNode.GetLocation().GetLineSpan().StartLinePosition;
            _findings.Add(new VarianceConversionFinding(
                Code: code,
                Direction: direction,
                Resolved: resolved,
                Flow: flow,
                SourceType: sourceText,
                TargetType: targetText,
                Message: ConversionMessage(code, direction, flow, sourceText, targetText),
                FilePath: _model.SyntaxTree.FilePath,
                Line: span.Line + 1,
                Column: span.Character + 1));
        }
    }

    /// <summary>
    /// Cheap structural pre-gate for <c>GetConversion</c>: variance can only play a role
    /// when the target is a closed variant generic type, a reference-element array, or a
    /// delegate (method-group conversion), or when the source itself is a closed variant
    /// type (user-defined conversion candidate).
    /// </summary>
    private static bool EdgeMightBeVarianceRelated(ITypeSymbol target, ITypeSymbol? source)
    {
        switch (target)
        {
            case IArrayTypeSymbol array:
                return array.ElementType.IsReferenceType || IsVariantClosed(source);
            case INamedTypeSymbol named:
                return IsVariantClosed(named)
                       || named.TypeKind == TypeKind.Delegate
                       || IsVariantClosed(source);
            default:
                return IsVariantClosed(source);
        }
    }

    private static VarianceConversionDirection ComparePair(ITypeSymbol source, ITypeSymbol target)
    {
        if (target is IArrayTypeSymbol targetArray && source is IArrayTypeSymbol sourceArray)
        {
            if (targetArray.Rank != sourceArray.Rank
                || !targetArray.ElementType.IsReferenceType
                || !sourceArray.ElementType.IsReferenceType)
            {
                return VarianceConversionDirection.None;
            }

            return IsDerivedFrom(sourceArray.ElementType, targetArray.ElementType)
                ? VarianceConversionDirection.Array
                : VarianceConversionDirection.None;
        }

        if (target is not INamedTypeSymbol targetNamed
            || source is not INamedTypeSymbol sourceNamed
            || !IsVariantClosed(targetNamed))
        {
            return VarianceConversionDirection.None;
        }

        var original = targetNamed.OriginalDefinition;
        if (!SymbolEqualityComparer.Default.Equals(sourceNamed.OriginalDefinition, original))
        {
            return VarianceConversionDirection.None;
        }

        var covariant = false;
        var contravariant = false;
        var count = Math.Min(sourceNamed.TypeArguments.Length, original.TypeParameters.Length);
        for (var i = 0; i < count; i++)
        {
            var parameter = original.TypeParameters[i];
            if (parameter.Variance == Microsoft.CodeAnalysis.VarianceKind.None)
            {
                continue;
            }

            var sourceArgument = sourceNamed.TypeArguments[i];
            var targetArgument = targetNamed.TypeArguments[i];
            if (SymbolEqualityComparer.Default.Equals(sourceArgument, targetArgument)
                || !sourceArgument.IsReferenceType
                || !targetArgument.IsReferenceType)
            {
                continue;
            }

            if (parameter.Variance == Microsoft.CodeAnalysis.VarianceKind.Out)
            {
                covariant |= IsDerivedFrom(sourceArgument, targetArgument);
            }
            else
            {
                contravariant |= IsDerivedFrom(targetArgument, sourceArgument);
            }
        }

        if (covariant && contravariant)
        {
            return VarianceConversionDirection.Both;
        }

        if (covariant)
        {
            return VarianceConversionDirection.Covariant;
        }

        return contravariant ? VarianceConversionDirection.Contravariant : VarianceConversionDirection.None;
    }

    private static VarianceConversionDirection CompareMethodGroup(IMethodSymbol? method, INamedTypeSymbol delegateType)
    {
        if (method == null)
        {
            return VarianceConversionDirection.Unresolved;
        }

        var original = delegateType.OriginalDefinition;
        var invoke = original.DelegateInvokeMethod ?? delegateType.DelegateInvokeMethod;
        if (invoke == null)
        {
            return VarianceConversionDirection.Unresolved;
        }

        var covariant = false;
        var contravariant = false;
        var mapped = false;
        for (var i = 0; i < original.TypeParameters.Length && i < delegateType.TypeArguments.Length; i++)
        {
            var parameter = original.TypeParameters[i];
            if (parameter.Variance == Microsoft.CodeAnalysis.VarianceKind.None)
            {
                continue;
            }

            var argument = delegateType.TypeArguments[i];
            for (var j = 0; j < invoke.Parameters.Length; j++)
            {
                if (!SymbolEqualityComparer.Default.Equals(invoke.Parameters[j].Type, parameter) || j >= method.Parameters.Length)
                {
                    continue;
                }

                mapped = true;
                var source = method.Parameters[j].Type;
                if (source.IsReferenceType && argument.IsReferenceType && IsDerivedFrom(argument, source))
                {
                    contravariant = true;
                }
            }

            if (SymbolEqualityComparer.Default.Equals(invoke.ReturnType, parameter))
            {
                mapped = true;
                if (method.ReturnType.IsReferenceType && argument.IsReferenceType
                    && IsDerivedFrom(method.ReturnType, argument))
                {
                    covariant = true;
                }
            }
        }

        if (covariant && contravariant)
        {
            return VarianceConversionDirection.Both;
        }

        if (covariant)
        {
            return VarianceConversionDirection.Covariant;
        }

        if (contravariant)
        {
            return VarianceConversionDirection.Contravariant;
        }

        return mapped ? VarianceConversionDirection.None : VarianceConversionDirection.Unresolved;
    }

    private static bool IsDerivedFrom(ITypeSymbol derived, ITypeSymbol baseType)
    {
        if (SymbolEqualityComparer.Default.Equals(derived, baseType))
        {
            return true;
        }

        if (!derived.IsReferenceType || !baseType.IsReferenceType)
        {
            return false;
        }

        for (var current = derived.BaseType; current != null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
            {
                return true;
            }
        }

        return derived.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, baseType));
    }

    private static bool IsVariantClosed(ITypeSymbol? type)
        => type is INamedTypeSymbol named
           && named.TypeArguments.Length > 0
           && named.OriginalDefinition.TypeParameters.Any(
               parameter => parameter.Variance != Microsoft.CodeAnalysis.VarianceKind.None);

    private static bool InsideLambda(ReturnStatementSyntax statement)
    {
        foreach (var ancestor in statement.Ancestors())
        {
            if (ancestor is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax)
            {
                return true;
            }

            if (ancestor is MemberDeclarationSyntax)
            {
                break;
            }
        }

        return false;
    }

    private static string CodeFor(VarianceConversionDirection direction, bool resolved, UseFlow flow)
    {
        if (!resolved || direction == VarianceConversionDirection.Both || flow == UseFlow.Both)
        {
            return VarianceCodes.BothConversionSite;
        }

        return direction == VarianceConversionDirection.Contravariant
            ? VarianceCodes.ContravariantConversionSite
            : VarianceCodes.CovariantConversionSite;
    }

    private static string ConversionMessage(
        string code,
        VarianceConversionDirection direction,
        UseFlow flow,
        string source,
        string target)
    {
        if (code == VarianceCodes.CovariantConversionSite)
        {
            var subject = direction == VarianceConversionDirection.Array
                ? "reference-array covariance"
                : "covariant conversion";
            return $"site '<{source}>' relies on {subject} to {target}; Java emits invariant types and "
                   + "this use will not compile; redesign at the boundary or await the use-site rewrite pass";
        }

        if (code == VarianceCodes.ContravariantConversionSite)
        {
            return $"site '<{source}>' relies on contravariant conversion to {target}; Java emits invariant types "
                   + "and this use will not compile; redesign at the boundary or await the use-site rewrite pass";
        }

        return $"site '<{source}>' -> {target}: variance conversion could not be fully resolved "
               + $"({(flow == UseFlow.Both ? "both-flow" : "dynamic/error/user-defined")}); conservatively treated as "
               + "unsafe; redesign at the boundary";
    }

    // ---- statistics ----

    private void FillStatistics(VarianceAuditAnalysis analysis, List<VarianceConversionFinding> findings)
    {
        _statistics.VarianceConversionsCovariant =
            findings.Count(f => f.Direction == VarianceConversionDirection.Covariant);
        _statistics.VarianceConversionsContravariant =
            findings.Count(f => f.Direction == VarianceConversionDirection.Contravariant);
        _statistics.VarianceConversionsArray =
            findings.Count(f => f.Direction == VarianceConversionDirection.Array);

        _statistics.UseSitesProducer = analysis.UseSites.Count(u => u.Flow == UseFlow.Producer);
        _statistics.UseSitesConsumer = analysis.UseSites.Count(u => u.Flow == UseFlow.Consumer);
        _statistics.UseSitesBoth = analysis.UseSites.Count(u => u.Flow == UseFlow.Both);

        var matched = new HashSet<(string, int, int)>();
        foreach (var use in analysis.UseSites.Where(u => u.VarianceConversion))
        {
            matched.Add((use.FilePath ?? string.Empty, use.Line, use.Column));
        }

        var unsafeSites = analysis.UseSites.Count(u => u.Classification == VarianceClassification.Unsafe);
        var unmatchedUnresolved = findings.Count(f =>
            f.Code == VarianceCodes.BothConversionSite
            && !matched.Contains((f.FilePath ?? string.Empty, f.Line, f.Column)));
        _statistics.UnsafeUseSites = unsafeSites + unmatchedUnresolved;
    }

    /// <summary>
    /// Short names of the variant generic types shipped by the BCL (all arities of
    /// Func/Action etc. share one name) plus the LINQ operators whose arguments are
    /// method-group conversions into Func/Action delegates. Used only as a syntax
    /// pre-screen; semantic analysis stays authoritative.
    /// </summary>
    internal static readonly IReadOnlySet<string> KnownVariantTypeNames = new HashSet<string>(
        new[]
        {
            // Variant collection / query interfaces.
            "IEnumerable", "IEnumerator", "IQueryable", "IOrderedQueryable", "IGrouping",
            "ILookup", "IOrderedEnumerable",
            "IReadOnlyCollection", "IReadOnlyList", "IReadOnlyDictionary", "IReadOnlySet",
            "IList", "ICollection", "IDictionary", "ISet",
            "IComparable", "IComparer", "IEqualityComparer",
            // Variant delegate families.
            "Action", "Func", "Predicate", "Converter", "Comparison",
            "Expression", "IObservable", "IObserver",
            "IAsyncEnumerable", "IAsyncEnumerator", "IProgress",
            // LINQ operators taking Func/Action delegate arguments.
            "Select", "Where", "SelectMany", "OrderBy", "OrderByDescending", "ThenBy",
            "ThenByDescending", "GroupBy", "Join", "GroupJoin", "ToLookup", "Distinct",
            "Cast", "OfType", "Concat", "Zip", "Union", "Intersect", "Except",
            "DefaultIfEmpty", "Reverse", "AsEnumerable", "AsQueryable", "ToArray",
            "ToList", "ToDictionary", "ToHashSet", "Aggregate", "Any", "All", "Count",
            "LongCount", "First", "FirstOrDefault", "Last", "LastOrDefault", "Single",
            "SingleOrDefault", "Min", "Max", "Sum", "Average", "Take", "TakeLast",
            "Skip", "SkipLast", "SequenceEqual",
        },
        StringComparer.Ordinal);

    private sealed class MemberFlowSummary
    {
        public Dictionary<ISymbol, SymbolFlowBucket> BySymbol { get; } =
            new(SymbolEqualityComparer.Default);
    }

    private sealed class SymbolFlowBucket
    {
        public bool AnyConsumer;
        public bool AnyProducer;
        public bool Unresolved;
    }
}
