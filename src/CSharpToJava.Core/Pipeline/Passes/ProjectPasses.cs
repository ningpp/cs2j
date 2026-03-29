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
    public List<ConversionResult> Results { get; } = [];
}

public sealed class ProjectLinqDesugarPass : ICs2jPass<ProjectPassState>
{
    public string Name => nameof(ProjectLinqDesugarPass);
    public Cs2jPassStage Stage => Cs2jPassStage.Desugar;

    public void Execute(ProjectPassState state)
    {
        var runLinqRewrite = state.Context.Options.EnableLinqRewrite && !state.Context.Options.EffectivePreferStreamApi;
        if (!runLinqRewrite)
        {
            return;
        }

        var rewrittenTrees = new List<SyntaxTree>();
        foreach (var syntaxTree in state.Compilation.SyntaxTrees)
        {
            if (string.IsNullOrWhiteSpace(syntaxTree.FilePath) || syntaxTree.FilePath.StartsWith("<", StringComparison.Ordinal))
            {
                rewrittenTrees.Add(syntaxTree);
                continue;
            }

            try
            {
                var semanticModel = state.Compilation.GetSemanticModel(syntaxTree);
                var rewriter = new LinqRewriter(semanticModel, state.Context.Options);
                var rewrittenRoot = rewriter.Visit(syntaxTree.GetRoot());
                var rewrittenTree = rewrittenRoot is CompilationUnitSyntax rewrittenCompilationUnit
                    ? syntaxTree.WithRootAndOptions(rewrittenCompilationUnit, syntaxTree.Options)
                    : syntaxTree;

                foreach (var skipped in rewriter.SkippedLinqChains)
                {
                    state.Context.Diagnostics.Warning($"LINQ rewrite skipped in '{Path.GetFileName(syntaxTree.FilePath)}': {skipped}");
                }

                rewrittenTrees.Add(rewrittenTree);
            }
            catch (Exception ex)
            {
                state.Context.Diagnostics.Warning($"LINQ rewrite skipped in '{Path.GetFileName(syntaxTree.FilePath)}': {ex.Message}");
                rewrittenTrees.Add(syntaxTree);
            }
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
        foreach (var typeGroup in state.TypeGroups)
        {
            if (!ShouldEmitTypeGroup(typeGroup, state.EmitFilePaths))
            {
                continue;
            }

            var result = TypeGroupResolver.ConvertTypeGroup(typeGroup, state.Compilation, state.Context, _irRewriters);
            if (result != null)
            {
                state.Results.Add(result);
            }
        }
    }

    private static bool ShouldEmitTypeGroup(PartialTypeGroup typeGroup, ISet<string>? emitFilePaths)
    {
        if (emitFilePaths == null || emitFilePaths.Count == 0)
        {
            return true;
        }

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

        return candidatePaths.Any(path => emitFilePaths.Contains(Path.GetFullPath(path)));
    }
}

public sealed class ProjectCompatibilityEmitPass : ICs2jPass<ProjectPassState>
{
    public string Name => nameof(ProjectCompatibilityEmitPass);
    public Cs2jPassStage Stage => Cs2jPassStage.Emit;

    public void Execute(ProjectPassState state)
    {
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
        CrossPackageImportResolver.AddCrossPackageImports(state.Results, state.Context.Options.SharedCompatibilityPackage);
    }
}

public sealed class ProjectPostGenerationRewriteEmitPass : ICs2jPass<ProjectPassState>
{
    public string Name => nameof(ProjectPostGenerationRewriteEmitPass);
    public Cs2jPassStage Stage => Cs2jPassStage.Emit;

    public void Execute(ProjectPassState state)
    {
        PostGenerationRewriteEngine.ApplyCompatibilityRewrites(state.Results);
    }
}