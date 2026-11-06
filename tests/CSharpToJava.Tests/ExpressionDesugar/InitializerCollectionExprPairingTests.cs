using CSharpToJava.Core.ExpressionDesugar;
using CSharpToJava.Core.ExpressionDesugar.Initializers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.ExpressionDesugar;

/// <summary>
/// W-17②/G-41 (upgrade plan §6.4-R0): the C24 whole-tree gate counts every
/// on-disk initializer node of the three residual kinds, but C24 analyzes
/// BEFORE the sibling C36 collection-expression rule rewrites the shared
/// progressive tree — so the initializer syntax C36 EMITS
/// (<c>new C&lt;T&gt;(){ … }</c> wrappers and the
/// <c>var __ed_n = new List&lt;T&gt;(){}</c> temp declaration) was silently
/// unpaired and aborted the chain as "residual without accounting" (the rt10
/// <c>JsonSchemaExporter.cs</c> 28/22 and <c>PropertyRefCacheBuilder.cs</c>
/// 1/0 CS2J0545 aborts). These tests pin the corrected pairing:
/// <see cref="InitializerStatistics.EmittedByCollectionExprNodes"/> counts
/// the emitted nodes so <see cref="InitializerStatistics.PairedResidualNodes"/>
/// matches the on-disk truth.
/// </summary>
public sealed class InitializerCollectionExprPairingTests
{
    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Array).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location),
        MetadataReference.CreateFromFile(Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
    };

    private static ExpressionDesugarResult Run(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "c24-c36-pairing-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Disable));
        var model = compilation.GetSemanticModel(tree);
        return new ExpressionDesugarPreprocessor().Preprocess(
            source, model,
            new ExpressionDesugarOptions
            {
                // Mirrors the chain configuration where both rules are active.
                Initializers = new InitializerOptions { Strict = true },
            });
    }

    /// <summary>
    /// Re-parses the pass output AS WRITTEN and counts the three residual
    /// node kinds — the exact computation the adapter's whole-tree gate
    /// (AfterFilesAsync) performs on disk.
    /// </summary>
    private static int CountOnDiskResidualNodes(string output)
    {
        var root = CSharpSyntaxTree
            .ParseText(output, new CSharpParseOptions(LanguageVersion.Preview))
            .GetCompilationUnitRoot();
        return root.DescendantNodes().Count(InitializerSyntaxFacts.IsResidualNode);
    }

    // ---- 1. The PropertyRefCacheBuilder 1/0 shape (red→green core) ----

    [Fact]
    public void SpreadStatementSequenceEmission_IsCountedInTheInitializerPairing()
    {
        // C36-only file (no C24 trigger syntax in source): the spread-bearing
        // site lowers to `var __ed_0 = new List<int>() { }` + AddRange +
        // ToArray() — the emitted `{ }` is exactly the
        // PropertyRefCacheBuilder.cs 1-residual / 0-paired mismatch shape
        // from the rt10 run (array target => List temp carries `{ }`).
        var result = Run("""
            #pragma warning disable
            using System.Collections.Generic;
            public static class Test {
                public static int[] M(List<int> src) => [.. src];
            }
            """);

        Assert.True(result.Changed);
        var stats = result.Statistics.Initializers;
        Assert.Equal(0, stats.PreservedResidualNodes);
        Assert.Equal(0, stats.DeferredResidualNodes);
        Assert.Equal(1, stats.EmittedByCollectionExprNodes);
        Assert.Equal(1, stats.PairedResidualNodes);

        // The on-disk truth must match the pairing (tree-gate invariant).
        Assert.Equal(1, CountOnDiskResidualNodes(result.OutputCode));
        Assert.Contains(
            "new global::System.Collections.Generic.List<int>() { }",
            result.OutputCode,
            StringComparison.Ordinal);
    }

    // ---- 2. Zero-temp mutable collection wrapper ----

    [Fact]
    public void ZeroTempMutableCollectionEmission_IsCountedInTheInitializerPairing()
    {
        var result = Run("""
            #pragma warning disable
            using System.Collections.Generic;
            public static class Test {
                public static List<int> M() => [1, 2];
            }
            """);

        Assert.True(result.Changed);
        var stats = result.Statistics.Initializers;
        Assert.Equal(1, stats.EmittedByCollectionExprNodes);
        Assert.Equal(1, stats.PairedResidualNodes);
        Assert.Equal(1, CountOnDiskResidualNodes(result.OutputCode));
    }

    // ---- 3. Array targets emit no C24-residual initializer (negative) ----

    [Fact]
    public void ZeroTempArrayEmission_ContributesNothingToTheInitializerPairing()
    {
        // `new int[]{ 1, 2 }` — the ArrayInitializer kind is excluded from
        // C24's residual kinds, so the pairing must stay zero.
        var result = Run("""
            #pragma warning disable
            public static class Test {
                public static int[] M() => [1, 2];
            }
            """);

        Assert.True(result.Changed);
        var stats = result.Statistics.Initializers;
        Assert.Equal(0, stats.EmittedByCollectionExprNodes);
        Assert.Equal(0, stats.PairedResidualNodes);
        Assert.Equal(0, CountOnDiskResidualNodes(result.OutputCode));
    }

    // ---- 4. Nested collection expressions: inner + outer, no double count ----

    [Fact]
    public void NestedCollectionExpressions_CountInnerAndWrapperWithoutDoubleCounting()
    {
        // [[1], [2]] over List<List<int>>: two inner emissions (counted at
        // their own replacement) plus the outer wrapper (isolated by the
        // replacement-vs-visited diff) = 3 emitted initializer nodes.
        var result = Run("""
            #pragma warning disable
            using System.Collections.Generic;
            public static class Test {
                public static List<List<int>> M() => [[1], [2]];
            }
            """);

        Assert.True(result.Changed);
        var stats = result.Statistics.Initializers;
        Assert.Equal(3, stats.EmittedByCollectionExprNodes);
        Assert.Equal(3, stats.PairedResidualNodes);
        Assert.Equal(3, CountOnDiskResidualNodes(result.OutputCode));
    }

    // ---- 5. Original initializer-bearing element: deferred + emitted, no overlap ----

    [Fact]
    public void OriginalInitializerElement_IsDeferredPaired_AndTheWrapperIsEmittedPaired()
    {
        // The element `new Elem { X = 1 }` exists in the ORIGINAL tree, so
        // C24's analyzer sees it and defers it (hasC36Face whole-file
        // deferral); the wrapper is new. Pairing must cover both — the
        // JsonSchemaExporter mixed shape.
        var result = Run("""
            #pragma warning disable
            using System.Collections.Generic;
            public sealed class Elem {
                public int X;
            }
            public static class Test {
                public static List<Elem> M() => [new Elem { X = 1 }];
            }
            """);

        Assert.True(result.Changed);
        var stats = result.Statistics.Initializers;
        Assert.Equal(1, stats.DeferredResidualNodes);
        Assert.Equal(1, stats.EmittedByCollectionExprNodes);
        Assert.Equal(2, stats.PairedResidualNodes);
        Assert.Equal(2, CountOnDiskResidualNodes(result.OutputCode));
    }

    // ---- 6. Mixed statement: several emission shapes in one file ----

    [Fact]
    public void MixedFile_AllEmissionShapesPairAgainstTheOnDiskTruth()
    {
        var result = Run("""
            #pragma warning disable
            using System.Collections.Generic;
            public sealed class Elem {
                public int X;
            }
            public static class Test {
                public static List<int> A() => [1, 2];
                public static int[] B() => [3, 4];
                public static int[] C(List<int> src) => [.. src];
                public static List<Elem> D() => [new Elem { X = 1 }];
            }
            """);

        Assert.True(result.Changed);
        var stats = result.Statistics.Initializers;
        // A wrapper + C temp declaration + D wrapper = 3 emitted;
        // D's original element creation is deferred-paired separately.
        Assert.Equal(3, stats.EmittedByCollectionExprNodes);
        Assert.Equal(1, stats.DeferredResidualNodes);
        Assert.Equal(4, stats.PairedResidualNodes);
        Assert.Equal(4, CountOnDiskResidualNodes(result.OutputCode));
    }

    // ---- 7. Whole-tree gate reconciliation (the adapter's AfterFiles view) ----

    [Fact]
    public void TreeGate_ReconcilesOnDiskResidualsAgainstTheEmittedPairing()
    {
        var result = Run("""
            #pragma warning disable
            using System.Collections.Generic;
            public static class Test {
                public static List<int> M(List<int> src) => [.. src];
            }
            """);

        // Exactly what ExpressionDesugarPassAdapter.AfterFilesAsync does:
        // per-file on-disk count vs CountPairedNodes(fileStatistics).
        var onDisk = CountOnDiskResidualNodes(result.OutputCode);
        var paired = InitializerTreeGate.CountPairedNodes(result.Statistics.Initializers);
        var gate = InitializerTreeGate.Reconcile(
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Test.cs"] = onDisk,
            },
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Test.cs"] = paired,
            });

        Assert.True(gate.Balanced);
        Assert.Empty(gate.UnpairedFiles);
    }

    // ---- 8. Statistics composition + merge ----

    [Fact]
    public void PairedResidualNodes_ComposesAllThreeSources()
    {
        var stats = new InitializerStatistics
        {
            PreservedResidualNodes = 2,
            DeferredResidualNodes = 3,
            EmittedByCollectionExprNodes = 4,
        };
        Assert.Equal(9, InitializerTreeGate.CountPairedNodes(stats));
    }

    [Fact]
    public void MergeFrom_SumsEmittedByCollectionExprNodes()
    {
        var target = new InitializerStatistics
        {
            EmittedByCollectionExprNodes = 2,
        };
        var source = new InitializerStatistics
        {
            PreservedResidualNodes = 1,
            DeferredResidualNodes = 1,
            EmittedByCollectionExprNodes = 3,
        };

        target.MergeFrom(source);

        Assert.Equal(5, target.EmittedByCollectionExprNodes);
        Assert.Equal(1, target.PreservedResidualNodes);
        Assert.Equal(1, target.DeferredResidualNodes);
        Assert.Equal(7, target.PairedResidualNodes);
    }
}
