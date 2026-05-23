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

    private static string NormalizeFilePath(string filePath)
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

        var runLinqRewrite = state.Context.Options.EnableLinqRewrite && !state.Context.Options.EffectivePreferStreamApi;
        if (!runLinqRewrite)
        {
            return;
        }

        var originalTrees = state.Compilation.SyntaxTrees.ToList();

        // Phase 1: Desugar LINQ query expressions (from…in…where…select) to
        // method-call chains (Where/Select/OrderBy/GroupBy).
        // This is purely syntactic — no semantic model required.
        var desugarResults = ProjectPassParallelism.RunDeterministic(
            originalTrees,
            state.Context.Options.EnableParallelProjectPasses,
            syntaxTree => DesugarSyntaxTree(syntaxTree));

        int totalDesugared = desugarResults.Sum(r => r.RewriteCount);
        var desugarChanged = desugarResults.Any(r => r.Changed);
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

        // Phase 2: Rewrite LINQ method-call chains to procedural loops.
        var preRewriteCompilation = state.Compilation;
        var rewriteInputTrees = preRewriteCompilation.SyntaxTrees.ToList();
        var rewriteResults = ProjectPassParallelism.RunDeterministic(
            rewriteInputTrees,
            state.Context.Options.EnableParallelProjectPasses,
            syntaxTree => RewriteSyntaxTree(state, syntaxTree));

        var aggregatedStats = new LinqRewriteStatistics();
        aggregatedStats.DesugaredQueryCount = totalDesugared;
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

        var rewriteChanged = rewriteResults.Any(r => r.Changed);
        if (rewriteChanged)
        {
            // Save the compilation from immediately before the final procedural
            // LINQ rewrite replacement. Downstream transforms can use it as a
            // semantic fallback for syntax nodes that still point at pre-rewrite trees.
            state.Context.PreDesugarCompilation = preRewriteCompilation;
            state.Compilation = ReplaceChangedSyntaxTrees(preRewriteCompilation, rewriteInputTrees, rewriteResults);
        }

        if (!desugarChanged && !rewriteChanged)
        {
            return;
        }

        RebuildProjectStateAfterLinqRewrite(state);
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

    private static void RebuildProjectStateAfterLinqRewrite(ProjectPassState state)
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
            var rewriter = new LinqRewriter(semanticModel, state.Context.Options);
            var originalRoot = syntaxTree.GetRoot();
            var rewrittenRoot = rewriter.Visit(originalRoot);
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
                Warnings = rewriter.SkippedLinqChains
                    .Select(skipped => $"LINQ rewrite skipped in '{Path.GetFileName(syntaxTree.FilePath)}': {skipped}")
                    .ToList(),
                RewriteCount = rewriter.RewrittenLinqQueries,
                Changed = changed,
                Statistics = rewriter.Statistics,
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
                UnsupportedDomainAnalyzer.AnalyzeSyntaxTree(syntaxTree)));

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
        var syntaxTrees = state.Compilation.SyntaxTrees
            .Where(syntaxTree => !string.IsNullOrWhiteSpace(syntaxTree.FilePath) && !syntaxTree.FilePath.StartsWith("<", StringComparison.Ordinal))
            .ToList();

        var analysisResults = ProjectPassParallelism.RunDeterministic(
            syntaxTrees,
            state.Context.Options.EnableParallelProjectPasses,
            syntaxTree => new ProjectSyntaxTreeDiagnosticsResult(
                syntaxTree.FilePath,
                PlatformBoundaryAnalyzer.AnalyzeSyntaxTree(syntaxTree, state.Compilation.GetSemanticModel(syntaxTree))));

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

public sealed class ProjectNativeInteropCheckPass : ICs2jPass<ProjectPassState>
{
    public string Name => nameof(ProjectNativeInteropCheckPass);
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
                NativeInteropAnalyzer.AnalyzeSyntaxTree(syntaxTree, state.Compilation.GetSemanticModel(syntaxTree))));

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

        foreach (var typeGroup in state.TypeGroups)
        {
            var candidatePaths = CollectCandidatePaths(typeGroup);
            if (candidatePaths.Any(path => state.BlockedFilePaths.Contains(NormalizeFilePath(path))))
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

    private static List<string> CollectCandidatePaths(PartialTypeGroup typeGroup)
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

    private static bool ShouldEmitTypeGroup(IReadOnlyCollection<string> candidatePaths, ISet<string>? emitFilePaths)
    {
        if (emitFilePaths == null || emitFilePaths.Count == 0)
        {
            return true;
        }

        return candidatePaths.Any(path => emitFilePaths.Contains(Path.GetFullPath(path)));
    }

    private static string NormalizeFilePath(string filePath)
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

        CrossPackageImportResolver.AddCrossPackageImports(state.Results, state.Context.Options.SharedCompatibilityPackage);
    }
}

