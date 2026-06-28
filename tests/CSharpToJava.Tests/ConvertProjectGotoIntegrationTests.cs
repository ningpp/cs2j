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
    public async Task ConvertProject_DefaultsThroughNoGotoSourceProject()
    {
        var tempRoot = CreateTempDirectory();
        var sourceRoot = Path.Combine(tempRoot, "Demo");
        var destinationRoot = Path.Combine(tempRoot, "java");
        Directory.CreateDirectory(sourceRoot);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "Demo.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "Sample.cs"), """
            namespace Demo;
            public class Sample
            {
                public int M()
                {
                    int value = 0;
                    goto Done;
                    value = 1;
                Done:
                    return value;
                }
            }
            """);

            var exit = await Program.MainImpl([
                "convert-project",
                "-s", sourceRoot,
                "-d", destinationRoot,
                "--no-cache",
            ]);

            Assert.Equal(0, exit);
            var noGotoRoot = Path.Combine(destinationRoot, ProjectGotoPreprocessor.IntermediateDirectoryName);
            var noGotoSample = Path.Combine(noGotoRoot, "Sample.cs");
            Assert.True(File.Exists(noGotoSample), $"Expected no-goto source mirror at {noGotoSample}");
            AssertNoGotoOrLabel(await File.ReadAllTextAsync(noGotoSample));
            Assert.Contains(
                Directory.EnumerateFiles(destinationRoot, "*.java", SearchOption.AllDirectories),
                path => path.EndsWith("Sample.java", StringComparison.OrdinalIgnoreCase));
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
    public async Task ConvertProject_ExtraDeps_AddsDependenciesToGeneratedPomAndPlan()
    {
        var tempRoot = CreateTempDirectory();
        var sourceRoot = Path.Combine(tempRoot, "Demo");
        var destinationRoot = Path.Combine(tempRoot, "java");
        Directory.CreateDirectory(sourceRoot);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, "Sample.cs"), """
            namespace Demo;
            public class Sample
            {
                public int M() => 1;
            }
            """);

            var exit = await Program.MainImpl([
                "convert-project",
                "-s", sourceRoot,
                "-d", destinationRoot,
                "--no-cache",
                "--extra-deps", "io.github.ningpp:System.Private.Uri:0.0.1-SNAPSHOT,org.example:extra-lib:1.2.3",
            ]);

            Assert.Equal(0, exit);
            var pom = await File.ReadAllTextAsync(Path.Combine(destinationRoot, "pom.xml"));
            Assert.Contains("<groupId>io.github.ningpp</groupId>", pom, StringComparison.Ordinal);
            Assert.Contains("<artifactId>System.Private.Uri</artifactId>", pom, StringComparison.Ordinal);
            Assert.Contains("<version>0.0.1-SNAPSHOT</version>", pom, StringComparison.Ordinal);
            Assert.Contains("<groupId>org.example</groupId>", pom, StringComparison.Ordinal);
            Assert.Contains("<artifactId>extra-lib</artifactId>", pom, StringComparison.Ordinal);
            Assert.Contains("<version>1.2.3</version>", pom, StringComparison.Ordinal);

            using var planDocument = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(destinationRoot, "cs2j-workspace-plan.json")));
            var dependencies = planDocument.RootElement
                .GetProperty("modules")[0]
                .GetProperty("dependencies")
                .EnumerateArray()
                .ToList();

            Assert.Contains(dependencies, dependency =>
                dependency.GetProperty("groupId").GetString() == "io.github.ningpp"
                && dependency.GetProperty("artifactId").GetString() == "System.Private.Uri"
                && dependency.GetProperty("version").GetString() == "0.0.1-SNAPSHOT");
            Assert.Contains(dependencies, dependency =>
                dependency.GetProperty("groupId").GetString() == "org.example"
                && dependency.GetProperty("artifactId").GetString() == "extra-lib"
                && dependency.GetProperty("version").GetString() == "1.2.3");
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ConvertProject_ExtraDeps_AddsDependenciesToEveryGeneratedModulePomAndPlanModule()
    {
        var tempRoot = CreateTempDirectory();
        var sourceRoot = Path.Combine(tempRoot, "Demo");
        var libRoot = Path.Combine(sourceRoot, "Lib");
        var appRoot = Path.Combine(sourceRoot, "App");
        var destinationRoot = Path.Combine(tempRoot, "java");
        Directory.CreateDirectory(libRoot);
        Directory.CreateDirectory(appRoot);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(libRoot, "Lib.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
            await File.WriteAllTextAsync(Path.Combine(libRoot, "LibType.cs"), """
            namespace Demo.Lib;
            public class LibType
            {
                public int Value() => 41;
            }
            """);

            await File.WriteAllTextAsync(Path.Combine(appRoot, "App.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\Lib\Lib.csproj" />
              </ItemGroup>
            </Project>
            """);
            await File.WriteAllTextAsync(Path.Combine(appRoot, "AppType.cs"), """
            namespace Demo.App;
            public class AppType
            {
                public int Value() => new Demo.Lib.LibType().Value() + 1;
            }
            """);

            var exit = await Program.MainImpl([
                "convert-project",
                "-s", Path.Combine(appRoot, "App.csproj"),
                "-d", destinationRoot,
                "--no-cache",
                "--extra-deps", "io.github.ningpp:System.Private.Uri:0.0.1-SNAPSHOT",
            ]);

            Assert.Equal(0, exit);

            var modulePoms = Directory.EnumerateFiles(destinationRoot, "pom.xml", SearchOption.AllDirectories)
                .Where(path => !string.Equals(path, Path.Combine(destinationRoot, "pom.xml"), StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Assert.True(modulePoms.Length >= 2, $"Expected at least two module poms under {destinationRoot}");
            foreach (var modulePomPath in modulePoms)
            {
                var pom = await File.ReadAllTextAsync(modulePomPath);
                Assert.Contains("<groupId>io.github.ningpp</groupId>", pom, StringComparison.Ordinal);
                Assert.Contains("<artifactId>System.Private.Uri</artifactId>", pom, StringComparison.Ordinal);
                Assert.Contains("<version>0.0.1-SNAPSHOT</version>", pom, StringComparison.Ordinal);
            }

            using var planDocument = JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(destinationRoot, "cs2j-workspace-plan.json")));
            var modules = planDocument.RootElement
                .GetProperty("modules")
                .EnumerateArray()
                .ToList();
            Assert.True(modules.Count >= 2, "Expected a workspace plan with at least two modules.");
            foreach (var module in modules)
            {
                var dependencies = module.GetProperty("dependencies").EnumerateArray().ToList();
                Assert.Contains(dependencies, dependency =>
                    dependency.GetProperty("groupId").GetString() == "io.github.ningpp"
                    && dependency.GetProperty("artifactId").GetString() == "System.Private.Uri"
                    && dependency.GetProperty("version").GetString() == "0.0.1-SNAPSHOT");
            }
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
