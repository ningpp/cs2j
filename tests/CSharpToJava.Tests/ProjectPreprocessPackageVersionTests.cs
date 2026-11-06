using CSharpToJava.CLI;

namespace CSharpToJava.Tests;

/// <summary>
/// RT10 NU1015: the intermediate tree flattens Directory.Build.props/targets and
/// drops eng/, so repository version properties evaluate to empty. These tests
/// cover the copy-time pinning (through an injected MSBuild evaluator, never a
/// real msbuild process) and the missing-import cleanup.
/// </summary>
public sealed class ProjectPreprocessPackageVersionTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _sourceRoot;

    public ProjectPreprocessPackageVersionTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "cs2j-pkg-version-" + Guid.NewGuid().ToString("N"));
        _sourceRoot = Path.Combine(_tempRoot, "repo");
        Directory.CreateDirectory(_sourceRoot);
    }

    public void Dispose()
    {
        MsBuildCompileEvaluator.PackageReferenceEvaluationOverride = null;
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temp cleanup is best-effort.
        }
    }

    private static Dictionary<string, string> Versions(params (string Identity, string Version)[] entries)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (identity, version) in entries)
        {
            map[identity] = version;
        }

        return map;
    }

    private string WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(_sourceRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return Path.GetFullPath(path);
    }

    [Fact]
    public void PinPackageReferenceVersions_ReplacesPropertyVersion()
    {
        const string content = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="X" Version="$(XVersion)" />
              </ItemGroup>
            </Project>
            """;

        var result = ProjectPreprocessSource.PinPackageReferenceVersions(
            content, Versions(("X", "1.2.3")));

        Assert.Contains("Version=\"1.2.3\"", result);
        Assert.DoesNotContain("$(XVersion)", result);
    }

    [Fact]
    public void PinPackageReferenceVersions_KeepsOriginalWhenEvaluationIsNull()
    {
        const string content = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="X" Version="$(XVersion)" />
              </ItemGroup>
            </Project>
            """;

        var result = ProjectPreprocessSource.PinPackageReferenceVersions(content, null);

        Assert.Contains("Version=\"$(XVersion)\"", result);
    }

    [Fact]
    public void PinPackageReferenceVersions_LeavesLiteralVersionUntouched()
    {
        const string content = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="X" Version="1.2.3" />
              </ItemGroup>
            </Project>
            """;

        var result = ProjectPreprocessSource.PinPackageReferenceVersions(
            content, Versions(("X", "9.9.9")));

        Assert.Contains("Version=\"1.2.3\"", result);
        Assert.DoesNotContain("9.9.9", result);
    }

    [Fact]
    public void PinPackageReferenceVersions_PinsCentralPackageVersion()
    {
        const string content = """
            <Project>
              <ItemGroup>
                <PackageVersion Include="X" Version="$(XVersion)" />
              </ItemGroup>
            </Project>
            """;

        var result = ProjectPreprocessSource.PinPackageReferenceVersions(
            content, Versions(("X", "1.2.3")));

        Assert.Contains("<PackageVersion Include=\"X\" Version=\"1.2.3\"", result);
    }

    [Fact]
    public void PinPackageReferenceVersionProperties_ResolvesPerProjectPropertyFromSiblingTargets()
    {
        var projectDirectory = Path.Combine(_tempRoot, "gen");
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(
            Path.Combine(projectDirectory, "Shared.targets"),
            """
            <Project>
              <ItemGroup>
                <PackageReference Include="Microsoft.CodeAnalysis.CSharp.Workspaces" Version="$(RoslynApiVersion)" />
              </ItemGroup>
            </Project>
            """);

        const string csproj = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <RoslynApiVersion>$(MicrosoftCodeAnalysisVersion_4_4)</RoslynApiVersion>
              </PropertyGroup>
            </Project>
            """;

        var result = ProjectPreprocessSource.PinPackageReferenceVersionProperties(
            csproj,
            Versions(("Microsoft.CodeAnalysis.CSharp.Workspaces", "4.4.0")),
            projectDirectory);

        Assert.Contains("<RoslynApiVersion>4.4.0</RoslynApiVersion>", result);
        Assert.DoesNotContain("$(MicrosoftCodeAnalysisVersion_4_4)", result);
    }

    [Fact]
    public void RemoveMissingImports_RemovesOnlyMissingTargets()
    {
        var baseDirectory = Path.Combine(_tempRoot, "intermediate");
        Directory.CreateDirectory(baseDirectory);
        File.WriteAllText(Path.Combine(baseDirectory, "present.props"), "<Project />");

        const string content = """
            <Project>
              <Import Project="present.props" />
              <Import Project="missing.props" />
              <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
            </Project>
            """;

        var result = ProjectPreprocessSource.RemoveMissingImports(content, baseDirectory);

        Assert.Contains("Project=\"present.props\"", result);
        Assert.DoesNotContain("missing.props", result);
        Assert.Contains("Sdk=\"Microsoft.NET.Sdk\"", result);
    }

    [Fact]
    public void RemoveMissingImports_DropsUnresolvableEngineeringImports()
    {
        var baseDirectory = Path.Combine(_tempRoot, "intermediate");
        Directory.CreateDirectory(baseDirectory);

        const string content = """
            <Project>
              <Import Project="..\..\Directory.Build.props" />
              <Import Project="$(RepositoryEngineeringDir)resources.targets" />
            </Project>
            """;

        var result = ProjectPreprocessSource.RemoveMissingImports(content, baseDirectory);

        Assert.DoesNotContain("Directory.Build.props", result);
        Assert.DoesNotContain("RepositoryEngineeringDir", result);
    }

    [Fact]
    public void RemoveMissingImports_DropsImplicitPackageReferencesWhenImportsAreRemoved()
    {
        var baseDirectory = Path.Combine(_tempRoot, "intermediate");
        Directory.CreateDirectory(baseDirectory);

        const string content = """
            <Project>
              <Import Project="$(RepositoryEngineeringDir)targetingpacks.targets" />
              <ItemGroup>
                <PackageReference Include="Build.Tasks" Version="1.0.0" IsImplicitlyDefined="true" />
                <PackageReference Include="Lib" Version="2.0.0" />
              </ItemGroup>
            </Project>
            """;

        var result = ProjectPreprocessSource.RemoveMissingImports(content, baseDirectory);

        Assert.DoesNotContain("Build.Tasks", result);
        Assert.Contains("Include=\"Lib\"", result);
    }

    [Fact]
    public void RemoveMissingImports_KeepsImplicitPackageReferencesWhenNothingWasRemoved()
    {
        var baseDirectory = Path.Combine(_tempRoot, "intermediate");
        Directory.CreateDirectory(baseDirectory);
        File.WriteAllText(Path.Combine(baseDirectory, "present.props"), "<Project />");

        const string content = """
            <Project>
              <Import Project="present.props" />
              <ItemGroup>
                <PackageReference Include="Build.Tasks" Version="1.0.0" IsImplicitlyDefined="true" />
              </ItemGroup>
            </Project>
            """;

        var result = ProjectPreprocessSource.RemoveMissingImports(content, baseDirectory);

        Assert.Contains("Build.Tasks", result);
    }

    [Fact]
    public async Task CopyProjectFilesAsync_PinsVersionThroughInjectedEvaluator()
    {
        var csproj = WriteFile(
            Path.Combine("App", "App.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="X" Version="$(XVersion)" />
              </ItemGroup>
            </Project>
            """);

        MsBuildCompileEvaluator.PackageReferenceEvaluationOverride =
            _ => Versions(("X", "1.2.3"));

        var layout = new ProjectPreprocessSourceLayout(_sourceRoot, csproj, new[] { csproj });
        var intermediateRoot = Path.Combine(_tempRoot, "intermediate");

        await ProjectPreprocessSource.CopyProjectFilesAsync(
            layout, _sourceRoot, intermediateRoot, new SupportedProjectFiltering.ExclusionInfo());

        var copied = await File.ReadAllTextAsync(
            Path.Combine(intermediateRoot, "App", "App.csproj"));
        Assert.Contains("Version=\"1.2.3\"", copied);
        Assert.DoesNotContain("$(XVersion)", copied);
    }

    [Fact]
    public async Task CopyProjectFilesAsync_KeepsOriginalWhenEvaluatorReturnsNull()
    {
        var csproj = WriteFile(
            Path.Combine("App", "App.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="X" Version="$(XVersion)" />
              </ItemGroup>
            </Project>
            """);

        MsBuildCompileEvaluator.PackageReferenceEvaluationOverride = _ => null;

        var layout = new ProjectPreprocessSourceLayout(_sourceRoot, csproj, new[] { csproj });
        var intermediateRoot = Path.Combine(_tempRoot, "intermediate");

        await ProjectPreprocessSource.CopyProjectFilesAsync(
            layout, _sourceRoot, intermediateRoot, new SupportedProjectFiltering.ExclusionInfo());

        var copied = await File.ReadAllTextAsync(
            Path.Combine(intermediateRoot, "App", "App.csproj"));
        Assert.Contains("Version=\"$(XVersion)\"", copied);
    }

    [Fact]
    public void CopyAncestorMsBuildDirectoryFiles_PinsVersionsAndDropsMissingImports()
    {
        var subdirectory = Path.Combine(_sourceRoot, "sub");
        Directory.CreateDirectory(subdirectory);
        var project = Path.Combine(subdirectory, "Sub.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        File.WriteAllText(
            Path.Combine(_sourceRoot, "Directory.Build.targets"),
            """
            <Project>
              <Import Project="..\..\Directory.Build.targets" />
              <ItemGroup>
                <PackageReference Include="Build.Tasks" Version="$(BuildTasksVersion)" />
              </ItemGroup>
            </Project>
            """);

        MsBuildCompileEvaluator.PackageReferenceEvaluationOverride =
            _ => Versions(("Build.Tasks", "9.9.9"));

        var intermediateRoot = Path.Combine(_tempRoot, "intermediate");
        Directory.CreateDirectory(intermediateRoot);

        ProjectPreprocessSource.CopyAncestorMsBuildDirectoryFiles(
            subdirectory, intermediateRoot, "[test]", verbose: false, project);

        var copied = File.ReadAllText(Path.Combine(intermediateRoot, "Directory.Build.targets"));
        Assert.Contains("Version=\"9.9.9\"", copied);
        Assert.DoesNotContain("$(BuildTasksVersion)", copied);
        Assert.DoesNotContain("..\\..\\Directory.Build.targets", copied);
    }

    [Fact]
    public void ParsePackageReferenceJson_ResolvesIdentityAndVersion()
    {
        const string json = """
            {
              "Items": {
                "PackageReference": [
                  { "Identity": "System.Buffers", "Version": "4.6.1" },
                  { "Identity": "System.Memory", "Version": "$(SystemMemoryVersion)" },
                  { "Identity": "NoVersion" }
                ]
              }
            }
            """;

        var versions = MsBuildCompileEvaluator.ParsePackageReferenceJson(json);

        Assert.NotNull(versions);
        Assert.Equal("4.6.1", versions!["System.Buffers"]);
        Assert.False(versions.ContainsKey("System.Memory"));
        Assert.False(versions.ContainsKey("NoVersion"));
    }

    [Fact]
    public void IsPreparedProjectOrMsBuildFile_MatchesProjectFilesAndRootMsBuildFilesOnly()
    {
        Assert.True(ProjectPreprocessSource.IsPreparedProjectOrMsBuildFile(
            Path.Combine(_sourceRoot, "sub", "Sub.csproj"), _sourceRoot));
        Assert.True(ProjectPreprocessSource.IsPreparedProjectOrMsBuildFile(
            Path.Combine(_sourceRoot, "Directory.Build.props"), _sourceRoot));
        Assert.False(ProjectPreprocessSource.IsPreparedProjectOrMsBuildFile(
            Path.Combine(_sourceRoot, "sub", "Directory.Build.props"), _sourceRoot));
        Assert.False(ProjectPreprocessSource.IsPreparedProjectOrMsBuildFile(
            Path.Combine(_sourceRoot, "sub", "Local.targets"), _sourceRoot));
    }
}
