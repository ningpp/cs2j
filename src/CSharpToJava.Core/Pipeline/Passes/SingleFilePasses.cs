using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java.Rewriters;
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
    /// <summary>Preserved Java IR compilation unit for post-emit passes.</summary>
    public Java.JavaCompilationUnit? JavaCompilation { get; set; }
    /// <summary>Aggregated LINQ rewrite statistics from the desugar pass.</summary>
    public LinqRewrite.LinqRewriteStatistics? LinqStatistics { get; set; }
}

public sealed class SingleFileLinqDesugarPass : ICs2jPass<SingleFilePassState>, ICs2jPassMetricSource
{
    public string Name => nameof(SingleFileLinqDesugarPass);
    public Cs2jPassStage Stage => Cs2jPassStage.Desugar;
    public int RewriteCount { get; private set; }
    public LinqRewriteStatistics? LinqStatistics { get; private set; }

    public void Execute(SingleFilePassState state)
    {
        RewriteCount = 0;
        LinqStatistics = null;

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
            var stats = new LinqRewriteStatistics();

            // Phase 1: Desugar LINQ query expressions (from…in…where…select) to
            // equivalent method-call chains (Where/Select/OrderBy/GroupBy).
            // This is purely syntactic and requires no semantic model.
            var desugarer = new LinqQueryDesugarer();
            var desugaredRoot = (CompilationUnitSyntax)desugarer.Visit(state.SyntaxTree.GetRoot());
            RewriteCount += desugarer.DesugaredCount;
            stats.DesugaredQueryCount = desugarer.DesugaredCount;

            if (desugarer.DesugaredCount > 0)
            {
                // Rebuild the compilation and semantic model so that the LinqRewriter
                // in Phase 2 can resolve the desugared method calls (Where/Select/…).
                state.SyntaxTree = state.SyntaxTree.WithRootAndOptions(desugaredRoot, state.SyntaxTree.Options);
                state.Compilation = CSharpCompilation.Create(
                    state.Compilation.AssemblyName ?? "TempAssembly",
                    new[] { state.SyntaxTree, ConversionPipeline.GlobalUsingsTree },
                    state.Compilation.References,
                    state.Compilation.Options);
                state.Library = Cs2jLibraryFactory.CreateSingleFile(
                    desugaredRoot.ToFullString(),
                    state.Request.FileName,
                    state.SyntaxTree,
                    state.Compilation);
                state.Context.SemanticModel = state.Library.PrimaryCompilation?.GetSemanticModel(state.SyntaxTree)
                    ?? state.Compilation.GetSemanticModel(state.SyntaxTree);
            }

            // Phase 2: Rewrite LINQ method-call chains to procedural loops.
            var rewriter = new LinqRewriter(state.Context.SemanticModel, state.Request.Options);
            var rewrittenRoot = (CompilationUnitSyntax)rewriter.Visit(state.SyntaxTree.GetRoot());
            RewriteCount += rewriter.RewrittenLinqQueries;
            state.SyntaxTree = state.SyntaxTree.WithRootAndOptions(rewrittenRoot, state.SyntaxTree.Options);

            stats.MergeFrom(rewriter.Statistics);
            LinqStatistics = stats;
            state.LinqStatistics = stats;

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

        // Run user-registered IR rewriters
        foreach (var rewriter in _irRewriters)
        {
            rewriter.VisitCompilationUnit(javaCompilation);
        }

        // Run built-in compatibility IR rewriters (D4: migrated from PostGenerationRewriteEngine)
        RunBuiltInRewriters(javaCompilation);

        // Run built-in IR structure rewriters (variable deduplication, etc.)
        RunStructureRewriters(javaCompilation);

        // Run built-in Java metadata validation rewriters when metadata is available
        var javaLibrary = state.Context.TypeMappings.JavaLibrary;
        if (javaLibrary is not null)
        {
            var apiValidator = new JavaApiValidationRewriter(javaLibrary, state.Context.Diagnostics);
            apiValidator.VisitCompilationUnit(javaCompilation);

            var exceptionChecker = new JavaExceptionCheckRewriter(javaLibrary, state.Context.Diagnostics);
            exceptionChecker.VisitCompilationUnit(javaCompilation);
        }

        // Run IR validation rewriters (diagnostics only — no tree modifications)
        RunValidationRewriters(javaCompilation, state.Context.Diagnostics);

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
        state.JavaCompilation = javaCompilation;
        state.EmitSucceeded = true;
    }

    /// <summary>
    /// Runs the built-in compatibility IR rewriters that address known conversion error patterns.
    /// These rewriters were migrated from string-level post-processing (PostGenerationRewriteEngine)
    /// to the IR layer for more robust and precise transformations.
    /// </summary>
    private static void RunBuiltInRewriters(Java.JavaCompilationUnit compilation)
    {
        new OperatorPrecedenceRewriter().VisitCompilationUnit(compilation);
        new MapEntryTypeRewriter().VisitCompilationUnit(compilation);
        new MemberwiseCloneRewriter().VisitCompilationUnit(compilation);
        new MathMethodRewriter().VisitCompilationUnit(compilation);
        new DelegateInvocationRewriter().VisitCompilationUnit(compilation);
        new GenericArrayCreationRewriter().VisitCompilationUnit(compilation);
        new IntStreamBoxedRewriter().VisitCompilationUnit(compilation);
        new ArrayIterableConversionRewriter().VisitCompilationUnit(compilation);
        new CollectStreamRoundtripRewriter().VisitCompilationUnit(compilation);
        new StopwatchApiRewriter().VisitCompilationUnit(compilation);
        new StringConcatRewriter().VisitCompilationUnit(compilation);
        new EventHandlerLambdaRewriter().VisitCompilationUnit(compilation);
        new ExceptionApiRewriter().VisitCompilationUnit(compilation);
    }

    /// <summary>
    /// Runs IR structure rewriters that fix structural issues (variable name conflicts, etc.)
    /// before final code emission.
    /// </summary>
    private static void RunStructureRewriters(Java.JavaCompilationUnit compilation)
    {
        new VariableNameDeduplicationRewriter().VisitCompilationUnit(compilation);
    }

    /// <summary>
    /// Runs purely diagnostic IR validation rewriters that check for potential compilation
    /// errors in the generated Java code. These rewriters do not modify the IR tree.
    /// </summary>
    private static void RunValidationRewriters(Java.JavaCompilationUnit compilation, Context.DiagnosticCollector diagnostics)
    {
        new JavaVariableReferenceValidationRewriter(diagnostics).VisitCompilationUnit(compilation);
        new JavaStaticContextValidationRewriter(diagnostics).VisitCompilationUnit(compilation);
        new JavaTypeParameterValidationRewriter(diagnostics).VisitCompilationUnit(compilation);
    }
}