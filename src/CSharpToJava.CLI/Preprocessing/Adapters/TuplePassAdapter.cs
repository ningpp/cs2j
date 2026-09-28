using System.Text;
using System.Xml.Linq;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.TupleToStruct;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the tuple-to-struct pass. Preserves the exact shared-statistics
/// accumulation, synthesized implicit-global-usings compilation, generated struct
/// distribution/ownership algorithm, Compile-Include wiring, BOM handling and
/// diagnostic wording of the former ProjectTupleToStructPreprocessor.
/// </summary>
internal sealed class TuplePassAdapter : ProjectPassAdapter
{
    private readonly TupleToStructOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;

    private readonly TupleToStructPreprocessor _preprocessor;
    private readonly List<TupleToStructDiagnostic> _diagnostics = new();
    private readonly TupleToStructStatistics _statistics = new();
    private readonly List<string> _rewrittenSourceFiles = new();
    private string? _generatedStructFilePath;

    public TuplePassAdapter(
        TupleToStructOptions options,
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes)
    {
        _options = options;
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
        _preprocessor = new TupleToStructPreprocessor(options);
    }

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.Tuple);
    public override string LogPrefix => "[tuple-to-struct]";
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override CSharpParseOptions CreateParseOptions(PassTreeContext tree)
        => new(LanguageVersion.Latest);
    public override string CompilationName => "tuple-preprocess";

    public TupleToStructStatistics Statistics => _statistics;
    public IReadOnlyList<TupleToStructDiagnostic> Diagnostics => _diagnostics;
    public string? GeneratedStructFilePath => _generatedStructFilePath;

    /// <summary>
    /// ImplicitUsings=enable projects rely on SDK-generated global usings; synthesize
    /// them so Type/Task names resolve instead of degrading tuple elements to object.
    /// </summary>
    public override IReadOnlyList<SyntaxTree> CreateAdditionalSyntaxTrees(PassTreeContext tree)
    {
        var globalUsings = new StringBuilder();
        foreach (var projectFile in tree.Layout.ProjectFiles
                     .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            var intermediateProjectFile = Path.Combine(
                tree.IntermediateRoot, Path.GetRelativePath(tree.Layout.SourceRoot, projectFile));
            var projectUsings = ImplicitGlobalUsings.BuildForProject(
                intermediateProjectFile, tree.IntermediateRoot);
            if (projectUsings != null)
            {
                globalUsings.AppendLine(projectUsings);
            }
        }

        if (globalUsings.Length == 0)
        {
            return Array.Empty<SyntaxTree>();
        }

        const string syntheticPath = "__cs2j_global_usings.g.cs";
        return new[]
        {
            CSharpSyntaxTree.ParseText(
                globalUsings.ToString(),
                new CSharpParseOptions(LanguageVersion.Latest),
                path: syntheticPath),
        };
    }

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        var fileDiagnostics = new List<TupleToStructDiagnostic>();
        var result = _preprocessor.RewriteFile(
            context.SourceCode, context.SemanticModel!, _statistics, fileDiagnostics);

        foreach (var diagnostic in fileDiagnostics)
        {
            _diagnostics.Add(diagnostic with { Message = $"{context.RelativePath}: {diagnostic.Message}" });
        }

        if (_verbose)
        {
            Console.WriteLine(
                $"[tuple-to-struct] {(result.Changed ? "transformed" : "clean (copy)")}: {context.RelativePath}");
        }

        if (result.Changed)
        {
            _rewrittenSourceFiles.Add(context.SourceFilePath);
            var rewrite = new PassFileRewrite
            {
                OutputCode = result.OutputCode,
                Changed = true,
                PreserveBom = true,
            };
            return Task.FromResult<PassFileRewrite?>(rewrite);
        }

        return Task.FromResult<PassFileRewrite?>(null);
    }

    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        // 生成文件的分配：每个编译单元只生成它真正需要自己定义的形状。元素类型非
        // public 的形状（含 internal 类型）在每个使用它的编译单元各生成一份；全
        // public 的形状只由"被其他使用方引用"的那个项目定义，其余使用方经项目引用复用。
        var generatedStructPaths = DistributeStructFiles(tree);
        _generatedStructFilePath = generatedStructPaths.FirstOrDefault();
        return Task.CompletedTask;
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new TupleToStructDiagnostic(
            TupleToStructSeverity.Error,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}. " +
            "Package references may not be resolved, leading to incorrect code generation."));

    public override void OnBuildFailed(int exitCode)
    {
        // Tuple-to-struct historically does not run verify builds.
    }

    public override void OnTestsFailed(int exitCode)
    {
        // Tuple-to-struct historically does not run verify tests.
    }

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == TupleToStructSeverity.Error);

    public override string BuildChainSummaryLine()
        => $"tuple-to-struct: {_statistics.FilesTransformed} transformed, " +
           $"{_statistics.FilesSkippedClean} clean, " +
           $"{_statistics.TupleTypesReplaced} types, " +
           $"{_statistics.TupleLiteralsRewritten} literals, " +
           $"{_statistics.DeconstructionsExpanded} deconstructions, " +
           $"{_statistics.StructsGenerated} structs generated";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Severity != TupleToStructSeverity.Info || verbose)
            .Select(d => $"[{d.Severity}] {d.Message}")
            .ToList();

    /// <summary>
    /// 分配并写出生成文件：每个项目只包含自己必须定义的形状，避免某个编译单元引用到
    /// 它看不见的类型，也避免同名公共类型重复出现在多个程序集。
    /// </summary>
    private IReadOnlyList<string> DistributeStructFiles(PassTreeContext tree)
    {
        var layout = tree.Layout;
        var intermediateRoot = tree.IntermediateRoot;
        var sourceRoot = tree.SourceRoot;
        var exclusionInfo = tree.Exclusion;

        var generatedPaths = new List<string>();
        var shapesByFile = _preprocessor.ShapesByFile;
        _statistics.StructsGenerated = _preprocessor.StructNames.Count;
        if (shapesByFile.Count == 0)
        {
            return generatedPaths;
        }

        var projectFiles = layout.ProjectFiles
            .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Where(file => !SupportedProjectFiltering.IsUnderExcludedProject(file, exclusionInfo))
            .ToList();

        // 项目 → 该项目编译的源文件集合、需要哪些形状。
        var shapesByProject = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectFile in projectFiles)
        {
            var intermediateProjectFile = Path.Combine(
                intermediateRoot, Path.GetRelativePath(sourceRoot, projectFile));
            var compiled = ProjectCompileSet.ComputeCompiledFiles(intermediateProjectFile, intermediateRoot);
            var needed = new HashSet<string>(StringComparer.Ordinal);
            shapesByProject[projectFile] = needed;
            if (compiled == null)
            {
                continue;
            }

            foreach (var (filePath, shapeKeys) in shapesByFile)
            {
                var intermediateFile = Path.GetFullPath(
                    Path.Combine(intermediateRoot, Path.GetRelativePath(sourceRoot, filePath)));
                if (!compiled.Contains(intermediateFile))
                {
                    continue;
                }

                foreach (var key in shapeKeys)
                {
                    needed.Add(key);
                }
            }
        }

        var referenceGraph = BuildProjectReferenceGraph(projectFiles, intermediateRoot, sourceRoot);

        // 形状 → 使用它的项目。
        var usersByShape = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (projectFile, needed) in shapesByProject)
        {
            foreach (var key in needed)
            {
                if (!usersByShape.TryGetValue(key, out var users))
                {
                    users = new List<string>();
                    usersByShape[key] = users;
                }

                users.Add(projectFile);
            }
        }

        // 公共形状的唯一所有者：被其他所有使用方（传递）引用的那个项目。
        var publicOwners = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, users) in usersByShape)
        {
            if (users.Count == 1 || !_preprocessor.IsShapePubliclyAccessible(key))
            {
                continue;
            }

            publicOwners[key] = users.FirstOrDefault(candidate =>
                users.All(other => IsReachable(candidate, other, referenceGraph)));
        }

        // 项目 → 它必须定义的形状。
        var defineByProject = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (projectFile, needed) in shapesByProject)
        {
            foreach (var key in needed)
            {
                var mustDefine = !_preprocessor.IsShapePubliclyAccessible(key)
                    || !publicOwners.TryGetValue(key, out var owner)
                    || owner == null
                    || string.Equals(owner, projectFile, StringComparison.OrdinalIgnoreCase);
                if (!mustDefine)
                {
                    continue;
                }

                if (!defineByProject.TryGetValue(projectFile, out var set))
                {
                    set = new SortedSet<string>(StringComparer.Ordinal);
                    defineByProject[projectFile] = set;
                }

                set.Add(key);
            }
        }

        // 互不引用的项目共享 public 形状时无法判定唯一归属：各自生成一份并提示。
        foreach (var (key, users) in usersByShape)
        {
            if (users.Count <= 1
                || !_preprocessor.IsShapePubliclyAccessible(key)
                || publicOwners.TryGetValue(key, out var owner) && owner != null)
            {
                continue;
            }

            _diagnostics.Add(new TupleToStructDiagnostic(
                TupleToStructSeverity.Warning,
                $"Tuple shape '{key}' is used by projects that do not reference each other " +
                $"({string.Join(", ", users.Select(Path.GetFileNameWithoutExtension))}); " +
                "a copy is generated into each of them."));
        }

        var multipleFiles = defineByProject.Count > 1;
        foreach (var (projectFile, shapeKeys) in defineByProject)
        {
            var projectName = Path.GetFileNameWithoutExtension(projectFile);
            var fileName = multipleFiles
                ? $"_Cs2jTupleTypes.{projectName}.cs"
                : _options.GeneratedFileName;

            var content = _preprocessor.GenerateStructFile(_preprocessor.CloseShapeKeys(shapeKeys));
            if (content == null)
            {
                continue;
            }

            var rootPath = Path.Combine(intermediateRoot, fileName);
            if (!File.Exists(rootPath))
            {
                File.WriteAllText(rootPath, content, new UTF8Encoding(false));
                generatedPaths.Add(rootPath);
            }

            var intermediateProjectFile = Path.Combine(
                intermediateRoot, Path.GetRelativePath(sourceRoot, projectFile));
            EnsureCompileItem(
                intermediateProjectFile,
                fileName,
                Path.GetDirectoryName(intermediateProjectFile)!,
                rootPath);

            if (_verbose)
            {
                Console.WriteLine(
                    $"[tuple-to-struct] {projectName}: {shapeKeys.Count} shape(s) -> {fileName}");
            }
        }

        if (defineByProject.Count == 0 && _verbose)
        {
            Console.WriteLine(
                "[tuple-to-struct] no project could be resolved for the generated shapes; " +
                "the struct file relies on SDK default globbing.");
        }

        return generatedPaths;
    }

    private static bool IsReachable(
        string from,
        string to,
        Dictionary<string, HashSet<string>> graph)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!graph.TryGetValue(from, out var direct))
        {
            return false;
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(direct);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (string.Equals(current, to, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!visited.Add(current))
            {
                continue;
            }

            if (graph.TryGetValue(current, out var next))
            {
                foreach (var item in next)
                {
                    pending.Push(item);
                }
            }
        }

        return false;
    }

    private static Dictionary<string, HashSet<string>> BuildProjectReferenceGraph(
        IReadOnlyList<string> projectFiles,
        string intermediateRoot,
        string sourceRoot)
    {
        var byDirectory = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectFile in projectFiles)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(projectFile))!
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            byDirectory[directory] = projectFile;
        }

        var graph = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectFile in projectFiles)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            graph[projectFile] = set;

            var intermediateProjectFile = Path.Combine(
                intermediateRoot, Path.GetRelativePath(sourceRoot, projectFile));
            foreach (var reference in ProjectCompileSet.ReadProjectReferences(intermediateProjectFile))
            {
                var referenceDirectory = Path.GetDirectoryName(Path.GetFullPath(reference));
                if (referenceDirectory == null)
                {
                    continue;
                }

                if (byDirectory.TryGetValue(
                        referenceDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        out var target))
                {
                    set.Add(target);
                }
            }
        }

        return graph;
    }

    private static void EnsureCompileItem(
        string projectFilePath,
        string fileName,
        string projectDirectory,
        string sharedFilePath)
    {
        if (!File.Exists(projectFilePath))
        {
            return;
        }

        XDocument document;
        try
        {
            document = XDocument.Load(projectFilePath, LoadOptions.PreserveWhitespace);
        }
        catch (Exception)
        {
            return;
        }

        var root = document.Root;
        if (root == null || !root.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var normalizedFileName = fileName.Replace('\\', '/');
        foreach (var compileElement in root.Descendants()
                     .Where(e => e.Name.LocalName == "Compile").ToList())
        {
            var include = (string?)compileElement.Attribute("Include");
            var link = (string?)compileElement.Attribute("Link");
            if (MatchesHelperFile(include, normalizedFileName)
                || MatchesHelperFile(link, normalizedFileName))
            {
                compileElement.Remove();
            }
        }

        var relativeInclude = Path.GetRelativePath(projectDirectory, sharedFilePath);
        root.Add(
            new XText(Environment.NewLine + "  "),
            new XElement(
                "ItemGroup",
                new XElement(
                    "Compile",
                    new XAttribute("Include", relativeInclude),
                    new XAttribute("Link", fileName))),
            new XText(Environment.NewLine));

        document.Save(projectFilePath, SaveOptions.DisableFormatting);
    }

    private static string? GetPropertyValue(XElement root, string propertyName)
    {
        return root.Descendants()
            .Where(element => element.Name.LocalName == propertyName)
            .Select(element => (string?)element.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static bool MatchesHelperFile(string? value, string normalizedFileName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value!.Replace('\\', '/');
        return normalized.Equals(normalizedFileName, StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("/" + normalizedFileName, StringComparison.OrdinalIgnoreCase);
    }
}
