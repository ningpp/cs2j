using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java.Rewriters;
using CSharpToJava.Core.LinqRewrite;
using CSharpToJava.Core.TypeScript;
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

        var isTsTarget = state.Request.Options.TargetLanguage == TargetLanguage.TypeScript;
        var runLinqRewrite = state.Request.Options.EnableLinqRewrite && !state.Request.Options.EffectivePreferStreamApi;
        if (!runLinqRewrite && !isTsTarget)
        {
            return;
        }

        if (state.Context.SemanticModel == null)
        {
            if (runLinqRewrite)
            {
                state.Context.Diagnostics.Warning("LINQ rewrite skipped: semantic model unavailable.");
            }
            return;
        }

        try
        {
            var stats = new LinqRewriteStatistics();

            // Phase 1: Desugar LINQ query expressions (from..in..where..select) to
            // equivalent method-call chains (Where/Select/OrderBy/GroupBy).
            // This is purely syntactic and requires no semantic model.
            if (runLinqRewrite)
            {
                var desugarer = new LinqQueryDesugarer();
                var desugaredRoot = (CompilationUnitSyntax)desugarer.Visit(state.SyntaxTree.GetRoot());
                RewriteCount += desugarer.DesugaredCount;
                stats.DesugaredQueryCount = desugarer.DesugaredCount;
                if (desugarer.DesugaredCount > 0)
                {
                    // Rebuild the compilation and semantic model so that the
                    // member-mapping / LINQ rewrites can resolve the desugared calls.
                    RebuildCompilation(state, desugaredRoot);
                }
            }

            // Phase 1.5 (Task 36): TS-target BCL member mapping (MemberMappings.json).
            // Must run before the LINQ rewrite: after Enumerable.xxx rewriting the
            // lambda parameter types are unresolvable (compilation has no Enumerable
            // reference) and member mapping would degrade to Java-ized names.
            // Independent of EnableLinqRewrite so that --no-linq-rewrite still maps.
            if (isTsTarget)
            {
                var memberMappingTable = new TsMemberMappingTable(
                    state.Request.Options.TypeScriptMappingConfigPath,
                    state.Request.Options.TypeScriptMemberMappingPath,
                    state.Request.Options.TypeMappingConfigPath);
                var memberRoot = new TypeScript.Rewriters.TsMemberMappingRewriter(
                    state.Context.SemanticModel, memberMappingTable, state.Context)
                    .Visit(state.SyntaxTree.GetRoot());
                if (memberRoot is CompilationUnitSyntax memberCompilationUnit
                    && !ReferenceEquals(memberCompilationUnit, state.SyntaxTree.GetRoot()))
                {
                    RebuildCompilation(state, memberCompilationUnit);
                }
            }

            if (!runLinqRewrite)
            {
                // TS target with LINQ rewrite disabled: member mapping only.
                return;
            }

            // Phase 2: LINQ chain rewriting. Java target -> procedural loops
            // (LinqRewriter); TS target (Task 36, spec 2.4 LINQ target fork) ->
            // simple chains (Where/Select with side-effect-free lambdas) become
            // array methods filter/map, complex chains become Enumerable.xxx
            // lazy chains (TsLinqRewriter, spec 4.3.8).
            CompilationUnitSyntax rewrittenRoot;
            if (isTsTarget)
            {
                var tsRewriter = new TypeScript.Rewriters.TsLinqRewriter(state.Context.SemanticModel, state.Context);
                rewrittenRoot = (CompilationUnitSyntax)tsRewriter.Visit(state.SyntaxTree.GetRoot());
                RewriteCount += tsRewriter.RewrittenCalls;
                stats.RewrittenChainCount += tsRewriter.RewrittenChains;
                LinqStatistics = stats;
                state.LinqStatistics = stats;
            }
            else
            {
                var rewriter = new LinqRewriter(state.Context.SemanticModel, state.Request.Options);
                rewrittenRoot = (CompilationUnitSyntax)rewriter.Visit(state.SyntaxTree.GetRoot());
                RewriteCount += rewriter.RewrittenLinqQueries;

                stats.MergeFrom(rewriter.Statistics);
                LinqStatistics = stats;
                state.LinqStatistics = stats;

                foreach (var skipped in rewriter.SkippedLinqChains)
                {
                    state.Context.Diagnostics.Warning("LINQ rewrite skipped: " + skipped + " (will use Stream API fallback)");
                }
            }

            RebuildCompilation(state, rewrittenRoot);
        }
        catch (Exception ex)
        {
            state.Context.Diagnostics.Warning("LINQ rewrite skipped due to error: " + ex.Message);
        }
    }

    /// <summary>Rebuild tree/compilation/library/semantic model from a new root.</summary>
    private static void RebuildCompilation(SingleFilePassState state, CompilationUnitSyntax root)
    {
        state.SyntaxTree = state.SyntaxTree.WithRootAndOptions(root, state.SyntaxTree.Options);
        state.Compilation = CSharpCompilation.Create(
            state.Compilation.AssemblyName ?? "TempAssembly",
            new[] { state.SyntaxTree, ConversionPipeline.GlobalUsingsTree },
            state.Compilation.References,
            state.Compilation.Options);
        state.Library = Cs2jLibraryFactory.CreateSingleFile(
            root.ToFullString(),
            state.Request.FileName,
            state.SyntaxTree,
            state.Compilation);
        state.Context.SemanticModel = state.Library.PrimaryCompilation?.GetSemanticModel(state.SyntaxTree)
            ?? state.Compilation.GetSemanticModel(state.SyntaxTree);
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
        VarTypeResolver.PreScan(state.Compilation, state.Context);
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

        // 1f. 泛型声明模式提升（Java 路径）：`x is List<T> v` → 类型模式 + 段首强转声明，
        //     保证模式变量保持完整泛型类型（与项目路径 ProjectJavaPatternRewritePass 一致）。
        //     改写后重建编译与语义模型，供 visitor 查询。
        if (state.Context.SemanticModel != null)
        {
            var patternRoot = new Rewriters.JavaGenericPatternRewriter()
                .Visit(state.SyntaxTree.GetRoot());
            if (patternRoot != null && !ReferenceEquals(patternRoot, state.SyntaxTree.GetRoot()))
            {
                state.SyntaxTree = state.SyntaxTree.WithRootAndOptions(
                    (Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax)patternRoot,
                    state.SyntaxTree.Options);
                state.Compilation = CSharpCompilation.Create(
                    state.Compilation.AssemblyName ?? "TempAssembly",
                    new[] { state.SyntaxTree, ConversionPipeline.GlobalUsingsTree },
                    state.Compilation.References,
                    state.Compilation.Options);
                state.Library = Cs2jLibraryFactory.CreateSingleFile(
                    state.SyntaxTree.GetRoot().ToFullString(),
                    state.Request.FileName,
                    state.SyntaxTree,
                    state.Compilation);
                state.Context.SemanticModel = state.Library.PrimaryCompilation?.GetSemanticModel(state.SyntaxTree)
                    ?? state.Compilation.GetSemanticModel(state.SyntaxTree);
            }
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
        new LineContinuationTrimRewriter().VisitCompilationUnit(compilation);
        new StringConcatRewriter().VisitCompilationUnit(compilation);
        new EventHandlerLambdaRewriter().VisitCompilationUnit(compilation);
        new ExceptionApiRewriter().VisitCompilationUnit(compilation);
        new InitActiveDiagonalsNullGuardRewriter().VisitCompilationUnit(compilation);
        new TangentBetweenBranchesNoopGuardRewriter().VisitCompilationUnit(compilation);
        new FindClosestPointsMaxIterationRewriter().VisitCompilationUnit(compilation);
    }

    /// <summary>
    /// Runs IR structure rewriters that fix structural issues (variable name conflicts, etc.)
    /// before final code emission.
    /// </summary>
    private static void RunStructureRewriters(Java.JavaCompilationUnit compilation)
    {
        new VariableNameDeduplicationRewriter().VisitCompilationUnit(compilation);
        new ImplicitCastCompletionRewriter().VisitCompilationUnit(compilation);
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
/// <summary>
/// TS 目标发射 pass（Task 11）：复用 CSharpToJavaVisitor 生成 Java IR，仅跑用户注册的
/// 目标无关 rewriter，然后由 TypeScriptEmitter 二次映射发射 .ts（Codegen 层分叉）。
/// Java 专属内置 rewriter（RunBuiltInRewriters/RunStructureRewriters）不进入本 pass。
/// </summary>
public sealed class SingleFileTsEmitPass : ICs2jPass<SingleFilePassState>
{
    private readonly IReadOnlyList<Java.JavaSyntaxRewriter> _irRewriters;

    public SingleFileTsEmitPass(IReadOnlyList<Java.JavaSyntaxRewriter> irRewriters)
    {
        _irRewriters = irRewriters;
    }

    public string Name => nameof(SingleFileTsEmitPass);
    public Cs2jPassStage Stage => Cs2jPassStage.Emit;

    public void Execute(SingleFilePassState state)
    {
        if (state.BlockEmit)
        {
            state.EmitSucceeded = false;
            state.GeneratedCode = string.Empty;
            return;
        }

        // 1. Task 19 数值语义：C# 语法树级改写（仅 TS 路径）。IR 方法体为 raw 文本
        //    （JavaRawStatement），类型信息在 IR 层不可得；在语法树 + 语义模型上完成
        //    整数除法/窄化转换/bigint 混算/List.Clear 判定，改写产物经共享 visitor
        //    流入 raw 文本（Math.trunc/BigInt/Number 在 Java 文本与 TS 中同形）。
        //    改写后重建编译与语义模型（模式同 LINQ desugar pass），供 visitor 查询。
        if (state.Context.SemanticModel != null)
        {
            var numericRoot = new TypeScript.Rewriters.TsNumericSemanticsRewriter(state.Context)
                .Visit(state.SyntaxTree.GetRoot());
            if (numericRoot != null && !ReferenceEquals(numericRoot, state.SyntaxTree.GetRoot()))
            {
                state.SyntaxTree = state.SyntaxTree.WithRootAndOptions(
                    (Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax)numericRoot,
                    state.SyntaxTree.Options);
                state.Compilation = CSharpCompilation.Create(
                    state.Compilation.AssemblyName ?? "TempAssembly",
                    new[] { state.SyntaxTree, ConversionPipeline.GlobalUsingsTree },
                    state.Compilation.References,
                    state.Compilation.Options);
                state.Library = Cs2jLibraryFactory.CreateSingleFile(
                    state.SyntaxTree.GetRoot().ToFullString(),
                    state.Request.FileName,
                    state.SyntaxTree,
                    state.Compilation);
                state.Context.SemanticModel = state.Library.PrimaryCompilation?.GetSemanticModel(state.SyntaxTree)
                    ?? state.Compilation.GetSemanticModel(state.SyntaxTree);
            }
        }

        // 1b. Task 23 重载消歧：语法树层改写（方法重载改名 + 调用点同步 + 构造器工厂调用点）。
        //     在数值语义重建后运行（需有效语义模型做重载决议）；改写后重建编译供 IR visitor 查询。
        if (state.Context.SemanticModel != null)
        {
            var overloadRoot = new TypeScript.Rewriters.TsOverloadDisambiguationRewriter(state.Context)
                .Visit(state.SyntaxTree.GetRoot());
            if (overloadRoot != null && !ReferenceEquals(overloadRoot, state.SyntaxTree.GetRoot()))
            {
                state.SyntaxTree = state.SyntaxTree.WithRootAndOptions(
                    (Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax)overloadRoot,
                    state.SyntaxTree.Options);
                state.Compilation = CSharpCompilation.Create(
                    state.Compilation.AssemblyName ?? "TempAssembly",
                    new[] { state.SyntaxTree, ConversionPipeline.GlobalUsingsTree },
                    state.Compilation.References,
                    state.Compilation.Options);
                state.Library = Cs2jLibraryFactory.CreateSingleFile(
                    state.SyntaxTree.GetRoot().ToFullString(),
                    state.Request.FileName,
                    state.SyntaxTree,
                    state.Compilation);
                state.Context.SemanticModel = state.Library.PrimaryCompilation?.GetSemanticModel(state.SyntaxTree)
                    ?? state.Compilation.GetSemanticModel(state.SyntaxTree);
            }
        }

        // 1c. Task 24 泛型擦除：语法树层改写（typeof(T)/default(T)/new T()/is T 降级 +
        //     工厂参数注入与调用点同步）。在重载重建后运行（需有效语义模型做类型参数
        //     判定与调用点解析）；改写后重建编译供 IR visitor 查询。
        if (state.Context.SemanticModel != null)
        {
            var genericRoot = new TypeScript.Rewriters.TsGenericErasureRewriter(state.Context)
                .Visit(state.SyntaxTree.GetRoot());
            if (genericRoot != null && !ReferenceEquals(genericRoot, state.SyntaxTree.GetRoot()))
            {
                state.SyntaxTree = state.SyntaxTree.WithRootAndOptions(
                    (Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax)genericRoot,
                    state.SyntaxTree.Options);
                state.Compilation = CSharpCompilation.Create(
                    state.Compilation.AssemblyName ?? "TempAssembly",
                    new[] { state.SyntaxTree, ConversionPipeline.GlobalUsingsTree },
                    state.Compilation.References,
                    state.Compilation.Options);
                state.Library = Cs2jLibraryFactory.CreateSingleFile(
                    state.SyntaxTree.GetRoot().ToFullString(),
                    state.Request.FileName,
                    state.SyntaxTree,
                    state.Compilation);
                state.Context.SemanticModel = state.Library.PrimaryCompilation?.GetSemanticModel(state.SyntaxTree)
                    ?? state.Compilation.GetSemanticModel(state.SyntaxTree);
            }
        }

        // 1d. Task 26 相等语义：语法树层改写（spec §2.6.4）。在泛型擦除重建后运行
        //     （需有效语义模型做类型判定）：record/struct/覆写 Equals 的类的 ==/!=/
        //     .Equals() 调用点 → CsObject.equals；ReferenceEquals → ==（TS ===）；
        //     其余 ==/!= 保持运算符形态由 BinaryExpressionTransformer 的 TS 分叉
        //     发射 ===/!==。改写后重建编译供 IR visitor 查询。
        if (state.Context.SemanticModel != null)
        {
            var equalityRoot = new TypeScript.Rewriters.TsEqualityRewriter(state.Context)
                .Visit(state.SyntaxTree.GetRoot());
            if (equalityRoot != null && !ReferenceEquals(equalityRoot, state.SyntaxTree.GetRoot()))
            {
                state.SyntaxTree = state.SyntaxTree.WithRootAndOptions(
                    (Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax)equalityRoot,
                    state.SyntaxTree.Options);
                state.Compilation = CSharpCompilation.Create(
                    state.Compilation.AssemblyName ?? "TempAssembly",
                    new[] { state.SyntaxTree, ConversionPipeline.GlobalUsingsTree },
                    state.Compilation.References,
                    state.Compilation.Options);
                state.Library = Cs2jLibraryFactory.CreateSingleFile(
                    state.SyntaxTree.GetRoot().ToFullString(),
                    state.Request.FileName,
                    state.SyntaxTree,
                    state.Compilation);
                state.Context.SemanticModel = state.Library.PrimaryCompilation?.GetSemanticModel(state.SyntaxTree)
                    ?? state.Compilation.GetSemanticModel(state.SyntaxTree);
            }
        }

        // 1e. Task 27 索引器：语法层改写用户索引器调用点
        //     b[i] → b.get_Item(i)、b[i] = v → b.set_Item(i, v)（spec §1.4.3）。
        //     在相等语义重建后运行（需有效语义模型判定用户索引器）；
        //     改写后重建编译供 IR visitor 查询。
        if (state.Context.SemanticModel != null)
        {
            var indexerRoot = new TypeScript.Rewriters.TsIndexerRewriter(state.Context)
                .Visit(state.SyntaxTree.GetRoot());
            if (indexerRoot != null && !ReferenceEquals(indexerRoot, state.SyntaxTree.GetRoot()))
            {
                state.SyntaxTree = state.SyntaxTree.WithRootAndOptions(
                    (Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax)indexerRoot,
                    state.SyntaxTree.Options);
                state.Compilation = CSharpCompilation.Create(
                    state.Compilation.AssemblyName ?? "TempAssembly",
                    new[] { state.SyntaxTree, ConversionPipeline.GlobalUsingsTree },
                    state.Compilation.References,
                    state.Compilation.Options);
                state.Library = Cs2jLibraryFactory.CreateSingleFile(
                    state.SyntaxTree.GetRoot().ToFullString(),
                    state.Request.FileName,
                    state.SyntaxTree,
                    state.Compilation);
                state.Context.SemanticModel = state.Library.PrimaryCompilation?.GetSemanticModel(state.SyntaxTree)
                    ?? state.Compilation.GetSemanticModel(state.SyntaxTree);
            }
        }

        // 1f. Task 27 模式匹配：语法层改写（if/while 声明模式 → 类型模式 + 段首声明、
        //     switch 表达式 → 嵌套三元、switch 语句模式标签 → 类型模式 + 段首声明）。
        //     在前端 Java 16 形态降低之前改写；改写后重建编译供 visitor 查询。
        if (state.Context.SemanticModel != null)
        {
            var patternRoot = new TypeScript.Rewriters.TsPatternMatchingRewriter()
                .Visit(state.SyntaxTree.GetRoot());
            if (patternRoot != null && !ReferenceEquals(patternRoot, state.SyntaxTree.GetRoot()))
            {
                state.SyntaxTree = state.SyntaxTree.WithRootAndOptions(
                    (Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax)patternRoot,
                    state.SyntaxTree.Options);
                state.Compilation = CSharpCompilation.Create(
                    state.Compilation.AssemblyName ?? "TempAssembly",
                    new[] { state.SyntaxTree, ConversionPipeline.GlobalUsingsTree },
                    state.Compilation.References,
                    state.Compilation.Options);
                state.Library = Cs2jLibraryFactory.CreateSingleFile(
                    state.SyntaxTree.GetRoot().ToFullString(),
                    state.Request.FileName,
                    state.SyntaxTree,
                    state.Compilation);
                state.Context.SemanticModel = state.Library.PrimaryCompilation?.GetSemanticModel(state.SyntaxTree)
                    ?? state.Compilation.GetSemanticModel(state.SyntaxTree);
            }
        }

        // 1g. Task 39 测试收集：xUnit/MSTest 测试类元数据 → context.TsTestClasses
        //     （只收集不改写；TsTestAssertRewriter 与发射器据此输出 vitest describe/it）
        if (state.Context.SemanticModel != null)
        {
            new TypeScript.Rewriters.TsTestCollectorRewriter(state.Context)
                .Visit(state.SyntaxTree.GetRoot());
        }

        // 2. 生成 Java IR（与 Java 路径一致）
        var visitor = new CSharpToJavaVisitor(state.Context);
        var compilationUnit = visitor.Visit(state.SyntaxTree.GetRoot());
        if (compilationUnit is not Java.JavaCompilationUnit javaCompilation)
        {
            state.EmitSucceeded = false;
            return;
        }

        // 3. 运行用户注册的 IR rewriter（目标无关）
        foreach (var rewriter in _irRewriters)
        {
            rewriter.VisitCompilationUnit(javaCompilation);
        }

        // 4/5. TS 专属 IR rewriter 装配 + 发射（Task 38：与项目级 ProjectTsEmitPass
        // 共享 TsEmitSupport，保证单文件/项目路径行为一致）
        var code = TypeScript.TsEmitSupport.EmitTypeScript(javaCompilation, state.Context);

        state.GeneratedCode = code;
        state.JavaCompilation = javaCompilation;
        state.EmitSucceeded = true;
    }


}
