using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.LinqRewrite;
using CSharpToJava.Core.Visitors;

namespace CSharpToJava.Core.Pipeline;

public sealed class SingleFilePassState
{
    public required ConversionRequest Request { get; init; }
    public required ConversionContext Context { get; init; }
    public required SyntaxTree SyntaxTree { get; set; }
    public required CSharpCompilation Compilation { get; set; }
    public required Cs2jLibrary Library { get; set; }
    public string GeneratedCode { get; set; } = string.Empty;
    public bool BlockEmit { get; set; }
    public bool EmitSucceeded { get; set; }
}

public sealed class SingleFileLinqDesugarPass : ICs2jPass<SingleFilePassState>, ICs2jPassMetricSource
{
    public string Name => nameof(SingleFileLinqDesugarPass);
    public Cs2jPassStage Stage => Cs2jPassStage.Desugar;
    public int RewriteCount { get; private set; }

    public void Execute(SingleFilePassState state)
    {
        RewriteCount = 0;

        var runLinqRewrite = state.Request.Options.EnableLinqRewrite && !state.Request.Options.EffectivePreferStreamApi;
        if (!runLinqRewrite)
        {
            return;
        }

        if (state.Context.SemanticModel == null)
        {
            state.Context.Diagnostics.Warning("LINQ rewrite skipped: semantic model unavailable.");
            return;
        }

        try
        {
            var rewriter = new LinqRewriter(state.Context.SemanticModel, state.Request.Options);
            var rewrittenRoot = (CompilationUnitSyntax)rewriter.Visit(state.SyntaxTree.GetRoot());
            RewriteCount = rewriter.RewrittenLinqQueries;
            state.SyntaxTree = state.SyntaxTree.WithRootAndOptions(rewrittenRoot, state.SyntaxTree.Options);

            foreach (var skipped in rewriter.SkippedLinqChains)
            {
                state.Context.Diagnostics.Warning($"LINQ rewrite skipped: {skipped} (will use Stream API fallback)");
            }

            state.Compilation = CSharpCompilation.Create(
                state.Compilation.AssemblyName ?? "TempAssembly",
                new[] { state.SyntaxTree, ConversionPipeline.GlobalUsingsTree },
                state.Compilation.References,
                state.Compilation.Options);

            state.Library = Cs2jLibraryFactory.CreateSingleFile(
                rewrittenRoot.ToFullString(),
                state.Request.FileName,
                state.SyntaxTree,
                state.Compilation);

            state.Context.SemanticModel = state.Library.PrimaryCompilation?.GetSemanticModel(state.SyntaxTree)
                ?? state.Compilation.GetSemanticModel(state.SyntaxTree);
        }
        catch (Exception ex)
        {
            state.Context.Diagnostics.Warning($"LINQ rewrite skipped due to error: {ex.Message}");
        }
    }
}

public sealed class SingleFileCompilationCheckPass : ICs2jPass<SingleFilePassState>
{
    public string Name => nameof(SingleFileCompilationCheckPass);
    public Cs2jPassStage Stage => Cs2jPassStage.Check;

    public void Execute(SingleFilePassState state)
    {
        var compilation = state.Library.PrimaryCompilation;
        if (compilation == null)
        {
            throw new InvalidOperationException("Single-file library does not contain a primary compilation.");
        }

        if (!compilation.ContainsSyntaxTree(state.SyntaxTree))
        {
            throw new InvalidOperationException("Primary compilation does not contain the current syntax tree.");
        }

        state.Context.SemanticModel = compilation.GetSemanticModel(state.SyntaxTree);
    }
}

public sealed class SingleFileUnsupportedDomainCheckPass : ICs2jPass<SingleFilePassState>
{
    public string Name => nameof(SingleFileUnsupportedDomainCheckPass);
    public Cs2jPassStage Stage => Cs2jPassStage.Check;

    public void Execute(SingleFilePassState state)
    {
        var diagnostics = UnsupportedDomainAnalyzer.AnalyzeSyntaxTree(state.SyntaxTree);
        foreach (var diagnostic in diagnostics)
        {
            state.Context.Diagnostics.Error(diagnostic.Message, diagnostic.Location, diagnostic.Code, diagnostic.Category);
        }

        if (diagnostics.Any(diagnostic => diagnostic.Severity == Context.DiagnosticSeverity.Error))
        {
            state.BlockEmit = true;
        }
    }
}

public sealed class SingleFilePlatformBoundaryCheckPass : ICs2jPass<SingleFilePassState>
{
    public string Name => nameof(SingleFilePlatformBoundaryCheckPass);
    public Cs2jPassStage Stage => Cs2jPassStage.Check;

    public void Execute(SingleFilePassState state)
    {
        var semanticModel = state.Context.SemanticModel ?? state.Compilation.GetSemanticModel(state.SyntaxTree);
        var diagnostics = PlatformBoundaryAnalyzer.AnalyzeSyntaxTree(state.SyntaxTree, semanticModel);
        foreach (var diagnostic in diagnostics)
        {
            state.Context.Diagnostics.Error(diagnostic.Message, diagnostic.Location, diagnostic.Code, diagnostic.Category);
        }

        if (diagnostics.Any(diagnostic => diagnostic.Severity == Context.DiagnosticSeverity.Error))
        {
            state.BlockEmit = true;
        }
    }
}

public sealed class SingleFileNativeInteropCheckPass : ICs2jPass<SingleFilePassState>
{
    public string Name => nameof(SingleFileNativeInteropCheckPass);
    public Cs2jPassStage Stage => Cs2jPassStage.Check;

    public void Execute(SingleFilePassState state)
    {
        var semanticModel = state.Context.SemanticModel ?? state.Compilation.GetSemanticModel(state.SyntaxTree);
        var diagnostics = NativeInteropAnalyzer.AnalyzeSyntaxTree(state.SyntaxTree, semanticModel);
        foreach (var diagnostic in diagnostics)
        {
            state.Context.Diagnostics.Error(diagnostic.Message, diagnostic.Location, diagnostic.Code, diagnostic.Category);
        }

        if (diagnostics.Any(diagnostic => diagnostic.Severity == Context.DiagnosticSeverity.Error))
        {
            state.BlockEmit = true;
        }
    }
}

public sealed class SingleFileContextNormalizationPass : ICs2jPass<SingleFilePassState>
{
    public string Name => nameof(SingleFileContextNormalizationPass);
    public Cs2jPassStage Stage => Cs2jPassStage.Normalize;

    public void Execute(SingleFilePassState state)
    {
        state.Compilation = state.Library.PrimaryCompilation ?? state.Compilation;
        state.Context.ProjectCompilation = state.Compilation;
        state.Context.ClearImports();
        state.Context.ClearAliases();
        state.Context.ClearMergedTypes();
        state.Context.ClearSynthesizedRecords();
        state.Context.SemanticModel = state.Compilation.GetSemanticModel(state.SyntaxTree);
    }
}

public sealed class SingleFileJavaEmitPass : ICs2jPass<SingleFilePassState>
{
    private readonly IReadOnlyList<Java.JavaSyntaxRewriter> _irRewriters;

    public SingleFileJavaEmitPass(IReadOnlyList<Java.JavaSyntaxRewriter> irRewriters)
    {
        _irRewriters = irRewriters;
    }

    public string Name => nameof(SingleFileJavaEmitPass);
    public Cs2jPassStage Stage => Cs2jPassStage.Emit;

    public void Execute(SingleFilePassState state)
    {
        if (state.BlockEmit)
        {
            state.EmitSucceeded = false;
            state.GeneratedCode = string.Empty;
            return;
        }

        var visitor = new CSharpToJavaVisitor(state.Context);
        var compilationUnit = visitor.Visit(state.SyntaxTree.GetRoot());
        if (compilationUnit is not Java.JavaCompilationUnit javaCompilation)
        {
            state.EmitSucceeded = false;
            return;
        }

        foreach (var rewriter in _irRewriters)
        {
            rewriter.VisitCompilationUnit(javaCompilation);
        }

        var code = javaCompilation.ToString("");
        if (string.IsNullOrWhiteSpace(code) && state.Request.FileName != null)
        {
            if (state.SyntaxTree.GetRoot() is CompilationUnitSyntax root && root.Members.Count == 0)
            {
                state.GeneratedCode = "// " + Path.GetFileName(state.Request.FileName) + " - contains no convertible code";
                state.EmitSucceeded = true;
                return;
            }
        }

        state.GeneratedCode = code;
        state.EmitSucceeded = true;
    }
}