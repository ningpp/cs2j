using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Analysis;
using CSharpToJava.Core.ClosedMetadata;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.LinqRewrite;
using CSharpToJava.Core.PartialType;

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
        if (IsSyntheticFilePath(filePath))
        {
            return filePath;
        }

        return Path.GetFullPath(filePath);
    }

    /// <summary>
    /// Roslyn synthetic syntax trees (annotations and compiler-injected helpers) carry a
    /// "&lt;...&gt;" placeholder path: they have no file on disk, must stay out of file-based
    /// filters and must never be run through path resolution.
    /// </summary>
    internal static bool IsSyntheticFilePath(string filePath)
    {
        return string.IsNullOrWhiteSpace(filePath) || filePath.StartsWith("<", StringComparison.Ordinal);
    }
}

internal sealed record ProjectSyntaxTreeDiagnosticsResult(string FilePath, IReadOnlyList<DiagnosticMessage> Diagnostics);

// C26 M4-c / 设计 §5.4：项目路径嵌入式 LINQ 改写 pass（ProjectLinqDesugarPass）
// 已摘除——LINQ 过程化改写统一由 linq-channel 预处理通道承担（CLI convert-project
// 默认链），下游转换不再内嵌两阶段 desugar+rewrite。单文件路径仍由
// SingleFileLinqDesugarPass 保留 E16（设计 §5.5/Q4）。本 helper 从原 pass 保留：
// 其它改写 C# 中间树的项目 pass（如 ProjectJavaPatternRewritePass）在整体替换
// SyntaxTree 后仍需用同一逻辑重建 Library/TypeGroups/ProjectCompilation。
internal static class ProjectStateRebuildHelper
{
    internal static void RebuildAfterCompilationChange(ProjectPassState state)
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
            .Where(syntaxTree => !ProjectPassState.IsSyntheticFilePath(syntaxTree.FilePath))
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
            .Where(syntaxTree => !ProjectPassState.IsSyntheticFilePath(syntaxTree.FilePath))
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
        var syntaxTrees = state.Compilation.SyntaxTrees
            .Where(syntaxTree => !ProjectPassState.IsSyntheticFilePath(syntaxTree.FilePath))
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
            .Where(tree => !ProjectPassState.IsSyntheticFilePath(tree.FilePath))
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
        ProjectStateRebuildHelper.RebuildAfterCompilationChange(state);
    }
}

public sealed class ProjectTypeEmitPass : ICs2jPass<ProjectPassState>
{
    /// <summary>
    /// The make-readonly preprocessing injects Cs2jTestUiCultureShim.cs to pin the C# test-run
    /// thread culture (a .NET Framework concern). Its types extend xunit framework classes
    /// (XunitTestFrameworkExecutor &amp; co.) from the NuGet package, which are never converted —
    /// emitting them would produce unresolvable base classes. The shim has no Java equivalent.
    /// </summary>
    private const string TestUiCultureShimFileName = "Cs2jTestUiCultureShim.cs";

    /// <summary>
    /// C30 generates <c>_Cs2jClosedTypes.&lt;Project&gt;.cs</c> registration files. Until the
    /// C30 M4 consumer linker rewrites typeof sites to consume the registry, the generated
    /// bootstrap class is dead code on the Java side, and its Dictionary/ContainsKey/Add body
    /// does not survive the project text visitor (java.util.Dictionary is abstract). Skip its
    /// Java emission; the file stays in the C# intermediate tree and its residual gate intact.
    /// </summary>
    private const string ClosedTypesRegistrationFilePrefix =
        ClosedMetadataRegistrationGenerator.GeneratedFileStem + ".";

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

        // 擦除冲突改名项目级预注册：类按文件序转换，跨类调用点可能先于声明类
        // 处理（真实链 Core/XmlWriter.cs → XPathNavigator.Clone 改名 clone_2）。
        // 必须在渲染任何成员体之前让全部改名映射可见（幂等，类内随后还会再跑）。
        foreach (var preScanGroup in state.TypeGroups)
            Transformers.Type.ClassTransformer.PreRegisterErasureConflictRenamesProjectWide(
                preScanGroup, state.Context);

        // Build effective IR rewriters: user-registered + built-in validation rewriters
        var effectiveRewriters = BuildEffectiveRewriters(state);

        foreach (var typeGroup in state.TypeGroups)
        {
            // C06 R8 FFM opt-in: the InteropFacadeAttribute support type is injected
            // into the C# intermediate tree purely as a marker for MethodTransformer.
            // It must not be converted to a Java class (its method-bearing user type
            // still reads the attribute via GetAttributes during member conversion).
            if (typeGroup.TypeSymbol.ToDisplayString() == InteropBoundaryFacts.InteropFacadeMarker)
            {
                continue;
            }

            var candidatePaths = CollectCandidatePaths(typeGroup);
            if (candidatePaths.Any(path => state.BlockedFilePaths.Contains(ProjectPassState.NormalizeFilePath(path))))
            {
                continue;
            }

            // The culture shim has no Java equivalent — see TestUiCultureShimFileName.
            if (candidatePaths.Any(path =>
                    path.EndsWith(TestUiCultureShimFileName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // C30 M3 generated registration file — Java consumption lands with M4.
            if (candidatePaths.Any(path =>
                    Path.GetFileName(path).StartsWith(
                        ClosedTypesRegistrationFilePrefix, StringComparison.OrdinalIgnoreCase)))
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
        rewriters.Add(new Java.Rewriters.LineContinuationTrimRewriter());
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
}
