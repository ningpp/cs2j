using CSharpToJava.CLI;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.EnumDeclaration;
using CSharpToJava.Core.EqualityConcat;
using CSharpToJava.Core.ExpressionDesugar;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.LexicalNormalize;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;
using CSharpToJava.Core.StructValueSemantics;
using CSharpToJava.Core.UnsafeFfm;
using CSharpToJava.Core.Workspace;
using Xunit;

namespace CSharpToJava.ProjectTests;

public class RefOutInProjectTests
{
    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleRefOutInSolution");

    private static ConversionOptions Options => new()
    {
        TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
    };

    /// <summary>
    /// §7.4 migration: the direct-pipeline tests in this class are the
    /// LegacyHolderPath safety net (external/locked residue + direct conversion
    /// entry, CD6) — their IntHolder/ObjectHolder assertions are kept
    /// deliberately until the legacy path is retired. ByRefPath behavior after
    /// preprocessing is covered by the preprocessed case below and by
    /// RefOutInByRefPathTests.
    /// </summary>
    [Fact]
    public async Task LegacyHolderPath_DirectPipeline_SafetyNetIntentionallyRetained()
    {
        var libDir = Path.Combine(FixtureDir, "SampleRefOutInLib");
        Assert.True(Directory.Exists(libDir), $"Fixture not found: {libDir}");

        var pipeline = new ConversionPipeline();
        var results = await pipeline.ConvertProjectWithPartialMergeAsync(libDir, Options);
        Assert.NotEmpty(results);
        var calculator = Assert.Single(results, r =>
            string.Equals(r.FileName, "Calculator.java", StringComparison.OrdinalIgnoreCase));
        Assert.True(calculator.Success, string.Join("\n", calculator.Diagnostics));
        Assert.Contains("IntHolder", calculator.GeneratedCode);
        Assert.Contains("ObjectHolder", calculator.GeneratedCode);
    }

    /// <summary>
    /// §7.4 migration ①: preprocessed-then-converted output asserts the new
    /// ByRef hierarchy (J1) instead of the legacy holders. The production
    /// convert-project chain runs closure-lift (default-on, anchored after
    /// ref-out-in): ref-out-in emits ByRef accessor proxies
    /// (<c>() =&gt; x, v =&gt; x = v</c>) whose setter lambdas mutably capture the
    /// local, and closure-lift then lifts those captures into display classes.
    /// The direct Java converter no longer carries the legacy array-holder arm,
    /// so both passes must run before direct conversion (C45 M5 retirement).
    /// </summary>
    [Fact]
    public async Task PreprocessedSolution_FullConversion_ProducesByRefHierarchy()
    {
        var slnPath = Path.Combine(FixtureDir, "SampleRefOutIn.sln");
        Assert.True(File.Exists(slnPath), $"Solution not found: {slnPath}");
        SolutionLoader.EnsureMSBuildRegistered();

        var outputDir = Path.Combine(Path.GetTempPath(), "cs2j-refoutin-migr-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var registry = PassCatalog.BuildRegistry();
            var chain = await CliPreprocessChain.RunAsync(new CliChainRequest
            {
                SourcePath = slnPath,
                DestinationRoot = outputDir,
                MakerOptionsFactory = () => new ReadOnlyStructMakerOptions(),
                RecordReadOnlyOptionsFactory = () => new RecordReadOnlyOptions(),
                MemberDesugarOptionsFactory = () => new MemberDesugarOptions(),
                EnumDeclarationOptionsFactory = () => new EnumDeclarationOptions(),
                EqualityConcatOptionsFactory = () => new EqualityConcatOptions(),
                UnsafeFfmOptionsFactory = () => new UnsafeFfmOptions(),
                ExpressionDesugarOptionsFactory = () => new ExpressionDesugarOptions(),
                StructValueSemanticsOptionsFactory = () => new StructValueSemanticsOptions(),
                JavaKeywordOptionsFactory = () => new JavaKeywordOptions(),
                LexicalNormalizeOptionsFactory = () => new LexicalNormalizeOptions(),
                LegacyToggles = PassChainFactory.BuildAllDisabledToggles(registry),
                EnablePassIds = new[] { "ref-out-in", "closure-lift" },
                VerifyBuild = true,
                VerifyTests = true,
            });
            Assert.True(chain.Success, "ref-out-in + closure-lift chain failed (see chain diagnostics)");

            var liftedSln = Path.Combine(outputDir, ".cs2j-closure-lift-src", "SampleRefOutIn.sln");
            Assert.True(File.Exists(liftedSln), $"closure-lift sln missing: {liftedSln}");

            using var loader = new SolutionLoader();
            var projects = await loader.OpenSolutionAsync(liftedSln);
            Assert.NotEmpty(projects);

            var pipeline = new ConversionPipeline();
            var sawByRef = false;
            // CD9: the Cs2jRefOutIn support project is symbol source only and is
            // filtered out of Java conversion exactly like Program.cs does.
            foreach (var wp in projects.Where(p =>
                !p.Name.Contains("Cs2jRefOutIn", StringComparison.OrdinalIgnoreCase)))
            {
                var extraPaths = wp.ProjectReferences
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(Path.GetDirectoryName)
                    .Where(d => !string.IsNullOrWhiteSpace(d))
                    .ToList()!;
                var results = await pipeline.ConvertProjectWithPartialMergeAsync(
                    wp.Directory, Options, additionalSemanticProjectPaths: extraPaths,
                    projectName: wp.Name, projectFilePath: wp.FilePath,
                    projectReferences: wp.ProjectReferences, isTestProject: wp.IsTestProject);
                Assert.NotEmpty(results);
                foreach (var r in results)
                {
                    Assert.True(r.Success, $"{wp.Name}/{r.FileName}: {string.Join("\n", r.Diagnostics)}");
                    if (r.GeneratedCode.Contains("ByRef<Integer>") || r.GeneratedCode.Contains("io.github.ningpp.compat.ByRef"))
                        sawByRef = true;
                }
            }
            Assert.True(sawByRef, "preprocessed conversion should land on the ByRef hierarchy");
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task LibProject_RefOutInMethods_ConvertCorrectly()
    {
        var libDir = Path.Combine(FixtureDir, "SampleRefOutInLib");
        Assert.True(Directory.Exists(libDir), $"Fixture not found: {libDir}");

        var pipeline = new ConversionPipeline();
        var results = await pipeline.ConvertProjectWithPartialMergeAsync(libDir, Options);

        Assert.NotEmpty(results);

        // Calculator.java — ref int params become IntHolder, out Point becomes ObjectHolder<Point>
        var calculator = Assert.Single(results, r =>
            string.Equals(r.FileName, "Calculator.java", StringComparison.OrdinalIgnoreCase));
        Assert.True(calculator.Success, string.Join("\n", calculator.Diagnostics));
        Assert.Contains("IntHolder", calculator.GeneratedCode);
        Assert.Contains("ObjectHolder", calculator.GeneratedCode);

        // GenericHolder.java — ref T becomes ObjectHolder<T>
        var genericHolder = Assert.Single(results, r =>
            string.Equals(r.FileName, "GenericHolder.java", StringComparison.OrdinalIgnoreCase));
        Assert.True(genericHolder.Success, string.Join("\n", genericHolder.Diagnostics));
        Assert.Contains("ObjectHolder", genericHolder.GeneratedCode);
    }

    [Fact]
    public async Task SlnProject_FullConversion_AllFilesSucceed()
    {
        var slnPath = Path.Combine(FixtureDir, "SampleRefOutIn.sln");
        Assert.True(File.Exists(slnPath), $"Solution not found: {slnPath}");
        SolutionLoader.EnsureMSBuildRegistered();

        using var loader = new SolutionLoader();
        var projects = await loader.OpenSolutionAsync(slnPath);
        Assert.Equal(2, projects.Count);

        var pipeline = new ConversionPipeline();

        foreach (var wp in projects)
        {
            var extraPaths = wp.ProjectReferences
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(Path.GetDirectoryName)
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .ToList()!;

            var results = await pipeline.ConvertProjectWithPartialMergeAsync(
                wp.Directory,
                Options,
                additionalSemanticProjectPaths: extraPaths,
                projectName: wp.Name,
                projectFilePath: wp.FilePath,
                projectReferences: wp.ProjectReferences,
                isTestProject: wp.IsTestProject);

            Assert.NotEmpty(results);
            foreach (var r in results)
            {
                Assert.True(r.Success, $"{wp.Name}/{r.FileName}: {string.Join("\n", r.Diagnostics)}");
            }
        }
    }

    [Fact]
    public async Task SlnProject_RefOutStruct_GeneratesObjectHolder()
    {
        var libDir = Path.Combine(FixtureDir, "SampleRefOutInLib");
        Assert.True(Directory.Exists(libDir), $"Fixture not found: {libDir}");

        var pipeline = new ConversionPipeline();
        var results = await pipeline.ConvertProjectWithPartialMergeAsync(libDir, Options);

        Assert.NotEmpty(results);
        var calculator = Assert.Single(results, r =>
            string.Equals(r.FileName, "Calculator.java", StringComparison.OrdinalIgnoreCase));
        Assert.True(calculator.Success, string.Join("\n", calculator.Diagnostics));

        // out Point (struct) should generate ObjectHolder<Point>
        Assert.Contains("ObjectHolder", calculator.GeneratedCode);
    }
}
