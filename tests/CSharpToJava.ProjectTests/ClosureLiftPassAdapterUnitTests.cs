using CSharpToJava.CLI;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.ClosureLift;
using Xunit;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// In-process coverage for the tree-wide tail of the closure-lift adapter
/// (design §7.3): <c>AfterFilesAsync</c> turns every residual anonymous method
/// in the intermediate tree into a CS2J4503 Error row, and the chain summary
/// keeps its C32 byte shape whenever no C45 work happened.
/// </summary>
public sealed class ClosureLiftPassAdapterUnitTests
{
    private static ClosureLiftPassAdapter CreateAdapter(bool strict = true)
        => new(new ClosureLiftOptions(),
            verbose: false,
            filterUnsupportedProjects: false,
            compatNamespacePrefixes: null,
            verifyBuild: false,
            verifyTests: false,
            strict: strict);

    private static PassTreeContext CreateTree(string intermediateRoot)
    {
        var sourceRoot = Path.Combine(intermediateRoot, "src");
        return new PassTreeContext
        {
            Layout = new ProjectPreprocessSourceLayout(sourceRoot, sourceRoot,
                Array.Empty<string>()),
            SourceRoot = sourceRoot,
            IntermediateRoot = intermediateRoot,
            DestinationRoot = Path.Combine(intermediateRoot, "dest"),
            ProjectGraph = null,
            Exclusion = new SupportedProjectFiltering.ExclusionInfo(),
            SourceFiles = Array.Empty<string>(),
            CsFiles = Array.Empty<string>(),
            InPlace = false,
            Snapshot = new InPlaceSourceSnapshot(enabled: false),
        };
    }

    private static string WriteIntermediateTree(params (string FileName, string Content)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(),
            "cs2j-m3-adapter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        foreach (var (fileName, content) in files)
            File.WriteAllText(Path.Combine(root, fileName), content);
        return root;
    }

    [Fact]
    public async Task AfterFiles_ResidualAnonymousMethod_EmitsCS2J4503Error()
    {
        var src = @"
class C {
    void M() {
        System.Comparison<int> c = delegate (int a, int b) { return a - b; };
    }
}";
        var root = WriteIntermediateTree(("C.cs", src));
        try
        {
            var adapter = CreateAdapter();

            await adapter.AfterFilesAsync(CreateTree(root));

            var row = Assert.Single(adapter.Diagnostics.Where(d => d.Code == "CS2J4503"));
            Assert.Equal(ClosureLiftSeverity.Error, row.Severity);
            Assert.Equal("C.cs", row.FilePath);
            Assert.Equal(4, row.Line); // the `delegate (int a, int b)` line
            Assert.True(adapter.HasErrors);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AfterFiles_CleanTree_NoRows()
    {
        var src = @"
class C {
    void M() {
        System.Console.WriteLine(1);
    }
}";
        var root = WriteIntermediateTree(("C.cs", src));
        try
        {
            var adapter = CreateAdapter();

            await adapter.AfterFilesAsync(CreateTree(root));

            Assert.Empty(adapter.Diagnostics);
            Assert.False(adapter.HasErrors);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Summary_NoC45Work_StaysC32ByteShape()
    {
        var adapter = CreateAdapter();

        // Red line (M3 ④): idle run — pure C32 files or no files at all — the
        // summary line must be byte-identical to the pre-C45 wording; the C45
        // segment simply never appears.
        Assert.Equal(
            "closure-lift: 0 transformed, 0 clean, 0 holder class(es), " +
            "0 capture(s) lifted, 0 blocked, 0 residual",
            adapter.BuildChainSummaryLine());
        Assert.DoesNotContain("display class", adapter.BuildChainSummaryLine());
        Assert.DoesNotContain("recursive", adapter.BuildChainSummaryLine());
        Assert.DoesNotContain("discard", adapter.BuildChainSummaryLine());
    }

    [Fact]
    public void Summary_C45Work_IncludesNestedRecursiveAndDiscard()
    {
        var adapter = CreateAdapter();
        var s = adapter.Statistics;
        s.DisplayClassesGenerated = 1;
        s.NestedDisplayClasses = 1;
        s.LambdaMethodsLifted = 2;
        s.StaticLambdaMethods = 1;
        s.ThisOnlyLambdas = 1;
        s.RecursiveLambdas = 1;
        s.DelegateSitesRewritten = 2;
        s.DiscardParametersNormalized = 3;

        var line = adapter.BuildChainSummaryLine();

        Assert.Contains("1 display class(es) (1 nested)", line);
        Assert.Contains(
            "2 lambda method(s) (1 static, 1 this-only, 1 recursive)", line);
        Assert.Contains("2 delegate site(s)", line);
        Assert.Contains("3 discard parameter(s) normalized", line);
        Assert.Contains("0 stmt lambda(s) residual (0 accounted)", line);
    }

    [Fact]
    public void Summary_DiscardOnlyWork_StillSurfacesC45Segment()
    {
        var adapter = CreateAdapter();
        adapter.Statistics.DiscardParametersNormalized = 1;

        var line = adapter.BuildChainSummaryLine();

        Assert.Contains("1 discard parameter(s) normalized", line);
        // The recursive/nested clauses stay conditional on non-zero counts.
        Assert.DoesNotContain("recursive", line);
        Assert.DoesNotContain("nested", line);
    }
}
