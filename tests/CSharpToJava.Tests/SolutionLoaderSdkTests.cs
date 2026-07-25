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
