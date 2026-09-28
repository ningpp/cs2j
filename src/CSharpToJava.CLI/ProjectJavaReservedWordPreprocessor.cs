using CSharpToJava.Core.JavaKeywords;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI;

internal sealed class ProjectJavaReservedWordPreprocessRequest
{
    public required string SourcePath { get; init; }
    public required string DestinationRoot { get; init; }
    public bool Force { get; init; } = true;
    public bool Verbose { get; init; }
    public required JavaKeywordOptions Options { get; init; }
    public bool FilterUnsupportedProjects { get; init; } = true;
    public IReadOnlyCollection<string>? CompatNamespacePrefixes { get; init; }
}

internal sealed class ProjectJavaReservedWordPreprocessResult
{
    public required string OriginalSourcePath { get; init; }
    public required string SourceRoot { get; init; }
    public required string IntermediateRoot { get; init; }
    public required string PreprocessedSourcePath { get; init; }
    public required bool Success { get; init; }
    public required JavaKeywordStatistics Statistics { get; init; }
    public required IReadOnlyList<JavaKeywordDiagnostic> Diagnostics { get; init; }
}

/// <summary>
/// 项目级 java-keywords 驱动：复制源码树 → 每轮全树建编译 → 逐文件 RewriteFile →
/// 整树重编译比对基线错误集 → 新增错误按 plan 归属并回退（FB-2，规范 §5）。
/// </summary>
internal static class ProjectJavaReservedWordPreprocessor
{
    public const string IntermediateDirectoryName = ".cs2j-java-keywords-src";

    public static async Task<ProjectJavaReservedWordPreprocessResult> PreprocessAsync(
        ProjectJavaReservedWordPreprocessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationRoot);

        var layout = ProjectPreprocessSource.ResolveLayout(request.SourcePath);
        var intermediateRoot = Path.Combine(
            Path.GetFullPath(request.DestinationRoot), IntermediateDirectoryName);

        if (Directory.Exists(intermediateRoot))
        {
            if (!request.Force)
            {
                throw new InvalidOperationException(
                    $"Java-keywords directory already exists: {intermediateRoot}. Use --force to overwrite.");
            }
            Directory.Delete(intermediateRoot, recursive: true);
        }
        Directory.CreateDirectory(intermediateRoot);

        var diagnostics = new List<JavaKeywordDiagnostic>();
        var rollbackDiagnostics = new List<JavaKeywordDiagnostic>();
        var sourceRoot = layout.SourceRoot;

        var projectGraph = ProjectPreprocessSource.TryLoadProjectGraph(layout, request.Verbose);
        var exclusionInfo = request.FilterUnsupportedProjects
            ? SupportedProjectFiltering.ComputeExclusions(
                projectGraph, request.Verbose, request.CompatNamespacePrefixes, layout.SourceRoot)
            : new SupportedProjectFiltering.ExclusionInfo();

        await ProjectPreprocessSource.CopyProjectFilesAsync(
            layout, sourceRoot, intermediateRoot, exclusionInfo);
        ProjectPreprocessSource.CopyAncestorMsBuildDirectoryFiles(
            sourceRoot, intermediateRoot, "[java-keywords]", request.Verbose);

        var sourceFiles = ProjectPreprocessSource.EnumerateSourceFiles(
                sourceRoot, intermediateRoot, Path.GetFullPath(request.DestinationRoot))
            .Where(file => !SupportedProjectFiltering.IsUnderExcludedProject(file, exclusionInfo))
            .Where(file => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .Select(file => (
                Source: file,
                Dest: Path.Combine(intermediateRoot, Path.GetRelativePath(sourceRoot, file))))
            .ToList();

        foreach (var pair in sourceFiles)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pair.Dest)!);
            File.Copy(pair.Source, pair.Dest, overwrite: true);
        }

        // 基线错误集：用原始内容、但 path 取中间树路径，保证键可直接比对。
        var baselineErrors = BuildErrorSet(
            sourceFiles.Select(p => (Path: p.Dest, Content: File.ReadAllText(p.Source))),
            layout, intermediateRoot);

        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var acceptedStats = new JavaKeywordStatistics();
        var acceptedDiagnostics = new List<JavaKeywordDiagnostic>();
        var success = true;
        const int maxIterations = 1024;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            // 每轮从原始副本出发：避免对已改写文件二次驱动造成幂等问题。
            foreach (var pair in sourceFiles)
                File.Copy(pair.Source, pair.Dest, overwrite: true);

            var passStats = new JavaKeywordStatistics();
            var passDiagnostics = new List<JavaKeywordDiagnostic>();
            var preprocessor = new JavaReservedWordPreprocessor(request.Options);
            var compilation = BuildCompilation(
                sourceFiles.Select(p => (Path: p.Dest, Content: File.ReadAllText(p.Dest))),
                layout, intermediateRoot);
            preprocessor.BindCompilation(compilation);
            preprocessor.ExcludePlans(excluded);

            // rewrittenByFile：被改写文件 → 其原始源文件路径（值当前未参与归属，仅作"在改写集"判定）。
            var rewrittenByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in sourceFiles)
            {
                var sourceCode = File.ReadAllText(pair.Dest);
                var tree = compilation.SyntaxTrees.FirstOrDefault(t =>
                    string.Equals(t.FilePath, pair.Dest, StringComparison.OrdinalIgnoreCase));
                if (tree == null) continue;

                var result = preprocessor.RewriteFile(
                    sourceCode,
                    compilation.GetSemanticModel(tree),
                    passStats,
                    passDiagnostics);

                if (result.Changed)
                {
                    File.WriteAllText(pair.Dest, result.OutputCode,
                        FileStartsWithBom(pair.Source)
                            ? new System.Text.UTF8Encoding(true)
                            : new System.Text.UTF8Encoding(false));
                    rewrittenByFile[pair.Dest] = pair.Source;
                }

                if (request.Verbose)
                {
                    Console.WriteLine(
                        $"[java-keywords] {(result.Changed ? "transformed" : "clean (copy)")}: " +
                        $"{Path.GetRelativePath(sourceRoot, pair.Source)}");
                }
            }

            var candidateCompilation = BuildCompilation(
                sourceFiles.Select(p => (Path: p.Dest, Content: File.ReadAllText(p.Dest))),
                layout, intermediateRoot);
            var newErrors = DiffErrors(baselineErrors, candidateCompilation, intermediateRoot);

            if (newErrors.Count == 0)
            {
                acceptedStats = passStats;
                acceptedDiagnostics = passDiagnostics;
                break;
            }

            // 归属新增错误 → 唯一 plan。
            var plansToRollback = new HashSet<string>(StringComparer.Ordinal);
            foreach (var error in newErrors)
            {
                if (preprocessor.TryAttributeDiagnostic(error, rewrittenByFile, out var planId)
                    && !excluded.Contains(planId)
                    && !plansToRollback.Contains(planId))
                {
                    plansToRollback.Add(planId);
                }
            }

            if (plansToRollback.Count == 0)
            {
                // 无法归属单一 plan → 回退本次运行全部改动（JK030）。
                foreach (var pair in sourceFiles)
                    File.Copy(pair.Source, pair.Dest, overwrite: true);
                var first = newErrors[0];
                var span = first.Location.GetLineSpan().StartLinePosition;
                diagnostics.Add(new JavaKeywordDiagnostic(
                    JavaKeywordSeverity.Error,
                    JavaKeywordDiagnosticCode.JK030,
                    "whole-tree validation failed and the new error could not be attributed to a " +
                    $"single plan ({first.Descriptor.Id}: {first.GetMessage()}); all changes " +
                    "from this run were rolled back.",
                    first.Location.SourceTree?.FilePath,
                    span.Line + 1,
                    span.Character + 1,
                    null));
                acceptedStats = new JavaKeywordStatistics();
                acceptedDiagnostics = new List<JavaKeywordDiagnostic>();
                success = false;
                break;
            }

            foreach (var planId in plansToRollback)
            {
                var plan = preprocessor.Plans.First(p => p.Id == planId);
                excluded.Add(planId);
                acceptedStats.PlansRolledBack++;
                rollbackDiagnostics.Add(new JavaKeywordDiagnostic(
                    JavaKeywordSeverity.Warning,
                    JavaKeywordDiagnosticCode.JK031,
                    "whole-tree validation attributed a new error to this plan; the plan " +
                    "and all of its touched files were rolled back atomically.",
                    plan.Symbol.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree?.FilePath,
                    (plan.Symbol.Locations.FirstOrDefault(l => l.IsInSource)
                        ?.GetLineSpan().StartLinePosition.Line ?? 0) + 1,
                    (plan.Symbol.Locations.FirstOrDefault(l => l.IsInSource)
                        ?.GetLineSpan().StartLinePosition.Character ?? 0) + 1,
                    plan.SymbolName));

                // 回退该计划触碰的所有文件（可能与其他计划共享文件；下一轮全部重放）。
                foreach (var touched in plan.TouchedFiles)
                {
                    var full = Path.GetFullPath(touched);
                    var pair = sourceFiles.FirstOrDefault(p =>
                        string.Equals(p.Dest, full, StringComparison.OrdinalIgnoreCase));
                    if (pair.Source != null) File.Copy(pair.Source, pair.Dest, overwrite: true);
                }
            }

            // 最后一轮仍失败：回退全部。
            if (iteration == maxIterations - 1)
            {
                foreach (var pair in sourceFiles)
                    File.Copy(pair.Source, pair.Dest, overwrite: true);
                success = false;
            }
        }

        diagnostics.AddRange(rollbackDiagnostics);
        diagnostics.AddRange(acceptedDiagnostics);

        var preprocessedSourcePath = ProjectPreprocessSource.MapToIntermediate(layout, intermediateRoot);
        var restoreTarget = ProjectPreprocessSource.ResolveRestoreTarget(layout, intermediateRoot);
        if (restoreTarget != null)
        {
            if (request.Verbose) Console.WriteLine($"Restoring packages: {restoreTarget}");
            var restoreExitCode = ProjectPreprocessSource.RunDotNetRestore(restoreTarget, "[java-keywords]");
            if (restoreExitCode != 0)
            {
                diagnostics.Add(new JavaKeywordDiagnostic(
                    JavaKeywordSeverity.Error,
                    JavaKeywordDiagnosticCode.JK040,
                    $"dotnet restore failed with exit code {restoreExitCode} for {restoreTarget}."));
                success = false;
            }
        }

        return new ProjectJavaReservedWordPreprocessResult
        {
            OriginalSourcePath = layout.EntryPath,
            SourceRoot = sourceRoot,
            IntermediateRoot = intermediateRoot,
            PreprocessedSourcePath = preprocessedSourcePath,
            Success = success && !diagnostics.Any(d => d.Severity == JavaKeywordSeverity.Error),
            Statistics = acceptedStats,
            Diagnostics = diagnostics,
        };
    }

    private static CSharpCompilation BuildCompilation(
        IEnumerable<(string Path, string Content)> files,
        ProjectPreprocessSourceLayout layout,
        string intermediateRoot)
    {
        var trees = new List<SyntaxTree>();
        foreach (var (path, content) in files)
        {
            trees.Add(CSharpSyntaxTree.ParseText(
                content,
                new CSharpParseOptions(LanguageVersion.Latest),
                path: path));
        }

        var globalUsings = new System.Text.StringBuilder();
        foreach (var projectFile in layout.ProjectFiles
                     .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            var intermediateProjectFile = Path.Combine(
                intermediateRoot, Path.GetRelativePath(layout.SourceRoot, projectFile));
            var projectUsings = ImplicitGlobalUsings.BuildForProject(intermediateProjectFile, intermediateRoot);
            if (projectUsings != null) globalUsings.AppendLine(projectUsings);
        }
        if (globalUsings.Length > 0)
        {
            const string syntheticPath = "__cs2j_global_usings.g.cs";
            trees.Add(CSharpSyntaxTree.ParseText(
                globalUsings.ToString(),
                new CSharpParseOptions(LanguageVersion.Latest),
                path: syntheticPath));
        }

        return CSharpCompilation.Create(
            "java-keywords-preprocess",
            trees,
            ProjectUnsignedTypePreprocessor.GetBasicReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
    }

    /// <summary>(相对中间根路径, 错误码, 消息) 集合。</summary>
    private static HashSet<(string File, string Id, string Message)> BuildErrorSet(
        IEnumerable<(string Path, string Content)> files,
        ProjectPreprocessSourceLayout layout,
        string intermediateRoot)
    {
        var compilation = BuildCompilation(files, layout, intermediateRoot);
        return new HashSet<(string, string, string)>(
            compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error && d.Location.SourceTree != null)
                .Select(d => (
                    Normalize(d.Location.SourceTree!.FilePath, intermediateRoot),
                    d.Descriptor.Id,
                    d.GetMessage())));
    }

    private static List<Diagnostic> DiffErrors(
        HashSet<(string File, string Id, string Message)> baseline,
        CSharpCompilation candidate,
        string intermediateRoot)
    {
        return candidate.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error
                && d.Location.SourceTree != null
                && !baseline.Contains((
                    Normalize(d.Location.SourceTree!.FilePath, intermediateRoot),
                    d.Descriptor.Id,
                    d.GetMessage())))
            .ToList();
    }

    private static string Normalize(string path, string intermediateRoot)
    {
        var full = Path.GetFullPath(path);
        return Path.GetRelativePath(intermediateRoot, full).Replace('\\', '/');
    }

    private static bool FileStartsWithBom(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> prefix = stackalloc byte[3];
        var read = stream.Read(prefix);
        return read >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF;
    }
}
