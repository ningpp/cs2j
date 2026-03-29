using CSharpToJava.Core.Pipeline.Planning;

namespace CSharpToJava.Tests;

public class WorkspacePlanTests
{
    [Fact]
    public void SingleModule_GeneratesPom_WithCorrectStructure()
    {
        var plan = new WorkspacePlanBuilder()
            .GroupId("com.example")
            .ArtifactId("my-project")
            .Version("1.0-SNAPSHOT")
            .JavaVersion("Java25")
            .AddSingleModule(hasTests: true, dependencies: WorkspacePlanBuilder.DefaultDependencies())
            .Build();

        Assert.Single(plan.Modules);
        Assert.True(plan.IsSingleModule);
        Assert.True(plan.Modules[0].HasTestSources);

        var gen = new MavenPomGenerator();
        var pom = gen.GenerateRootBuildFile(plan);

        Assert.Contains("<artifactId>my-project</artifactId>", pom);
        Assert.Contains("<groupId>com.example</groupId>", pom);
        Assert.Contains("<packaging>jar</packaging>", pom);
        Assert.Contains("<java.version>25</java.version>", pom);
        Assert.Contains("maven-surefire-plugin", pom);
        Assert.Contains("junit-jupiter", pom);
        Assert.Contains("vavr", pom);
        Assert.Contains("jackson-databind", pom);
    }

    [Fact]
    public void SingleModule_NoTests_OmitsSurefireAndJUnit()
    {
        var plan = new WorkspacePlanBuilder()
            .GroupId("com.example")
            .ArtifactId("my-lib")
            .JavaVersion("Java21")
            .AddSingleModule(hasTests: false)
            .Build();

        var gen = new MavenPomGenerator();
        var pom = gen.GenerateRootBuildFile(plan);

        Assert.DoesNotContain("maven-surefire-plugin", pom);
        Assert.DoesNotContain("junit-jupiter", pom);
        Assert.Contains("<java.version>21</java.version>", pom);
    }

    [Fact]
    public void MultiModule_ParentPom_HasModulesSection()
    {
        var plan = new WorkspacePlanBuilder()
            .GroupId("com.msagl")
            .ArtifactId("msagl-parent")
            .Version("2.0.0")
            .JavaVersion("Java25")
            .AddModule(new JavaModulePlan
            {
                ModuleName = "msagl-core",
                IsTestOnly = false,
                Dependencies = WorkspacePlanBuilder.DefaultDependencies(),
            })
            .AddModule(new JavaModulePlan
            {
                ModuleName = "msagl-drawing",
                IsTestOnly = false,
                Dependencies =
                [
                    ..WorkspacePlanBuilder.DefaultDependencies(),
                    WorkspacePlanBuilder.InternalModuleRef("com.msagl", "msagl-core"),
                ],
            })
            .Build();

        Assert.False(plan.IsSingleModule);
        Assert.Equal(2, plan.Modules.Count);

        var gen = new MavenPomGenerator();
        var parentPom = gen.GenerateRootBuildFile(plan);

        Assert.Contains("<packaging>pom</packaging>", parentPom);
        Assert.Contains("<module>msagl-core</module>", parentPom);
        Assert.Contains("<module>msagl-drawing</module>", parentPom);
    }

    [Fact]
    public void ChildModule_HasParentRef_AndInternalDeps()
    {
        var plan = new WorkspacePlanBuilder()
            .GroupId("com.msagl")
            .ArtifactId("msagl-parent")
            .Version("2.0.0")
            .JavaVersion("Java25")
            .AddModule(new JavaModulePlan
            {
                ModuleName = "msagl-core",
                IsTestOnly = false,
                Dependencies = WorkspacePlanBuilder.DefaultDependencies(),
            })
            .AddModule(new JavaModulePlan
            {
                ModuleName = "msagl-drawing",
                IsTestOnly = false,
                Dependencies =
                [
                    ..WorkspacePlanBuilder.DefaultDependencies(),
                    WorkspacePlanBuilder.InternalModuleRef("com.msagl", "msagl-core"),
                ],
            })
            .Build();

        var gen = new MavenPomGenerator();
        var childPom = gen.GenerateModuleBuildFile(plan, plan.Modules[1]);

        Assert.Contains("<parent>", childPom);
        Assert.Contains("<artifactId>msagl-parent</artifactId>", childPom);
        Assert.Contains("<artifactId>msagl-drawing</artifactId>", childPom);
        Assert.Contains("<artifactId>msagl-core</artifactId>", childPom);
        Assert.Contains("${project.version}", childPom);
    }

    [Fact]
    public void ChildModule_WithTestSources_IncludesJUnit()
    {
        var plan = new WorkspacePlanBuilder()
            .GroupId("com.test")
            .ArtifactId("parent")
            .JavaVersion("Java25")
            .AddModule(new JavaModulePlan
            {
                ModuleName = "core",
                IsTestOnly = false,
                SourceSets = new JavaSourceSets { TestSources = ["test"] },
                Dependencies = WorkspacePlanBuilder.DefaultDependencies(),
            })
            .Build();

        var gen = new MavenPomGenerator();
        var pom = gen.GenerateModuleBuildFile(plan, plan.Modules[0]);

        // Single module in multi-module → still uses child path
        Assert.Contains("junit-jupiter", pom);
        Assert.Contains("maven-surefire-plugin", pom);
    }

    [Fact]
    public void TestScope_Dependency_RendersCorrectScope()
    {
        var plan = new WorkspacePlanBuilder()
            .GroupId("com.test")
            .ArtifactId("test-proj")
            .JavaVersion("Java25")
            .AddModule(new JavaModulePlan
            {
                ModuleName = "test-proj",
                IsTestOnly = false,
                Dependencies =
                [
                    ..WorkspacePlanBuilder.DefaultDependencies(),
                    WorkspacePlanBuilder.InternalModuleRef("com.test", "some-dep", JavaDependencyScope.Test),
                ],
            })
            .Build();

        var gen = new MavenPomGenerator();
        var pom = gen.GenerateModuleBuildFile(plan, plan.Modules[0]);

        Assert.Contains("<scope>test</scope>", pom);
    }

    [Fact]
    public void CompatPacks_StoredInModulePlan()
    {
        var module = new JavaModulePlan
        {
            ModuleName = "msagl-core",
            IsTestOnly = false,
            RequiredCompatPacks = ["ref-holder", "dotnet-core", "xml"],
        };

        Assert.Equal(3, module.RequiredCompatPacks.Count);
        Assert.Contains("ref-holder", module.RequiredCompatPacks);
    }

    [Fact]
    public void BuildFileName_IsPomXml()
    {
        var gen = new MavenPomGenerator();
        Assert.Equal("pom.xml", gen.BuildFileName);
    }
}
