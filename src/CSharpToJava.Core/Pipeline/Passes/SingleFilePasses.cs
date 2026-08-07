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

        // 4. 运行 TS 专属 rewriter（M1 暂空；Task 16 起装配属性重组，在用户 rewriter 之后、
        //    发射之前运行，Roles 侧表注入发射器）
        var (accessorRoles, compatSymbols) = RunTsRewriters(javaCompilation, state.Context);

        // 5. TS 发射
        var emitter = new TypeScript.TypeScriptEmitter(accessorRoles);
        var code = emitter.Emit(javaCompilation, state.Context, compatSymbols);

        state.GeneratedCode = code;
        state.JavaCompilation = javaCompilation;
        state.EmitSucceeded = true;
    }

    /// <summary>
    /// 运行 TS 专属 IR rewriter（Java 路径不进入本 pass）。Task 16：属性访问器重组
    /// （类自动属性 getX/setX → TS 字段/访问器 + 调用点改写）；返回角色侧表供
    /// TypeScriptEmitter 发射访问器使用，以及 rewriter 登记的 compat 符号
    /// （Task 18：CsString 等，发射器据此生成 @csharptotypescript/compat 导入）。
    /// M3 在此追加其余 rewriter。
    /// </summary>
    private static (IReadOnlyDictionary<Java.JavaMethodDeclaration, TypeScript.Rewriters.TsAccessorRole>? Roles,
        IReadOnlySet<string> CompatSymbols) RunTsRewriters(
        Java.JavaCompilationUnit compilation, ConversionContext context)
    {
        // TsOverloadDisambiguationRewriter, TsGenericErasureRewriter,
        // TsInterfaceProbeRewriter, TsEqualityRewriter (M3)
        var propertyRewriter = new TypeScript.Rewriters.TsPropertyReconstructionRewriter();
        propertyRewriter.VisitCompilationUnit(compilation);
        // Task 17：枚举转换调用点（getValue/fromValue/ordinal）改写 + Enum.Parse/ToString 诊断
        var enumRewriter = new TypeScript.Rewriters.TsEnumCallRewriter(context);
        enumRewriter.VisitCompilationUnit(compilation);
        // Task 18：字符串调用改写（String.format/StringHelper.formatCs/String.valueOf/
        // StringBuilder 链 → CsString.format/String(...)/模板字符串）
        var stringRewriter = new TypeScript.Rewriters.TsStringCallRewriter(context);
        stringRewriter.VisitCompilationUnit(compilation);
        // Task 23：构造器重载 → Create$N 工厂（IR 层合成；调用点已在语法层改写为 Type.Create$N(...)）
        var ctorFactoryRewriter = new TypeScript.Rewriters.TsCtorFactoryRewriter(context);
        ctorFactoryRewriter.VisitCompilationUnit(compilation);
        // Task 24：泛型擦除收尾（类字面量/类型参数强转/lambda 箭头/工厂参数类型与调用改写）
        var genericErasureRewriter = new TypeScript.Rewriters.TsGenericErasureIrRewriter(context);
        genericErasureRewriter.VisitCompilationUnit(compilation);
        // Task 25：接口运行时探针（is$IXxx 生成 + is/as 调用点改写 + 实现类构造器注入标记）
        var interfaceProbeRewriter = new TypeScript.Rewriters.TsInterfaceProbeRewriter(context);
        interfaceProbeRewriter.VisitCompilationUnit(compilation);
        // Task 27：struct 发射 TS 化（implements Cloneable 移除、clone()/合成 equals/hashCode
        // 方法体 → 合法 TS；值拷贝语义辅助 cloneStruct 归 M4）
        var structRewriter = new TypeScript.Rewriters.TsStructRewriter(context);
        structRewriter.VisitCompilationUnit(compilation);
        // Task 27：ref/out Holder 调用点 TS 化（Ref<T> + refOf/outRef，spec §4.3.1；
        // 参数类型映射在 TsMemberMappingTable.Holder 分支）
        var refOutRewriter = new TypeScript.Rewriters.TsRefOutRewriter(context);
        refOutRewriter.VisitCompilationUnit(compilation);
        var compatSymbols = new HashSet<string>(stringRewriter.CompatSymbols, StringComparer.Ordinal);
        compatSymbols.UnionWith(genericErasureRewriter.CompatSymbols);
        // Task 24：语法层泛型擦除 rewriter 登记的 compat 符号（TypeToken/csDefault）
        compatSymbols.UnionWith(context.TsCompatSymbols);
        return (propertyRewriter.Roles, compatSymbols);
    }
}
