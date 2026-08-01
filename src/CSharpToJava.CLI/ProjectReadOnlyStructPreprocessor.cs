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
        ".git", ".vs", ".idea", "bin", "obj", "node_modules",
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

        // Copy project files (.csproj, .sln) to the intermediate directory
        foreach (var projectFile in layout.ProjectFiles)
        {
            var relativePath = Path.GetRelativePath(sourceRoot, projectFile);
            var destinationFile = Path.Combine(intermediateRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(projectFile, destinationFile, overwrite: true);
        }

        // Collect all .cs files and parse them into syntax trees for compilation
        var sourceFiles = EnumerateSourceFiles(sourceRoot, intermediateRoot).ToList();
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
                    if (method.ReturnType is Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax returnTypeId &&
                        returnTypeId.Identifier.Text == structName)
                    {
                        migratedMethodToStruct[method.Identifier.Text] = structName;
                    }
                }
            }
        }

        if (migratedMethodToStruct.Count == 0) return;

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
            var updater = new CallSiteUpdater(migratedMethodToStruct, model);
            var newRoot = updater.Visit(root);
            if (newRoot != null && newRoot.ToFullString() != root.ToFullString())
            {
                await File.WriteAllTextAsync(file, newRoot.ToFullString(), new System.Text.UTF8Encoding(false));
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
                    if (publicFieldStructs.TryGetValue(structName, out var existing))
                        existing.UnionWith(withMethodProps);
                    else
                        publicFieldStructs[structName] = withMethodProps;
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
            var rewriter = new AssignmentRewriter(publicFieldStructs, model);
            rewriter.BuildVariableTypeMap(root);
            var newRoot = rewriter.Visit(root);
            if (newRoot != null && newRoot.ToFullString() != root.ToFullString())
            {
                await File.WriteAllTextAsync(file, newRoot.ToFullString(), new System.Text.UTF8Encoding(false));
            }
        }
    }

    internal sealed record SourceLayout(string SourceRoot, string EntryPath, IReadOnlyList<string> ProjectFiles);
}
