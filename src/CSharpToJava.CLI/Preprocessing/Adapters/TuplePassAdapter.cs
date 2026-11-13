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

    /// <summary>本次运行的项目树上下文；用于把语法树映射回所属项目。</summary>
    private PassTreeContext? _tree;

    /// <summary>源文件 → 所属项目观测到的运行时元数（按项目作用域）。null 表示无项目图，统一使用 <see cref="_unionArities"/>。</summary>
    private IReadOnlyDictionary<string, IReadOnlyCollection<int>>? _aritiesByFile;

    /// <summary>项目文件 → 该项目观测到的运行时元数（项目内所有文件的并集）。</summary>
    private IReadOnlyDictionary<string, IReadOnlyCollection<int>> _aritiesByProject =
        new Dictionary<string, IReadOnlyCollection<int>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>整棵树的观测并集，仅用于无法归属到项目时的保守兜底。</summary>
    private IReadOnlyCollection<int> _unionArities = Array.Empty<int>();

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
        => new(LanguageVersion.Latest, preprocessorSymbols: new[] { "NETCOREAPP" });
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

    public override Task OnTreePreparedAsync(PassTreeContext tree)
    {
        _tree = tree;
        return Task.CompletedTask;
    }

    /// <summary>
    /// typeof(ValueTuple&lt;...&gt;) 观察值元组的运行时身份。降级后的 struct 不再是泛型
    /// ValueTuple，<c>Type.IsGenericType</c> / <c>GetGenericTypeDefinition()</c> 驱动的
    /// 反射消费者（如 System.Text.Json 的 JsonConverterFactory.CanConvert）会静默失配。
    /// 观测必须按**项目**作用域生效：合并编译单元里某个项目（如 json 测试）的
    /// <c>typeof(ValueTuple&lt;,&gt;)</c> 不得让另一个项目（如 regex）的二元组也被保留为
    /// 原生 ValueTuple（Java 侧映射成 io.vavr.Tuple2 后具名元素访问会编译失败）。
    /// 同一项目内仍按 arity 取并集，保证声明点与使用点形态一致（混用会 CS0029）。
    /// </summary>
    public override Task CompilationReadyAsync(CSharpCompilation compilation)
    {
        var observedByFile = new Dictionary<string, IReadOnlyCollection<int>>(
            StringComparer.OrdinalIgnoreCase);
        var union = new HashSet<int>();
        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            if (string.IsNullOrEmpty(syntaxTree.FilePath))
            {
                continue;
            }

            var root = syntaxTree.GetRoot();
            var arities = new HashSet<int>(
                TupleToStructPreprocessor.FindRuntimeObservedTupleArities(root));

            // 条件编译分支在本 pass 的固定符号集下是 DisabledText，其中的元组语法
            // 无法参与改写，只能原样保留；同 arity 的声明若被降级就会与该分支混用
            // （CS0030/CS0029，语料 CharSetSolver 的 `#if DEBUG` foreach 解构）。
            // 与 typeof(ValueTuple<...>) 一样按项目作用域把该 arity 保持原生。
            arities.UnionWith(TupleToStructPreprocessor.FindUnrewritableTupleArities(root));
            if (arities.Count == 0)
            {
                continue;
            }

            observedByFile[syntaxTree.FilePath] = arities;
            union.UnionWith(arities);
        }

        _unionArities = union;
        _aritiesByProject =
            new Dictionary<string, IReadOnlyCollection<int>>(StringComparer.OrdinalIgnoreCase);

        if (_tree == null || _tree.Layout.ProjectFiles.Count == 0)
        {
            // 无项目图（纯目录转换）时没有项目边界，只能保持整单元并集的旧语义。
            _aritiesByFile = null;
            return Task.CompletedTask;
        }

        var projects = BuildProjectCompileScopes(_tree);
        _aritiesByProject = TupleRuntimeIdentityScope.ComputeProjectArities(
            projects, observedByFile);
        _aritiesByFile = TupleRuntimeIdentityScope.MapFileArities(
            projects, observedByFile, _tree.CsFiles);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 项目 → 实际编译文件集合。优先按 csproj 的 Compile Include/Remove 与 SDK
    /// 默认 glob 求值（<see cref="ProjectCompileSet"/>）；无法解析时集合为 null，
    /// 归属退化为"最近的 csproj 目录前缀"（近似，见 MapFileArities）。
    /// 必须对**源树**求值：编译准备阶段中间树尚未复制 .cs 文件（它们在逐文件改写
    /// 时才落盘），对中间树求值会得到空集合；统一目录（in-place）链里源树就是
    /// 中间树，二者等价。
    /// </summary>
    private List<TupleProjectCompileScope> BuildProjectCompileScopes(PassTreeContext tree)
    {
        var projects = new List<TupleProjectCompileScope>();
        foreach (var projectFile in tree.Layout.ProjectFiles
                     .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            var compiled = ProjectCompileSet.ComputeCompiledFiles(
                projectFile, tree.Layout.SourceRoot);

            projects.Add(new TupleProjectCompileScope(
                Path.GetFullPath(projectFile),
                Path.GetDirectoryName(Path.GetFullPath(projectFile))!,
                compiled?.Select(Path.GetFullPath).ToList()));
        }

        return projects;
    }

    /// <summary>文件所属项目观测到的元数；无法归属时保守使用整树并集。</summary>
    private IReadOnlyCollection<int> ResolveRuntimeObservedArities(string sourceFilePath)
    {
        if (_aritiesByFile != null
            && _aritiesByFile.TryGetValue(Path.GetFullPath(sourceFilePath), out var arities))
        {
            return arities;
        }

        return _unionArities;
    }

    /// <summary>项目观测到的元数；映射缺失时保守使用整树并集。</summary>
    private IReadOnlyCollection<int> ResolveProjectArities(string projectFile)
        => _aritiesByProject.TryGetValue(Path.GetFullPath(projectFile), out var arities)
            ? arities
            : _unionArities;

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        var fileDiagnostics = new List<TupleToStructDiagnostic>();
        var result = _preprocessor.RewriteFile(
            context.SourceCode,
            context.SemanticModel!,
            _statistics,
            fileDiagnostics,
            ResolveRuntimeObservedArities(context.SourceFilePath));

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
           $"{_statistics.TupleLocalsNormalizedToVar} tuple locals -> var, " +
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

        // 源生成器/分析器项目必须自带它用到的所有形状：编译器加载分析器时不会一并解析
        // 它的依赖程序集，复用被引用程序集里的生成 struct 会让生成器在加载期
        // FileNotFoundException 而静默失效（CS8785 → 消费方 CS8795）。
        var analyzerProjects = FindAnalyzerProjects(projectFiles, intermediateRoot, sourceRoot);

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

        // 公共形状的定义者集合。生成 struct 按逻辑元数共享（同一 arity 只有一个定义），
        // 因此同一元组的使用方必须看到**同一个** C# 类型；若各自生成一份，跨程序集传递
        // 元组时两边类型不同（CS0436 + CS1503）。
        //
        // 归属规则：先在"弱连通引用分量"内选一个所有使用方都能（传递）引用到的项目作为
        // 唯一定义者——方向反了会把定义放进最下游的消费者程序集，上游程序集便看不到该
        // 类型；分量内确实没有公共可达项目时，退化为各使用方分别定义一份并告警。
        var componentOf = ComputeWeakComponents(projectFiles, referenceGraph);
        var ownersByShape = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (key, users) in usersByShape)
        {
            if (users.Count == 1 || !_preprocessor.IsShapePubliclyAccessible(key))
            {
                continue;
            }

            var owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unshareable = new List<string>();
            foreach (var group in users.GroupBy(user => componentOf[user]))
            {
                var componentProjects = projectFiles.Where(project =>
                    componentOf[project] == group.Key
                    && !analyzerProjects.Contains(project)
                    && _preprocessor.CanGenerateShape(key, ResolveProjectArities(project)));
                var owner = group
                    .Concat(componentProjects)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(candidate =>
                        group.All(other => IsReachable(other, candidate, referenceGraph)));
                if (owner != null)
                {
                    owners.Add(owner);
                    continue;
                }

                unshareable.AddRange(group);
            }

            foreach (var user in unshareable)
            {
                owners.Add(user);
            }

            ownersByShape[key] = owners;

            if (unshareable.Count > 0)
            {
                _diagnostics.Add(new TupleToStructDiagnostic(
                    TupleToStructSeverity.Warning,
                    $"Tuple shape '{key}' is used by projects that do not reference each other " +
                    $"({string.Join(", ", unshareable.Select(Path.GetFileNameWithoutExtension))}); " +
                    "a copy is generated into each of them."));
            }
        }

        // 项目 → 它必须定义的形状。
        var defineByProject = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);

        void AddShape(string projectFile, string key)
        {
            if (!defineByProject.TryGetValue(projectFile, out var set))
            {
                set = new SortedSet<string>(StringComparer.Ordinal);
                defineByProject[projectFile] = set;
            }

            set.Add(key);
        }

        foreach (var (projectFile, needed) in shapesByProject)
        {
            var selfContained = analyzerProjects.Contains(projectFile);
            foreach (var key in needed)
            {
                var mustDefine = selfContained
                    || !_preprocessor.IsShapePubliclyAccessible(key)
                    || !ownersByShape.TryGetValue(key, out var owners)
                    || owners.Count == 0
                    || owners.Contains(projectFile);
                if (mustDefine)
                {
                    AddShape(projectFile, key);
                }
            }
        }

        // 定义者可能自己并不使用该元组（例如被所有使用方引用的核心库），仍必须为它生成。
        foreach (var (key, owners) in ownersByShape)
        {
            foreach (var owner in owners)
            {
                AddShape(owner, key);
            }
        }

        var multipleFiles = defineByProject.Count > 1;
        foreach (var (projectFile, shapeKeys) in defineByProject)
        {
            var projectName = Path.GetFileNameWithoutExtension(projectFile);
            var fileName = multipleFiles
                ? $"_Cs2jTupleTypes.{projectName}.cs"
                : _options.GeneratedFileName;

            // 该项目的运行时身份观测只在本项目内约束生成：别的项目观测到的 arity
            // 不得让本项目的形状既不降级也不生成（悬空引用）。
            // 分析器项目自带一份 internal 定义，避免与被引用程序集的同名 struct 冲突。
            var content = _preprocessor.GenerateStructFile(
                _preprocessor.CloseShapeKeys(shapeKeys),
                ResolveProjectArities(projectFile),
                internalAccessibility: analyzerProjects.Contains(projectFile));
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

    /// <summary>
    /// 项目引用图的弱连通分量编号。形状归属只在分量内讨论：不同分量之间没有任何类型
    /// 流动，各自定义同名 struct 不会冲突。
    /// </summary>
    private static Dictionary<string, int> ComputeWeakComponents(
        IReadOnlyList<string> projectFiles,
        Dictionary<string, HashSet<string>> graph)
    {
        var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projectFiles)
        {
            parent[project] = project;
        }

        string Find(string project)
        {
            var root = project;
            while (!string.Equals(parent[root], root, StringComparison.OrdinalIgnoreCase))
            {
                root = parent[root];
            }

            while (!string.Equals(parent[project], root, StringComparison.OrdinalIgnoreCase))
            {
                var next = parent[project];
                parent[project] = root;
                project = next;
            }

            return root;
        }

        void Union(string left, string right)
        {
            if (!parent.ContainsKey(left) || !parent.ContainsKey(right))
            {
                return;
            }

            var leftRoot = Find(left);
            var rightRoot = Find(right);
            if (!string.Equals(leftRoot, rightRoot, StringComparison.OrdinalIgnoreCase))
            {
                parent[leftRoot] = rightRoot;
            }
        }

        foreach (var (from, targets) in graph)
        {
            foreach (var target in targets)
            {
                Union(from, target);
            }
        }

        var componentIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projectFiles)
        {
            var root = Find(project);
            if (!componentIds.TryGetValue(root, out var id))
            {
                id = componentIds.Count;
                componentIds[root] = id;
            }

            result[project] = id;
        }

        return result;
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

    /// <summary>
    /// 被其他项目以 <c>OutputItemType="Analyzer"</c> 引用的项目（源生成器/分析器）。
    /// </summary>
    private static HashSet<string> FindAnalyzerProjects(
        IReadOnlyList<string> projectFiles,
        string intermediateRoot,
        string sourceRoot)
    {
        var byDirectory = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectFile in projectFiles)
        {
            var intermediateProjectFile = Path.Combine(
                intermediateRoot, Path.GetRelativePath(sourceRoot, projectFile));
            byDirectory[NormalizeDirectory(Path.GetDirectoryName(Path.GetFullPath(intermediateProjectFile))!)] =
                projectFile;
        }

        var analyzers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectFile in projectFiles)
        {
            var intermediateProjectFile = Path.Combine(
                intermediateRoot, Path.GetRelativePath(sourceRoot, projectFile));
            foreach (var reference in ReadAnalyzerProjectReferences(intermediateProjectFile))
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(reference));
                if (directory != null
                    && byDirectory.TryGetValue(NormalizeDirectory(directory), out var target))
                {
                    analyzers.Add(target);
                }
            }
        }

        return analyzers;
    }

    private static string NormalizeDirectory(string directory) =>
        directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// 读取 <c>OutputItemType="Analyzer"</c> 的 ProjectReference（解析失败返回空集）。
    /// </summary>
    private static IReadOnlyList<string> ReadAnalyzerProjectReferences(string intermediateProjectFile)
    {
        if (!File.Exists(intermediateProjectFile))
        {
            return Array.Empty<string>();
        }

        XDocument document;
        try
        {
            document = XDocument.Load(intermediateProjectFile, LoadOptions.None);
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }

        var root = document.Root;
        if (root == null)
        {
            return Array.Empty<string>();
        }

        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(intermediateProjectFile))!;
        var properties = MSBuildPropertyReader.Read(projectDirectory, projectDirectory, root);
        var result = new List<string>();
        foreach (var element in root.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
        {
            if (!string.Equals(
                    (string?)element.Attribute("OutputItemType"),
                    "Analyzer",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var include = (string?)element.Attribute("Include");
            if (string.IsNullOrWhiteSpace(include))
            {
                continue;
            }

            var expanded = MSBuildPropertyReader.Expand(include!, properties, projectDirectory);
            try
            {
                result.Add(Path.GetFullPath(Path.Combine(projectDirectory, expanded)));
            }
            catch (Exception)
            {
                // 非法路径忽略。
            }
        }

        return result;
    }

    /// <summary>
    /// 读取项目引用时忽略 <c>OutputItemType="Analyzer"</c> 的边：源生成器/分析器由编译器
    /// 在独立加载上下文中运行，它自己的依赖程序集不会被一起加载。若让分析器复用被引用
    /// 程序集里的生成 struct，加载期就会 FileNotFoundException，整个生成器静默失效
    /// （CS8785 → 消费方 CS8795）。分析器因此必须与其他项目分开成独立分量、自行定义形状。
    /// </summary>
    private static IReadOnlyList<string> ReadNonAnalyzerProjectReferences(string intermediateProjectFile)
    {
        if (!File.Exists(intermediateProjectFile))
        {
            return Array.Empty<string>();
        }

        XDocument document;
        try
        {
            document = XDocument.Load(intermediateProjectFile, LoadOptions.None);
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }

        var root = document.Root;
        if (root == null)
        {
            return Array.Empty<string>();
        }

        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(intermediateProjectFile))!;
        var properties = MSBuildPropertyReader.Read(projectDirectory, projectDirectory, root);
        var result = new List<string>();
        foreach (var element in root.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
        {
            if (string.Equals(
                    (string?)element.Attribute("OutputItemType"),
                    "Analyzer",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var include = (string?)element.Attribute("Include");
            if (string.IsNullOrWhiteSpace(include))
            {
                continue;
            }

            var expanded = MSBuildPropertyReader.Expand(include!, properties, projectDirectory);
            try
            {
                result.Add(Path.GetFullPath(Path.Combine(projectDirectory, expanded)));
            }
            catch (Exception)
            {
                // 非法路径忽略。
            }
        }

        return result;
    }

    private static Dictionary<string, HashSet<string>> BuildProjectReferenceGraph(
        IReadOnlyList<string> projectFiles,
        string intermediateRoot,
        string sourceRoot)
    {
        var byDirectory = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectFile in projectFiles)
        {
            // ProjectReference 的解析结果是中间树里的绝对路径，索引也必须用中间树
            // 路径；用源树路径索引会让所有引用边丢失（被误判为互不引用）。
            var intermediateProjectFile = Path.Combine(
                intermediateRoot, Path.GetRelativePath(sourceRoot, projectFile));
            var directory = Path.GetDirectoryName(Path.GetFullPath(intermediateProjectFile))!
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
            foreach (var reference in ReadNonAnalyzerProjectReferences(intermediateProjectFile))
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

        // 生成文件与 csproj 同目录（单工程直挂源根的布局）时，SDK 默认 globbing 已
        // 自动包含该文件，再追加显式 Compile 会触发 NETSDK1022 重复项（多 pass 链中
        // 会与后续/前序 pass 的生成文件同时重复）；仅在 EnableDefaultCompileItems=false
        // 的工程里才需要显式挂接。多工程布局 Include=..\文件，必须显式挂接。
        var helperDirectory = Path.GetDirectoryName(Path.GetFullPath(sharedFilePath));
        var sameDirectory = string.Equals(
            helperDirectory,
            Path.GetFullPath(projectDirectory),
            StringComparison.OrdinalIgnoreCase);
        var defaultCompileItemsDisabled = string.Equals(
            GetPropertyValue(root, "EnableDefaultCompileItems"),
            "false",
            StringComparison.OrdinalIgnoreCase);
        if (!sameDirectory || defaultCompileItemsDisabled)
        {
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
        }

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

/// <summary>
/// 一个项目的编译作用域快照：项目文件、项目目录和它实际编译的源文件。
/// <see cref="CompiledFiles"/> 为 null 表示 csproj 无法解析，归属退化为目录前缀近似。
/// </summary>
internal sealed record TupleProjectCompileScope(
    string ProjectFile,
    string ProjectDirectory,
    IReadOnlyCollection<string>? CompiledFiles);

/// <summary>
/// 把 <c>typeof(ValueTuple&lt;...&gt;)</c> 的运行时身份观测映射到项目/文件作用域。
/// 规则：
/// <list type="bullet">
/// <item>项目内所有文件的观测取并集（arity 级一致，避免 struct / ValueTuple 混用 CS0029）；</item>
/// <item>同一文件只被一个项目编译 → 该项目集合；被多个项目共享（链接源码）→ 取并集，
/// 因为改写结果只有一份文本、必须同时满足所有编译它的项目；</item>
/// <item>无法从 Compile 集合归属时 → 最近的 csproj 目录前缀（近似）；仍无法归属 → 整树并集。</item>
/// </list>
/// 跨项目绝不合并：只有观测到该 arity 的项目才保持原生 ValueTuple。
/// </summary>
internal static class TupleRuntimeIdentityScope
{
    internal static Dictionary<string, IReadOnlyCollection<int>> ComputeProjectArities(
        IReadOnlyList<TupleProjectCompileScope> projects,
        IReadOnlyDictionary<string, IReadOnlyCollection<int>> observedByFile)
    {
        var result = new Dictionary<string, IReadOnlyCollection<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            var arities = new HashSet<int>();
            if (project.CompiledFiles != null)
            {
                foreach (var file in project.CompiledFiles)
                {
                    if (observedByFile.TryGetValue(file, out var fileArities))
                    {
                        arities.UnionWith(fileArities);
                    }
                }
            }
            else
            {
                // Compile 集合不可得：目录前缀内的观测全部算作该项目（近似）。
                foreach (var (file, fileArities) in observedByFile)
                {
                    if (IsUnderDirectory(file, project.ProjectDirectory))
                    {
                        arities.UnionWith(fileArities);
                    }
                }
            }

            result[project.ProjectFile] = arities;
        }

        return result;
    }

    internal static Dictionary<string, IReadOnlyCollection<int>> MapFileArities(
        IReadOnlyList<TupleProjectCompileScope> projects,
        IReadOnlyDictionary<string, IReadOnlyCollection<int>> observedByFile,
        IEnumerable<string>? additionalFiles = null)
    {
        var projectArities = ComputeProjectArities(projects, observedByFile);

        var ownersByFile = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            foreach (var file in project.CompiledFiles ?? Array.Empty<string>())
            {
                if (!ownersByFile.TryGetValue(file, out var owners))
                {
                    owners = new List<string>();
                    ownersByFile[file] = owners;
                }

                owners.Add(project.ProjectFile);
            }
        }

        var union = new HashSet<int>();
        foreach (var arities in observedByFile.Values)
        {
            union.UnionWith(arities);
        }

        var files = new HashSet<string>(observedByFile.Keys, StringComparer.OrdinalIgnoreCase);
        files.UnionWith(ownersByFile.Keys);
        if (additionalFiles != null)
        {
            files.UnionWith(additionalFiles.Select(Path.GetFullPath));
        }

        var result = new Dictionary<string, IReadOnlyCollection<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (ownersByFile.TryGetValue(file, out var owners))
            {
                if (owners.Count == 1)
                {
                    result[file] = projectArities[owners[0]];
                    continue;
                }

                // 共享源码只改写一次，必须同时满足所有编译它的项目：取并集（保守）。
                var shared = new HashSet<int>();
                foreach (var owner in owners)
                {
                    shared.UnionWith(projectArities[owner]);
                }

                result[file] = shared;
                continue;
            }

            var nearest = FindNearestProject(file, projects);
            result[file] = nearest != null
                ? projectArities[nearest.ProjectFile]
                : union;
        }

        return result;
    }

    private static TupleProjectCompileScope? FindNearestProject(
        string file,
        IReadOnlyList<TupleProjectCompileScope> projects)
    {
        TupleProjectCompileScope? best = null;
        var bestLength = -1;
        foreach (var project in projects)
        {
            if (!IsUnderDirectory(file, project.ProjectDirectory))
            {
                continue;
            }

            var length = Path.GetFullPath(project.ProjectDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length;
            if (length > bestLength)
            {
                best = project;
                bestLength = length;
            }
        }

        return best;
    }

    private static bool IsUnderDirectory(string file, string directory)
    {
        var fullFile = Path.GetFullPath(file);
        var fullDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return fullFile.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
    }
}
