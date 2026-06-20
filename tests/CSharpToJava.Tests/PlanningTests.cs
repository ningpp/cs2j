using System.Text.Json;
using CSharpToJava.CLI;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.Pipeline.Planning;

namespace CSharpToJava.Tests;

public class PlanningTests
{
    [Fact]
    public void NormalizeModuleName_ReplacesParenthesesWithHyphens()
    {
        // Maven artifactId only allows [a-zA-Z0-9._-]
        // C# project names like "YamlDotNet(net8.0)" must be normalized
        var result = MultiModulePlanner.NormalizeModuleName("YamlDotNet(net8.0)");
        Assert.DoesNotContain("(", result);
        Assert.DoesNotContain(")", result);
        Assert.Matches(@"^[a-z0-9._-]+$", result);
    }

    [Fact]
    public void NormalizeModuleName_ProducesValidMavenArtifactId()
    {
        var result = MultiModulePlanner.NormalizeModuleName("YamlDotNet.Test(net10.0)");
        Assert.DoesNotContain("(", result);
        Assert.DoesNotContain(")", result);
        Assert.Matches(@"^[a-z0-9._-]+$", result);
    }
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
    public void MavenPomGenerator_GenerateRootBuildFile_DoesNotIncludeSpotlessFormattingByDefault()
    {
        var module = new JavaModulePlan
        {
            ModuleName = "demo-module",
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
        var pom = generator.GenerateRootBuildFile(plan, true);

        Assert.DoesNotContain("spotless-maven-plugin", pom, StringComparison.Ordinal);
        Assert.DoesNotContain("googleJavaFormat", pom, StringComparison.Ordinal);
    }

    [Fact]
    public void MavenPomGenerator_GenerateMavenConfig_AddsAlsoMakeForMultiModuleWorkspaces()
    {
        var generator = new MavenPomGenerator();
        var config = generator.GenerateMavenConfig();

        Assert.Contains("--also-make", config, StringComparison.Ordinal);
        Assert.Contains("-am", config, StringComparison.Ordinal);
    }

    [Fact]
    public void MavenPomGenerator_GenerateModuleBuildFile_DeclaresCompilerPluginVersionAndValidJavacArgs()
    {
        var module = new JavaModulePlan
        {
            ModuleName = "demo-module",
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

        Assert.Contains("<artifactId>maven-compiler-plugin</artifactId>", pom, StringComparison.Ordinal);
        Assert.Contains("<version>3.13.0</version>", pom, StringComparison.Ordinal);
        Assert.Contains("<arg>-Xmaxerrs</arg>", pom, StringComparison.Ordinal);
        Assert.DoesNotContain("<maxerrs>", pom, StringComparison.Ordinal);
        Assert.DoesNotContain("<maxwarns>", pom, StringComparison.Ordinal);
    }

    [Fact]
    public void JavaWorkspacePlanJsonSerializer_SerializesModulesDependenciesCompatPacksAndRuntimeBridges()
    {
        var jsonBridge = new JavaRuntimeBridgeRequirement
        {
            BridgeId = "json",
            Description = "System.Text.Json -> Jackson bridge",
            RequiredCompatPacks = ["json"],
            Dependencies =
            [
                new JavaDependency
                {
                    GroupId = "com.fasterxml.jackson.core",
                    ArtifactId = "jackson-databind",
                    Version = "2.17.0",
                }
            ],
        };

        var plan = new JavaWorkspacePlan
        {
            GroupId = "com.example",
            ArtifactId = "demo-parent",
            Version = "1.0-SNAPSHOT",
            JavaVersion = "Java25",
            RequiredRuntimeBridges = [jsonBridge],
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
                    RequiredRuntimeBridges = [jsonBridge],
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

        var runtimeBridges = root.GetProperty("requiredRuntimeBridges").EnumerateArray().ToList();
        var runtimeBridge = Assert.Single(runtimeBridges);
        Assert.Equal("json", runtimeBridge.GetProperty("bridgeId").GetString());

        var compatPacks = module.GetProperty("requiredCompatPacks").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Equal(new[] { "json", "trace" }, compatPacks);

        var moduleRuntimeBridges = module.GetProperty("requiredRuntimeBridges").EnumerateArray().ToList();
        Assert.Equal("json", Assert.Single(moduleRuntimeBridges).GetProperty("bridgeId").GetString());

        var dependencies = module.GetProperty("dependencies").EnumerateArray().ToList();
        Assert.Equal(2, dependencies.Count);
        Assert.Equal("test", dependencies[1].GetProperty("scope").GetString());
        Assert.True(dependencies[1].GetProperty("isInternal").GetBoolean());
    }

    [Fact]
    public void CompatibilityPackPlanner_Analyze_ReturnsPackIdsRuntimeBridgesAndExternalDependencies()
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

        var jsonBridge = Assert.Single(requirements.RuntimeBridges, bridge => bridge.BridgeId == "json");
        Assert.Equal("System.Text.Json → Jackson bridge", jsonBridge.Description);
        Assert.Equal(new[] { "json" }, jsonBridge.RequiredCompatPacks);
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
    public void WorkspacePlanBuilder_DefaultDependenciesForModule_OmitsCurrentModule()
    {
        var dependencies = WorkspacePlanBuilder.DefaultDependenciesForModule(
            "io.github.ningpp",
            "System.Private.Xml");

        Assert.DoesNotContain(dependencies, dependency =>
            dependency.GroupId == "io.github.ningpp"
            && dependency.ArtifactId == "System.Private.Xml");
        Assert.DoesNotContain(dependencies, dependency =>
            dependency.GroupId == "io.github.ningpp"
            && dependency.ArtifactId == "System.Private.Uri");
    }

    [Fact]
    public void WorkspacePlanBuilder_MergeRuntimeBridges_DeduplicatesBridgeIdsAndDependencies()
    {
        var merged = WorkspacePlanBuilder.MergeRuntimeBridges(
        [
            new JavaRuntimeBridgeRequirement
            {
                BridgeId = "json",
                Description = "System.Text.Json -> Jackson bridge",
                RequiredCompatPacks = ["json"],
                Dependencies =
                [
                    new JavaDependency
                    {
                        GroupId = "com.fasterxml.jackson.core",
                        ArtifactId = "jackson-databind",
                        Version = "2.17.0",
                    }
                ],
            },
            new JavaRuntimeBridgeRequirement
            {
                BridgeId = "json",
                Description = "System.Text.Json -> Jackson bridge",
                RequiredCompatPacks = ["json"],
                Dependencies =
                [
                    new JavaDependency
                    {
                        GroupId = "com.fasterxml.jackson.core",
                        ArtifactId = "jackson-databind",
                        Version = "2.17.0",
                    }
                ],
            },
            new JavaRuntimeBridgeRequirement
            {
                BridgeId = "trace",
                Description = "System.Diagnostics.Trace compatibility",
                RequiredCompatPacks = ["trace"],
            }
        ]);

        Assert.Equal(2, merged.Count);
        Assert.Equal("json", merged[0].BridgeId);
        Assert.Single(merged[0].Dependencies);
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

    [Fact]
    public async Task OutputIncrementalWriteSession_SkipsUnchangedWrites_AndDeletesStaleOutputs()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), "cs2j-output-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputRoot);

        try
        {
            var generatedPath = Path.Combine(outputRoot, "src", "main", "java", "Sample.java");
            var workspacePlanPath = Path.Combine(outputRoot, "cs2j-workspace-plan.json");
            var serializer = new OutputIncrementalManifestJsonSerializer();

            var firstSession = OutputIncrementalWriteSession.Create(outputRoot, @"C:\repos\demo");
            Assert.False(firstSession.HasPreviousManifest);
            Assert.True(firstSession.WriteTextFile(generatedPath, "class Sample {}", OutputIncrementalEntryKind.GeneratedSource));
            Assert.True(firstSession.WriteTextFile(workspacePlanPath, "{\"modules\":[]}", OutputIncrementalEntryKind.WorkspaceManifest));
            await firstSession.SaveAsync();

            var sentinelTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(generatedPath, sentinelTime);

            var secondSession = OutputIncrementalWriteSession.Create(outputRoot, @"C:\repos\demo");
            Assert.True(secondSession.HasPreviousManifest);
            Assert.False(secondSession.WriteTextFile(generatedPath, "class Sample {}", OutputIncrementalEntryKind.GeneratedSource));
            await secondSession.SaveAsync();

            Assert.Equal(sentinelTime, File.GetLastWriteTimeUtc(generatedPath));
            Assert.False(File.Exists(workspacePlanPath));

            var manifestPath = Path.Combine(outputRoot, OutputIncrementalWriteSession.ManifestFileName);
            var manifest = serializer.Deserialize(await File.ReadAllTextAsync(manifestPath));
            var entry = Assert.Single(Assert.IsType<OutputIncrementalManifest>(manifest).Entries);
            Assert.Equal(Path.Combine("src", "main", "java", "Sample.java"), entry.RelativePath);
            Assert.Equal(OutputIncrementalEntryKind.GeneratedSource, entry.Kind);
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task OutputIncrementalWriteSession_CopyFile_RewritesWhenSourceChanges()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "cs2j-output-copy-" + Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.Combine(tempRoot, "source");
        var outputRoot = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(outputRoot);

        try
        {
            var sourceFile = Path.Combine(sourceRoot, "data.json");
            var outputFile = Path.Combine(outputRoot, "resources", "data.json");
            await File.WriteAllTextAsync(sourceFile, "{\"value\":1}");

            var firstSession = OutputIncrementalWriteSession.Create(outputRoot, sourceRoot);
            Assert.True(firstSession.CopyFile(sourceFile, outputFile, OutputIncrementalEntryKind.CopiedResource));
            await firstSession.SaveAsync();

            var sentinelTime = new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(outputFile, sentinelTime);

            var secondSession = OutputIncrementalWriteSession.Create(outputRoot, sourceRoot);
            Assert.False(secondSession.CopyFile(sourceFile, outputFile, OutputIncrementalEntryKind.CopiedResource));
            await secondSession.SaveAsync();
            Assert.Equal(sentinelTime, File.GetLastWriteTimeUtc(outputFile));

            await File.WriteAllTextAsync(sourceFile, "{\"value\":2}");

            var thirdSession = OutputIncrementalWriteSession.Create(outputRoot, sourceRoot);
            Assert.True(thirdSession.CopyFile(sourceFile, outputFile, OutputIncrementalEntryKind.CopiedResource));
            await thirdSession.SaveAsync();

            Assert.Equal("{\"value\":2}", await File.ReadAllTextAsync(outputFile));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InputFingerprintSnapshotBuilder_BuildsStableSnapshotAndRoundTrips()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "cs2j-input-fingerprint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var sourceFile = Path.Combine(tempRoot, "Program.cs");
            var mappingFile = Path.Combine(tempRoot, "TypeMappings.json");
            var templateFile = Path.Combine(tempRoot, ".gitignore");
            var toolFile = Path.Combine(tempRoot, "tool.dll");

            await File.WriteAllTextAsync(sourceFile, "class Program {}\n");
            await File.WriteAllTextAsync(mappingFile, "{}\n");
            await File.WriteAllTextAsync(templateFile, "target/\n");
            await File.WriteAllTextAsync(toolFile, "tool-binary-placeholder\n");

            var snapshot = InputFingerprintSnapshotBuilder.Build(new InputFingerprintBuildRequest
            {
                SourceName = "demo",
                SourcePath = tempRoot,
                InputFilePaths = [sourceFile],
                OptionTokens = ["mode=multi-module", "include-tests=true"],
                TemplateFilePaths = [templateFile],
                ToolAssemblyPaths = [toolFile],
                MappingConfigPath = mappingFile,
            });

            var serializer = new InputFingerprintSnapshotJsonSerializer();
            var roundTripped = serializer.Deserialize(serializer.Serialize(snapshot));

            Assert.True(snapshot.Matches(roundTripped));
            Assert.Contains(snapshot.Entries, entry => entry.Kind == InputFingerprintEntryKind.SourceInput && entry.Path == Path.GetFullPath(sourceFile));
            Assert.Contains(snapshot.Entries, entry => entry.Kind == InputFingerprintEntryKind.MappingConfiguration && entry.Path == Path.GetFullPath(mappingFile));
            Assert.Contains(snapshot.Entries, entry => entry.Kind == InputFingerprintEntryKind.TemplateAsset && entry.Path == Path.GetFullPath(templateFile));
            Assert.Contains(snapshot.Entries, entry => entry.Kind == InputFingerprintEntryKind.ToolAssembly && entry.Path == Path.GetFullPath(toolFile));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InputFingerprintSnapshot_Matches_DetectsOptionChanges()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "cs2j-input-options-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var sourceFile = Path.Combine(tempRoot, "Program.cs");
            await File.WriteAllTextAsync(sourceFile, "class Program {}\n");

            var baseline = InputFingerprintSnapshotBuilder.Build(new InputFingerprintBuildRequest
            {
                SourceName = "demo",
                SourcePath = tempRoot,
                InputFilePaths = [sourceFile],
                OptionTokens = ["mode=multi-module"],
            });

            var changedOptions = InputFingerprintSnapshotBuilder.Build(new InputFingerprintBuildRequest
            {
                SourceName = "demo",
                SourcePath = tempRoot,
                InputFilePaths = [sourceFile],
                OptionTokens = ["mode=single-module"],
            });

            Assert.False(baseline.Matches(changedOptions));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
