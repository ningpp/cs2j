using CSharpToJava.CLI;
using CSharpToJava.Core.DefaultParameterStripper;

namespace CSharpToJava.ProjectTests;

public class StripDefaultProjectTests
{
    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleStripDefaultSolution");

    [Fact]
    public async Task CrossProject_CallSiteUnchanged_AndBuildSucceeds()
    {
        var slnPath = Path.Combine(FixtureDir, "SampleStripDefaultSolution.sln");
        Assert.True(File.Exists(slnPath), $"Solution not found: {slnPath}");
        var outputDir = Path.Combine(FixtureDir, ".test-output");

        try
        {
            var result = await ProjectDefaultParameterStripper.PreprocessAsync(
                new ProjectDefaultParameterStripRequest
                {
                    SourcePath = slnPath,
                    DestinationRoot = outputDir,
                    Verbose = false,
                });

            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            Assert.True(result.Statistics.FilesTransformed > 0);
            Assert.True(result.Statistics.OverloadsGenerated > 0);

            // D1: App call sites must be byte-for-byte identical to original
            var originalAppPath = Path.Combine(FixtureDir, "SampleStripDefaultApp", "Program.cs");
            var preprocessedAppPath = Path.Combine(
                result.IntermediateRoot, "SampleStripDefaultApp", "Program.cs");
            var originalContent = await File.ReadAllTextAsync(originalAppPath);
            var preprocessedContent = await File.ReadAllTextAsync(preprocessedAppPath);
            Assert.Equal(originalContent, preprocessedContent);

            // Lib: defaults stripped
            var libPath = Path.Combine(result.IntermediateRoot, "SampleStripDefaultLib", "Defaults.cs");
            var libContent = await File.ReadAllTextAsync(libPath);
            Assert.DoesNotContain("= \"default\"", libContent);
            Assert.DoesNotContain("= true)", libContent);
            Assert.DoesNotContain("= 10)", libContent);
            Assert.DoesNotContain("= 20)", libContent);
            Assert.DoesNotContain("= \"unnamed\"", libContent);
            Assert.DoesNotContain("= \"world\"", libContent);

            // Overloads generated
            Assert.Contains("void Process(int a)", libContent);
            Assert.Contains("void Process(int a, string b)", libContent);
            Assert.Contains("int Calculate(int x)", libContent);
            Assert.Contains("int Calculate(int x, int y)", libContent);
            Assert.Contains("Service()", libContent);
            Assert.Contains("static string Greet()", libContent);
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task NoDefaults_FilesTransformedZero()
    {
        // Use a temp directory with a single .cs file that has no defaults
        var tempDir = Path.Combine(Path.GetTempPath(), $"cs2j-strip-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var srcFile = Path.Combine(tempDir, "NoDefaults.cs");
            await File.WriteAllTextAsync(srcFile, "class C { public void M(int a) { } }");
            var outputDir = Path.Combine(tempDir, ".test-output-nodata");

            var result = await ProjectDefaultParameterStripper.PreprocessAsync(
                new ProjectDefaultParameterStripRequest
                {
                    SourcePath = tempDir,
                    DestinationRoot = outputDir,
                    Verbose = false,
                    FilterUnsupportedProjects = false,
                    VerifyBuild = false,
                    VerifyTests = false,
                });

            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            Assert.Equal(0, result.Statistics.FilesTransformed);
            Assert.Equal(1, result.Statistics.FilesSkippedClean);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Rerun_DoesNotLeaveOldArtifacts()
    {
        var slnPath = Path.Combine(FixtureDir, "SampleStripDefaultSolution.sln");
        var outputDir = Path.Combine(FixtureDir, ".test-output-rerun");

        try
        {
            for (var i = 0; i < 2; i++)
            {
                var result = await ProjectDefaultParameterStripper.PreprocessAsync(
                    new ProjectDefaultParameterStripRequest
                    {
                        SourcePath = slnPath,
                        DestinationRoot = outputDir,
                        Verbose = false,
                    });
                Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            }
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }
}
