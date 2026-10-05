using System.Text.RegularExpressions;
using CSharpToJava.Core.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExtensionMethodResolve;

/// <summary>
/// One M1 rewrite site, keyed for the post-rewrite identity gate. Original
/// definition display strings are stable across the original and recompiled
/// compilations (symbol identity is not shared across compilations).
/// </summary>
/// <param name="FilePath">Syntax tree file path the site belongs to.</param>
/// <param name="SpanStart">Original invocation span (orders sibling sites with the same signature).</param>
/// <param name="MethodOriginalDefinitionDisplay"><c>Unreduced.OriginalDefinition.ToDisplayString()</c>.</param>
/// <param name="ArgumentCount">Expected argument count AFTER the rewrite (receiver + original arguments).</param>
/// <param name="ParameterTypeDisplays">Unreduced original-definition parameter type displays, in order.</param>
public sealed record ExtensionRewrittenSiteKey(
    string FilePath,
    int SpanStart,
    string MethodOriginalDefinitionDisplay,
    int ArgumentCount,
    IReadOnlyList<string> ParameterTypeDisplays);

/// <summary>
/// AfterFiles gate (design §4.5):
/// (1) remaining reduced invocations (LINQ-host set exempt, plus diagnostic-
///     paired exempt sites; chain exempts match by span containment) — must be 0;
/// (2) remaining reduced method groups (non-call member access) — paired with
///     CS2J1207 exempts, otherwise residual;
/// (3) rewritten-site rebinding identity: rebound <c>OriginalDefinition</c> and
///     parameter sequence must match the pre-rewrite unreduced symbol;
/// (4) expression-tree shape: no statement-shaped nodes inside an
///     <c>Expression&lt;&gt;</c> lambda, reduced sugar must carry an exempt
///     (rollback/report-only/ET-conditional), and global::-qualified ordinary
///     calls inside must pair with rewritten keys;
/// (6) orphan static calls: a <c>global::</c>-qualified call whose host name
/// does not resolve in the new compilation — must be 0.
/// (5, M4) extension <c>this</c> modifiers: the post-rewrite tree must carry
/// the same number of <c>this</c>-modified first parameters as the input
/// (C19 never touches declarations) — plus the modifiers C49 extension-block
/// materialization legitimately introduces (instance methods and property
/// get/set pairs); the delta must be 0.
/// </summary>
public static class ExtensionMethodResidualScanner
{
    public const string RuleRemainingReducedInvocation = "remaining-reduced-invocation";
    public const string RuleRemainingReducedMethodGroup = "remaining-reduced-method-group";
    public const string RuleRewriteIdentity = "rewrite-identity";
    public const string RuleUnresolvedStaticHost = "unresolved-static-host";
    public const string RuleExpressionTreeShape = "expression-tree-shape";
    public const string RuleThisModifierPreserved = "extension-this-modifier-preserved";

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static Dictionary<string, int> Scan(
        Compilation rewritten,
        IReadOnlyList<ExtensionRewrittenSiteKey> rewrittenSites,
        ExtensionMethodResolveOptions options)
        => Scan(rewritten, rewrittenSites, options, Array.Empty<ExtensionExemptSite>());

    public static Dictionary<string, int> Scan(
        Compilation rewritten,
        IReadOnlyList<ExtensionRewrittenSiteKey> rewrittenSites,
        ExtensionMethodResolveOptions options,
        IReadOnlyList<ExtensionExemptSite> exemptSites)
        => Scan(rewritten, rewrittenSites, options, exemptSites, null, 0);

    public static Dictionary<string, int> Scan(
        Compilation rewritten,
        IReadOnlyList<ExtensionRewrittenSiteKey> rewrittenSites,
        ExtensionMethodResolveOptions options,
        IReadOnlyList<ExtensionExemptSite> exemptSites,
        IReadOnlyCollection<string>? excludedFiles,
        int baselineThisModifierCount,
        int expectedAddedThisModifiers = 0)
    {
        ArgumentNullException.ThrowIfNull(rewritten);
        ArgumentNullException.ThrowIfNull(rewrittenSites);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(exemptSites);

        var excluded = NormalizeExcluded(excludedFiles);
        var residuals = new Dictionary<string, int>(StringComparer.Ordinal);
        var exemptionIndex = new ExemptionIndex(exemptSites);

        ScanCallShapes(rewritten, options, exemptionIndex, residuals, excluded);
        ScanExpressionTreeShapes(rewritten, options, exemptionIndex, rewrittenSites, residuals, excluded);
        ScanRewriteIdentity(rewritten, rewrittenSites, residuals, excluded);
        ScanThisModifierPreservation(
            rewritten, baselineThisModifierCount, expectedAddedThisModifiers, residuals, excluded);

        return residuals;
    }

    private static HashSet<string> NormalizeExcluded(IReadOnlyCollection<string>? excludedFiles)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (excludedFiles is null)
        {
            return result;
        }

        foreach (var file in excludedFiles)
        {
            result.Add(NormalizePath(file));
        }

        return result;
    }

    private static bool IsExcluded(string treeFilePath, HashSet<string> excluded)
        => excluded.Contains(NormalizePath(treeFilePath));

    /// <summary>All whitespace removed; exempt nodes replay byte-for-byte otherwise.</summary>
    public static string CompactSource(string text) => Whitespace.Replace(text, string.Empty);

    private static void ScanCallShapes(
        Compilation compilation,
        ExtensionMethodResolveOptions options,
        ExemptionIndex exemptions,
        Dictionary<string, int> residuals,
        HashSet<string> excluded)
    {
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (IsExcluded(tree.FilePath, excluded))
            {
                continue;
            }

            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetCompilationUnitRoot();
            var filePath = tree.FilePath;

            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var info = model.GetSymbolInfo(invocation);

                // Gate 1: a reduced invocation that survived the rewrite. LINQ
                // hosts are owned by E16/C26 and stay reduced on purpose; ET
                // interiors are owned by gate 4.
                if (info.Symbol is IMethodSymbol { MethodKind: MethodKind.ReducedExtension } reduced
                    && reduced.ReducedFrom is { } unreduced)
                {
                    var hostName = unreduced.ContainingType.ToDisplayString();
                    if (options.LinqHostSet.Contains(hostName)
                        || IsInsideExpressionTreeLambda(invocation, model)
                        || exemptions.ClaimsInvocation(filePath, invocation))
                    {
                        continue;
                    }

                    Add(residuals, RuleRemainingReducedInvocation);
                    continue;
                }

                // Gate 6: global::-qualified call whose host name is unresolvable.
                // Overload failures against a RESOLVED host are not counted here
                // (gate 3 covers call shape/identity for our own rewrites).
                if (info.Symbol is null
                    && invocation.Expression is MemberAccessExpressionSyntax memberAccess
                    && memberAccess.GetFirstToken().IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.GlobalKeyword))
                {
                    var hostSymbol = model.GetSymbolInfo(memberAccess.Expression).Symbol;
                    if (hostSymbol is null)
                    {
                        Add(residuals, RuleUnresolvedStaticHost);
                    }
                }
            }

            // Gate 2: non-call member access still binding as (or offering) a
            // reduced extension method group. CS2J1207-paired exempts only.
            foreach (var memberAccess in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                if (memberAccess.Parent is InvocationExpressionSyntax parentInvocation
                    && parentInvocation.Expression == memberAccess)
                {
                    continue;
                }

                if (IsInsideExpressionTreeLambda(memberAccess, model))
                {
                    continue;
                }

                if (!IsReducedMethodGroup(model.GetSymbolInfo(memberAccess), out var hostName))
                {
                    continue;
                }

                if (options.LinqHostSet.Contains(hostName)
                    || exemptions.ClaimsMethodGroup(filePath, memberAccess))
                {
                    continue;
                }

                Add(residuals, RuleRemainingReducedMethodGroup);
            }
        }
    }

    private static void ScanExpressionTreeShapes(
        Compilation compilation,
        ExtensionMethodResolveOptions options,
        ExemptionIndex exemptions,
        IReadOnlyList<ExtensionRewrittenSiteKey> rewrittenSites,
        Dictionary<string, int> residuals,
        HashSet<string> excluded)
    {
        var sitesRoot = LongestCommonDirectory(rewrittenSites.Select(s => s.FilePath));

        foreach (var tree in compilation.SyntaxTrees)
        {
            if (IsExcluded(tree.FilePath, excluded))
            {
                continue;
            }

            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetCompilationUnitRoot();
            var filePath = tree.FilePath;
            var keysForTree = rewrittenSites
                .Where(s => MatchesRelative(NormalizePath(tree.FilePath),
                    RelativeToRoot(NormalizePath(s.FilePath), sitesRoot)))
                .ToList();

            foreach (var lambda in root.DescendantNodes().OfType<LambdaExpressionSyntax>())
            {
                if (!ExpressionTreeFacts.IsExpressionTreeLambda(model, lambda))
                {
                    continue;
                }

                // (a) No statement-shaped lowering may have happened inside an ET lambda.
                if (lambda.Body.DescendantNodesAndSelf().OfType<StatementSyntax>().Any())
                {
                    Add(residuals, RuleExpressionTreeShape);
                }

                // (b) Reduced invocations inside must be rollback/report-only/
                // chain exempt; LINQ hosts remain owned sugar.
                foreach (var invocation in lambda.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var info = model.GetSymbolInfo(invocation);
                    if (info.Symbol is IMethodSymbol { MethodKind: MethodKind.ReducedExtension } reduced
                        && reduced.ReducedFrom is { } unreduced)
                    {
                        if (!options.LinqHostSet.Contains(unreduced.ContainingType.ToDisplayString())
                            && !exemptions.ClaimsInvocation(filePath, invocation))
                        {
                            Add(residuals, RuleExpressionTreeShape);
                        }

                        continue;
                    }

                    // (d) An inserted global::-qualified ordinary call must pair
                    // with a rewritten site (identity itself is gate 3's job);
                    // files without any rewrite key cannot host our insertions.
                    if (info.Symbol is IMethodSymbol { MethodKind: MethodKind.Ordinary } ordinary
                        && invocation.Expression is MemberAccessExpressionSyntax memberAccess
                        && memberAccess.GetFirstToken().IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.GlobalKeyword)
                        && keysForTree.Count > 0)
                    {
                        var parameterTypes = ordinary.OriginalDefinition.Parameters
                            .Select(p => p.Type.ToDisplayString())
                            .ToList();
                        var paired = keysForTree.Any(s =>
                            s.MethodOriginalDefinitionDisplay == ordinary.OriginalDefinition.ToDisplayString()
                            && s.ArgumentCount == invocation.ArgumentList.Arguments.Count
                            && s.ParameterTypeDisplays.SequenceEqual(parameterTypes, StringComparer.Ordinal));
                        if (!paired)
                        {
                            Add(residuals, RuleExpressionTreeShape);
                        }
                    }
                }

                // (c) Reduced method groups inside ET are banned (CS2J1207 exempt).
                foreach (var memberAccess in lambda.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
                {
                    if (memberAccess.Parent is InvocationExpressionSyntax parentInvocation
                        && parentInvocation.Expression == memberAccess)
                    {
                        continue;
                    }

                    if (IsReducedMethodGroup(model.GetSymbolInfo(memberAccess), out var hostName)
                        && !options.LinqHostSet.Contains(hostName)
                        && !exemptions.ClaimsMethodGroup(filePath, memberAccess))
                    {
                        Add(residuals, RuleExpressionTreeShape);
                    }
                }
            }
        }
    }

    private static bool IsInsideExpressionTreeLambda(SyntaxNode node, SemanticModel model)
    {
        foreach (var ancestor in node.Ancestors().OfType<LambdaExpressionSyntax>())
        {
            if (ExpressionTreeFacts.IsExpressionTreeLambda(model, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsReducedMethodGroup(Microsoft.CodeAnalysis.SymbolInfo info, out string hostName)
    {
        hostName = string.Empty;
        if (info.Symbol is IMethodSymbol { MethodKind: MethodKind.ReducedExtension } reduced
            && reduced.ReducedFrom is { } unreduced)
        {
            hostName = unreduced.ContainingType.ToDisplayString();
            return true;
        }

        var candidate = info.CandidateSymbols
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m => m.MethodKind == MethodKind.ReducedExtension && m.ReducedFrom is not null);
        if (candidate?.ReducedFrom is { } candidateUnreduced)
        {
            hostName = candidateUnreduced.ContainingType.ToDisplayString();
            return true;
        }

        return false;
    }

    private static void ScanRewriteIdentity(
        Compilation compilation,
        IReadOnlyList<ExtensionRewrittenSiteKey> sites,
        Dictionary<string, int> residuals,
        HashSet<string> excluded)
    {
        if (sites.Count == 0)
        {
            return;
        }

        // Site paths come from the ORIGINAL compilation, while the rewritten
        // compilation parses the intermediate tree at a different root. Match
        // by the path relative to the longest common directory of the site
        // paths (e.g. SampleExtApp/Program.cs), which is identical across the
        // mirrored layouts.
        var commonRoot = LongestCommonDirectory(sites.Select(s => s.FilePath));

        foreach (var fileGroup in sites.GroupBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            var relativePath = RelativeToRoot(NormalizePath(fileGroup.Key), commonRoot);
            var tree = compilation.SyntaxTrees.FirstOrDefault(t =>
                MatchesRelative(NormalizePath(t.FilePath), relativePath));

            // M4: the file was reverted byte-for-byte by the build-failure
            // rollback; its rewrite keys are no longer owed by the candidate.
            if (tree is not null && IsExcluded(tree.FilePath, excluded))
            {
                continue;
            }

            if (tree is null)
            {
                Add(residuals, RuleRewriteIdentity, fileGroup.Count());
                continue;
            }

            var model = compilation.GetSemanticModel(tree);

            // All ordinary static invocations in the file grouped by their
            // original definition display; each rewritten site pairs with one
            // node from its signature group (ordered by span).
            var nodesBySignature = new Dictionary<string, List<(InvocationExpressionSyntax Node, IMethodSymbol Symbol)>>(
                StringComparer.Ordinal);
            foreach (var invocation in tree.GetCompilationUnitRoot().DescendantNodes()
                         .OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol
                    {
                        MethodKind: MethodKind.Ordinary,
                    } method)
                {
                    continue;
                }

                var signature = method.OriginalDefinition.ToDisplayString();
                if (!nodesBySignature.TryGetValue(signature, out var list))
                {
                    list = new List<(InvocationExpressionSyntax, IMethodSymbol)>();
                    nodesBySignature[signature] = list;
                }

                list.Add((invocation, method));
            }

            foreach (var list in nodesBySignature.Values)
            {
                list.Sort((a, b) => a.Node.SpanStart.CompareTo(b.Node.SpanStart));
            }

            foreach (var key in fileGroup.OrderBy(s => s.SpanStart))
            {
                if (!nodesBySignature.TryGetValue(key.MethodOriginalDefinitionDisplay, out var candidates)
                    || candidates.Count == 0)
                {
                    Add(residuals, RuleRewriteIdentity);
                    continue;
                }

                var (node, symbol) = candidates[0];
                candidates.RemoveAt(0);

                var reboundParameterTypes = symbol.OriginalDefinition.Parameters
                    .Select(p => p.Type.ToDisplayString())
                    .ToList();

                if (node.ArgumentList.Arguments.Count != key.ArgumentCount
                    || reboundParameterTypes.Count != key.ParameterTypeDisplays.Count
                    || !reboundParameterTypes.SequenceEqual(key.ParameterTypeDisplays, StringComparer.Ordinal))
                {
                    Add(residuals, RuleRewriteIdentity);
                }
            }
        }
    }

    private static void ScanThisModifierPreservation(
        Compilation compilation,
        int baselineCount,
        int expectedAddedCount,
        Dictionary<string, int> residuals,
        HashSet<string> excluded)
    {
        var preserved = 0;
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (IsExcluded(tree.FilePath, excluded))
            {
                continue;
            }

            preserved += CountExtensionThisModifiers(tree);
        }

        // Rolled-back files replay byte-for-byte, so their this modifiers are
        // present on BOTH sides and must be excluded from both counts.
        if (baselineCount > 0 || expectedAddedCount > 0 || preserved > 0)
        {
            // Baseline passed in was computed over the ORIGINAL full tree;
            // subtract the share living in excluded (reverted) trees.
            var excludedShare = excluded.Count == 0
                ? 0
                : compilation.SyntaxTrees
                    .Where(t => IsExcluded(t.FilePath, excluded))
                    .Sum(CountExtensionThisModifiers);
            // expectedAddedCount is already scoped to non-reverted files:
            // C49 materialization turns block instance members (methods and
            // property get/set pairs) into classic this-parameter methods.
            var delta = Math.Abs((baselineCount - excludedShare + expectedAddedCount) - preserved);
            if (delta > 0)
            {
                Add(residuals, RuleThisModifierPreserved, delta);
            }
        }
    }

    /// <summary>
    /// Count extension-method first parameters carrying the <c>this</c>
    /// modifier in one syntax tree. Classic extension form (a static class
    /// method whose first parameter has the <c>this</c> token); C# 14
    /// extension blocks only reach this tree after C49 materialization, where
    /// their instance members carry the classic form by construction.
    /// </summary>
    public static int CountExtensionThisModifiers(SyntaxTree tree)
    {
        var root = tree.GetCompilationUnitRoot();
        var count = 0;
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var parameters = method.ParameterList.Parameters;
            if (parameters.Count > 0
                && parameters[0].Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ThisKeyword)))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Exemption pairing: chain-region exempts (1205/ET-conditional) cover
    /// every residual node within their source span; exact-site exempts
    /// (1204/1207/1210) pair one node by file + compact text + span order.
    /// </summary>
    private sealed class ExemptionIndex
    {
        private readonly List<(string FilePath, ExtensionExemptKind Kind, int Start, int End, string Compact)> _regions = new();
        private readonly Dictionary<string, Queue<(int Start, string Compact)>> _exact = new(StringComparer.Ordinal);

        public ExemptionIndex(IReadOnlyList<ExtensionExemptSite> exempts)
        {
            AllPaths = exempts.Select(e => e.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            // Original-tree paths and mirrored intermediate-tree paths differ in
            // their absolute prefixes (gate 3 uses the same relative matching).
            var commonRoot = LongestCommonDirectory(AllPaths);

            foreach (var exempt in exempts.OrderBy(e => e.SpanStart))
            {
                var relativeFile = RelativeToRoot(NormalizePath(exempt.FilePath), commonRoot);

                if (exempt.Kind is ExtensionExemptKind.NullConditionalUnliftable
                    or ExtensionExemptKind.ExprTreeNullConditional
                    && exempt.SpanLength > 0)
                {
                    _regions.Add((
                        relativeFile,
                        exempt.Kind,
                        exempt.SpanStart,
                        exempt.SpanStart + exempt.SpanLength,
                        exempt.CompactText));
                    continue;
                }

                var key = relativeFile + "|" + (int)exempt.Kind + "|" + exempt.CompactText;
                if (!_exact.TryGetValue(key, out var queue))
                {
                    queue = new Queue<(int, string)>();
                    _exact[key] = queue;
                }

                queue.Enqueue((exempt.SpanStart, exempt.CompactText));
            }
        }

        public IReadOnlyList<string> AllPaths { get; }

        public bool ClaimsInvocation(string treeFilePath, InvocationExpressionSyntax node)
        {
            var normalized = NormalizePath(treeFilePath);
            var span = node.Span;
            if (_regions.Any(r => MatchesRelative(normalized, r.FilePath)
                    && r.Start <= span.Start && span.End <= r.End))
            {
                return true;
            }

            return ClaimExact(normalized, ExtensionExemptKind.RefReceiverMismatch, node)
                   || ClaimExact(normalized, ExtensionExemptKind.ExprTreeRollback, node)
                   || ClaimExact(normalized, ExtensionExemptKind.NullConditionalUnliftable, node)
                   || ClaimExact(normalized, ExtensionExemptKind.ExprTreeNullConditional, node)
                   || ClaimExact(normalized, ExtensionExemptKind.MixedLinqChain, node)
                   || ClaimExact(normalized, ExtensionExemptKind.HostOverrideSkip, node);
        }

        public bool ClaimsMethodGroup(string treeFilePath, MemberAccessExpressionSyntax node)
            => ClaimExact(NormalizePath(treeFilePath), ExtensionExemptKind.ReducedMethodGroup, node);

        private bool ClaimExact(string normalizedFile, ExtensionExemptKind kind, SyntaxNode node)
        {
            var compact = CompactSource(node.ToString());
            // Match by suffix-relative path, mirroring ScanRewriteIdentity:
            // exact keys are stored relative to the exempts' common directory.
            var match = _exact
                .Where(kv => kv.Key.EndsWith("|" + (int)kind + "|" + compact, StringComparison.Ordinal)
                    && MatchesRelative(normalizedFile, kv.Key[..kv.Key.IndexOf('|')]))
                .Select(kv => kv.Value)
                .FirstOrDefault(q => q.Count > 0);
            if (match is null)
            {
                return false;
            }

            // Exempts were stored in span order; residual nodes replay the same
            // source order, so dequeue head.
            match.Dequeue();
            return true;
        }
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    /// <summary>
    /// Longest common DIRECTORY prefix (segment boundary) of the given paths;
    /// for a single path the result is its containing directory.
    /// </summary>
    private static string LongestCommonDirectory(IEnumerable<string> paths)
    {
        var normalized = paths.Select(NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (normalized.Count == 0)
        {
            return string.Empty;
        }

        var split = normalized
            .Select(p => p.Split('/', StringSplitOptions.RemoveEmptyEntries))
            .ToList();
        var shortest = split.Min(s => s.Length);

        var common = 0;
        for (var i = 0; i < shortest - 1; i++)
        {
            var segment = split[0][i];
            if (split.All(s => string.Equals(s[i], segment, StringComparison.OrdinalIgnoreCase)))
            {
                common++;
            }
            else
            {
                break;
            }
        }

        return string.Join('/', split[0].Take(common));
    }

    private static string RelativeToRoot(string normalizedPath, string commonRoot)
    {
        if (commonRoot.Length == 0)
        {
            return normalizedPath;
        }

        var prefix = commonRoot.EndsWith('/') ? commonRoot : commonRoot + "/";
        return normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? normalizedPath[prefix.Length..]
            : normalizedPath;
    }

    private static bool MatchesRelative(string normalizedTreePath, string relativePath)
    {
        if (string.Equals(normalizedTreePath, relativePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedTreePath.EndsWith(
            "/" + relativePath,
            StringComparison.OrdinalIgnoreCase);
    }

    private static void Add(Dictionary<string, int> residuals, string rule, int count = 1)
    {
        residuals.TryGetValue(rule, out var current);
        residuals[rule] = current + count;
    }
}
