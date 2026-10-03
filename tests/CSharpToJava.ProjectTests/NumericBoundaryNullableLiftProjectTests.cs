using System.Diagnostics;
using System.Text.RegularExpressions;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// Project-level gate for C22 M2 (lifted <c>Nullable&lt;T&gt;</c>
/// explicitization), co-located with the numeric-boundary pass. The paired
/// Native*/Guarded* fixture under SampleNumericBoundaryLib/Nullable runs the
/// full product chain (C17/C22/C15/C34 all enabled): the intermediate tree
/// carries HasValue/.Value guards with zero residual lifted shapes, builds
/// Release, passes its test run, executes the null/value grids via the sample
/// app, needs no extra support-library injection from the nullable files, and
/// is byte-stable across a second chain run.
/// </summary>
public class NumericBoundaryNullableLiftProjectTests
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
            EnableDecimalOps = true,
            EnableIncrementTargets = true,
            VerifyBuild = verify,
            VerifyTests = verify,
        });
    }

    [Fact]
    public async Task Enabled_NullableFixture_ExplicitGuards_BuildsRunsAndIdempotent()
    {
        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "SampleNumericBoundarySolution");
        var outputRoot = Path.Combine(Path.GetTempPath(), "cs2j-c22-positive");
        var idempotentRoot = Path.Combine(Path.GetTempPath(), "cs2j-c22-idempotent");
        try
        {
            var result = await RunPassAsync(
                Path.Combine(fixtureDir, "SampleNumericBoundarySolution.sln"),
                outputRoot,
                verify: true);

            Assert.True(result.Success,
                "C22-enabled numeric-boundary chain must succeed (verify build + tests)");
            Assert.Contains(IntermediateName, result.ExecutedIntermediateDirectoryNames);

            var intermediate = Path.Combine(outputRoot, IntermediateName);
            var nullableDir = Path.Combine(intermediate, "SampleNumericBoundaryLib", "Nullable");

            var byFile = Directory.EnumerateFiles(nullableDir, "*.cs")
                .ToDictionary(
                    path => Path.GetFileName(path)!,
                    path => File.ReadAllText(path));
            Assert.Equal(8, byFile.Count);

            // Global invariants for every rewritten nullable file: guards are
            // explicit, GOV identifier gone, no checked helper injected (the
            // fixture has no checked composition sites).
            foreach (var (fileName, text) in byFile)
            {
                Assert.Contains(".HasValue", text);
                Assert.Contains(".Value", text);
                // The GOV *call* must be gone; the sample class name itself still
                // contains the identifier, so pin the call shape explicitly.
                Assert.DoesNotMatch(@"GetValueOrDefault\s*\(", text);
                Assert.DoesNotContain("Cs2jUnsigned", text);
                Assert.DoesNotContain("CS2J5357", text);
            }

            AssertGuardPins(byFile);

            // Runtime evidence: the full Native==Guarded matrix exits clean.
            var appProject = Path.Combine(
                intermediate, "SampleNumericBoundaryApp", "SampleNumericBoundaryApp.csproj");
            var (exitCode, output) = await RunDotNetAsync(
                "run", $"-c Release --project \"{appProject}\" --no-launch-profile");
            Assert.True(exitCode == 0, $"C22 sample app exited with {exitCode}:\n{output}");
            Assert.Contains("numeric-boundary sample OK", output);

            // Idempotency: a second chain run reproduces the Nullable tree exactly.
            var second = await RunPassAsync(
                Path.Combine(fixtureDir, "SampleNumericBoundarySolution.sln"),
                idempotentRoot,
                verify: false);
            Assert.True(second.Success);
            Assert.Equal(
                result.ExecutedSupportLibraries,
                second.ExecutedSupportLibraries);

            var secondNullableDir = Path.Combine(
                idempotentRoot, IntermediateName, "SampleNumericBoundaryLib", "Nullable");
            foreach (var (fileName, firstText) in byFile)
            {
                var secondText = File.ReadAllText(Path.Combine(secondNullableDir, fileName));
                Assert.Equal(firstText, secondText);
            }
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }

            if (Directory.Exists(idempotentRoot))
            {
                Directory.Delete(idempotentRoot, recursive: true);
            }
        }
    }

    /// <summary>
    /// Exact guard-shape pins over whitespace-compacted text, one set per
    /// fixture category. They lock that the Native lifted forms became the
    /// same HasValue/Value shape as the hand-written Guarded samples.
    /// </summary>
    private static void AssertGuardPins(IReadOnlyDictionary<string, string> byFile)
    {
        string Compact(string name) => Regex.Replace(byFile[name], @"\s+", "");

        var arithmetic = Compact("NullableArithmetic.cs");
        Assert.Contains("a.HasValue&&b.HasValue?(int?)(a.Value+b.Value):(int?)null", arithmetic);
        Assert.Contains("a.HasValue?(int?)(a.Value+b):(int?)null", arithmetic);
        Assert.Contains("a.HasValue&&b.HasValue?(long?)(a.Value-b.Value):(long?)null", arithmetic);
        Assert.Contains("a.HasValue?(double?)(a.Value*b):(double?)null", arithmetic);
        Assert.Contains("a.HasValue?(int?)(-a.Value):(int?)null", arithmetic);
        Assert.Contains("a.HasValue?(long?)(+a.Value):(long?)null", arithmetic);

        var relational = Compact("NullableRelational.cs");
        Assert.Contains("a.HasValue&&b.HasValue&&a.Value>=b.Value", relational);
        Assert.Contains("a.HasValue&&a.Value>=b", relational);
        Assert.Contains("a.HasValue&&a.Value<b", relational);

        var equality = Compact("NullableEquality.cs");
        Assert.Contains(
            "(!a.HasValue&&!b.HasValue)||(a.HasValue&&b.HasValue&&a.Value==b.Value)", equality);
        Assert.Contains("(a.HasValue^b.HasValue)||(a.HasValue&&b.HasValue&&a.Value!=b.Value)",
            equality);
        Assert.Contains("a.HasValue&&a.Value==b", equality);
        Assert.Contains("(!a.HasValue)", equality);
        Assert.Contains("(a.HasValue)", equality);
        Assert.Contains("a.HasValue&&a.Value", equality);

        var logic = Compact("NullableBoolLogic.cs");
        Assert.Contains(".HasValue", logic);
        Assert.Contains("!a.Value", logic);
        Assert.Contains("a.Value^b.Value", logic);
        Assert.Contains("(bool?)(!a.Value)", logic);

        var gov = Compact("NullableGetValueOrDefault.cs");
        Assert.Contains("(a.HasValue?a.Value:default(int))", gov);
        Assert.Contains("(a.HasValue?a.Value:d)", gov);
        Assert.Contains("__cs2j_n_d", gov);
        Assert.Contains("probe.Next()", gov);

        var conversions = Compact("NullableConversions.cs");
        Assert.Contains("NativeUnwrapCast(int?a)=>a.Value", conversions);
        Assert.Contains("NativeUnwrapMember(int?a)=>a.Value", conversions);
        Assert.Contains("a.HasValue?(long?)(long)a.Value:(long?)null", conversions);
        Assert.Contains("a.HasValue?(byte?)(byte)a.Value:(byte?)null", conversions);
        Assert.Contains("a.HasValue?(int?)(int)a.Value:(int?)null", conversions);

        var enums = Compact("NullableEnumBackstop.cs");
        Assert.Contains(".HasValue", enums);
        Assert.Contains("(int)a.Value&(int)b.Value", enums);
        Assert.Contains("(int)a.Value|(int)b.Value", enums);
        Assert.Contains("~((int)a.Value)", enums);
        Assert.Contains("(int?)(int)a.Value", enums);

        var increment = Compact("NullableIncrement.cs");
        Assert.Contains("x.HasValue?(int?)(x.Value+1):(int?)null", increment);
        Assert.Contains(
            "__cs2j_n_o2.HasValue?(int?)(__cs2j_n_o2.Value-1):(int?)null", increment);
        Assert.Contains("x.HasValue&&y.HasValue?(int?)(x.Value+y.Value):(int?)null", increment);
        Assert.Contains("x.HasValue?(int?)(x.Value+y):(int?)null", increment);
        Assert.Contains("__cs2j_n_o", increment);
    }

    private static async Task<(int ExitCode, string Output)> RunDotNetAsync(
        string verb, string arguments)
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
