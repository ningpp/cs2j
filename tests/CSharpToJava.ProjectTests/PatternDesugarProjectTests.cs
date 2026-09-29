using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CSharpToJava.CLI;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// Project-level gate for the pattern-desugar pass (C03). The SamplePatternSolution
/// fixture exercises pattern switches, non-integer governing switches, catch
/// filters and type-parameter catches; running the pass over it must produce a
/// tree that builds, whose runtime tests still pass, and which contains no target
/// syntax (asserted per file here).
/// </summary>
public class PatternDesugarProjectTests
{
    private const string PassId = "pattern-desugar";

    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "SamplePatternSolution");

    private static async Task<CliChainResult> RunPassAsync(
        string sourcePath, string outputRoot, bool verify = true)
    {
        // Only the pattern-desugar pass runs: the default chain would also
        // transform the fixture, and the point here is this pass in isolation.
        var registry = PassCatalog.BuildRegistry();
        return await CliPreprocessChain.RunAsync(new CliChainRequest
        {
            SourcePath = sourcePath,
            DestinationRoot = outputRoot,
            MakerOptionsFactory = () => new ReadOnlyStructMakerOptions(),
            RecordReadOnlyOptionsFactory = () => new RecordReadOnlyOptions(),
            JavaKeywordOptionsFactory = () => new JavaKeywordOptions(),
            LegacyToggles = PassChainFactory.BuildAllDisabledToggles(registry),
            EnablePassIds = new[] { PassId },
            VerifyBuild = verify,
            VerifyTests = verify,
        });
    }

    [Fact]
    public async Task Fixture_LowersAllTargets_AndIntermediateTreeVerifies()
    {
        var outputRoot = Path.Combine(FixtureDir, ".test-output-pattern-desugar");
        try
        {
            var result = await RunPassAsync(
                Path.Combine(FixtureDir, "SamplePatternSolution.sln"), outputRoot);

            // VerifyBuild/VerifyTests default to true, so a green result already
            // proves the intermediate tree compiles and its runtime tests pass —
            // that is the .NET-half evidence for semantic equivalence.
            Assert.True(result.Success, "pattern-desugar pass failed (see chain diagnostics above)");
            Assert.Contains(".cs2j-pattern-desugar-src", result.ExecutedIntermediateDirectoryNames);

            var intermediate = Path.Combine(outputRoot, ".cs2j-pattern-desugar-src");
            Assert.True(Directory.Exists(intermediate), $"missing intermediate tree: {intermediate}");

            // Residual gate: no target syntax survives in any output file, and the
            // fail-fast constructor forms the converter rejects are gone.
            foreach (var file in Directory.EnumerateFiles(intermediate, "*.cs", SearchOption.AllDirectories))
            {
                var text = await File.ReadAllTextAsync(file);
                var root = CSharpSyntaxTree.ParseText(text).GetRoot();
                Assert.Empty(root.DescendantNodes().OfType<CasePatternSwitchLabelSyntax>());
                Assert.Empty(root.DescendantNodes()
                    .OfType<CatchClauseSyntax>()
                    .Where(c => c.Filter != null));
            }

            var switches = await File.ReadAllTextAsync(
                Path.Combine(intermediate, "SamplePatternLib", "PatternSwitchSamples.cs"));
            Assert.Contains("is string &&", switches);
            Assert.Contains("is int &&", switches);
            Assert.DoesNotContain("switch (o)", switches);

            var catches = await File.ReadAllTextAsync(
                Path.Combine(intermediate, "SamplePatternLib", "CatchSamples.cs"));
            Assert.Contains("catch (System.Exception __cs2jEx_", catches);
            Assert.Contains("is T &&", catches);
        }
        finally
        {
            if (Directory.Exists(outputRoot))
                Directory.Delete(outputRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CleanProjectWithoutTargets_IsByteLevelPreserved()
    {
        var tempSource = Path.Combine(Path.GetTempPath(), "cs2j-c03-clean-" + Guid.NewGuid().ToString("N"));
        var outputRoot = tempSource + "-out";
        Directory.CreateDirectory(tempSource);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempSource, "Clean.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>disable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            // An int-governed switch with plain labels is out of C03's scope (D15).
            const string source = "public class C { public int M(int x) { switch (x) { case 1: return 1; default: return 0; } } }\n";
            await File.WriteAllTextAsync(Path.Combine(tempSource, "Class1.cs"), source);

            var result = await RunPassAsync(tempSource, outputRoot, verify: false);

            Assert.True(result.Success, "clean project must pass without rewriting");
            var copied = await File.ReadAllTextAsync(
                Path.Combine(outputRoot, ".cs2j-pattern-desugar-src", "Class1.cs"));
            Assert.Equal(source, copied);
        }
        finally
        {
            if (Directory.Exists(tempSource)) Directory.Delete(tempSource, recursive: true);
            if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, recursive: true);
        }
    }
}
