using System.Text;
using CSharpToJava.Core.EnumDeclaration;
using CSharpToJava.Core.Preprocessing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the C05 enum-declaration pass. Drives the same whole-tree
/// iteration shape as record-readonly: each round replays originals →
/// BindCompilation → RewriteFile per file → recompile → diff against the
/// baseline error set → attribute new errors to a single enum plan and roll
/// that plan back (ED031), or roll the whole run back (ED030). After the run a
/// semantic residual gate verifies §7.5 (no leftover targeted enum
/// declarations, System.Enum calls, governing-enum switches or unhandled
/// casts); findings are ED013 and escalate to Error under strict mode.
/// </summary>
internal sealed class EnumDeclarationPassAdapter : ProjectPassAdapter
{
    private readonly EnumDeclarationOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;
    private readonly bool _strict;

    private readonly List<EnumDeclarationDiagnostic> _diagnostics = new();
    private EnumDeclarationStatistics _statistics = new();

    // 本轮被接受的"归一目标"enum 全限定名（可行且未被排除），供 residual 闸门使用。
    private HashSet<string> _acceptedTargets = new(StringComparer.Ordinal);

    public EnumDeclarationPassAdapter(
        EnumDeclarationOptions options,
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes,
        bool verifyBuild,
        bool verifyTests,
        bool strict)
    {
        _options = options;
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
        _verifyBuild = verifyBuild;
        _verifyTests = verifyTests;
        _strict = strict;
    }

    public override PassDescriptor Descriptor => PassCatalog.ImplementedById(PassIds.EnumDeclaration);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes => _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;

    public EnumDeclarationStatistics Statistics => _statistics;
    public IReadOnlyList<EnumDeclarationDiagnostic> Diagnostics => _diagnostics;

    public override async Task ExecuteAsync(PassExecutionContext context)
    {
        var tree = context.Tree;
        var intermediateRoot = tree.IntermediateRoot;
        var sourceRoot = tree.SourceRoot;

        var sourceFiles = tree.CsFiles
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .Select(file => (
                Source: file,
                Dest: Path.Combine(intermediateRoot, Path.GetRelativePath(sourceRoot, file))))
            .ToList();

        foreach (var pair in sourceFiles)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pair.Dest)!);
            tree.ResetScratchFile(pair.Source, pair.Dest);
        }

        foreach (var asset in tree.SourceFiles)
        {
            if (asset.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || asset.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var assetDest = Path.Combine(
                intermediateRoot, Path.GetRelativePath(sourceRoot, asset));
            Directory.CreateDirectory(Path.GetDirectoryName(assetDest)!);
            tree.ResetScratchFile(asset, assetDest);
        }

        // 基线编译同时充当每轮循环的首个编译：每轮开头都要把全部 scratch 文件
        // 重置回原始字节，故其内容与基线（读自源文件）逐字节相同，无需重新解析。
        var baselineCompilation = BuildCompilation(
            sourceFiles.Select(p => (Path: p.Dest, Content: File.ReadAllText(p.Source))),
            tree.Layout, intermediateRoot);
        // filePath → 基线 SyntaxTree 映射：整轮只建一次，供「按文件取树」与增量候选
        // 编译（只替换被改写文件的重解析树）复用，取代每文件一次线性扫描。
        var treeByPath = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
        foreach (var baselineTree in baselineCompilation.SyntaxTrees)
            treeByPath[baselineTree.FilePath] = baselineTree;

        // 基线错误键索引：受影响集合内每棵树自身的 SemanticModel 诊断首次使用时绑定
        // （与使用点 walk 已完成的绑定重合，不再重复计费），跨轮复用安全。
        var baselineIndex = new BaselineErrorIndex(baselineCompilation, intermediateRoot);

        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var rollbackDiagnostics = new List<EnumDeclarationDiagnostic>();
        var acceptedStats = new EnumDeclarationStatistics();
        var acceptedDiagnostics = new List<EnumDeclarationDiagnostic>();
        var acceptedTargets = new HashSet<string>(StringComparer.Ordinal);
        var success = true;
        const int maxIterations = 1024;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            foreach (var pair in sourceFiles)
                tree.ResetScratchFile(pair.Source, pair.Dest);

            var passStats = new EnumDeclarationStatistics();
            var passDiagnostics = new List<EnumDeclarationDiagnostic>();
            var preprocessor = new EnumDeclarationPreprocessor(_options);
            var compilation = baselineCompilation;
            preprocessor.BindCompilation(compilation);
            preprocessor.ExcludePlans(excluded);

            // 跨枚举成员初值耦合：保持真 enum 的枚举 D（被排除/不可行/无计划），
            // 其成员初值引用了 S 的成员时，S 本轮必须一起回滚，否则 S.M 变成
            // static readonly 字段，D 的常量初值编译失败（CS0029，且无法归因，
            // 表现为 ED030）。传递闭包由 Core 不动点计算。
            var forcedRollbacks = preprocessor.ComputeForcedRollbacks(excluded);
            foreach (var planId in forcedRollbacks)
            {
                excluded.Add(planId);
                var forcedPlan = preprocessor.Plans.First(p => p.Id == planId);
                var (forcedFile, forcedLine, forcedCol) = forcedPlan.PrimaryLocation();
                rollbackDiagnostics.Add(new EnumDeclarationDiagnostic(
                    EnumDeclarationSeverity.Warning,
                    EnumDeclarationDiagnosticCode.ED031,
                    "another enum that remains a real enum has member initializers " +
                    "referencing members of this enum; this plan was rolled back together " +
                    "with it so those initializers stay compile-time constants, and the " +
                    "enum remains a real enum.",
                    forcedFile, forcedLine, forcedCol, forcedPlan.EnumQualifiedName));
            }
            if (forcedRollbacks.Count > 0) preprocessor.ExcludePlans(excluded);

            // 摘要级统计/诊断（ED010/ED020/ED040/ED001 等）本轮只落账一次。原先由首个
            // 文件的 RewriteFile 顺手落账；这里显式前置，使下面只对「被活动 plan 触碰」
            // 的文件做改写也保持诊断集合与顺序不变。
            preprocessor.PrimeSummary(passStats, passDiagnostics);

            // 只有出现在活动 plan 触碰清单里的文件才可能改变：其余文件 RewriteFile 必然
            // 返回 unchanged（activePlans 为空）。整树扫描（含原 declaresActive 的全文
            // DescendantNodes 扫描）对它们纯属开销，故直接跳过读盘与改写。
            var affected = preprocessor.ActiveTouchedFiles();
            var targetFiles = sourceFiles.Where(p => affected.Contains(p.Dest)).ToList();

            // 每个目标文件的使用点/声明改写彼此独立（改写器对 plan 只读，语义模型各自
            // 独立），故并行执行；统计与诊断按文件顺序在串行合并阶段汇总，保证与逐文件
            // 串行执行逐项一致。
            var outcomes = new FileRewriteOutcome[targetFiles.Count];
            Parallel.For(0, targetFiles.Count, i =>
            {
                var pair = targetFiles[i];
                var fileStats = new EnumDeclarationStatistics();
                var fileDiagnostics = new List<EnumDeclarationDiagnostic>();
                if (!treeByPath.TryGetValue(pair.Dest, out var syntaxTree))
                {
                    outcomes[i] = new FileRewriteOutcome(
                        pair,
                        new EnumDeclarationFileResult { OutputCode = string.Empty, Changed = false },
                        fileStats,
                        fileDiagnostics);
                    return;
                }

                var result = preprocessor.RewriteFile(
                    File.ReadAllText(pair.Dest),
                    compilation.GetSemanticModel(syntaxTree),
                    fileStats,
                    fileDiagnostics);
                outcomes[i] = new FileRewriteOutcome(pair, result, fileStats, fileDiagnostics);
            });

            var rewrittenByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var outcome in outcomes)
            {
                passStats.FilesScanned += outcome.Stats.FilesScanned;
                passStats.FilesSkippedClean += outcome.Stats.FilesSkippedClean;
                passStats.FilesTransformed += outcome.Stats.FilesTransformed;
                passDiagnostics.AddRange(outcome.Diagnostics);

                if (outcome.Result.Changed)
                {
                    File.WriteAllText(outcome.Pair.Dest, outcome.Result.OutputCode,
                        FileStartsWithBom(outcome.Pair.Source)
                            ? new UTF8Encoding(true)
                            : new UTF8Encoding(false));
                    rewrittenByFile[outcome.Pair.Dest] = outcome.Pair.Source;
                }

                if (_verbose)
                {
                    Console.WriteLine(
                        $"[enum-declaration] {(outcome.Result.Changed ? "transformed" : "clean (copy)")}: " +
                        $"{Path.GetRelativePath(sourceRoot, outcome.Pair.Source)}");
                }
            }

            // 本轮零改写短路：scratch 文件在循环开头已按原始字节重置，未被改写的文件
            // 逐字节保持原样，故候选树与基线树完全相同 → 新错误必然为空集。直接接受，
            // 省去候选整树重编译、候选 GetDiagnostics 与基线的惰性 GetDiagnostics。
            if (rewrittenByFile.Count == 0)
            {
                acceptedStats = passStats;
                acceptedDiagnostics = passDiagnostics;
                acceptedTargets = preprocessor.Plans
                    .Where(p => p.Feasible && !excluded.Contains(p.Id))
                    .Select(p => p.EnumQualifiedName)
                    .ToHashSet(StringComparer.Ordinal);
                break;
            }

            // 增量候选编译：候选与基线只有被改写的 ~105 棵语法树不同，故直接在基线上
            // ReplaceSyntaxTree，只重解析这些文件，省去 ~800 个未变文件的重复解析。树
            // 集合内容与顺序、编译选项、引用集均与整体重建逐项相同，故 GetDiagnostics
            // 的错误集合与顺序不变（等价性见提交说明）。
            var candidateCompilation = baselineCompilation;
            foreach (var destPath in rewrittenByFile.Keys)
            {
                if (!treeByPath.TryGetValue(destPath, out var oldTree)) continue;
                var newTree = CSharpSyntaxTree.ParseText(
                    File.ReadAllText(destPath),
                    new CSharpParseOptions(LanguageVersion.Latest),
                    path: oldTree.FilePath);
                candidateCompilation = candidateCompilation.ReplaceSyntaxTree(oldTree, newTree);
            }

            // 门控诊断只覆盖「本轮真正被改写的文件」：只有这些文件在基线/候选之间逐字节不同，
            // 也只有它们（及其声明级位置）才可能承载新错误。本仓已逐树实测：集合外 797 棵树
            // 的基线/候选 Error 多重集逐棵全等（见提交说明）。
            var affectedTreePaths = new HashSet<string>(
                rewrittenByFile.Keys, StringComparer.OrdinalIgnoreCase);

            // 基线/候选两侧用同一形状、同一文件集合，只取「受影响集合内每棵树的
            // SemanticModel 诊断」（parse + body + declaration + semantic）。集合之外的树
            // 在基线/候选间逐树诊断全等（本仓 797 棵逐棵实测 differ=0）。
            // 不再调用整编译 GetDeclarationDiagnostics()：本仓实测其 11037 条错误
            // (树+id+span+消息) 100% 已由所属树自身的 SemanticModel 诊断给出
            // (coveredAllTrees=11037/11037)，故该流对集合内判定是冗余的、对集合外无新增
            // (集合外逐树诊断相同 ⇒ 其上声明错误也相同)。
            var baselineTask = Task.Run(() => baselineIndex.Build(affectedTreePaths));
            var candidateTask = Task.Run(
                () => CandidateErrorList(candidateCompilation, affectedTreePaths));
            var baselineKeys = await baselineTask;
            var candidateErrorList = await candidateTask;
            var newErrors = candidateErrorList
                .Where(d => !baselineKeys.Contains(ErrorKey(d, intermediateRoot)))
                .ToList();

            if (newErrors.Count == 0)
            {
                acceptedStats = passStats;
                acceptedDiagnostics = passDiagnostics;
                acceptedTargets = preprocessor.Plans
                    .Where(p => p.Feasible && !excluded.Contains(p.Id))
                    .Select(p => p.EnumQualifiedName)
                    .ToHashSet(StringComparer.Ordinal);
                break;
            }

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
                foreach (var pair in sourceFiles)
                    tree.ResetScratchFile(pair.Source, pair.Dest);
                var first = newErrors[0];
                var span = first.Location.GetLineSpan().StartLinePosition;
                _diagnostics.Add(new EnumDeclarationDiagnostic(
                    EnumDeclarationSeverity.Error,
                    EnumDeclarationDiagnosticCode.ED030,
                    "whole-tree validation failed and the new error could not be attributed to a " +
                    $"single enum plan ({first.Descriptor.Id}: {first.GetMessage()}); all changes " +
                    "from this run were rolled back.",
                    first.Location.SourceTree?.FilePath,
                    span.Line + 1,
                    span.Character + 1,
                    null));
                acceptedStats = new EnumDeclarationStatistics();
                acceptedDiagnostics = new List<EnumDeclarationDiagnostic>();
                acceptedTargets = new HashSet<string>(StringComparer.Ordinal);
                success = false;
                break;
            }

            foreach (var planId in plansToRollback)
            {
                var plan = preprocessor.Plans.First(p => p.Id == planId);
                excluded.Add(planId);
                acceptedStats.PlansRolledBack++;
                var (file, line, col) = plan.PrimaryLocation();
                rollbackDiagnostics.Add(new EnumDeclarationDiagnostic(
                    EnumDeclarationSeverity.Warning,
                    EnumDeclarationDiagnosticCode.ED031,
                    "whole-tree validation attributed a new error to this enum plan; the plan " +
                    "and all of its touched files were rolled back atomically and the enum " +
                    "remains a real enum.",
                    file, line, col, plan.EnumQualifiedName));

                foreach (var touched in plan.TouchedFiles)
                {
                    var full = Path.GetFullPath(touched);
                    var pair = sourceFiles.FirstOrDefault(p =>
                        string.Equals(p.Dest, full, StringComparison.OrdinalIgnoreCase));
                    if (pair.Source != null) tree.ResetScratchFile(pair.Source, pair.Dest);
                }
            }

            if (iteration == maxIterations - 1)
            {
                foreach (var pair in sourceFiles)
                    tree.ResetScratchFile(pair.Source, pair.Dest);
                success = false;
            }
        }

        _diagnostics.AddRange(rollbackDiagnostics);
        _diagnostics.AddRange(acceptedDiagnostics);
        _statistics = acceptedStats;
        _acceptedTargets = acceptedTargets;

        // 与标准 spine 一致：只有启用 build/test 验证时才需要 restore 中间树；
        // --no-verify-build/--no-verify-tests 下整树 restore 纯属开销，直接跳过。
        var restoreExitCode = (VerifyBuild || VerifyTests) ? context.Restore() : 0;
        if (restoreExitCode != 0)
        {
            success = false;
        }

        context.Success = success;
        await Task.CompletedTask;
    }

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
        => throw new NotSupportedException("EnumDeclarationPassAdapter drives the file loop itself.");

    public override Task AfterFilesAsync(PassTreeContext tree)
    {
        // §7.5 语义级 residual 闸门：扫描最终中间树，任何仍绑定到归一目标类型的
        // enum 声明 / System.Enum 调用 / governing-enum switch / 未处理强转都计残余。
        if (_acceptedTargets.Count == 0) return Task.CompletedTask;

        var intermediateRoot = tree.IntermediateRoot;
        var sourceRoot = tree.SourceRoot;
        var files = tree.CsFiles
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .Select(file => Path.Combine(intermediateRoot, Path.GetRelativePath(sourceRoot, file)))
            .Where(File.Exists)
            .Select(path => (Path: path, Content: File.ReadAllText(path)))
            .ToList();

        var compilation = BuildCompilation(files, tree.Layout, intermediateRoot);
        var residualCount = 0;
        var samples = new List<string>();

        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            if (syntaxTree.FilePath == "__cs2j_global_usings.g.cs") continue;
            var model = compilation.GetSemanticModel(syntaxTree);
            var rootNode = syntaxTree.GetRoot();

            foreach (var enumDecl in rootNode.DescendantNodes().OfType<EnumDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(enumDecl) is { } symbol
                    && _acceptedTargets.Contains(symbol.ToDisplayString()))
                {
                    residualCount++;
                    if (samples.Count < 5)
                        samples.Add($"residual enum declaration '{symbol.ToDisplayString()}'");
                }
            }

            foreach (var invocation in rootNode.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method
                    || method.ContainingType.ToDisplayString() != "System.Enum"
                    || !method.IsStatic)
                {
                    continue;
                }

                var referenced = method.TypeArguments.FirstOrDefault()?.ToDisplayString();
                if (referenced == null
                    && invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression
                        is TypeOfExpressionSyntax tos)
                {
                    referenced = model.GetTypeInfo(tos.Type).Type?.ToDisplayString();
                }
                if (referenced != null && _acceptedTargets.Contains(referenced))
                {
                    residualCount++;
                    if (samples.Count < 5)
                        samples.Add($"residual System.Enum.{method.Name} call on '{referenced}'");
                }
            }

            foreach (var switchStmt in rootNode.DescendantNodes()
                         .OfType<SwitchStatementSyntax>())
            {
                var govType = model.GetTypeInfo(switchStmt.Expression).Type;
                if (govType != null && _acceptedTargets.Contains(govType.ToDisplayString()))
                {
                    residualCount++;
                    if (samples.Count < 5)
                        samples.Add($"residual enum-governing switch on '{govType.ToDisplayString()}'");
                }
            }

            foreach (var cast in rootNode.DescendantNodes().OfType<CastExpressionSyntax>())
            {
                var target = model.GetTypeInfo(cast.Type).Type?.ToDisplayString();
                var source = model.GetTypeInfo(cast.Expression).Type?.ToDisplayString();
                if ((target != null && _acceptedTargets.Contains(target))
                    || (source != null && _acceptedTargets.Contains(source)))
                {
                    residualCount++;
                    if (samples.Count < 5)
                        samples.Add($"residual cast involving enum target ({source} -> {target})");
                }
            }
        }

        if (residualCount > 0)
        {
            _statistics.ResidualCount += residualCount;
            _diagnostics.Add(new EnumDeclarationDiagnostic(
                _strict ? EnumDeclarationSeverity.Error : EnumDeclarationSeverity.Warning,
                EnumDeclarationDiagnosticCode.ED013,
                $"{residualCount} residual enum declaration/API/switch/cast site(s) " +
                $"remaining after enum-declaration preprocessing: {string.Join("; ", samples)}"));
        }

        return Task.CompletedTask;
    }

    /// <summary>单文件改写结果 + 该文件独占的统计/诊断，供串行合并阶段按序汇总。</summary>
    private sealed record FileRewriteOutcome(
        (string Source, string Dest) Pair,
        EnumDeclarationFileResult Result,
        EnumDeclarationStatistics Stats,
        IReadOnlyList<EnumDeclarationDiagnostic> Diagnostics);

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new EnumDeclarationDiagnostic(
            EnumDeclarationSeverity.Warning,
            EnumDeclarationDiagnosticCode.ED041,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}."));

    public override void OnBuildFailed(int exitCode)
    {
        // 构建失败由 runner 的整树错误集/verify 流程落账，这里不重复诊断。
    }

    public override void OnTestsFailed(int exitCode)
    {
        // 测试失败由 runner 的 verify 流程落账，这里不重复诊断。
    }

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == EnumDeclarationSeverity.Error);

    public override string BuildChainSummaryLine()
        => $"enum-declaration: {_statistics.EnumsRewritten} enum(s) rewritten " +
           $"(flags={_statistics.FlagsCount}, non-int={_statistics.NonIntCount}, " +
           $"explicit={_statistics.ExplicitCount}), {_statistics.FilesTransformed} file(s), " +
           $"{_statistics.PlansRolledBack} plan(s) rolled back, " +
           $"{_statistics.PlansDeferred} kept real (numeric-boundary), " +
           $"{_statistics.ResidualCount} residual";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Severity != EnumDeclarationSeverity.Info || verbose)
            .Select(d => d.ToMsBuildString())
            .ToList();

    private static CSharpCompilation BuildCompilation(
        IEnumerable<(string Path, string Content)> files,
        ProjectPreprocessSourceLayout layout,
        string intermediateRoot)
    {
        // 解析彼此独立（ParseText 纯函数），并行执行但按索引保序写入，得到的树内容与
        // 顺序与串行逐个解析完全相同。
        var fileList = files.ToList();
        var treeArray = new SyntaxTree[fileList.Count];
        Parallel.For(0, fileList.Count, i =>
        {
            var (path, content) = fileList[i];
            treeArray[i] = CSharpSyntaxTree.ParseText(
                content,
                new CSharpParseOptions(LanguageVersion.Latest),
                path: path);
        });
        var trees = new List<SyntaxTree>(treeArray);

        var globalUsings = new StringBuilder();
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
            "enum-declaration-preprocess",
            trees,
            ProjectUnsignedTypePreprocessor.GetBasicReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
    }

    /// <summary>归一化的「文件 + 诊断 Id + 消息」错误键；无源位置的诊断不参与。</summary>
    private static (string File, string Id, string Message) ErrorKey(
        Diagnostic diagnostic, string intermediateRoot)
        => (
            Normalize(diagnostic.Location.SourceTree!.FilePath, intermediateRoot),
            diagnostic.Descriptor.Id,
            diagnostic.GetMessage());

    /// <summary>
    /// 候选侧「受影响集合内逐树 SemanticModel 诊断」中命中 Error 且带源位置的错误，按
    /// SyntaxTrees 顺序返回。与 <see cref="BaselineErrorIndex"/> 的基线侧形状与文件集合
    /// 一一对应，二者相减即与整树 diff 同判定。
    /// </summary>
    private static List<Diagnostic> CandidateErrorList(
        CSharpCompilation candidate,
        IReadOnlySet<string> affectedTreePaths)
    {
        var errors = new List<Diagnostic>();
        foreach (var tree in candidate.SyntaxTrees)
        {
            if (!affectedTreePaths.Contains(tree.FilePath)) continue;
            foreach (var diagnostic in candidate.GetSemanticModel(tree).GetDiagnostics())
                Consider(diagnostic);
        }
        return errors;

        void Consider(Diagnostic diagnostic)
        {
            if (diagnostic.Severity == DiagnosticSeverity.Error
                && diagnostic.Location.SourceTree != null)
            {
                errors.Add(diagnostic);
            }
        }
    }

    /// <summary>
    /// 基线的「受影响集合内逐树 SemanticModel 诊断」错误键索引。每棵树自身的诊断首次
    /// 使用时绑定（与使用点 walk 的绑定重合，不再重复计费）。索引只随文件集合增长而增长，
    /// 跨轮复用安全：额外键的文件必不在本轮受影响集合内，不可能与本轮候选错误撞键
    /// （键含归一化文件路径）。
    /// </summary>
    private sealed class BaselineErrorIndex
    {
        private readonly CSharpCompilation _baseline;
        private readonly string _intermediateRoot;
        private readonly Dictionary<SyntaxTree, HashSet<(string File, string Id, string Message)>>
            _treeKeys = new();

        public BaselineErrorIndex(CSharpCompilation baseline, string intermediateRoot)
        {
            _baseline = baseline;
            _intermediateRoot = intermediateRoot;
        }

        public HashSet<(string File, string Id, string Message)> Build(
            IReadOnlySet<string> affectedTreePaths)
        {
            var keys = new HashSet<(string File, string Id, string Message)>();
            foreach (var tree in _baseline.SyntaxTrees)
            {
                if (!affectedTreePaths.Contains(tree.FilePath)) continue;
                if (!_treeKeys.TryGetValue(tree, out var treeKeys))
                {
                    treeKeys = new HashSet<(string File, string Id, string Message)>();
                    foreach (var diagnostic in _baseline.GetSemanticModel(tree).GetDiagnostics())
                    {
                        if (diagnostic.Severity == DiagnosticSeverity.Error
                            && diagnostic.Location.SourceTree != null)
                        {
                            treeKeys.Add(ErrorKey(diagnostic, _intermediateRoot));
                        }
                    }

                    _treeKeys[tree] = treeKeys;
                }

                keys.UnionWith(treeKeys);
            }

            return keys;
        }
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
