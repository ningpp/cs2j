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
/// AfterFiles gate (design §4.5). M1 implements:
/// (1) remaining reduced invocations (LINQ-host set exempt) — must be 0;
/// (3) rewritten-site rebinding identity: rebound <c>OriginalDefinition</c> and
///     parameter sequence must match the pre-rewrite unreduced symbol;
/// (6) orphan static calls: a <c>global::</c>-qualified call whose host name
///     does not resolve in the new compilation — must be 0.
/// Gates 2/4/5 land with M2 (method groups, expression-tree identity, this
/// modifiers).
/// </summary>
public static class ExtensionMethodResidualScanner
{
    public const string RuleRemainingReducedInvocation = "remaining-reduced-invocation";
    public const string RuleRewriteIdentity = "rewrite-identity";
    public const string RuleUnresolvedStaticHost = "unresolved-static-host";

    public static Dictionary<string, int> Scan(
        Compilation rewritten,
        IReadOnlyList<ExtensionRewrittenSiteKey> rewrittenSites,
        ExtensionMethodResolveOptions options)
    {
        ArgumentNullException.ThrowIfNull(rewritten);
        ArgumentNullException.ThrowIfNull(rewrittenSites);
        ArgumentNullException.ThrowIfNull(options);

        var residuals = new Dictionary<string, int>(StringComparer.Ordinal);

        ScanCallShapes(rewritten, options, residuals);
        ScanRewriteIdentity(rewritten, rewrittenSites, residuals);

        return residuals;
    }

    private static void ScanCallShapes(
        Compilation compilation,
        ExtensionMethodResolveOptions options,
        Dictionary<string, int> residuals)
    {
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var invocation in tree.GetCompilationUnitRoot().DescendantNodes()
                         .OfType<InvocationExpressionSyntax>())
            {
                var info = model.GetSymbolInfo(invocation);

                // Gate 1: a reduced invocation that survived the rewrite. LINQ
                // hosts are owned by E16/C26 and stay reduced on purpose.
                if (info.Symbol is IMethodSymbol { MethodKind: MethodKind.ReducedExtension } reduced
                    && reduced.ReducedFrom is { } unreduced)
                {
                    var hostName = unreduced.ContainingType.ToDisplayString();
                    if (!options.LinqHostSet.Contains(hostName))
                    {
                        Add(residuals, RuleRemainingReducedInvocation);
                    }

                    continue;
                }

                // Gate 6: global::-qualified call whose host name is unresolvable.
                // Overload failures against a RESOLVED host are not counted here
                // (gate 3 covers call shape/identity for our own rewrites).
                if (info.Symbol is null
                    && invocation.Expression is MemberAccessExpressionSyntax memberAccess
                    && memberAccess.GetFirstToken().IsKind(SyntaxKind.GlobalKeyword))
                {
                    var hostSymbol = model.GetSymbolInfo(memberAccess.Expression).Symbol;
                    if (hostSymbol is null)
                    {
                        Add(residuals, RuleUnresolvedStaticHost);
                    }
                }
            }
        }
    }

    private static void ScanRewriteIdentity(
        Compilation compilation,
        IReadOnlyList<ExtensionRewrittenSiteKey> sites,
        Dictionary<string, int> residuals)
    {
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
