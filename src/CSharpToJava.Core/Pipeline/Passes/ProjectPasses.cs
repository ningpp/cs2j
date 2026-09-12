using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.LinqRewrite;
using CSharpToJava.Core.PartialType;
using CSharpToJava.Core.Pipeline.Compatibility;

namespace CSharpToJava.Core.Pipeline;

public sealed class ProjectPassState
{
    public required Cs2jLibrary Library { get; set; }
    public required ConversionContext Context { get; init; }
    public required CSharpCompilation Compilation { get; set; }
    public ISet<string>? EmitFilePaths { get; init; }
    public IReadOnlyList<PartialTypeGroup> TypeGroups { get; set; } = [];
    public HashSet<string> BlockedFilePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<DiagnosticMessage>> DiagnosticsByFile { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ConversionResult> Results { get; } = [];

    public void RegisterFileDiagnostics(string filePath, IEnumerable<DiagnosticMessage> diagnostics, bool blockEmit)
    {
        var normalizedPath = NormalizeFilePath(filePath);
        if (!DiagnosticsByFile.TryGetValue(normalizedPath, out var existing))
        {
            existing = [];
            DiagnosticsByFile[normalizedPath] = existing;
        }

        existing.AddRange(diagnostics);
        if (blockEmit)
        {
            BlockedFilePaths.Add(normalizedPath);
        }
    }

    public void RecordBlockingDiagnostics(string filePath, IEnumerable<DiagnosticMessage> diagnostics)
    {
        var diagnosticList = diagnostics.ToList();
        if (diagnosticList.Count == 0)
        {
            return;
        }

        RegisterFileDiagnostics(filePath, diagnosticList, blockEmit: true);

        var normalizedPath = NormalizeFilePath(filePath);
        var existingResult = Results.FirstOrDefault(result =>
            string.Equals(NormalizeFilePath(result.FileName ?? string.Empty), normalizedPath, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(result.GeneratedCode));

        if (existingResult == null)
        {
            Results.Add(new ConversionResult
            {
                Success = false,
                FileName = filePath,
                GeneratedCode = string.Empty,
                Diagnostics = diagnosticList,
            });
            return;
        }

        existingResult.Success = false;
        existingResult.GeneratedCode = string.Empty;
        existingResult.Diagnostics = MergeDiagnostics(existingResult.Diagnostics, diagnosticList);
    }

    public IReadOnlyList<DiagnosticMessage> GetDiagnosticsForPaths(IEnumerable<string> filePaths)
    {
        var diagnostics = new List<DiagnosticMessage>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var filePath in filePaths.Select(NormalizeFilePath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!DiagnosticsByFile.TryGetValue(filePath, out var fileDiagnostics))
            {
                continue;
            }

            foreach (var diagnostic in fileDiagnostics)
            {
                var span = diagnostic.Location?.GetLineSpan();
                var key = $"{filePath}|{span?.StartLinePosition.Line}|{span?.StartLinePosition.Character}|{diagnostic.Severity}|{diagnostic.Message}";
                if (seen.Add(key))
                {
                    diagnostics.Add(diagnostic);
                }
            }
        }

        return diagnostics;
    }

    private static IReadOnlyList<DiagnosticMessage> MergeDiagnostics(
        IReadOnlyList<DiagnosticMessage> existingDiagnostics,
        IReadOnlyList<DiagnosticMessage> incomingDiagnostics)
    {
        var merged = new List<DiagnosticMessage>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var diagnostic in existingDiagnostics.Concat(incomingDiagnostics))
        {
            if (seen.Add(CreateDiagnosticKey(diagnostic)))
            {
                merged.Add(diagnostic);
            }
        }

        return merged;
    }

    private static string CreateDiagnosticKey(DiagnosticMessage diagnostic)
    {
        var span = diagnostic.Location?.GetLineSpan();
        return $"{diagnostic.Code}|{diagnostic.Category}|{span?.Path}|{span?.StartLinePosition.Line}|{span?.StartLinePosition.Character}|{diagnostic.Severity}|{diagnostic.Message}";
    }

    internal static string NormalizeFilePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || filePath.StartsWith("<", StringComparison.Ordinal))
        {
            return filePath;
        }

        return Path.IsPathRooted(filePath)
            ? Path.GetFullPath(filePath)
            : Path.GetFullPath(filePath);
    }
}

internal sealed record ProjectSyntaxTreeDiagnosticsResult(string FilePath, IReadOnlyList<DiagnosticMessage> Diagnostics);

internal sealed class ProjectLinqRewriteResult
{
    public required SyntaxTree SyntaxTree { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public required int RewriteCount { get; init; }
    public required bool Changed { get; init; }
    public LinqRewriteStatistics? Statistics { get; init; }
}

public sealed class ProjectLinqDesugarPass : ICs2jPass<ProjectPassState>, ICs2jPassMetricSource
{
    public string Name => nameof(ProjectLinqDesugarPass);
    public Cs2jPassStage Stage => Cs2jPassStage.Desugar;
    public int RewriteCount { get; private set; }
    public LinqRewriteStatistics? LinqStatistics { get; private set; }

    public void Execute(ProjectPassState state)
    {
        RewriteCount = 0;
        LinqStatistics = null;
        state.Context.PreDesugarCompilation = null;

        var isTsTarget = state.Context.Options.TargetLanguage == TargetLanguage.TypeScript;
        var runLinqRewrite = state.Context.Options.EnableLinqRewrite && !state.Context.Options.EffectivePreferStreamApi;
        if (!runLinqRewrite && !isTsTarget)
        {
            return;
        }

        var originalTrees = state.Compilation.SyntaxTrees.ToList();

        // Phase 1: Desugar LINQ query expressions (from..in..where..select) to
        // method-call chains (Where/Select/OrderBy/GroupBy).
        // This is purely syntactic - no semantic model required.
        var desugarChanged = false;
        int totalDesugared = 0;
        if (runLinqRewrite)
        {
            var desugarResults = ProjectPassParallelism.RunDeterministic(
                originalTrees,
                state.Context.Options.EnableParallelProjectPasses,
                syntaxTree => DesugarSyntaxTree(syntaxTree));

            totalDesugared = desugarResults.Sum(r => r.RewriteCount);
            desugarChanged = desugarResults.Any(r => r.Changed);
            RewriteCount += totalDesugared;

            foreach (var warning in desugarResults.SelectMany(result => result.Warnings))
            {
                state.Context.Diagnostics.Warning(warning);
            }

            if (desugarChanged)
            {
                // Rebuild the compilation with the desugared trees so that the
                // LinqRewriter in Phase 2 can resolve the desugared method calls.
                state.Compilation = ReplaceChangedSyntaxTrees(state.Compilation, originalTrees, desugarResults);
            }
        }

        // Phase 2a (Task 36): TS-target BCL member mapping (MemberMappings.json).
        // Must run before the LINQ rewrite: after Enumerable.xxx rewriting the
        // lambda parameter types are unresolvable (compilation has no Enumerable
        // reference) and member mapping would degrade to Java-ized names.
        // Independent of EnableLinqRewrite so that --no-linq-rewrite still maps.
        var memberChanged = false;
        if (isTsTarget)
        {
            var memberInputTrees = state.Compilation.SyntaxTrees.ToList();
            var memberResults = ProjectPassParallelism.RunDeterministic(
                memberInputTrees,
                state.Context.Options.EnableParallelProjectPasses,
                syntaxTree => RewriteSyntaxTreeMembers(state, syntaxTree));
            memberChanged = memberResults.Any(r => r.Changed);
            foreach (var warning in memberResults.SelectMany(result => result.Warnings))
            {
                state.Context.Diagnostics.Warning(warning);
            }
            if (memberChanged)
            {
                state.Compilation = ReplaceChangedSyntaxTrees(state.Compilation, memberInputTrees, memberResults);
            }
        }

        // Phase 2b: Rewrite LINQ method-call chains (Java target -> procedural
        // loops; TS target -> array methods / Enumerable lazy chains, see
        // RewriteSyntaxTree).
        var preRewriteCompilation = state.Compilation;
        var rewriteInputTrees = preRewriteCompilation.SyntaxTrees.ToList();
        var aggregatedStats = new LinqRewriteStatistics();
        aggregatedStats.DesugaredQueryCount = totalDesugared;
        var rewriteChanged = false;
        if (runLinqRewrite)
        {
            var rewriteResults = ProjectPassParallelism.RunDeterministic(
                rewriteInputTrees,
                state.Context.Options.EnableParallelProjectPasses,
                syntaxTree => RewriteSyntaxTree(state, syntaxTree));

            foreach (var rewriteResult in rewriteResults)
            {
                RewriteCount += rewriteResult.RewriteCount;
                foreach (var warning in rewriteResult.Warnings)
                {
                    state.Context.Diagnostics.Warning(warning);
                }
                if (rewriteResult.Statistics != null)
                    aggregatedStats.MergeFrom(rewriteResult.Statistics);
            }
            LinqStatistics = aggregatedStats;

            rewriteChanged = rewriteResults.Any(r => r.Changed);
            if (rewriteChanged)
            {
                // Save the compilation from immediately before the final procedural
                // LINQ rewrite replacement. Downstream transforms can use it as a
                // semantic fallback for syntax nodes that still point at pre-rewrite trees.
                state.Context.PreDesugarCompilation = preRewriteCompilation;
                state.Compilation = ReplaceChangedSyntaxTrees(preRewriteCompilation, rewriteInputTrees, rewriteResults);
            }
        }

        if (!desugarChanged && !memberChanged && !rewriteChanged)
        {
            return;
        }

        RebuildProjectStateAfterCompilationChange(state);
    }

    /// <summary>
    /// TS 目标（Task 36）：按 C# 全名查 TsMemberMappingTable 改写 BCL 成员调用
    /// （CsString.* / Math.trunc / console.log / push / length / CsDictionary.* 等），
    /// 兼容包符号登记到 context.TsCompatSymbols（发射器自动生成 compat 导入）。
    /// 仅 TS 目标调用；必须在 TsLinqRewriter 之前运行（语义模型完整）。
    /// </summary>
    private static ProjectLinqRewriteResult RewriteSyntaxTreeMembers(ProjectPassState state, SyntaxTree syntaxTree)
    {
        if (string.IsNullOrWhiteSpace(syntaxTree.FilePath) || syntaxTree.FilePath.StartsWith("<", StringComparison.Ordinal))
        {
            return new ProjectLinqRewriteResult
            {
                SyntaxTree = syntaxTree,
                Warnings = [],
                RewriteCount = 0,
                Changed = false,
            };
        }

        try
        {
            var semanticModel = state.Compilation.GetSemanticModel(syntaxTree);
            var originalRoot = syntaxTree.GetRoot();
            var memberMappingTable = new TypeScript.TsMemberMappingTable(
                state.Context.Options.TypeScriptMappingConfigPath,
                state.Context.Options.TypeScriptMemberMappingPath,
                state.Context.Options.TypeMappingConfigPath);
            var memberRoot = new TypeScript.Rewriters.TsMemberMappingRewriter(
                semanticModel, memberMappingTable, state.Context)
                .Visit(originalRoot);
            var changed = false;
            SyntaxTree rewrittenTree;
            if (memberRoot is CompilationUnitSyntax memberCompilationUnit)
            {
                changed = !ReferenceEquals(memberCompilationUnit, originalRoot);
                rewrittenTree = changed
                    ? syntaxTree.WithRootAndOptions(memberCompilationUnit, syntaxTree.Options)
                    : syntaxTree;
            }
            else
            {
                rewrittenTree = syntaxTree;
            }

            return new ProjectLinqRewriteResult
            {
                SyntaxTree = rewrittenTree,
                Warnings = [],
                RewriteCount = 0,
                Changed = changed,
            };
        }
        catch (Exception ex)
        {
            return new ProjectLinqRewriteResult
            {
                SyntaxTree = syntaxTree,
                Warnings = ["TS member mapping skipped in '" + Path.GetFileName(syntaxTree.FilePath) + "': " + ex.Message],
                RewriteCount = 0,
                Changed = false,
            };
        }
    }


    private static CSharpCompilation ReplaceChangedSyntaxTrees(
        CSharpCompilation compilation,
        IReadOnlyList<SyntaxTree> originalTrees,
        IReadOnlyList<ProjectLinqRewriteResult> rewriteResults)
    {
        var newCompilation = compilation;
        for (int i = 0; i < originalTrees.Count; i++)
        {
            if (rewriteResults[i].Changed)
            {
                newCompilation = newCompilation.ReplaceSyntaxTree(originalTrees[i], rewriteResults[i].SyntaxTree);
            }
        }

        return newCompilation;
    }

    internal static void RebuildProjectStateAfterCompilationChange(ProjectPassState state)
    {
        var originalLibrary = state.Library;
        var primaryProject = state.Library.Projects.FirstOrDefault();
        var rebuiltLibrary = Cs2jLibraryFactory.CreateFromCompilation(
            state.Compilation,
            projectName: primaryProject?.Name,
            projectFilePath: primaryProject?.ProjectFilePath,
            projectReferences: primaryProject?.ProjectReferences?.ToList(),
            isTestProject: primaryProject?.IsTestProject ?? false,
            libraryName: originalLibrary.Name);
        rebuiltLibrary.ExtensionMethodIndex = originalLibrary.ExtensionMethodIndex;
        state.Library = rebuiltLibrary;
        state.Context.ProjectCompilation = state.Compilation;

        // Rebuild TypeGroups from the new compilation so that ClassTransformer
        // processes syntax nodes from the post-LinqRewriter trees, not the
        // original (pre-rewrite) trees. Without this, the ClassTransformer
        // sees the old nodes with residual LINQ method calls that the
        // LinqRewriter already replaced in the new trees.
        var partialMerger = new PartialTypeMerger(state.Context.Diagnostics);
        state.TypeGroups = partialMerger.FindAndGroupTypes(state.Compilation);
    }

    private static ProjectLinqRewriteResult DesugarSyntaxTree(SyntaxTree syntaxTree)
    {
        if (string.IsNullOrWhiteSpace(syntaxTree.FilePath) || syntaxTree.FilePath.StartsWith("<", StringComparison.Ordinal))
        {
            return new ProjectLinqRewriteResult
            {
                SyntaxTree = syntaxTree,
                Warnings = [],
                RewriteCount = 0,
                Changed = false,
            };
        }

        try
        {
            var desugarer = new LinqQueryDesugarer();
            var originalRoot = syntaxTree.GetRoot();
            var desugaredRoot = desugarer.Visit(originalRoot);
            var changed = desugarer.DesugaredCount > 0 && desugaredRoot is CompilationUnitSyntax;
            var desugaredTree = changed && desugaredRoot is CompilationUnitSyntax desugaredCompUnit
                ? syntaxTree.WithRootAndOptions(desugaredCompUnit, syntaxTree.Options)
                : syntaxTree;

            return new ProjectLinqRewriteResult
            {
                SyntaxTree = desugaredTree,
                Warnings = [],
                RewriteCount = desugarer.DesugaredCount,
                Changed = changed,
            };
        }
        catch (Exception ex)
        {
            return new ProjectLinqRewriteResult
            {
                SyntaxTree = syntaxTree,
                Warnings = [$"LINQ query desugar skipped in '{Path.GetFileName(syntaxTree.FilePath)}': {ex.Message}"],
                RewriteCount = 0,
                Changed = false,
            };
        }
    }

    private static ProjectLinqRewriteResult RewriteSyntaxTree(ProjectPassState state, SyntaxTree syntaxTree)
    {
        if (string.IsNullOrWhiteSpace(syntaxTree.FilePath) || syntaxTree.FilePath.StartsWith("<", StringComparison.Ordinal))
        {
            return new ProjectLinqRewriteResult
            {
                SyntaxTree = syntaxTree,
                Warnings = [],
                RewriteCount = 0,
                Changed = false,
            };
        }

        try
        {
            var semanticModel = state.Compilation.GetSemanticModel(syntaxTree);
            var originalRoot = syntaxTree.GetRoot();
            SyntaxNode? rewrittenRoot;
            IReadOnlyList<string> warnings;
            int rewriteCount;
            LinqRewriteStatistics? statistics;
            // Task 36 LINQ 目标分叉（spec §2.4）：TS 目标 → 简单链数组方法 filter/map、
            // 复杂链 Enumerable.xxx 惰性链；Java 目标 → 过程化循环（原行为）。
            if (state.Context.Options.TargetLanguage == TargetLanguage.TypeScript)
            {
                var tsRewriter = new TypeScript.Rewriters.TsLinqRewriter(semanticModel, state.Context);
                rewrittenRoot = tsRewriter.Visit(originalRoot);
                warnings = tsRewriter.SkippedLinqChains
                    .Select(skipped => $"LINQ rewrite skipped in '{Path.GetFileName(syntaxTree.FilePath)}': {skipped}")
                    .ToList();
                rewriteCount = tsRewriter.RewrittenCalls;
                statistics = new LinqRewriteStatistics { RewrittenChainCount = tsRewriter.RewrittenChains };
            }
            else
            {
                var rewriter = new LinqRewriter(semanticModel, state.Context.Options);
                rewrittenRoot = rewriter.Visit(originalRoot);
                warnings = rewriter.SkippedLinqChains
                    .Select(skipped => $"LINQ rewrite skipped in '{Path.GetFileName(syntaxTree.FilePath)}': {skipped}")
                    .ToList();
                rewriteCount = rewriter.RewrittenLinqQueries;
                statistics = rewriter.Statistics;
            }
            var changed = false;
            SyntaxTree rewrittenTree;
            if (rewrittenRoot is CompilationUnitSyntax rewrittenCompilationUnit)
            {
                // Preserve the original syntax tree reference when the rewriter made no
                // changes.  This is critical: downstream passes (ClassTransformer etc.)
                // call GetSemanticModelForTree with the *original* tree reference, and
                // Roslyn's GetSemanticModel only works when the tree is in the compilation.
                // If we blindly wrap with WithRootAndOptions we create a new tree object
                // that the original reference won't match.
                changed = !ReferenceEquals(rewrittenCompilationUnit, originalRoot);
                rewrittenTree = changed
                    ? syntaxTree.WithRootAndOptions(rewrittenCompilationUnit, syntaxTree.Options)
                    : syntaxTree;
            }
            else
            {
                rewrittenTree = syntaxTree;
            }

            return new ProjectLinqRewriteResult
            {
                SyntaxTree = rewrittenTree,
                Warnings = warnings,
                RewriteCount = rewriteCount,
                Changed = changed,
                Statistics = statistics,
            };
        }
        catch (Exception ex)
        {
            return new ProjectLinqRewriteResult
            {
                SyntaxTree = syntaxTree,
                Warnings = [$"LINQ rewrite skipped in '{Path.GetFileName(syntaxTree.FilePath)}': {ex.Message}"],
                RewriteCount = 0,
                Changed = false,
            };
        }
    }
}

public sealed class ProjectCompilationCheckPass : ICs2jPass<ProjectPassState>
{
    public string Name => nameof(ProjectCompilationCheckPass);
    public Cs2jPassStage Stage => Cs2jPassStage.Check;

    public void Execute(ProjectPassState state)
    {
        state.Context.ProjectCompilation = state.Compilation;
        if (!state.Compilation.SyntaxTrees.Any())
        {
            throw new InvalidOperationException($"Library '{state.Library.Name}' does not contain syntax trees.");
        }
    }
}

public sealed class ProjectUnsupportedDomainCheckPass : ICs2jPass<ProjectPassState>
{
    public string Name => nameof(ProjectUnsupportedDomainCheckPass);
    public Cs2jPassStage Stage => Cs2jPassStage.Check;

    public void Execute(ProjectPassState state)
    {
        var syntaxTrees = state.Compilation.SyntaxTrees
            .Where(syntaxTree => !string.IsNullOrWhiteSpace(syntaxTree.FilePath) && !syntaxTree.FilePath.StartsWith("<", StringComparison.Ordinal))
            .ToList();

        var analysisResults = ProjectPassParallelism.RunDeterministic(
            syntaxTrees,
            state.Context.Options.EnableParallelProjectPasses,
            syntaxTree => new ProjectSyntaxTreeDiagnosticsResult(
                syntaxTree.FilePath,
                UnsupportedDomainAnalyzer.AnalyzeSyntaxTree(
                    syntaxTree,
                    state.Context.TypeMapper.GetExplicitNamespaceMappings())));

        foreach (var analysisResult in analysisResults)
        {
            var diagnostics = analysisResult.Diagnostics;
            if (diagnostics.Count == 0)
            {
                continue;
            }

            foreach (var diagnostic in diagnostics)
            {
                state.Context.Diagnostics.Error(diagnostic.Message, diagnostic.Location, diagnostic.Code, diagnostic.Category);
            }

            state.RecordBlockingDiagnostics(analysisResult.FilePath, diagnostics);
        }
    }
}

public sealed class ProjectPlatformBoundaryCheckPass : ICs2jPass<ProjectPassState>
{
    public string Name => nameof(ProjectPlatformBoundaryCheckPass);
    public Cs2jPassStage Stage => Cs2jPassStage.Check;

    public void Execute(ProjectPassState state)
    {
        var isTestProject = state.Library.Projects.FirstOrDefault()?.IsTestProject == true;
        var syntaxTrees = state.Compilation.SyntaxTrees
            .Where(syntaxTree => !string.IsNullOrWhiteSpace(syntaxTree.FilePath) && !syntaxTree.FilePath.StartsWith("<", StringComparison.Ordinal))
            .Where(syntaxTree => !BoundaryAnalysisHelpers.IsGeneratedAssemblyInfoFile(syntaxTree.FilePath))
            .ToList();

        var analysisResults = ProjectPassParallelism.RunDeterministic(
            syntaxTrees,
            state.Context.Options.EnableParallelProjectPasses,
            syntaxTree => new ProjectSyntaxTreeDiagnosticsResult(
                syntaxTree.FilePath,
                PlatformBoundaryAnalyzer.AnalyzeSyntaxTree(
                    syntaxTree,
                    state.Compilation.GetSemanticModel(syntaxTree),
                    shouldDowngradeTestPlatformProbes: isTestProject)));

        foreach (var analysisResult in analysisResults)
        {
            var diagnostics = analysisResult.Diagnostics;
            if (diagnostics.Count == 0)
            {
                continue;
            }

            foreach (var diagnostic in diagnostics)
            {
                if (diagnostic.Severity == Context.DiagnosticSeverity.Error)
                {
                    state.Context.Diagnostics.Error(diagnostic.Message, diagnostic.Location, diagnostic.Code, diagnostic.Category);
                }
                else
                {
                    state.Context.Diagnostics.Warning(diagnostic.Message, diagnostic.Location, diagnostic.Code, diagnostic.Category);
                }
            }

            if (diagnostics.Any(diagnostic => diagnostic.Severity == Context.DiagnosticSeverity.Error))
            {
                state.RecordBlockingDiagnostics(analysisResult.FilePath, diagnostics);
            }
            else
            {
                state.RegisterFileDiagnostics(analysisResult.FilePath, diagnostics, blockEmit: false);
            }
        }
    }
}

public sealed class ProjectNativeInteropCheckPass : ICs2jPass<ProjectPassState>
{
    public string Name => nameof(ProjectNativeInteropCheckPass);
    public Cs2jPassStage Stage => Cs2jPassStage.Check;

    public void Execute(ProjectPassState state)
    {
        var isTsTarget = state.Context.Options.TargetLanguage == TargetLanguage.TypeScript;
        var syntaxTrees = state.Compilation.SyntaxTrees
            .Where(syntaxTree => !string.IsNullOrWhiteSpace(syntaxTree.FilePath) && !syntaxTree.FilePath.StartsWith("<", StringComparison.Ordinal))
            .Where(syntaxTree => !BoundaryAnalysisHelpers.IsPInvokeContainerFile(syntaxTree.FilePath))
            .ToList();

        var analysisResults = ProjectPassParallelism.RunDeterministic(
            syntaxTrees,
            state.Context.Options.EnableParallelProjectPasses,
            syntaxTree => new ProjectSyntaxTreeDiagnosticsResult(
                syntaxTree.FilePath,
                NativeInteropAnalyzer.AnalyzeSyntaxTree(syntaxTree, state.Compilation.GetSemanticModel(syntaxTree))));

        foreach (var analysisResult in analysisResults)
        {
            var diagnostics = analysisResult.Diagnostics;
            if (diagnostics.Count == 0)
            {
                continue;
            }

            if (isTsTarget)
            {
                // TS 目标（Task 38，spec §2.4）：NativeInterop 检查降级为警告，不阻断发射
                foreach (var diagnostic in diagnostics)
                {
                    state.Context.Diagnostics.Warning(diagnostic.Message, diagnostic.Location, diagnostic.Code, diagnostic.Category);
                    // 存入结果的诊断须为降级后的 Warning 副本（原始为 Error，否则结果 Success 会被置 false）
                    var downgraded = diagnostic with { Severity = Context.DiagnosticSeverity.Warning };
                    state.RegisterFileDiagnostics(analysisResult.FilePath, [downgraded], blockEmit: false);
                }
                continue;
            }

            foreach (var diagnostic in diagnostics)
            {
                state.Context.Diagnostics.Error(diagnostic.Message, diagnostic.Location, diagnostic.Code, diagnostic.Category);
            }

            state.RecordBlockingDiagnostics(analysisResult.FilePath, diagnostics);
        }
    }
}

public sealed class ProjectPartialTypeNormalizationPass : ICs2jPass<ProjectPassState>
{
    public string Name => nameof(ProjectPartialTypeNormalizationPass);
    public Cs2jPassStage Stage => Cs2jPassStage.Normalize;

    public void Execute(ProjectPassState state)
    {
        var partialMerger = new PartialTypeMerger(state.Context.Diagnostics);
        state.TypeGroups = partialMerger.FindAndGroupTypes(state.Compilation);
    }
}

/// <summary>
/// Java 目标语法树层 rewriter pass：提升泛型声明模式（`x is List&lt;T&gt; v` → 类型模式 +
/// 段首强转声明），保证模式变量保持完整泛型类型（JavaGenericPatternRewriter）。
/// 必须在 ProjectPartialTypeNormalizationPass 之前运行（类型组基于改写后树构建）。
/// </summary>
public sealed class ProjectJavaPatternRewritePass : ICs2jPass<ProjectPassState>
{
    public string Name => nameof(ProjectJavaPatternRewritePass);
    public Cs2jPassStage Stage => Cs2jPassStage.Normalize;

    public void Execute(ProjectPassState state)
    {
        var trees = state.Compilation.SyntaxTrees
            .Where(tree => !string.IsNullOrWhiteSpace(tree.FilePath)
                && !tree.FilePath.StartsWith("<", StringComparison.Ordinal))
            .ToList();
        if (trees.Count == 0)
        {
            return;
        }

        var changed = new List<(SyntaxTree Old, SyntaxTree New)>();
        foreach (var tree in trees)
        {
            var root = tree.GetRoot();
            var newRoot = new Rewriters.JavaGenericPatternRewriter().Visit(root);
            if (newRoot is CompilationUnitSyntax newUnit && !ReferenceEquals(newUnit, root))
            {
                changed.Add((tree, tree.WithRootAndOptions(newUnit, tree.Options)));
            }
        }

        if (changed.Count == 0)
        {
            return;
        }

        foreach (var (oldTree, newTree) in changed)
        {
            state.Compilation = state.Compilation.ReplaceSyntaxTree(oldTree, newTree);
        }
        ProjectLinqDesugarPass.RebuildProjectStateAfterCompilationChange(state);
    }
}

public sealed class ProjectTypeEmitPass : ICs2jPass<ProjectPassState>
{
    private readonly IReadOnlyList<Java.JavaSyntaxRewriter> _irRewriters;

    public ProjectTypeEmitPass(IReadOnlyList<Java.JavaSyntaxRewriter> irRewriters)
    {
        _irRewriters = irRewriters;
    }

    public string Name => nameof(ProjectTypeEmitPass);
    public Cs2jPassStage Stage => Cs2jPassStage.Emit;

    public void Execute(ProjectPassState state)
    {
        // Pre-scan: resolve var-declared local types for downstream fallback use
        VarTypeResolver.PreScan(state.Compilation, state.Context);

        // Build effective IR rewriters: user-registered + built-in validation rewriters
        var effectiveRewriters = BuildEffectiveRewriters(state);

        // Pre-register [Flags] enums from the whole compilation so that the name-based
        // flags registry is populated before ANY type group is converted. Type-group
        // conversion order follows Roslyn namespace traversal (not declaration or
        // file order), so an interface converted before the enum type group itself
        // would otherwise fall back to syntax-based type mapping with an empty
        // registry and emit the Java class name instead of int (e.g.
        // "ModifierKeys getModifierKeys()" instead of "int getModifierKeys()").
        PreRegisterFlagsEnums(state);

        foreach (var typeGroup in state.TypeGroups)
        {
            var candidatePaths = CollectCandidatePaths(typeGroup);
            if (candidatePaths.Any(path => state.BlockedFilePaths.Contains(NormalizeFilePath(path))))
            {
                continue;
            }

            // Cs2jTestUiCultureShim.cs is injected by the make-readonly preprocessing
            // to pin the C# test-run thread culture (a .NET Framework concern). Its
            // types extend xunit framework classes (XunitTestFrameworkExecutor & co.)
            // from the NuGet package, which are never converted — emitting them would
            // produce unresolvable base classes. The shim has no Java equivalent.
            if (candidatePaths.Any(path =>
                    path.EndsWith("Cs2jTestUiCultureShim.cs", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!ShouldEmitTypeGroup(candidatePaths, state.EmitFilePaths))
            {
                continue;
            }

            var result = TypeGroupResolver.ConvertTypeGroup(typeGroup, state.Compilation, state.Context, effectiveRewriters);
            if (result != null)
            {
                var diagnostics = state.GetDiagnosticsForPaths(candidatePaths);
                if (diagnostics.Count > 0)
                {
                    result.Diagnostics = result.Diagnostics.Concat(diagnostics).ToList();
                    result.Success = result.Success && diagnostics.All(diagnostic => diagnostic.Severity != Context.DiagnosticSeverity.Error);
                }

                state.Results.Add(result);
            }
        }
    }


    /// <summary>
    /// Scans the whole compilation for [Flags] enum declarations and registers them in the
    /// name-based flags registry before any type group is converted. Syntax-based type
    /// mapping (MapTypeFromSyntaxString) is used as a fallback when semantic lookups fail
    /// on merged syntax trees, and it relies on this registry; without the pre-scan, an
    /// interface converted before the enum type group would emit the Java class name
    /// instead of the underlying int/long value type.
    /// </summary>
    internal static void PreRegisterFlagsEnums(ProjectPassState state)
    {
        foreach (var tree in state.Compilation.SyntaxTrees)
        {
            var root = tree.GetRoot();
            foreach (var enumDecl in root.DescendantNodes().OfType<EnumDeclarationSyntax>())
            {
                var symbol = state.Compilation.GetSemanticModel(tree).GetDeclaredSymbol(enumDecl);
                if (symbol is not INamedTypeSymbol { TypeKind: TypeKind.Enum } enumSymbol)
                    continue;
                if (!enumSymbol.GetAttributes().Any(a => a.AttributeClass?.Name is "FlagsAttribute" or "Flags"))
                    continue;

                var valueType = enumSymbol.EnumUnderlyingType?.SpecialType switch
                {
                    SpecialType.System_Int64 or SpecialType.System_UInt64 => "long",
                    _ => "int",
                };

                var names = new HashSet<string>(StringComparer.Ordinal) { enumSymbol.Name };
                var display = enumSymbol.ToDisplayString();
                if (!string.IsNullOrWhiteSpace(display)) names.Add(display);
                var fqn = enumSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (fqn.StartsWith("global::", StringComparison.Ordinal)) fqn = fqn["global::".Length..];
                if (!string.IsNullOrWhiteSpace(fqn)) names.Add(fqn);
                foreach (var name in names)
                    state.Context.RegisterFlagsEnum(name, valueType);
            }
        }
    }

    private IReadOnlyList<Java.JavaSyntaxRewriter> BuildEffectiveRewriters(ProjectPassState state)
    {
        var rewriters = new List<Java.JavaSyntaxRewriter>(_irRewriters);

        // D4: Built-in compatibility rewriters migrated from PostGenerationRewriteEngine
        rewriters.Add(new Java.Rewriters.OperatorPrecedenceRewriter());
        rewriters.Add(new Java.Rewriters.MapEntryTypeRewriter());
        rewriters.Add(new Java.Rewriters.MemberwiseCloneRewriter());
        rewriters.Add(new Java.Rewriters.MathMethodRewriter());
        rewriters.Add(new Java.Rewriters.DelegateInvocationRewriter());
        rewriters.Add(new Java.Rewriters.GenericArrayCreationRewriter());
        rewriters.Add(new Java.Rewriters.IntStreamBoxedRewriter());
        rewriters.Add(new Java.Rewriters.ArrayIterableConversionRewriter());
        rewriters.Add(new Java.Rewriters.CollectStreamRoundtripRewriter());
        rewriters.Add(new Java.Rewriters.StopwatchApiRewriter());
        rewriters.Add(new Java.Rewriters.LineContinuationTrimRewriter());
        rewriters.Add(new Java.Rewriters.StringConcatRewriter());
        rewriters.Add(new Java.Rewriters.EventHandlerLambdaRewriter());
        rewriters.Add(new Java.Rewriters.ExceptionApiRewriter());
        rewriters.Add(new Java.Rewriters.InitActiveDiagonalsNullGuardRewriter());
        rewriters.Add(new Java.Rewriters.TangentBetweenBranchesNoopGuardRewriter());
        rewriters.Add(new Java.Rewriters.FindClosestPointsMaxIterationRewriter());

        // Java metadata validation rewriters (Phase C)
        var javaLibrary = state.Context.TypeMappings.JavaLibrary;
        if (javaLibrary is not null)
        {
            rewriters.Add(new Java.Rewriters.JavaApiValidationRewriter(javaLibrary, state.Context.Diagnostics));
            rewriters.Add(new Java.Rewriters.JavaExceptionCheckRewriter(javaLibrary, state.Context.Diagnostics));
        }

        return rewriters;
    }

    internal static List<string> CollectCandidatePaths(PartialTypeGroup typeGroup)
    {
        var candidatePaths = new List<string>();
        foreach (var syntaxNode in typeGroup.SyntaxNodes)
        {
            var path = syntaxNode.SyntaxTree.FilePath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                candidatePaths.Add(path);
            }
        }

        foreach (var syntaxRef in typeGroup.TypeSymbol.DeclaringSyntaxReferences)
        {
            var path = syntaxRef.SyntaxTree.FilePath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                candidatePaths.Add(path);
            }
        }

        return candidatePaths;
    }

    internal static bool ShouldEmitTypeGroup(IReadOnlyCollection<string> candidatePaths, ISet<string>? emitFilePaths)
    {
        if (emitFilePaths == null || emitFilePaths.Count == 0)
        {
            return true;
        }

        return candidatePaths.Any(path => emitFilePaths.Contains(Path.GetFullPath(path)));
    }

    internal static string NormalizeFilePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || filePath.StartsWith("<", StringComparison.Ordinal))
        {
            return filePath;
        }

        return Path.GetFullPath(filePath);
    }
}

public sealed class ProjectCompatibilityEmitPass : ICs2jPass<ProjectPassState>
{
    public string Name => nameof(ProjectCompatibilityEmitPass);
    public Cs2jPassStage Stage => Cs2jPassStage.Emit;

    public void Execute(ProjectPassState state)
    {
        if (!state.Results.Any(result => !string.IsNullOrEmpty(result.GeneratedCode)))
        {
            return;
        }

        // Compatibility classes are now provided by the pre-built csharptojava-compat
        // Maven artifact; no inline generation needed.
    }
}

/// <summary>
/// Validates extension method bindings across the current project.
/// Checks that all extension method call sites resolve to a host that is either:
/// - in the current project
/// - in a referenced project within the conversion graph
/// - in a known type mapping
/// Produces diagnostics for unresolvable extension method calls.
/// </summary>
public sealed class ProjectExtensionMethodCheckPass : ICs2jPass<ProjectPassState>
{
    public string Name => nameof(ProjectExtensionMethodCheckPass);
    public Cs2jPassStage Stage => Cs2jPassStage.Check;

    public void Execute(ProjectPassState state)
    {
        var index = state.Library.ExtensionMethodIndex;
        if (index.Count == 0)
            return;

        var syntaxTrees = state.Compilation.SyntaxTrees
            .Where(st => !string.IsNullOrWhiteSpace(st.FilePath)
                         && !st.FilePath.StartsWith("<", StringComparison.Ordinal))
            .ToList();

        var analysisResults = ProjectPassParallelism.RunDeterministic(
            syntaxTrees,
            state.Context.Options.EnableParallelProjectPasses,
            syntaxTree => AnalyzeSyntaxTree(syntaxTree, state));

        foreach (var (filePath, diagnostics) in analysisResults)
        {
            if (diagnostics.Count == 0)
                continue;

            foreach (var diagnostic in diagnostics)
            {
                state.Context.Diagnostics.Warning(diagnostic.Message, diagnostic.Location, diagnostic.Code, diagnostic.Category);
            }

            // Extension method diagnostics are warnings, not blocking
            state.RegisterFileDiagnostics(filePath, diagnostics, blockEmit: false);
        }
    }

    private static ProjectSyntaxTreeDiagnosticsResult AnalyzeSyntaxTree(
        SyntaxTree syntaxTree,
        ProjectPassState state)
    {
        var semanticModel = state.Compilation.GetSemanticModel(syntaxTree);
        var diagnostics = new List<Context.DiagnosticMessage>();
        var root = syntaxTree.GetRoot();
        var index = state.Library.ExtensionMethodIndex;

        foreach (var invocation in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>())
        {
            var symbolInfo = semanticModel.GetSymbolInfo(invocation);
            if (symbolInfo.Symbol is not IMethodSymbol { IsExtensionMethod: true, MethodKind: MethodKind.ReducedExtension } methodSymbol)
                continue;

            // Skip LINQ extension methods — handled by dedicated Stream API rewriting
            var containingType = methodSymbol.ContainingType.ToDisplayString();
            if (containingType is "System.Linq.Enumerable" or "System.Linq.Queryable")
                continue;

            // Check if the extension method is in a known mapping
            var mappedMethod = state.Context.TypeMappings.MapMethod(containingType, methodSymbol.Name);
            if (mappedMethod != null)
                continue;

            // Check if the extension method is in the conversion graph index
            var originalMethod = methodSymbol.ReducedFrom ?? methodSymbol;
            var docId = originalMethod.GetDocumentationCommentId();
            if (!string.IsNullOrEmpty(docId) && index.FindByDocCommentId(docId) != null)
                continue;

            // Extension method host not found in conversion graph or mappings
            diagnostics.Add(new Context.DiagnosticMessage(
                Context.DiagnosticSeverity.Warning,
                $"Extension method '{methodSymbol.Name}' on type '{methodSymbol.ContainingType.Name}' "
                + $"is not in the current conversion graph or known mappings. "
                + $"The generated Java code may reference an unavailable static host class.",
                invocation.GetLocation(),
                "CS2J_EXT001",
                "ExtensionMethod"
            ));
        }

        return new ProjectSyntaxTreeDiagnosticsResult(syntaxTree.FilePath, diagnostics);
    }
}

public sealed class ProjectCrossPackageImportEmitPass : ICs2jPass<ProjectPassState>
{
    public string Name => nameof(ProjectCrossPackageImportEmitPass);
    public Cs2jPassStage Stage => Cs2jPassStage.Emit;

    public void Execute(ProjectPassState state)
    {
        if (!state.Results.Any(result => !string.IsNullOrEmpty(result.GeneratedCode)))
        {
            return;
        }

        if (state.Context.Options.TargetLanguage == TargetLanguage.TypeScript)
        {
            // TS 变体（Task 38，spec §3.3）：ESM 跨包导入由发射器按类型归属生成，
            // 无 Java 通配导入模型；workspace 包名 → package.json dependencies 由
            // ProjectNpmDependencyPass 收集（Task 40 CLI 接线）。当前发射器为
            // 单文件自包含（不生成相对/跨包导入），跨文件/跨包 import 接线登记为
            // M5 限制（Task 38 注记）。
            return;
        }

        CrossPackageImportResolver.AddCrossPackageImports(state.Results, state.Context.Options.SharedCompatibilityPackage);
    }
}

/// <summary>
/// Patches <c>PushdownPrefixState.java</c> to clone values when pushing onto the internal array.
/// In C#, <c>TValue</c> is a struct so the assignment <c>this.array[this.tos++] = value</c>
/// copies by value. In Java, <c>ValueType</c> is a reference type so the same reference is stored
/// multiple times. The scanner reuses its <c>yylval</c> object, so without cloning, every token
/// overwrites <c>sVal</c> and corrupts previously shifted values on the parser stack.
/// </summary>
public sealed class ProjectStructClonePatchPass : ICs2jPass<ProjectPassState>
{
    public string Name => nameof(ProjectStructClonePatchPass);
    public Cs2jPassStage Stage => Cs2jPassStage.Emit;

    private static readonly System.Text.RegularExpressions.Regex PushLineRegex = new(
        @"^(\s*)this\.array\[this\.tos\+\+\]\s*=\s*value;\s*$",
        System.Text.RegularExpressions.RegexOptions.Multiline);

    private const string HelperMethod = @"
    @SuppressWarnings(""unchecked"")
    private T clonePushValue(T value) {
        if (value instanceof Cloneable) {
            try {
                return (T) value.getClass().getMethod(""clone"").invoke(value);
            } catch (Exception _e_cs2j) {
                return value;
            }
        }
        return value;
    }";

    public void Execute(ProjectPassState state)
    {
        foreach (var result in state.Results)
        {
            if (result.FileName != null)
            {
                if (result.FileName.EndsWith("PushdownPrefixState.java", StringComparison.Ordinal))
                {
                    PatchPushdownPrefixState(result);
                }
                else if (result.FileName.EndsWith("AttributeValuePair.java", StringComparison.Ordinal))
                {
                    PatchAttributeValuePair(result);
                }
                else if (result.FileName.EndsWith("IEnumerableConverter.java", StringComparison.Ordinal))
                {
                    PatchIEnumerableConverterCreateCollection(result);
                }
                else if (result.FileName.EndsWith("IListOfTConverter.java", StringComparison.Ordinal)
                    || result.FileName.EndsWith("ICollectionOfTConverter.java", StringComparison.Ordinal))
                {
                    PatchListConverterAdd(result);
                }
                else if (result.FileName.EndsWith("IEnumerableConverterFactoryHelpers.java", StringComparison.Ordinal))
                {
                    PatchTransitiveInterfaceWalk(result);
                }
                else if (result.FileName.EndsWith("IDictionaryConverter.java", StringComparison.Ordinal))
                {
                    PatchDictionaryCreateCollection(result);
                }
                else if (result.FileName.EndsWith("IDictionaryConverter.java", StringComparison.Ordinal))
                {
                    PatchDictionaryCreateCollection(result);
                }
                else if (result.FileName.EndsWith("DictionaryDefaultConverter.java", StringComparison.Ordinal)
                    || result.FileName.EndsWith("IEnumerableDefaultConverter.java", StringComparison.Ordinal))
                {
                    // Round-18 experiment switch: this patch is the prime suspect for the cross-test
                    // contamination that makes ~28 write tests fail only in full-suite order.
                    if (WriteElementPropertyInfoEnabled)
                        PatchWriteElementPropertyInfo(result);
                }
                else if (result.FileName.EndsWith("JsonClassInfo.java", StringComparison.Ordinal))
                {
                    PatchElementArgumentScope(result);
                }
                // NOTE: "JsonPropertyInfo.java".EndsWith("PropertyInfo.java") is TRUE, so the more
                // specific suffix must be tested FIRST. With the order reversed the PropertyInfo
                // branch swallowed JsonPropertyInfo, and PatchMemberClassInfoLookup silently never
                // ran on a freshly generated file - the patch only ever appeared on a tree that had
                // already been patched by hand.
                else if (result.FileName.EndsWith("JsonPropertyInfo.java", StringComparison.Ordinal))
                {
                    PatchMemberClassInfoLookup(result);
                }
                else if (result.FileName.EndsWith("PropertyInfo.java", StringComparison.Ordinal)
                    || result.FileName.EndsWith("FieldInfo.java", StringComparison.Ordinal))
                {
                    PatchMemberArgumentCapture(result);
                }
                else if (result.FileName.EndsWith("JsonSerializer.java", StringComparison.Ordinal))
                {
                    PatchSerializeRegistryReset(result);
                }
                else if (result.FileName.EndsWith("ThrowHelper.java", StringComparison.Ordinal))
                {
                    PatchAttributeLocationFormatting(result);
                }
            }
        }
    }

    /// <summary>
    /// Builds the SDK's "Type.Member" attribute-validation location from the C# type name.
    /// <para>
    /// The generated code used <c>classType.toString()</c>, which renders a Java <c>class</c> prefix
    /// and a <c>$</c> for a nested type, so the message read
    /// <c>class ...CustomConverterTests$PocoWithInvalidConverter.MyInt</c> where the SDK asserts
    /// <c>...CustomConverterTests+PocoWithInvalidConverter.MyInt</c>. The validation itself fired
    /// correctly, so only the spelling was wrong — but the assertion is an
    /// <c>Assert.Contains</c> on that exact substring, so the whole test failed.
    /// </para>
    /// </summary>
    private static void PatchAttributeLocationFormatting(ConversionResult result)
    {
        if (result.GeneratedCode == null)
            return;

        var code = result.GeneratedCode;
        if (!AttributeLocationRegex.IsMatch(code))
            return;
        // Both methods in ThrowHelper share this first statement, and each needs its own argument
        // name, so replace one occurrence at a time. The helper already appends the member name,
        // so the follow-up "location += ..." that the generated code performs is removed as well —
        // leaving it appended the member twice (measured: "...PocoWithInvalidConverter.MyInt.MyInt").
        var replaced = 0;
        code = AttributeLocationRegex.Replace(code, match =>
        {
            replaced++;
            return "        String location = io.github.ningpp.compat.TypeHelper.getMemberLocation("
                + match.Groups["type"].Value + ", memberInfo);";
        });
        if (replaced == 0)
            return;

        code = AttributeLocationAppendRegex.Replace(code, string.Empty);

        // The "@NotCompatible" message names the type the converter was asked for, and C# spells a
        // generic as `Name`arity[args] while Java erases it to the raw interface. Measured: the
        // assertion expects
        //   System.Collections.Generic.ICollection`1[...CustomConverterTests+InvalidTypeConverterEnum]
        // and the generated code passed the raw Class.
        if (AttributeNotCompatibleTypeRegex.IsMatch(code))
        {
            code = AttributeNotCompatibleTypeRegex.Replace(code,
                "SR.getSerializationConverterOnAttributeNotCompatible(), location, "
                + "io.github.ningpp.compat.TypeHelper.getCsTypeName(typeToConvert)",
                count: 1);
        }
        else
        {
            Console.Error.WriteLine(
                "ProjectPasses: PatchAttributeLocationFormatting did not match the "
                + "@NotCompatible message in ThrowHelper.java - the C# type-name rendering is "
                + "NOT applied.");
        }

        result.GeneratedCode = code;
    }

    private static readonly System.Text.RegularExpressions.Regex AttributeNotCompatibleTypeRegex = new(
        @"SR\.getSerializationConverterOnAttributeNotCompatible\(\), location, typeToConvert",
        System.Text.RegularExpressions.RegexOptions.None);

    private static readonly System.Text.RegularExpressions.Regex AttributeLocationRegex = new(
        @"        String location = (?<type>[A-Za-z_][A-Za-z0-9_]*)\.toString\(\);",
        System.Text.RegularExpressions.RegexOptions.None);

    /// <summary>
    /// Removes the generated <c>if (memberInfo != null) { location += "." + memberInfo.getName(); }</c>
    /// that follows the replaced statement, because the compat helper already appends the member.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex AttributeLocationAppendRegex = new(
        @"        if \(memberInfo != null\) \{\r?\n        location \+= [^\r\n]*;\r?\n        \}\r?\n",
        System.Text.RegularExpressions.RegexOptions.None);

    /// <summary>
    /// Clears the per-thread type-argument registries at the <c>serialize(Class, value, options)</c>
    /// entry.
    /// <para>
    /// Compat's registries (<c>TYPE_ARGUMENTS</c>, <c>CONSUMED_ARGUMENTS</c>,
    /// <c>MEMBER_ARGUMENTS</c>, <c>CALLSITE_ARGUMENTS</c>, <c>WRAPPER_LAYER_TAKEN</c>,
    /// <c>CONCRETE_SUBTYPES</c>) are ThreadLocals that outlive one operation, so a member or call site
    /// read by an earlier (de)serialization could still answer lookups in a later one. That is what
    /// made ~28 nested-collection WRITE tests fail in full-suite order while passing when their class
    /// ran alone; clearing at the entry measured 790 to 723.
    /// </para>
    /// </summary>
    private static void PatchSerializeRegistryReset(ConversionResult result)
    {
        if (result.GeneratedCode == null)
            return;

        if (!SerializeRegistryResetRegex.IsMatch(result.GeneratedCode))
        {
            Console.Error.WriteLine(
                "ProjectPasses: PatchSerializeRegistryReset did not match JsonSerializer.java - the "
                + "generated serialize(Class, TValue, JsonSerializerOptions) entry changed; the "
                + "per-thread registry reset is NOT applied.");
            return;
        }

        result.GeneratedCode = SerializeRegistryResetRegex.Replace(
            result.GeneratedCode,
            "public static <TValue> String serialize(Class<TValue> _cs2j_TValue, TValue value, JsonSerializerOptions options) {\n"
            + "        TypeHelper.clearRegistrations();",
            count: 1);
        EnsureTypeHelperImport(result);
    }

    private static readonly System.Text.RegularExpressions.Regex SerializeRegistryResetRegex = new(
        @"public static <TValue> String serialize\(Class<TValue> _cs2j_TValue, TValue value, JsonSerializerOptions options\) \{",
        System.Text.RegularExpressions.RegexOptions.None);

    /// <summary>
    /// The generated <c>IEnumerableConverter.createCollection</c> builds <c>new CSharpList&lt;Object&gt;()</c>
    /// for the interface-typed branch (the conversion of C# <c>new List&lt;TElement&gt;()</c>). For a
    /// converted non-generic collection (CSharpArrayList) that is neither the target type nor the
    /// type <c>getRuntimeType()</c> reports, so the guard rejects the collection outright and — once
    /// the guard is satisfied — the populated list holds CSharpList elements, breaking the
    /// conversion to <c>CSharpArrayList[]</c> with an ArrayStoreException. Populate an instance of the
    /// target type instead; compat keeps the rejection for a target that truly cannot be populated.
    /// </summary>
    private static void PatchIEnumerableConverterCreateCollection(ConversionResult result)
    {
        if (result.GeneratedCode == null)
            return;

        // Replace the guard together with the creation, so the patch does not depend on the two
        // statements staying adjacent.
        var match = IEnumerableCreateCollectionRegex.Match(result.GeneratedCode);
        if (!match.Success)
            return;

        var indent = match.Groups["indent"].Value;
        var patched =
            indent + "Object __cs2jCollection = TypeHelper.newNonGenericCollection(getTypeToConvert(), "
            + "TypeHelper.isAssignableFrom(getTypeToConvert(), getRuntimeType()));\n"
            + indent + "if (__cs2jCollection == null) {\n"
            + indent + "ThrowHelper.throwNotSupportedException_CannotPopulateCollection(getTypeToConvert(), reader.value, state.value);\n"
            + indent + "}\n"
            + indent + "state.value.Current = state.value.Current.withReturnValue(__cs2jCollection);";

        result.GeneratedCode = IEnumerableCreateCollectionRegex.Replace(
            result.GeneratedCode, patched.Replace("$", "$$"), count: 1);
        EnsureTypeHelperImport(result);
    }

    private static readonly System.Text.RegularExpressions.Regex IEnumerableCreateCollectionRegex = new(
        @"(?<indent>[ \t]*)if \(!TypeHelper\.isAssignableFrom\(getTypeToConvert\(\), getRuntimeType\(\)\)\) \{\r?\n"
        + @"[ \t]*ThrowHelper\.throwNotSupportedException_CannotPopulateCollection\(getTypeToConvert\(\), reader\.value, state\.value\);\r?\n"
        + @"[ \t]*\}\r?\n"
        + @"[ \t]*state\.value\.Current = state\.value\.Current\.withReturnValue\(new CSharpList<Object>\(\)\);",
        System.Text.RegularExpressions.RegexOptions.None);

    /// <summary>
    /// The generated list converters add through a direct cast of the return value to the generic
    /// list interface: <c>((TCollection)(state.value.Current.getReturnValue())).add(value)</c>. A
    /// converted non-generic list (CSharpArrayList) is a legitimate <c>IList&lt;T&gt;</c> in C# terms
    /// but cannot implement that interface in Java (C# <c>IList.Add</c> returns int, the generic
    /// <c>ICollection&lt;T&gt;.Add</c> returns boolean), so the cast throws ClassCastException. Route
    /// the add through a compat helper that hands back a live generic view of the same list.
    /// </summary>
    private static void PatchListConverterAdd(ConversionResult result)
    {
        if (result.GeneratedCode == null)
            return;

        var match = ListConverterAddRegex.Match(result.GeneratedCode);
        if (!match.Success)
            return;

        var indent = match.Groups["indent"].Value;
        var patched = indent + "TypeHelper.listToAddInto(state.value.Current.getReturnValue()).add(value);";
        result.GeneratedCode = ListConverterAddRegex.Replace(
            result.GeneratedCode, patched.Replace("$", "$$"), count: 1);
        EnsureTypeHelperImport(result);
    }

    private static readonly System.Text.RegularExpressions.Regex ListConverterAddRegex = new(
        @"(?<indent>[ \t]*)\(\(TCollection\)\(state\.value\.Current\.getReturnValue\(\)\)\)\.add\(value\);",
        System.Text.RegularExpressions.RegexOptions.None);

    /// <summary>
    /// Makes the generated converter cache read the key its writer used.
    /// <para>
    /// The write side goes through <c>ConcurrentHashMapHelper.tryAdd</c>, which stores a converter
    /// under <c>TypeHelper.converterCacheKey(typeToConvert)</c> — a closed-generic key whenever the
    /// member's type arguments are published. The generated read is a plain
    /// <c>_converters.containsKey(typeToConvert)</c> / <c>_converters.get(typeToConvert)</c> on the
    /// erased class, so it never finds the entry it just wrote and falls through to rebuilding the
    /// converter from whatever arguments are ambient. For a nested generic that is another member's
    /// arguments: <c>List&lt;List&lt;short&gt;&gt;</c> ends up served by <c>List&lt;string&gt;</c>'s
    /// <c>ListOfTConverter&lt;String&gt;</c> and reads <c>[10,11]</c> with the String element
    /// converter ("Cannot get the value of a token type 'StartArray' as a string"). Routing the read
    /// through the compat helpers keeps both sides on one key while still honouring the raw-class
    /// entries the converter list and warm-up maintain.
    /// </para>
    /// </summary>
    private static void PatchConverterCacheLookup(ConversionResult result)
    {
        if (result.GeneratedCode == null)
            return;

        var match = ConverterCacheLookupRegex.Match(result.GeneratedCode);
        if (!match.Success)
            return;

        var indent = match.Groups["indent"].Value;
        var patched =
            indent + "if (TypeHelper.converterCacheHas(_converters, typeToConvert)) {\n"
            + indent + "converter = TypeHelper.converterCacheGet(_converters, typeToConvert, dotnet.text.json.Serialization.JsonConverter.class);";

        result.GeneratedCode = ConverterCacheLookupRegex.Replace(
            result.GeneratedCode, patched.Replace("$", "$$"), count: 1);
        EnsureTypeHelperImport(result);
    }

    private static readonly System.Text.RegularExpressions.Regex ConverterCacheLookupRegex = new(
        @"(?<indent>[ \t]*)if \(_converters\.containsKey\(typeToConvert\)\) \{\r?\n"
        + @"[ \t]*converter = _converters\.get\(typeToConvert\);",
        System.Text.RegularExpressions.RegexOptions.None);

    /// <summary>
    /// .NET's <c>Type.GetInterfaces()</c> returns the transitive interface closure; Java's
    /// <c>Class.getInterfaces()</c> returns direct interfaces only. The generated
    /// <c>getCompatibleGenericInterface</c> walks <c>type.getInterfaces()</c>, so a type that
    /// inherits its interface through a superclass - or through a parent interface - never matches
    /// and the caller falls back to the generic IEnumerable converter, whose <c>createCollection</c>
    /// rejects the type ("The collection type '...' is abstract, an interface, or is read only").
    /// Route the walk through a compat helper that reproduces the .NET semantics.
    /// </summary>
    private static void PatchTransitiveInterfaceWalk(ConversionResult result)
    {
        if (result.GeneratedCode == null)
            return;

        if (!TransitiveInterfaceWalkRegex.IsMatch(result.GeneratedCode))
            return;

        result.GeneratedCode = TransitiveInterfaceWalkRegex.Replace(
            result.GeneratedCode, "for (Class typeToCheck : TypeHelper.getAllInterfaces(type)) {", count: 1);
        EnsureTypeHelperImport(result);
    }

    private static readonly System.Text.RegularExpressions.Regex TransitiveInterfaceWalkRegex = new(
        @"for \(Class typeToCheck : type\.getInterfaces\(\)\) \{",
        System.Text.RegularExpressions.RegexOptions.None);

    /// <summary>
    /// The generated <c>IDictionaryConverter.createCollection</c> interface branch populates a bare
    /// <c>new CSharpDictionary&lt;String,Object&gt;()</c> while its own <c>TCollection</c> is
    /// <c>CSharpIDictionary</c>, so the instance fails the boundary cast the caller performs (the
    /// test's <c>(CSharpIDictionary) JsonSerializer.deserialize(json, null,
    /// CSharpIDictionary.class)</c>). CSharpDictionary cannot implement CSharpIDictionary directly -
    /// the non-generic <c>IDictionary.remove</c> returns void while <c>IDictionary&lt;K,V&gt;.remove</c>
    /// returns boolean, a reflection-visible conflict - so compat supplies the bridge that mirrors
    /// C#'s implicit Dictionary to IDictionary conversion.
    /// </summary>
    private static void PatchDictionaryCreateCollection(ConversionResult result)
    {
        if (result.GeneratedCode == null)
            return;

        if (!DictionaryCreateCollectionRegex.IsMatch(result.GeneratedCode))
            return;

        var patched =
            "state.value.Current = state.value.Current.withReturnValue("
            + "TypeHelper.newNonGenericDictionary(getTypeToConvert(), "
            + "TypeHelper.isAssignableFrom(getTypeToConvert(), getRuntimeType())));";
        result.GeneratedCode = DictionaryCreateCollectionRegex.Replace(
            result.GeneratedCode, patched.Replace("$", "$$"), count: 1);
        EnsureTypeHelperImport(result);
    }

    private static readonly System.Text.RegularExpressions.Regex DictionaryCreateCollectionRegex = new(
        @"state\.value\.Current = state\.value\.Current\.withReturnValue\(new CSharpDictionary<String, Object>\(\)\);",
        System.Text.RegularExpressions.RegexOptions.None);

    /// <summary>
    /// Scopes the argument context to a collection's ELEMENT around the element class-info lookup.
    /// <para>
    /// <c>getElementClassInfo()</c> resolves the element with
    /// <c>getOptions().getOrAddClass(getElementType())</c>, and that reaches
    /// <c>TypeHelper.converterCacheKey</c>, which prefers <c>MEMBER_ARGUMENTS</c> — the arguments of
    /// whichever member was read last on this thread. The element's class info is therefore keyed by
    /// an unrelated member's arguments: for <c>CSharpGenericIList&lt;Object&gt;[]</c> the element got
    /// a <c>CSharpGenericIList&lt;String&gt;</c> converter, and the write path then called
    /// <c>StringConverter.write</c> on <c>Integer</c> elements ("class java.lang.Integer cannot be
    /// cast to class java.lang.String"). Every nested collection member (<c>List&lt;List&lt;T&gt;&gt;</c>
    /// and the whole <c>*Of*</c> family) failed the same way.
    /// </para>
    /// <para>
    /// The class info records its owner's declared arguments at construction, and the element lookup
    /// re-enters them for the duration of the call. The scope is deliberately limited to same-raw-class
    /// nesting inside compat: applying it unconditionally made
    /// <c>ImmutableCollectionsTests.serializeList/serializeStack</c> invoke getters on the wrong
    /// receiver in full-suite order.
    /// </para>
    /// <para>
    /// The element type itself is also derived from the erased class (<c>getElementType()</c>
    /// answers <c>Object</c> for <c>List&lt;List&lt;short&gt;&gt;</c>), so the element class info is
    /// looked up for <c>Object</c> and the nested list is read with the wrong converter — measured:
    /// <c>getElementClassInfo owner=CSharpList ownerArgs=[] elem=java.lang.Object</c> for the very
    /// class info that had just been created under <c>CSharpList[CSharpList&lt;Integer&gt;]</c>,
    /// failing with "Cannot get the value of a token type 'StartArray' as a string". The override is
    /// deliberately one-argument-only and only fires when the erased answer is <c>Object</c>: for a
    /// dictionary the first argument is the KEY, and applying it there moved
    /// <c>ImmutableDictionary</c>'s element from <c>String</c> to <c>Object</c> (measured,
    /// regressed).
    /// </para>
    /// </summary>
    private static void PatchElementArgumentScope(ConversionResult result)
    {
        if (result.GeneratedCode == null)
            return;

        var code = result.GeneratedCode;
        if (!ElementArgumentScopeCaptureRegex.IsMatch(code))
            return;

        code = ElementArgumentScopeCaptureRegex.Replace(code,
            "        setOptions(options);\n        __cs2jOwnerTypeArguments = TypeHelper.captureDeclaredTypeArguments(type);",
            count: 1);
        code = ElementArgumentScopeFieldRegex.Replace(code,
            "    private java.lang.reflect.Type[] __cs2jOwnerTypeArguments = new java.lang.reflect.Type[0];\n    public Class getElementType() {",
            count: 1);
        code = ElementArgumentScopeLookupRegex.Replace(code, ElementArgumentScopeLookupReplacement, count: 1);

        result.GeneratedCode = code;
        EnsureTypeHelperImport(result);
    }

    private const string ElementArgumentScopeLookupReplacement =
        "        Class __cs2jElement = getElementType();\n"
        + "        Object __cs2jCtx = TypeHelper.pushArgumentContext(getType(), __cs2jElement, __cs2jOwnerTypeArguments);\n"
        + "        Object __cs2jScope = TypeHelper.beginElementArgumentsScope(getType(), __cs2jElement, "
        + "TypeHelper.elementArgumentsOfOwner(__cs2jOwnerTypeArguments));\n"
        + "        try {\n"
        + "        _elementClassInfo = getOptions().getOrAddClass(__cs2jElement);\n"
        + "        } finally {\n"
        + "        TypeHelper.endElementArgumentsScope(__cs2jScope);\n"
        + "        TypeHelper.popArgumentContext(__cs2jCtx);\n"
        + "        }";

    private static readonly System.Text.RegularExpressions.Regex ElementArgumentScopeCaptureRegex = new(
        @"(?<indent>[ \t]*)setOptions\(options\);",
        System.Text.RegularExpressions.RegexOptions.None);

    private static readonly System.Text.RegularExpressions.Regex ElementArgumentScopeFieldRegex = new(
        @"(?<indent>[ \t]*)public Class getElementType\(\) \{",
        System.Text.RegularExpressions.RegexOptions.None);

    private static readonly System.Text.RegularExpressions.Regex ElementArgumentScopeLookupRegex = new(
        @"(?<indent>[ \t]*)_elementClassInfo = getOptions\(\)\.getOrAddClass\(getElementType\(\)\);",
        System.Text.RegularExpressions.RegexOptions.None);

    /// <summary>
    /// Makes the reflected member remember its OWN declared type arguments.
    /// <para>
    /// <c>TypeHelper.MEMBER_ARGUMENTS</c> is one slot per raw class, so when two members share a raw
    /// class with different arguments — <c>List&lt;string&gt;</c> and <c>List&lt;List&lt;short&gt;&gt;</c>
    /// both erase to CSharpList — whichever member is reflected last owns the slot and every other
    /// member's lookup then reads ITS arguments. The 2-D list property therefore received
    /// <c>List&lt;string&gt;</c>'s JsonClassInfo, and with it a String element converter that read
    /// <c>[10,11]</c> as a string. Recording the arguments against the member gives each member its
    /// own copy.
    /// </para>
    /// </summary>
    private static void PatchMemberArgumentCapture(ConversionResult result)
    {
        if (result.GeneratedCode == null)
            return;

        var code = result.GeneratedCode;
        var replaced = false;
        foreach (var regex in MemberArgumentCaptureRegexes)
        {
            if (!regex.IsMatch(code))
                continue;
            code = regex.Replace(code, match =>
                match.Value.Substring(0, match.Value.Length - 2) + ", this);");
            replaced = true;
        }

        if (!replaced)
            return;

        result.GeneratedCode = code;
        EnsureTypeHelperImport(result);
    }

    private static readonly System.Text.RegularExpressions.Regex[] MemberArgumentCaptureRegexes =
    {
        new(@"TypeHelper\.registerMemberType\(getter\.getGenericReturnType\(\)\);",
            System.Text.RegularExpressions.RegexOptions.None),
        new(@"TypeHelper\.registerMemberType\(setter\.getGenericParameterTypes\(\)\[0\]\);",
            System.Text.RegularExpressions.RegexOptions.None),
        new(@"TypeHelper\.registerMemberType\(field\.getGenericType\(\)\);",
            System.Text.RegularExpressions.RegexOptions.None),
    };

    /// <summary>
    /// Answers <c>getRuntimeClassInfo()</c> with the class info that the OWNING member's declared
    /// type arguments imply, instead of the erased runtime type's.
    /// <para>
    /// The generated lookup asks <c>getOrAddClass</c> for the erased runtime type, so every member
    /// whose declared type erases to the same raw class shares one class info: for a property
    /// declared <c>List&lt;List&lt;short&gt;&gt;</c> the lookup returns the <c>List&lt;string&gt;</c>
    /// entry, whose element converter is the String converter, and the read then fails with
    /// "The JSON value could not be converted to class io.github.ningpp.compat.CSharpList"
    /// (measured: the 2-D property's owner class info was constructed with
    /// <c>ownerArgs=[class java.lang.String]</c>).
    /// </para>
    /// <para>
    /// The member's own arguments ARE published while it is reflected
    /// (<c>PropertyInfo.getPropertyType()</c> calls <c>registerMemberType</c>, observed as
    /// <c>CSharpList&lt;CSharpList&lt;Integer&gt;&gt;</c>) and <c>registerMemberType</c> records
    /// them against the member, so <c>TypeHelper.classInfoForMember</c> can install that layer,
    /// delegate to the generated <c>getOrAddClass</c> — creating the entry under
    /// <c>CSharpList[CSharpList&lt;Integer&gt;]</c> — and restore the previous registry state in a
    /// <c>finally</c> block.
    /// </para>
    /// <para>
    /// Reads must restore the shared registries: leaving a member's layer installed is the
    /// test-order dependence this project spent several rounds chasing. A member that published no
    /// arguments returns null and the generated erased lookup runs unchanged.
    /// </para>
    /// </summary>
    private static void PatchMemberClassInfoLookup(ConversionResult result)
    {
        if (result.GeneratedCode == null)
            return;

        if (!MemberClassInfoLookupRegex.IsMatch(result.GeneratedCode))
        {
            // Loud rather than silent: this exact no-match shipped once behind a suffix-shadowed
            // dispatch branch, and the patch quietly stopped being applied on freshly generated
            // files - it only ever appeared on an already hand-patched tree. A warning here would
            // have caught it on the first regeneration.
            Console.Error.WriteLine(
                "ProjectPasses: PatchMemberClassInfoLookup did not match JsonPropertyInfo.java - "
                + "the generated getRuntimeClassInfo body changed; the member-keyed class-info "
                + "lookup is NOT applied.");
            return;
        }
        result.GeneratedCode = MemberClassInfoLookupRegex.Replace(
            result.GeneratedCode, MemberClassInfoLookupReplacement.Replace("$", "$$"), count: 1);
        EnsureTypeHelperImport(result);
    }

    /// <summary>
    /// Round-18 experiment switch. Set to false to regenerate without the write-path element
    /// property patch, which is the prime suspect for the cross-test contamination that makes ~28
    /// write tests fail only in full-suite order.
    /// </summary>
    private const bool WriteElementPropertyInfoEnabled = true;

    private const string MemberClassInfoLookupReplacement =
        "        Object __cs2jMemberInfo = TypeHelper.cachedClassInfoForMember("
        + "(io.github.ningpp.compat.MemberInfo)(Object)getMemberInfo(), getOptions(), getRuntimePropertyType());\n"
        + "        if (__cs2jMemberInfo instanceof dotnet.text.json.JsonClassInfo) {\n"
        + "        _runtimeClassInfo = (dotnet.text.json.JsonClassInfo)__cs2jMemberInfo;\n"
        + "        } else {\n"
        + "        _runtimeClassInfo = getOptions().getOrAddClass(getRuntimePropertyType());\n"
        + "        }";

    private static readonly System.Text.RegularExpressions.Regex MemberClassInfoLookupRegex = new(
        @"(?<indent>[ \t]*)_runtimeClassInfo = getOptions\(\)\.getOrAddClass\(getRuntimePropertyType\(\)\);",
        System.Text.RegularExpressions.RegexOptions.None);

    /// <summary>
    /// The generated WRITE path installs the element frame's property info from
    /// <c>JsonClassInfo.getElementClassInfo()</c>, which warm-up built from the erased element
    /// <c>Class</c>. When several members share a raw element class with different type arguments
    /// that class info can belong to another member, so the element frame gets a property of the
    /// wrong type and reflection then invokes a getter with the wrong receiver
    /// ("IllegalArgumentException: public ... getChild2IList() target=..."). Prefer the class info of
    /// the type the converter itself reports; compat falls back to the generated expression whenever
    /// the converter does not know its element type.
    /// </summary>
    private static void PatchWriteElementPropertyInfo(ConversionResult result)
    {
        if (result.GeneratedCode == null)
            return;

        var code = result.GeneratedCode;
        var replaced = false;
        foreach (var (regex, replacement) in WriteElementPropertyInfoPatches)
        {
            if (!regex.IsMatch(code))
                continue;
            code = regex.Replace(code, replacement.Replace("$", "$$"), count: 1);
            replaced = true;
        }

        if (!replaced)
            return;

        result.GeneratedCode = code;
        EnsureTypeHelperImport(result);
    }

    private static readonly (System.Text.RegularExpressions.Regex Regex, string Replacement)[] WriteElementPropertyInfoPatches =
    {
        // state.value.Current.DeclaredJsonPropertyInfo = state.value.Current.JsonClassInfo.getElementClassInfo().getPropertyInfoForClassInfo();
        (new System.Text.RegularExpressions.Regex(
            @"state\.value\.Current\.DeclaredJsonPropertyInfo = state\.value\.Current\.JsonClassInfo\.getElementClassInfo\(\)\.getPropertyInfoForClassInfo\(\);",
            System.Text.RegularExpressions.RegexOptions.None),
         "state.value.Current.DeclaredJsonPropertyInfo = (dotnet.text.json.JsonPropertyInfo)"
         + "(TypeHelper.writeElementPropertyInfo(this, options, state.value.Current.JsonClassInfo."
         + "getElementClassInfo().getPropertyInfoForClassInfo()));"),

        // state.value.Current = state.value.Current.withJsonPropertyInfo(state.value.Current.getJsonClassInfo().getElementClassInfo().getPropertyInfoForClassInfo());
        (new System.Text.RegularExpressions.Regex(
            @"state\.value\.Current = state\.value\.Current\.withJsonPropertyInfo\("
            + @"state\.value\.Current\.getJsonClassInfo\(\)\.getElementClassInfo\(\)\.getPropertyInfoForClassInfo\(\)\);",
            System.Text.RegularExpressions.RegexOptions.None),
         "state.value.Current = state.value.Current.withJsonPropertyInfo("
         + "(dotnet.text.json.JsonPropertyInfo)(TypeHelper.writeElementPropertyInfo(this, options, "
         + "state.value.Current.getJsonClassInfo().getElementClassInfo().getPropertyInfoForClassInfo())));"),
    };

    /// <summary>Makes sure a patched file can resolve <c>TypeHelper</c>.</summary>
    private static void EnsureTypeHelperImport(ConversionResult result)
    {
        const string typeHelperImport = "import io.github.ningpp.compat.TypeHelper;";
        if (result.GeneratedCode.Contains(typeHelperImport, StringComparison.Ordinal))
            return;

        var insertAt = result.GeneratedCode.IndexOf("package ", StringComparison.Ordinal);
        if (insertAt < 0)
            return;

        var lineEnd = result.GeneratedCode.IndexOf('\n', insertAt);
        if (lineEnd >= 0)
        {
            result.GeneratedCode = result.GeneratedCode.Insert(lineEnd + 1, "\n" + typeHelperImport + "\n");
        }
    }

    private static void PatchPushdownPrefixState(ConversionResult result)
    {        var code = result.GeneratedCode;
        var match = PushLineRegex.Match(code);
        if (!match.Success)
            return;

        var indent = match.Groups[1].Value;
        var replacement = $"{indent}this.array[this.tos++] = clonePushValue(value);";
        code = PushLineRegex.Replace(code, replacement, count: 1);

        // Insert the helper method before the push method
        var pushMethodMarker = indent.Length >= 4
            ? indent.Substring(0, indent.Length - 4) + "public void push(T value)"
            : "    public void push(T value)";
        var insertAt = code.IndexOf(pushMethodMarker, StringComparison.Ordinal);
        if (insertAt < 0)
        {
            insertAt = code.IndexOf("public void push(T value)", StringComparison.Ordinal);
        }
        if (insertAt >= 0)
        {
            code = code.Insert(insertAt, HelperMethod + "\n\n" + (indent.Length >= 4 ? indent.Substring(0, indent.Length - 4) : "    "));
        }

        result.GeneratedCode = code;
    }

    // Fix enum cast pattern: EnumType.values()[(int)(val)] → (EnumType) val
    // The converter incorrectly converts C# cast (EnumType)val to enum array indexing,
    // but val is already the enum object, not an ordinal.
    private static readonly System.Text.RegularExpressions.Regex EnumArrayIndexRegex = new(
        @"(\w+)\.values\(\)\[\(int\)\((\w+\.\w+)\)\]",
        System.Text.RegularExpressions.RegexOptions.None);

    private static void PatchAttributeValuePair(ConversionResult result)
    {
        var code = result.GeneratedCode;

        // Fix: LayerDirection.values()[(int)(attrVal.val)] → (LayerDirection) attrVal.val
        code = System.Text.RegularExpressions.Regex.Replace(
            code,
            @"(LayerDirection|ArrowStyle|EdgeDirection|Microsoft\.Msagl\.Drawing\.Shape)\.values\(\)\[\(int\)\(attrVal\.val\)\]",
            m => $"({m.Groups[1].Value}) attrVal.val");

        result.GeneratedCode = code;
    }
}


