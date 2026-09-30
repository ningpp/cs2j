using CSharpToJava.CLI;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.Workspace;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// End-to-end: the isolated index-range pass desugars a sample project, and
/// the main C#->Java pipeline converts the desugared calls through the
/// existing type/method mapping path. Pins the lowercase ArrayHelper.slice
/// mapping (no explicit member entry: PascalToCamelCase + compat lookup).
/// </summary>
public class IndexRangeConversionEndToEndTests
{
    private static ConversionOptions Options => new()
    {
        TypeMappingConfigPath = Path.Combine(
            AppContext.BaseDirectory, "config", "TypeMappings.json"),
    };

    [Fact]
    public async Task IndexRangePreprocessedProject_ConvertsToJava_WithSliceMapping()
    {
        var workDir = Path.Combine(
            Path.GetTempPath(), "cs2j-ir-e2e-" + Guid.NewGuid().ToString("N"));
        var outputDir = Path.Combine(workDir, "out");

        try
        {
            Directory.CreateDirectory(workDir);
            File.WriteAllText(
                Path.Combine(workDir, "Sample.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
                "  <PropertyGroup>\n" +
                "    <TargetFramework>net10.0</TargetFramework>\n" +
                "    <Nullable>disable</Nullable>\n" +
                "  </PropertyGroup>\n" +
                "</Project>\n");
            File.WriteAllText(
                Path.Combine(workDir, "SliceOps.cs"),
                "public class SliceOps\n" +
                "{\n" +
                "    public static int[] Middle(int[] a) => a[1..^1];\n" +
                "    public static string Tail(string s) => s[1..];\n" +
                "}\n");

            // 1) Isolated index-range pass (all defaults off + index-range on).
            var chainResult = await CliPreprocessChain.RunAsync(new CliChainRequest
            {
                SourcePath = Path.Combine(workDir, "Sample.csproj"),
                DestinationRoot = outputDir,
                MakerOptionsFactory = () =>
                    new CSharpToJava.Core.ReadOnlyStructMaker
                        .ReadOnlyStructMakerOptions(),
                RecordReadOnlyOptionsFactory = () =>
                    new CSharpToJava.Core.RecordReadOnly
                        .RecordReadOnlyOptions(),
                JavaKeywordOptionsFactory = () =>
                    new CSharpToJava.Core.JavaKeywords
                        .JavaKeywordOptions(),
                MemberDesugarOptionsFactory = () =>
                    new CSharpToJava.Core.MemberDesugar
                        .MemberDesugarOptions(),
                LegacyToggles = PassCatalog.BuildRegistry().Ordered
                    .Select(d => d.Id)
                    .ToDictionary(
                        id => id, _ => false, StringComparer.OrdinalIgnoreCase),
                EnablePassIds = new[] { PassIds.IndexRange },
            });

            Assert.True(chainResult.Success);

            // 2) Load the preprocessed tree.
            SolutionLoader.EnsureMSBuildRegistered();
            using var loader = new SolutionLoader();
            var workspaceProjects = await loader
                .TryOpenAsync(chainResult.FinalSourcePath);
            Assert.NotNull(workspaceProjects);
            Assert.NotEmpty(workspaceProjects);

            // 3) The support project is a symbol source only; never converted.
            var convertProjects = workspaceProjects
                .Where(p => !Cs2jArrayProject.IsCs2jArrayProject(p.FilePath))
                .ToList();

            var pipeline = new ConversionPipeline();
            var converted = new List<ConversionResult>();
            foreach (var wp in convertProjects)
            {
                converted.AddRange(await pipeline.ConvertProjectWithPartialMergeAsync(
                    wp.Directory, Options,
                    additionalSemanticProjectPaths: convertProjects
                        .Select(p => p.Directory),
                    projectName: wp.Name,
                    projectFilePath: wp.FilePath));
            }

            var sliceJava = converted.FirstOrDefault(
                c => c.FileName.Contains("SliceOps"));
            Assert.NotNull(sliceJava);

            // Pascal Slice -> camel slice via automatic method mapping.
            Assert.Contains("ArrayHelper.slice(", sliceJava.GeneratedCode);
            Assert.DoesNotContain("ArrayHelper.Slice(", sliceJava.GeneratedCode);
            // string open-right range -> Substring(i) -> substring(i).
            Assert.Contains(".substring(1)", sliceJava.GeneratedCode);
        }
        finally
        {
            // The loaded workspace can hold file handles; retry a few times.
            for (var attempt = 0; attempt < 3 && Directory.Exists(workDir); attempt++)
            {
                try
                {
                    Directory.Delete(workDir, recursive: true);
                }
                catch (IOException)
                {
                    Thread.Sleep(200);
                }
                catch (UnauthorizedAccessException)
                {
                    Thread.Sleep(200);
                }
            }
        }
    }
}
