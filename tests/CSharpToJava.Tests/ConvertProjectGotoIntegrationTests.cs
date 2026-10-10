using CSharpToJava.CLI;
using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Tests;

public sealed class ConvertProjectGotoIntegrationTests
{
    [Fact]
    public async Task ProjectGotoPreprocessor_CopiesProjectTreeAndEliminatesDirtySources()
    {
        var tempRoot = CreateTempDirectory();
        var sourceRoot = Path.Combine(tempRoot, "source");
        var destinationRoot = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(sourceRoot);

        try
        {
            var dirtySource = Path.Combine(sourceRoot, "Dirty.cs");
            var cleanSource = Path.Combine(sourceRoot, "Clean.cs");
            var resource = Path.Combine(sourceRoot, "data.txt");
            await File.WriteAllTextAsync(dirtySource, "class Dirty { void M() { goto Done; Done: return; } }");
            await File.WriteAllTextAsync(cleanSource, "class Clean { void M() { int x = 1; } }");
            await File.WriteAllTextAsync(resource, "payload");

            var result = await ProjectGotoPreprocessor.PreprocessAsync(new ProjectGotoPreprocessRequest
            {
                SourcePath = sourceRoot,
                DestinationRoot = destinationRoot,
                Force = true,
            });

            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            Assert.True(result.Statistics.FilesTransformed >= 1);
            Assert.Equal(
                Path.Combine(destinationRoot, ProjectGotoPreprocessor.IntermediateDirectoryName),
                result.IntermediateRoot);

            var dirtyOutput = Path.Combine(result.IntermediateRoot, "Dirty.cs");
            var cleanOutput = Path.Combine(result.IntermediateRoot, "Clean.cs");
            AssertNoGotoOrLabel(await File.ReadAllTextAsync(dirtyOutput));
            Assert.Equal(await File.ReadAllTextAsync(cleanSource), await File.ReadAllTextAsync(cleanOutput));
            Assert.Equal("payload", await File.ReadAllTextAsync(Path.Combine(result.IntermediateRoot, "data.txt")));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ConvertProject_NoEliminateGoto_SkipsNoGotoSourceProject()
    {
        var tempRoot = CreateTempDirectory();
        var sourceRoot = Path.Combine(tempRoot, "Demo");
        var destinationRoot = Path.Combine(tempRoot, "java");
        Directory.CreateDirectory(sourceRoot);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "Sample.cs"), "class Sample { void M() { goto Done; Done: return; } }");

            var exit = await Program.MainImpl([
                "convert-project",
                "-s", sourceRoot,
                "-d", destinationRoot,
                "--no-cache",
                "--no-eliminate-goto",
            ]);

            Assert.Equal(0, exit);
            Assert.False(Directory.Exists(Path.Combine(destinationRoot, ProjectGotoPreprocessor.IntermediateDirectoryName)));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ConvertProject_InvalidExtraDeps_ReturnsFailureBeforeWritingPom()
    {
        var tempRoot = CreateTempDirectory();
        var sourceRoot = Path.Combine(tempRoot, "Demo");
        var destinationRoot = Path.Combine(tempRoot, "java");
        Directory.CreateDirectory(sourceRoot);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "Sample.cs"), "class Sample { }");

            var exit = await Program.MainImpl([
                "convert-project",
                "-s", sourceRoot,
                "-d", destinationRoot,
                "--no-cache",
                "--extra-deps", "org.example::1.0.0",
            ]);

            Assert.Equal(1, exit);
            Assert.False(File.Exists(Path.Combine(destinationRoot, "pom.xml")));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "cs2j-convert-project-goto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void AssertNoGotoOrLabel(string code)
    {
        var root = CSharpSyntaxTree.ParseText(code).GetRoot();
        Assert.Empty(root.DescendantNodes().OfType<GotoStatementSyntax>());
        Assert.Empty(root.DescendantNodes().OfType<LabeledStatementSyntax>());
    }
}
