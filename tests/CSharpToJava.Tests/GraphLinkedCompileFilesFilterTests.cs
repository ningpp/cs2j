using CSharpToJava.CLI;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression for CS2001 in the convert-project verification build: shared
/// source pulled into a test project via
/// <c>&lt;Compile Include="..\Common\…" Link="…" /&gt;</c> must be copied to the
/// intermediate tree even when the shared directory (e.g. uri\tests\Common)
/// contains no project of its own and therefore is not "under a project
/// directory" of the entry graph.
/// </summary>
public sealed class GraphLinkedCompileFilesFilterTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _sourceRoot;

    public GraphLinkedCompileFilesFilterTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "cs2j-linked-compile-" + Guid.NewGuid().ToString("N"));
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

    private string WriteFile(params string[] segments)
    {
        var path = Path.Combine(_sourceRoot, Path.Combine(segments));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "// stub");
        return Path.GetFullPath(path);
    }

    private string WriteProject(string relativeDirectory, string projectName, string csprojBody)
    {
        var directory = Path.Combine(_sourceRoot, relativeDirectory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, projectName + ".csproj");
        File.WriteAllText(path, csprojBody);
        return Path.GetFullPath(path);
    }

    private static string SimpleProject(params string[] itemElements)
        => """
           <Project>
             <ItemGroup>
           """ + string.Join(Environment.NewLine, itemElements) + """

             </ItemGroup>
           </Project>
           """;

    private static DiscoveredProject Project(string projectFilePath)
    {
        var directory = Path.GetDirectoryName(projectFilePath)!;
        return new DiscoveredProject
        {
            Name = Path.GetFileNameWithoutExtension(projectFilePath),
            ProjectFilePath = projectFilePath,
            ProjectDirectory = directory,
            Kind = ProjectKind.Production,
            ProjectReferences = Array.Empty<string>(),
            ResourceItems = Array.Empty<ResourceItem>(),
        };
    }

    private static ProjectGraph SingleGraph(DiscoveredProject project)
        => new()
        {
            RootProjectPath = project.ProjectFilePath,
            ProjectsInTopologicalOrder = new[] { project },
        };

    [Fact]
    public void LinkedSharedSource_SiblingDirectoryWithNoProject_IsKept()
    {
        var csproj = WriteProject(
            Path.Combine("uri", "tests", "FunctionalTests"),
            "dotnet-uri-functional-tests",
            SimpleProject(
                "<Compile Include=\"LocalTest.cs\" />",
                "<Compile Include=\"..\\Common\\System\\ThreadCultureChange.cs\" Link=\"Common\\ThreadCultureChange.cs\" />",
                "<Compile Include=\"..\\Common\\Xunit\\AssertExtensions.cs\" Link=\"Common\\AssertExtensions.cs\" />"));

        var localFile = WriteFile("uri", "tests", "FunctionalTests", "LocalTest.cs");
        var shared1 = WriteFile("uri", "tests", "Common", "System", "ThreadCultureChange.cs");
        var shared2 = WriteFile("uri", "tests", "Common", "Xunit", "AssertExtensions.cs");
        // A foreign tree under the common root that the entry graph does not
        // reference must still be dropped.
        var foreign = WriteFile("json", "src", "Utf8JsonWriter.cs");

        var layout = new ProjectPreprocessSourceLayout(
            _sourceRoot, csproj, new[] { csproj });
        var files = new[] { csproj, localFile, shared1, shared2, foreign };

        var result = ProjectPreprocessSource.FilterToGraphMembers(
            files, SingleGraph(Project(csproj)), layout,
            new SupportedProjectFiltering.ExclusionInfo());

        Assert.Contains(shared1, result);
        Assert.Contains(shared2, result);
        Assert.Contains(localFile, result);
        Assert.DoesNotContain(foreign, result);
    }

    [Fact]
    public void LinkedSharedSource_RecursiveGlob_IsKept()
    {
        var csproj = WriteProject(
            Path.Combine("uri", "tests", "UnitTests"),
            "dotnet-uri-unit-tests",
            SimpleProject("<Compile Include=\"..\\Common\\**\\*.cs\" />"));

        var nested = WriteFile("uri", "tests", "Common", "System", "PlatformDetection.cs");
        var foreign = WriteFile("other", "src", "NotLinked.cs");

        var layout = new ProjectPreprocessSourceLayout(
            _sourceRoot, csproj, new[] { csproj });

        var result = ProjectPreprocessSource.FilterToGraphMembers(
            new[] { csproj, nested, foreign }, SingleGraph(Project(csproj)), layout,
            new SupportedProjectFiltering.ExclusionInfo());

        Assert.Contains(nested, result);
        Assert.DoesNotContain(foreign, result);
    }

    [Fact]
    public void LinkedSharedSource_RemovedByCompileRemove_IsDropped()
    {
        var csproj = WriteProject(
            Path.Combine("uri", "tests", "UnitTests"),
            "dotnet-uri-unit-tests",
            SimpleProject(
                "<Compile Include=\"..\\Common\\**\\*.cs\" />",
                "<Compile Remove=\"..\\Common\\System\\Skipped.cs\" />"));

        var kept = WriteFile("uri", "tests", "Common", "System", "PlatformDetection.cs");
        var removed = WriteFile("uri", "tests", "Common", "System", "Skipped.cs");

        var layout = new ProjectPreprocessSourceLayout(
            _sourceRoot, csproj, new[] { csproj });

        var result = ProjectPreprocessSource.FilterToGraphMembers(
            new[] { csproj, kept, removed }, SingleGraph(Project(csproj)), layout,
            new SupportedProjectFiltering.ExclusionInfo());

        Assert.Contains(kept, result);
        Assert.DoesNotContain(removed, result);
    }

    [Fact]
    public void FileCompiledByKeptProject_ButPhysicallyUnderExcludedDirectory_IsKept()
    {
        var keptProjectFile = WriteProject(
            Path.Combine("product", "src"),
            "product",
            SimpleProject("<Compile Include=\"..\\..\\tests\\Legacy\\SharedHarness.cs\" Link=\"SharedHarness.cs\" />"));
        var excludedProjectFile = WriteProject(
            Path.Combine("tests", "Legacy"),
            "legacy-tests",
            SimpleProject("<Compile Include=\"SharedHarness.cs\" />"));
        var linked = WriteFile("tests", "Legacy", "SharedHarness.cs");
        var excludedOnly = WriteFile("tests", "Legacy", "OldTest.cs");

        var graph = new ProjectGraph
        {
            RootProjectPath = keptProjectFile,
            ProjectsInTopologicalOrder = new[]
            {
                Project(keptProjectFile),
                Project(excludedProjectFile),
            },
        };
        var exclusion = new SupportedProjectFiltering.ExclusionInfo();
        exclusion.ExcludedProjectPaths.Add(excludedProjectFile);
        exclusion.ExcludedProjectDirectories.Add(Path.GetDirectoryName(excludedProjectFile)!);

        var layout = new ProjectPreprocessSourceLayout(
            _sourceRoot, keptProjectFile, new[] { keptProjectFile, excludedProjectFile });

        var result = ProjectPreprocessSource.FilterToGraphMembers(
            new[] { keptProjectFile, excludedProjectFile, linked, excludedOnly },
            graph, layout, exclusion);

        Assert.Contains(linked, result);
        Assert.DoesNotContain(excludedOnly, result);
        Assert.DoesNotContain(excludedProjectFile, result);
    }

    [Fact]
    public void WithoutGraph_ExcludedProjectDirectories_AreStillFiltered()
    {
        var excludedDir = Path.Combine(_sourceRoot, "tests", "Legacy");
        Directory.CreateDirectory(excludedDir);
        var excludedProjectFile = Path.Combine(excludedDir, "legacy-tests.csproj");
        File.WriteAllText(excludedProjectFile, "<Project />");
        var keptFile = WriteFile("product", "src", "Product.cs");
        var excludedFile = Path.Combine(excludedDir, "OldTest.cs");
        File.WriteAllText(excludedFile, "// stub");

        var exclusion = new SupportedProjectFiltering.ExclusionInfo();
        exclusion.ExcludedProjectPaths.Add(excludedProjectFile);
        exclusion.ExcludedProjectDirectories.Add(excludedDir);

        var layout = new ProjectPreprocessSourceLayout(
            _sourceRoot, _sourceRoot, Array.Empty<string>());

        var result = ProjectPreprocessSource.FilterToGraphMembers(
            new[] { keptFile, excludedFile }, null, layout, exclusion);

        Assert.Contains(keptFile, result);
        Assert.DoesNotContain(excludedFile, result);
    }

    [Fact]
    public void ComputeCompiledFiles_WithoutDefaultItems_ResolvesOnlyExplicitIncludes()
    {
        var csproj = WriteProject(
            Path.Combine("uri", "tests", "FunctionalTests"),
            "dotnet-uri-functional-tests",
            SimpleProject(
                "<Compile Include=\"LocalTest.cs\" />",
                "<Compile Include=\"..\\Common\\System\\ThreadCultureChange.cs\" Link=\"Common\\ThreadCultureChange.cs\" />"));
        var local = WriteFile("uri", "tests", "FunctionalTests", "LocalTest.cs");
        var loose = WriteFile("uri", "tests", "FunctionalTests", "LooseSdkFile.cs");
        var linked = WriteFile("uri", "tests", "Common", "System", "ThreadCultureChange.cs");

        var compiled = ProjectCompileSet.ComputeCompiledFiles(
            csproj, _sourceRoot, includeDefaultItems: false);

        Assert.NotNull(compiled);
        Assert.Contains(local, compiled!);
        Assert.Contains(linked, compiled!);
        // EnableDefaultCompileItems glob expansion is skipped: a loose .cs in
        // the project directory that no <Compile> names is not reported.
        Assert.DoesNotContain(loose, compiled!);
    }
}
