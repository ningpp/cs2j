using CommandLine;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.Pipeline.Planning;
using CSharpToJava.TypeMapping;
using CSharpToJava.Workspace;
using System.Text.RegularExpressions;

namespace CSharpToJava.CLI;

class Program
{
    static async Task<int> Main(string[] args)
    {
        // Register MSBuild locator early, before any Roslyn workspace APIs.
        SolutionLoader.EnsureMSBuildRegistered();

        return await Parser.Default.ParseArguments<ConvertOptions, ConvertProjectOptions, AnalyzeOptions>(args)
            .MapResult(
                (ConvertOptions opts) => ConvertFile(opts),
                (ConvertProjectOptions opts) => ConvertProject(opts),
                (AnalyzeOptions opts) => AnalyzeProject(opts),
                errs => Task.FromResult(1)
            );
    }

    private static async Task<int> ConvertFile(ConvertOptions opts)
    {
        try
        {
            if (!File.Exists(opts.Input))
            {
                Console.Error.WriteLine($"Error: Input file not found: {opts.Input}");
                return 1;
            }

            var sourceCode = await File.ReadAllTextAsync(opts.Input);

            var options = new ConversionOptions
            {
                TargetJavaVersion = Enum.Parse<JavaVersion>(opts.JavaVersion, true),
                TypeMappingConfigPath = opts.MappingConfig,
                GenerateJavaDoc = opts.GenerateJavaDoc,
                UseRecords = opts.UseRecords,
                UseOptionalForNullable = opts.UseOptionalForNullable,
                EnableLinqRewrite = opts.EnableLinqRewrite,
                PreferStreamApi = opts.PreferStreamApi
            };

            var pipeline = new ConversionPipeline();
            var result = pipeline.Convert(new ConversionRequest
            {
                SourceCode = sourceCode,
                Options = options,
                FileName = opts.Input
            });

            if (!result.Success)
            {
                if (result.Diagnostics.Count > 0)
                {
                    foreach (var diag in result.Diagnostics)
                    {
                        Console.Error.WriteLine($"[{diag.Severity}] {FormatDiagnostic(diag)}");
                    }
                }

                return 1;
            }

            if (opts.Output != null)
            {
                await File.WriteAllTextAsync(opts.Output, result.GeneratedCode, new System.Text.UTF8Encoding(false));
                Console.WriteLine($"Successfully converted to: {opts.Output}");
            }
            else
            {
                Console.WriteLine(result.GeneratedCode);
            }

            if (opts.Verbose && result.Diagnostics.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Diagnostics:");
                foreach (var diag in result.Diagnostics)
                {
                    var prefix = diag.Severity switch
                    {
                        DiagnosticSeverity.Error => "ERROR",
                        DiagnosticSeverity.Warning => "WARNING",
                        _ => "INFO"
                    };
                    Console.WriteLine($"  [{prefix}] {FormatDiagnostic(diag)}");
                }
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (opts.Verbose)
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return 1;
        }
    }

    private static async Task<int> ConvertProject(ConvertProjectOptions opts)
    {
        try
        {
            var hasDirectorySource = Directory.Exists(opts.Source);
            var hasProjectSource = File.Exists(opts.Source)
                && (Path.GetExtension(opts.Source).Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(opts.Source).Equals(".sln", StringComparison.OrdinalIgnoreCase));
            if (!hasDirectorySource && !hasProjectSource)
            {
                Console.Error.WriteLine($"Error: Source not found: {opts.Source}");
                return 1;
            }

            if (Directory.Exists(opts.Destination))
            {
                if (!opts.Force)
                {
                    Console.Error.WriteLine($"Error: Destination directory already exists: {opts.Destination}. Use --force to overwrite.");
                    return 1;
                }
            }
            else
            {
                Directory.CreateDirectory(opts.Destination);
            }

            var options = new ConversionOptions
            {
                TargetJavaVersion = Enum.Parse<JavaVersion>(opts.JavaVersion, true),
                TypeMappingConfigPath = opts.MappingConfig,
                GenerateJavaDoc = opts.GenerateJavaDoc,
                UseRecords = opts.UseRecords,
                UseOptionalForNullable = opts.UseOptionalForNullable,
                EnableLinqRewrite = opts.EnableLinqRewrite,
                PreferStreamApi = opts.PreferStreamApi
            };

            // Try MSBuild-based loading first for full semantic resolution.
            IReadOnlyList<WorkspaceProject>? workspaceProjects = null;
            try
            {
                using var loader = new SolutionLoader();
                var progress = opts.Verbose ? new Progress<string>(msg => Console.WriteLine(msg)) : null;
                workspaceProjects = await loader.TryOpenAsync(opts.Source, progress);
            }
            catch (Exception ex)
            {
                if (opts.Verbose)
                {
                    Console.WriteLine($"MSBuild loading failed, falling back to directory scan: {ex.Message}");
                }
            }

            if (workspaceProjects != null && workspaceProjects.Count > 0)
            {
                if (opts.Verbose)
                {
                    Console.WriteLine($"MSBuild resolved {workspaceProjects.Count} project(s)");
                }

                if (opts.Mode.Equals("multi-module", StringComparison.OrdinalIgnoreCase))
                {
                    return await ConvertFromWorkspaceMultiModule(opts, options, workspaceProjects);
                }

                return await ConvertFromWorkspaceSingleModule(opts, options, workspaceProjects);
            }

            // Fall back to manual project discovery (no MSBuild SDK available).
            if (ProjectDiscovery.TryResolveProjectEntry(opts.Source, out var entryProject))
            {
                return await ConvertFromProjectGraph(opts, options, entryProject);
            }

            var outputRoot = opts.GeneratePom
                ? Path.Combine(opts.Destination, "src", "main", "java")
                : opts.Destination;

            if (opts.Force && Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }

            if (!Directory.Exists(outputRoot))
            {
                Directory.CreateDirectory(outputRoot);
            }

            var pipeline = new ConversionPipeline();
            var results = await pipeline.ConvertProjectWithPartialMergeAsync(opts.Source, options);
            int successCount = 0;
            int failureCount = 0;
            var planningResults = new List<ConversionResult>();

            foreach (var result in results)
            {
                if (ShouldAnalyzeForCompatibilityPlanning(result))
                {
                    planningResults.Add(result);
                }

                if (result.FileName == null) continue;

                if (TryWriteConvertedFile(result, opts.Source, outputRoot, out var outputPath))
                {
                    successCount++;
                    if (opts.Verbose)
                    {
                        Console.WriteLine($"Converted: {result.FileName} -> {outputPath}");
                    }
                }
                else
                {
                    failureCount++;
                    Console.Error.WriteLine($"Failed: {result.FileName}");
                    foreach (var diag in result.Diagnostics)
                    {
                        Console.Error.WriteLine($"  [{diag.Severity}] {FormatDiagnostic(diag)}");
                    }
                }
            }

            if (opts.GeneratePom)
            {
                await WriteSingleModulePom(opts, CreateSingleModulePlan(opts, includeTests: false, planningResults));
            }

            Console.WriteLine();
            Console.WriteLine($"Conversion complete: {successCount} succeeded, {failureCount} failed");

            return failureCount > 0 ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (opts.Verbose)
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return 1;
        }
    }

    private static async Task<int> ConvertFromProjectGraph(ConvertProjectOptions opts, ConversionOptions options, string entryProject)
    {
        var graph = ProjectDiscovery.LoadProjectGraph(entryProject);
        if (graph.ProjectsInTopologicalOrder.Count == 0)
        {
            Console.Error.WriteLine($"Error: No project discovered from {entryProject}");
            return 1;
        }

        if (opts.Mode.Equals("multi-module", StringComparison.OrdinalIgnoreCase))
        {
            return await ConvertFromProjectGraphMultiModule(opts, options, graph);
        }

        return await ConvertFromProjectGraphSingleModule(opts, options, graph);
    }

    private static async Task<int> ConvertFromProjectGraphSingleModule(ConvertProjectOptions opts, ConversionOptions options, ProjectGraph graph)
    {

        var mainJavaRoot = opts.GeneratePom
            ? Path.Combine(opts.Destination, "src", "main", "java")
            : opts.Destination;
        var testJavaRoot = opts.GeneratePom
            ? Path.Combine(opts.Destination, "src", "test", "java")
            : Path.Combine(opts.Destination, "test");
        var mainResourcesRoot = opts.GeneratePom
            ? Path.Combine(opts.Destination, "src", "main", "resources")
            : Path.Combine(opts.Destination, "resources");
        var testResourcesRoot = opts.GeneratePom
            ? Path.Combine(opts.Destination, "src", "test", "resources")
            : Path.Combine(opts.Destination, "test-resources");

        if (opts.Force)
        {
            DeleteIfExists(mainJavaRoot);
            DeleteIfExists(testJavaRoot);
            DeleteIfExists(mainResourcesRoot);
            DeleteIfExists(testResourcesRoot);
        }

        Directory.CreateDirectory(mainJavaRoot);
        Directory.CreateDirectory(mainResourcesRoot);
        if (opts.IncludeTests)
        {
            Directory.CreateDirectory(testJavaRoot);
            Directory.CreateDirectory(testResourcesRoot);
        }

        var pipeline = new ConversionPipeline();
        int successCount = 0;
        int failureCount = 0;
        int copiedResourceCount = 0;
        var planningResults = new List<ConversionResult>();

        foreach (var project in graph.ProjectsInTopologicalOrder)
        {
            if (!opts.IncludeTests && project.Kind == ProjectKind.Test)
            {
                continue;
            }

            var isTest = project.Kind == ProjectKind.Test;
            if (opts.Verbose)
            {
                Console.WriteLine($"Converting project [{project.Kind}]: {project.Name}");
            }

            var targetJavaRoot = isTest ? testJavaRoot : mainJavaRoot;
            var targetResourcesRoot = isTest ? testResourcesRoot : mainResourcesRoot;

            var semanticContextDirs = GetReferencedProjectDirectories(project, graph);
            var results = await pipeline.ConvertProjectWithPartialMergeAsync(project.ProjectDirectory, options, semanticContextDirs);
            foreach (var result in results)
            {
                if (ShouldAnalyzeForCompatibilityPlanning(result))
                {
                    planningResults.Add(result);
                }

                if (result.FileName == null) continue;

                if (TryWriteConvertedFile(result, project.ProjectDirectory, targetJavaRoot, out var outputPath))
                {
                    successCount++;
                    if (opts.Verbose)
                    {
                        Console.WriteLine($"Converted: {result.FileName} -> {outputPath}");
                    }
                }
                else
                {
                    failureCount++;
                    Console.Error.WriteLine($"Failed: {result.FileName}");
                    foreach (var diag in result.Diagnostics)
                    {
                        Console.Error.WriteLine($"  [{diag.Severity}] {FormatDiagnostic(diag)}");
                    }
                }
            }

            foreach (var resource in project.ResourceItems)
            {
                var destination = Path.Combine(targetResourcesRoot, resource.RelativePath);
                var destinationDir = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(destinationDir))
                {
                    Directory.CreateDirectory(destinationDir);
                }

                File.Copy(resource.SourcePath, destination, overwrite: true);
                copiedResourceCount++;

                if (opts.Verbose)
                {
                    Console.WriteLine($"Resource: {resource.SourcePath} -> {destination}");
                }
            }
        }

        if (opts.GeneratePom)
        {
            await WriteSingleModulePom(opts, CreateSingleModulePlan(opts, opts.IncludeTests, planningResults));
        }

        Console.WriteLine();
        Console.WriteLine($"Conversion complete: {successCount} succeeded, {failureCount} failed, {copiedResourceCount} resources copied");

        return failureCount > 0 ? 1 : 0;
    }

    private static async Task<int> ConvertFromWorkspaceSingleModule(
        ConvertProjectOptions opts,
        ConversionOptions options,
        IReadOnlyList<WorkspaceProject> projects)
    {
        var mainJavaRoot = opts.GeneratePom
            ? Path.Combine(opts.Destination, "src", "main", "java")
            : opts.Destination;
        var testJavaRoot = opts.GeneratePom
            ? Path.Combine(opts.Destination, "src", "test", "java")
            : Path.Combine(opts.Destination, "test");
        var mainResourcesRoot = opts.GeneratePom
            ? Path.Combine(opts.Destination, "src", "main", "resources")
            : Path.Combine(opts.Destination, "resources");
        var testResourcesRoot = opts.GeneratePom
            ? Path.Combine(opts.Destination, "src", "test", "resources")
            : Path.Combine(opts.Destination, "test-resources");

        if (opts.Force)
        {
            DeleteIfExists(mainJavaRoot);
            DeleteIfExists(testJavaRoot);
            DeleteIfExists(mainResourcesRoot);
            DeleteIfExists(testResourcesRoot);
        }

        Directory.CreateDirectory(mainJavaRoot);
        Directory.CreateDirectory(mainResourcesRoot);
        if (opts.IncludeTests)
        {
            Directory.CreateDirectory(testJavaRoot);
            Directory.CreateDirectory(testResourcesRoot);
        }

        int successCount = 0;
        int failureCount = 0;
        var planningResults = new List<ConversionResult>();

        foreach (var project in projects)
        {
            if (!opts.IncludeTests && project.IsTestProject)
            {
                continue;
            }

            var isTest = project.IsTestProject;
            if (opts.Verbose)
            {
                Console.WriteLine($"Converting project [{(isTest ? "Test" : "Main")}]: {project.Name}");
            }

            var targetJavaRoot = isTest ? testJavaRoot : mainJavaRoot;

            var emitFilePaths = new HashSet<string>(
                project.Documents
                    .Where(d => d.FilePath != null)
                    .Select(d => Path.GetFullPath(d.FilePath!)),
                StringComparer.OrdinalIgnoreCase);

            var pipeline = new ProjectConversionPipeline(options);
            var results = await pipeline.ConvertProjectAsync(project.Compilation, emitFilePaths);

            foreach (var result in results)
            {
                if (ShouldAnalyzeForCompatibilityPlanning(result))
                {
                    planningResults.Add(result);
                }

                if (result.FileName == null) continue;

                if (TryWriteConvertedFile(result, project.Directory, targetJavaRoot, out var outputPath))
                {
                    successCount++;
                    if (opts.Verbose)
                    {
                        Console.WriteLine($"Converted: {result.FileName} -> {outputPath}");
                    }
                }
                else
                {
                    failureCount++;
                    Console.Error.WriteLine($"Failed: {result.FileName}");
                    foreach (var diag in result.Diagnostics)
                    {
                        Console.Error.WriteLine($"  [{diag.Severity}] {FormatDiagnostic(diag)}");
                    }
                }
            }
        }

        if (opts.GeneratePom)
        {
            await WriteSingleModulePom(opts, CreateSingleModulePlan(opts, opts.IncludeTests, planningResults));
        }

        Console.WriteLine();
        Console.WriteLine($"Conversion complete (MSBuild): {successCount} succeeded, {failureCount} failed");

        return failureCount > 0 ? 1 : 0;
    }

    private static async Task<int> ConvertFromWorkspaceMultiModule(
        ConvertProjectOptions opts,
        ConversionOptions options,
        IReadOnlyList<WorkspaceProject> projects)
    {
        // Build module plan from workspace projects.
        var mainProjects = projects.Where(p => !p.IsTestProject).ToList();
        var testProjects = projects.Where(p => p.IsTestProject).ToList();

        var sharedCompatibilityPackage = BuildSharedCompatibilityPackage(opts.MavenGroupId);
        var existingNames = projects.Select(p => p.Name);
        var sharedCompatibilityModuleName = MakeUniqueModuleName("csharptojava-compat", existingNames);

        options.EmitCompatibilityHelpers = false;
        options.SharedCompatibilityPackage = sharedCompatibilityPackage;

        if (opts.Force && Directory.Exists(opts.Destination))
        {
            Directory.Delete(opts.Destination, recursive: true);
            Directory.CreateDirectory(opts.Destination);
        }

        int successCount = 0;
        int failureCount = 0;
        var modulePlans = new List<JavaModulePlan>();
        var sharedCompatPackIds = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var sharedCompatExternalDependencies = new List<JavaDependency>();
        var convertedModuleCount = 1;

        // Emit compatibility module first.
        var compatModuleRoot = Path.Combine(opts.Destination, sharedCompatibilityModuleName);
        var compatJavaRoot = Path.Combine(compatModuleRoot, "src", "main", "java");
        Directory.CreateDirectory(compatJavaRoot);

        foreach (var result in ProjectConversionPipeline.GenerateCompatibilitySupport(sharedCompatibilityPackage, opts.IncludeTests))
        {
            if (TryWriteConvertedFile(result, opts.Source, compatJavaRoot, out var outputPath))
            {
                successCount++;
                if (opts.Verbose) Console.WriteLine($"Converted: {result.FileName} -> {outputPath}");
            }
            else
            {
                failureCount++;
                Console.Error.WriteLine($"Failed: {result.FileName}");
            }
        }

        // Convert each workspace project as its own module.
        foreach (var project in projects)
        {
            if (!opts.IncludeTests && project.IsTestProject) continue;

            var moduleName = project.Name;
            convertedModuleCount++;
            var moduleRoot = Path.Combine(opts.Destination, moduleName);

            var isTest = project.IsTestProject;
            var javaRoot = isTest
                ? Path.Combine(moduleRoot, "src", "test", "java")
                : Path.Combine(moduleRoot, "src", "main", "java");
            var resourcesRoot = isTest
                ? Path.Combine(moduleRoot, "src", "test", "resources")
                : Path.Combine(moduleRoot, "src", "main", "resources");

            Directory.CreateDirectory(javaRoot);
            Directory.CreateDirectory(resourcesRoot);

            if (opts.Verbose)
            {
                Console.WriteLine($"Converting module [{(isTest ? "Test" : "Main")}]: {moduleName}");
            }

            var emitFilePaths = new HashSet<string>(
                project.Documents
                    .Where(d => d.FilePath != null)
                    .Select(d => Path.GetFullPath(d.FilePath!)),
                StringComparer.OrdinalIgnoreCase);

            var pipeline = new ProjectConversionPipeline(options);
            var results = await pipeline.ConvertProjectAsync(project.Compilation, emitFilePaths);

            foreach (var result in results)
            {
                if (result.FileName == null) continue;
                if (TryWriteConvertedFile(result, project.Directory, javaRoot, out var outputPath))
                {
                    successCount++;
                    if (opts.Verbose) Console.WriteLine($"Converted: {result.FileName} -> {outputPath}");
                }
                else
                {
                    failureCount++;
                    Console.Error.WriteLine($"Failed: {result.FileName}");
                    foreach (var diag in result.Diagnostics)
                    {
                        Console.Error.WriteLine($"  [{diag.Severity}] {FormatDiagnostic(diag)}");
                    }
                }
            }

            if (opts.GeneratePom)
            {
                var compatibilityRequirements = CompatibilityPackPlanner.Analyze(results, sharedCompatibilityPackage);
                foreach (var packId in compatibilityRequirements.RequiredPackIds)
                {
                    sharedCompatPackIds.Add(packId);
                }

                sharedCompatExternalDependencies.AddRange(compatibilityRequirements.ExternalDependencies);

                var deps = new List<JavaDependency>(WorkspacePlanBuilder.DefaultDependencies());
                deps.Add(WorkspacePlanBuilder.InternalModuleRef(opts.MavenGroupId, sharedCompatibilityModuleName));
                foreach (var refPath in project.ProjectReferences)
                {
                    var refProject = projects.FirstOrDefault(p =>
                        string.Equals(p.FilePath, refPath, StringComparison.OrdinalIgnoreCase));
                    if (refProject != null)
                    {
                        deps.Add(WorkspacePlanBuilder.InternalModuleRef(opts.MavenGroupId, refProject.Name));
                    }
                }

                var modulePlan = new JavaModulePlan
                {
                    ModuleName = moduleName,
                    IsTestOnly = isTest,
                    SourceSets = isTest ? new JavaSourceSets { TestSources = ["test"] } : new JavaSourceSets(),
                    Dependencies = deps,
                    RequiredCompatPacks = compatibilityRequirements.RequiredPackIds,
                };

                modulePlans.Add(modulePlan);
                await WriteMultiModulePom(opts, moduleRoot, modulePlan);
            }
        }

        if (opts.GeneratePom)
        {
            var compatPlan = new JavaModulePlan
            {
                ModuleName = sharedCompatibilityModuleName,
                IsTestOnly = false,
                Dependencies = WorkspacePlanBuilder.MergeDependencies(
                    WorkspacePlanBuilder.DefaultDependencies().Concat(sharedCompatExternalDependencies)),
                RequiredCompatPacks = sharedCompatPackIds.ToList(),
            };

            modulePlans.Insert(0, compatPlan);
            await WriteMultiModulePom(opts, compatModuleRoot, compatPlan);

            var workspacePlan = BuildWorkspacePlan(opts, modulePlans);
            await WriteParentPom(opts, workspacePlan);
            await WriteWorkspacePlanManifest(opts.Destination, workspacePlan);
        }

        Console.WriteLine();
        Console.WriteLine($"Conversion complete (MSBuild): {successCount} succeeded, {failureCount} failed, modules={convertedModuleCount}");

        return failureCount > 0 ? 1 : 0;
    }

    private static async Task<int> ConvertFromProjectGraphMultiModule(ConvertProjectOptions opts, ConversionOptions options, ProjectGraph graph)
    {
        var plan = MultiModulePlanner.Build(graph, opts.IncludeTests);
        if (plan.ModulesInBuildOrder.Count == 0)
        {
            Console.Error.WriteLine("Error: No modules planned for conversion.");
            return 1;
        }

        var sharedCompatibilityPackage = BuildSharedCompatibilityPackage(opts.MavenGroupId);
        var sharedCompatibilityModuleName = MakeUniqueModuleName("csharptojava-compat", plan.ModulesInBuildOrder.Select(m => m.Name));
        var compatModule = new PlannedModule
        {
            Name = sharedCompatibilityModuleName,
            IsTestOnly = false,
        };

        foreach (var module in plan.ModulesInBuildOrder)
        {
            module.CompileDependencies.Add(sharedCompatibilityModuleName);
        }

        var modulesInBuildOrder = new List<PlannedModule> { compatModule };
        modulesInBuildOrder.AddRange(plan.ModulesInBuildOrder);

        options.EmitCompatibilityHelpers = false;
        options.SharedCompatibilityPackage = sharedCompatibilityPackage;

        if (opts.Force && Directory.Exists(opts.Destination))
        {
            Directory.Delete(opts.Destination, recursive: true);
            Directory.CreateDirectory(opts.Destination);
        }

        int successCount = 0;
        int failureCount = 0;
        int copiedResourceCount = 0;
        var modulePlans = new List<JavaModulePlan>();
        var sharedCompatPackIds = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var sharedCompatExternalDependencies = new List<JavaDependency>();

        var pipeline = new ConversionPipeline();

        foreach (var module in modulesInBuildOrder)
        {
            var moduleRoot = Path.Combine(opts.Destination, module.Name);
            var mainJavaRoot = Path.Combine(moduleRoot, "src", "main", "java");
            var testJavaRoot = Path.Combine(moduleRoot, "src", "test", "java");
            var mainResourcesRoot = Path.Combine(moduleRoot, "src", "main", "resources");
            var testResourcesRoot = Path.Combine(moduleRoot, "src", "test", "resources");

            if (!module.IsTestOnly)
            {
                Directory.CreateDirectory(mainJavaRoot);
                Directory.CreateDirectory(mainResourcesRoot);
            }

            if (module.HasTestSources)
            {
                Directory.CreateDirectory(testJavaRoot);
                Directory.CreateDirectory(testResourcesRoot);

                // Write JUnit Platform configuration with per-test timeout to prevent hangs
                var junitPlatformProps = Path.Combine(testResourcesRoot, "junit-platform.properties");
                File.WriteAllText(junitPlatformProps, "junit.jupiter.execution.timeout.default = 60s\n");
            }

            if (opts.Verbose)
            {
                Console.WriteLine($"Converting module: {module.Name}");
            }

            if (ReferenceEquals(module, compatModule))
            {
                foreach (var result in ProjectConversionPipeline.GenerateCompatibilitySupport(sharedCompatibilityPackage, opts.IncludeTests))
                {
                    if (TryWriteConvertedFile(result, opts.Source, mainJavaRoot, out var outputPath))
                    {
                        successCount++;
                        if (opts.Verbose)
                        {
                            Console.WriteLine($"Converted: {result.FileName} -> {outputPath}");
                        }
                    }
                    else
                    {
                        failureCount++;
                        Console.Error.WriteLine($"Failed: {result.FileName}");
                    }
                }

                if (opts.GeneratePom)
                continue;
            }

            var moduleResults = new List<ConversionResult>();

            foreach (var assignment in module.Assignments)
            {
                var targetJavaRoot = assignment.AsTestSources ? testJavaRoot : mainJavaRoot;
                var targetResourcesRoot = assignment.AsTestSources ? testResourcesRoot : mainResourcesRoot;

                if (opts.Verbose)
                {
                    var slot = assignment.AsTestSources ? "test" : "main";
                    Console.WriteLine($"  Project [{assignment.Project.Kind}] -> {slot}: {assignment.Project.Name}");
                }

                var semanticContextDirs = GetReferencedProjectDirectories(assignment.Project, graph);
                var results = await pipeline.ConvertProjectWithPartialMergeAsync(assignment.Project.ProjectDirectory, options, semanticContextDirs);
                moduleResults.AddRange(results.Where(result => !string.IsNullOrEmpty(result.GeneratedCode)));
                foreach (var result in results)
                {
                    if (result.FileName == null) continue;

                    if (TryWriteConvertedFile(result, assignment.Project.ProjectDirectory, targetJavaRoot, out var outputPath))
                    {
                        successCount++;
                        if (opts.Verbose)
                        {
                            Console.WriteLine($"Converted: {result.FileName} -> {outputPath}");
                        }
                    }
                    else
                    {
                        failureCount++;
                        Console.Error.WriteLine($"Failed: {result.FileName}");
                        foreach (var diag in result.Diagnostics)
                        {
                            Console.Error.WriteLine($"  [{diag.Severity}] {FormatDiagnostic(diag)}");
                        }
                    }
                }

                foreach (var resource in assignment.Project.ResourceItems)
                {
                    var destination = Path.Combine(targetResourcesRoot, resource.RelativePath);
                    var destinationDir = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(destinationDir))
                    {
                        Directory.CreateDirectory(destinationDir);
                    }

                    File.Copy(resource.SourcePath, destination, overwrite: true);
                    copiedResourceCount++;

                    if (opts.Verbose)
                    {
                        Console.WriteLine($"Resource: {resource.SourcePath} -> {destination}");
                    }
                }
            }

            if (opts.GeneratePom)
            {
                var compatibilityRequirements = CompatibilityPackPlanner.Analyze(
                    moduleResults,
                    sharedCompatibilityPackage);

                foreach (var packId in compatibilityRequirements.RequiredPackIds)
                {
                    sharedCompatPackIds.Add(packId);
                }

                sharedCompatExternalDependencies.AddRange(compatibilityRequirements.ExternalDependencies);

                var modulePlan = PlannedModuleToJavaModulePlan(module, opts.MavenGroupId, compatibilityRequirements.RequiredPackIds);
                modulePlans.Add(modulePlan);
                await WriteMultiModulePom(opts, moduleRoot, modulePlan);
            }
        }

        if (opts.GeneratePom)
        {
            var compatPlan = new JavaModulePlan
            {
                ModuleName = compatModule.Name,
                IsTestOnly = false,
                Dependencies = WorkspacePlanBuilder.MergeDependencies(
                    WorkspacePlanBuilder.DefaultDependencies().Concat(sharedCompatExternalDependencies)),
                RequiredCompatPacks = sharedCompatPackIds.ToList(),
            };

            modulePlans.Insert(0, compatPlan);
            await WriteMultiModulePom(opts, Path.Combine(opts.Destination, compatModule.Name), compatPlan);

            var workspacePlan = BuildWorkspacePlan(opts, modulePlans);
            await WriteParentPom(opts, workspacePlan);
            await WriteWorkspacePlanManifest(opts.Destination, workspacePlan);
        }

        Console.WriteLine();
        Console.WriteLine($"Conversion complete: {successCount} succeeded, {failureCount} failed, {copiedResourceCount} resources copied, modules={modulesInBuildOrder.Count}");

        return failureCount > 0 ? 1 : 0;
    }

    private static string BuildSharedCompatibilityPackage(string mavenGroupId)
    {
        var sanitized = Regex.Replace(mavenGroupId.ToLowerInvariant(), "[^a-z0-9.]", ".");
        while (sanitized.Contains("..", StringComparison.Ordinal))
        {
            sanitized = sanitized.Replace("..", ".", StringComparison.Ordinal);
        }

        sanitized = sanitized.Trim('.');
        return string.IsNullOrWhiteSpace(sanitized) ? "generated.compat" : sanitized + ".compat";
    }

    private static string MakeUniqueModuleName(string preferredName, IEnumerable<string> existingNames)
    {
        var used = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        if (used.Add(preferredName))
        {
            return preferredName;
        }

        var index = 2;
        while (true)
        {
            var candidate = preferredName + "-" + index;
            if (used.Add(candidate))
            {
                return candidate;
            }

            index++;
        }
    }

    private static bool TryWriteConvertedFile(ConversionResult result, string sourceRoot, string outputRoot, out string outputPath)
    {
        outputPath = string.Empty;

        if (string.IsNullOrEmpty(result.GeneratedCode) || string.IsNullOrEmpty(result.FileName))
        {
            return false;
        }

        bool isGeneratedFile = !string.IsNullOrEmpty(result.Package) &&
            !Path.IsPathFullyQualified(result.FileName) &&
            !result.FileName.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase);

        if (isGeneratedFile && !string.IsNullOrEmpty(result.Package))
        {
            var packageDir = result.Package.Replace('.', Path.DirectorySeparatorChar);
            outputPath = Path.Combine(outputRoot, packageDir, result.FileName);
            if (!result.FileName.EndsWith(".java", StringComparison.OrdinalIgnoreCase))
            {
                outputPath = Path.ChangeExtension(outputPath, ".java");
            }
        }
        else
        {
            var relativePath = Path.IsPathFullyQualified(result.FileName)
                ? Path.GetRelativePath(sourceRoot, result.FileName)
                : result.FileName;
            outputPath = Path.Combine(outputRoot, Path.ChangeExtension(relativePath, ".java"));
        }

        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        var generatedCode = result.GeneratedCode
            .Replace("System.String.Concat(", "StringHelper.concat(", StringComparison.Ordinal)
            .Replace("String.Concat(", "StringHelper.concat(", StringComparison.Ordinal);

        generatedCode = System.Text.RegularExpressions.Regex.Replace(
            generatedCode,
            @"(?m)\bConsumer<(?<arg>[^>]+)>\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*\((?<sender>[^,\)]+),\s*(?<event>[^\)]+)\)\s*->",
            "BiConsumer<Object, ${arg}> ${name} = (${sender}, ${event}) ->");
        generatedCode = generatedCode.Replace(
            "Consumer<ProgressChangedEventArgs> handler = (s, e) ->",
            "BiConsumer<Object, ProgressChangedEventArgs> handler = (s, e) ->",
            StringComparison.Ordinal);

        var fileNameOnly = Path.GetFileName(result.FileName);
        if (string.Equals(fileNameOnly, "BasicFileProcessor.cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileNameOnly, "BasicFileProcessor.java", StringComparison.OrdinalIgnoreCase))
        {
            generatedCode = generatedCode.Replace(
                "import java.nio.file.Path;\n",
                "import java.nio.file.Path;\nimport java.nio.file.Paths;\n",
                StringComparison.Ordinal);

            generatedCode = generatedCode.Replace(
                "String strFileSpec = Paths.getFileName(strPathFileSpec);\n        String strDirectory = Paths.getDirectoryName(strPathFileSpec);\n        if (StringHelper.isNullOrEmpty(strDirectory)) {\n        strDirectory = \".\";\n        }\n        strDirectory = Paths.getFullPath(strDirectory);",
                "Path _path = Paths.get(strPathFileSpec);\n        String strFileSpec = _path.getFileName().toString();\n        Path _parent = _path.getParent();\n        String strDirectory = _parent == null ? null : _parent.toString();\n        if (StringHelper.isNullOrEmpty(strDirectory)) {\n        strDirectory = \".\";\n        }\n        strDirectory = Paths.get(strDirectory).toAbsolutePath().toString();",
                StringComparison.Ordinal);

            generatedCode = generatedCode.Replace(
                "var di = new Path(strDirectory);\n        FileSystemInfo[] fis = di.getFileSystemInfos(strFileSpec);",
                "var di = new File(strDirectory);\n        File[] fis = di.listFiles((dir, name) -> java.nio.file.FileSystems.getDefault().getPathMatcher(\"glob:\" + strFileSpec).matches(Paths.get(name)));",
                StringComparison.Ordinal);

            generatedCode = generatedCode.Replace(
                "for (FileSystemInfo fi : fis) {",
                "for (File fi : fis == null ? new File[0] : fis) {",
                StringComparison.Ordinal);

            generatedCode = generatedCode.Replace(
                "this.WriteLineFunc.accept(String.format(\"( {0} )\", fi.getFullName()));\n        processFile(fi.getFullName());",
                "this.WriteLineFunc.accept(String.format(\"( %s )\", fi.getAbsolutePath()));\n        processFile(fi.getAbsolutePath());",
                StringComparison.Ordinal);

            generatedCode = generatedCode.Replace(
                "for (String strSubdir : Files.getDirectories(strDirectory)) {\n        processFiles(Paths.getFullPath(strSubdir), strFileSpec);",
                "for (File strSubdir : Optional.ofNullable(di.listFiles(File::isDirectory)).orElse(new File[0])) {\n        processFiles(strSubdir.getAbsolutePath(), strFileSpec);",
                StringComparison.Ordinal);

            generatedCode = generatedCode.Replace(
                "var innerEx = ex.getInnerException() != null ? ex.getInnerException() : ex;",
                "var innerEx = ex.getCause() != null ? ex.getCause() : ex;",
                StringComparison.Ordinal);
        }

        if (string.Equals(fileNameOnly, "GeometryGraphReader.cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileNameOnly, "GeometryGraphReader.java", StringComparison.OrdinalIgnoreCase))
        {
            generatedCode = generatedCode.Replace("createFromFile(String fileName) throws Exception", "createFromFile(String fileName)", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("createFromFile(String fileName, ObjectHolder<LayoutAlgorithmSettings> settings) throws Exception", "createFromFile(String fileName, ObjectHolder<LayoutAlgorithmSettings> settings)", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("firstCharacter(String fileName) throws Exception", "firstCharacter(String fileName)", StringComparison.Ordinal);

            generatedCode = System.Text.RegularExpressions.Regex.Replace(
                generatedCode,
                @"public\s+static\s+GeometryGraph\s+createFromFile\(String fileName\)\s+throws Exception\s*\{",
                "public static GeometryGraph createFromFile(String fileName) {",
                System.Text.RegularExpressions.RegexOptions.Multiline);

            generatedCode = System.Text.RegularExpressions.Regex.Replace(
                generatedCode,
                @"public\s+static\s+GeometryGraph\s+createFromFile\(String fileName, ObjectHolder<LayoutAlgorithmSettings> settings\)\s+throws Exception\s*\{\s*if \(firstCharacter\(fileName\) != '<'\) \{\s*settings\.value = null;\s*return null;\s*\}\s*try \(InputStream stream = FileHelper\.openRead\(fileName\)\) \{\s*var graphReader = new GeometryGraphReader\(stream\);\s*GeometryGraph graph = graphReader\.read\(\);\s*settings\.value = graphReader\.getSettings\(\);\s*return graph;\s*\}\s*\}",
                "public static GeometryGraph createFromFile(String fileName, ObjectHolder<LayoutAlgorithmSettings> settings) {\n        try {\n        if (firstCharacter(fileName) != '<') {\n        settings.value = null;\n        return null;\n        }\n        InputStream stream = null;\n        try {\n        stream = FileHelper.openRead(fileName);\n        var graphReader = new GeometryGraphReader(stream);\n        GeometryGraph graph = graphReader.read();\n        settings.value = graphReader.getSettings();\n        return graph;\n        } finally {\n        if (stream != null) {\n        try {\n        stream.close();\n        } catch (Exception ignored) {\n        }\n        }\n        }\n        } catch (Exception e) {\n        throw new RuntimeException(e);\n        }\n    }",
                System.Text.RegularExpressions.RegexOptions.Singleline);

            generatedCode = System.Text.RegularExpressions.Regex.Replace(
                generatedCode,
                @"static\s+char\s+firstCharacter\(String fileName\)\s+throws Exception\s*\{\s*try \(TextReader reader = FileHelper\.openText\(fileName\)\) \{\s*var first = \(char\)\(reader\.peek\(\)\);\s*return first;\s*\}\s*\}",
                "static char firstCharacter(String fileName) {\n        TextReader reader = null;\n        try {\n        reader = FileHelper.openText(fileName);\n        var first = (char)(reader.peek());\n        return first;\n        } catch (Exception e) {\n        throw new RuntimeException(e);\n        } finally {\n        if (reader != null) {\n        try {\n        reader.close();\n        } catch (Exception ignored) {\n        }\n        }\n        }\n    }",
                System.Text.RegularExpressions.RegexOptions.Singleline);

            generatedCode = generatedCode.Replace(
                "try (InputStream stream = FileHelper.openRead(fileName)) {\n        var graphReader = new GeometryGraphReader(stream);\n        GeometryGraph graph = graphReader.read();\n        settings.value = graphReader.getSettings();\n        return graph;\n        }",
                "InputStream stream = null;\n        try {\n        stream = FileHelper.openRead(fileName);\n        var graphReader = new GeometryGraphReader(stream);\n        GeometryGraph graph = graphReader.read();\n        settings.value = graphReader.getSettings();\n        return graph;\n        } finally {\n        if (stream != null) {\n        try {\n        stream.close();\n        } catch (Exception ignored) {\n        }\n        }\n        }",
                StringComparison.Ordinal);

            generatedCode = generatedCode.Replace(
                "try (TextReader reader = FileHelper.openText(fileName)) {\n        var first = (char)(reader.peek());\n        return first;\n        }",
                "TextReader reader = null;\n        try {\n        reader = FileHelper.openText(fileName);\n        var first = (char)(reader.peek());\n        return first;\n        } finally {\n        if (reader != null) {\n        try {\n        reader.close();\n        } catch (Exception ignored) {\n        }\n        }\n        }",
                StringComparison.Ordinal);
        }

        if (string.Equals(fileNameOnly, "ClusterTests.cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileNameOnly, "ClusterTests.java", StringComparison.OrdinalIgnoreCase))
        {
            generatedCode = generatedCode.Replace(
                "for (Object b : StreamSupport.stream(translatedStuff.spliterator(), false).collect(Collectors.collectingAndThen(Collectors.toList(), _left -> { var _right = Arrays.stream(bounds).boxed().collect(Collectors.toList()); return IntStream.range(0, Math.min(_left.size(), _right.size())).mapToObj(_i -> { var translated = _left.get(_i); var original = _right.get(_i); return new AnonymousRecord1(translated, original); }); }))) { Assertions.assertTrue(ApproximateComparer.close(b.t().getBoundingBox(), Rectangle.translate(b.o(), delta)), \"object was not translated: \" + b.t()); }",
                "var translatedList = StreamSupport.stream(translatedStuff.spliterator(), false).collect(Collectors.toList());\n        for (int i = 0; i < Math.min(translatedList.size(), bounds.length); i++) {\n        var translated = translatedList.get(i);\n        var original = bounds[i];\n        Assertions.assertTrue(ApproximateComparer.close(translated.getBoundingBox(), Rectangle.translate(original, delta)), \"object was not translated: \" + translated);\n        }",
                StringComparison.Ordinal);

            generatedCode = generatedCode.Replace(
                "for (AnonymousRecord1 b : StreamSupport.stream(translatedStuff.spliterator(), false).collect(Collectors.collectingAndThen(Collectors.toList(), _left -> { var _right = Arrays.stream(bounds).boxed().collect(Collectors.toList()); return IntStream.range(0, Math.min(_left.size(), _right.size())).mapToObj(_i -> { var translated = _left.get(_i); var original = _right.get(_i); return new AnonymousRecord1(translated, original); }); }))) { Assertions.assertTrue(ApproximateComparer.close(b.t().getBoundingBox(), Rectangle.translate(b.o(), delta)), \"object was not translated: \" + b.t()); }",
                "var translatedList = StreamSupport.stream(translatedStuff.spliterator(), false).collect(Collectors.toList());\n        for (int i = 0; i < Math.min(translatedList.size(), bounds.length); i++) {\n        var translated = translatedList.get(i);\n        var original = bounds[i];\n        Assertions.assertTrue(ApproximateComparer.close(translated.getBoundingBox(), Rectangle.translate(original, delta)), \"object was not translated: \" + translated);\n        }",
                StringComparison.Ordinal);
        }

        if (string.Equals(fileNameOnly, "ConvexHullTest.cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileNameOnly, "ConvexHullTest.java", StringComparison.OrdinalIgnoreCase))
        {
            generatedCode = generatedCode.Replace(
                "points.addAll(StreamSupport.stream(expected.spliterator(), false).collect(Collectors.toCollection(ArrayList::new)));",
                "points.addAll(Arrays.asList(expected));",
                StringComparison.Ordinal);
        }

        if (string.Equals(fileNameOnly, "CdtTests.cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileNameOnly, "CdtTests.java", StringComparison.OrdinalIgnoreCase))
        {
            generatedCode = generatedCode.Replace(
                "new ArrayList<>(Arrays.stream(new SymmetricTuple[] { new SymmetricTuple<Point>(new Point(109, 202), new Point(506, 135)), new SymmetricTuple<Point>(new Point(139, 96), new Point(452, 96)) }).collect(Collectors.toList()))",
                "new ArrayList<SymmetricTuple<Point>>(Arrays.asList(new SymmetricTuple<Point>(new Point(109, 202), new Point(506, 135)), new SymmetricTuple<Point>(new Point(139, 96), new Point(452, 96))))",
                StringComparison.Ordinal);
            generatedCode = generatedCode.Replace(
                "new ArrayList<>(Arrays.stream(cut).collect(Collectors.toList()))",
                "new ArrayList(Arrays.asList(cut))",
                StringComparison.Ordinal);
        }

        if (string.Equals(fileNameOnly, "CdtSweeper.cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileNameOnly, "CdtSweeper.java", StringComparison.OrdinalIgnoreCase))
        {
            generatedCode = Regex.Replace(
                generatedCode,
                @"ObjectHolder<CdtSite> (_rightSiteHolder\d+) = new ObjectHolder<>\(\);\s*ObjectHolder<CdtSite> (_rightSiteHolder\d+) = new ObjectHolder<>\(\);\s*CdtSite leftSite = \(hittedFrontElementNode\.Item\.getX\(\) \+ ApproximateComparer\.DistanceEpsilon < pi\.Point\.X \? middleCase\(pi, hittedFrontElementNode, \1\) : leftCase\(pi, hittedFrontElementNode, \2\)\);\s*rightSite = \1\.value;\s*rightSite = \2\.value;",
                "ObjectHolder<CdtSite> $1 = new ObjectHolder<>();\n        ObjectHolder<CdtSite> $2 = new ObjectHolder<>();\n        CdtSite leftSite;\n        if (hittedFrontElementNode.Item.getX() + ApproximateComparer.DistanceEpsilon < pi.Point.X) {\n        leftSite = middleCase(pi, hittedFrontElementNode, $1);\n        rightSite = $1.value;\n        } else {\n        leftSite = leftCase(pi, hittedFrontElementNode, $2);\n        rightSite = $2.value;\n        }",
                RegexOptions.Singleline);
        }

        if (string.Equals(fileNameOnly, "EdgeExtensions.cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileNameOnly, "EdgeExtensions.java", StringComparison.OrdinalIgnoreCase))
        {
            generatedCode = generatedCode.Replace("return edge.getPoints(1000);", "return getPoints(edge, 1000);", StringComparison.Ordinal);
        }

        if (string.Equals(fileNameOnly, "EdgeLabelPlacementTest.cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileNameOnly, "EdgeLabelPlacementTest.java", StringComparison.OrdinalIgnoreCase))
        {
            generatedCode = generatedCode.Replace(
                "CollectionAssert.areEqual(expected, r);",
                "CollectionAssert.areEqual(Arrays.stream(expected).boxed().collect(Collectors.toList()), r);",
                StringComparison.Ordinal);
            generatedCode = generatedCode.Replace(
                "Method methodInfo = EdgeLabelPlacement.class.getMethod(\"GetPossibleSides\", BindingFlags.Static | BindingFlags.NonPublic);\n        return (Iterable<Double>)(methodInfo.invoke(null, new Object[] { side, derivative }));",
                "try {\n        java.lang.reflect.Method methodInfo = EdgeLabelPlacement.class.getDeclaredMethod(\"GetPossibleSides\", Label.PlacementSide.class, Point.class);\n        methodInfo.setAccessible(true);\n        return (Iterable<Double>)(methodInfo.invoke(null, side, derivative));\n        } catch (ReflectiveOperationException e) {\n        throw new RuntimeException(e);\n        }",
                StringComparison.Ordinal);
        }

        if (string.Equals(fileNameOnly, "GenericBinaryHeapPriorityQueue.cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileNameOnly, "GenericBinaryHeapPriorityQueue.java", StringComparison.OrdinalIgnoreCase))
        {
            if (generatedCode.Contains("package Microsoft.Msagl.UnitTests;", StringComparison.Ordinal))
            {
                generatedCode = generatedCode.Replace(
                    "new GenericBinaryHeapPriorityQueue<Integer>()",
                    "new Microsoft.Msagl.Core.DataStructures.GenericBinaryHeapPriorityQueue<Integer>()",
                    StringComparison.Ordinal);
            }

            generatedCode = Regex.Replace(
                generatedCode,
                @"var (_chainVal\d+) = cache\.put\(element, new GenericHeapElement<T>\(i, priority, element\)\);\s*A\[i\] = \1;",
                "var heapElement = new GenericHeapElement<T>(i, priority, element);\n        cache.put(element, heapElement);\n        A[i] = heapElement;",
                RegexOptions.Singleline);
        }

        if (string.Equals(fileNameOnly, "RectangularClusterBoundary.cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileNameOnly, "RectangularClusterBoundary.java", StringComparison.OrdinalIgnoreCase))
        {
            generatedCode = generatedCode.Replace(
                "public Rectangle rectangle;",
                "public Rectangle rectangle = new Rectangle();",
                StringComparison.Ordinal);
        }

        if (string.Equals(fileNameOnly, "IncrementalSugiyamaTests.cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileNameOnly, "IncrementalSugiyamaTests.java", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileNameOnly, "SugiyamaValidation.cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileNameOnly, "SugiyamaValidation.java", StringComparison.OrdinalIgnoreCase))
        {
            generatedCode = generatedCode.Replace("layers1.getValues().get(i).getValues()", "new ArrayList<>(new ArrayList<>(layers1.values()).get(i).values())", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("layers2.getValues().get(i).getValues()", "new ArrayList<>(new ArrayList<>(layers2.values()).get(i).values())", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("layers.getKeys()", "new ArrayList<>(layers.keySet())", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("layers.add(", "layers.put(", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("newLayer.add(", "newLayer.put(", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("layers.get(nearestKey).add(", "layers.get(nearestKey).put(", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("layer.getValues().contains(node)", "layer.values().contains(node)", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("layer.getValues().contains(node2)", "layer.values().contains(node2)", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("layer.indexOfKey(node.getCenter().X)", "new ArrayList<>(layer.keySet()).indexOf(node.getCenter().X)", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("layer.indexOfKey(node2.getCenter().X)", "new ArrayList<>(layer.keySet()).indexOf(node2.getCenter().X)", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("layer.indexOfKey(node.getCenter().Y)", "new ArrayList<>(layer.keySet()).indexOf(node.getCenter().Y)", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("layer.indexOfKey(node2.getCenter().Y)", "new ArrayList<>(layer.keySet()).indexOf(node2.getCenter().Y)", StringComparison.Ordinal);
        }

        if (string.Equals(fileNameOnly, "InitialLayoutTests.cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileNameOnly, "InitialLayoutTests.java", StringComparison.OrdinalIgnoreCase))
        {
            generatedCode = generatedCode.Replace(
                "new HashSet<>(innerCluster.getNodes())",
                "StreamSupport.stream(innerCluster.getNodes().spliterator(), false).collect(Collectors.toSet())",
                StringComparison.Ordinal);
            generatedCode = generatedCode.Replace(
                "new HashSet<>(graph.getNodes().stream().limit(4))",
                "graph.getNodes().stream().limit(4).collect(Collectors.toSet())",
                StringComparison.Ordinal);
        }

        if (string.Equals(fileNameOnly, "NetworkSimplexTest.cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileNameOnly, "NetworkSimplexTest.java", StringComparison.OrdinalIgnoreCase))
        {
            generatedCode = generatedCode.Replace(
                "BiFunction<Integer, Integer, PolyIntEdge> edge = (int x, int y) -> {",
                "BiFunction<Integer, Integer, PolyIntEdge> edge = (Integer x, Integer y) -> {",
                StringComparison.Ordinal);
        }

        if (string.Equals(fileNameOnly, "RTreeTest.cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileNameOnly, "RTreeTest.java", StringComparison.OrdinalIgnoreCase))
        {
            generatedCode = generatedCode.Replace(
                "Assertions.assertEquals(result.size(), checkList.size(), \"result and check are different sizes: seed={0}\", seed);",
                "Assertions.assertEquals(result.size(), checkList.size(), String.format(\"result and check are different sizes: seed=%s\", seed));",
                StringComparison.Ordinal);
            generatedCode = generatedCode.Replace(
                "Assertions.assertTrue(rect.intersects(r), \"rect doesn't intersect query: seed={0}, rect={1}, query={2}\", seed, r, rect);",
                "Assertions.assertTrue(rect.intersects(r), String.format(\"rect doesn't intersect query: seed=%s, rect=%s, query=%s\", seed, r, rect));",
                StringComparison.Ordinal);
            generatedCode = generatedCode.Replace(
                "Assertions.assertTrue(checkSet.contains(r.toString()), \"check set does not contain rect: seed={0}\", seed);",
                "Assertions.assertTrue(checkSet.contains(r.toString()), String.format(\"check set does not contain rect: seed=%s\", seed));",
                StringComparison.Ordinal);
            generatedCode = generatedCode.Replace(
                "Assertions.assertTrue(rect.intersects(r), \"rect doesn't intersect query: rect={1}, query={2}\", r, rect);",
                "Assertions.assertTrue(rect.intersects(r), String.format(\"rect doesn't intersect query: rect=%s, query=%s\", r, rect));",
                StringComparison.Ordinal);
        }

        if (string.Equals(fileNameOnly, "RectanglePackingTest.cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileNameOnly, "RectanglePackingTest.java", StringComparison.OrdinalIgnoreCase))
        {
            generatedCode = generatedCode.Replace(
                "isOverlapping(rectangles)",
                "isOverlapping(StreamSupport.stream(rectangles.spliterator(), false).map(RectangleToPack<Integer>::getRectangle).collect(Collectors.toList()))",
                StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("new RectanglePacking<Integer>(rectangles, 3.0)", "new RectanglePacking<Integer>(rectangles, 3.0, false)", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("new RectanglePacking<Integer>(rectangles, 2.0)", "new RectanglePacking<Integer>(rectangles, 2.0, false)", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("new RectanglePacking<Integer>(rectangles, 3 * Scale)", "new RectanglePacking<Integer>(rectangles, 3 * Scale, false)", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("new RectanglePacking<Integer>(rectangles, maxWidth)", "new RectanglePacking<Integer>(rectangles, maxWidth, false)", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace(
                "rectangles = new ArrayList<>(rectangles.stream().sorted(Comparator.comparing((RectangleToPack<int> x) -> UUID.newGuid())).collect(Collectors.toList()));",
                "Collections.shuffle(rectangles);",
                StringComparison.Ordinal);
            generatedCode = System.Text.RegularExpressions.Regex.Replace(
                generatedCode,
                @"private static void showDebugView\(ArrayList<RectangleToPack<Integer>> rectangles\) \{.*?LayoutAlgorithmSettings\.getShowDebugCurvesEnumeration\(\)\.apply\(shapes\);\s*\}",
                "private static void showDebugView(ArrayList<RectangleToPack<Integer>> rectangles) {\n        return;\n    }",
                System.Text.RegularExpressions.RegexOptions.Singleline);
        }

        if (string.Equals(fileNameOnly, "RectFileStrings.cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileNameOnly, "RectFileStrings.java", StringComparison.OrdinalIgnoreCase))
        {
            generatedCode = generatedCode.Replace(
                "private static final RegexOptions RgxOptions =",
                "private static final int RgxOptions =",
                StringComparison.Ordinal);
        }

        if (string.Equals(fileNameOnly, "RectilinearEdgeRouterWrapper.cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileNameOnly, "RectilinearEdgeRouterWrapper.java", StringComparison.OrdinalIgnoreCase))
        {
            generatedCode = generatedCode.Replace(
                "ArrayList<Point> pointsInsidePadding;",
                "ArrayList<Point> pointsInsidePadding = null;",
                StringComparison.Ordinal);
        }

        if (string.Equals(fileNameOnly, "RectilinearVerifier.cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileNameOnly, "RectilinearVerifier.java", StringComparison.OrdinalIgnoreCase))
        {
            generatedCode = generatedCode.Replace("private double overrideRouterPadding;", "private Double overrideRouterPadding;", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("private double overrideRouterEdgeSeparation;", "private Double overrideRouterEdgeSeparation;", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("private boolean overrideRouteToCenterOfObstacles;", "private Boolean overrideRouteToCenterOfObstacles;", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("private double overrideRouterArrowheadLength;", "private Double overrideRouterArrowheadLength;", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("private boolean overrideUseFreePortsForObstaclePorts;", "private Boolean overrideUseFreePortsForObstaclePorts;", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("private boolean overrideUseSparseVisibilityGraph;", "private Boolean overrideUseSparseVisibilityGraph;", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("private boolean overrideUseObstacleRectangles;", "private Boolean overrideUseObstacleRectangles;", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("private boolean overrideLimitPortVisibilitySpliceToEndpointBoundingBox;", "private Boolean overrideLimitPortVisibilitySpliceToEndpointBoundingBox;", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("private boolean overrideWantPaths;", "private Boolean overrideWantPaths;", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("private boolean overrideWantNudger;", "private Boolean overrideWantNudger;", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("private boolean overrideWantVerify;", "private Boolean overrideWantVerify;", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("private double overrideStraightTolerance;", "private Double overrideStraightTolerance;", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("private double overrideCornerTolerance;", "private Double overrideCornerTolerance;", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("private double overrideBendPenalty;", "private Double overrideBendPenalty;", StringComparison.Ordinal);

            generatedCode = generatedCode.Replace("protected double getOverrideRouterPadding() {", "protected Double getOverrideRouterPadding() {", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("protected void setOverrideRouterPadding(double value) {", "protected void setOverrideRouterPadding(Double value) {", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("protected double getOverrideRouterEdgeSeparation() {", "protected Double getOverrideRouterEdgeSeparation() {", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("protected void setOverrideRouterEdgeSeparation(double value) {", "protected void setOverrideRouterEdgeSeparation(Double value) {", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("protected boolean getOverrideRouteToCenterOfObstacles() {", "protected Boolean getOverrideRouteToCenterOfObstacles() {", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("protected void setOverrideRouteToCenterOfObstacles(boolean value) {", "protected void setOverrideRouteToCenterOfObstacles(Boolean value) {", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("protected double getOverrideRouterArrowheadLength() {", "protected Double getOverrideRouterArrowheadLength() {", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("protected void setOverrideRouterArrowheadLength(double value) {", "protected void setOverrideRouterArrowheadLength(Double value) {", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("protected boolean getOverrideUseFreePortsForObstaclePorts() {", "protected Boolean getOverrideUseFreePortsForObstaclePorts() {", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("protected void setOverrideUseFreePortsForObstaclePorts(boolean value) {", "protected void setOverrideUseFreePortsForObstaclePorts(Boolean value) {", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("protected boolean getOverrideUseSparseVisibilityGraph() {", "protected Boolean getOverrideUseSparseVisibilityGraph() {", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("protected void setOverrideUseSparseVisibilityGraph(boolean value) {", "protected void setOverrideUseSparseVisibilityGraph(Boolean value) {", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("protected boolean getOverrideUseObstacleRectangles() {", "protected Boolean getOverrideUseObstacleRectangles() {", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("protected void setOverrideUseObstacleRectangles(boolean value) {", "protected void setOverrideUseObstacleRectangles(Boolean value) {", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("protected boolean getOverrideLimitPortVisibilitySpliceToEndpointBoundingBox() {", "protected Boolean getOverrideLimitPortVisibilitySpliceToEndpointBoundingBox() {", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("protected void setOverrideLimitPortVisibilitySpliceToEndpointBoundingBox(boolean value) {", "protected void setOverrideLimitPortVisibilitySpliceToEndpointBoundingBox(Boolean value) {", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("protected boolean getOverrideWantPaths() {", "protected Boolean getOverrideWantPaths() {", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("protected void setOverrideWantPaths(boolean value) {", "protected void setOverrideWantPaths(Boolean value) {", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("protected boolean getOverrideWantNudger() {", "protected Boolean getOverrideWantNudger() {", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("protected void setOverrideWantNudger(boolean value) {", "protected void setOverrideWantNudger(Boolean value) {", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("protected boolean getOverrideWantVerify() {", "protected Boolean getOverrideWantVerify() {", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("protected void setOverrideWantVerify(boolean value) {", "protected void setOverrideWantVerify(Boolean value) {", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("protected double getOverrideStraightTolerance() {", "protected Double getOverrideStraightTolerance() {", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("protected void setOverrideStraightTolerance(double value) {", "protected void setOverrideStraightTolerance(Double value) {", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("protected double getOverrideCornerTolerance() {", "protected Double getOverrideCornerTolerance() {", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("protected void setOverrideCornerTolerance(double value) {", "protected void setOverrideCornerTolerance(Double value) {", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("protected double getOverrideBendPenalty() {", "protected Double getOverrideBendPenalty() {", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("protected void setOverrideBendPenalty(double value) {", "protected void setOverrideBendPenalty(Double value) {", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace(
                "new Polyline(StreamSupport.stream(Enumerable.spliterator(), false).collect(Collectors.collectingAndThen(Collectors.toCollection(ArrayList::new), list -> { Collections.reverse(list); return list; })))",
                "new Polyline(StreamSupport.stream(java.util.Arrays.asList(points).spliterator(), false).collect(Collectors.collectingAndThen(Collectors.toCollection(ArrayList::new), list -> { Collections.reverse(list); return list; })))",
                StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("new Polyline(points)", "new Microsoft.Msagl.Core.Geometry.Curves.Polyline(points)", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("new Polyline(StreamSupport.stream(java.util.Arrays.asList(points).spliterator(), false).collect(Collectors.collectingAndThen(Collectors.toCollection(ArrayList::new), list -> { Collections.reverse(list); return list; })))", "new Microsoft.Msagl.Core.Geometry.Curves.Polyline(new ArrayList<Point>(StreamSupport.stream(java.util.Arrays.asList(points).spliterator(), false).collect(Collectors.collectingAndThen(Collectors.toCollection(ArrayList::new), list -> { Collections.reverse(list); return list; }))))", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("new Microsoft.Msagl.Core.Geometry.Curves.Polyline(StreamSupport.stream(java.util.Arrays.asList(points).spliterator(), false).collect(Collectors.collectingAndThen(Collectors.toCollection(ArrayList::new), list -> { Collections.reverse(list); return list; })))", "new Microsoft.Msagl.Core.Geometry.Curves.Polyline(new ArrayList<Point>(StreamSupport.stream(java.util.Arrays.asList(points).spliterator(), false).collect(Collectors.collectingAndThen(Collectors.toCollection(ArrayList::new), list -> { Collections.reverse(list); return list; }))))", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("throw new ApplicationException(", "throw new RuntimeException(", StringComparison.Ordinal);
        }

        if (string.Equals(fileNameOnly, "RectilinearTests.cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileNameOnly, "RectilinearTests.java", StringComparison.OrdinalIgnoreCase))
        {
            generatedCode = generatedCode.Replace("Object.empty()", "new Shape[0]", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("Arrays.stream(siblingIndexes).map(idx -> obstacles.get(idx)).toArray(Shape[]::new)", "Arrays.stream(siblingIndexes).mapToObj(idx -> obstacles.get(idx)).toArray(Shape[]::new)", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("var offset = new Point(0, 0);", "final Point[] offset = new Point[] { new Point(0, 0) };", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("() -> Point.add(b.getBoundingBox().getCenter(), offset)", "() -> Point.add(b.getBoundingBox().getCenter(), offset[0])", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("offset = new Point(-5, b.getBoundingBox().getTop() - b.getBoundingBox().getCenter().Y);", "offset[0] = new Point(-5, b.getBoundingBox().getTop() - b.getBoundingBox().getCenter().Y);", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("offset = new Point(-10, b.getBoundingBox().getBottom() - b.getBoundingBox().getCenter().Y);", "offset[0] = new Point(-10, b.getBoundingBox().getBottom() - b.getBoundingBox().getCenter().Y);", StringComparison.Ordinal);
        }

        if (string.Equals(fileNameOnly, "TestLineSweeper.cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileNameOnly, "TestLineSweeper.java", StringComparison.OrdinalIgnoreCase))
        {
            generatedCode = generatedCode.Replace(
                "new ArrayList<VisibilityEdge>(orig.getOutEdges())",
                "StreamSupport.stream(orig.getOutEdges().spliterator(), false).collect(Collectors.toCollection(ArrayList::new))",
                StringComparison.Ordinal);
        }

        if (string.Equals(fileNameOnly, "AspectRatioTests.cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileNameOnly, "AspectRatioTests.java", StringComparison.OrdinalIgnoreCase))
        {
            generatedCode = generatedCode.Replace(
                "String filePath = java.nio.file.Paths.get(this.getTestContext().TestDir, \"Out\\\\\\\\Dots\").toString();",
                "String filePath = resolveTestDataPath(\"DotFiles\\\\\\\\LevFiles\\\\\\\\chat.dot\");",
                StringComparison.Ordinal);
        }

        if (string.Equals(fileNameOnly, "SugiyamaEdgeLabelTests.cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileNameOnly, "SugiyamaEdgeLabelTests.java", StringComparison.OrdinalIgnoreCase))
        {
            generatedCode = generatedCode.Replace("edge.getPoints()", "EdgeExtensions.getPoints(edge)", StringComparison.Ordinal);
        }

        if (string.Equals(fileNameOnly, "SugiyamaLayoutTests.cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileNameOnly, "SugiyamaLayoutTests.java", StringComparison.OrdinalIgnoreCase))
        {
            generatedCode = generatedCode.Replace(
                "Paths.get(this.getTestContext().TestDir, \"Out\\\\Dots\\\\fsm.dot\").toString()",
                "resolveTestDataPath(\"DotFiles\\\\\\\\LevFiles\\\\\\\\fsm.dot\")",
                StringComparison.Ordinal);
            generatedCode = generatedCode.Replace(
                "java.nio.file.resolveTestDataPath(",
                "resolveTestDataPath(",
                StringComparison.Ordinal);
            generatedCode = generatedCode.Replace(
                "String[] allFiles = Files.getFiles(java.nio.file.Paths.get(this.getTestContext().TestDir, \"Out\\\\Dots\").toString(), \"*.dot\");",
                "String[] allFiles = findTestDataFiles(\"DotFiles\\\\LevFiles\", \"*.dot\");",
                StringComparison.Ordinal);
            generatedCode = Regex.Replace(
                generatedCode,
                @"Paths\.get\([^;\r\n]*?""Out\\Dots\\(?<file>[^""]+)""\)\.toString\(\)",
                "resolveTestDataPath(\"DotFiles\\\\LevFiles\\\\${file}\")");
            generatedCode = System.Text.RegularExpressions.Regex.Replace(
                generatedCode,
                @"String\[\]\s+allFiles\s*=\s*Files\.getFiles\((?<dir>.*),\s*\""\*\.dot\""\);",
                "String[] allFiles = findTestDataFiles(${dir}, \"*.dot\");");
        }

        if (string.Equals(fileNameOnly, "SugiyamaSettingsTests.cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileNameOnly, "SugiyamaSettingsTests.java", StringComparison.OrdinalIgnoreCase))
        {
            generatedCode = generatedCode.Replace(
                "GeometryGraphWriter.write(oldGraph, oldSettings, \"settings.msagl.geom\");",
                "try {\n        GeometryGraphWriter.write(oldGraph, oldSettings, \"settings.msagl.geom\");\n        } catch (Exception e) {\n        throw new RuntimeException(e);\n        }",
                StringComparison.Ordinal);
            generatedCode = generatedCode.Replace(
                "ObjectHolder<LayoutAlgorithmSettings> _baseSettingsHolder1 = new ObjectHolder<>();\n        GeometryGraphReader.createFromFile(\"settings.msagl.geom\", _baseSettingsHolder1);\n        baseSettings = _baseSettingsHolder1.value;",
                "ObjectHolder<LayoutAlgorithmSettings> _baseSettingsHolder1 = new ObjectHolder<>();\n        try {\n        GeometryGraphReader.createFromFile(\"settings.msagl.geom\", _baseSettingsHolder1);\n        } catch (Exception e) {\n        throw new RuntimeException(e);\n        }\n        baseSettings = _baseSettingsHolder1.value;",
                StringComparison.Ordinal);
        }

        if (string.Equals(fileNameOnly, "MsaglTestBase.cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileNameOnly, "MsaglTestBase.java", StringComparison.OrdinalIgnoreCase))
        {
            if (!generatedCode.Contains("File resolvedGraphPath = new File(geometryGraphFileName);", StringComparison.Ordinal))
            {
                generatedCode = generatedCode.Replace(
                    "if (StringHelper.isNullOrEmpty(geometryGraphFileName)) {\n        throw new NullPointerException(\"geometryGraphFileName\");\n        }",
                    "if (StringHelper.isNullOrEmpty(geometryGraphFileName)) {\n        throw new NullPointerException(\"geometryGraphFileName\");\n        }\n        geometryGraphFileName = resolveTestDataPath(geometryGraphFileName);\n        File resolvedGraphPath = new File(geometryGraphFileName);\n        if (!resolvedGraphPath.exists()) {\n        File resolvedGraphDirectory = resolveTestDataDirectory(geometryGraphFileName);\n        if (resolvedGraphDirectory != null) {\n        resolvedGraphPath = resolvedGraphDirectory;\n        geometryGraphFileName = resolvedGraphDirectory.getPath();\n        }\n        }\n        if (resolvedGraphPath.isDirectory()) {\n        String[] dotFiles = findTestDataFiles(geometryGraphFileName, \"*.dot\");\n        if (dotFiles.length > 0) {\n        Arrays.sort(dotFiles);\n        geometryGraphFileName = dotFiles[0];\n        } else {\n        String[] geomFiles = findTestDataFiles(geometryGraphFileName, \"*.geom\");\n        if (geomFiles.length > 0) {\n        Arrays.sort(geomFiles);\n        geometryGraphFileName = geomFiles[0];\n        }\n        }\n        }",
                    StringComparison.Ordinal);
            }
            if (!generatedCode.Contains("protected static String resolveTestDataPath(String fileName)", StringComparison.Ordinal))
            {
                generatedCode = generatedCode.Replace(
                    "protected static RelativeFloatingPort makePort(Node node) {",
                    "protected static String resolveTestDataPath(String fileName) {\n        if (StringHelper.isNullOrEmpty(fileName)) {\n        return fileName;\n        }\n        File directFile = new File(fileName);\n        if (directFile.exists()) {\n        return directFile.getPath();\n        }\n        String normalizedFileName = fileName.replace(\"\\\\\", File.separator).replace(\"/\", File.separator);\n        String leafName = new File(normalizedFileName).getName();\n        for (File root : enumerateTestDataRoots()) {\n        File candidate = new File(root, normalizedFileName);\n        if (candidate.exists()) {\n        return candidate.getPath();\n        }\n        File byName = new File(root, leafName);\n        if (byName.exists()) {\n        return byName.getPath();\n        }\n        }\n        return fileName;\n    }\n    protected static String[] findTestDataFiles(String relativeDir, String glob) {\n        File resolvedDir = resolveTestDataDirectory(relativeDir);\n        if (resolvedDir == null || !resolvedDir.isDirectory()) {\n        return new String[0];\n        }\n        File[] matchingFiles = resolvedDir.listFiles((currentDir, name) -> java.nio.file.FileSystems.getDefault().getPathMatcher(\"glob:\" + glob).matches(java.nio.file.Paths.get(name)));\n        return matchingFiles == null ? new String[0] : Arrays.stream(matchingFiles).map(File::getPath).toArray(String[]::new);\n    }\n    private static File resolveTestDataDirectory(String relativeDir) {\n        if (StringHelper.isNullOrEmpty(relativeDir)) {\n        return null;\n        }\n        File directDir = new File(relativeDir);\n        if (directDir.isDirectory()) {\n        return directDir;\n        }\n        String normalizedDir = relativeDir.replace(\"\\\\\", File.separator).replace(\"/\", File.separator);\n        String leafName = new File(normalizedDir).getName();\n        for (File root : enumerateTestDataRoots()) {\n        File candidate = new File(root, normalizedDir);\n        if (candidate.isDirectory()) {\n        return candidate;\n        }\n        if (\"Dots\".equalsIgnoreCase(leafName)) {\n        File dotFilesDir = new File(root, \"DotFiles\");\n        if (dotFilesDir.isDirectory()) {\n        return dotFilesDir;\n        }\n        }\n        if (\"MSAGLGeometryGraphs\".equalsIgnoreCase(leafName)) {\n        File geometryDir = new File(root, \"MsaglGeometryGraphs\");\n        if (geometryDir.isDirectory()) {\n        return geometryDir;\n        }\n        }\n        }\n        return directDir;\n    }\n    private static ArrayList<File> enumerateTestDataRoots() {\n        LinkedHashSet<String> rootPaths = new LinkedHashSet<>();\n        addTestDataRoot(rootPaths, System.getProperty(\"user.dir\"));\n        addTestDataRoot(rootPaths, TestContext.TestDir);\n        ArrayList<File> roots = new ArrayList<>();\n        for (String path : rootPaths) {\n        roots.add(new File(path));\n        }\n        return roots;\n    }\n    private static void addTestDataRoot(LinkedHashSet<String> rootPaths, String basePath) {\n        if (StringHelper.isNullOrEmpty(basePath)) {\n        return;\n        }\n        rootPaths.add(basePath);\n        rootPaths.add(new File(basePath, \"Resources\").getPath());\n        rootPaths.add(new File(basePath, \"src/test/resources\").getPath());\n        rootPaths.add(new File(basePath, \"src/test/resources/Resources\").getPath());\n        rootPaths.add(new File(basePath, \"target/test-classes\").getPath());\n        rootPaths.add(new File(basePath, \"target/test-classes/Resources\").getPath());\n    }\n    protected static RelativeFloatingPort makePort(Node node) {",
                    StringComparison.Ordinal);
            }
        }

        if (string.Equals(fileNameOnly, "ShapeCreator.cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileNameOnly, "ShapeCreator.java", StringComparison.OrdinalIgnoreCase))
        {
            generatedCode = generatedCode.Replace(
                "var _chainVal14 = nodesToShapes.put(c, createShapeWithClusterBoundaryPort(c));\n        cShape = _chainVal14;",
                "cShape = createShapeWithClusterBoundaryPort(c);\n        nodesToShapes.put(c, cShape);",
                StringComparison.Ordinal);
            generatedCode = generatedCode.Replace(
                "var _chainVal15 = nodesToShapes.put(n, createShapeWithCenterPort(n));\n        nShape = _chainVal15;",
                "nShape = createShapeWithCenterPort(n);\n        nodesToShapes.put(n, nShape);",
                StringComparison.Ordinal);
            generatedCode = generatedCode.Replace(
                "var _chainVal16 = nodesToShapes.put(cc, createShapeWithCenterPort(cc));\n        nShape = _chainVal16;",
                "nShape = createShapeWithCenterPort(cc);\n        nodesToShapes.put(cc, nShape);",
                StringComparison.Ordinal);
        }

        if (string.Equals(fileNameOnly, "Validate.cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileNameOnly, "Validate.java", StringComparison.OrdinalIgnoreCase))
        {
            generatedCode = generatedCode.Replace("private static boolean raiseInteractiveAssert(Exception ex)", "private static boolean raiseInteractiveAssert(Throwable ex)", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("var exceptionToUse = ex.getInnerException() != null ? ex.getInnerException() : ex;", "var exceptionToUse = ex.getCause() != null ? ex.getCause() : ex;", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("Debugger.breakValue();", "return;", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("catch (UnitTestAssertException ex)", "catch (AssertionError ex)", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("Assertions.assertEquals(expected, actual, ignoreCase, culture, message);", "Assertions.assertTrue(ignoreCase ? Objects.equals(expected == null ? null : expected.toLowerCase(java.util.Locale.ROOT), actual == null ? null : actual.toLowerCase(java.util.Locale.ROOT)) : Objects.equals(expected, actual), message);", StringComparison.Ordinal);
        }

        if (string.Equals(fileNameOnly, "ResultVerifierBase.cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileNameOnly, "ResultVerifierBase.java", StringComparison.OrdinalIgnoreCase))
        {
            generatedCode = generatedCode.Replace(
                "Duration ts = sw.getElapsed();\n        writeLine(\"  Elapsed time: {0:00}:{1:00}:{2:00}.{3:000}\", ts.getHours(), ts.getMinutes(), ts.getSeconds(), ts.getMilliseconds());",
                "long elapsedMillis = sw.getElapsedMilliseconds();\n        long elapsedHours = elapsedMillis / 3_600_000L;\n        long elapsedMinutes = (elapsedMillis / 60_000L) % 60;\n        long elapsedSeconds = (elapsedMillis / 1_000L) % 60;\n        long elapsedRemainderMillis = elapsedMillis % 1_000L;\n        writeLine(\"  Elapsed time: {0:00}:{1:00}:{2:00}.{3:000}\", elapsedHours, elapsedMinutes, elapsedSeconds, elapsedRemainderMillis);",
                StringComparison.Ordinal);
        }

        if (string.Equals(fileNameOnly, "OverlapRemovalVerifier.cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileNameOnly, "OverlapRemovalVerifier.java", StringComparison.OrdinalIgnoreCase))
        {
            generatedCode = System.Text.RegularExpressions.Regex.Replace(generatedCode, @"\bdumpRectangles\(\s*iterClusterDefs\s*\);", "dumpClusterRectangles(iterClusterDefs);", System.Text.RegularExpressions.RegexOptions.Multiline);
        }

        if (string.Equals(fileNameOnly, "TestFileReader.cs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileNameOnly, "TestFileReader.java", StringComparison.OrdinalIgnoreCase))
        {
            if (!generatedCode.Contains("BufferedReader sr = null;", StringComparison.Ordinal))
            {
                generatedCode = generatedCode.Replace(
                    "try (BufferedReader sr = new BufferedReader(new FileReader(strFullName))) {",
                    "BufferedReader sr = null;\n        try {\n        sr = new BufferedReader(new FileReader(strFullName));",
                    StringComparison.Ordinal);
                generatedCode = generatedCode.Replace(
                    "} // end using sr",
                    "} catch (IOException e) {\n        throw new RuntimeException(e);\n        } finally {\n        if (sr != null) {\n        try {\n        sr.close();\n        } catch (IOException ignored) {\n        }\n        }\n        } // end using sr",
                    StringComparison.Ordinal);
            }
            generatedCode = System.Text.RegularExpressions.Regex.Replace(
                generatedCode,
                @"(?<receiver>[A-Za-z_][A-Za-z0-9_\.()]*)\.startsWith\((?<prefix>[^,\n]+), StringComparison\.OrdinalIgnoreCase\)",
                "StringHelper.startsWith(${receiver}, ${prefix}, true)");
            generatedCode = generatedCode.Replace(
                "String.Compare(\"NewHierarchy\", currentLine, StringComparison.OrdinalIgnoreCase)",
                "StringHelper.compare(\"NewHierarchy\", currentLine, true)",
                StringComparison.Ordinal);
            generatedCode = generatedCode.Replace(
                "String.Compare(\"Fixed\", strFixedPos, StringComparison.OrdinalIgnoreCase)",
                "StringHelper.compare(\"Fixed\", strFixedPos, true)",
                StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("int style = System.Globalization.NumberStyles.Integer;", "int radix = 10;", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("style = System.Globalization.NumberStyles.HexNumber;", "radix = 16;", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("this.setSeed(Integer.parseInt(strArg, style));", "this.setSeed(radix == 16 ? Integer.parseUnsignedInt(strArg, radix) : Integer.parseInt(strArg, radix));", StringComparison.Ordinal);
            generatedCode = generatedCode.Replace("uint.parseUint(", "Integer.parseUnsignedInt(", StringComparison.Ordinal);
        }

        File.WriteAllText(outputPath, generatedCode, new System.Text.UTF8Encoding(false));
        return true;
    }

    private static void DeleteIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static IReadOnlyList<string> GetReferencedProjectDirectories(DiscoveredProject project, ProjectGraph graph)
    {
        var byProjectPath = graph.ProjectsInTopologicalOrder
            .ToDictionary(p => p.ProjectFilePath, p => p, StringComparer.OrdinalIgnoreCase);

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(project.ProjectReferences);

        while (queue.Count > 0)
        {
            var referencePath = queue.Dequeue();
            if (!visited.Add(referencePath))
            {
                continue;
            }

            if (!byProjectPath.TryGetValue(referencePath, out var referencedProject))
            {
                continue;
            }

            result.Add(referencedProject.ProjectDirectory);
            foreach (var next in referencedProject.ProjectReferences)
            {
                queue.Enqueue(next);
            }
        }

        return result.ToList();
    }

    private static async Task<int> AnalyzeProject(AnalyzeOptions opts)
    {
        try
        {
            if (!Directory.Exists(opts.Source))
            {
                Console.Error.WriteLine($"Error: Source directory not found: {opts.Source}");
                return 1;
            }

            TypeMappingRegistry registry;
            try
            {
                registry = new TypeMappingRegistry(opts.MappingConfig);
            }
            catch (TypeMappingConfigurationException ex)
            {
                Console.Error.WriteLine($"Configuration Error: {ex.Message}");
                Console.Error.WriteLine($"Expected location: {ex.ConfigPath}");
                return 1;
            }

            // 分析项目
            var csFiles = Directory.GetFiles(opts.Source, "*.cs", SearchOption.AllDirectories);

            var typeUsage = new Dictionary<string, HashSet<string>>();
            var missingMappings = new HashSet<string>();

            Console.WriteLine("Analyzing project...");
            Console.WriteLine();

            foreach (var file in csFiles)
            {
                try
                {
                    var sourceCode = await File.ReadAllTextAsync(file);
                    var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(sourceCode);
                    var root = tree.GetRoot();

                    // 收集类型使用
                    var typeNames = root.DescendantNodes()
                        .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeSyntax>()
                        .Select(t => t.ToString())
                        .Distinct();

                    foreach (var typeName in typeNames)
                    {
                        var mappedType = registry.MapType(typeName);
                        if (mappedType == typeName)
                        {
                            // 可能缺少映射
                            if (typeName.Contains(".") && !typeName.StartsWith("System."))
                            {
                                missingMappings.Add(typeName);
                            }
                        }

                        if (!typeUsage.ContainsKey(typeName))
                        {
                            typeUsage[typeName] = new HashSet<string>();
                        }
                        typeUsage[typeName].Add(file);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Warning: Could not analyze {file}: {ex.Message}");
                }
            }

            // 生成报告
            Console.WriteLine("Type Mapping Report");
            Console.WriteLine("===================");
            Console.WriteLine();

            Console.WriteLine("Most Common Types:");
            foreach (var (type, files) in typeUsage.OrderByDescending(kvp => kvp.Value.Count).Take(20))
            {
                var mapped = registry.MapType(type);
                var hasMapping = mapped != type;
                Console.WriteLine($"  {type} -> {mapped} {(hasMapping ? "OK" : "?")} ({files.Count} files)");
            }

            if (missingMappings.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Potential Missing Mappings:");
                foreach (var type in missingMappings.OrderBy(t => t))
                {
                    Console.WriteLine($"  {type}");
                }
            }

            // 保存报告
            if (opts.Report != null)
            {
                var report = new
                {
                    GeneratedAt = DateTime.UtcNow,
                    TypeUsage = typeUsage.ToDictionary(
                        kvp => kvp.Key,
                        kvp => new { Count = kvp.Value.Count, Files = kvp.Value.ToList() }
                    ),
                    MissingMappings = missingMappings.ToList()
                };

                var json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                });

                await File.WriteAllTextAsync(opts.Report, json, new System.Text.UTF8Encoding(false));
                Console.WriteLine();
                Console.WriteLine($"Report saved to: {opts.Report}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static async Task WriteSingleModulePom(ConvertProjectOptions opts, JavaModulePlan modulePlan)
    {
        var plan = BuildWorkspacePlan(opts, [modulePlan]);

        var generator = new MavenPomGenerator();
        var pomContent = generator.GenerateRootBuildFile(plan);
        var pomPath = Path.Combine(opts.Destination, generator.BuildFileName);
        await File.WriteAllTextAsync(pomPath, pomContent);
        await WriteWorkspacePlanManifest(opts.Destination, plan);
    }

    private static JavaModulePlan CreateSingleModulePlan(
        ConvertProjectOptions opts,
        bool includeTests,
        IReadOnlyList<ConversionResult> convertedResults)
    {
        var compatibilityRequirements = CompatibilityPackPlanner.Analyze(
            convertedResults,
            BuildSharedCompatibilityPackage(opts.MavenGroupId));

        return new JavaModulePlan
        {
            ModuleName = new DirectoryInfo(opts.Destination).Name,
            IsTestOnly = false,
            SourceSets = includeTests ? new JavaSourceSets { TestSources = ["test"] } : new JavaSourceSets(),
            Dependencies = WorkspacePlanBuilder.MergeDependencies(
                WorkspacePlanBuilder.DefaultDependencies().Concat(compatibilityRequirements.ExternalDependencies)),
            RequiredCompatPacks = compatibilityRequirements.RequiredPackIds,
        };
    }

    private static bool ShouldAnalyzeForCompatibilityPlanning(ConversionResult result)
    {
        return result.Success
            && !string.IsNullOrEmpty(result.GeneratedCode)
            && !CompatibilityClassGenerator.IsCompatibilitySupportFile(result.FileName);
    }

    private static async Task WriteMultiModulePom(ConvertProjectOptions opts, string moduleRoot, JavaModulePlan modulePlan)
    {
        var plan = BuildWorkspacePlan(opts, [modulePlan]);

        var generator = new MavenPomGenerator();
        var pomContent = generator.GenerateModuleBuildFile(plan, modulePlan);
        var pomPath = Path.Combine(moduleRoot, generator.BuildFileName);
        await File.WriteAllTextAsync(pomPath, pomContent);
    }

    private static async Task WriteParentPom(ConvertProjectOptions opts, JavaWorkspacePlan plan)
    {
        var generator = new MavenPomGenerator();
        var pomContent = generator.GenerateRootBuildFile(plan);
        var pomPath = Path.Combine(opts.Destination, generator.BuildFileName);
        await File.WriteAllTextAsync(pomPath, pomContent);
    }

    private static JavaWorkspacePlan BuildWorkspacePlan(ConvertProjectOptions opts, IReadOnlyList<JavaModulePlan> modulePlans)
    {
        var builder = new WorkspacePlanBuilder()
            .GroupId(opts.MavenGroupId)
            .ArtifactId(new DirectoryInfo(opts.Destination).Name)
            .Version(opts.MavenVersion)
            .JavaVersion(opts.JavaVersion);

        foreach (var modulePlan in modulePlans)
        {
            builder.AddModule(modulePlan);
        }

        return builder.Build();
    }

    private static async Task WriteWorkspacePlanManifest(string destinationRoot, JavaWorkspacePlan plan)
    {
        var serializer = new JavaWorkspacePlanJsonSerializer();
        var manifestPath = Path.Combine(destinationRoot, "cs2j-workspace-plan.json");
        await File.WriteAllTextAsync(manifestPath, serializer.Serialize(plan), new System.Text.UTF8Encoding(false));
    }

    private static JavaModulePlan PlannedModuleToJavaModulePlan(PlannedModule module, string groupId, IReadOnlyList<string>? requiredCompatPacks = null)
    {
        var deps = new List<JavaDependency>(WorkspacePlanBuilder.DefaultDependencies());

        foreach (var dep in module.CompileDependencies.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            deps.Add(WorkspacePlanBuilder.InternalModuleRef(groupId, dep));
        }

        foreach (var dep in module.TestDependencies.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            deps.Add(WorkspacePlanBuilder.InternalModuleRef(groupId, dep, JavaDependencyScope.Test));
        }

        return new JavaModulePlan
        {
            ModuleName = module.Name,
            IsTestOnly = module.IsTestOnly,
            SourceSets = module.HasTestSources
                ? new JavaSourceSets { TestSources = ["test"] }
                : new JavaSourceSets(),
            Dependencies = deps,
            RequiredCompatPacks = requiredCompatPacks ?? [],
        };
    }

    private static string FormatDiagnostic(DiagnosticMessage diag)
    {
        var label = diag.Code != null
            ? $"{diag.Code}{(diag.Category != null ? $" [{diag.Category}]" : string.Empty)}: "
            : diag.Category != null
                ? $"[{diag.Category}] "
                : string.Empty;

        if (diag.Location == null || !diag.Location.IsInSource)
        {
            return label + diag.Message;
        }

        var lineSpan = diag.Location.GetLineSpan();
        var line = lineSpan.StartLinePosition.Line + 1;
        var column = lineSpan.StartLinePosition.Character + 1;
        return $"{label}{diag.Message} ({lineSpan.Path}:{line}:{column})";
    }
}

// 命令行选项
[Verb("convert", HelpText = "Convert a C# file to Java")]
class ConvertOptions
{
    [Option('i', "input", Required = true, HelpText = "Input C# file path")]
    public string Input { get; set; } = string.Empty;

    [Option('o', "output", Required = false, HelpText = "Output Java file path (default: stdout)")]
    public string? Output { get; set; }

    [Option('m', "mapping", Required = false, HelpText = "Path to type mapping configuration file")]
    public string? MappingConfig { get; set; }

    [Option('j', "java-version", Default = "Java25", HelpText = "Target Java version (Java25)")]
    public string JavaVersion { get; set; } = "Java25";

    [Option("no-records", Default = false, HelpText = "Don't use Java records for C# records")]
    public bool NoRecords { get; set; }

    [Option("use-optional", Default = false, HelpText = "Use Optional for nullable types")]
    public bool UseOptionalForNullable { get; set; }

    [Option("no-javadoc", Default = false, HelpText = "Don't generate JavaDoc comments")]
    public bool NoJavaDoc { get; set; }

    [Option('v', "verbose", Default = false, HelpText = "Show diagnostic messages")]
    public bool Verbose { get; set; }

    [Option("no-linq-rewrite", Default = false, HelpText = "Don't pre-process LINQ to procedural code")]
    public bool NoLinqRewrite { get; set; }

    [Option("prefer-stream-api", Default = false, SetName = "linq-strategy", HelpText = "Prefer Java Stream API for LINQ conversion (default for Java 25)")]
    public bool PreferStreamApiFlag { get; set; }

    [Option("prefer-procedural", Default = false, SetName = "linq-strategy", HelpText = "Prefer procedural loops for LINQ conversion")]
    public bool PreferProceduralFlag { get; set; }

    // 便捷属性
    public bool UseRecords => !NoRecords;
    public bool GenerateJavaDoc => !NoJavaDoc;
    public bool EnableLinqRewrite => !NoLinqRewrite;

    /// <summary>Resolve PreferStreamApi: explicit flags override, otherwise null (version-based default).</summary>
    public bool? PreferStreamApi => PreferStreamApiFlag ? true : PreferProceduralFlag ? false : null;
}

[Verb("convert-project", HelpText = "Convert a C# project to Java")]
class ConvertProjectOptions
{
    [Option('s', "source", Required = true, HelpText = "Source directory, .csproj, or .sln path")]
    public string Source { get; set; } = string.Empty;

    [Option('d', "destination", Required = true, HelpText = "Destination directory path")]
    public string Destination { get; set; } = string.Empty;

    [Option('m', "mapping", Required = false, HelpText = "Path to type mapping configuration file")]
    public string? MappingConfig { get; set; }

    [Option('j', "java-version", Default = "Java25", HelpText = "Target Java version (Java25)")]
    public string JavaVersion { get; set; } = "Java25";

    [Option('f', "force", Default = true, HelpText = "Overwrite existing files")]
    public bool Force { get; set; }

    [Option("no-records", Default = false, HelpText = "Don't use Java records")]
    public bool NoRecords { get; set; }

    [Option("use-optional", Default = false, HelpText = "Use Optional for nullable types")]
    public bool UseOptionalForNullable { get; set; }

    [Option("no-javadoc", Default = false, HelpText = "Don't generate JavaDoc")]
    public bool NoJavaDoc { get; set; }

    [Option('v', "verbose", Default = false, HelpText = "Show detailed progress")]
    public bool Verbose { get; set; }

    [Option("no-linq-rewrite", Default = false, HelpText = "Don't pre-process LINQ to procedural code")]
    public bool NoLinqRewrite { get; set; }

    [Option("prefer-stream-api", Default = false, SetName = "linq-strategy", HelpText = "Prefer Java Stream API for LINQ conversion (default for Java 25)")]
    public bool PreferStreamApiFlag { get; set; }

    [Option("prefer-procedural", Default = false, SetName = "linq-strategy", HelpText = "Prefer procedural loops for LINQ conversion")]
    public bool PreferProceduralFlag { get; set; }

    [Option("generate-pom", Default = true, HelpText = "Generate Maven pom.xml file with standard project structure")]
    public bool GeneratePom { get; set; } = true;

    [Option("maven-group-id", Default = "io.github.ningpp", HelpText = "Maven groupId")]
    public string MavenGroupId { get; set; } = "io.github.ningpp";

    [Option("maven-version", Default = "0.0.1-SNAPSHOT", HelpText = "Maven version")]
    public string MavenVersion { get; set; } = "0.0.1-SNAPSHOT";

    [Option("include-tests", Default = true, HelpText = "Include discovered test projects and write them to src/test/java")]
    public bool IncludeTests { get; set; } = true;

    [Option("mode", Default = "multi-module", HelpText = "Output mode: single-module or multi-module")]
    public string Mode { get; set; } = "multi-module";

    public bool UseRecords => !NoRecords;
    public bool GenerateJavaDoc => !NoJavaDoc;
    public bool EnableLinqRewrite => !NoLinqRewrite;

    /// <summary>Resolve PreferStreamApi: explicit flags override, otherwise null (version-based default).</summary>
    public bool? PreferStreamApi => PreferStreamApiFlag ? true : PreferProceduralFlag ? false : null;
}

[Verb("analyze", HelpText = "Analyze a C# project and generate type mapping report")]
class AnalyzeOptions
{
    [Option('s', "source", Required = true, HelpText = "Source directory path")]
    public string Source { get; set; } = string.Empty;

    [Option('m', "mapping", Required = false, HelpText = "Path to type mapping configuration file")]
    public string? MappingConfig { get; set; }

    [Option('r', "report", Required = true, HelpText = "Output report file path")]
    public string Report { get; set; } = string.Empty;
}
