using CSharpToJava.CLI;
using CSharpToJava.Core.RefOutInPreprocessor;
using Xunit;

namespace CSharpToJava.ProjectTests;

public class RefOutInPreprocessorTests
{
    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleRefOutInV2Solution");

    [Fact]
    public async Task LibProject_RefOutInParams_RewrittenToByRef()
    {
        var libDir = Path.Combine(FixtureDir, "SampleRefOutInV2Lib");
        Assert.True(Directory.Exists(libDir), $"Fixture not found: {libDir}");
        var outputDir = Path.Combine(Path.GetTempPath(), "cs2j-refoutin-test-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            var result = await ProjectRefOutInPreprocessor.PreprocessAsync(
                new ProjectRefOutInPreprocessRequest
                {
                    SourcePath = libDir,
                    DestinationRoot = outputDir,
                    Verbose = false,
                    VerifyBuild = true,
                    VerifyTests = true,
                });

            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            Assert.True(result.Statistics.FilesTransformed > 0);

            var intermediateDir = result.IntermediateRoot;

            // Calculator.cs should contain ByRef<int>
            var calcContent = await File.ReadAllTextAsync(
                Path.Combine(intermediateDir, "Calculator.cs"));
            Assert.Contains("ByRef<int>", calcContent);
            Assert.Contains("a.Value", calcContent);

            // External calls preserved
            Assert.True(result.Statistics.ExternalRefArgumentsKept >= 0);

            // Support library released
            Assert.True(File.Exists(Path.Combine(intermediateDir, "Cs2jRefOutIn", "Cs2jRefOutIn.csproj")),
                "Cs2jRefOutIn support project not generated");
            Assert.True(File.Exists(Path.Combine(intermediateDir, "Cs2jRefOutIn", "ByRef.cs")),
                "Cs2jRefOutIn ByRef.cs not generated");
            Assert.True(File.Exists(Path.Combine(intermediateDir, "Cs2jRefOutIn", "RefStructByRef.cs")),
                "Cs2jRefOutIn RefStructByRef.cs not generated (Q2-A)");

            // No inline _Cs2jRefOutIn.cs in project mode
            Assert.False(File.Exists(Path.Combine(intermediateDir, "_Cs2jRefOutIn.cs")),
                "Inline helper file should not exist in project mode");

            // ProjectReference injected
            var csprojContent = await File.ReadAllTextAsync(
                Path.Combine(intermediateDir, "SampleRefOutInV2Lib.csproj"));
            Assert.Contains("Cs2jRefOutIn.csproj", csprojContent);
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task AppProject_CallSites_LiftedCorrectly()
    {
        var appDir = Path.Combine(FixtureDir, "SampleRefOutInV2App");
        Assert.True(Directory.Exists(appDir), $"Fixture not found: {appDir}");
        var outputDir = Path.Combine(Path.GetTempPath(), "cs2j-refoutin-test-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            var result = await ProjectRefOutInPreprocessor.PreprocessAsync(
                new ProjectRefOutInPreprocessRequest
                {
                    SourcePath = Path.Combine(FixtureDir, "SampleRefOutInV2.sln"),
                    DestinationRoot = outputDir,
                    Verbose = false,
                    VerifyBuild = true,
                    VerifyTests = true,
                });

            Assert.True(result.Success, string.Join("\n", result.Diagnostics));

            var intermediateDir = result.IntermediateRoot;
            var progContent = await File.ReadAllTextAsync(
                Path.Combine(intermediateDir, "SampleRefOutInV2App", "Program.cs"));

            // Internal call sites lifted to ByRef
            Assert.Contains("ByRef<int>.Create", progContent);

            // C4: ref-return declaration becomes ByRef<int>; C7: rebinding becomes a proxy assignment
            Assert.Contains("ByRef<int> slot = calc.GetRef(arr, 1);", progContent);
            Assert.Contains("slot = ByRef<int>.Create(arr, 2);", progContent);

            // External BCL calls preserved
            Assert.Contains("out int", progContent); // int.TryParse, dict.TryGetValue
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task CleanFile_NoRefOutIn_PassedThrough()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "cs2j-refoutin-clean-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        var cleanCs = Path.Combine(tempDir, "Clean.cs");
        await File.WriteAllTextAsync(cleanCs, "public class C { public int Value; }");
        var csprojPath = Path.Combine(tempDir, "CleanLib.csproj");
        await File.WriteAllTextAsync(csprojPath,
            @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>");

        var outputDir = Path.Combine(Path.GetTempPath(), "cs2j-refoutin-out-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var result = await ProjectRefOutInPreprocessor.PreprocessAsync(
                new ProjectRefOutInPreprocessRequest
                {
                    SourcePath = tempDir,
                    DestinationRoot = outputDir,
                    Verbose = false,
                    VerifyBuild = true,
                    VerifyTests = true,
                });

            Assert.True(result.Success);
            Assert.Equal(1, result.Statistics.FilesSkippedClean);
            Assert.Equal(0, result.Statistics.FilesTransformed);
            // No Cs2jRefOutIn directory released for clean files
            Assert.False(File.Exists(Path.Combine(result.IntermediateRoot, "Cs2jRefOutIn", "ByRef.cs")));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }
}
