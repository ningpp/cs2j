using CSharpToJava.CLI;

namespace CSharpToJava.ProjectTests;

public class UnsignedTypeProjectTests
{
    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleUnsignedSolution");

    [Fact]
    public async Task LibProject_AllUnsignedTypes_RewrittenCorrectly()
    {
        var libDir = Path.Combine(FixtureDir, "SampleUnsignedLib");
        Assert.True(Directory.Exists(libDir), $"Fixture not found: {libDir}");

        var result = await ProjectUnsignedTypePreprocessor.PreprocessAsync(
            new ProjectUnsignedTypePreprocessRequest
            {
                SourcePath = libDir,
                DestinationRoot = Path.Combine(libDir, "..", ".test-output"),
                Verbose = false,
            });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.True(result.Statistics.FilesTransformed > 0);

        var intermediateDir = result.IntermediateRoot;
        Assert.True(Directory.Exists(intermediateDir));

        var typesContent = await File.ReadAllTextAsync(
            Path.Combine(intermediateDir, "UnsignedTypes.cs"));
        Assert.Contains("System.UInt32", typesContent);
        Assert.Contains("System.UInt64", typesContent);
        Assert.Contains("System.UInt16", typesContent);
        Assert.Contains("System.Byte", typesContent);

        var opsContent = await File.ReadAllTextAsync(
            Path.Combine(intermediateDir, "UnsignedOperators.cs"));
        Assert.Contains(".Add(", opsContent);
        Assert.Contains(".Subtract(", opsContent);
        Assert.Contains(".Multiply(", opsContent);
        Assert.Contains(".Equals(", opsContent);
        Assert.Contains(".CompareTo(", opsContent);

        var castsContent = await File.ReadAllTextAsync(
            Path.Combine(intermediateDir, "UnsignedCasts.cs"));
        Assert.Contains("(System.UInt32)", castsContent);
        Assert.Contains(".ToInt32()", castsContent);

        var arraysContent = await File.ReadAllTextAsync(
            Path.Combine(intermediateDir, "UnsignedArrays.cs"));
        Assert.Contains("System.UInt32[]", arraysContent);
        Assert.Contains("System.UInt32[,]", arraysContent);
        Assert.Contains("System.UInt32[][]", arraysContent);

        var genericsContent = await File.ReadAllTextAsync(
            Path.Combine(intermediateDir, "UnsignedGenerics.cs"));
        Assert.Contains("List<System.UInt32>", genericsContent);
        Assert.Contains("Dictionary<System.UInt32, System.UInt64>", genericsContent);

        Assert.True(File.Exists(Path.Combine(intermediateDir, "_Cs2jUnsignedExtensions.cs")),
            "Extension methods file not generated");

        Assert.Contains("using Cs2jUnsigned;", opsContent);
    }

    [Fact]
    public async Task AppProject_CrossProjectRewrite_Succeeds()
    {
        var appDir = Path.Combine(FixtureDir, "SampleUnsignedApp");
        Assert.True(Directory.Exists(appDir), $"Fixture not found: {appDir}");

        var result = await ProjectUnsignedTypePreprocessor.PreprocessAsync(
            new ProjectUnsignedTypePreprocessRequest
            {
                SourcePath = Path.Combine(FixtureDir, "SampleUnsignedSolution.sln"),
                DestinationRoot = Path.Combine(FixtureDir, ".test-output-sln"),
                Verbose = false,
            });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
    }

    [Fact]
    public async Task SlnProject_FullRewrite_AllFilesSucceed()
    {
        var slnPath = Path.Combine(FixtureDir, "SampleUnsignedSolution.sln");
        Assert.True(File.Exists(slnPath), $"Solution not found: {slnPath}");

        var result = await ProjectUnsignedTypePreprocessor.PreprocessAsync(
            new ProjectUnsignedTypePreprocessRequest
            {
                SourcePath = slnPath,
                DestinationRoot = Path.Combine(FixtureDir, ".test-output-sln"),
                Verbose = false,
            });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.True(result.Statistics.FilesTransformed > 0);
    }
}
