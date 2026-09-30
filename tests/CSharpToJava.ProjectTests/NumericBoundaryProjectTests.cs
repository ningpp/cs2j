using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// Project-level gate for the numeric-boundary pass (C15, M1):
/// unchecked-only fixtures are stripped with no support library, retained
/// checked nodes still produce a tree that builds and keeps runtime overflow
/// behavior, and project-wide CheckForOverflowUnderflow is rejected with
/// CS2J5326.
/// </summary>
public class NumericBoundaryProjectTests
{
    private const string PassId = "numeric-boundary";
    private const string IntermediateName = ".cs2j-numeric-boundary-src";

    private static async Task<CliChainResult> RunPassAsync(
        string sourcePath, string outputRoot, bool verify)
    {
        var registry = PassCatalog.BuildRegistry();
        return await CliPreprocessChain.RunAsync(new CliChainRequest
        {
            SourcePath = sourcePath,
            DestinationRoot = outputRoot,
            MakerOptionsFactory = () => new ReadOnlyStructMakerOptions(),
            RecordReadOnlyOptionsFactory = () => new RecordReadOnlyOptions(),
            MemberDesugarOptionsFactory = () => new MemberDesugarOptions(),
            JavaKeywordOptionsFactory = () => new JavaKeywordOptions(),
            LegacyToggles = PassChainFactory.BuildAllDisabledToggles(registry),
            EnablePassIds = new[] { PassId },
            VerifyBuild = verify,
            VerifyTests = verify,
        });
    }

    [Fact]
    public async Task UncheckedFixture_Stripped_NoSupportLibrary_IntermediateBuildsAndRuntimeChecksPass()
    {
        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "SampleNumericBoundarySolution");
        // Keep the output root in TEMP: nesting under the test bin directory makes
        // the built app's exe path exceed MAX_PATH on Windows.
        var outputRoot = Path.Combine(Path.GetTempPath(), "cs2j-nb-m1-positive");
        try
        {
            var result = await RunPassAsync(
                Path.Combine(fixtureDir, "SampleNumericBoundarySolution.sln"),
                outputRoot,
                verify: true);

            Assert.True(result.Success, "numeric-boundary pass failed (see chain diagnostics)");
            Assert.Contains(IntermediateName, result.ExecutedIntermediateDirectoryNames);

            var intermediate = Path.Combine(outputRoot, IntermediateName);
            Assert.True(Directory.Exists(intermediate));

            // M1 emits no checked helpers, so no Cs2jUnsignedMath project may be
            // released into the intermediate tree.
            Assert.False(
                Directory.Exists(Path.Combine(intermediate, "Cs2jUnsignedMath")),
                "M1 must not release the Cs2jUnsignedMath support library");

            // NB-13 over the intermediate user tree: zero unchecked keyword nodes;
            // checked nodes are allowed to remain in M1.
            foreach (var file in Directory.EnumerateFiles(intermediate, "*.cs", SearchOption.AllDirectories))
            {
                var text = await File.ReadAllTextAsync(file);
                var root = CSharpSyntaxTree.ParseText(text).GetRoot();
                Assert.Empty(root.DescendantNodes()
                    .Where(n => n.IsKind(SyntaxKind.UncheckedExpression)
                        || n.IsKind(SyntaxKind.UncheckedStatement)));
            }

            var uncheckedSamples = await File.ReadAllTextAsync(
                Path.Combine(intermediate, "SampleNumericBoundaryLib", "UncheckedSamples.cs"));
            Assert.Contains("(int)u", uncheckedSamples);
            Assert.Contains("(byte)v", uncheckedSamples);
            Assert.Contains("(a + b)", uncheckedSamples);
            Assert.Contains("-1", uncheckedSamples);
            // NB-2: the unwrapped block leaves no nested bare block for the splice site.
            var uncheckedRoot = CSharpSyntaxTree.ParseText(uncheckedSamples).GetRoot();
            Assert.Empty(uncheckedRoot.DescendantTokens()
                .Where(t => t.IsKind(SyntaxKind.UncheckedKeyword)));

            var checkedSamples = await File.ReadAllTextAsync(
                Path.Combine(intermediate, "SampleNumericBoundaryLib", "CheckedSamples.cs"));
            // M1 retention: built-in integer checked sites stay untouched.
            Assert.Contains("checked(a + b)", checkedSamples);
            Assert.Contains("checked((byte)v)", checkedSamples);
            Assert.Contains("checked", checkedSamples);

            // Runtime evidence (the .NET half of equivalence): run the Exe fixture
            // against the rewritten intermediate tree.
            var appProject = Path.Combine(
                intermediate, "SampleNumericBoundaryApp", "SampleNumericBoundaryApp.csproj");
            var (exitCode, output) = await RunDotNetAsync("run", $"-c Release --project \"{appProject}\" --no-launch-profile");
            Assert.True(exitCode == 0,
                $"numeric-boundary sample app exited with {exitCode}:\n{output}");
            Assert.Contains("numeric-boundary sample OK", output);
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CheckForOverflowUnderflowProject_IsRejected_CS2J5326()
    {
        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "SampleNumericBoundaryCheckedSolution");
        var outputRoot = Path.Combine(fixtureDir, ".test-output-checked-overflow");
        try
        {
            var result = await RunPassAsync(
                Path.Combine(fixtureDir, "SampleNumericBoundaryCheckedSolution.sln"),
                outputRoot,
                verify: false);

            Assert.False(result.Success, "project-wide /checked must fail numeric-boundary M1");

            // M1 placeholder semantics (design decision 15): the pass reports the
            // per-project CS2J5326 error and the chain fails. CliChainResult does
            // not surface diagnostic text, so Success=false is the contracted signal;
            // the adapter-side aggregation is unit-covered through BuildChainDiagnosticLines.
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }
        }
    }

    private static async Task<(int ExitCode, string Output)> RunDotNetAsync(string verb, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"{verb} {arguments}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, stdout + stderr);
    }
}
