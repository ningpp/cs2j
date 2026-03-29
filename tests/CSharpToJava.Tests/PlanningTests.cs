using System.Text.Json;
using CSharpToJava.Core.Context;
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

    [Fact]
    public void PassProfileSnapshotBuilder_AggregatesMetricsAcrossFiles()
    {
        var snapshot = PassProfileSnapshotBuilder.Build(
        [
            new PassProfileEntry
            {
                EntryKind = PassProfileEntryKind.File,
                ModuleName = "core",
                FileName = "A.java",
                Success = true,
                PassMetrics =
                [
                    new Cs2jPassMetric("CheckPass", Cs2jPassStage.Check, TimeSpan.FromMilliseconds(5), 0, 1, 100, 120, RewriteCount: 1),
                    new Cs2jPassMetric("EmitPass", Cs2jPassStage.Emit, TimeSpan.FromMilliseconds(8), 1, 1, 120, 125),
                ]
            },
            new PassProfileEntry
            {
                EntryKind = PassProfileEntryKind.File,
                ModuleName = "core",
                FileName = "B.java",
                Success = false,
                PassMetrics =
                [
                    new Cs2jPassMetric("CheckPass", Cs2jPassStage.Check, TimeSpan.FromMilliseconds(7), 0, 2, 90, 130, RewriteCount: 2),
                ]
            }
        ]);

        Assert.Equal(2, snapshot.Entries.Count);

        var checkAggregate = Assert.Single(snapshot.Aggregates, aggregate => aggregate.Name == "CheckPass");
        Assert.Equal(Cs2jPassStage.Check, checkAggregate.Stage);
        Assert.Equal(PassProfileEntryKind.File, checkAggregate.EntryKind);
        Assert.Equal(2, checkAggregate.EntryCount);
        Assert.Equal(12d, checkAggregate.TotalElapsedMilliseconds, precision: 3);
        Assert.Equal(3, checkAggregate.TotalDiagnosticDelta);
        Assert.Equal(60, checkAggregate.TotalManagedMemoryDelta);
        Assert.Equal(3, checkAggregate.TotalRewriteCount);
    }

    [Fact]
    public void PassProfileEntryBuilder_CreateProjectEntry_DoesNotDuplicateMetricsAcrossResults()
    {
        var entry = PassProfileEntryBuilder.CreateProjectEntry(
            [
                new Cs2jPassMetric("ProjectCheckPass", Cs2jPassStage.Check, TimeSpan.FromMilliseconds(5), 0, 1, 100, 120, RewriteCount: 4)
            ],
            [
                new ConversionResult
                {
                    Success = true,
                    FileName = "A.java",
                },
                new ConversionResult
                {
                    Success = true,
                    FileName = "B.java",
                }
            ],
            projectName: "CoreProject",
            moduleName: "core");

        var snapshot = PassProfileSnapshotBuilder.Build([Assert.IsType<PassProfileEntry>(entry)]);
        var aggregate = Assert.Single(snapshot.Aggregates);

        Assert.Equal(PassProfileEntryKind.Project, aggregate.EntryKind);
        Assert.Equal(1, aggregate.EntryCount);
        Assert.Equal(5d, aggregate.TotalElapsedMilliseconds, precision: 3);
        Assert.Equal(4, aggregate.TotalRewriteCount);
    }

    [Fact]
    public void CanarySummarySnapshotBuilder_AggregatesDiagnosticsAndPassProfile()
    {
        var passProfileSnapshot = PassProfileSnapshotBuilder.Build(
        [
            new PassProfileEntry
            {
                EntryKind = PassProfileEntryKind.Project,
                ModuleName = "core",
                ProjectName = "CoreProject",
                Success = false,
                PassMetrics =
                [
                    new Cs2jPassMetric("ProjectCheckPass", Cs2jPassStage.Check, TimeSpan.FromMilliseconds(5), 0, 2, 100, 120, RewriteCount: 3),
                ]
            },
            new PassProfileEntry
            {
                EntryKind = PassProfileEntryKind.Project,
                ModuleName = "graph",
                ProjectName = "GraphProject",
                Success = true,
                PassMetrics =
                [
                    new Cs2jPassMetric("ProjectCheckPass", Cs2jPassStage.Check, TimeSpan.FromMilliseconds(7), 0, 1, 90, 110, RewriteCount: 1),
                ]
            }
        ]);

        var summary = CanarySummarySnapshotBuilder.Build(
            "MSAGL",
            "C:/repos/msagl",
            [
                new ConversionResult
                {
                    Success = true,
                    FileName = "A.java",
                    Diagnostics =
                    [
                        new DiagnosticMessage(DiagnosticSeverity.Warning, "platform warning", null, "CS2J3102", "platform-boundary")
                    ]
                },
                new ConversionResult
                {
                    Success = false,
                    FileName = "B.java",
                    Diagnostics =
                    [
                        new DiagnosticMessage(DiagnosticSeverity.Error, "interop error", null, "CS2J3201", "native-interop"),
                        new DiagnosticMessage(DiagnosticSeverity.Error, "interop usage", null, "CS2J3203", "native-interop"),
                    ]
                }
            ],
            passProfileSnapshot);

        Assert.Equal("MSAGL", summary.SourceName);
        Assert.Equal("C:/repos/msagl", summary.SourcePath);
        Assert.Equal(2, summary.ModuleCount);
        Assert.Equal(2, summary.ResultCount);
        Assert.Equal(1, summary.SuccessCount);
        Assert.Equal(1, summary.FailureCount);
        Assert.Equal(0, summary.DiagnosticSeverities.InfoCount);
        Assert.Equal(1, summary.DiagnosticSeverities.WarningCount);
        Assert.Equal(2, summary.DiagnosticSeverities.ErrorCount);

        var nativeInterop = Assert.Single(summary.DiagnosticCategories, entry => entry.Category == "native-interop");
        Assert.Equal(2, nativeInterop.Count);
        var platformBoundary = Assert.Single(summary.DiagnosticCategories, entry => entry.Category == "platform-boundary");
        Assert.Equal(1, platformBoundary.Count);

        var interopCode = Assert.Single(summary.DiagnosticCodes, entry => entry.Code == "CS2J3201");
        Assert.Equal(1, interopCode.Count);

        var passAggregate = Assert.Single(summary.PassAggregates);
        Assert.Equal(PassProfileEntryKind.Project, passAggregate.EntryKind);
        Assert.Equal(2, passAggregate.EntryCount);
        Assert.Equal(4, passAggregate.TotalRewriteCount);
    }
}