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
        var outputDir = Path.Combine(libDir, "..", ".test-output");

        try
        {
            var result = await ProjectUnsignedTypePreprocessor.PreprocessAsync(
                new ProjectUnsignedTypePreprocessRequest
                {
                    SourcePath = libDir,
                    DestinationRoot = outputDir,
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
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AppProject_CrossProjectRewrite_Succeeds()
    {
        var appDir = Path.Combine(FixtureDir, "SampleUnsignedApp");
        Assert.True(Directory.Exists(appDir), $"Fixture not found: {appDir}");
        var outputDir = Path.Combine(FixtureDir, ".test-output-sln");

        try
        {
            var result = await ProjectUnsignedTypePreprocessor.PreprocessAsync(
                new ProjectUnsignedTypePreprocessRequest
                {
                    SourcePath = Path.Combine(FixtureDir, "SampleUnsignedSolution.sln"),
                    DestinationRoot = outputDir,
                    Verbose = false,
                });

            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SlnProject_FullRewrite_AllFilesSucceed()
    {
        var slnPath = Path.Combine(FixtureDir, "SampleUnsignedSolution.sln");
        Assert.True(File.Exists(slnPath), $"Solution not found: {slnPath}");
        var outputDir = Path.Combine(FixtureDir, ".test-output-sln");

        try
        {
            var result = await ProjectUnsignedTypePreprocessor.PreprocessAsync(
                new ProjectUnsignedTypePreprocessRequest
                {
                    SourcePath = slnPath,
                    DestinationRoot = outputDir,
                    Verbose = false,
                });

            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            Assert.True(result.Statistics.FilesTransformed > 0);
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExplicitCompileLib_ExtensionFileAddedToProjectCompileItems()
    {
        var libDir = Path.Combine(FixtureDir, "SampleExplicitCompileLib");
        Assert.True(Directory.Exists(libDir), $"Fixture not found: {libDir}");
        var outputDir = Path.Combine(libDir, "..", ".test-output-explicit");

        try
        {
            var result = await ProjectUnsignedTypePreprocessor.PreprocessAsync(
                new ProjectUnsignedTypePreprocessRequest
                {
                    SourcePath = libDir,
                    DestinationRoot = outputDir,
                    Verbose = false,
                });

            Assert.True(result.Success, string.Join("\n", result.Diagnostics));

            var csprojPath = Path.Combine(result.IntermediateRoot, "SampleExplicitCompileLib.csproj");
            var csprojContent = await File.ReadAllTextAsync(csprojPath);
            Assert.Contains("_Cs2jUnsignedExtensions.cs", csprojContent);
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CrossDirectoryCompile_ExtensionFileAddedToCompilingProjects()
    {
        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "SampleCrossCompile");
        var slnPath = Path.Combine(fixtureDir, "SampleCrossCompile.sln");
        Assert.True(File.Exists(slnPath), $"Fixture not found: {slnPath}");
        var outputDir = Path.Combine(fixtureDir, ".test-output-cross");

        try
        {
            var result = await ProjectUnsignedTypePreprocessor.PreprocessAsync(
                new ProjectUnsignedTypePreprocessRequest
                {
                    SourcePath = slnPath,
                    DestinationRoot = outputDir,
                    Verbose = false,
                });

            Assert.True(result.Success, string.Join("\n", result.Diagnostics));

            var srcCsproj = Path.Combine(result.IntermediateRoot, "Src", "SampleCrossCompile.csproj");
            var testsCsproj = Path.Combine(result.IntermediateRoot, "Tests", "SampleCrossCompile.Tests.csproj");
            var srcContent = await File.ReadAllTextAsync(srcCsproj);
            var testsContent = await File.ReadAllTextAsync(testsCsproj);
            Assert.Contains("_Cs2jUnsignedExtensions.cs", srcContent);
            Assert.Contains("_Cs2jUnsignedExtensions.cs", testsContent);
            Assert.True(File.Exists(Path.Combine(result.IntermediateRoot, "Tests", "_Cs2jUnsignedExtensions.cs")),
                "Extension file not copied into the cross-directory compiling project");
            Assert.True(File.Exists(Path.Combine(result.IntermediateRoot, "Src", "_Cs2jUnsignedExtensions.cs")),
                "Extension file not copied into the referenced compiling project");
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
    }
}
