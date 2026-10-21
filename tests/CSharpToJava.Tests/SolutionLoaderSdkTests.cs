using CSharpToJava.Core.Workspace;

namespace CSharpToJava.Tests;

public class SolutionLoaderSdkTests
{
    [Theory]
    [InlineData("10.0.300", 10, 0, 300)]
    [InlineData("2.1.818", 2, 1, 818)]
    [InlineData("8.0.100-preview.1", 8, 0, 100)]
    [InlineData("9.0.100-rc.2", 9, 0, 100)]
    [InlineData("not-a-version", null, null, null)]
    public void TryParseSdkVersion_ExtractsLeadingNumericVersion(
        string directoryName,
        int? expectedMajor,
        int? expectedMinor,
        int? expectedBuild)
    {
        var version = SolutionLoader.TryParseSdkVersion(directoryName);

        if (expectedMajor == null)
        {
            Assert.Null(version);
            return;
        }

        Assert.NotNull(version);
        Assert.Equal(expectedMajor.Value, version.Major);
        Assert.Equal(expectedMinor.GetValueOrDefault(), version.Minor);
        Assert.Equal(expectedBuild.GetValueOrDefault(), version.Build);
    }

    [Fact]
    public void TryParseSdkVersion_SortByVersion_PicksLatestSdkOverStringOrdering()
    {
        var directoryNames = new[]
        {
            "2.1.818",
            "10.0.300",
            "8.0.100",
        };

        var latest = directoryNames
            .Select(d => new { Name = d, Version = SolutionLoader.TryParseSdkVersion(d) })
            .Where(x => x.Version != null)
            .OrderByDescending(x => x.Version)
            .First();

        Assert.Equal("10.0.300", latest.Name);
    }

    [Fact]
    public void ScanProjectResources_EmbeddedResourceWithLogicalName_UsesLogicalName()
    {
        using var temp = new TempDir();
        var projectDir = temp.Path;
        var dataDir = Path.Combine(projectDir, "TestData");
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, "dummy.xml"), "<root/>");

        var csproj = Path.Combine(projectDir, "Test.csproj");
        File.WriteAllText(csproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <EmbeddedResource Include="TestData\dummy.xml">
                  <LogicalName>System.Xml.XPath.Tests.TestData.dummy.xml</LogicalName>
                </EmbeddedResource>
              </ItemGroup>
            </Project>
            """);

        var resources = SolutionLoader.ScanProjectResources(csproj);

        Assert.Single(resources);
        Assert.EndsWith("TestData\\dummy.xml", resources[0].SourcePath);
        Assert.Equal("System.Xml.XPath.Tests.TestData.dummy.xml", resources[0].RelativePath);
    }

    [Fact]
    public void ScanProjectResources_EmbeddedResourceWildcardWithMetadata_ExpandsLogicalName()
    {
        using var temp = new TempDir();
        var projectDir = temp.Path;
        var dataDir = Path.Combine(projectDir, "TestData");
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, "dummy.xml"), "<root/>");
        File.WriteAllText(Path.Combine(dataDir, "xp004.xml"), "<root/>");

        var csproj = Path.Combine(projectDir, "Test.csproj");
        File.WriteAllText(csproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <EmbeddedResource Include="TestData\*.xml">
                  <LogicalName>System.Xml.XPath.Tests.TestData.%(Filename)%(Extension)</LogicalName>
                </EmbeddedResource>
              </ItemGroup>
            </Project>
            """);

        var resources = SolutionLoader.ScanProjectResources(csproj).OrderBy(r => r.RelativePath).ToList();

        Assert.Equal(2, resources.Count);
        Assert.Equal("System.Xml.XPath.Tests.TestData.dummy.xml", resources[0].RelativePath);
        Assert.Equal("System.Xml.XPath.Tests.TestData.xp004.xml", resources[1].RelativePath);
    }

    [Fact]
    public void ScanProjectResources_EmbeddedResourceWithoutLogicalName_UsesIncludePath()
    {
        using var temp = new TempDir();
        var projectDir = temp.Path;
        var dataDir = Path.Combine(projectDir, "TestData");
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, "sample.xml"), "<root/>");

        var csproj = Path.Combine(projectDir, "Test.csproj");
        File.WriteAllText(csproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <EmbeddedResource Include="TestData\sample.xml" />
              </ItemGroup>
            </Project>
            """);

        var resources = SolutionLoader.ScanProjectResources(csproj);

        Assert.Single(resources);
        Assert.Equal(Path.Combine("TestData", "sample.xml"), resources[0].RelativePath);
    }

    /// <summary>
    /// Directory sources whose .csproj files live in immediate subdirectories
    /// (e.g. &lt;root&gt;\src\Lib.csproj + &lt;root&gt;\tests\App.csproj) must resolve to the
    /// REFERENCING root project so MSBuildWorkspace loads the whole closure.
    /// </summary>
    [Fact]
    public void TryResolveNestedProjectEntry_PrefersReferencingRootProject()
    {
        using var temp = new TempDir();
        var root = temp.Path;
        WriteProject(Path.Combine(root, "src", "Lib.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        WriteProject(Path.Combine(root, "tests", "App.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\src\Lib.csproj" />
              </ItemGroup>
            </Project>
            """);

        Assert.True(SolutionLoader.TryResolveNestedProjectEntry(root, out var entry));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(root, "tests", "App.csproj")),
            Path.GetFullPath(entry));
    }

    [Fact]
    public void TryResolveNestedProjectEntry_SingleNestedProject_ReturnsIt()
    {
        using var temp = new TempDir();
        var root = temp.Path;
        var lib = Path.Combine(root, "src", "Lib.csproj");
        WriteProject(lib, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);

        Assert.True(SolutionLoader.TryResolveNestedProjectEntry(root, out var entry));
        Assert.Equal(Path.GetFullPath(lib), Path.GetFullPath(entry));
    }

    [Fact]
    public void TryResolveNestedProjectEntry_NoNestedProjects_ReturnsFalse()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "src"));
        Directory.CreateDirectory(Path.Combine(temp.Path, "docs"));

        Assert.False(SolutionLoader.TryResolveNestedProjectEntry(temp.Path, out _));
    }

    [Fact]
    public void TryResolveNestedProjectEntry_DisconnectedRoots_ReturnsFalse()
    {
        using var temp = new TempDir();
        var root = temp.Path;
        WriteProject(Path.Combine(root, "src", "Lib.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        WriteProject(Path.Combine(root, "tools", "Tool.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);

        // Two unrelated entry candidates: opening one would silently drop the
        // other module, so discovery must decline and let the caller fall back.
        Assert.False(SolutionLoader.TryResolveNestedProjectEntry(root, out _));
    }

    /// <summary>
    /// Preprocessed source trees copy the entry solution to the location it had in
    /// the original repository (e.g. &lt;stage&gt;\json\System.Text.Json.sln), so the
    /// solution is nested one level below the stage root. A single nested solution
    /// is unambiguous and must be picked up instead of falling back to the flat
    /// single-module directory scan.
    /// </summary>
    [Fact]
    public void TryResolveNestedSolutionEntry_SingleNestedSolution_ReturnsIt()
    {
        using var temp = new TempDir();
        var root = temp.Path;
        var sln = Path.Combine(root, "json", "System.Text.Json.sln");
        Directory.CreateDirectory(Path.GetDirectoryName(sln)!);
        File.WriteAllText(sln, string.Empty);
        Directory.CreateDirectory(Path.Combine(root, "uri"));

        Assert.True(SolutionLoader.TryResolveNestedSolutionEntry(root, out var entry));
        Assert.Equal(Path.GetFullPath(sln), Path.GetFullPath(entry));
    }

    [Fact]
    public void TryResolveNestedSolutionEntry_MultipleNestedSolutions_ReturnsFalse()
    {
        using var temp = new TempDir();
        var root = temp.Path;
        Directory.CreateDirectory(Path.Combine(root, "json"));
        Directory.CreateDirectory(Path.Combine(root, "xml"));
        File.WriteAllText(Path.Combine(root, "json", "System.Text.Json.sln"), string.Empty);
        File.WriteAllText(Path.Combine(root, "xml", "csharpxml.sln"), string.Empty);

        // Two unrelated entry candidates: opening one would silently drop the other
        // module, so discovery must decline and let the caller fall back.
        Assert.False(SolutionLoader.TryResolveNestedSolutionEntry(root, out _));
    }

    [Fact]
    public void TryResolveNestedSolutionEntry_NoNestedSolution_ReturnsFalse()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "json", "src"));

        Assert.False(SolutionLoader.TryResolveNestedSolutionEntry(temp.Path, out _));
    }

    [Fact]
    public void TryResolveNestedSolutionEntry_PrefersTopLevelSolution()
    {
        using var temp = new TempDir();
        var root = temp.Path;
        var topLevel = Path.Combine(root, "csharp-sdk.sln");
        File.WriteAllText(topLevel, string.Empty);
        Directory.CreateDirectory(Path.Combine(root, "json"));
        File.WriteAllText(Path.Combine(root, "json", "System.Text.Json.sln"), string.Empty);

        Assert.True(SolutionLoader.TryResolveNestedSolutionEntry(root, out var entry));
        Assert.Equal(Path.GetFullPath(topLevel), Path.GetFullPath(entry));
    }

    /// <summary>
    /// .slnx is XML with &lt;Project Path="..."/&gt; entries nested inside &lt;Folder&gt;
    /// nodes. Paths are relative to the solution directory; both separators must work.
    /// </summary>
    [Fact]
    public void ReadSlnxProjectPaths_ResolvesNestedProjectEntries()
    {
        using var temp = new TempDir();
        var root = temp.Path;
        var lib = Path.Combine(root, "src", "Lib.csproj");
        var app = Path.Combine(root, "tests", "Unit", "App.csproj");
        WriteProject(lib, """<Project Sdk="Microsoft.NET.Sdk" />""");
        WriteProject(app, """<Project Sdk="Microsoft.NET.Sdk" />""");

        var slnx = Path.Combine(root, "Sample.slnx");
        File.WriteAllText(slnx, """
            <Solution>
              <Folder Name="/src/">
                <Project Path="src/Lib.csproj" />
              </Folder>
              <Folder Name="/tests/">
                <Folder Name="/tests/Unit/">
                  <Project Path="tests\Unit\App.csproj" />
                </Folder>
              </Folder>
            </Solution>
            """);

        var paths = SolutionLoader.ReadSlnxProjectPaths(slnx);

        Assert.Equal(2, paths.Count);
        Assert.Equal(Path.GetFullPath(lib), paths[0]);
        Assert.Equal(Path.GetFullPath(app), paths[1]);
    }

    [Fact]
    public void ReadSlnxProjectPaths_IgnoresMissingAndDuplicateEntries()
    {
        using var temp = new TempDir();
        var root = temp.Path;
        var lib = Path.Combine(root, "src", "Lib.csproj");
        WriteProject(lib, """<Project Sdk="Microsoft.NET.Sdk" />""");

        var slnx = Path.Combine(root, "Sample.slnx");
        File.WriteAllText(slnx, """
            <Solution>
              <Project Path="src/Lib.csproj" />
              <Project Path="src/./Lib.csproj" />
              <Project Path="src/Missing.csproj" />
              <Project Path="$(Undefined)/Lib.csproj" />
            </Solution>
            """);

        var paths = SolutionLoader.ReadSlnxProjectPaths(slnx);

        Assert.Single(paths);
        Assert.Equal(Path.GetFullPath(lib), paths[0]);
    }

    [Fact]
    public void TryResolveNestedSolutionEntry_TopLevelSlnx_ReturnsIt()
    {
        using var temp = new TempDir();
        var root = temp.Path;
        var slnx = Path.Combine(root, "uri.slnx");
        File.WriteAllText(slnx, "<Solution />");

        Assert.True(SolutionLoader.TryResolveNestedSolutionEntry(root, out var entry));
        Assert.Equal(Path.GetFullPath(slnx), Path.GetFullPath(entry));
    }

    private static void WriteProject(string projectPath, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
        File.WriteAllText(projectPath, content);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cs2j_" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
