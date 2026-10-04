using System.Text;
using System.Xml.Linq;
using CSharpToJava.Core.AnonymousTypeLift;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the C21 anonymous-type-lift pass. Standard spine (no ExecuteAsync
/// override): per-file rewriter plus AfterFiles distribution of the generated
/// internal sealed immutable class files. The distribution is the simplified
/// D3 variant of the tuple pass — the lifted classes are always internal and
/// every project gets a full closure copy, so there is no public-owner graph
/// and no reachability analysis.
/// </summary>
internal sealed class AnonymousTypeLiftPassAdapter : ProjectPassAdapter
{
    private readonly AnonymousTypeLiftOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;

    private readonly AnonymousTypeLiftPreprocessor _preprocessor;
    private readonly List<AnonymousTypeLiftDiagnostic> _diagnostics = new();
    private readonly AnonymousTypeLiftStatistics _statistics = new();
    private readonly List<string> _rewrittenSourceFiles = new();

    public AnonymousTypeLiftPassAdapter(
        AnonymousTypeLiftOptions options,
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes,
        bool verifyBuild,
        bool verifyTests)
    {
        _options = options;
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
        _verifyBuild = verifyBuild;
        _verifyTests = verifyTests;
        _preprocessor = new AnonymousTypeLiftPreprocessor(options);
    }

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.AnonymousTypeLift);
    public override string LogPrefix => "[anonymous-type-lift]";
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;

    // §5.1/§13 Q3: both Latest and the NETCOREAPP preprocessor symbol are
    // required (the E06 override historically dropped the base symbol).
    public override CSharpParseOptions CreateParseOptions(PassTreeContext tree)
        => new(LanguageVersion.Latest, preprocessorSymbols: new[] { "NETCOREAPP" });

    public override string CompilationName => "anonymous-type-lift-preprocess";

    public AnonymousTypeLiftStatistics Statistics => _statistics;
    public IReadOnlyList<AnonymousTypeLiftDiagnostic> Diagnostics => _diagnostics;

    /// <summary>
    /// ImplicitUsings=enable projects rely on SDK-generated global usings; synthesize
    /// them so the lifted property types resolve instead of degrading to object.
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
        var fileDiagnostics = new List<AnonymousTypeLiftDiagnostic>();
        var result = _preprocessor.RewriteFile(
            context.SourceCode, context.SemanticModel!, _statistics, fileDiagnostics);

        foreach (var diagnostic in fileDiagnostics)
        {
            _diagnostics.Add(diagnostic with { Message = $"{context.RelativePath}: {diagnostic.Message}" });
        }

        if (_verbose)
        {
            Console.WriteLine(
                $"[anonymous-type-lift] {(result.Changed ? "transformed" : "clean (copy)")}: {context.RelativePath}");
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
        DistributeTypeFiles(tree);
        return Task.CompletedTask;
    }

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new AnonymousTypeLiftDiagnostic(
            "AL050",
            AnonymousTypeLiftSeverity.Warning,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}. " +
            "Package references may not be resolved, leading to incorrect code generation."));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new AnonymousTypeLiftDiagnostic(
            "AL050",
            AnonymousTypeLiftSeverity.Error,
            $"dotnet build failed with exit code {exitCode} for the anonymous-type-lift " +
            "intermediate tree; the generated type file or a rewritten creation site did " +
            "not compile. Review the build output above."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new AnonymousTypeLiftDiagnostic(
            "AL050",
            AnonymousTypeLiftSeverity.Error,
            $"dotnet test failed with exit code {exitCode} for the anonymous-type-lift " +
            "intermediate tree; the lifted types do not behave like the original " +
            "anonymous types. Review the test output above."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == AnonymousTypeLiftSeverity.Error);

    public override string BuildChainSummaryLine()
        => $"anonymous-type-lift: {_statistics.FilesTransformed} transformed, " +
           $"{_statistics.FilesSkippedClean} clean, " +
           $"{_statistics.CreationsRewritten} creations rewritten, " +
           $"{_statistics.ShapesRegistered} shapes " +
           $"({_statistics.GenericShapes} generic, {_statistics.DynamicShapes} dynamic), " +
           $"{_statistics.TypesGenerated} types generated, " +
           $"{_statistics.ResidualCreations} residual";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Severity != AnonymousTypeLiftSeverity.Info || verbose)
            .Select(d => $"[{d.Severity}] {d.Message}")
            .ToList();

    /// <summary>
    /// 分发并写出生成文件（D3 简化版）：生成类恒 internal，每个实际编译了被改写
    /// 源文件的项目各得一份形状嵌套闭包的全集；不建引用图、无 owner 判定。
    /// </summary>
    private void DistributeTypeFiles(PassTreeContext tree)
    {
        var layout = tree.Layout;
        var intermediateRoot = tree.IntermediateRoot;
        var sourceRoot = tree.SourceRoot;
        var exclusionInfo = tree.Exclusion;

        var shapesByFile = _preprocessor.ShapesByFile;
        _statistics.TypesGenerated = _preprocessor.TypeNames.Count;
        if (shapesByFile.Count == 0)
        {
            return;
        }

        var projectFiles = layout.ProjectFiles
            .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Where(file => !SupportedProjectFiltering.IsUnderExcludedProject(file, exclusionInfo))
            .ToList();

        // 项目 → 该项目编译的改写文件用到的形状键（嵌套闭包在生成前展开）。
        // 只保留至少需要一个形状的项目（单/多工程命名据此判定）。
        var shapesByProject = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectFile in projectFiles)
        {
            var intermediateProjectFile = Path.Combine(
                intermediateRoot, Path.GetRelativePath(sourceRoot, projectFile));
            var compiled = ProjectCompileSet.ComputeCompiledFiles(intermediateProjectFile, intermediateRoot);
            if (compiled == null)
            {
                continue;
            }

            HashSet<string>? needed = null;
            foreach (var (filePath, shapeKeys) in shapesByFile)
            {
                var intermediateFile = Path.GetFullPath(
                    Path.Combine(intermediateRoot, Path.GetRelativePath(sourceRoot, filePath)));
                if (!compiled.Contains(intermediateFile))
                {
                    continue;
                }

                needed ??= new HashSet<string>(StringComparer.Ordinal);
                foreach (var key in shapeKeys)
                {
                    needed.Add(key);
                }
            }

            if (needed is { Count: > 0 })
            {
                shapesByProject[projectFile] = needed;
            }
        }

        var multipleFiles = shapesByProject.Count > 1;
        var filesWritten = 0;
        foreach (var (projectFile, needed) in shapesByProject)
        {
            var projectName = Path.GetFileNameWithoutExtension(projectFile);
            var fileName = multipleFiles
                ? $"_Cs2jAnonymousTypes.{projectName}.cs"
                : _options.GeneratedFileName;

            var closedKeys = _preprocessor.CloseShapeKeys(needed);
            var content = _preprocessor.GenerateTypeFile(closedKeys);
            if (content == null)
            {
                continue;
            }

            var rootPath = Path.Combine(intermediateRoot, fileName);
            if (!File.Exists(rootPath))
            {
                File.WriteAllText(rootPath, content, new UTF8Encoding(false));
                filesWritten++;
            }

            var intermediateProjectFile = Path.Combine(
                intermediateRoot, Path.GetRelativePath(sourceRoot, projectFile));
            EnsureCompileItem(
                intermediateProjectFile,
                fileName,
                Path.GetDirectoryName(intermediateProjectFile)!,
                rootPath);

            _diagnostics.Add(new AnonymousTypeLiftDiagnostic(
                "AL070",
                AnonymousTypeLiftSeverity.Info,
                $"Project '{projectName}': {closedKeys.Count} lifted type shape(s) " +
                $"distributed into {fileName}."));

            if (_verbose)
            {
                Console.WriteLine(
                    $"[anonymous-type-lift] {projectName}: {closedKeys.Count} shape(s) -> {fileName}");
            }
        }

        _statistics.CrossCompilationCopies = filesWritten;

        if (shapesByProject.Count == 0 && _verbose)
        {
            Console.WriteLine(
                "[anonymous-type-lift] no project could be resolved for the lifted shapes; " +
                "the generated type file relies on SDK default globbing.");
        }
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
