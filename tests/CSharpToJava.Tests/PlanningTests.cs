using System.Text.Json;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.Pipeline.Planning;

namespace CSharpToJava.Tests;

public class PlanningTests
{
    [Fact]
    public void MavenPomGenerator_GenerateModuleBuildFile_UsesChildPomShape_ForSingleModulePlan()
    {
        var module = new JavaModulePlan
        {
            ModuleName = "core-module",
            IsTestOnly = false,
        };

        var plan = new JavaWorkspacePlan
        {
            GroupId = "com.example",
            ArtifactId = "demo-parent",
            Version = "1.0-SNAPSHOT",
            JavaVersion = "Java25",
            Modules = [module],
        };

        var generator = new MavenPomGenerator();
        var pom = generator.GenerateModuleBuildFile(plan, module);

        Assert.Contains("<parent>", pom, StringComparison.Ordinal);
        Assert.Contains("<artifactId>demo-parent</artifactId>", pom, StringComparison.Ordinal);
        Assert.Contains("<artifactId>core-module</artifactId>", pom, StringComparison.Ordinal);
        Assert.DoesNotContain("<modules>", pom, StringComparison.Ordinal);
    }

    [Fact]
    public void JavaWorkspacePlanJsonSerializer_SerializesModulesDependenciesAndCompatPacks()
    {
        var plan = new JavaWorkspacePlan
        {
            GroupId = "com.example",
            ArtifactId = "demo-parent",
            Version = "1.0-SNAPSHOT",
            JavaVersion = "Java25",
            Modules =
            [
                new JavaModulePlan
                {
                    ModuleName = "core-module",
                    IsTestOnly = false,
                    Dependencies =
                    [
                        new JavaDependency
                        {
                            GroupId = "io.vavr",
                            ArtifactId = "vavr",
                            Version = "0.10.4",
                        },
                        new JavaDependency
                        {
                            GroupId = "com.example",
                            ArtifactId = "compat",
                            Version = "${project.version}",
                            Scope = JavaDependencyScope.Test,
                            IsInternal = true,
                        }
                    ],
                    RequiredCompatPacks = ["json", "trace"],
                    SourceSets = new JavaSourceSets
                    {
                        TestSources = ["test"],
                    },
                }
            ],
        };

        var serializer = new JavaWorkspacePlanJsonSerializer();
        var json = serializer.Serialize(plan);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("com.example", root.GetProperty("groupId").GetString());
        Assert.Equal("demo-parent", root.GetProperty("artifactId").GetString());

        var modules = root.GetProperty("modules").EnumerateArray().ToList();
        var module = Assert.Single(modules);
        Assert.Equal("core-module", module.GetProperty("moduleName").GetString());

        var compatPacks = module.GetProperty("requiredCompatPacks").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Equal(new[] { "json", "trace" }, compatPacks);

        var dependencies = module.GetProperty("dependencies").EnumerateArray().ToList();
        Assert.Equal(2, dependencies.Count);
        Assert.Equal("test", dependencies[1].GetProperty("scope").GetString());
        Assert.True(dependencies[1].GetProperty("isInternal").GetBoolean());
    }

    [Fact]
    public void CompatibilityPackPlanner_Analyze_ReturnsPackIdsAndExternalDependencies()
    {
        var requirements = CompatibilityPackPlanner.Analyze(
            [
                new ConversionResult
                {
                    Success = true,
                    FileName = "Sample.java",
                    GeneratedCode = "class Sample { JsonSerializerOptions options; Trace.writeLine(\"x\"); }",
                }
            ],
            "com.example.compat");

        Assert.Equal(new[] { "json", "trace" }, requirements.RequiredPackIds);
        var dependency = Assert.Single(requirements.ExternalDependencies);
        Assert.Equal("com.fasterxml.jackson.core", dependency.GroupId);
        Assert.Equal("jackson-databind", dependency.ArtifactId);
    }

    [Fact]
    public void WorkspacePlanBuilder_MergeDependencies_DeduplicatesByGroupArtifactAndScope()
    {
        var merged = WorkspacePlanBuilder.MergeDependencies(
            WorkspacePlanBuilder.DefaultDependencies().Concat(
            [
                WorkspacePlanBuilder.ExternalDependency("com.fasterxml.jackson.core:jackson-databind:2.17.0"),
                WorkspacePlanBuilder.ExternalDependency("org.slf4j:slf4j-api:2.0.17"),
            ]));

        Assert.Single(merged, dependency =>
            dependency.GroupId == "com.fasterxml.jackson.core"
            && dependency.ArtifactId == "jackson-databind"
            && dependency.Scope == JavaDependencyScope.Compile);
        Assert.Contains(merged, dependency => dependency.GroupId == "org.slf4j" && dependency.ArtifactId == "slf4j-api");
    }
}