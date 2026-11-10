using CSharpToJava.CLI;

namespace CSharpToJava.Tests;

/// <summary>
/// xunit/mstest/nunit shipping build props set <c>IsTestProject</c> to true
/// unconditionally. A plain library that references such a package but not
/// <c>Microsoft.NET.Test.Sdk</c> therefore looks runnable to <c>dotnet test</c>,
/// which starts a testhost without the test SDK assets and aborts the whole
/// verification chain (dotnet/runtime's TestUtilities, ModuleCore, XmlCoreTest
/// and XmlReaderLib are such libraries). These tests cover the intermediate-tree
/// normalization that marks them with <c>IsTestProject=false</c>.
/// </summary>
public sealed class ProjectPreprocessSourceNonTestLibraryTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _sourceRoot;

    public ProjectPreprocessSourceNonTestLibraryTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "cs2j-non-test-lib-" + Guid.NewGuid().ToString("N"));
        _sourceRoot = Path.Combine(_tempRoot, "repo");
        Directory.CreateDirectory(_sourceRoot);
    }

    public void Dispose()
    {
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

    private static string Mark(string csprojContent)
        => ProjectPreprocessSource.MarkNonRunnableTestLibraries(csprojContent);

    [Fact]
    public void MarkNonRunnableTestLibraries_MarksXunitLibraryWithoutTestSdk()
    {
        const string content = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="xunit" Version="2.9.3" />
                <Compile Include="..\Shared\Helper.cs" Link="Helper.cs" />
              </ItemGroup>
            </Project>
            """;

        var result = Mark(content);

        Assert.Contains("<IsTestProject>false</IsTestProject>", result);
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", result);
        Assert.Contains("<PackageReference Include=\"xunit\" Version=\"2.9.3\" />", result);
        Assert.Contains("<Compile Include=\"..\\Shared\\Helper.cs\" Link=\"Helper.cs\" />", result);
    }

    [Theory]
    [InlineData("xunit")]
    [InlineData("xunit.v3")]
    [InlineData("MSTest.TestFramework")]
    [InlineData("NUnit")]
    [InlineData("Microsoft.DotNet.XUnitExtensions")]
    public void MarkNonRunnableTestLibraries_MarksEveryTestFrameworkPackageCaseInsensitively(string packageId)
    {
        var content = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="{packageId}" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """;

        var result = Mark(content);

        Assert.Contains("<IsTestProject>false</IsTestProject>", result);
    }

    [Fact]
    public void MarkNonRunnableTestLibraries_LeavesRunnableTestProjectUntouched()
    {
        const string content = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
              </ItemGroup>
            </Project>
            """;

        var result = Mark(content);

        Assert.Equal(content, result);
    }

    [Fact]
    public void MarkNonRunnableTestLibraries_LeavesExplicitIsTestProjectTrueUntouched()
    {
        const string content = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <IsTestProject>true</IsTestProject>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="xunit" Version="2.9.3" />
              </ItemGroup>
            </Project>
            """;

        var result = Mark(content);

        Assert.Equal(content, result);
    }

    [Fact]
    public void MarkNonRunnableTestLibraries_LeavesExplicitIsTestProjectFalseUntouched()
    {
        const string content = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <IsTestProject>false</IsTestProject>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="xunit" Version="2.9.3" />
              </ItemGroup>
            </Project>
            """;

        var result = Mark(content);

        Assert.Equal(content, result);
    }

    [Fact]
    public void MarkNonRunnableTestLibraries_LeavesProjectWithoutTestFrameworkUntouched()
    {
        const string content = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.1" />
              </ItemGroup>
            </Project>
            """;

        var result = Mark(content);

        Assert.Equal(content, result);
    }

    [Fact]
    public void MarkNonRunnableTestLibraries_IsIdempotentAndAddsSingleProperty()
    {
        const string content = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="xunit" Version="2.9.3" />
              </ItemGroup>
            </Project>
            """;

        var once = Mark(content);
        var twice = Mark(once);

        Assert.Equal(once, twice);
        var document = System.Xml.Linq.XDocument.Parse(twice);
        Assert.Equal(
            1,
            document.Descendants().Count(element => string.Equals(
                element.Name.LocalName, "IsTestProject", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void MarkNonRunnableTestLibraries_AddsPropertyGroupToProjectWithoutOne()
    {
        const string content = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="xunit" Version="2.9.3" />
              </ItemGroup>
            </Project>
            """;

        var result = Mark(content);

        var document = System.Xml.Linq.XDocument.Parse(result);
        var propertyGroup = Assert.Single(document.Root!.Elements("PropertyGroup"));
        Assert.Equal("false", propertyGroup.Element("IsTestProject")?.Value);
        Assert.Contains("Include=\"xunit\"", result);
    }

    [Fact]
    public void MarkNonRunnableTestLibraries_HandlesSelfClosingProject()
    {
        const string content = "<Project Sdk=\"Microsoft.NET.Sdk\" />";

        var result = Mark(content);

        Assert.Equal(content, result);
        Assert.NotNull(System.Xml.Linq.XDocument.Parse(result).Root);
    }

    [Fact]
    public void MarkNonRunnableTestLibraries_ReturnsNonProjectOrInvalidContentUnchanged()
    {
        const string notAProject = """
            <ProjectFile>
              <ItemGroup>
                <PackageReference Include="xunit" Version="2.9.3" />
              </ItemGroup>
            </ProjectFile>
            """;
        const string invalid = "<Project><PropertyGroup>";

        Assert.Equal(notAProject, Mark(notAProject));
        Assert.Equal(invalid, Mark(invalid));
    }

    [Fact]
    public async Task CopyProjectFilesAsync_MarksNonRunnableTestLibraryInIntermediateTree()
    {
        var csproj = Path.Combine(_sourceRoot, "TestUtilities", "TestUtilities.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(csproj)!);
        await File.WriteAllTextAsync(
            csproj,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="xunit" Version="2.9.3" />
              </ItemGroup>
            </Project>
            """);

        var layout = new ProjectPreprocessSourceLayout(_sourceRoot, csproj, new[] { csproj });
        var intermediateRoot = Path.Combine(_tempRoot, "intermediate");

        await ProjectPreprocessSource.CopyProjectFilesAsync(
            layout, _sourceRoot, intermediateRoot, new SupportedProjectFiltering.ExclusionInfo());

        var copied = await File.ReadAllTextAsync(
            Path.Combine(intermediateRoot, "TestUtilities", "TestUtilities.csproj"));
        Assert.Contains("<IsTestProject>false</IsTestProject>", copied);
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", copied);
    }
}
