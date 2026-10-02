using System.Diagnostics;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// Project-level gate for C34 (decimal-ops normalization), co-located with
/// the C15 numeric-boundary pass and enabled by default in the product chain
/// (<see cref="CliChainRequest.EnableDecimalOps"/> kill-switch). The decimal
/// fixture is reduced to BCL static calls, downgrades const-decimal sites,
/// keeps single-evaluation semantics and still builds and runs; with the
/// family switch pinned off the same fixture passes through byte-identical;
/// a 536x unsupported site fails the chain with the file rolled back.
/// </summary>
public class NumericBoundaryDecimalOpsProjectTests
{
    private const string PassId = "numeric-boundary";
    private const string IntermediateName = ".cs2j-numeric-boundary-src";

    private static async Task<CliChainResult> RunPassAsync(
        string sourcePath, string outputRoot, bool verify, bool enableDecimalOps)
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
            LexicalNormalizeOptionsFactory = () =>
                new CSharpToJava.Core.LexicalNormalize.LexicalNormalizeOptions(),
            EqualityConcatOptionsFactory = () =>
                new CSharpToJava.Core.EqualityConcat.EqualityConcatOptions(),
            UnsafeFfmOptionsFactory = () =>
                new CSharpToJava.Core.UnsafeFfm.UnsafeFfmOptions(),
            ExpressionDesugarOptionsFactory = () =>
                new CSharpToJava.Core.ExpressionDesugar.ExpressionDesugarOptions(),
            JavaKeywordOptionsFactory = () => new JavaKeywordOptions(),
            LegacyToggles = PassChainFactory.BuildAllDisabledToggles(registry),
            EnablePassIds = new[] { PassId },
            EnableDecimalOps = enableDecimalOps,
            VerifyBuild = verify,
            VerifyTests = verify,
        });
    }

    [Fact]
    public async Task Enabled_DecimalFixture_Rewritten_BuildsAndRuns()
    {
        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "SampleNumericBoundarySolution");
        var outputRoot = Path.Combine(Path.GetTempPath(), "cs2j-c34-positive");
        try
        {
            var result = await RunPassAsync(
                Path.Combine(fixtureDir, "SampleNumericBoundarySolution.sln"),
                outputRoot,
                verify: true,
                enableDecimalOps: true);

            Assert.True(result.Success, "C34-enabled numeric-boundary pass failed (see chain diagnostics)");
            Assert.Contains(IntermediateName, result.ExecutedIntermediateDirectoryNames);

            var intermediate = Path.Combine(outputRoot, IntermediateName);
            var decimalDir = Path.Combine(intermediate, "SampleNumericBoundaryLib", "DecimalOps");

            var arithmetic = await File.ReadAllTextAsync(Path.Combine(decimalDir, "DecimalArithmetic.cs"));
            Assert.Contains("(decimal.Add(", arithmetic);
            Assert.Contains("(decimal.Subtract(", arithmetic);
            Assert.Contains("(decimal.Multiply(", arithmetic);
            Assert.Contains("(decimal.Divide(", arithmetic);
            Assert.Contains("(decimal.Remainder(", arithmetic);
            Assert.Contains("(decimal.Negate(", arithmetic);
            Assert.Contains("(decimal.Compare(", arithmetic);
            // DC-7 hard invariant.
            Assert.DoesNotContain(".Equals", arithmetic);
            Assert.DoesNotContain("CompareTo", arithmetic);

            var compounds = await File.ReadAllTextAsync(Path.Combine(decimalDir, "DecimalCompounds.cs"));
            Assert.Contains("(decimal.Add(", compounds);
            Assert.Contains("(decimal.Subtract(", compounds);
            Assert.Contains("(decimal.Multiply(", compounds);
            Assert.Contains("__cs2j_dec_r", compounds);
            Assert.Contains("__cs2j_dec_a0", compounds);

            var mixed = await File.ReadAllTextAsync(Path.Combine(decimalDir, "DecimalMixedOperands.cs"));
            Assert.Contains("Cs2jUnsigned.UnsignedMath.ToDecimalUInt32", mixed);
            Assert.Contains("Cs2jUnsigned.UnsignedMath.ToDecimalUInt64", mixed);
            Assert.Contains("(decimal)", mixed);

            var constants = await File.ReadAllTextAsync(Path.Combine(decimalDir, "DecimalConstants.cs"));
            Assert.Contains("static readonly decimal Seeded", constants);
            Assert.Contains("static readonly decimal CascadedFromSeeded", constants);
            Assert.Contains("public const decimal Pure", constants);

            // Runtime evidence: arithmetic results, scale equality, overflow
            // and division-by-zero exceptions and single-evaluation counters.
            var appProject = Path.Combine(
                intermediate, "SampleNumericBoundaryApp", "SampleNumericBoundaryApp.csproj");
            var (exitCode, output) = await RunDotNetAsync("run", $"-c Release --project \"{appProject}\" --no-launch-profile");
            Assert.True(exitCode == 0,
                $"C34 sample app exited with {exitCode}:\n{output}");
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
    public async Task Disabled_DecimalFixture_PassesThroughByteIdentical()
    {
        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "SampleNumericBoundarySolution");
        var outputRoot = Path.Combine(Path.GetTempPath(), "cs2j-c34-dormant");
        var sourceDecimalDir = Path.Combine(fixtureDir, "SampleNumericBoundaryLib", "DecimalOps");
        try
        {
            var result = await RunPassAsync(
                Path.Combine(fixtureDir, "SampleNumericBoundarySolution.sln"),
                outputRoot,
                verify: true,
                enableDecimalOps: false);

            Assert.True(result.Success, "dormant C34 must not affect the C15 chain");

            var intermediate = Path.Combine(outputRoot, IntermediateName);
            foreach (var sourceFile in Directory.EnumerateFiles(sourceDecimalDir, "*.cs"))
            {
                var intermediateFile = Path.Combine(
                    intermediate, "SampleNumericBoundaryLib", "DecimalOps", Path.GetFileName(sourceFile));
                var sourceText = await File.ReadAllTextAsync(sourceFile);
                var intermediateText = await File.ReadAllTextAsync(intermediateFile);
                Assert.Equal(sourceText, intermediateText);
                // No rewritten call sites (the doc comment mentions the names).
                Assert.DoesNotContain("(decimal.Add(", intermediateText);
            }
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
    public async Task Enabled_UnsupportedSite_FailsChain_FileRolledBack()
    {
        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "SampleNumericBoundaryUnsupportedSolution");
        var outputRoot = Path.Combine(Path.GetTempPath(), "cs2j-c34-unsupported");
        try
        {
            var result = await RunPassAsync(
                Path.Combine(fixtureDir, "SampleNumericBoundaryUnsupportedSolution.sln"),
                outputRoot,
                verify: false,
                enableDecimalOps: true);

            Assert.False(result.Success, "CS2J5361 must fail the C34-enabled chain");

            var intermediate = Path.Combine(outputRoot, IntermediateName);
            var programPath = Path.Combine(
                intermediate, "SampleNumericBoundaryUnsupportedApp", "Program.cs");
            var program = await File.ReadAllTextAsync(programPath);
            // Rolled back byte-identical: the unsupported decimal site is untouched.
            Assert.Contains("a + m", program);
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
