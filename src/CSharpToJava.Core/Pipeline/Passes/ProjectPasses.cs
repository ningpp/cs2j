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
            }
        }
    }

    private static void PatchPushdownPrefixState(ConversionResult result)
    {
        var code = result.GeneratedCode;
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


