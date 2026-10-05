using System.Text.Json;
using CSharpToJava.CLI;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.EnumDeclaration;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.LinqChannel;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C26 M1 reachability gate for the linq-channel pass: the standalone entry
/// point (CliPreprocessChain with --pass-style explicit selection and every
/// legacy toggle off) must construct the two-phase adapter, run it end to end
/// over a real restore/build-verified intermediate tree and land the
/// schemaVersion 2 channel report. The default-off path must not materialize
/// any intermediate tree.
///
/// Full IQueryable-preservation and cross-project equivalence verification is
/// exercised by SampleLinqChannelSolution (design §7.6; M2/M3).
/// </summary>
public sealed class LinqChannelPassProjectTests
{
    private const string PassId = "linq-channel";
    private const string IntermediateDirName = ".cs2j-linq-channel-src";
    private const string ReportFileName = "cs2j-linq-channel-report.json";

    private static Task<CliChainResult> RunChainAsync(
        string sourcePath,
        string outputRoot,
        IReadOnlyCollection<string>? enablePassIds,
        bool verifyBuild = true,
        bool verifyTests = true)
    {
        var registry = PassCatalog.BuildRegistry();
        return CliPreprocessChain.RunAsync(new CliChainRequest
        {
            SourcePath = sourcePath,
            DestinationRoot = outputRoot,
            MakerOptionsFactory = () => new ReadOnlyStructMakerOptions(),
            RecordReadOnlyOptionsFactory = () => new RecordReadOnlyOptions(),
            MemberDesugarOptionsFactory = () => new MemberDesugarOptions(),
            EnumDeclarationOptionsFactory = () => new EnumDeclarationOptions(),
            LexicalNormalizeOptionsFactory = () =>
                new CSharpToJava.Core.LexicalNormalize.LexicalNormalizeOptions(),
            EqualityConcatOptionsFactory = () =>
                new CSharpToJava.Core.EqualityConcat.EqualityConcatOptions(),
            UnsafeFfmOptionsFactory = () => new CSharpToJava.Core.UnsafeFfm.UnsafeFfmOptions(),
            ExpressionDesugarOptionsFactory = () =>
                new CSharpToJava.Core.ExpressionDesugar.ExpressionDesugarOptions(),
            StructValueSemanticsOptionsFactory = () =>
                new CSharpToJava.Core.StructValueSemantics.StructValueSemanticsOptions(),
            JavaKeywordOptionsFactory = () => new JavaKeywordOptions(),
            LinqChannelOptionsFactory = () => new LinqChannelOptions(),
            LegacyToggles = PassChainFactory.BuildAllDisabledToggles(registry),
            EnablePassIds = enablePassIds,
            VerifyBuild = verifyBuild,
            VerifyTests = verifyTests,
        });
    }

    [Fact]
    public async Task EnabledChain_RunsTwoPhaseAdapter_VerifiesBuild_AndWritesReport()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "cs2j-c26-chain-" + Guid.NewGuid().ToString("N"));
        var sourceDir = Path.Combine(tempRoot, "src");
        var outputDir = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(sourceDir);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(sourceDir, "Tools.cs"),
                """
                using System.Collections.Generic;
                using System.Linq;

                namespace SampleLinqUse;

                public static class Tools
                {
                    // Enumerable member chain: stage 2 expands procedurally.
                    public static int[] EvensDoubled(int[] src)
                        => src.Where(x => x % 2 == 0).Select(x => x * 2).ToArray();

                    // Query expression: stage 1 desugars, stage 2 expands.
                    public static int PositiveSum(int[] src)
                    {
                        var q = from x in src
                                where x > 0
                                select x;
                        return q.Sum();
                    }

                    // Plain foreach over a List: the foreach-rewrap entry
                    // point sees the collection through the observe hook.
                    public static int PlainLoop(List<int> values)
                    {
                        var total = 0;
                        foreach (var v in values)
                        {
                            total += v;
                        }

                        return total;
                    }
                }
                """);
            await File.WriteAllTextAsync(
                Path.Combine(sourceDir, "SampleLinqUse.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n" +
                "    <TargetFramework>net10.0</TargetFramework>\n" +
                "    <ImplicitUsings>enable</ImplicitUsings>\n" +
                "    <Nullable>enable</Nullable>\n" +
                "    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>\n" +
                "  </PropertyGroup>\n</Project>\n");

            var errorCapture = new StringWriter();
            var originalError = Console.Error;
            CliChainResult result;
            try
            {
                Console.SetError(errorCapture);
                result = await RunChainAsync(
                    Path.Combine(sourceDir, "SampleLinqUse.csproj"),
                    outputDir,
                    new[] { PassId });
            }
            finally
            {
                Console.SetError(originalError);
            }

            Assert.True(
                result.Success,
                "linq-channel chain failed; stderr:\n" + errorCapture);
            Assert.Contains(IntermediateDirName, result.ExecutedIntermediateDirectoryNames);

            var intermediate = Path.Combine(outputDir, IntermediateDirName);
            Assert.True(Directory.Exists(intermediate), $"missing intermediate tree: {intermediate}");

            var reportPath = Path.Combine(intermediate, ReportFileName);
            Assert.True(File.Exists(reportPath), $"missing channel report: {reportPath}");

            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            var root = report.RootElement;
            Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
            Assert.True(
                root.GetProperty("desugaredQueryCount").GetInt32() >= 1,
                "stage 1 must count the query expression");
            Assert.True(
                root.GetProperty("rewrittenChainCount").GetInt32() >= 1,
                "stage 2 must rewrite at least one Enumerable chain");
            Assert.True(
                root.GetProperty("routing").GetProperty("enumerableChainsExpanded").GetInt32() >= 1,
                "the Enumerable chains must route Expandable");
            Assert.True(
                root.GetProperty("routing").GetProperty("queryableChainsPreserved").GetInt32() == 0,
                "no IQueryable usage exists in this fixture");

            // The intermediate build verification already ran inside the
            // chain (verify build is part of the pass); assert no error
            // diagnostics leaked into stderr.
            Assert.DoesNotContain("CS2J1351", errorCapture.ToString());
            Assert.DoesNotContain("CS2J1350", errorCapture.ToString());
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DefaultOffChain_DoesNotTouchTree_AndReturnsInputPath()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "cs2j-c26-off-" + Guid.NewGuid().ToString("N"));
        var sourceDir = Path.Combine(tempRoot, "src");
        var outputDir = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(sourceDir);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(sourceDir, "C.cs"),
                "namespace SampleLinqUse;\n\npublic static class C { public static int[] Mk() => new[] { 1 }; }\n");
            var projectPath = Path.Combine(sourceDir, "SampleLinqUse.csproj");
            await File.WriteAllTextAsync(
                projectPath,
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n" +
                "    <TargetFramework>net10.0</TargetFramework>\n" +
                "    <ImplicitUsings>enable</ImplicitUsings>\n" +
                "  </PropertyGroup>\n</Project>\n");

            // All-default chain without explicit selection: the plan is
            // empty (all-default-off legacy toggles, linq-channel descriptor
            // default-off), so no tree must be materialized.
            var result = await RunChainAsync(projectPath, outputDir, enablePassIds: null);

            Assert.True(
                result.Success,
                "an empty plan must succeed");
            Assert.DoesNotContain(IntermediateDirName, result.ExecutedIntermediateDirectoryNames);
            Assert.Equal(Path.GetFullPath(projectPath), Path.GetFullPath(result.FinalSourcePath));
            Assert.False(
                Directory.Exists(Path.Combine(outputDir, IntermediateDirName)),
                "default-off run must not materialize the c26 intermediate tree");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
