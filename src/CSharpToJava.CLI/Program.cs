using CommandLine;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.Pipeline.Compatibility;
using CSharpToJava.Core.Pipeline.Planning;
using CSharpToJava.TypeMapping;
using CSharpToJava.Core.Workspace;

namespace CSharpToJava.CLI;

public class Program
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
                JavaMetadataPath = ResolveJavaMetadataPath(opts.MappingConfig),
                GenerateJavaDoc = opts.GenerateJavaDoc,
                UseRecords = opts.UseRecords,
                UseOptionalForNullable = opts.UseOptionalForNullable,
                EnableLinqRewrite = opts.EnableLinqRewrite,
                PreferStreamApi = opts.PreferStreamApi,
                EnableParallelProjectPasses = opts.EnableParallelProjectPasses,
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
                JavaMetadataPath = ResolveJavaMetadataPath(opts.MappingConfig),
                GenerateJavaDoc = opts.GenerateJavaDoc,
                UseRecords = opts.UseRecords,
                UseOptionalForNullable = opts.UseOptionalForNullable,
                EnableLinqRewrite = opts.EnableLinqRewrite,
                PreferStreamApi = opts.PreferStreamApi,
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
                workspaceProjects = NormalizeWorkspaceProjects(workspaceProjects, opts.Verbose);
                var inputFingerprintSnapshot = BuildWorkspaceInputFingerprintSnapshot(opts, workspaceProjects);
                if (!opts.NoCache && TryReusePreviousProjectOutputs(opts.Destination, inputFingerprintSnapshot, opts.Verbose))
                {
                    return 0;
                }

                workspaceProjects = FilterUnsupportedWorkspaceProjects(workspaceProjects, opts.Verbose);
                if (workspaceProjects.Count == 0)
                {
                    Console.Error.WriteLine("Error: All discovered projects were excluded by platform rules.");
                    return 1;
                }

                if (opts.Verbose)
                {
                    Console.WriteLine($"MSBuild resolved {workspaceProjects.Count} project(s)");
                }

                return await ConvertFromWorkspaceMultiModule(opts, options, workspaceProjects, inputFingerprintSnapshot);
            }

            if (workspaceProjects != null && workspaceProjects.Count == 0)
            {
                if (opts.Verbose)
                {
                    Console.WriteLine("MSBuild loaded the project but produced empty compilations; falling back to directory scan.");
                }
                workspaceProjects = null;
            }

            // Fall back to manual project discovery (no MSBuild SDK available).
            if (ProjectDiscovery.TryResolveProjectEntry(opts.Source, out var entryProject))
            {
                var graph = ProjectDiscovery.LoadProjectGraph(entryProject);
                var inputFingerprintSnapshot = BuildProjectGraphInputFingerprintSnapshot(opts, graph);
                if (!opts.NoCache && TryReusePreviousProjectOutputs(opts.Destination, inputFingerprintSnapshot, opts.Verbose))
                {
                    return 0;
                }

                graph = FilterUnsupportedProjectGraph(graph, opts.Verbose);
                if (graph.ProjectsInTopologicalOrder.Count == 0)
                {
                    Console.Error.WriteLine("Error: All discovered projects were excluded by platform rules.");
                    return 1;
                }

                return await ConvertFromProjectGraph(opts, options, graph, inputFingerprintSnapshot);
            }

            var manualInputFingerprintSnapshot = BuildManualInputFingerprintSnapshot(opts);
            if (!opts.NoCache && TryReusePreviousProjectOutputs(opts.Destination, manualInputFingerprintSnapshot, opts.Verbose))
            {
                return 0;
            }

            var outputRoot = Path.Combine(opts.Destination, "src", "main", "java");
            var outputSession = OutputIncrementalWriteSession.Create(opts.Destination, opts.Source);

            if (opts.Force && Directory.Exists(outputRoot) && !outputSession.HasPreviousManifest)
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
            var passProfileEntries = new List<PassProfileEntry>();

            AddProjectPassProfileEntry(
                passProfileEntries,
                pipeline.LastProjectPassMetrics,
                results,
                GetSourceName(opts.Source));

            foreach (var result in results)
            {
                if (ShouldAnalyzeForCompatibilityPlanning(result))
                {
                    planningResults.Add(result);
                }

                if (result.FileName == null) continue;

                if (TryWriteConvertedFile(result, opts.Source, outputRoot, outputSession, out var outputPath))
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

            await WriteSingleModulePom(opts, CreateSingleModulePlan(opts, includeTests: false, planningResults), outputSession);

            GeneratedProjectAssets.WriteRootFiles(opts.Destination, outputSession);
            var passProfileSnapshot = await WritePassProfileSnapshot(opts.Destination, passProfileEntries, outputSession);
            await WriteCanarySummarySnapshot(opts.Destination, opts.Source, results, passProfileSnapshot, outputSession);
            WriteInputFingerprintSnapshot(opts.Destination, manualInputFingerprintSnapshot, outputSession);
            if (opts.LinqReport)
                WriteLinqReport(opts.Destination, pipeline.LastLinqStatistics, outputSession);
            await outputSession.SaveAsync();

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

    private static async Task<int> ConvertFromProjectGraph(
        ConvertProjectOptions opts,
        ConversionOptions options,
        ProjectGraph graph,
        InputFingerprintSnapshot inputFingerprintSnapshot)
    {
        if (graph.ProjectsInTopologicalOrder.Count == 0)
        {
            Console.Error.WriteLine($"Error: No project discovered from {graph.RootProjectPath}");
            return 1;
        }

        return await ConvertFromProjectGraphMultiModule(opts, options, graph, inputFingerprintSnapshot);

    }

    private static async Task<int> ConvertFromWorkspaceMultiModule(
        ConvertProjectOptions opts,
        ConversionOptions options,
        IReadOnlyList<WorkspaceProject> projects,
        InputFingerprintSnapshot inputFingerprintSnapshot)
    {
        var outputSession = OutputIncrementalWriteSession.Create(opts.Destination, opts.Source);
        // Build module plan from workspace projects.
        var mainProjects = projects.Where(p => !p.IsTestProject).ToList();
        var testProjects = projects.Where(p => p.IsTestProject).ToList();

        options.SharedCompatibilityPackage = CompatibilityRuntime.JavaPackage;

        if (opts.Force && Directory.Exists(opts.Destination) && !outputSession.HasPreviousManifest)
        {
            ClearDestinationForFreshConversion(opts.Destination);
        }

        int successCount = 0;
        int failureCount = 0;
        int copiedResourceCount = 0;
        var modulePlans = new List<JavaModulePlan>();
        var convertedModuleCount = 1;
        var canaryResults = new List<ConversionResult>();
        var passProfileEntries = new List<PassProfileEntry>();
        CSharpToJava.Core.LinqRewrite.LinqRewriteStatistics? workspaceLinqStatistics = null;

        // Sort projects topologically so that dependencies (which may register
        // default factory methods) are converted before the projects that depend on them.
        var sortedProjects = SortProjectsTopologically(projects);

        // Convert each workspace project as its own module.
        foreach (var project in sortedProjects)
        {
            if (!opts.IncludeTests && project.IsTestProject) continue;

            var moduleName = MultiModulePlanner.NormalizeModuleName(project.Name);
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

            // Copy project resources (CopyToOutputDirectory files from .csproj)
            foreach (var resource in project.ResourceItems)
            {
                var destination = Path.Combine(resourcesRoot, resource.RelativePath);
                if (outputSession.CopyFile(resource.SourcePath, destination, OutputIncrementalEntryKind.CopiedResource))
                {
                    copiedResourceCount++;
                }

                if (opts.Verbose)
                {
                    Console.WriteLine($"Resource: {resource.SourcePath} -> {destination}");
                }
            }

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
            var results = await pipeline.ConvertProjectAsync(
                project.Compilation,
                emitFilePaths,
                project.Name,
                project.FilePath,
                project.ProjectReferences,
                project.IsTestProject);
            MergeLinqStatistics(ref workspaceLinqStatistics, pipeline.LastLinqStatistics);
            canaryResults.AddRange(results);
            AddProjectPassProfileEntry(passProfileEntries, pipeline.LastPassMetrics, results, project.Name, moduleName);

            foreach (var result in results)
            {
                if (result.FileName == null) continue;
                if (TryWriteConvertedFile(result, project.Directory, javaRoot, outputSession, out var outputPath))
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

            var compatibilityRequirements = CompatibilityPackPlanner.Analyze(results, CompatibilityRuntime.JavaPackage);

            var deps = new List<JavaDependency>(WorkspacePlanBuilder.DefaultDependenciesForModule(
                opts.MavenGroupId,
                moduleName));
            foreach (var refPath in project.ProjectReferences)
            {
                var refProject = projects.FirstOrDefault(p =>
                    string.Equals(p.FilePath, refPath, StringComparison.OrdinalIgnoreCase));
                if (refProject != null)
                {
                    deps.Add(WorkspacePlanBuilder.InternalModuleRef(opts.MavenGroupId, MultiModulePlanner.NormalizeModuleName(refProject.Name)));
                }
            }

            var modulePlan = new JavaModulePlan
            {
                ModuleName = moduleName,
                IsTestOnly = isTest,
                SourceSets = isTest ? new JavaSourceSets { TestSources = ["test"] } : new JavaSourceSets(),
                Dependencies = deps,
                RequiredCompatPacks = compatibilityRequirements.RequiredPackIds,
                RequiredRuntimeBridges = compatibilityRequirements.RuntimeBridges,
            };

            modulePlans.Add(modulePlan);
            await WriteMultiModulePom(opts, moduleRoot, modulePlan, outputSession);
        }

        var workspacePlan = BuildWorkspacePlan(opts, modulePlans);
        await WriteParentPom(opts, workspacePlan, outputSession);
        await WriteWorkspacePlanManifest(opts.Destination, workspacePlan, outputSession);

        GeneratedProjectAssets.WriteRootFiles(opts.Destination, outputSession);
        var passProfileSnapshot = await WritePassProfileSnapshot(opts.Destination, passProfileEntries, outputSession);
        await WriteCanarySummarySnapshot(opts.Destination, opts.Source, canaryResults, passProfileSnapshot, outputSession);
        WriteInputFingerprintSnapshot(opts.Destination, inputFingerprintSnapshot, outputSession);
        if (opts.LinqReport)
            WriteLinqReport(opts.Destination, workspaceLinqStatistics, outputSession);
        await outputSession.SaveAsync();

        Console.WriteLine();
        Console.WriteLine($"Conversion complete (MSBuild): {successCount} succeeded, {failureCount} failed, {copiedResourceCount} resources copied, modules={convertedModuleCount}");

        return failureCount > 0 ? 1 : 0;
    }

    private static async Task<int> ConvertFromProjectGraphMultiModule(
        ConvertProjectOptions opts,
        ConversionOptions options,
        ProjectGraph graph,
        InputFingerprintSnapshot inputFingerprintSnapshot)
    {
        var plan = MultiModulePlanner.Build(graph, opts.IncludeTests);
        if (plan.ModulesInBuildOrder.Count == 0)
        {
            Console.Error.WriteLine("Error: No modules planned for conversion.");
            return 1;
        }

        options.SharedCompatibilityPackage = CompatibilityRuntime.JavaPackage;
        var outputSession = OutputIncrementalWriteSession.Create(opts.Destination, opts.Source);

        if (opts.Force && Directory.Exists(opts.Destination) && !outputSession.HasPreviousManifest)
        {
            ClearDestinationForFreshConversion(opts.Destination);
        }

        int successCount = 0;
        int failureCount = 0;
        int copiedResourceCount = 0;
        var modulePlans = new List<JavaModulePlan>();
        var canaryResults = new List<ConversionResult>();
        var passProfileEntries = new List<PassProfileEntry>();
        CSharpToJava.Core.LinqRewrite.LinqRewriteStatistics? projectGraphLinqStatistics = null;

        var pipeline = new ConversionPipeline();

        foreach (var module in plan.ModulesInBuildOrder)
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
                outputSession.WriteTextFile(junitPlatformProps, "junit.jupiter.execution.timeout.default = 60s\n", OutputIncrementalEntryKind.BuildFile);
            }

            if (opts.Verbose)
            {
                Console.WriteLine($"Converting module: {module.Name}");
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
                var results = await pipeline.ConvertProjectWithPartialMergeAsync(
                    assignment.Project.ProjectDirectory,
                    options,
                    semanticContextDirs,
                    assignment.Project.Name,
                    assignment.Project.ProjectFilePath,
                    assignment.Project.ProjectReferences,
                    assignment.Project.Kind == ProjectKind.Test);
                MergeLinqStatistics(ref projectGraphLinqStatistics, pipeline.LastLinqStatistics);
                canaryResults.AddRange(results);
                moduleResults.AddRange(results.Where(result => !string.IsNullOrEmpty(result.GeneratedCode)));
                AddProjectPassProfileEntry(passProfileEntries, pipeline.LastProjectPassMetrics, results, assignment.Project.Name, module.Name);
                foreach (var result in results)
                {
                    if (result.FileName == null) continue;

                    if (TryWriteConvertedFile(result, assignment.Project.ProjectDirectory, targetJavaRoot, outputSession, out var outputPath))
                    {
                        successCount++;
                        if (opts.Verbose)
                        {
                            Console.WriteLine($"Converted: {result.FileName} -> {outputPath}");
                            foreach (var diag in result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Info))
                            {
                                Console.WriteLine($"  [INFO] {FormatDiagnostic(diag)}");
                            }
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
                    if (outputSession.CopyFile(resource.SourcePath, destination, OutputIncrementalEntryKind.CopiedResource))
                    {
                        copiedResourceCount++;
                    }

                    if (opts.Verbose)
                    {
                        Console.WriteLine($"Resource: {resource.SourcePath} -> {destination}");
                    }
                }
            }

            var compatibilityRequirements = CompatibilityPackPlanner.Analyze(
                moduleResults,
                CompatibilityRuntime.JavaPackage);

            var modulePlan = PlannedModuleToJavaModulePlan(
                module,
                opts.MavenGroupId,
                compatibilityRequirements.RequiredPackIds,
                compatibilityRequirements.RuntimeBridges);
            modulePlans.Add(modulePlan);
            await WriteMultiModulePom(opts, moduleRoot, modulePlan, outputSession);
        }

        var workspacePlan = BuildWorkspacePlan(opts, modulePlans);
        await WriteParentPom(opts, workspacePlan, outputSession);
        await WriteWorkspacePlanManifest(opts.Destination, workspacePlan, outputSession);

        GeneratedProjectAssets.WriteRootFiles(opts.Destination, outputSession);
        var passProfileSnapshot = await WritePassProfileSnapshot(opts.Destination, passProfileEntries, outputSession);
        await WriteCanarySummarySnapshot(opts.Destination, opts.Source, canaryResults, passProfileSnapshot, outputSession);
        WriteInputFingerprintSnapshot(opts.Destination, inputFingerprintSnapshot, outputSession);
        if (opts.LinqReport)
            WriteLinqReport(opts.Destination, projectGraphLinqStatistics, outputSession);
        await outputSession.SaveAsync();

        Console.WriteLine();
        Console.WriteLine($"Conversion complete: {successCount} succeeded, {failureCount} failed, {copiedResourceCount} resources copied, modules={plan.ModulesInBuildOrder.Count}");

        return failureCount > 0 ? 1 : 0;
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

    private static bool TryWriteConvertedFile(
        ConversionResult result,
        string sourceRoot,
        string outputRoot,
        OutputIncrementalWriteSession outputSession,
        out string outputPath)
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
        var generatedCode = result.GeneratedCode;
        outputSession.WriteTextFile(outputPath, generatedCode, OutputIncrementalEntryKind.GeneratedSource);
        return true;
    }

    private static void DeleteIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    internal static void ClearDestinationForFreshConversion(string destinationRoot)
    {
        if (!Directory.Exists(destinationRoot))
        {
            Directory.CreateDirectory(destinationRoot);
            return;
        }

        var fullDestinationPath = Path.GetFullPath(destinationRoot);
        if (string.IsNullOrWhiteSpace(fullDestinationPath)
            || Path.GetPathRoot(fullDestinationPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(fullDestinationPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new InvalidOperationException($"Refusing to clear unsafe destination path: {destinationRoot}");
        }

        foreach (var file in Directory.EnumerateFiles(fullDestinationPath))
        {
            File.Delete(file);
        }

        foreach (var directory in Directory.EnumerateDirectories(fullDestinationPath))
        {
            Directory.Delete(directory, recursive: true);
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

    private static Task WriteSingleModulePom(
        ConvertProjectOptions opts,
        JavaModulePlan modulePlan,
        OutputIncrementalWriteSession outputSession)
    {
        var plan = BuildWorkspacePlan(opts, [modulePlan]);

        var generator = new MavenPomGenerator();
        var pomContent = generator.GenerateRootBuildFile(plan, false);
        var pomPath = Path.Combine(opts.Destination, generator.BuildFileName);
        outputSession.WriteTextFile(pomPath, pomContent, OutputIncrementalEntryKind.BuildFile);
        return WriteWorkspacePlanManifest(opts.Destination, plan, outputSession);
    }

    private static JavaModulePlan CreateSingleModulePlan(
        ConvertProjectOptions opts,
        bool includeTests,
        IReadOnlyList<ConversionResult> convertedResults)
    {
        var compatibilityRequirements = CompatibilityPackPlanner.Analyze(
            convertedResults,
            CompatibilityRuntime.JavaPackage);

        var deps = new List<JavaDependency>();
        deps.AddRange(WorkspacePlanBuilder.DefaultDependenciesForModule(
            opts.MavenGroupId,
            new DirectoryInfo(opts.Destination).Name));
        deps.AddRange(compatibilityRequirements.ExternalDependencies);

        return new JavaModulePlan
        {
            ModuleName = new DirectoryInfo(opts.Destination).Name,
            IsTestOnly = false,
            SourceSets = includeTests ? new JavaSourceSets { TestSources = ["test"] } : new JavaSourceSets(),
            Dependencies = WorkspacePlanBuilder.MergeDependencies(deps),
            RequiredCompatPacks = compatibilityRequirements.RequiredPackIds,
            RequiredRuntimeBridges = compatibilityRequirements.RuntimeBridges,
        };
    }

    private static bool ShouldAnalyzeForCompatibilityPlanning(ConversionResult result)
    {
        return result.Success
            && !string.IsNullOrEmpty(result.GeneratedCode);
    }

    private static Task WriteMultiModulePom(
        ConvertProjectOptions opts,
        string moduleRoot,
        JavaModulePlan modulePlan,
        OutputIncrementalWriteSession outputSession)
    {
        var plan = BuildWorkspacePlan(opts, [modulePlan]);

        var generator = new MavenPomGenerator();
        var pomContent = generator.GenerateModuleBuildFile(plan, modulePlan);
        var pomPath = Path.Combine(moduleRoot, generator.BuildFileName);
        outputSession.WriteTextFile(pomPath, pomContent, OutputIncrementalEntryKind.BuildFile);
        return Task.CompletedTask;
    }

    private static Task WriteParentPom(
        ConvertProjectOptions opts,
        JavaWorkspacePlan plan,
        OutputIncrementalWriteSession outputSession)
    {
        var generator = new MavenPomGenerator();
        var pomContent = generator.GenerateRootBuildFile(plan, true);
        var pomPath = Path.Combine(opts.Destination, generator.BuildFileName);
        outputSession.WriteTextFile(pomPath, pomContent, OutputIncrementalEntryKind.BuildFile);
        return Task.CompletedTask;
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

    private static Task WriteWorkspacePlanManifest(
        string destinationRoot,
        JavaWorkspacePlan plan,
        OutputIncrementalWriteSession outputSession)
    {
        var serializer = new JavaWorkspacePlanJsonSerializer();
        var manifestPath = Path.Combine(destinationRoot, "cs2j-workspace-plan.json");
        outputSession.WriteTextFile(manifestPath, serializer.Serialize(plan), OutputIncrementalEntryKind.WorkspaceManifest);
        return Task.CompletedTask;
    }

    private static Task<PassProfileSnapshot?> WritePassProfileSnapshot(
        string destinationRoot,
        IReadOnlyList<PassProfileEntry> entries,
        OutputIncrementalWriteSession outputSession)
    {
        if (entries.Count == 0)
        {
            return Task.FromResult<PassProfileSnapshot?>(null);
        }

        var snapshot = PassProfileSnapshotBuilder.Build(entries);
        var serializer = new PassProfileSnapshotJsonSerializer();
        var snapshotPath = Path.Combine(destinationRoot, "cs2j-pass-profile.json");
        outputSession.WriteTextFile(snapshotPath, serializer.Serialize(snapshot), OutputIncrementalEntryKind.PassProfile);
        return Task.FromResult<PassProfileSnapshot?>(snapshot);
    }

    private static Task WriteCanarySummarySnapshot(
        string destinationRoot,
        string sourcePath,
        IReadOnlyList<ConversionResult> results,
        PassProfileSnapshot? passProfileSnapshot,
        OutputIncrementalWriteSession outputSession)
    {
        if (results.Count == 0)
        {
            return Task.CompletedTask;
        }

        var snapshot = CanarySummarySnapshotBuilder.Build(GetSourceName(sourcePath), sourcePath, results, passProfileSnapshot);
        var serializer = new CanarySummarySnapshotJsonSerializer();
        var summaryPath = Path.Combine(destinationRoot, "cs2j-canary-summary.json");
        outputSession.WriteTextFile(summaryPath, serializer.Serialize(snapshot), OutputIncrementalEntryKind.CanarySummary);
        return Task.CompletedTask;
    }

    private static void WriteInputFingerprintSnapshot(
        string destinationRoot,
        InputFingerprintSnapshot snapshot,
        OutputIncrementalWriteSession outputSession)
    {
        var serializer = new InputFingerprintSnapshotJsonSerializer();
        var snapshotPath = Path.Combine(destinationRoot, InputFingerprintSnapshot.FileName);
        outputSession.WriteTextFile(snapshotPath, serializer.Serialize(snapshot), OutputIncrementalEntryKind.InputFingerprint);
    }

    private static void WriteLinqReport(
        string destinationRoot,
        CSharpToJava.Core.LinqRewrite.LinqRewriteStatistics? stats,
        OutputIncrementalWriteSession outputSession)
    {
        if (stats == null)
        {
            return;
        }

        var report = new System.Text.Json.Nodes.JsonObject
        {
            ["desugaredQueryCount"] = stats.DesugaredQueryCount,
            ["rewrittenChainCount"] = stats.RewrittenChainCount,
            ["rewrittenMethodCount"] = stats.RewrittenMethodCount,
            ["skippedChainCount"] = stats.SkippedChains.Count,
        };

        if (stats.SkippedChains.Count > 0)
        {
            var skippedArray = new System.Text.Json.Nodes.JsonArray();
            foreach (var skip in stats.SkippedChains)
            {
                skippedArray.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["reason"] = skip.Reason.ToString(),
                    ["line"] = skip.LineNumber,
                    ["method"] = skip.MethodName,
                    ["message"] = skip.Message,
                });
            }
            report["skippedChains"] = skippedArray;
        }

        var uncovered = stats.GetUncoveredOperatorsByFrequency();
        if (uncovered.Count > 0)
        {
            var uncoveredArray = new System.Text.Json.Nodes.JsonArray();
            foreach (var kv in uncovered)
            {
                uncoveredArray.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["operator"] = kv.Key,
                    ["occurrences"] = kv.Value,
                });
            }
            report["uncoveredOperators"] = uncoveredArray;
        }

        var json = report.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var reportPath = Path.Combine(destinationRoot, "cs2j-linq-report.json");
        outputSession.WriteTextFile(reportPath, json, OutputIncrementalEntryKind.Report);
    }

    private static void MergeLinqStatistics(
        ref CSharpToJava.Core.LinqRewrite.LinqRewriteStatistics? aggregated,
        CSharpToJava.Core.LinqRewrite.LinqRewriteStatistics? incoming)
    {
        if (incoming == null)
        {
            return;
        }

        aggregated ??= new CSharpToJava.Core.LinqRewrite.LinqRewriteStatistics();
        aggregated.MergeFrom(incoming);
    }

    private static bool TryReusePreviousProjectOutputs(
        string destinationRoot,
        InputFingerprintSnapshot currentSnapshot,
        bool verbose)
    {
        var snapshotPath = Path.Combine(destinationRoot, InputFingerprintSnapshot.FileName);
        var outputManifestPath = Path.Combine(destinationRoot, OutputIncrementalWriteSession.ManifestFileName);
        if (!File.Exists(snapshotPath) || !File.Exists(outputManifestPath))
        {
            return false;
        }

        try
        {
            var serializer = new InputFingerprintSnapshotJsonSerializer();
            var previousSnapshot = serializer.Deserialize(File.ReadAllText(snapshotPath));
            if (!currentSnapshot.Matches(previousSnapshot))
            {
                return false;
            }

            Console.WriteLine("Inputs unchanged; reusing existing outputs.");
            if (verbose)
            {
                Console.WriteLine($"Input fingerprint matched: {snapshotPath}");
            }

            return true;
        }
        catch (Exception ex)
        {
            if (verbose)
            {
                Console.WriteLine($"Failed to evaluate input fingerprint snapshot, continuing with conversion: {ex.Message}");
            }

            return false;
        }
    }

    private static InputFingerprintSnapshot BuildWorkspaceInputFingerprintSnapshot(
        ConvertProjectOptions opts,
        IReadOnlyList<WorkspaceProject> projects)
    {
        var commonRoot = GetCommonRoot(projects.Select(project => project.Directory).Append(ResolveInputRoot(opts.Source)));
        return BuildInputFingerprintSnapshot(opts, EnumerateInputFiles(commonRoot, opts.Destination));
    }

    private static InputFingerprintSnapshot BuildProjectGraphInputFingerprintSnapshot(
        ConvertProjectOptions opts,
        ProjectGraph graph)
    {
        var commonRoot = GetCommonRoot(graph.ProjectsInTopologicalOrder.Select(project => project.ProjectDirectory).Append(ResolveInputRoot(opts.Source)));
        return BuildInputFingerprintSnapshot(opts, EnumerateInputFiles(commonRoot, opts.Destination));
    }

    private static InputFingerprintSnapshot BuildManualInputFingerprintSnapshot(ConvertProjectOptions opts)
    {
        var sourceRoot = ResolveInputRoot(opts.Source);
        return BuildInputFingerprintSnapshot(opts, EnumerateInputFiles(sourceRoot, opts.Destination));
    }

    /// <summary>
    /// Sorts workspace projects in topological order (dependencies first) so that
    /// factory methods and other cross-project state registered during dependency
    /// conversion are available when converting dependent projects.
    /// </summary>
    private static IReadOnlyList<WorkspaceProject> SortProjectsTopologically(
        IReadOnlyList<WorkspaceProject> projects)
    {
        var projectByPath = projects.ToDictionary(
            p => Path.GetFullPath(p.FilePath),
            p => p,
            StringComparer.OrdinalIgnoreCase);

        var visited = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new List<WorkspaceProject>();

        foreach (var project in projects)
        {
            Visit(project);
        }

        return result;

        void Visit(WorkspaceProject project)
        {
            var path = Path.GetFullPath(project.FilePath);
            if (visited.TryGetValue(path, out var state))
            {
                if (state == 2) return; // Already fully processed
                // state == 1 means cycle — break it gracefully
                return;
            }

            visited[path] = 1; // Currently visiting

            // Visit dependencies first
            foreach (var refPath in project.ProjectReferences)
            {
                var fullPath = Path.GetFullPath(refPath);
                if (projectByPath.TryGetValue(fullPath, out var depProject))
                {
                    Visit(depProject);
                }
            }

            visited[path] = 2; // Fully processed
            result.Add(project);
        }
    }

    private static ProjectGraph FilterUnsupportedProjectGraph(ProjectGraph graph, bool verbose)
    {
        var exclusions = ProjectConversionExclusionPlanner.Plan(graph);
        if (exclusions.Count == 0)
        {
            return graph;
        }

        WriteProjectExclusionSummary(exclusions.Values, verbose);

        var includedPaths = new HashSet<string>(
            graph.ProjectsInTopologicalOrder
                .Select(project => Path.GetFullPath(project.ProjectFilePath))
                .Where(path => !exclusions.ContainsKey(path)),
            StringComparer.OrdinalIgnoreCase);

        var filteredProjects = graph.ProjectsInTopologicalOrder
            .Where(project => includedPaths.Contains(Path.GetFullPath(project.ProjectFilePath)))
            .Select(project => new DiscoveredProject
            {
                Name = project.Name,
                ProjectFilePath = project.ProjectFilePath,
                ProjectDirectory = project.ProjectDirectory,
                Kind = project.Kind,
                ProjectReferences = project.ProjectReferences
                    .Select(Path.GetFullPath)
                    .Where(includedPaths.Contains)
                    .ToList(),
                ResourceItems = project.ResourceItems,
            })
            .ToList();

        return new ProjectGraph
        {
            RootProjectPath = graph.RootProjectPath,
            ProjectsInTopologicalOrder = filteredProjects,
        };
    }

    private static IReadOnlyList<WorkspaceProject> NormalizeWorkspaceProjects(
        IReadOnlyList<WorkspaceProject> projects,
        bool verbose)
    {
        var groupedProjects = projects
            .GroupBy(project => Path.GetFullPath(project.FilePath), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (groupedProjects.All(group => group.Count() == 1))
        {
            return projects;
        }

        var normalizedProjects = new List<WorkspaceProject>(groupedProjects.Count);
        foreach (var group in groupedProjects)
        {
            var selectedProject = group.First();
            normalizedProjects.Add(selectedProject);

            if (verbose && group.Count() > 1)
            {
                var names = string.Join(", ", group.Select(project => project.Name));
                Console.WriteLine($"Collapsing multi-target workspace project '{selectedProject.FilePath}' to '{selectedProject.Name}' from [{names}]");
            }
        }

        return normalizedProjects;
    }

    private static IReadOnlyList<WorkspaceProject> FilterUnsupportedWorkspaceProjects(
        IReadOnlyList<WorkspaceProject> projects,
        bool verbose)
    {
        var exclusions = ProjectConversionExclusionPlanner.Plan(projects);
        if (exclusions.Count == 0)
        {
            return projects;
        }

        WriteProjectExclusionSummary(exclusions.Values, verbose);

        var includedPaths = new HashSet<string>(
            projects
                .Select(project => Path.GetFullPath(project.FilePath))
                .Where(path => !exclusions.ContainsKey(path)),
            StringComparer.OrdinalIgnoreCase);

        return projects
            .Where(project => includedPaths.Contains(Path.GetFullPath(project.FilePath)))
            .Select(project => new WorkspaceProject
            {
                Name = project.Name,
                FilePath = project.FilePath,
                Directory = project.Directory,
                Compilation = project.Compilation,
                Documents = project.Documents,
                ProjectReferences = project.ProjectReferences
                    .Select(Path.GetFullPath)
                    .Where(includedPaths.Contains)
                    .ToList(),
                IsTestProject = project.IsTestProject,
                ResourceItems = project.ResourceItems,
            })
            .ToList();
    }

    private static void WriteProjectExclusionSummary(
        IEnumerable<ProjectConversionExclusion> exclusions,
        bool verbose)
    {
        var orderedExclusions = exclusions
            .OrderBy(exclusion => exclusion.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Console.WriteLine($"Skipping {orderedExclusions.Count} project(s) excluded by platform rules.");
        if (!verbose)
        {
            return;
        }

        foreach (var exclusion in orderedExclusions)
        {
            Console.WriteLine($"  Skipped: {exclusion.ProjectName} ({exclusion.Reason})");
        }
    }

    private static InputFingerprintSnapshot BuildInputFingerprintSnapshot(
        ConvertProjectOptions opts,
        IReadOnlyList<string> inputFiles)
    {
        return InputFingerprintSnapshotBuilder.Build(new InputFingerprintBuildRequest
        {
            SourceName = GetSourceName(opts.Source),
            SourcePath = opts.Source,
            InputFilePaths = inputFiles,
            OptionTokens = GetProjectConversionOptionTokens(opts),
            TemplateFilePaths = GeneratedProjectAssets.GetTemplateAssetPaths(),
            ToolAssemblyPaths = GetToolAssemblyPaths(),
            MappingConfigPath = opts.MappingConfig,
        });
    }

    private static IReadOnlyList<string> GetProjectConversionOptionTokens(ConvertProjectOptions opts)
    {
        return new List<string>
        {
            $"java-version={opts.JavaVersion}",
            $"mapping-config={(string.IsNullOrWhiteSpace(opts.MappingConfig) ? "<default>" : Path.GetFullPath(opts.MappingConfig))}",
            $"use-records={opts.UseRecords}",
            $"use-optional={opts.UseOptionalForNullable}",
            $"generate-javadoc={opts.GenerateJavaDoc}",
            $"enable-linq-rewrite={opts.EnableLinqRewrite}",
            $"prefer-stream-api={opts.PreferStreamApi?.ToString() ?? "default"}",
            $"maven-group-id={opts.MavenGroupId}",
            $"maven-version={opts.MavenVersion}",
            $"include-tests={opts.IncludeTests}",
        };
    }

    private static IReadOnlyList<string> GetToolAssemblyPaths()
    {
        return new[]
        {
            typeof(Program).Assembly.Location,
            typeof(ConversionPipeline).Assembly.Location,
            typeof(ProjectConversionPipeline).Assembly.Location,
            typeof(SolutionLoader).Assembly.Location,
        }
        .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
        .Select(Path.GetFullPath)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
    }

    private static IReadOnlyList<string> EnumerateInputFiles(string rootPath, string destinationRoot)
    {
        if (File.Exists(rootPath))
        {
            return new[] { Path.GetFullPath(rootPath) };
        }

        var fullRootPath = Path.GetFullPath(rootPath);
        var fullDestinationPath = Path.GetFullPath(destinationRoot);
        var ignoredDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git",
            ".vs",
            ".idea",
            "bin",
            "obj",
            "node_modules",
        };

        var files = new List<string>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(fullRootPath);

        while (pendingDirectories.Count > 0)
        {
            var currentDirectory = pendingDirectories.Pop();
            if (IsSameOrDescendantPath(currentDirectory, fullDestinationPath))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(currentDirectory))
            {
                files.Add(Path.GetFullPath(file));
            }

            foreach (var subDirectory in Directory.EnumerateDirectories(currentDirectory))
            {
                if (ignoredDirectoryNames.Contains(Path.GetFileName(subDirectory)))
                {
                    continue;
                }

                if (IsSameOrDescendantPath(subDirectory, fullDestinationPath))
                {
                    continue;
                }

                pendingDirectories.Push(subDirectory);
            }
        }

        return files
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ResolveInputRoot(string sourcePath)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        return File.Exists(fullPath)
            ? Path.GetDirectoryName(fullPath) ?? fullPath
            : fullPath;
    }

    /// <summary>
    /// Auto-discovers the Java standard-library metadata directory (<c>config/java/</c>)
    /// as a sibling of the type mapping configuration file.
    /// </summary>
    private static string? ResolveJavaMetadataPath(string? mappingConfig)
    {
        // Determine the config base directory from the mapping config path
        string configDir;
        if (!string.IsNullOrEmpty(mappingConfig))
        {
            var fullPath = Path.IsPathRooted(mappingConfig) ? mappingConfig : Path.Combine(Directory.GetCurrentDirectory(), mappingConfig);
            configDir = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        }
        else
        {
            configDir = Path.Combine(Directory.GetCurrentDirectory(), "config");
        }

        var javaDir = Path.Combine(configDir, "java");
        return Directory.Exists(Path.Combine(javaDir, "java.base")) ? javaDir : null;
    }

    private static string GetCommonRoot(IEnumerable<string> paths)
    {
        var normalizedPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedPaths.Count == 0)
        {
            return Path.GetFullPath(".");
        }

        var commonRoot = normalizedPaths[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (normalizedPaths.Any(path => !IsSameOrDescendantPath(path, commonRoot)))
        {
            var parent = Directory.GetParent(commonRoot);
            if (parent == null)
            {
                break;
            }

            commonRoot = parent.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return commonRoot;
    }

    private static bool IsSameOrDescendantPath(string path, string candidateAncestor)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullAncestor = Path.GetFullPath(candidateAncestor).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(fullPath, fullAncestor, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var ancestorWithSeparator = fullAncestor + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(ancestorWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddProjectPassProfileEntry(
        List<PassProfileEntry> entries,
        IReadOnlyList<Cs2jPassMetric> passMetrics,
        IEnumerable<ConversionResult> results,
        string? projectName,
        string? moduleName = null)
    {
        var entry = PassProfileEntryBuilder.CreateProjectEntry(passMetrics, results, projectName, moduleName);
        if (entry != null)
        {
            entries.Add(entry);
        }
    }

    private static string GetSourceName(string sourcePath)
    {
        var fullPath = Path.GetFullPath(sourcePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (File.Exists(fullPath))
        {
            return Path.GetFileNameWithoutExtension(fullPath);
        }

        return Path.GetFileName(fullPath);
    }

    private static JavaModulePlan PlannedModuleToJavaModulePlan(
        PlannedModule module,
        string groupId,
        IReadOnlyList<string>? requiredCompatPacks = null,
        IReadOnlyList<JavaRuntimeBridgeRequirement>? requiredRuntimeBridges = null)
    {
        var deps = new List<JavaDependency>(WorkspacePlanBuilder.DefaultDependenciesForModule(
            groupId,
            module.Name));

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
            RequiredRuntimeBridges = requiredRuntimeBridges ?? [],
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

    [Option("no-parallel-project-passes", Default = false, HelpText = "Disable parallel execution for safe project-level tree-local passes")]
    public bool NoParallelProjectPasses { get; set; }

    // 便捷属性
    public bool UseRecords => !NoRecords;
    public bool GenerateJavaDoc => !NoJavaDoc;
    public bool EnableLinqRewrite => !NoLinqRewrite;
    public bool EnableParallelProjectPasses => !NoParallelProjectPasses;

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

    [Option("no-cache", Default = true, HelpText = "Disable fingerprint-based caching of previous outputs")]
    public bool NoCache { get; set; }

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

    [Option("maven-group-id", Default = "io.github.ningpp", HelpText = "Maven groupId")]
    public string MavenGroupId { get; set; } = "io.github.ningpp";

    [Option("maven-version", Default = "0.0.1-SNAPSHOT", HelpText = "Maven version")]
    public string MavenVersion { get; set; } = "0.0.1-SNAPSHOT";

    [Option("include-tests", Default = true, HelpText = "Include discovered test projects and write them to src/test/java")]
    public bool IncludeTests { get; set; } = true;

    [Option("linq-report", Default = false, HelpText = "Output LINQ preprocessing report (rewrite stats, skipped chains, uncovered operators)")]
    public bool LinqReport { get; set; }

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
