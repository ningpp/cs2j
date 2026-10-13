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

        var baselineErrors = BuildErrorSet(
            sourceFiles.Select(p => (Path: p.Dest, Content: File.ReadAllText(p.Source))),
            tree.Layout, intermediateRoot);

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
            var compilation = BuildCompilation(
                sourceFiles.Select(p => (Path: p.Dest, Content: File.ReadAllText(p.Dest))),
                tree.Layout, intermediateRoot);
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

            var rewrittenByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in sourceFiles)
            {
                var sourceCode = File.ReadAllText(pair.Dest);
                var syntaxTree = compilation.SyntaxTrees.FirstOrDefault(t =>
                    string.Equals(t.FilePath, pair.Dest, StringComparison.OrdinalIgnoreCase));
                if (syntaxTree == null) continue;

                var result = preprocessor.RewriteFile(
                    sourceCode,
                    compilation.GetSemanticModel(syntaxTree),
                    passStats,
                    passDiagnostics);

                if (result.Changed)
                {
                    File.WriteAllText(pair.Dest, result.OutputCode,
                        FileStartsWithBom(pair.Source)
                            ? new UTF8Encoding(true)
                            : new UTF8Encoding(false));
                    rewrittenByFile[pair.Dest] = pair.Source;
                }

                if (_verbose)
                {
                    Console.WriteLine(
                        $"[enum-declaration] {(result.Changed ? "transformed" : "clean (copy)")}: " +
                        $"{Path.GetRelativePath(sourceRoot, pair.Source)}");
                }
            }

            var candidateCompilation = BuildCompilation(
                sourceFiles.Select(p => (Path: p.Dest, Content: File.ReadAllText(p.Dest))),
                tree.Layout, intermediateRoot);
            var newErrors = DiffErrors(baselineErrors, candidateCompilation, intermediateRoot);

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

        var restoreExitCode = context.Restore();
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
        var trees = new List<SyntaxTree>();
        foreach (var (path, content) in files)
        {
            trees.Add(CSharpSyntaxTree.ParseText(
                content,
                new CSharpParseOptions(LanguageVersion.Latest),
                path: path));
        }

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
