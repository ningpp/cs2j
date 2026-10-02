using System.Diagnostics;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// Project-level gate for C17 M1 (increment-targets read-modify-write
/// normalization), co-located with the C15 numeric-boundary pass and enabled
/// by default. The IncrementTargets fixture (instrumented property/indexer
/// probe, enum arrays, simple enum/unsigned residuals) is rewritten to a
/// single-evaluation shape, the uint/ulong helper sites still pull in the
/// Cs2jUnsignedMath support library, and the transformed solution builds and
/// runs. With the family kill-switch flipped off, the legacy C15 behavior
/// returns: a complex postfix inside a checked block is CS2J5324 and the
/// offending file is rolled back byte-identical.
/// </summary>
public class NumericBoundaryIncrementTargetProjectTests
{
    private const string PassId = "numeric-boundary";
    private const string IntermediateName = ".cs2j-numeric-boundary-src";

    private static async Task<CliChainResult> RunPassAsync(
        string sourcePath, string outputRoot, bool verify, bool enableIncrementTargets)
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
            ExpressionDesugarOptionsFactory = () =>
                new CSharpToJava.Core.ExpressionDesugar.ExpressionDesugarOptions(),
            JavaKeywordOptionsFactory = () => new JavaKeywordOptions(),
            LegacyToggles = PassChainFactory.BuildAllDisabledToggles(registry),
            EnablePassIds = new[] { PassId },
            EnableIncrementTargets = enableIncrementTargets,
            VerifyBuild = verify,
            VerifyTests = verify,
        });
    }

    [Fact]
    public async Task Enabled_IncrementFixture_Rewritten_BuildsAndRuns()
    {
        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "SampleNumericBoundarySolution");
        var outputRoot = Path.Combine(Path.GetTempPath(), "cs2j-c17-positive");
        try
        {
            var result = await RunPassAsync(
                Path.Combine(fixtureDir, "SampleNumericBoundarySolution.sln"),
                outputRoot,
                verify: true,
                enableIncrementTargets: true);

            Assert.True(result.Success, "C17-enabled numeric-boundary pass failed (see chain diagnostics)");
            Assert.Contains(IntermediateName, result.ExecutedIntermediateDirectoryNames);

            var intermediate = Path.Combine(outputRoot, IntermediateName);
            var incrementDir = Path.Combine(
                intermediate, "SampleNumericBoundaryLib", "IncrementTargets");

            var probe = await File.ReadAllTextAsync(Path.Combine(incrementDir, "SideEffectProbe.cs"));
            // Receivers/index args/old values are hoisted exactly once.
            Assert.Contains("__cs2j_inc_", probe);
            // uint property residual routes through the support library, which
            // forces Cs2jUnsignedMath injection for the family.
            Assert.Contains("Cs2jUnsigned.UnsignedMath.AddUInt32", probe);
            // byte property keeps a narrow write-back cast.
            Assert.Contains("(byte)(", probe);

            var samples = await File.ReadAllTextAsync(Path.Combine(incrementDir, "EnumIncrementSamples.cs"));
            Assert.Contains("__cs2j_inc_", samples);
            Assert.Contains("Cs2jUnsigned.UnsignedMath.AddUInt32", samples);
            Assert.Contains("Cs2jUnsigned.UnsignedMath.AddUInt64", samples);
            Assert.Contains("(byte)(", samples);
            // No owned ++/-- survives in the rewritten fixture.
            Assert.DoesNotContain("colors[index]++", samples);
            Assert.DoesNotContain("u++;", samples.Replace(" ", ""));

            // M2 IT-7: decimal sites expand to 'old ± 1M' (literal normalizes
            // to uppercase M); EnableDecimalOps stays off in the default chain,
            // so no decimal.Add reduction appears here.
            var decimalSamples = await File.ReadAllTextAsync(
                Path.Combine(incrementDir, "DecimalIncrementSamples.cs"));
            Assert.Contains("+ 1M", decimalSamples);
            Assert.Contains("- 1M", decimalSamples);
            Assert.DoesNotContain("decimal.Add", decimalSamples);
            Assert.DoesNotContain("DecimalProp++", decimalSamples);
            Assert.DoesNotContain("DecimalProp--", decimalSamples);
            Assert.DoesNotContain("]++;", decimalSamples.Replace(" ", ""));
            // The 1e-28m precision probe literal is copied through untouched.
            Assert.Contains("0.0000000000000000000000000001m", decimalSamples);

            // Runtime evidence: single evaluation, enum/unsigned semantics.
            var appProject = Path.Combine(
                intermediate, "SampleNumericBoundaryApp", "SampleNumericBoundaryApp.csproj");
            var (exitCode, output) = await RunDotNetAsync(
                "run", $"-c Release --project \"{appProject}\" --no-launch-profile");
            Assert.True(exitCode == 0,
                $"C17 sample app exited with {exitCode}:\n{output}");
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
    public async Task Disabled_LegacyC15Behavior_FailsChain_AndRollsFileBack()
    {
        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "SampleNumericBoundarySolution");
        var outputRoot = Path.Combine(Path.GetTempPath(), "cs2j-c17-dormant");
        try
        {
            var result = await RunPassAsync(
                Path.Combine(fixtureDir, "SampleNumericBoundarySolution.sln"),
                outputRoot,
                verify: false,
                enableIncrementTargets: false);

            Assert.False(result.Success,
                "with C17 disabled the checked property postfix must stay a legacy CS2J5324 error");

            var intermediate = Path.Combine(outputRoot, IntermediateName);
            var probePath = Path.Combine(
                intermediate, "SampleNumericBoundaryLib", "IncrementTargets", "SideEffectProbe.cs");
            var probe = await File.ReadAllTextAsync(probePath);
            // NB-12: error rolls the whole file back to the original text.
            Assert.Contains("ColorProp++;", probe);
            Assert.DoesNotContain("__cs2j_inc_", probe);
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
