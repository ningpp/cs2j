using CSharpToJava.Core.ReadOnlyStructMaker;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI;

internal sealed class ProjectReadOnlyPreprocessRequest
{
    public required string SourcePath { get; init; }
    public required string DestinationRoot { get; init; }
    public bool Force { get; init; } = true;
    public bool Verbose { get; init; }
    public ReadOnlyStructMakerOptions MakerOptions { get; init; } = new();

    /// <summary>
    /// When true (default) the preprocessor first scans the project graph and only
    /// copies/transforms projects the converter supports, rewriting the copied .sln so
    /// unsupported (e.g. WPF/UWP/WinForms) projects are dropped.
    /// </summary>
    public bool FilterUnsupportedProjects { get; init; } = true;

    /// <summary>Run <c>dotnet build</c> on the readonly output to verify it compiles.</summary>
    public bool VerifyBuild { get; init; } = true;

    /// <summary>Run <c>dotnet test</c> on the readonly output (only when test projects exist).</summary>
    public bool VerifyTests { get; init; } = true;
}

internal sealed class ProjectReadOnlyPreprocessResult
{
    public required string OriginalSourcePath { get; init; }
    public required string SourceRoot { get; init; }
    public required string IntermediateRoot { get; init; }
    public required string PreprocessedSourcePath { get; init; }
    public required bool Success { get; init; }
    public required ReadOnlyStructMakerStatistics Statistics { get; init; }
    public required IReadOnlyList<ReadOnlyStructMakerDiagnostic> Diagnostics { get; init; }
}

internal static class ProjectReadOnlyStructPreprocessor
{
    public const string IntermediateDirectoryName = ".cs2j-readonly-src";

    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", "TestResults",
    };

    public static async Task<ProjectReadOnlyPreprocessResult> PreprocessAsync(ProjectReadOnlyPreprocessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationRoot);

        var layout = ResolveSourceLayout(request.SourcePath);
        var intermediateRoot = Path.Combine(Path.GetFullPath(request.DestinationRoot), IntermediateDirectoryName);

        if (Directory.Exists(intermediateRoot))
        {
            if (!request.Force)
            {
                throw new InvalidOperationException(
                    $"Readonly source directory already exists: {intermediateRoot}. Use --force to overwrite.");
            }

            Directory.Delete(intermediateRoot, recursive: true);
        }

        Directory.CreateDirectory(intermediateRoot);

        var diagnostics = new List<ReadOnlyStructMakerDiagnostic>();
        var statistics = new ReadOnlyStructMakerStatistics();
        var maker = new ReadOnlyStructMaker();
        var sourceRoot = layout.SourceRoot;
        var destinationRoot = Path.GetFullPath(request.DestinationRoot);

        // Strategy: before transforming, scan the project graph to determine which
        // projects the converter supports, then copy ONLY those projects (preserving the
        // directory structure) and rewrite the copied .sln to drop the unsupported ones.
        var projectGraph = TryLoadProjectGraph(layout, request.Verbose);
        var exclusionInfo = request.FilterUnsupportedProjects
            ? SupportedProjectFiltering.ComputeExclusions(projectGraph, request.Verbose)
            : new SupportedProjectFiltering.ExclusionInfo();

        // Copy project files (.csproj, .sln) to the intermediate directory, skipping the
        // project files of excluded projects and rewriting the solution file.
        foreach (var projectFile in layout.ProjectFiles)
        {
            if (SupportedProjectFiltering.IsUnderExcludedProject(projectFile, exclusionInfo))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(sourceRoot, projectFile);
            var destinationFile = Path.Combine(intermediateRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);

            if (exclusionInfo.HasExclusions &&
                projectFile.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                var solutionContent = await File.ReadAllTextAsync(projectFile);
                var rewrittenSolution = SupportedProjectFiltering.RewriteSolutionContent(
                    solutionContent,
                    Path.GetDirectoryName(projectFile) ?? sourceRoot,
                    exclusionInfo);
                await File.WriteAllTextAsync(destinationFile, rewrittenSolution, new System.Text.UTF8Encoding(false));
            }
            else
            {
                File.Copy(projectFile, destinationFile, overwrite: true);
            }
        }

        // Collect all .cs files and parse them into syntax trees for compilation.
        // Files that belong to excluded (unsupported) projects are not copied/transformed.
        var sourceFiles = EnumerateSourceFiles(sourceRoot, intermediateRoot)
            .Where(file => !SupportedProjectFiltering.IsUnderExcludedProject(file, exclusionInfo))
            .ToList();
        var csFiles = sourceFiles
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Parse all C# files into syntax trees
        var syntaxTrees = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
        foreach (var csFile in csFiles)
        {
            var sourceCode = await File.ReadAllTextAsync(csFile);
            var tree = CSharpSyntaxTree.ParseText(sourceCode, path: csFile);
            syntaxTrees[csFile] = tree;
        }

        // Create a compilation with all trees for semantic analysis
        var references = GetBasicReferences();
        var compilation = CSharpCompilation.Create(
            "readonly-preprocess",
            syntaxTrees.Values,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true));

        // Process each .cs file
        var globalMigratedMethods = new Dictionary<string, string>(); // methodName -> structName
        foreach (var sourceFile in sourceFiles)
        {
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            var destinationFile = Path.Combine(intermediateRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);

            if (!sourceFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                // The solution file was already copied (and rewritten to drop
                // unsupported projects) in the project-file loop above; copying it
                // again here would restore the unfiltered original.
                if (sourceFile.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                File.Copy(sourceFile, destinationFile, overwrite: true);
                continue;
            }

            if (!syntaxTrees.TryGetValue(sourceFile, out var tree))
            {
                File.Copy(sourceFile, destinationFile, overwrite: true);
                continue;
            }

            var semanticModel = compilation.GetSemanticModel(tree);
            var result = maker.MakeReadOnly(tree, semanticModel, request.MakerOptions);

            MergeStatistics(statistics, result.Statistics);

            foreach (var diagnostic in result.Diagnostics)
            {
                diagnostics.Add(diagnostic with { FilePath = relativePath });
            }

            // Collect migrated void method names for cross-file call site update
            foreach (var diagnostic in result.Diagnostics)
            {
                if (diagnostic.Level == ConversionLevel.MethodMigrate && diagnostic.StructName != null)
                {
                    // We'll detect method names from the output code in the second pass
                    globalMigratedMethods[diagnostic.StructName] = diagnostic.StructName;
                }
            }

            if (!result.Changed)
            {
                File.Copy(sourceFile, destinationFile, overwrite: true);
            }
            else
            {
                await File.WriteAllTextAsync(destinationFile, result.OutputCode!, new System.Text.UTF8Encoding(false));
            }
        }

        // The readonly-struct rewrite relies on modern C# features (e.g. covariant
        // return types for interface implementations). Older target frameworks default
        // to old language versions, so pin the latest version in the intermediate builds.
        // Runs after the per-file copy loop because project files may also be copied there.
        EnsureLangVersionLatest(intermediateRoot);

        // The intermediate tree is built without assembly signing. When a project grants
        // InternalsVisibleTo with a PublicKey but nothing is actually signed, the compiler
        // rejects the unsigned friend assembly (CS0281), which also hides internal virtual
        // members from test overrides (CS0115). Strip the PublicKey in that case.
        NormalizeFriendAssemblyAccess(intermediateRoot);

        // Second pass: update call sites across all files for migrated methods
        if (globalMigratedMethods.Count > 0 && request.MakerOptions.UpdateCallSites)
        {
            await UpdateCrossFileCallSites(intermediateRoot, sourceRoot, sourceFiles, globalMigratedMethods, request);
        }

        // Third pass: update cross-file read accesses for L4 public field conversions
        if (request.MakerOptions.EnablePublicFieldConversion)
        {
            await UpdateCrossFileFieldReads(intermediateRoot, sourceRoot, sourceFiles, request);
        }

        // Third-and-a-half pass: hoist ref/out arguments that target readonly struct
        // fields into temps with WithXxx write-back (CS0192).
        await HoistRefReadonlyFieldArguments(intermediateRoot, sourceRoot, sourceFiles);

        // Third-and-three-quarter pass: interfaces whose void mutating methods are
        // implemented by migrated struct-returning methods get their return type
        // changed to the interface itself (C# 9 covariant returns); call sites through
        // the interface are rewritten to reassign the receiver, and the now-unneeded
        // explicit interface implementations are removed.
        await TransformMigratedInterfaces(intermediateRoot, sourceRoot, sourceFiles);

        // Companion pass: concrete types (typically classes) that implement the
        // transformed interface members must widen their return types too. Classes
        // mutate in place, so their void implementations simply start returning
        // `this` — preserving reference semantics exactly.
        await FixConcreteInterfaceImplementers(intermediateRoot, sourceRoot, sourceFiles);

        // Final interface-compat step: C# only allows covariant returns through
        // EXPLICIT interface implementations, so every implementer of a transformed
        // interface needs a bridge explicit implementation delegating to the concrete
        // method. Structs received theirs from the widener above; classes get them here.
        await AddClassInterfaceBridges(intermediateRoot, sourceRoot, sourceFiles);

        // Fourth pass: normalize constructors of readonly structs so each field is
        // assigned exactly once (Java final fields allow a single assignment). Runs
        // last because earlier call-site passes can introduce repeated assignments.
        await NormalizeReadOnlyStructConstructors(intermediateRoot, sourceRoot, sourceFiles);

        // Fifth pass: structs inside inactive preprocessor regions (#if) are invisible
        // to Roslyn's syntax trees and never compiled, but mark them readonly too so
        // literally every struct declaration in the output is a readonly struct.
        await MarkDisabledRegionStructsReadonly(intermediateRoot, sourceRoot, sourceFiles);

        // Restore NuGet packages in the intermediate directory
        var restoreTarget = ResolveRestoreTarget(intermediateRoot, layout);
        if (restoreTarget != null)
        {
            if (request.Verbose)
            {
                Console.WriteLine($"Restoring packages: {restoreTarget}");
            }
            var restoreExitCode = RunDotNetRestore(restoreTarget);
            if (request.Verbose)
            {
                Console.WriteLine($"Restore exit code: {restoreExitCode}");
            }
            if (restoreExitCode != 0)
            {
                diagnostics.Add(new ReadOnlyStructMakerDiagnostic(
                    ReadOnlyStructSeverity.Warning, "<project>",
                    $"dotnet restore failed with exit code {restoreExitCode}. " +
                    "Package references may not be resolved.",
                    null, null));
            }

            // Verify that the readonly-transformed C# still builds (and its tests pass).
            // This is the hard correctness gate for the readonly preprocessing step.
            await VerifyBuildAndTestsAsync(restoreTarget, projectGraph, exclusionInfo, request, diagnostics);
        }

        return new ProjectReadOnlyPreprocessResult
        {
            OriginalSourcePath = layout.EntryPath,
            SourceRoot = sourceRoot,
            IntermediateRoot = intermediateRoot,
            PreprocessedSourcePath = MapToIntermediate(layout, intermediateRoot),
            Success = !diagnostics.Any(d => d.Severity == ReadOnlyStructSeverity.Error),
            Statistics = statistics,
            Diagnostics = diagnostics,
        };
    }

    private static List<MetadataReference> GetBasicReferences()
    {
        var references = new List<MetadataReference>();

        // Add core runtime references
        var trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?.Split(Path.PathSeparator);
        if (trustedAssemblies != null)
        {
            foreach (var assembly in trustedAssemblies)
            {
                if (File.Exists(assembly))
                {
                    references.Add(MetadataReference.CreateFromFile(assembly));
                }
            }
        }
        else
        {
            // Fallback: add basic references
            references.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
            var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            var runtimeAssembly = Path.Combine(runtimeDir, "System.Runtime.dll");
            if (File.Exists(runtimeAssembly))
                references.Add(MetadataReference.CreateFromFile(runtimeAssembly));
        }

        return references;
    }

    private static IEnumerable<string> EnumerateSourceFiles(string sourceRoot, string destinationRoot)
    {
        var pending = new Stack<string>();
        pending.Push(sourceRoot);

        while (pending.Count > 0)
        {
            var currentDirectory = pending.Pop();
            if (IsSameOrDescendantPath(currentDirectory, destinationRoot))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(currentDirectory))
            {
                yield return Path.GetFullPath(file);
            }

            foreach (var subDirectory in Directory.EnumerateDirectories(currentDirectory))
            {
                if (IgnoredDirectoryNames.Contains(Path.GetFileName(subDirectory)))
                {
                    continue;
                }

                if (IsSameOrDescendantPath(subDirectory, destinationRoot))
                {
                    continue;
                }

                pending.Push(Path.GetFullPath(subDirectory));
            }
        }
    }

    private static SourceLayout ResolveSourceLayout(string sourcePath)
    {
        var fullSourcePath = Path.GetFullPath(sourcePath);

        // Case 1: Source is a .sln file
        if (File.Exists(fullSourcePath) && Path.GetExtension(fullSourcePath).Equals(".sln", StringComparison.OrdinalIgnoreCase))
        {
            var graph = ProjectDiscovery.LoadProjectGraph(fullSourcePath);
            var sourceRoot = GetCommonRoot(graph.ProjectsInTopologicalOrder.Select(p => p.ProjectDirectory));
            var projectFiles = graph.ProjectsInTopologicalOrder
                .Select(p => p.ProjectFilePath)
                .Append(fullSourcePath)
                .ToList();
            return new SourceLayout(sourceRoot, fullSourcePath, projectFiles);
        }

        // Case 2: Source is a .csproj file
        if (File.Exists(fullSourcePath) && Path.GetExtension(fullSourcePath).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            var graph = ProjectDiscovery.LoadProjectGraph(fullSourcePath);
            var sourceRoot = GetCommonRoot(graph.ProjectsInTopologicalOrder.Select(p => p.ProjectDirectory));
            var projectFiles = graph.ProjectsInTopologicalOrder
                .Select(p => p.ProjectFilePath)
                .ToList();
            return new SourceLayout(sourceRoot, fullSourcePath, projectFiles);
        }

        // Case 3: Source is a directory
        if (Directory.Exists(fullSourcePath))
        {
            if (ProjectDiscovery.TryResolveProjectEntry(fullSourcePath, out var entryProjectPath))
            {
                var graph = ProjectDiscovery.LoadProjectGraph(entryProjectPath);
                var sourceRoot = GetCommonRoot(graph.ProjectsInTopologicalOrder.Select(p => p.ProjectDirectory));
                var projectFiles = graph.ProjectsInTopologicalOrder
                    .Select(p => p.ProjectFilePath)
                    .ToList();

                if (Path.GetExtension(entryProjectPath).Equals(".sln", StringComparison.OrdinalIgnoreCase))
                {
                    projectFiles.Add(entryProjectPath);
                }
                else
                {
                    var slnDirs = new[] { sourceRoot, fullSourcePath };
                    foreach (var searchDir in slnDirs)
                    {
                        if (!Directory.Exists(searchDir)) continue;
                        var slnInDir = Directory.GetFiles(searchDir, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
                        if (slnInDir != null)
                        {
                            projectFiles.Add(slnInDir);
                            break;
                        }
                    }
                }

                return new SourceLayout(sourceRoot, fullSourcePath, projectFiles);
            }

            return new SourceLayout(fullSourcePath, fullSourcePath, Array.Empty<string>());
        }

        throw new FileNotFoundException("Source path not found.", fullSourcePath);
    }

    private static string MapToIntermediate(SourceLayout layout, string intermediateRoot)
    {
        if (layout.ProjectFiles.Count > 0 && Directory.Exists(layout.EntryPath))
        {
            return intermediateRoot;
        }

        if (File.Exists(layout.EntryPath))
        {
            return Path.Combine(intermediateRoot, Path.GetRelativePath(layout.SourceRoot, layout.EntryPath));
        }

        if (string.Equals(layout.SourceRoot, layout.EntryPath, StringComparison.OrdinalIgnoreCase))
        {
            return intermediateRoot;
        }

        return Path.Combine(intermediateRoot, Path.GetRelativePath(layout.SourceRoot, layout.EntryPath));
    }

    private static string? ResolveRestoreTarget(string intermediateRoot, SourceLayout layout)
    {
        var preprocessedPath = MapToIntermediate(layout, intermediateRoot);
        if (File.Exists(preprocessedPath) &&
            (preprocessedPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
             preprocessedPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            return preprocessedPath;
        }

        if (Directory.Exists(preprocessedPath))
        {
            var sln = Directory.GetFiles(preprocessedPath, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (sln != null) return sln;

            var csproj = Directory.GetFiles(preprocessedPath, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (csproj != null) return csproj;
        }

        return null;
    }

    private static int RunDotNetRestore(string targetPath)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"restore \"{targetPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null)
            {
                Console.Error.WriteLine("Failed to start dotnet restore process.");
                return -1;
            }
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                var output = outputTask.Result;
                var error = errorTask.Result;
                Console.Error.WriteLine($"dotnet restore exited with code {process.ExitCode} for {targetPath}");
                if (!string.IsNullOrWhiteSpace(output))
                    Console.Error.WriteLine($"stdout: {output}");
                if (!string.IsNullOrWhiteSpace(error))
                    Console.Error.WriteLine($"stderr: {error}");
            }
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"dotnet restore failed: {ex.Message}");
            return -1;
        }
    }

    private static ProjectGraph? TryLoadProjectGraph(SourceLayout layout, bool verbose)
    {
        if (layout.ProjectFiles.Count == 0)
        {
            return null;
        }

        try
        {
            if (ProjectDiscovery.TryResolveProjectEntry(layout.EntryPath, out var entryPath))
            {
                return ProjectDiscovery.LoadProjectGraph(entryPath);
            }
        }
        catch (Exception ex)
        {
            if (verbose)
            {
                Console.Error.WriteLine($"Warning: could not load the project graph for support filtering: {ex.Message}");
            }
        }

        return null;
    }

    private static async Task VerifyBuildAndTestsAsync(
        string restoreTarget,
        ProjectGraph? projectGraph,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        ProjectReadOnlyPreprocessRequest request,
        List<ReadOnlyStructMakerDiagnostic> diagnostics)
    {
        if (!request.VerifyBuild && !request.VerifyTests)
        {
            return;
        }

        Console.WriteLine($"[make-readonly] Verifying build: dotnet build \"{restoreTarget}\"");
        var buildExit = await RunDotNetCommandAsync("build", restoreTarget, request.Verbose);
        if (buildExit != 0)
        {
            diagnostics.Add(new ReadOnlyStructMakerDiagnostic(
                ReadOnlyStructSeverity.Error, "<project>",
                $"dotnet build failed with exit code {buildExit} after readonly transformation. " +
                "The readonly-struct rewrite produced code that does not compile.",
                null, null));
            return;
        }

        if (request.VerifyTests && HasSupportedTestProjects(projectGraph, exclusionInfo))
        {
            Console.WriteLine($"[make-readonly] Verifying tests: dotnet test \"{restoreTarget}\"");
            var testExit = await RunDotNetCommandAsync("test", restoreTarget, request.Verbose);
            if (testExit != 0)
            {
                diagnostics.Add(new ReadOnlyStructMakerDiagnostic(
                    ReadOnlyStructSeverity.Error, "<project>",
                    $"dotnet test failed with exit code {testExit} after readonly transformation.",
                    null, null));
            }
        }
    }

    private static bool HasSupportedTestProjects(
        ProjectGraph? projectGraph,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo)
    {
        if (projectGraph == null)
        {
            return false;
        }

        return projectGraph.ProjectsInTopologicalOrder.Any(project =>
            project.Kind == ProjectKind.Test &&
            !exclusionInfo.ExcludedProjectPaths.Contains(Path.GetFullPath(project.ProjectFilePath)));
    }

    private static async Task<int> RunDotNetCommandAsync(string verb, string targetPath, bool verbose)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"{verb} \"{targetPath}\" --nologo",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null)
            {
                Console.Error.WriteLine($"Failed to start dotnet {verb} process.");
                return -1;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await outputTask;
            var error = await errorTask;

            if (process.ExitCode != 0)
            {
                Console.Error.WriteLine($"dotnet {verb} exited with code {process.ExitCode} for {targetPath}");
                if (!string.IsNullOrWhiteSpace(output))
                    Console.Error.WriteLine(output);
                if (!string.IsNullOrWhiteSpace(error))
                    Console.Error.WriteLine(error);
            }
            else if (verbose)
            {
                Console.WriteLine($"dotnet {verb} succeeded for {targetPath}");
            }

            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"dotnet {verb} failed: {ex.Message}");
            return -1;
        }
    }

    private static void MergeStatistics(ReadOnlyStructMakerStatistics target, ReadOnlyStructMakerStatistics source)
    {
        target.StructsScanned += source.StructsScanned;
        target.StructsConverted += source.StructsConverted;
        target.StructsSkipped += source.StructsSkipped;
        target.StructsFailed += source.StructsFailed;
        target.Level0_Skipped += source.Level0_Skipped;
        target.Level1_DirectAdd += source.Level1_DirectAdd;
        target.Level2_PropertyConvert += source.Level2_PropertyConvert;
        target.Level3_DataContainer += source.Level3_DataContainer;
        target.Level5_MethodMigrate += source.Level5_MethodMigrate;
        target.MethodsMigrated += source.MethodsMigrated;
        target.CallSitesUpdated += source.CallSitesUpdated;
    }

    private static string GetCommonRoot(IEnumerable<string> paths)
    {
        var normalized = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Count == 0)
        {
            return Path.GetFullPath(".");
        }

        var commonRoot = normalized[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (normalized.Any(path => !IsSameOrDescendantPath(path, commonRoot)))
        {
            var parent = Directory.GetParent(commonRoot);
            if (parent == null) break;
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

        return fullPath.StartsWith(fullAncestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Second pass: updates call sites of migrated void→struct methods across all files.
    /// </summary>
    private static async Task UpdateCrossFileCallSites(
        string intermediateRoot, string sourceRoot, List<string> sourceFiles,
        Dictionary<string, string> migratedStructs, ProjectReadOnlyPreprocessRequest request)
    {
        // Parse all output .cs files from the intermediate directory
        var outputCsFiles = new List<string>();
        foreach (var sourceFile in sourceFiles)
        {
            if (!sourceFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            var outputFile = Path.Combine(intermediateRoot, relativePath);
            if (File.Exists(outputFile))
                outputCsFiles.Add(outputFile);
        }

        var outputTrees = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in outputCsFiles)
        {
            var code = await File.ReadAllTextAsync(file);
            var tree = CSharpSyntaxTree.ParseText(code, path: file);
            outputTrees[file] = tree;
        }

        // Find migrated method names by scanning struct declarations
        var migratedMethodToStruct = new Dictionary<string, string>();
        var outMigratedMethodToStruct = new Dictionary<string, string>();
        var migratedArities = new Dictionary<string, HashSet<int>>();
        var allMethodArities = new Dictionary<string, HashSet<int>>();
        foreach (var (_, tree) in outputTrees)
        {
            var root = tree.GetRoot();
            foreach (var structDecl in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.StructDeclarationSyntax>())
            {
                var structName = structDecl.Identifier.Text;
                if (!migratedStructs.ContainsKey(structName)) continue;

                // Find methods that return the struct type (migrated from void)
                foreach (var method in structDecl.Members.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>())
                {
                    if (method.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword)))
                        continue;

                    var name = method.Identifier.Text;
                    var arity = method.ParameterList.Parameters.Count;
                    var arityKey = structName + "." + name;

                    // Record every non-generated overload arity (WithXxx methods are
                    // generated helpers, not candidates at original call sites).
                    if (!name.StartsWith("With", StringComparison.Ordinal))
                    {
                        if (!allMethodArities.TryGetValue(arityKey, out var aset))
                            allMethodArities[arityKey] = aset = new HashSet<int>();
                        aset.Add(arity);
                    }

                    if (method.ReturnType is Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax returnTypeId &&
                        returnTypeId.Identifier.Text == structName)
                    {
                        migratedMethodToStruct[name] = structName;
                        if (!migratedArities.TryGetValue(arityKey, out var mset))
                            migratedArities[arityKey] = mset = new HashSet<int>();
                        mset.Add(arity);
                    }

                    // Find methods migrated with an out parameter (OtherReturnToOut):
                    // they carry `out <StructName> newStatus`.
                    foreach (var parameter in method.ParameterList.Parameters)
                    {
                        if (parameter.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.OutKeyword)) &&
                            parameter.Type is Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax outType &&
                            outType.Identifier.Text == structName)
                        {
                            outMigratedMethodToStruct[name] = structName;
                        }
                    }
                }
            }
        }

        if (migratedMethodToStruct.Count == 0 && outMigratedMethodToStruct.Count == 0) return;

        // Create compilation for semantic analysis
        var references = GetBasicReferences();
        var outputCompilation = CSharpCompilation.Create(
            "callsite-update",
            outputTrees.Values,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        // Apply CallSiteUpdater to each file
        foreach (var (file, tree) in outputTrees)
        {
            var root = tree.GetRoot();
            var model = outputCompilation.GetSemanticModel(tree);
            var updater = new CallSiteUpdater(migratedMethodToStruct, model,
                outMigratedMethodToStruct: outMigratedMethodToStruct,
                migratedArities: migratedArities,
                allMethodArities: allMethodArities);
            var newRoot = updater.Visit(root);
            if (newRoot != null && newRoot.ToFullString() != root.ToFullString())
            {
                await File.WriteAllTextAsync(file, newRoot.ToFullString(), new System.Text.UTF8Encoding(false));
            }
        }
    }

    /// <summary>
    /// Final pass: rewrites constructors of readonly structs so every instance field is
    /// assigned exactly once (shadow locals), keeping the intermediate C# compilable and
    /// the generated Java final-field assignments legal.
    /// </summary>
    private static async Task NormalizeReadOnlyStructConstructors(
        string intermediateRoot, string sourceRoot, List<string> sourceFiles)
    {
        foreach (var sourceFile in sourceFiles)
        {
            if (!sourceFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            var outputFile = Path.Combine(intermediateRoot, relativePath);
            if (!File.Exists(outputFile)) continue;

            var code = await File.ReadAllTextAsync(outputFile);
            var tree = CSharpSyntaxTree.ParseText(code, path: outputFile);
            var root = (Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax)tree.GetRoot();

            // Fast pre-check: skip files without readonly structs.
            if (!root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.StructDeclarationSyntax>()
                .Any(s => s.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ReadOnlyKeyword))))
                continue;

            var newRoot = CSharpToJava.Core.ReadOnlyStructMaker.ConstructorNormalizer.Normalize(root);
            if (newRoot.ToFullString() != root.ToFullString())
            {
                await File.WriteAllTextAsync(outputFile, newRoot.ToFullString(), new System.Text.UTF8Encoding(false));
            }
        }
    }

    /// <summary>
    /// Cross-file pass: updates read accesses (obj.Field → obj.getField()) for L4-converted
    /// struct fields in ALL files, not just the file containing the struct definition.
    /// </summary>
    private static async Task UpdateCrossFileFieldReads(
        string intermediateRoot, string sourceRoot, List<string> sourceFiles,
        ProjectReadOnlyPreprocessRequest request)
    {
        // Parse all output .cs files from the intermediate directory
        var outputCsFiles = new List<string>();
        foreach (var sourceFile in sourceFiles)
        {
            if (!sourceFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            var outputFile = Path.Combine(intermediateRoot, relativePath);
            if (File.Exists(outputFile))
                outputCsFiles.Add(outputFile);
        }

        var outputTrees = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in outputCsFiles)
        {
            var code = await File.ReadAllTextAsync(file);
            var tree = CSharpSyntaxTree.ParseText(code, path: file);
            outputTrees[file] = tree;
        }

        // Find L4-converted structs: those with private fields + getXxx() getter methods
        var publicFieldStructs = new Dictionary<string, HashSet<string>>();
        foreach (var (_, tree) in outputTrees)
        {
            var root = tree.GetRoot();
            foreach (var structDecl in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.StructDeclarationSyntax>())
            {
                var structName = structDecl.Identifier.Text;
                var privateFields = structDecl.Members
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax>()
                    .Where(f => f.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PrivateKeyword)))
                    .SelectMany(f => f.Declaration.Variables.Select(v => v.Identifier.Text))
                    .ToHashSet();

                if (privateFields.Count == 0) continue;

                // Verify getter methods exist for these fields
                var getterNames = structDecl.Members
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
                    .Where(m => m.Identifier.Text.StartsWith("get") && m.ParameterList.Parameters.Count == 0)
                    .Select(m => m.Identifier.Text[3..]) // strip "get" prefix
                    .ToHashSet();

                var convertedFields = privateFields.Where(f => getterNames.Contains(f)).ToHashSet();
                if (convertedFields.Count > 0)
                {
                    publicFieldStructs[structName] = convertedFields;
                }
            }
        }

        // Also detect L5-migrated structs: those with WithXxx() methods for property setters.
        // L5 migration converts public property setters to WithXxx() methods and removes setters.
        // Object initializers and external assignments using those setters must be updated.
        // L5 structs have public get-only properties (not private fields + getXxx() getters),
        // so the AssignmentRewriter must use property access for reads, not getXxx().
        var l5StructNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var withChainStructs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, tree) in outputTrees)
        {
            var root = tree.GetRoot();
            foreach (var structDecl in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.StructDeclarationSyntax>())
            {
                var structName = structDecl.Identifier.Text;
                var withMethodProps = structDecl.Members
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
                    .Where(m => m.Identifier.Text.StartsWith("With", StringComparison.Ordinal)
                                && m.ParameterList.Parameters.Count == 1
                                && m.ReturnType is Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax rt
                                && rt.Identifier.Text == structName)
                    .Select(m => m.Identifier.Text.Substring(4)) // strip "With" prefix
                    .ToHashSet();

                if (withMethodProps.Count > 0)
                {
                    withChainStructs.Add(structName);
                    // Only treat as L4 (getXxx read rewriting) when the struct truly has
                    // getXxx() getter methods for its PRIVATE fields. Methods merely named
                    // GetXxx (e.g. Rectangle.GetIntersection) must not trigger this.
                    var privateFieldNames = structDecl.Members
                        .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax>()
                        .Where(f => f.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PrivateKeyword)))
                        .SelectMany(f => f.Declaration.Variables.Select(v => v.Identifier.Text))
                        .ToHashSet(StringComparer.Ordinal);
                    var hasGetterMethods = structDecl.Members
                        .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
                        .Any(m => m.ParameterList.Parameters.Count == 0
                                  && m.Identifier.Text.StartsWith("get", StringComparison.Ordinal)
                                  && privateFieldNames.Contains(m.Identifier.Text.Substring(3)));
                    if (!hasGetterMethods)
                    {
                        l5StructNames.Add(structName);
                    }

                    if (publicFieldStructs.TryGetValue(structName, out var existing))
                        existing.UnionWith(withMethodProps);
                    else
                        publicFieldStructs[structName] = withMethodProps;
                }
            }
        }

        // Also detect L3-converted structs (DataContainer): structs with get-only auto-properties
        // and a constructor whose parameters match all property names. These structs need object
        // initializers (new S { Prop = v }) converted to constructor calls (new S(prop: v)).
        // Reads stay as property access (like L5) since there are no getXxx() methods.
        foreach (var (_, tree) in outputTrees)
        {
            var root = tree.GetRoot();
            foreach (var structDecl in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.StructDeclarationSyntax>())
            {
                var structName = structDecl.Identifier.Text;
                if (publicFieldStructs.ContainsKey(structName))
                    continue;

                // Find get-only auto-properties: { get; } with no setter, no body, no expression body
                var getOnlyProps = structDecl.Members
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax>()
                    .Where(p => p.AccessorList?.Accessors.Count == 1
                                && p.AccessorList.Accessors[0].IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.GetAccessorDeclaration)
                                && p.AccessorList.Accessors[0].Body == null
                                && p.AccessorList.Accessors[0].ExpressionBody == null)
                    .Select(p => p.Identifier.Text)
                    .ToHashSet();

                if (getOnlyProps.Count == 0)
                    continue;

                // Check if there's a constructor with parameters matching ALL property names
                // (case-insensitive: property "Direction" matches parameter "direction")
                var hasMatchingCtor = structDecl.Members
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ConstructorDeclarationSyntax>()
                    .Any(c =>
                    {
                        var paramNames = c.ParameterList.Parameters
                            .Select(p => p.Identifier.Text)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        return getOnlyProps.All(prop => paramNames.Contains(prop));
                    });

                if (hasMatchingCtor)
                {
                    publicFieldStructs[structName] = getOnlyProps;
                    l5StructNames.Add(structName);
                }
            }
        }

        if (publicFieldStructs.Count == 0) return;

        // Create compilation for semantic analysis
        var references = GetBasicReferences();
        var outputCompilation = CSharpCompilation.Create(
            "field-read-update",
            outputTrees.Values,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        // Apply AssignmentRewriter to each file (handles both read and write accesses)
        foreach (var (file, tree) in outputTrees)
        {
            var root = tree.GetRoot();
            var model = outputCompilation.GetSemanticModel(tree);
            var rewriter = new AssignmentRewriter(publicFieldStructs, model, l5StructNames, withChainStructs);
            rewriter.BuildVariableTypeMap(root);
            var newRoot = rewriter.Visit(root);
            if (newRoot != null && newRoot.ToFullString() != root.ToFullString())
            {
                await File.WriteAllTextAsync(file, newRoot.ToFullString(), new System.Text.UTF8Encoding(false));
            }
        }
    }

    /// <summary>
    /// Cross-file pass: replaces ref/out arguments that target fields of readonly structs
    /// (CS0192) with hoisted temps plus a WithXxx write-back after the call:
    /// <code>
    /// mult(ref t, ref p0.aRot);  →  var __cs2jRef0 = p0.aRot;
    ///                               mult(ref t, ref __cs2jRef0);
    ///                               p0 = p0.WithaRot(__cs2jRef0);
    /// </code>
    /// </summary>
    private static async Task HoistRefReadonlyFieldArguments(
        string intermediateRoot, string sourceRoot, List<string> sourceFiles)
    {
        var outputCsFiles = new List<string>();
        foreach (var sourceFile in sourceFiles)
        {
            if (!sourceFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            var outputFile = Path.Combine(intermediateRoot, relativePath);
            if (File.Exists(outputFile))
                outputCsFiles.Add(outputFile);
        }

        var outputTrees = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in outputCsFiles)
        {
            var code = await File.ReadAllTextAsync(file);
            outputTrees[file] = CSharpSyntaxTree.ParseText(code, path: file);
        }

        // Collect instance field names of readonly structs (all of them are readonly fields).
        var readonlyStructFields = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (_, tree) in outputTrees)
        {
            var root = tree.GetRoot();
            foreach (var structDecl in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.StructDeclarationSyntax>())
            {
                if (!structDecl.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ReadOnlyKeyword)))
                    continue;
                var fields = structDecl.Members
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax>()
                    .Where(f => !f.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword)
                                                     || m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ConstKeyword)))
                    .SelectMany(f => f.Declaration.Variables.Select(v => v.Identifier.Text))
                    .ToHashSet(StringComparer.Ordinal);
                if (fields.Count == 0) continue;
                var name = structDecl.Identifier.Text;
                if (readonlyStructFields.TryGetValue(name, out var existing))
                    existing.UnionWith(fields);
                else
                    readonlyStructFields[name] = fields;
            }
        }

        if (readonlyStructFields.Count == 0) return;

        var references = GetBasicReferences();
        var outputCompilation = CSharpCompilation.Create(
            "ref-hoist",
            outputTrees.Values,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        foreach (var (file, tree) in outputTrees)
        {
            var root = tree.GetRoot();
            var model = outputCompilation.GetSemanticModel(tree);
            var rewriter = new RefReadonlyFieldHoister(readonlyStructFields, model);
            var newRoot = rewriter.Visit(root);
            if (newRoot != null && newRoot.ToFullString() != root.ToFullString())
            {
                await File.WriteAllTextAsync(file, newRoot.ToFullString(), new System.Text.UTF8Encoding(false));
            }
        }
    }

    /// <summary>
    /// Rewrites statement-level invocations whose ref/out arguments target readonly struct
    /// fields into hoisted temp + call + WithXxx write-back. Constructors are skipped
    /// (ref field access is legal there).
    /// </summary>
    private sealed class RefReadonlyFieldHoister : Microsoft.CodeAnalysis.CSharp.CSharpSyntaxRewriter
    {
        private readonly Dictionary<string, HashSet<string>> _readonlyStructFields;
        private readonly SemanticModel _model;
        private int _counter;

        public RefReadonlyFieldHoister(Dictionary<string, HashSet<string>> readonlyStructFields, SemanticModel model)
        {
            _readonlyStructFields = readonlyStructFields;
            _model = model;
        }

        public override Microsoft.CodeAnalysis.SyntaxNode? VisitBlock(Microsoft.CodeAnalysis.CSharp.Syntax.BlockSyntax node)
        {
            // Pre-collect the name→type context from the ORIGINAL node (whose parent
            // chain is intact). Nodes produced by base.VisitBlock lose their link to
            // ancestors above the block while we process them.
            var context = BuildNameTypeContext(node);

            node = (Microsoft.CodeAnalysis.CSharp.Syntax.BlockSyntax)base.VisitBlock(node)!;
            var newStatements = new List<Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax>();
            foreach (var statement in node.Statements)
            {
                var (leading, middle, trailing) = RewriteStatement(statement, context);
                newStatements.AddRange(leading);
                newStatements.Add(middle);
                newStatements.AddRange(trailing);
            }
            return node.WithStatements(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.List(newStatements));
        }

        private sealed record HoistContext(
            Dictionary<string, string> NameToType,
            string? ThisStructName);

        private static HoistContext BuildNameTypeContext(Microsoft.CodeAnalysis.CSharp.Syntax.BlockSyntax node)
        {
            var nameToType = new Dictionary<string, string>(StringComparer.Ordinal);

            var enclosingMethod = node.Ancestors()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.BaseMethodDeclarationSyntax>().FirstOrDefault();
            if (enclosingMethod != null)
            {
                foreach (var param in enclosingMethod.ParameterList.Parameters)
                {
                    var tn = param.Type != null ? ExtractSimpleTypeName(param.Type) : null;
                    if (tn != null) nameToType[param.Identifier.Text] = tn;
                }
            }

            var enclosingType = node.Ancestors()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>().FirstOrDefault();
            if (enclosingType != null)
            {
                foreach (var field in enclosingType.Members.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax>())
                {
                    var tn = ExtractSimpleTypeName(field.Declaration.Type);
                    if (tn == null) continue;
                    foreach (var variable in field.Declaration.Variables)
                        nameToType[variable.Identifier.Text] = tn;
                }
            }

            // Locals declared anywhere inside this block (explicit types only).
            foreach (var decl in node.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclarationSyntax>())
            {
                var tn = ExtractSimpleTypeName(decl.Type);
                if (tn == null || tn == "var") continue;
                foreach (var variable in decl.Variables)
                    nameToType[variable.Identifier.Text] = tn;
            }

            var enclosingStruct = node.Ancestors()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.StructDeclarationSyntax>().FirstOrDefault();
            return new HoistContext(nameToType, enclosingStruct?.Identifier.Text);
        }

        private (List<Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax>,
                 Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax,
                 List<Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax>)
            RewriteStatement(Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax statement, HoistContext context)
        {
            var leading = new List<Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax>();
            var trailing = new List<Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax>();
            var current = statement;

            // Repeatedly hoist the first hoistable ref/out argument until none remain.
            while (true)
            {
                var invocations = current.DescendantNodesAndSelf()
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>().ToList();
                var done = true;
                foreach (var invocation in invocations)
                {
                    var hoisted = TryHoist(invocation, current, leading, trailing, context);
                    if (hoisted.changed)
                    {
                        current = hoisted.NewStatement;
                        done = false;
                        break; // restart scan on the rewritten statement
                    }
                }
                if (done) break;
            }

            return (leading, current, trailing);
        }

        private (bool changed,
                 Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax NewStatement,
                 Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax? NewInvocation)
            TryHoist(
                Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax invocation,
                Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax statement,
                List<Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax> leading,
                List<Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax> trailing,
                HoistContext context)
        {
            foreach (var argument in invocation.ArgumentList.Arguments)
            {
                if (!argument.RefKindKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.RefKeyword) &&
                    !argument.RefKindKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.OutKeyword))
                    continue;
                if (argument.Expression is not Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax ma ||
                    ma.Name is not Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax fieldNameId)
                    continue;

                var fieldName = fieldNameId.Identifier.Text;
                string? structName = null;
                try
                {
                    var type = _model.GetTypeInfo(ma.Expression).Type;
                    if (type != null && _readonlyStructFields.TryGetValue(type.Name, out var fields) &&
                        fields.Contains(fieldName))
                        structName = type.Name;
                }
                catch (ArgumentException)
                {
                    // Node was synthesized by an earlier pass and is not part of the
                    // semantic model's tree — fall back to syntactic resolution.
                }
                if (structName == null)
                {
                    structName = ResolveStructTypeSyntactic(ma.Expression, fieldName, context);
                }
                if (structName == null) continue;

                var tempName = "__cs2jRef" + (_counter++);

                leading.Add(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.LocalDeclarationStatement(
                    Microsoft.CodeAnalysis.CSharp.SyntaxFactory.VariableDeclaration(
                            Microsoft.CodeAnalysis.CSharp.SyntaxFactory.IdentifierName("var")
                                .WithTrailingTrivia(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Space))
                        .WithVariables(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.SingletonSeparatedList(
                            Microsoft.CodeAnalysis.CSharp.SyntaxFactory.VariableDeclarator(tempName)
                                .WithInitializer(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.EqualsValueClause(
                                    ma.WithoutTrivia())
                                    .WithEqualsToken(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.EqualsToken)
                                        .WithLeadingTrivia(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Space)
                                        .WithTrailingTrivia(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Space))))))
                    .WithLeadingTrivia(statement.GetLeadingTrivia())
                    .WithTrailingTrivia(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.CarriageReturnLineFeed));

                var newArguments = invocation.ArgumentList.Arguments.Replace(
                    argument,
                    argument.WithExpression(
                        Microsoft.CodeAnalysis.CSharp.SyntaxFactory.IdentifierName(tempName)));
                var newInvocation = invocation.WithArgumentList(
                    invocation.ArgumentList.WithArguments(newArguments));
                var newStatement = statement.ReplaceNode(invocation, newInvocation);

                // Write-back so any modification through the ref is preserved.
                var receiver = ma.Expression.WithoutTrivia();
                trailing.Add(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ExpressionStatement(
                    Microsoft.CodeAnalysis.CSharp.SyntaxFactory.AssignmentExpression(
                        Microsoft.CodeAnalysis.CSharp.SyntaxKind.SimpleAssignmentExpression,
                        receiver,
                        Microsoft.CodeAnalysis.CSharp.SyntaxFactory.InvocationExpression(
                            Microsoft.CodeAnalysis.CSharp.SyntaxFactory.MemberAccessExpression(
                                Microsoft.CodeAnalysis.CSharp.SyntaxKind.SimpleMemberAccessExpression,
                                receiver,
                                Microsoft.CodeAnalysis.CSharp.SyntaxFactory.IdentifierName("With" + fieldName)),
                            Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ArgumentList(
                                Microsoft.CodeAnalysis.CSharp.SyntaxFactory.SingletonSeparatedList(
                                    Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Argument(
                                        Microsoft.CodeAnalysis.CSharp.SyntaxFactory.IdentifierName(tempName)))))))
                    .WithLeadingTrivia(statement.GetLeadingTrivia())
                    .WithTrailingTrivia(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.CarriageReturnLineFeed));

                return (true, newStatement, newInvocation);
            }

            return (false, statement, null);
        }

        /// <summary>
        /// Syntactic fallback for receiver type resolution when the semantic model
        /// cannot see the node (synthesized by an earlier pass). Uses the pre-collected
        /// name→type context (parameters, locals, fields) plus `this`.
        /// </summary>
        private string? ResolveStructTypeSyntactic(Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax expression, string fieldName, HoistContext context)
        {
            // this.field — enclosing struct
            if (expression is Microsoft.CodeAnalysis.CSharp.Syntax.ThisExpressionSyntax)
            {
                if (context.ThisStructName != null &&
                    _readonlyStructFields.TryGetValue(context.ThisStructName, out var sf) &&
                    sf.Contains(fieldName))
                    return context.ThisStructName;
                return null;
            }

            // Root identifier of the receiver chain
            Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax current = expression;
            while (true)
            {
                switch (current)
                {
                    case Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax ma2:
                        current = ma2.Expression;
                        continue;
                    case Microsoft.CodeAnalysis.CSharp.Syntax.ElementAccessExpressionSyntax ea:
                        current = ea.Expression;
                        continue;
                    case Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax id:
                        if (context.NameToType.TryGetValue(id.Identifier.Text, out var typeName) &&
                            _readonlyStructFields.TryGetValue(typeName, out var fields2) &&
                            fields2.Contains(fieldName))
                            return typeName;
                        return null;
                    default:
                        return null;
                }
            }
        }

        private static string? ExtractSimpleTypeName(Microsoft.CodeAnalysis.CSharp.Syntax.TypeSyntax type) => type switch
        {
            Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax id => id.Identifier.Text,
            Microsoft.CodeAnalysis.CSharp.Syntax.QualifiedNameSyntax qn => qn.Right.Identifier.Text,
            Microsoft.CodeAnalysis.CSharp.Syntax.GenericNameSyntax gn => gn.Identifier.Text,
            _ => null
        };
    }

    /// <summary>
    /// Ensures every intermediate .csproj pins <c>&lt;LangVersion&gt;latest&lt;/LangVersion&gt;</c>
    /// (inserted into the first PropertyGroup when absent).
    /// </summary>
    private static void EnsureLangVersionLatest(string intermediateRoot)
    {
        foreach (var csproj in Directory.EnumerateFiles(intermediateRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(csproj);
            if (content.Contains("<LangVersion>", StringComparison.OrdinalIgnoreCase))
                continue;
            var marker = "<PropertyGroup";
            var index = content.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;
            var tagEnd = content.IndexOf('>', index);
            if (tagEnd < 0) continue;
            var patched = content.Insert(tagEnd + 1, "\n    <LangVersion>latest</LangVersion>");
            File.WriteAllText(csproj, patched);
        }
    }

    private static readonly System.Text.RegularExpressions.Regex InternalsVisibleToPublicKeyRegex = new(
        "InternalsVisibleTo\\(\"(?<name>[^\"]+?),\\s*PublicKey=[0-9a-fA-F]+\"\\)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex SignAssemblyRegex = new(
        "<SignAssembly>\\s*true\\s*</SignAssembly>",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Strips the <c>PublicKey=...</c> portion of <c>InternalsVisibleTo</c> attributes when
    /// no project in the intermediate tree enables strong-name signing. An unsigned
    /// granting assembly cannot expose internals to a key-qualified friend, so leaving the
    /// key in place fails the build with CS0281 (and hides internal virtual members from
    /// test overrides, CS0115).
    /// </summary>
    private static void NormalizeFriendAssemblyAccess(string intermediateRoot)
    {
        if (AnyProjectSigned(intermediateRoot))
        {
            return;
        }

        foreach (var csFile in Directory.EnumerateFiles(intermediateRoot, "*.cs", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(csFile);
            if (!content.Contains("InternalsVisibleTo", StringComparison.Ordinal))
            {
                continue;
            }

            var patched = InternalsVisibleToPublicKeyRegex.Replace(
                content,
                match => $"InternalsVisibleTo(\"{match.Groups["name"].Value}\")");
            if (!string.Equals(patched, content, StringComparison.Ordinal))
            {
                File.WriteAllText(csFile, patched);
            }
        }
    }

    private static bool AnyProjectSigned(string intermediateRoot)
    {
        foreach (var csproj in Directory.EnumerateFiles(intermediateRoot, "*.csproj", SearchOption.AllDirectories))
        {
            if (SignAssemblyRegex.IsMatch(File.ReadAllText(csproj)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Textual pass: adds the <c>readonly</c> modifier to struct declarations that live
    /// inside inactive #if regions (disabled text — invisible to Roslyn and never
    /// compiled), so every struct declaration in the output is readonly.
    /// </summary>
    private static async Task MarkDisabledRegionStructsReadonly(
        string intermediateRoot, string sourceRoot, List<string> sourceFiles)
    {
        var structPattern = new System.Text.RegularExpressions.Regex(
            @"(?<=^|\n)(?<indent>[ \t]*)(?<mods>(?:(?:public|internal|private|protected|file|unsafe|partial|ref)\s+)*)(?<kw>struct)(?<name>\s+\w+)",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        foreach (var sourceFile in sourceFiles)
        {
            if (!sourceFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            var outputFile = Path.Combine(intermediateRoot, relativePath);
            if (!File.Exists(outputFile)) continue;

            var code = await File.ReadAllTextAsync(outputFile);
            var tree = CSharpSyntaxTree.ParseText(code, path: outputFile);

            // Collect line spans of ACTIVE struct declarations (already handled by the maker).
            var activeLines = new HashSet<int>();
            foreach (var structDecl in tree.GetRoot().DescendantNodes()
                         .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.StructDeclarationSyntax>())
            {
                activeLines.Add(tree.GetLineSpan(structDecl.Span).StartLinePosition.Line);
            }

            var changed = false;
            var newCode = structPattern.Replace(code, match =>
            {
                var line = tree.GetLineSpan(Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(match.Index, match.Index)).StartLinePosition.Line;
                if (activeLines.Contains(line))
                    return match.Value; // active struct — handled by the maker
                var mods = match.Groups["mods"].Value;
                if (mods.Contains("readonly"))
                    return match.Value;
                changed = true;
                return match.Groups["indent"].Value + mods + "readonly " + match.Groups["kw"].Value + match.Groups["name"].Value;
            });

            if (changed)
            {
                await File.WriteAllTextAsync(outputFile, newCode, new System.Text.UTF8Encoding(false));
            }
        }
    }

    /// <summary>
    /// Companion to TransformMigratedInterfaces: for every concrete type that directly
    /// declares a transformed interface, rewrites the matching void implementation
    /// methods to return the concrete type (<c>return this;</c>). Struct implementers
    /// are excluded — their methods were already migrated by the readonly-struct maker.
    /// </summary>
    private static async Task FixConcreteInterfaceImplementers(
        string intermediateRoot, string sourceRoot, List<string> sourceFiles)
    {
        // Load the members transformed by TransformMigratedInterfaces (persisted in a
        // metadata file): interface name -> member names.
        var affectedMembersByInterface = await LoadTransformedInterfaceMembers(intermediateRoot);
        if (affectedMembersByInterface.Count == 0) return;

        var outputCsFiles = new List<string>();
        foreach (var sourceFile in sourceFiles)
        {
            if (!sourceFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            var outputFile = Path.Combine(intermediateRoot, relativePath);
            if (File.Exists(outputFile))
                outputCsFiles.Add(outputFile);
        }

        var outputTrees = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in outputCsFiles)
        {
            var code = await File.ReadAllTextAsync(file);
            outputTrees[file] = CSharpSyntaxTree.ParseText(code, path: file);
        }

        var rewriter = new ConcreteImplementerRewriter(affectedMembersByInterface);
        foreach (var (file, tree) in outputTrees)
        {
            var root = tree.GetRoot();
            var newRoot = rewriter.Visit(root);
            if (newRoot != null && newRoot.ToFullString() != root.ToFullString())
            {
                await File.WriteAllTextAsync(file, newRoot.ToFullString(), new System.Text.UTF8Encoding(false));
            }
        }
    }

    /// <summary>
    /// Metadata describing transformed interface members:
    /// interface name -> (its type parameter names, member name -> overload parameter
    /// type keys in the interface's GENERIC form, e.g. "P" or "IRectangle&lt;P&gt;").
    /// </summary>
    private sealed class TransformedInterfaceInfo
    {
        public List<string> TypeParams { get; set; } = new();
        public Dictionary<string, List<string>> Members { get; set; } = new();
    }

    /// <summary>
    /// Reads the <c>.cs2j-iface-members.json</c> metadata written by
    /// TransformMigratedInterfaces.
    /// </summary>
    private static async Task<Dictionary<string, TransformedInterfaceInfo>> LoadTransformedInterfaceMembers(string intermediateRoot)
    {
        var metadataPath = Path.Combine(intermediateRoot, ".cs2j-iface-members.json");
        if (!File.Exists(metadataPath))
            return new Dictionary<string, TransformedInterfaceInfo>(StringComparer.Ordinal);
        try
        {
            var json = await File.ReadAllTextAsync(metadataPath);
            var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, TransformedInterfaceInfo>>(json);
            return map ?? new Dictionary<string, TransformedInterfaceInfo>(StringComparer.Ordinal);
        }
        catch (Exception)
        {
            return new Dictionary<string, TransformedInterfaceInfo>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Widens void implementation methods of transformed interface members to return
    /// the declaring (concrete) type, appending <c>return this;</c> semantics.
    /// </summary>
    private sealed class ConcreteImplementerRewriter : Microsoft.CodeAnalysis.CSharp.CSharpSyntaxRewriter
    {
        private readonly Dictionary<string, TransformedInterfaceInfo> _affectedMembersByInterface;

        public ConcreteImplementerRewriter(Dictionary<string, TransformedInterfaceInfo> affectedMembersByInterface)
        {
            _affectedMembersByInterface = affectedMembersByInterface;
        }

        public override Microsoft.CodeAnalysis.SyntaxNode? VisitClassDeclaration(
            Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax node)
            => RewriteType(node, node.Identifier.Text, node.BaseList);

        public override Microsoft.CodeAnalysis.SyntaxNode? VisitStructDeclaration(
            Microsoft.CodeAnalysis.CSharp.Syntax.StructDeclarationSyntax node)
        {
            // Struct implementers were already migrated (methods return the struct);
            // only classes need the in-place `return this` widening.
            return base.VisitStructDeclaration(node);
        }

        private Microsoft.CodeAnalysis.SyntaxNode RewriteType(
            Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax node,
            string typeName,
            Microsoft.CodeAnalysis.CSharp.Syntax.BaseListSyntax? baseList)
        {
            if (baseList == null)
                return base.VisitClassDeclaration((Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax)node);

            var affectedMembers = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
            foreach (var baseType in baseList.Types)
            {
                var baseName = baseType.Type switch
                {
                    Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax id => id.Identifier.Text,
                    Microsoft.CodeAnalysis.CSharp.Syntax.GenericNameSyntax gn => gn.Identifier.Text,
                    Microsoft.CodeAnalysis.CSharp.Syntax.QualifiedNameSyntax qn => qn.Right.Identifier.Text,
                    _ => null
                };
                if (baseName == null || !_affectedMembersByInterface.TryGetValue(baseName, out var ifaceInfo) ||
                    ifaceInfo.Members == null)
                    continue;
                foreach (var (memberName, paramKeys) in ifaceInfo.Members)
                {
                    if (!affectedMembers.TryGetValue(memberName, out var set))
                        affectedMembers[memberName] = set = new HashSet<int>();
                    foreach (var pk in paramKeys)
                        set.Add(pk.Length == 0 ? 0 : pk.Split(',').Length);
                }
            }

            if (affectedMembers.Count == 0)
                return base.VisitClassDeclaration((Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax)node);

            var newMembers = node.Members;
            for (var i = 0; i < newMembers.Count; i++)
            {
                if (newMembers[i] is not Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax method ||
                    method.Body == null ||
                    !affectedMembers.TryGetValue(method.Identifier.Text, out var memberArities) ||
                    !memberArities.Contains(method.ParameterList.Parameters.Count))
                    continue;
                if (method.ReturnType is not Microsoft.CodeAnalysis.CSharp.Syntax.PredefinedTypeSyntax pre ||
                    !pre.Keyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.VoidKeyword))
                    continue;

                // Rewritten methods keep mutating in place but return the instance.
                var newBody = (Microsoft.CodeAnalysis.CSharp.Syntax.BlockSyntax)
                    new BareReturnToThisRewriter().Visit(method.Body)!;
                var last = newBody.Statements.LastOrDefault();
                var endsWithReturn = last is Microsoft.CodeAnalysis.CSharp.Syntax.ReturnStatementSyntax;
                if (!endsWithReturn)
                {
                    newBody = newBody.AddStatements(
                        Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ReturnStatement(
                            Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ThisExpression())
                        .WithReturnKeyword(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ReturnKeyword)
                            .WithTrailingTrivia(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Space)));
                }

                newMembers = newMembers.Replace(method, method
                    .WithReturnType(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.IdentifierName(typeName)
                        .WithTrailingTrivia(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Space))
                    .WithBody(newBody));
            }

            var rewritten = node.WithMembers(newMembers);
            return rewritten == node
                ? base.VisitClassDeclaration((Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax)node)
                : rewritten;
        }

        /// <summary>Replaces bare `return;` with `return this;`.</summary>
        private sealed class BareReturnToThisRewriter : Microsoft.CodeAnalysis.CSharp.CSharpSyntaxRewriter
        {
            public override Microsoft.CodeAnalysis.SyntaxNode? VisitReturnStatement(
                Microsoft.CodeAnalysis.CSharp.Syntax.ReturnStatementSyntax node)
            {
                if (node.Expression == null)
                {
                    return node
                        .WithReturnKeyword(node.ReturnKeyword.WithTrailingTrivia(
                            Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Space))
                        .WithExpression(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ThisExpression());
                }
                return base.VisitReturnStatement(node);
            }
        }
    }

    /// <summary>
    /// Adds bridge explicit interface implementations to CLASSES implementing
    /// transformed interfaces: <c>IFace&lt;T&gt; IFace.M(args) { return M(args); }</c>.
    /// Structs are skipped (their bridges come from the readonly-struct maker + widener).
    /// </summary>
    private static async Task AddClassInterfaceBridges(
        string intermediateRoot, string sourceRoot, List<string> sourceFiles)
    {
        // Load the members transformed by TransformMigratedInterfaces (metadata file).
        var affectedMembersByInterface = await LoadTransformedInterfaceMembers(intermediateRoot);
        if (affectedMembersByInterface.Count == 0) return;

        var outputCsFiles = new List<string>();
        foreach (var sourceFile in sourceFiles)
        {
            if (!sourceFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            var outputFile = Path.Combine(intermediateRoot, relativePath);
            if (File.Exists(outputFile))
                outputCsFiles.Add(outputFile);
        }

        var outputTrees = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in outputCsFiles)
        {
            var code = await File.ReadAllTextAsync(file);
            outputTrees[file] = CSharpSyntaxTree.ParseText(code, path: file);
        }

        var rewriter = new ClassInterfaceBridgeRewriter(affectedMembersByInterface);
        foreach (var (file, tree) in outputTrees)
        {
            var root = tree.GetRoot();
            var newRoot = rewriter.Visit(root);
            if (newRoot != null && newRoot.ToFullString() != root.ToFullString())
            {
                await File.WriteAllTextAsync(file, newRoot.ToFullString(), new System.Text.UTF8Encoding(false));
            }
        }
    }

    /// <summary>
    /// Adds bridge explicit implementations to classes for widened interface members.
    /// </summary>
    private sealed class ClassInterfaceBridgeRewriter : Microsoft.CodeAnalysis.CSharp.CSharpSyntaxRewriter
    {
        private readonly Dictionary<string, TransformedInterfaceInfo> _affectedMembersByInterface;

        public ClassInterfaceBridgeRewriter(Dictionary<string, TransformedInterfaceInfo> affectedMembersByInterface)
        {
            _affectedMembersByInterface = affectedMembersByInterface;
        }

        public override Microsoft.CodeAnalysis.SyntaxNode? VisitClassDeclaration(
            Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax node)
        {
            node = (Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;
            if (node.BaseList == null) return node;

            var newMembers = node.Members;
            foreach (var baseType in node.BaseList.Types)
            {
                var baseName = baseType.Type switch
                {
                    Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax id => id.Identifier.Text,
                    Microsoft.CodeAnalysis.CSharp.Syntax.GenericNameSyntax gn => gn.Identifier.Text,
                    Microsoft.CodeAnalysis.CSharp.Syntax.QualifiedNameSyntax qn => qn.Right.Identifier.Text,
                    _ => null
                };
                if (baseName == null || !_affectedMembersByInterface.TryGetValue(baseName, out var info) ||
                    info.Members == null)
                    continue;

                // Substitute the interface type parameters with the concrete type
                // arguments used in the class's base list (IRectangle<double> → P=double).
                var substitution = new Dictionary<string, string>(StringComparer.Ordinal);
                if (baseType.Type is Microsoft.CodeAnalysis.CSharp.Syntax.GenericNameSyntax baseGeneric &&
                    info.TypeParams.Count == baseGeneric.TypeArgumentList.Arguments.Count)
                {
                    for (var ti = 0; ti < info.TypeParams.Count; ti++)
                        substitution[info.TypeParams[ti]] = baseGeneric.TypeArgumentList.Arguments[ti].ToString();
                }

                foreach (var (memberName, paramKeys) in info.Members)
                {
                    foreach (var paramKey in paramKeys)
                    {
                        var concreteKey = paramKey;
                        foreach (var (tp, arg) in substitution)
                        {
                            concreteKey = System.Text.RegularExpressions.Regex.Replace(
                                concreteKey, $@"\b{System.Text.RegularExpressions.Regex.Escape(tp)}\b",
                                arg.Replace("$", "$$"));
                        }
                        var arity = paramKey.Length == 0 ? 0 : paramKey.Split(',').Length;

                        // Skip when an explicit implementation already exists.
                        if (newMembers.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().Any(m =>
                            m.ExplicitInterfaceSpecifier != null &&
                            m.ExplicitInterfaceSpecifier.Name.ToString().StartsWith(baseName, StringComparison.Ordinal) &&
                            m.Identifier.Text == memberName &&
                            ParamTypesMatch(m, concreteKey)))
                            continue;

                        // Find the concrete implementation whose parameter types match the
                        // substituted interface signature.
                        var concrete = newMembers.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
                            .FirstOrDefault(m => m.Identifier.Text == memberName &&
                                                 m.ExplicitInterfaceSpecifier == null &&
                                                 m.Body != null &&
                                                 ParamTypesMatch(m, concreteKey));
                        if (concrete == null) continue;

                        var arguments = concrete.ParameterList.Parameters
                            .Select(p => Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Argument(
                                Microsoft.CodeAnalysis.CSharp.SyntaxFactory.IdentifierName(p.Identifier.Text)))
                            .ToList();
                        var call = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.InvocationExpression(
                            Microsoft.CodeAnalysis.CSharp.SyntaxFactory.IdentifierName(memberName),
                            Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ArgumentList(
                                Microsoft.CodeAnalysis.CSharp.SyntaxFactory.SeparatedList(arguments)));

                        var bridge = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.MethodDeclaration(
                                baseType.Type.WithTrailingTrivia(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Space),
                                memberName)
                            .WithExplicitInterfaceSpecifier(
                                Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ExplicitInterfaceSpecifier(
                                    Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseName(baseType.Type.ToString())))
                            .WithParameterList(concrete.ParameterList)
                            .WithBody(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Block(
                                Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ReturnStatement(call)
                                    .WithReturnKeyword(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ReturnKeyword)
                                        .WithTrailingTrivia(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Space))))
                            .WithLeadingTrivia(
                                Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Comment("// cs2j-class-bridge"),
                                Microsoft.CodeAnalysis.CSharp.SyntaxFactory.CarriageReturnLineFeed);

                        newMembers = newMembers.Add(bridge);
                    }
                }
            }

            return node.WithMembers(newMembers);
        }

        /// <summary>
        /// True when the method's parameter type list equals the substituted interface
        /// parameter key (comma-joined type strings, whitespace-insensitive).
        /// </summary>
        private static bool ParamTypesMatch(
            Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax method, string concreteKey)
        {
            var methodKey = string.Join(",", method.ParameterList.Parameters
                .Select(p => p.Type?.ToString() ?? ""));
            static string Normalize(string s) => new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
            return Normalize(methodKey) == Normalize(concreteKey);
        }
    }

    /// <summary>
    /// Cross-file pass: readonly-struct migration turns void mutating methods into
    /// struct-returning methods, which breaks interface implementation (return types
    /// must match). For every interface member affected, this pass:
    ///   1. changes the interface method's return type to the interface type itself
    ///      (satisfied via C# 9 covariant returns),
    ///   2. rewrites interface call sites `recv.M(args);` → `recv = recv.M(args);`,
    ///   3. removes the generated explicit interface implementations (the migrated
    ///      public method now implements the member implicitly).
    /// </summary>
    private static async Task TransformMigratedInterfaces(
        string intermediateRoot, string sourceRoot, List<string> sourceFiles)
    {
        var outputCsFiles = new List<string>();
        foreach (var sourceFile in sourceFiles)
        {
            if (!sourceFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            var outputFile = Path.Combine(intermediateRoot, relativePath);
            if (File.Exists(outputFile))
                outputCsFiles.Add(outputFile);
        }

        var outputTrees = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in outputCsFiles)
        {
            var code = await File.ReadAllTextAsync(file);
            outputTrees[file] = CSharpSyntaxTree.ParseText(code, path: file);
        }

        var references = GetBasicReferences();
        var outputCompilation = CSharpCompilation.Create(
            "iface-transform",
            outputTrees.Values,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        // 1) Discover conflicts: interface members (void) whose implementing method in a
        //    readonly struct now returns the struct type.
        var transformedInterfaceMethods = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var interfaceMethodFiles = new Dictionary<IMethodSymbol, string>(SymbolEqualityComparer.Default);
        var explicitImplsToWiden = new List<(string File, string IfaceTypeName, string MemberName, INamedTypeSymbol Interface)>();
        var seenWidenKeys = new HashSet<(string, string, string)>();

        foreach (var (file, tree) in outputTrees)
        {
            var model = outputCompilation.GetSemanticModel(tree);
            foreach (var structDecl in tree.GetRoot().DescendantNodes()
                         .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.StructDeclarationSyntax>())
            {
                if (!structDecl.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ReadOnlyKeyword)))
                    continue;
                if (model.GetDeclaredSymbol(structDecl) is not INamedTypeSymbol structSymbol)
                    continue;

                foreach (var iface in structSymbol.AllInterfaces)
                {
                    if (iface.DeclaringSyntaxReferences.Length == 0)
                        continue; // external interface — cannot rewrite
                    foreach (var member in iface.GetMembers())
                    {
                        if (member is not IMethodSymbol ifaceMethod ||
                            ifaceMethod.MethodKind != MethodKind.Ordinary ||
                            !ifaceMethod.ReturnsVoid)
                            continue;
                        // The migrated overload returns the struct type. It may be hidden
                        // behind a generated explicit implementation (which satisfies the
                        // interface today), so search the struct's members directly.
                        var migratedImpl = structSymbol.GetMembers(ifaceMethod.Name)
                            .OfType<IMethodSymbol>()
                            .FirstOrDefault(m => m.MethodKind == MethodKind.Ordinary &&
                                                 !m.IsStatic &&
                                                 m.Parameters.Length == ifaceMethod.Parameters.Length &&
                                                 SymbolEqualityComparer.Default.Equals(m.ReturnType, structSymbol));
                        if (migratedImpl == null) continue;

                        transformedInterfaceMethods.Add(ifaceMethod);
                        var ifaceSyntaxFile = ifaceMethod.DeclaringSyntaxReferences[0].SyntaxTree.FilePath;
                        interfaceMethodFiles[ifaceMethod] = outputTrees.ContainsKey(ifaceSyntaxFile)
                            ? ifaceSyntaxFile
                            : outputTrees.Keys.FirstOrDefault(f => string.Equals(
                                Path.GetFileName(f), Path.GetFileName(ifaceSyntaxFile),
                                StringComparison.OrdinalIgnoreCase)) ?? "";

                        // Structs cannot use covariant returns (CS8825), so their
                        // generated explicit implementations are WIDENED to return the
                        // interface type instead of being removed. Deduplicated by
                        // (file, interface, member) — overload arity does not matter
                        // because one explicit implementation is widened once.
                        var widenKey = (file,
                            iface.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            ifaceMethod.Name);
                        if (!seenWidenKeys.Contains(widenKey))
                        {
                            seenWidenKeys.Add(widenKey);
                            explicitImplsToWiden.Add((widenKey.file, widenKey.Item2, widenKey.Name, iface));
                        }
                    }
                }
            }
        }

        if (transformedInterfaceMethods.Count == 0) return;

        // Persist the affected members for the companion passes (they re-parse the
        // files and cannot distinguish transformed members from members that always
        // returned the interface type, e.g. ICurve.Clone()). Each member entry carries
        // the interface-generic parameter type keys of the affected overloads.
        var affectedMap = transformedInterfaceMethods
            .GroupBy(m => m.ContainingType.Name)
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    TypeParams = g.First().ContainingType.TypeParameters.Select(tp => tp.Name).ToList(),
                    Members = g.GroupBy(m => m.Name)
                        .ToDictionary(
                            mg => mg.Key,
                            // Record parameter types in the interface's GENERIC form
                            // (OriginalDefinition) so every implementer can substitute
                            // its own type arguments.
                            mg => mg.Select(m => string.Join(",",
                                m.OriginalDefinition.Parameters.Select(p =>
                                    p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)))).Distinct().ToList())
                });
        await File.WriteAllTextAsync(
            Path.Combine(intermediateRoot, ".cs2j-iface-members.json"),
            System.Text.Json.JsonSerializer.Serialize(affectedMap));

        // 2) Rewrite interface declarations, call sites, and remove explicit impls.
        var affectedMemberKeys = transformedInterfaceMethods
            .Select(m => (InterfaceName: m.ContainingType.Name, MemberName: m.Name))
            .ToHashSet();
        var ifaceRewriter = new InterfaceReturnTypeRewriter(affectedMemberKeys);
        var callSiteRewriter = new InterfaceCallSiteRewriter(transformedInterfaceMethods, outputCompilation);
        var explicitWidener = new ExplicitInterfaceImplWidener(explicitImplsToWiden);

        foreach (var (file, tree) in outputTrees)
        {
            var root = tree.GetRoot();
            var newRoot = ifaceRewriter.Visit(root);
            newRoot = callSiteRewriter.Visit(newRoot);
            newRoot = explicitWidener.Visit(newRoot);
            if (newRoot != null && newRoot.ToFullString() != root.ToFullString())
            {
                await File.WriteAllTextAsync(file, newRoot.ToFullString(), new System.Text.UTF8Encoding(false));
            }
        }
    }

    /// <summary>
    /// Changes the return type of affected interface methods to the declaring
    /// interface type (including its type parameters). Affected members are
    /// identified by (interface name, member name) pairs.
    /// </summary>
    private sealed class InterfaceReturnTypeRewriter : Microsoft.CodeAnalysis.CSharp.CSharpSyntaxRewriter
    {
        private readonly HashSet<(string InterfaceName, string MemberName)> _affected;

        public InterfaceReturnTypeRewriter(HashSet<(string InterfaceName, string MemberName)> affected)
        {
            _affected = affected;
        }

        public override Microsoft.CodeAnalysis.SyntaxNode? VisitInterfaceDeclaration(
            Microsoft.CodeAnalysis.CSharp.Syntax.InterfaceDeclarationSyntax node)
        {
            var ifaceName = node.Identifier.Text;

            var ifaceTypeSyntax = node.TypeParameterList != null
                ? (Microsoft.CodeAnalysis.CSharp.Syntax.TypeSyntax)Microsoft.CodeAnalysis.CSharp.SyntaxFactory
                    .GenericName(node.Identifier)
                    .WithTypeArgumentList(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.TypeArgumentList(
                        Microsoft.CodeAnalysis.CSharp.SyntaxFactory.SeparatedList(
                            node.TypeParameterList.Parameters.Select(p =>
                                (Microsoft.CodeAnalysis.CSharp.Syntax.TypeSyntax)Microsoft.CodeAnalysis.CSharp.SyntaxFactory
                                    .IdentifierName(p.Identifier.Text)))))
                : Microsoft.CodeAnalysis.CSharp.SyntaxFactory.IdentifierName(node.Identifier);

            var newMembers = node.Members;
            for (var i = 0; i < newMembers.Count; i++)
            {
                if (newMembers[i] is Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax method &&
                    method.ReturnType is Microsoft.CodeAnalysis.CSharp.Syntax.PredefinedTypeSyntax pre &&
                    pre.Keyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.VoidKeyword) &&
                    _affected.Contains((ifaceName, method.Identifier.Text)))
                {
                    newMembers = newMembers.Replace(method,
                        method.WithReturnType(ifaceTypeSyntax.WithTrailingTrivia(
                            Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Space)));
                }
            }

            return base.VisitInterfaceDeclaration(node.WithMembers(newMembers));
        }
    }

    /// <summary>
    /// Rewrites statement-level calls through transformed interface methods:
    /// `recv.M(args);` → `recv = recv.M(args);`
    /// </summary>
    private sealed class InterfaceCallSiteRewriter : Microsoft.CodeAnalysis.CSharp.CSharpSyntaxRewriter
    {
        private readonly HashSet<IMethodSymbol> _methods;
        private readonly CSharpCompilation _compilation;

        public InterfaceCallSiteRewriter(HashSet<IMethodSymbol> methods, CSharpCompilation compilation)
        {
            _methods = methods;
            _compilation = compilation;
        }

        public override Microsoft.CodeAnalysis.SyntaxNode? VisitExpressionStatement(
            Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionStatementSyntax node)
        {
            if (node.Expression is Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax invocation &&
                invocation.Expression is Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax ma)
            {
                try
                {
                    var tree = node.SyntaxTree;
                    if (tree != null)
                    {
                        var model = _compilation.GetSemanticModel(tree);
                        var symbol = model.GetSymbolInfo(invocation).Symbol;
                        if (symbol is IMethodSymbol methodSymbol &&
                            _methods.Contains(methodSymbol) &&
                            IsAssignableLValue(ma.Expression))
                        {
                            var assignment = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.AssignmentExpression(
                                Microsoft.CodeAnalysis.CSharp.SyntaxKind.SimpleAssignmentExpression,
                                ma.Expression.WithoutTrivia(),
                                invocation.WithoutTrivia());
                            return node.WithExpression(assignment);
                        }
                    }
                }
                catch (ArgumentException)
                {
                }
            }
            return base.VisitExpressionStatement(node);
        }

        private static bool IsAssignableLValue(Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax expression) => expression switch
        {
            Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax => true,
            Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax m => IsAssignableLValue(m.Expression),
            Microsoft.CodeAnalysis.CSharp.Syntax.ElementAccessExpressionSyntax e => IsAssignableLValue(e.Expression),
            _ => false
        };
    }

    /// <summary>
    /// Widens the generated explicit interface implementations of transformed members:
    /// <code>
    /// void IFace.M(args) { M(args); }  →  IFace&lt;T&gt; IFace.M(args) { return M(args); }
    /// </code>
    /// C# forbids covariant returns for structs (CS8825), but explicit implementations
    /// may return the interface type directly, preserving the interface contract.
    /// </summary>
    private sealed class ExplicitInterfaceImplWidener : Microsoft.CodeAnalysis.CSharp.CSharpSyntaxRewriter
    {
        private readonly Dictionary<(string Iface, string Member), INamedTypeSymbol> _toWiden;
        private readonly HashSet<string> _files;
        private string _currentFile = "";

        public ExplicitInterfaceImplWidener(List<(string File, string IfaceTypeName, string MemberName, INamedTypeSymbol Interface)> widens)
        {
            _toWiden = new Dictionary<(string Iface, string Member), INamedTypeSymbol>();
            foreach (var w in widens)
                _toWiden[(w.IfaceTypeName, w.MemberName)] = w.Interface;
            _files = widens.Select(r => r.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public override Microsoft.CodeAnalysis.SyntaxNode? VisitCompilationUnit(
            Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax node)
        {
            _currentFile = node.SyntaxTree?.FilePath ?? "";
            return base.VisitCompilationUnit(node);
        }

        public override Microsoft.CodeAnalysis.SyntaxNode? VisitMethodDeclaration(
            Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax node)
        {
            if (node.ExplicitInterfaceSpecifier == null || !_files.Contains(_currentFile))
                return base.VisitMethodDeclaration(node);

            var ifaceName = node.ExplicitInterfaceSpecifier.Name.ToString();
            var memberName = node.Identifier.Text;
            if (!_toWiden.TryGetValue((ifaceName, memberName), out var ifaceSymbol))
                return base.VisitMethodDeclaration(node);

            // Interface return type including its type parameters, e.g. IRectangle<P>.
            var ifaceTypeSyntax = BuildInterfaceTypeSyntax(node.ExplicitInterfaceSpecifier.Name, ifaceSymbol);

            // Argument list from the explicit implementation's parameters.
            var arguments = node.ParameterList.Parameters
                .Select(p => Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Argument(
                    Microsoft.CodeAnalysis.CSharp.SyntaxFactory.IdentifierName(p.Identifier.Text)))
                .ToList();
            var call = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.InvocationExpression(
                Microsoft.CodeAnalysis.CSharp.SyntaxFactory.IdentifierName(memberName),
                Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ArgumentList(
                    Microsoft.CodeAnalysis.CSharp.SyntaxFactory.SeparatedList(arguments)));

            var body = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Block(
                Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ReturnStatement(call)
                    .WithReturnKeyword(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ReturnKeyword)
                        .WithTrailingTrivia(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Space)));

            return node
                .WithReturnType(ifaceTypeSyntax.WithTrailingTrivia(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Space))
                .WithBody(body)
                .WithLeadingTrivia(node.GetLeadingTrivia()
                    .Add(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Comment("// cs2j-iface-bridge"))
                    .Add(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.CarriageReturnLineFeed));
        }

        private static Microsoft.CodeAnalysis.CSharp.Syntax.TypeSyntax BuildInterfaceTypeSyntax(
            Microsoft.CodeAnalysis.CSharp.Syntax.NameSyntax declaredName, INamedTypeSymbol ifaceSymbol)
        {
            if (ifaceSymbol.TypeParameters.Length == 0)
                return declaredName;
            if (declaredName is Microsoft.CodeAnalysis.CSharp.Syntax.GenericNameSyntax generic)
                return generic; // already carries concrete type arguments
            return Microsoft.CodeAnalysis.CSharp.SyntaxFactory.GenericName(
                    declaredName is Microsoft.CodeAnalysis.CSharp.Syntax.QualifiedNameSyntax qualified
                        ? qualified.Right.Identifier
                        : ((Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax)declaredName).Identifier)
                .WithTypeArgumentList(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.TypeArgumentList(
                    Microsoft.CodeAnalysis.CSharp.SyntaxFactory.SeparatedList(
                        ifaceSymbol.TypeParameters.Select(tp =>
                            (Microsoft.CodeAnalysis.CSharp.Syntax.TypeSyntax)Microsoft.CodeAnalysis.CSharp.SyntaxFactory
                                .IdentifierName(tp.Name)))));
        }
    }

    internal sealed record SourceLayout(string SourceRoot, string EntryPath, IReadOnlyList<string> ProjectFiles);
}
