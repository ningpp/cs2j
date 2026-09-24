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
            Assert.Contains("Cs2jUnsigned.UnsignedMath.AddUInt32(", opsContent);
            Assert.Contains("Cs2jUnsigned.UnsignedMath.SubtractUInt32(", opsContent);
            Assert.Contains("Cs2jUnsigned.UnsignedMath.MultiplyUInt32(", opsContent);
            Assert.Contains("Cs2jUnsigned.UnsignedMath.CompareUInt32(", opsContent);

            var castsContent = await File.ReadAllTextAsync(
                Path.Combine(intermediateDir, "UnsignedCasts.cs"));
            Assert.Contains("(global::System.UInt32)", castsContent);
            Assert.Contains("Cs2jUnsigned.UnsignedMath.ToInt64UInt32(unchecked((int)x))", castsContent);
            Assert.Contains("Cs2jUnsigned.UnsignedMath.ToByteInt32(x)", castsContent);

            var arraysContent = await File.ReadAllTextAsync(
                Path.Combine(intermediateDir, "UnsignedArrays.cs"));
            Assert.Contains("System.UInt32[]", arraysContent);
            Assert.Contains("System.UInt32[,]", arraysContent);
            Assert.Contains("System.UInt32[][]", arraysContent);

            var genericsContent = await File.ReadAllTextAsync(
                Path.Combine(intermediateDir, "UnsignedGenerics.cs"));
            Assert.Contains("List<global::System.UInt32>", genericsContent);
            Assert.Contains("Dictionary<global::System.UInt32, global::System.UInt64>", genericsContent);

            Assert.True(File.Exists(Path.Combine(intermediateDir, "Cs2jUnsignedMath", "Cs2jUnsignedMath.csproj")),
                "Cs2jUnsignedMath support project not generated");
            Assert.True(File.Exists(Path.Combine(intermediateDir, "Cs2jUnsignedMath", "UnsignedMath.cs")),
                "Cs2jUnsignedMath helper source not generated");
            Assert.False(File.Exists(Path.Combine(intermediateDir, "_Cs2jUnsignedMath.cs")),
                "Old flat helper file must not be generated");
            Assert.False(File.Exists(Path.Combine(intermediateDir, "_Cs2jUnsignedExtensions.cs")),
                "Old extension file must not be generated");

            var libCsprojContent = await File.ReadAllTextAsync(
                Path.Combine(intermediateDir, "SampleUnsignedLib.csproj"));
            Assert.Contains(
                "<ProjectReference Include=\"Cs2jUnsignedMath\\Cs2jUnsignedMath.csproj\" />",
                libCsprojContent);
            Assert.DoesNotContain("_Cs2jUnsignedMath.cs", libCsprojContent);
            Assert.DoesNotContain("using Cs2jUnsigned;", opsContent);
            Assert.Contains("Cs2jUnsigned.UnsignedMath", opsContent);
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

            foreach (var projectName in new[] { "SampleUnsignedApp", "SampleUnsignedLib" })
            {
                var csprojContent = await File.ReadAllTextAsync(
                    Path.Combine(result.IntermediateRoot, projectName, projectName + ".csproj"));
                Assert.Contains(
                    "<ProjectReference Include=\"..\\Cs2jUnsignedMath\\Cs2jUnsignedMath.csproj\" />",
                    csprojContent);
                Assert.DoesNotContain("_Cs2jUnsignedMath.cs", csprojContent);
            }

            Assert.True(File.Exists(Path.Combine(result.IntermediateRoot, "Cs2jUnsignedMath", "UnsignedMath.cs")),
                "Cs2jUnsignedMath helper source not generated");
            Assert.False(File.Exists(Path.Combine(result.IntermediateRoot, "_Cs2jUnsignedMath.cs")),
                "Old flat helper file must not be generated");
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

            foreach (var projectName in new[] { "SampleUnsignedApp", "SampleUnsignedLib" })
            {
                var csprojContent = await File.ReadAllTextAsync(
                    Path.Combine(result.IntermediateRoot, projectName, projectName + ".csproj"));
                Assert.Contains(
                    "<ProjectReference Include=\"..\\Cs2jUnsignedMath\\Cs2jUnsignedMath.csproj\" />",
                    csprojContent);
                Assert.DoesNotContain("_Cs2jUnsignedMath.cs", csprojContent);
            }

            Assert.True(File.Exists(Path.Combine(result.IntermediateRoot, "Cs2jUnsignedMath", "UnsignedMath.cs")),
                "Cs2jUnsignedMath helper source not generated");
            Assert.False(File.Exists(Path.Combine(result.IntermediateRoot, "_Cs2jUnsignedMath.cs")),
                "Old flat helper file must not be generated");
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
    public async Task ExplicitCompileLib_ProjectReferenceInjected()
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
            Assert.Contains(
                "<ProjectReference Include=\"Cs2jUnsignedMath\\Cs2jUnsignedMath.csproj\" />",
                csprojContent);
            Assert.DoesNotContain("_Cs2jUnsignedMath.cs", csprojContent);
            Assert.DoesNotContain(".test-output-explicit", csprojContent);
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
            Assert.Contains(
                "<ProjectReference Include=\"..\\Cs2jUnsignedMath\\Cs2jUnsignedMath.csproj\" />",
                srcContent);
            Assert.Contains(
                "<ProjectReference Include=\"..\\Cs2jUnsignedMath\\Cs2jUnsignedMath.csproj\" />",
                testsContent);
            Assert.DoesNotContain(".test-output-cross", testsContent);
            Assert.True(File.Exists(Path.Combine(result.IntermediateRoot, "Cs2jUnsignedMath", "UnsignedMath.cs")),
                "Cs2jUnsignedMath helper source not generated at intermediate root");
            Assert.False(File.Exists(Path.Combine(result.IntermediateRoot, "_Cs2jUnsignedMath.cs")),
                "Old flat helper file must not be generated");
            Assert.False(Directory.Exists(Path.Combine(result.IntermediateRoot, "Tests", "Cs2jUnsignedMath")),
                "Helper project must not be copied into project directories");
            Assert.False(Directory.Exists(Path.Combine(result.IntermediateRoot, "Src", "Cs2jUnsignedMath")),
                "Helper project must not be copied into project directories");
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
    public async Task Rerun_DoesNotLeaveOldExtensionArtifacts()
    {
        var libDir = Path.Combine(FixtureDir, "SampleUnsignedLib");
        var outputDir = Path.Combine(libDir, "..", ".test-output-rerun");

        try
        {
            for (var i = 0; i < 2; i++)
            {
                var result = await ProjectUnsignedTypePreprocessor.PreprocessAsync(
                    new ProjectUnsignedTypePreprocessRequest
                    {
                        SourcePath = libDir,
                        DestinationRoot = outputDir,
                        Verbose = false,
                    });
                Assert.True(result.Success, string.Join("\n", result.Diagnostics));
                Assert.False(Directory
                    .EnumerateFiles(result.IntermediateRoot, "_Cs2jUnsignedExtensions.cs", SearchOption.AllDirectories)
                    .Any(), "stale extension file after rerun");
                Assert.False(File.Exists(Path.Combine(result.IntermediateRoot, "_Cs2jUnsignedMath.cs")),
                    "old flat helper file after rerun");
            }
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
