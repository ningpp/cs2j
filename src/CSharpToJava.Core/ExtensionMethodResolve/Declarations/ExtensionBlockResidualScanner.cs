using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CSharpToJava.Core.ExtensionMethodResolve.Declarations;

/// <summary>Residual counts after C49 block materialization (post-rewrite recompile).</summary>
internal sealed record BlockResidualResult(
    int RemainingBlocks,
    int UnpairedExtensionSites,
    int IdentityFailures,
    int IndexerRejectionMismatches)
{
    public bool HasFailures
        => RemainingBlocks > 0
           || UnpairedExtensionSites > 0
           || IdentityFailures > 0
           || IndexerRejectionMismatches > 0;

    public Dictionary<string, int> ToCounts()
        => new()
        {
            ["remaining-block-declarations"] = RemainingBlocks,
            ["unpaired-extension-sites"] = UnpairedExtensionSites,
            ["identity-failures"] = IdentityFailures,
            ["indexer-rejection-mismatches"] = IndexerRejectionMismatches,
        };
}

/// <summary>
/// Post-rewrite scan for C49 (§4.6): verifies
/// (1) no extension block nodes remain unless rejected (diagnostic-paired)
/// or the file was rolled back;
/// (2) no invocation/member access/operator still binds to a
/// <c>TypeKind.Extension</c> container without a deferred/external pairing;
/// (3) rewritten sites recompile to the generated target member
/// (OriginalDefinition identity: methods, properties, operators, statics);
/// (4) indexer rejection diagnostics pair one-to-one with indexer
/// declarations left inside retained blocks.
/// The fifth assertion (second run zero diff) is enforced by tests.
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
        var indexerMismatches = 0;

        foreach (var tree in rewritten.SyntaxTrees)
        {
            if (excludedFiles is not null
                && excludedFiles.Contains(tree.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var root = tree.GetCompilationUnitRoot();
            var model = rewritten.GetSemanticModel(tree);
            analyses.TryGetValue(tree, out var analysis);

            // Rejected blocks are paired with an Error diagnostic at the
            // block's line; collect which lines carry a block-level rejection.
            var rejectedBlockLines = analysis is not null
                ? new HashSet<int>(analysis.Diagnostics
                    .Where(d => d.Severity == ExtensionSeverity.Error
                                && (d.Code == ExtensionBlockCodes.Unsupported
                                    || d.Code == ExtensionBlockCodes.ParseProbeFailed
                                    || d.Code == ExtensionBlockCodes.PropertyChainGate
                                    || d.Code == ExtensionBlockCodes.UnsupportedReceiver
                                    || d.Code == ExtensionBlockCodes.SignatureCollision))
                    .Select(d => d.Line ?? -1))
                : new HashSet<int>();

            var retainedBlocks = new List<ExtensionBlockDeclarationSyntax>();
            foreach (var block in ExtensionBlockSyntaxFacts.EnumerateBlocks(root))
            {
                var line = block.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                if (!rejectedBlockLines.Contains(line))
                {
                    remainingBlocks++;
                }

                retainedBlocks.Add(block);
            }

            // (4) Indexer rejection count pairing.
            indexerMismatches += VerifyIndexerPairing(root, retainedBlocks, analysis);

            // Paired spans: deferred sites plus external metadata reports.
            var pairedSpans = new HashSet<int>();
            if (analysis is not null)
            {
                foreach (var deferred in analysis.DeferredSites)
                {
                    pairedSpans.Add(deferred.Site.SpanStart);
                }

                foreach (var external in analysis.ExternalSites)
                {
                    pairedSpans.Add(external.Site.SpanStart);
                }
            }

            // (2a) invocation sites (block methods).
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(invocation).Symbol is IMethodSymbol
                    {
                        MethodKind: MethodKind.Ordinary,
                    } method
                    && method.ContainingType.TypeKind == TypeKind.Extension
                    && !pairedSpans.Contains(invocation.SpanStart))
                {
                    unpairedSites++;
                }
            }

            // (2b) property accesses (member access + member binding).
            var propertyAccesses = root.DescendantNodes()
                .OfType<MemberAccessExpressionSyntax>()
                .Cast<ExpressionSyntax>()
                .Concat(root.DescendantNodes().OfType<MemberBindingExpressionSyntax>());
            foreach (var access in propertyAccesses)
            {
                if (model.GetSymbolInfo(access).Symbol is { } symbol
                    && IsExtensionContainerSymbol(symbol)
                    && !pairedSpans.Contains(access.SpanStart))
                {
                    unpairedSites++;
                }
            }

            // (2c) operator sites still binding to extension containers.
            foreach (var binary in root.DescendantNodes().OfType<BinaryExpressionSyntax>())
            {
                if (model.GetSymbolInfo(binary).Symbol is IMethodSymbol
                    {
                        MethodKind: MethodKind.UserDefinedOperator,
                    } op
                    && op.ContainingType.TypeKind == TypeKind.Extension
                    && !pairedSpans.Contains(binary.SpanStart))
                {
                    unpairedSites++;
                }
            }

            foreach (var unary in root.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>())
            {
                if (model.GetSymbolInfo(unary).Symbol is IMethodSymbol
                    {
                        MethodKind: MethodKind.UserDefinedOperator,
                    } op
                    && op.ContainingType.TypeKind == TypeKind.Extension
                    && !pairedSpans.Contains(unary.SpanStart))
                {
                    unpairedSites++;
                }
            }

            foreach (var compound in root.DescendantNodes()
                         .OfType<AssignmentExpressionSyntax>()
                         .Where(a => !a.IsKind(SyntaxKind.SimpleAssignmentExpression)))
            {
                if (model.GetOperation(compound) is ICompoundAssignmentOperation
                    {
                        OperatorMethod.ContainingType.TypeKind: TypeKind.Extension,
                    }
                    && !pairedSpans.Contains(compound.SpanStart))
                {
                    unpairedSites++;
                }
            }

            if (analysis is not null)
            {
                identityFailures += VerifySiteIdentity(rewritten, model, root, analysis);
            }
        }

        return new BlockResidualResult(
            remainingBlocks, unpairedSites, identityFailures, indexerMismatches);
    }

    private static int VerifyIndexerPairing(
        CompilationUnitSyntax root,
        IReadOnlyList<ExtensionBlockDeclarationSyntax> retainedBlocks,
        BlockAnalysisResult? analysis)
    {
        var indexerLines = retainedBlocks
            .SelectMany(b => ExtensionBlockSyntaxFacts.GetMembers(b))
            .OfType<IndexerDeclarationSyntax>()
            .Select(i => i.GetLocation().GetLineSpan().StartLinePosition.Line + 1)
            .ToHashSet();

        var indexerCount = indexerLines.Count;
        var paired = analysis?.Diagnostics
            .Count(d => d.Severity == ExtensionSeverity.Error
                        && d.Code == ExtensionBlockCodes.Unsupported
                        && d.Line is { } line
                        && indexerLines.Contains(line))
            ?? 0;

        return indexerCount == paired ? 0 : Math.Abs(indexerCount - paired);
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
        BlockAnalysisResult analysis)
    {
        var failures = 0;

        // One host lookup per distinct block-parent display name.
        var hosts = new Dictionary<string, INamedTypeSymbol?>(StringComparer.Ordinal);
        foreach (var name in analysis.BlockPlans.Select(b => b.Host.ToDisplayString()).Distinct())
        {
            hosts[name] = FindHost(rewritten, name);
        }

        INamedTypeSymbol? HostFor(BlockPlan block)
            => hosts.TryGetValue(block.Host.ToDisplayString(), out var host) ? host : null;

        // ---- Instance methods ----
        foreach (var site in analysis.MethodSites)
        {
            var node = root.GetAnnotatedNodes(site.Annotation)
                .OfType<InvocationExpressionSyntax>().FirstOrDefault();
            var expected = FindGeneratedMember(HostFor(site.Block), site.Member.Name);
            if (!HasIdentity(model, node, expected))
            {
                failures++;
            }
        }

        // ---- Property reads / writes / compounds ----
        foreach (var site in analysis.ReadSites)
        {
            var node = root.GetAnnotatedNodes(site.Annotation)
                .OfType<InvocationExpressionSyntax>().FirstOrDefault();
            var expected = FindGeneratedMember(HostFor(site.Block),
                AccessorName(site.Member.Name, getter: true));
            if (!HasIdentity(model, node, expected))
            {
                failures++;
            }
        }

        foreach (var site in analysis.WriteSites)
        {
            var node = root.GetAnnotatedNodes(site.Annotation)
                .OfType<InvocationExpressionSyntax>().FirstOrDefault();
            var expected = FindGeneratedMember(HostFor(site.Block),
                AccessorName(site.Member.Name, getter: false));
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
                AccessorName(compound.Member.Name, getter: false));
            if (!HasIdentity(model, node, expected))
            {
                failures++;
            }
        }

        // ---- M2/M3: operators ----
        foreach (var site in analysis.BinaryOperatorSites)
        {
            var node = root.GetAnnotatedNodes(site.Annotation)
                .OfType<InvocationExpressionSyntax>().FirstOrDefault();
            var expected = FindGeneratedMember(HostFor(site.Block), site.Member.GeneratedName);
            if (!HasIdentity(model, node, expected))
            {
                failures++;
            }
        }

        foreach (var site in analysis.UnaryOperatorSites)
        {
            var node = root.GetAnnotatedNodes(site.Annotation)
                .OfType<InvocationExpressionSyntax>().FirstOrDefault();
            var expected = FindGeneratedMember(HostFor(site.Block), site.Member.GeneratedName);
            if (!HasIdentity(model, node, expected))
            {
                failures++;
            }
        }

        foreach (var site in analysis.OperatorCompoundSites)
        {
            var annotated = root.GetAnnotatedNodes(site.Annotation).FirstOrDefault();
            var node = annotated?.DescendantNodesAndSelf()
                .OfType<InvocationExpressionSyntax>()
                .FirstOrDefault();
            var expected = FindGeneratedMember(HostFor(site.Block), site.Member.GeneratedName);
            if (!HasIdentity(model, node, expected))
            {
                failures++;
            }
        }

        // ---- M2/M3: static members ----
        foreach (var site in analysis.StaticMethodSites)
        {
            var node = root.GetAnnotatedNodes(site.Annotation)
                .OfType<InvocationExpressionSyntax>().FirstOrDefault();
            var expected = FindGeneratedMember(HostFor(site.Block), site.Member.Name);
            if (!HasIdentity(model, node, expected))
            {
                failures++;
            }
        }

        foreach (var site in analysis.StaticPropertyReadSites)
        {
            var node = root.GetAnnotatedNodes(site.Annotation)
                .OfType<InvocationExpressionSyntax>().FirstOrDefault();
            var expected = FindGeneratedMember(HostFor(site.Block),
                AccessorName(site.Member.Name, getter: true));
            if (!HasIdentity(model, node, expected))
            {
                failures++;
            }
        }

        foreach (var site in analysis.StaticPropertyWriteSites)
        {
            var node = root.GetAnnotatedNodes(site.Annotation)
                .OfType<InvocationExpressionSyntax>().FirstOrDefault();
            var expected = FindGeneratedMember(HostFor(site.Block),
                AccessorName(site.Member.Name, getter: false));
            if (!HasIdentity(model, node, expected))
            {
                failures++;
            }
        }

        return failures;
    }

    private static string AccessorName(string propertyName, bool getter)
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
