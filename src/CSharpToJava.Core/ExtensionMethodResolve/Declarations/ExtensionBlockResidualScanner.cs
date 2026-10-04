using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExtensionMethodResolve.Declarations;

/// <summary>Residual counts after C49 block materialization (post-rewrite recompile).</summary>
internal sealed record BlockResidualResult(
    int RemainingBlocks,
    int UnpairedExtensionSites,
    int IdentityFailures)
{
    public bool HasFailures => RemainingBlocks > 0 || UnpairedExtensionSites > 0 || IdentityFailures > 0;

    public Dictionary<string, int> ToCounts()
        => new()
        {
            ["remaining-block-declarations"] = RemainingBlocks,
            ["unpaired-extension-sites"] = UnpairedExtensionSites,
            ["identity-failures"] = IdentityFailures,
        };
}

/// <summary>
/// Post-rewrite scan for C49: verifies (1) no extension block nodes remain
/// unless they were rejected (diagnostic-paired) or the file was rolled back;
/// (2) no call/member-access still binds to a <c>TypeKind.Extension</c>
/// container without a deferred-site pairing; (3) rewritten sites recompile
/// to the generated target member (OriginalDefinition identity).
/// </summary>
internal static class ExtensionBlockResidualScanner
{
    public static BlockResidualResult Scan(
        Compilation rewritten,
        IReadOnlyDictionary<SyntaxTree, BlockAnalysisResult> analyses,
        IReadOnlyCollection<string>? excludedFiles)
    {
        ArgumentNullException.ThrowIfNull(rewritten);

        var remainingBlocks = 0;
        var unpairedSites = 0;
        var identityFailures = 0;

        foreach (var tree in rewritten.SyntaxTrees)
        {
            if (excludedFiles is not null
                && excludedFiles.Contains(tree.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var root = tree.GetCompilationUnitRoot();
            var model = rewritten.GetSemanticModel(tree);

            // Rejected blocks (indexers/unsupported members/gate) are paired
            // with an Error diagnostic; count which block nodes remain paired.
            var rejectedBlockSpans = analyses.TryGetValue(tree, out var analysis)
                ? new HashSet<int>(analysis.Diagnostics
                    .Where(d => d.Severity == ExtensionSeverity.Error
                                && (d.Code == ExtensionBlockCodes.Unsupported
                                    || d.Code == ExtensionBlockCodes.ParseProbeFailed
                                    || d.Code == ExtensionBlockCodes.PropertyChainGate))
                    .Select(d => d.Line ?? -1))
                : new HashSet<int>();

            foreach (var block in ExtensionBlockSyntaxFacts.EnumerateBlocks(root))
            {
                var line = block.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                if (!rejectedBlockSpans.Contains(line))
                {
                    remainingBlocks++;
                }
            }

            var deferredSpans = analysis is not null
                ? new HashSet<int>(analysis.DeferredSites.Select(d => d.Site.SpanStart))
                : new HashSet<int>();

            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(invocation).Symbol is IMethodSymbol
                    {
                        MethodKind: MethodKind.Ordinary,
                    } method
                    && method.ContainingType.TypeKind == TypeKind.Extension
                    && !deferredSpans.Contains(invocation.SpanStart))
                {
                    unpairedSites++;
                }
            }

            var propertyAccesses = root.DescendantNodes()
                .OfType<MemberAccessExpressionSyntax>()
                .Cast<ExpressionSyntax>()
                .Concat(root.DescendantNodes().OfType<MemberBindingExpressionSyntax>());
            foreach (var access in propertyAccesses)
            {
                if (model.GetSymbolInfo(access).Symbol is { } symbol
                    && IsExtensionContainerSymbol(symbol)
                    && !deferredSpans.Contains(access.SpanStart))
                {
                    unpairedSites++;
                }
            }

            if (analysis is not null)
            {
                identityFailures += VerifySiteIdentity(rewritten, model, root, tree, analysis);
            }
        }

        return new BlockResidualResult(remainingBlocks, unpairedSites, identityFailures);
    }

    private static bool IsExtensionContainerSymbol(ISymbol symbol)
        => symbol switch
        {
            IPropertySymbol property => property.ContainingType.TypeKind == TypeKind.Extension,
            _ => false,
        };

    private static int VerifySiteIdentity(
        Compilation rewritten,
        SemanticModel model,
        CompilationUnitSyntax root,
        SyntaxTree tree,
        BlockAnalysisResult analysis)
    {
        var failures = 0;

        // One host lookup per distinct block-parent display name (one tree may
        // host blocks in multiple static classes).
        var hosts = new Dictionary<string, INamedTypeSymbol?>(StringComparer.Ordinal);
        foreach (var name in analysis.BlockPlans.Select(b => b.Host.ToDisplayString()).Distinct())
        {
            hosts[name] = FindHost(rewritten, name);
        }

        INamedTypeSymbol? HostFor(BlockPlan block)
            => hosts.TryGetValue(block.Host.ToDisplayString(), out var host) ? host : null;

        foreach (var methodSite in analysis.MethodSites)
        {
            var node = root.GetAnnotatedNodes(methodSite.Annotation)
                .OfType<InvocationExpressionSyntax>().FirstOrDefault();
            var expected = FindGeneratedMember(HostFor(methodSite.Block), methodSite.Member.Name);
            if (!HasIdentity(model, node, expected))
            {
                failures++;
            }
        }

        foreach (var readSite in analysis.ReadSites)
        {
            var node = root.GetAnnotatedNodes(readSite.Annotation)
                .OfType<InvocationExpressionSyntax>().FirstOrDefault();
            var expected = FindGeneratedMember(HostFor(readSite.Block),
                AccessorsToMethodsName(readSite.Member.Name, getter: true));
            if (!HasIdentity(model, node, expected))
            {
                failures++;
            }
        }

        foreach (var writeSite in analysis.WriteSites)
        {
            var node = root.GetAnnotatedNodes(writeSite.Annotation)
                .OfType<InvocationExpressionSyntax>().FirstOrDefault();
            var expected = FindGeneratedMember(HostFor(writeSite.Block),
                AccessorsToMethodsName(writeSite.Member.Name, getter: false));
            if (!HasIdentity(model, node, expected))
            {
                failures++;
            }
        }

        foreach (var compound in analysis.CompoundSites)
        {
            var annotatedStatement = root.GetAnnotatedNodes(compound.StatementAnnotation).FirstOrDefault();
            var node = annotatedStatement?.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>()
                .LastOrDefault();
            var expected = FindGeneratedMember(HostFor(compound.Block),
                AccessorsToMethodsName(compound.Member.Name, getter: false));
            if (!HasIdentity(model, node, expected))
            {
                failures++;
            }
        }

        return failures;
    }

    private static string AccessorsToMethodsName(string propertyName, bool getter)
        => (getter ? "get_" : "set_") + propertyName;

    private static bool HasIdentity(
        SemanticModel model,
        InvocationExpressionSyntax? node,
        IMethodSymbol? expected)
    {
        if (node is null || expected is null)
        {
            return false;
        }

        var rebound = model.GetSymbolInfo(node).Symbol;
        return rebound is not null
               && SymbolEqualityComparer.Default.Equals(rebound.OriginalDefinition, expected.OriginalDefinition);
    }

    private static IMethodSymbol? FindGeneratedMember(INamedTypeSymbol? host, string metadataName)
        => host?.GetMembers()
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m => m.MetadataName == metadataName);

    private static INamedTypeSymbol? FindHost(Compilation rewritten, string expectedName)
    {
        foreach (var type in EnumerateAllTypes(rewritten.GlobalNamespace))
        {
            if (type.ToDisplayString() == expectedName)
            {
                return type;
            }
        }

        return null;
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateAllTypes(INamespaceSymbol @namespace)
    {
        foreach (var type in @namespace.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in EnumerateNested(type))
            {
                yield return nested;
            }
        }

        foreach (var nestedNamespace in @namespace.GetNamespaceMembers())
        {
            foreach (var type in EnumerateAllTypes(nestedNamespace))
            {
                yield return type;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateNested(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            yield return nested;
            foreach (var deeper in EnumerateNested(nested))
            {
                yield return deeper;
            }
        }
    }
}
