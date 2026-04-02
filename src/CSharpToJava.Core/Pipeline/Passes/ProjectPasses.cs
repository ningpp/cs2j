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
}

public sealed class ProjectLinqDesugarPass : ICs2jPass<ProjectPassState>, ICs2jPassMetricSource
{
    public string Name => nameof(ProjectLinqDesugarPass);
    public Cs2jPassStage Stage => Cs2jPassStage.Desugar;
    public int RewriteCount { get; private set; }

    public void Execute(ProjectPassState state)
    {
        RewriteCount = 0;

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
        RewriteCount += totalDesugared;

        if (totalDesugared > 0)
        {
            // Rebuild the compilation with the desugared trees so that the
            // LinqRewriter in Phase 2 can resolve the desugared method calls.
            var newCompilation = state.Compilation;
            for (int i = 0; i < originalTrees.Count; i++)
            {
                newCompilation = newCompilation.ReplaceSyntaxTree(originalTrees[i], desugarResults[i].SyntaxTree);
            }
            state.Compilation = newCompilation;
        }

        // Phase 2: Rewrite LINQ method-call chains to procedural loops.
        var rewriteResults = ProjectPassParallelism.RunDeterministic(
            state.Compilation.SyntaxTrees.ToList(),
            state.Context.Options.EnableParallelProjectPasses,
            syntaxTree => RewriteSyntaxTree(state, syntaxTree));

        var rewrittenTrees = new List<SyntaxTree>(rewriteResults.Count);
        foreach (var rewriteResult in rewriteResults)
        {
            RewriteCount += rewriteResult.RewriteCount;
            foreach (var warning in rewriteResult.Warnings)
            {
                state.Context.Diagnostics.Warning(warning);
            }

            rewrittenTrees.Add(rewriteResult.SyntaxTree);
        }

        state.Compilation = CSharpCompilation.Create(
            state.Compilation.AssemblyName ?? "TempAssembly",
            rewrittenTrees,
            state.Compilation.References,
            state.Compilation.Options);

        var primaryProject = state.Library.Projects.FirstOrDefault();
        state.Library = Cs2jLibraryFactory.CreateFromCompilation(
            state.Compilation,
            projectName: primaryProject?.Name,
            projectFilePath: primaryProject?.ProjectFilePath,
            projectReferences: primaryProject?.ProjectReferences?.ToList(),
            isTestProject: primaryProject?.IsTestProject ?? false,
            libraryName: state.Library.Name);
        state.Context.ProjectCompilation = state.Compilation;
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
            };
        }

        try
        {
            var desugarer = new LinqQueryDesugarer();
            var desugaredRoot = desugarer.Visit(syntaxTree.GetRoot());
            var desugaredTree = desugaredRoot is CompilationUnitSyntax desugaredCompUnit
                ? syntaxTree.WithRootAndOptions(desugaredCompUnit, syntaxTree.Options)
                : syntaxTree;

            return new ProjectLinqRewriteResult
            {
                SyntaxTree = desugaredTree,
                Warnings = [],
                RewriteCount = desugarer.DesugaredCount,
            };
        }
        catch (Exception ex)
        {
            return new ProjectLinqRewriteResult
            {
                SyntaxTree = syntaxTree,
                Warnings = [$"LINQ query desugar skipped in '{Path.GetFileName(syntaxTree.FilePath)}': {ex.Message}"],
                RewriteCount = 0,
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
            };
        }

        try
        {
            var semanticModel = state.Compilation.GetSemanticModel(syntaxTree);
            var rewriter = new LinqRewriter(semanticModel, state.Context.Options);
            var rewrittenRoot = rewriter.Visit(syntaxTree.GetRoot());
            var rewrittenTree = rewrittenRoot is CompilationUnitSyntax rewrittenCompilationUnit
                ? syntaxTree.WithRootAndOptions(rewrittenCompilationUnit, syntaxTree.Options)
                : syntaxTree;

            return new ProjectLinqRewriteResult
            {
                SyntaxTree = rewrittenTree,
                Warnings = rewriter.SkippedLinqChains
                    .Select(skipped => $"LINQ rewrite skipped in '{Path.GetFileName(syntaxTree.FilePath)}': {skipped}")
                    .ToList(),
                RewriteCount = rewriter.RewrittenLinqQueries,
            };
        }
        catch (Exception ex)
        {
            return new ProjectLinqRewriteResult
            {
                SyntaxTree = syntaxTree,
                Warnings = [$"LINQ rewrite skipped in '{Path.GetFileName(syntaxTree.FilePath)}': {ex.Message}"],
                RewriteCount = 0,
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

        if (!state.Context.Options.EmitCompatibilityHelpers)
        {
            return;
        }

        var basePackage = CompatibilityClassGenerator.DetermineBasePackage(state.Results);
        if (state.Context.Options.UseCompatibilityPacks)
        {
            var packRegistry = CompatibilityPackRegistry.CreateDefault();
            state.Results.AddRange(packRegistry.GenerateApplicable(state.Results, basePackage));
            return;
        }

        var includeTestContext = CompatibilityClassGenerator.RequiresTestContext(state.Results);
        state.Results.AddRange(CompatibilityClassGenerator.GenerateCompatibilitySupport(basePackage, includeTestContext));
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

public sealed class ProjectPostGenerationRewriteEmitPass : ICs2jPass<ProjectPassState>, ICs2jPassMetricSource
{
    public string Name => nameof(ProjectPostGenerationRewriteEmitPass);
    public Cs2jPassStage Stage => Cs2jPassStage.Emit;
    public int RewriteCount { get; private set; }

    public void Execute(ProjectPassState state)
    {
        RewriteCount = 0;

        if (!state.Results.Any(result => !string.IsNullOrEmpty(result.GeneratedCode)))
        {
            return;
        }

        RewriteCount = PostGenerationRewriteEngine.ApplyCompatibilityRewrites(state.Results);
    }
}