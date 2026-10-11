using CSharpToJava.CLI;
using Xunit;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// Regression for the convert-project strict gate failing on files of
/// unrelated source trees that merely share the solution's source root
/// (e.g. an on-disk 'json\' repository not referenced by the entry .sln): the
/// whole-tree enumerator compiled those files with the pass' basic metadata
/// set, type resolution failed and spurious residual errors aborted the run.
/// </summary>
public sealed class SourceGraphMembershipFilterTests
{
    private static DiscoveredProject Project(string root, params string[] segments)
    {
        var dir = segments.Aggregate(root, Path.Combine);
        var name = segments.Last();
        return new DiscoveredProject
        {
            Name = name,
            ProjectFilePath = Path.Combine(dir, name + ".csproj"),
            ProjectDirectory = dir,
            Kind = ProjectKind.Production,
            ProjectReferences = Array.Empty<string>(),
            ResourceItems = Array.Empty<ResourceItem>(),
        };
    }

    private static ProjectGraph Graph(params DiscoveredProject[] projects)
        => new()
        {
            RootProjectPath = projects[0].ProjectFilePath,
            ProjectsInTopologicalOrder = projects,
        };

    [Fact]
    public void Without_Graph_All_Files_Remain_In_Scope()
    {
        var files = new[]
        {
            Path.Combine(Path.GetTempPath(), "repo", "a.cs"),
            Path.Combine(Path.GetTempPath(), "repo", "foreign", "b.cs"),
        };
        var layout = new ProjectPreprocessSourceLayout(
            Path.Combine(Path.GetTempPath(), "repo"),
            Path.Combine(Path.GetTempPath(), "repo"),
            Array.Empty<string>());

        var result = ProjectPreprocessSource.FilterToGraphMembers(
            files, null, layout, new SupportedProjectFiltering.ExclusionInfo());

        Assert.Equal(files, result);
    }

    [Fact]
    public void Files_Of_Projects_Not_In_Graph_Are_Dropped()
    {
        var root = Path.Combine(Path.GetTempPath(), "cs2j-graph-filter-1");
        var included = Project(root, "uri", "src");
        var graph = Graph(included);
        var layout = new ProjectPreprocessSourceLayout(
            root, Path.Combine(root, "sdk.sln"),
            new[] { Path.Combine(root, "sdk.sln"), included.ProjectFilePath });

        var files = new[]
        {
            // Included project: sources, nested dirs, its own csproj.
            Path.Combine(included.ProjectDirectory, "GlobalUsings.cs"),
            Path.Combine(included.ProjectDirectory, "System", "Uri.cs"),
            included.ProjectFilePath,
            // Entry solution at the repo root is not under any project dir.
            Path.Combine(root, "sdk.sln"),
            // A completely separate tree on disk that the solution does not
            // reference (the json\ case from the incident).
            Path.Combine(root, "json", "src", "Utf8JsonWriter.cs"),
            Path.Combine(root, "json", "tests", "TestUtilities.cs"),
            Path.Combine(root, "json", "System.Text.Json.sln"),
            // Loose junk at the repository root.
            Path.Combine(root, "notes.txt"),
        };

        var result = ProjectPreprocessSource.FilterToGraphMembers(
            files, graph, layout, new SupportedProjectFiltering.ExclusionInfo());

        Assert.Equal(
        [
            Path.Combine(included.ProjectDirectory, "GlobalUsings.cs"),
            Path.Combine(included.ProjectDirectory, "System", "Uri.cs"),
            included.ProjectFilePath,
            Path.Combine(root, "sdk.sln"),
        ], result.ToArray());
    }

    [Fact]
    public void Files_Under_Excluded_Project_Are_Dropped_Even_When_In_Graph()
    {
        var root = Path.Combine(Path.GetTempPath(), "cs2j-graph-filter-2");
        var kept = Project(root, "uri", "src");
        var excluded = Project(root, "uri", "tests");
        var graph = Graph(kept, excluded);
        var layout = new ProjectPreprocessSourceLayout(
            root, root, new[] { kept.ProjectFilePath, excluded.ProjectFilePath });

        var exclusion = new SupportedProjectFiltering.ExclusionInfo();
        exclusion.ExcludedProjectPaths.Add(excluded.ProjectFilePath);
        exclusion.ExcludedProjectDirectories.Add(excluded.ProjectDirectory);

        var files = new[]
        {
            Path.Combine(kept.ProjectDirectory, "Uri.cs"),
            Path.Combine(excluded.ProjectDirectory, "UriTests.cs"),
        };

        var result = ProjectPreprocessSource.FilterToGraphMembers(files, graph, layout, exclusion);

        Assert.Equal([Path.Combine(kept.ProjectDirectory, "Uri.cs")], result.ToArray());
    }

    [Fact]
    public void Relative_Paths_Are_Normalized_Before_Matching()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cs2j-graph-filter-3"));
        var included = Project(root, "xml-tiny", "src");
        var graph = Graph(included);
        var layout = new ProjectPreprocessSourceLayout(
            root, root, new[] { included.ProjectFilePath });

        var current = Environment.CurrentDirectory;
        try
        {
            Directory.CreateDirectory(root);
            Environment.CurrentDirectory = root;
            var files = new[]
            {
                Path.Combine(".", "xml-tiny", "src", "XmlReader.cs"),
                Path.Combine(".", "json", "src", "JsonReader.cs"),
            };

            var result = ProjectPreprocessSource.FilterToGraphMembers(
                files, graph, layout, new SupportedProjectFiltering.ExclusionInfo());

            Assert.Equal(
                [Path.Combine(included.ProjectDirectory, "XmlReader.cs")],
                result.ToArray());
        }
        finally
        {
            Environment.CurrentDirectory = current;
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
