using CSharpToJava.CLI;

namespace CSharpToJava.ProjectTests;

[Collection("ArrayMethodPreprocessSerial")]
public class ArrayMethodProjectTests
{
    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleArraySolution");

    [Fact]
    public async Task LibProject_ArrayMembers_RewrittenCorrectly()
    {
        var libDir = Path.Combine(FixtureDir, "SampleArrayLib");
        Assert.True(Directory.Exists(libDir), $"Fixture not found: {libDir}");
        var outputDir = Path.Combine(libDir, "..", ".test-output");

        try
        {
            var result = await ProjectArrayMethodPreprocessor.PreprocessAsync(
                new ProjectArrayMethodPreprocessRequest
                {
                    SourcePath = libDir,
                    DestinationRoot = outputDir,
                    Verbose = false,
                });

            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            Assert.True(result.Statistics.FilesTransformed > 0);

            var intermediateDir = result.IntermediateRoot;
            Assert.True(Directory.Exists(intermediateDir));

            var opsContent = await File.ReadAllTextAsync(
                Path.Combine(intermediateDir, "ArrayOperations.cs"));
            Assert.Contains("Cs2jArray.ArrayHelper.GetLength", opsContent);
            Assert.Contains("Cs2jArray.ArrayHelper.Clone", opsContent);
            Assert.Contains("Cs2jArray.ArrayHelper.Sort", opsContent);
            Assert.Contains("Cs2jArray.ArrayHelper.Resize", opsContent);
            Assert.Contains("Cs2jArray.ArrayHelper.Exists", opsContent);
            Assert.Contains("Cs2jArray.ArrayHelper.SetValue", opsContent);
            Assert.Contains("Cs2jArray.ArrayHelper.GetValue", opsContent);
            Assert.Contains("__cs2j_row0", opsContent); // foreach flattened

            // Cs2jArray lib released
            Assert.True(Directory.Exists(Path.Combine(intermediateDir, "Cs2jArray")));
            Assert.True(File.Exists(Path.Combine(intermediateDir, "Cs2jArray", "ArrayHelper.cs")));
            Assert.True(File.Exists(Path.Combine(intermediateDir, "Cs2jArray", "Cs2jArray.csproj")));

            // ProjectReference injected
            var libCsproj = await File.ReadAllTextAsync(
                Path.Combine(intermediateDir, "SampleArrayLib.csproj"));
            Assert.Contains("Cs2jArray.csproj", libCsproj);
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Solution_BothProjects_RewrittenAndCs2jArrayFiltered()
    {
        var slnPath = Path.Combine(FixtureDir, "SampleArray.sln");
        Assert.True(File.Exists(slnPath));
        var outputDir = Path.Combine(FixtureDir, ".test-output-sln");

        try
        {
            var result = await ProjectArrayMethodPreprocessor.PreprocessAsync(
                new ProjectArrayMethodPreprocessRequest
                {
                    SourcePath = slnPath,
                    DestinationRoot = outputDir,
                    Verbose = false,
                });

            Assert.True(result.Success, string.Join("\n", result.Diagnostics));

            // Lib has rewritten files, so it should have Cs2jArray injected
            var libCsproj = await File.ReadAllTextAsync(
                Path.Combine(result.IntermediateRoot, "SampleArrayLib", "SampleArrayLib.csproj"));
            Assert.Contains("Cs2jArray.csproj", libCsproj);
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }
}
