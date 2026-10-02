using System.Diagnostics;
using System.Xml.Linq;
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
/// Project-level gate for the numeric-boundary pass (C15, M2/M3):
/// checked sites are explicitized to Cs2jUnsigned.UnsignedMath helpers, the
/// Cs2jUnsignedMath support library is released and referenced, the rewritten
/// intermediate tree builds and preserves runtime overflow behavior,
/// project-wide CheckForOverflowUnderflow is handled the same way, and
/// unsupported sites fail the chain with the file rolled back.
/// </summary>
public class NumericBoundaryProjectTests
{
    private const string PassId = "numeric-boundary";
    private const string IntermediateName = ".cs2j-numeric-boundary-src";
    private const string SupportLibDirectoryName = "Cs2jUnsignedMath";

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
            StructValueSemanticsOptionsFactory = () =>
                new CSharpToJava.Core.StructValueSemantics.StructValueSemanticsOptions(),
            EnumDeclarationOptionsFactory = () =>
                new CSharpToJava.Core.EnumDeclaration.EnumDeclarationOptions(),
            EqualityConcatOptionsFactory = () =>
                new CSharpToJava.Core.EqualityConcat.EqualityConcatOptions(),
            UnsafeFfmOptionsFactory = () =>
                new CSharpToJava.Core.UnsafeFfm.UnsafeFfmOptions(),
            JavaKeywordOptionsFactory = () => new JavaKeywordOptions(),
            LegacyToggles = PassChainFactory.BuildAllDisabledToggles(registry),
            EnablePassIds = new[] { PassId },
            VerifyBuild = verify,
            VerifyTests = verify,
        });
    }

    [Fact]
    public async Task MixedFixture_Rewritten_SupportLibraryReleasedAndReferenced_IntermediateBuildsAndRuns()
    {
        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "SampleNumericBoundarySolution");
        // Keep the output root in TEMP: nesting under the test bin directory makes
        // the built app's exe path exceed MAX_PATH on Windows.
        var outputRoot = Path.Combine(Path.GetTempPath(), "cs2j-nb-m2-positive");
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

            // M2 releases the support library into the intermediate tree.
            var supportLibDir = Path.Combine(intermediate, SupportLibDirectoryName);
            Assert.True(Directory.Exists(supportLibDir),
                "numeric-boundary must release the Cs2jUnsignedMath support library");
            Assert.True(File.Exists(Path.Combine(supportLibDir, "UnsignedMath.cs")));
            Assert.True(File.Exists(Path.Combine(supportLibDir, "Cs2jUnsignedMath.csproj")));

            // The project compiling rewritten files gets the ProjectReference.
            var libProject = Path.Combine(
                intermediate, "SampleNumericBoundaryLib", "SampleNumericBoundaryLib.csproj");
            var libProjectText = await File.ReadAllTextAsync(libProject);
            Assert.Contains("Cs2jUnsignedMath.csproj", libProjectText);

            // NB-13 over the intermediate USER tree (the support library itself
            // legitimately contains checked): zero checked/unchecked keyword nodes.
            foreach (var file in Directory.EnumerateFiles(intermediate, "*.cs", SearchOption.AllDirectories))
            {
                if (file.StartsWith(supportLibDir, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var text = await File.ReadAllTextAsync(file);
                var root = CSharpSyntaxTree.ParseText(text).GetRoot();
                Assert.DoesNotContain(root.DescendantNodes(), n =>
                    n.IsKind(SyntaxKind.CheckedExpression)
                    || n.IsKind(SyntaxKind.CheckedStatement)
                    || n.IsKind(SyntaxKind.UncheckedExpression)
                    || n.IsKind(SyntaxKind.UncheckedStatement));
            }

            var uncheckedSamples = await File.ReadAllTextAsync(
                Path.Combine(intermediate, "SampleNumericBoundaryLib", "UncheckedSamples.cs"));
            Assert.Contains("(int)u", uncheckedSamples);
            Assert.Contains("(byte)v", uncheckedSamples);
            Assert.Contains("(a + b)", uncheckedSamples);
            Assert.Contains("-1", uncheckedSamples);
            var uncheckedRoot = CSharpSyntaxTree.ParseText(uncheckedSamples).GetRoot();
            Assert.DoesNotContain(uncheckedRoot.DescendantTokens(),
                t => t.IsKind(SyntaxKind.UncheckedKeyword));

            var checkedSamples = await File.ReadAllTextAsync(
                Path.Combine(intermediate, "SampleNumericBoundaryLib", "CheckedSamples.cs"));
            // M2/M3: every checked site is a fully-qualified helper call.
            Assert.Contains("Cs2jUnsigned.UnsignedMath.AddCheckedInt32", checkedSamples);
            Assert.Contains("Cs2jUnsigned.UnsignedMath.AddCheckedInt64", checkedSamples);
            Assert.Contains("Cs2jUnsigned.UnsignedMath.AddCheckedUInt32", checkedSamples);
            Assert.Contains("Cs2jUnsigned.UnsignedMath.AddCheckedUInt64", checkedSamples);
            Assert.Contains("Cs2jUnsigned.UnsignedMath.NegateCheckedInt32", checkedSamples);
            Assert.Contains("Cs2jUnsigned.UnsignedMath.ToByteCheckedInt32", checkedSamples);
            Assert.Contains("Cs2jUnsigned.UnsignedMath.ToInt32CheckedUInt64", checkedSamples);
            var checkedRoot = CSharpSyntaxTree.ParseText(checkedSamples).GetRoot();
            Assert.DoesNotContain(checkedRoot.DescendantTokens(),
                t => t.IsKind(SyntaxKind.CheckedKeyword));

            // Runtime evidence: the rewritten Exe fixture self-checks values and
            // overflow behavior against the helper-backed intermediate tree.
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
    public async Task CheckForOverflowUnderflowProject_RewrittenWithoutFlag_OverflowsAtRuntime()
    {
        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "SampleNumericBoundaryCheckedSolution");
        // TEMP root: a fixture-local output makes the built exe path exceed MAX_PATH.
        var outputRoot = Path.Combine(Path.GetTempPath(), "cs2j-nb-checked-overflow");
        try
        {
            var result = await RunPassAsync(
                Path.Combine(fixtureDir, "SampleNumericBoundaryCheckedSolution.sln"),
                outputRoot,
                verify: true);

            Assert.True(result.Success, "project-wide /checked must be rewritten, not rejected");

            var intermediate = Path.Combine(outputRoot, IntermediateName);
            var appProjectPath = Path.Combine(
                intermediate, "SampleNumericBoundaryCheckedApp", "SampleNumericBoundaryCheckedApp.csproj");
            var appProjectText = await File.ReadAllTextAsync(appProjectPath);
            // The flag ELEMENT is removed from the intermediate tree; helpers
            // carry the semantics (comments mentioning the name may remain).
            var appProjectXml = XDocument.Parse(appProjectText);
            Assert.DoesNotContain(appProjectXml.Descendants(),
                e => e.Name.LocalName == "CheckForOverflowUnderflow");
            Assert.Contains("Cs2jUnsignedMath.csproj", appProjectText);

            var programPath = Path.Combine(
                intermediate, "SampleNumericBoundaryCheckedApp", "Program.cs");
            var program = await File.ReadAllTextAsync(programPath);
            var programRoot = CSharpSyntaxTree.ParseText(program).GetRoot();
            Assert.DoesNotContain(programRoot.DescendantTokens(),
                t => t.IsKind(SyntaxKind.CheckedKeyword)
                    || t.IsKind(SyntaxKind.UncheckedKeyword));
            // foreach compound accumulation in the (former) /checked project.
            Assert.Contains("Cs2jUnsigned.UnsignedMath.AddCheckedInt32", program);

            Assert.True(Directory.Exists(Path.Combine(intermediate, SupportLibDirectoryName)),
                "the support library must be released for a /checked project");

            // Runtime: the non-overflowing sum prints first, then the overflow
            // path surfaces as an unhandled OverflowException.
            var (exitCode, output) = await RunDotNetAsync(
                "run", $"-c Release --project \"{appProjectPath}\" --no-launch-profile");
            Assert.NotEqual(0, exitCode);
            // Match the app's own output line (dotnet run interleaves MSBuild logs).
            var outputLines = output.Split('\n').Select(l => l.Trim('\r', ' ', '\t')).ToHashSet();
            Assert.Contains("6", outputLines);
            Assert.Contains("System.OverflowException", output);
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
    public async Task DynamicCheckedConversion_FailsChain_FileRolledBack()
    {
        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "SampleNumericBoundaryUnsupportedSolution");
        var outputRoot = Path.Combine(Path.GetTempPath(), "cs2j-nb-unsupported");
        try
        {
            var result = await RunPassAsync(
                Path.Combine(fixtureDir, "SampleNumericBoundaryUnsupportedSolution.sln"),
                outputRoot,
                verify: false);

            // CS2J5322: dynamic checked sites cannot be explicitized; NB-12 rolls
            // the whole file back and fails the chain (diagnostic text is covered
            // at adapter/unit level; CliChainResult contracts Success=false).
            Assert.False(result.Success, "dynamic checked conversion must fail numeric-boundary");

            var intermediate = Path.Combine(outputRoot, IntermediateName);
            var programPath = Path.Combine(
                intermediate, "SampleNumericBoundaryUnsupportedApp", "Program.cs");
            var program = await File.ReadAllTextAsync(programPath);
            // Rolled back byte-identical: the unsupported site stays untouched.
            Assert.Contains("checked((int)d)", program);
            Assert.DoesNotContain("Cs2jUnsigned", program);
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
