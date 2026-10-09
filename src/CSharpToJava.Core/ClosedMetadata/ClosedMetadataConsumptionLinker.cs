using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.ClosedMetadata;

/// <summary>
/// CM-LINK consumer linker (design §5.1 ConsumptionLinker, rules CM-LINK / D8):
/// inserts an idempotent bootstrap guard statement immediately before every
/// serializer-entry call statement the registration set can serve:
/// <code>
/// global::__Cs2jClosedTypes_&lt;Owner&gt;.__cs2j_register_closed_types();
/// JsonSerializer.Serialize(value);
/// </code>
/// Target selection is restricted to holders guaranteed reachable from the consumer
/// project so the intermediate tree always compiles:
/// - a type declared in source (DTO/generic user construction) → its declaring
///   project's holder; naming the type at the call site already requires that
///   project reference;
/// - a framework construction (e.g. List&lt;int&gt;) → only the consumer project's
///   own holder; cross-project framework closures without a reference are left
///   unguarded and surface through the M4c residual gate (§5.4 rule 4).
/// Sites without a statement form (expression-bodied members, field initializers)
/// are left untouched in M4b; the same residual rule accounts for them.
/// </summary>
public sealed class ClosedMetadataConsumptionLinker
{
    private const string GuardAnnotationKind = "cs2j-cm-guard";

    private readonly ClosedMetadataAnalysis _analysis;
    private readonly Func<string, string?> _holderNameByPath;
    private readonly string? _selfHolderName;
    private readonly Dictionary<string, string> _holderByRecordKey = new(StringComparer.Ordinal);
    private readonly HashSet<string> _holderNames = new(StringComparer.Ordinal);

    public ClosedMetadataConsumptionLinker(
        ClosedMetadataAnalysis analysis,
        Func<string, string?> holderNameByPath,
        string? selfHolderName)
    {
        _analysis = analysis;
        _holderNameByPath = holderNameByPath;
        _selfHolderName = selfHolderName;

        foreach (var record in analysis.ClosedTypes)
        {
            var holder = holderNameByPath(record.DeclaringFilePath);
            if (holder is null)
            {
                continue;
            }

            _holderNames.Add(holder);
            _holderByRecordKey.TryAdd(record.Key.SortText, holder);
        }
    }

    /// <summary>
    /// Inserts guards into the tree backing <paramref name="model"/>. Returns null
    /// when the file stays a clean byte-level copy (no servable consumer site).
    /// </summary>
    public ClosedMetadataFileResult? LinkFile(SemanticModel model)
    {
        var root = model.SyntaxTree.GetRoot();
        var targets = new Dictionary<StatementSyntax, SortedSet<string>>(ReferenceEqualityComparer.Instance);
        var linkedSites = 0;

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method
                || !ClosedMetadataCollector.IsSerializerEntry(method)
                || !method.IsGenericMethod
                || method.TypeArguments.Any(IsTypeParameter))
            {
                continue;
            }

            var holders = ResolveHolderNames(method.TypeArguments);
            if (holders.Count == 0)
            {
                continue;
            }

            // A guard is a statement: expression-bodied members, field/property
            // initializers and constructor initializers have no statement anchor and
            // are deferred to the residual gate rather than reshaped here.
            var statement = invocation.FirstAncestorOrSelf<StatementSyntax>();
            if (statement is null)
            {
                continue;
            }

            if (!targets.TryGetValue(statement, out var ownerSet))
            {
                ownerSet = new SortedSet<string>(StringComparer.Ordinal);
                targets[statement] = ownerSet;
            }

            ownerSet.UnionWith(holders);
            linkedSites++;
        }

        if (targets.Count == 0)
        {
            return null;
        }

        // Annotate every target in a SINGLE ReplaceNodes pass: sequential ReplaceNode
        // calls rebuild the ancestor lists and earlier annotations can be lost from
        // the lookup path of the still-original sibling nodes.
        var entries = new List<(SyntaxAnnotation Annotation, SortedSet<string> Holders)>(targets.Count);
        var annotatedRoot = root.ReplaceNodes(
            targets.Keys,
            (statement, _) =>
            {
                var annotation = new SyntaxAnnotation(
                    GuardAnnotationKind, entries.Count.ToString(CultureInfo.InvariantCulture));
                entries.Add((annotation, targets[statement]));
                return statement.WithAdditionalAnnotations(annotation);
            });

        var rewritten = new GuardSyntaxRewriter(entries).Visit(annotatedRoot)!;
        var statistics = new ClosedMetadataStatistics { ConsumerSitesLinked = linkedSites };
        return new ClosedMetadataFileResult(
            rewritten.ToFullString(),
            true,
            Array.Empty<ClosedMetadataDiagnostic>(),
            statistics);
    }

    private SortedSet<string> ResolveHolderNames(ImmutableArray<ITypeSymbol> typeArguments)
    {
        var holders = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var typeArgument in typeArguments)
        {
            foreach (var type in EnumerateTypeClosure(typeArgument))
            {
                if (type is not INamedTypeSymbol named)
                {
                    continue;
                }

                if (named.IsGenericType && !named.IsDefinition)
                {
                    if (ClosedTypeKey.Create(named) is { } key
                        && _holderByRecordKey.TryGetValue(key.SortText, out var holder)
                        && IsReachableHolder(holder, named))
                    {
                        holders.Add(holder);
                    }

                    continue;
                }

                if (!named.IsGenericType)
                {
                    // Non-generic user DTO: its member closures are registered in its
                    // declaring project; framework types have no source location.
                    var sourcePath = named.Locations
                        .FirstOrDefault(location => location.IsInSource)?.SourceTree?.FilePath;
                    if (sourcePath is not null
                        && _holderNameByPath(sourcePath) is { } ownerHolder
                        && _holderNames.Contains(ownerHolder))
                    {
                        holders.Add(ownerHolder);
                    }
                }
            }
        }

        return holders;
    }

    /// <summary>
    /// A framework-construction record may be owned by an unrelated project (its
    /// first ENUM-1 site); cross-references are only guaranteed for user-declared
    /// types or for the consumer's own holder.
    /// </summary>
    private bool IsReachableHolder(string holder, INamedTypeSymbol constructedType)
    {
        if (holder == _selfHolderName)
        {
            return true;
        }

        return constructedType.Locations.Any(location => location.IsInSource);
    }

    private static IEnumerable<ITypeSymbol> EnumerateTypeClosure(ITypeSymbol type)
    {
        yield return type;
        switch (type)
        {
            case IArrayTypeSymbol array:
                foreach (var nested in EnumerateTypeClosure(array.ElementType))
                {
                    yield return nested;
                }

                break;
            case INamedTypeSymbol { IsDefinition: false } named:
                foreach (var argument in named.TypeArguments)
                {
                    foreach (var nested in EnumerateTypeClosure(argument))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }

    private static bool IsTypeParameter(ITypeSymbol type) => type is ITypeParameterSymbol;

    private static ExpressionStatementSyntax GuardStatement(string holderName)
        => ExpressionStatement(
            InvocationExpression(
                ParseExpression($"global::{holderName}.{ClosedMetadataRegistrationGenerator.BootstrapMethodName}")));

    /// <summary>
    /// Top-down expansion: statement-list containers (blocks, switch sections,
    /// top-level globals) consume guard annotations before children are visited, so
    /// each target is handled exactly once; targets in embedded statement positions
    /// (if/while/for bodies…) fall through to <see cref="Visit"/> and get wrapped in
    /// a freshly generated block.
    /// </summary>
    private sealed class GuardSyntaxRewriter : CSharpSyntaxRewriter
    {
        private readonly IReadOnlyList<(SyntaxAnnotation Annotation, SortedSet<string> Holders)> _entries;

        public GuardSyntaxRewriter(IReadOnlyList<(SyntaxAnnotation, SortedSet<string>)> entries)
        {
            _entries = entries;
        }

        public override SyntaxNode? VisitBlock(BlockSyntax node)
        {
            var prepared = PrepareStatements(node, node.Statements, node.WithStatements);
            return base.VisitBlock(prepared);
        }

        public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
        {
            var prepared = PrepareStatements(node, node.Statements, node.WithStatements);
            return base.VisitSwitchSection(prepared);
        }

        public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
        {
            var prepared = node;
            if (node.Members.Any(member => member is GlobalStatementSyntax global && HasGuard(global.Statement)))
            {
                var members = new List<MemberDeclarationSyntax>();
                foreach (var member in node.Members)
                {
                    if (member is GlobalStatementSyntax global
                        && TryConsume(global.Statement, out var guards))
                    {
                        var stripped = global.Statement.WithoutAnnotations(GuardsAnnotations(guards!));
                        foreach (var guard in guards!)
                        {
                            members.Add(GlobalStatement(LineGuard(guard.Holder, global.GetLeadingTrivia())));
                        }

                        members.Add(global.WithStatement(stripped));
                    }
                    else
                    {
                        members.Add(member);
                    }
                }

                prepared = node.WithMembers(List(members));
            }

            return base.VisitCompilationUnit(prepared);
        }

        public override SyntaxNode? Visit(SyntaxNode? node)
        {
            var result = base.Visit(node);
            if (result is StatementSyntax statement && TryConsume(statement, out var guards))
            {
                return WrapInBlock(statement, guards!);
            }

            return result;
        }

        private TNode PrepareStatements<TNode>(
            TNode node,
            SyntaxList<StatementSyntax> statements,
            Func<SyntaxList<StatementSyntax>, TNode> withStatements)
            where TNode : SyntaxNode
        {
            if (!statements.Any(HasGuard))
            {
                return node;
            }

            var rewritten = new List<StatementSyntax>();
            foreach (var statement in statements)
            {
                if (TryConsume(statement, out var guards))
                {
                    var leading = statement.GetLeadingTrivia();
                    foreach (var guard in guards!)
                    {
                        rewritten.Add(LineGuard(guard.Holder, leading));
                    }

                    rewritten.Add(statement.WithoutAnnotations(GuardsAnnotations(guards)));
                }
                else
                {
                    rewritten.Add(statement);
                }
            }

            return withStatements(List(rewritten));
        }

        private bool TryConsume(StatementSyntax statement, out IReadOnlyList<(string Holder, SyntaxAnnotation Annotation)>? guards)
        {
            var annotation = statement.GetAnnotations(GuardAnnotationKind).FirstOrDefault();
            if (annotation is null)
            {
                guards = null;
                return false;
            }

            var index = int.Parse(annotation.Data!, CultureInfo.InvariantCulture);
            guards = _entries[index].Holders
                .Select(holder => (holder, _entries[index].Annotation))
                .ToList();
            return true;
        }

        private static StatementSyntax LineGuard(string holderName, SyntaxTriviaList leading)
            => GuardStatement(holderName)
                .WithLeadingTrivia(leading)
                .WithTrailingTrivia(TriviaList(LineFeed));

        private static SyntaxAnnotation[] GuardsAnnotations(IReadOnlyList<(string Holder, SyntaxAnnotation Annotation)> guards)
            => guards.Select(guard => guard.Annotation).ToArray();

        private static bool HasGuard(SyntaxNode node)
            => node.GetAnnotations(GuardAnnotationKind).Any();

        private static BlockSyntax WrapInBlock(
            StatementSyntax statement,
            IReadOnlyList<(string Holder, SyntaxAnnotation Annotation)> guards)
        {
            var leading = statement.GetLeadingTrivia();
            var blockStatements = new List<StatementSyntax>();
            foreach (var guard in guards)
            {
                blockStatements.Add(LineGuard(guard.Holder, leading));
            }

            blockStatements.Add(statement.WithoutAnnotations(GuardsAnnotations(guards)));
            return Block(
                    Token(leading, SyntaxKind.OpenBraceToken, TriviaList(CarriageReturnLineFeed)),
                    List(blockStatements),
                    Token(TriviaList(leading), SyntaxKind.CloseBraceToken, TriviaList()));
        }
    }
}
