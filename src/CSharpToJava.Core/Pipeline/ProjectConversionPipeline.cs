using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.TypeMapping;

namespace CSharpToJava.Core.Pipeline;

/// <summary>
/// 项目级别的转换管道 - 支持合并 partial 类型
/// 使用完整的编译语义模型进行跨文件分析
/// </summary>
public class ProjectConversionPipeline
{
    private readonly ConversionOptions _options;
    private readonly TypeMappingRegistry _typeMappings;
    private readonly List<Java.JavaSyntaxRewriter> _irRewriters = new();

    public IReadOnlyList<Cs2jPassMetric> LastPassMetrics { get; private set; } = Array.Empty<Cs2jPassMetric>();

    /// <summary>
    /// 创建项目转换管道
    /// <exception cref="TypeMappingConfigurationException">配置文件不存在或格式错误</exception>
    public ProjectConversionPipeline(ConversionOptions options)
    {
        _options = options;
        _typeMappings = new TypeMappingRegistry(options.TypeMappingConfigPath);
    }

    /// <summary>
    /// 注册 IR 层后处理重写器。
    /// </summary>
    public ProjectConversionPipeline AddIRRewriter(Java.JavaSyntaxRewriter rewriter)
    {
        _irRewriters.Add(rewriter);
        return this;
    }

    /// <summary>
    /// Converts a normalized Phase 1 library input.
    /// Current implementation assumes a single primary compilation per library.
    /// </summary>
    public Task<List<ConversionResult>> ConvertLibraryAsync(
        Cs2jLibrary library,
        ISet<string>? emitFilePaths = null)
        => ConvertLibraryAsync(library, emitFilePaths, NormalizationPlan.Default);

    private Task<List<ConversionResult>> ConvertLibraryAsync(
        Cs2jLibrary library,
        ISet<string>? emitFilePaths,
        NormalizationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(library);

        LastPassMetrics = Array.Empty<Cs2jPassMetric>();

        var context = new ConversionContext(_options, _typeMappings);
        var compilation = library.PrimaryCompilation;
        if (compilation == null)
        {
            context.Diagnostics.Error($"Library '{library.Name}' does not contain a primary compilation.");
            return Task.FromResult(new List<ConversionResult>
            {
                new()
                {
                    Success = false,
                    FileName = library.Name,
                    Diagnostics = context.Diagnostics.Messages.ToList(),
                }
            });
        }

        context.CurrentProjectName = library.Projects.FirstOrDefault()?.Name ?? library.Name;
        context.ProjectCompilation = compilation;
        return Task.FromResult(ConvertCompilationCore(library, compilation, context, emitFilePaths, plan));
    }

    /// <summary>
    /// </summary>
    /// <param name="sourceFiles">源代码文件列表</param>
    /// <returns>转换结果列表</returns>
    public Task<List<ConversionResult>> ConvertProjectAsync(
        IEnumerable<SourceFile> sourceFiles,
        ISet<string>? emitFilePaths = null,
        string? projectName = null,
        string? projectFilePath = null,
        IReadOnlyList<string>? projectReferences = null,
        bool isTestProject = false)
    {
        LastPassMetrics = Array.Empty<Cs2jPassMetric>();

        var context = new ConversionContext(_options, _typeMappings);
        var sourceFileList = sourceFiles.ToList();
        context.CurrentProjectName = projectName;

        try
        {
            // Phase 1: Build compilation from all source files.
            var compilation = ProjectCompilationBuilder.BuildCompilation(sourceFileList, context);
            if (compilation == null)
            {
                return Task.FromResult(TypeGroupResolver.CreateFailureResults(sourceFileList, context));
            }

            var library = Cs2jLibraryFactory.CreateFromSourceFiles(
                sourceFileList,
                compilation,
                projectName: projectName,
                projectFilePath: projectFilePath,
                projectReferences: projectReferences,
                isTestProject: isTestProject);
            return Task.FromResult(ConvertCompilationCore(
                library, library.PrimaryCompilation!, context, emitFilePaths, NormalizationPlan.Default));
        }
        catch (Exception ex)
        {
            context.Diagnostics.Error($"Project conversion failed: {ex.Message}");
            return Task.FromResult(TypeGroupResolver.CreateFailureResults(sourceFileList, context));
        }
    }

    /// <summary>
    /// Converts a project using a pre-built CSharpCompilation (e.g. from MSBuildWorkspace).
    /// Skips the manual compilation step — the provided compilation already has full references.
    /// </summary>
    public Task<List<ConversionResult>> ConvertProjectAsync(
        CSharpCompilation compilation,
        ISet<string>? emitFilePaths = null,
        string? projectName = null,
        string? projectFilePath = null,
        IReadOnlyList<string>? projectReferences = null,
        bool isTestProject = false)
    {
        // Strip #if DEBUG regions by default (Release semantics). Set CS2J_KEEP_DEBUG=1
        // to convert them instead — required when the source's tests depend on debug-only
        // members such as RegexCode.ToString() (RegexReductionTests compares it).
        // The actual rewrites all happen in the single NormalizeCompilation step;
        // this entry point only selects the plan.
        var plan = ComputeNormalizationPlan();
        var library = Cs2jLibraryFactory.CreateFromCompilation(
            compilation,
            projectName,
            projectFilePath,
            projectReferences,
            isTestProject);
        return ConvertLibraryAsync(library, emitFilePaths, plan);
    }

    private enum DebugStripMode
    {
        None,
        AllFiles,
        RegexFilesOnly,
    }

    private sealed record NormalizationPlan(DebugStripMode DebugStrip, bool RewriteCompileToAssembly)
    {
        public static readonly NormalizationPlan Default = new(DebugStripMode.None, false);
    }

    private static NormalizationPlan ComputeNormalizationPlan()
    {
        // Release semantics by default: #if DEBUG regions (debug-only helpers such
        // as RegexAssemblyCompiler) are excluded from conversion.
        if (!string.Equals(
                Environment.GetEnvironmentVariable("CS2J_KEEP_DEBUG"),
                "1",
                StringComparison.OrdinalIgnoreCase))
        {
            return new NormalizationPlan(DebugStripMode.AllFiles, false);
        }

        // RegexAssemblyCompiler is debug-only dynamic assembly emission with no Java
        // equivalent (TypeBuilder/ILGenerator are stubs), and RegexCompiler's DEBUG
        // CompileToAssembly references it. Strip DEBUG from those two files only;
        // Regex.cs keeps its DEBUG blocks, but its CompileToAssembly branch must be
        // rewritten to throw instead of calling the stripped RegexCompiler method.
        return new NormalizationPlan(DebugStripMode.RegexFilesOnly, true);
    }

    /// <summary>
    /// Applies every C# syntax-tree rewrite in one pass: DEBUG symbol removal,
    /// global:: alias stripping, Regex.CompileToAssembly body rewrite and the
    /// generic declaration-pattern rewrite. Each tree is parsed at most once and
    /// the compilation is rebuilt once, so Roslyn's semantic-model cache is not
    /// invalidated several times before the pass pipeline starts.
    /// </summary>
    private static (CSharpCompilation Compilation, Cs2jLibrary Library) NormalizeCompilation(
        CSharpCompilation compilation,
        Cs2jLibrary library,
        NormalizationPlan plan)
    {
        var trees = compilation.SyntaxTrees.ToList();
        if (trees.Count == 0)
            return (compilation, library);

        var firstOptions = trees[0].Options as CSharpParseOptions;
        var stripDebug = plan.DebugStrip != DebugStripMode.None
            && firstOptions?.PreprocessorSymbolNames.Contains("DEBUG") == true;
        var remainingSymbols = stripDebug
            ? firstOptions!.PreprocessorSymbolNames
                .Where(symbol => !string.Equals(symbol, "DEBUG", StringComparison.Ordinal))
                .ToList()
            : null;

        var newTrees = new Microsoft.CodeAnalysis.SyntaxTree[trees.Count];
        var changed = false;
        for (var index = 0; index < trees.Count; index++)
        {
            var tree = trees[index];
            var options = tree.Options as CSharpParseOptions ?? CSharpParseOptions.Default;
            var sourceText = tree.GetText();
            var text = sourceText.ToString();
            var hasGlobalAlias = text.Contains("global::", StringComparison.Ordinal);
            var stripDebugHere = stripDebug && (plan.DebugStrip switch
            {
                DebugStripMode.AllFiles => true,
                DebugStripMode.RegexFilesOnly => IsRegexCompilerFile(tree.FilePath),
                _ => false,
            });
            Microsoft.CodeAnalysis.SyntaxTree current = tree;

            if (stripDebugHere || hasGlobalAlias)
            {
                var targetOptions = stripDebugHere
                    ? options.WithPreprocessorSymbols(remainingSymbols!)
                    : options;
                current = hasGlobalAlias
                    ? CSharpSyntaxTree.ParseText(
                        text.Replace("global::", "", StringComparison.Ordinal),
                        targetOptions,
                        tree.FilePath)
                    : CSharpSyntaxTree.ParseText(sourceText, targetOptions, tree.FilePath);
                changed = true;
            }

            if (plan.RewriteCompileToAssembly
                && tree.FilePath.EndsWith("Regex.cs", StringComparison.OrdinalIgnoreCase))
            {
                var root = current.GetRoot();
                var rewritten = new CompileToAssemblyThrowingRewriter().Visit(root);
                if (rewritten is not null && !ReferenceEquals(rewritten, root))
                {
                    current = CSharpSyntaxTree.Create(
                        (CSharpSyntaxNode)rewritten,
                        (CSharpParseOptions)current.Options,
                        tree.FilePath);
                    changed = true;
                }
            }

            if (!ProjectPassState.IsSyntheticFilePath(tree.FilePath))
            {
                var root = current.GetRoot();
                var rewritten = new CSharpToJava.Core.Rewriters.JavaGenericPatternRewriter().Visit(root);
                if (rewritten is CompilationUnitSyntax unit && !ReferenceEquals(unit, root))
                {
                    current = current.WithRootAndOptions(unit, current.Options);
                    changed = true;
                }
            }

            newTrees[index] = current;
        }

        if (!changed)
            return (compilation, library);

        var normalized = compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(newTrees);
        var primary = library.Projects.FirstOrDefault();
        var rebuiltLibrary = Cs2jLibraryFactory.CreateFromCompilation(
            normalized,
            projectName: primary?.Name,
            projectFilePath: primary?.ProjectFilePath,
            projectReferences: primary?.ProjectReferences?.ToList(),
            isTestProject: primary?.IsTestProject ?? false,
            libraryName: library.Name);
        return (normalized, rebuiltLibrary);
    }

    private static bool IsRegexCompilerFile(string path)
        => path.EndsWith("RegexAssemblyCompiler.cs", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("RegexCompiler.cs", StringComparison.OrdinalIgnoreCase);

    private sealed class CompileToAssemblyThrowingRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            if (node.Identifier.Text == "CompileToAssembly" && node.Body != null)
            {
                var throwStatement = SyntaxFactory.ThrowStatement(
                    SyntaxFactory.ObjectCreationExpression(
                        SyntaxFactory.ParseTypeName("PlatformNotSupportedException"))
                    .WithArgumentList(SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(
                                SyntaxFactory.MemberAccessExpression(
                                    SyntaxKind.SimpleMemberAccessExpression,
                                    SyntaxFactory.IdentifierName("SR"),
                                    SyntaxFactory.IdentifierName("PlatformNotSupported_CompileToAssembly")))))));
                return node.WithBody(
                    SyntaxFactory.Block(SyntaxFactory.SingletonList<StatementSyntax>(throwStatement)));
            }
            return base.VisitMethodDeclaration(node);
        }
    }

    // C26 M4-c / 设计 §5.4：项目路径不再内嵌 LINQ 过程化改写 pass
    // （原 ProjectLinqDesugarPass 已摘除，LINQ 改写统一走 linq-channel
    // 预处理通道），故此处不再登记 LINQ pass、也无 LastLinqStatistics；
    // 单文件路径的统计仍由 ConversionResult.LinqStatistics 暴露。
    private IReadOnlyList<ICs2jPass<ProjectPassState>> CreateProjectPasses()
    {
        return new ICs2jPass<ProjectPassState>[]
        {
            new ProjectCompilationCheckPass(),
            new ProjectUnsupportedDomainCheckPass(),
            new ProjectPlatformBoundaryCheckPass(),
            new ProjectNativeInteropCheckPass(),
            new ProjectPartialTypeNormalizationPass(),
            new ProjectTypeEmitPass(_irRewriters),
        };
    }

    private static void AttachPassMetrics(IEnumerable<ConversionResult> results, IReadOnlyList<Cs2jPassMetric> passMetrics)
    {
        var metrics = passMetrics.ToList();
        foreach (var result in results)
        {
            result.PassMetrics = metrics;
        }
    }

    private List<ConversionResult> ConvertCompilationCore(
        Cs2jLibrary library,
        CSharpCompilation compilation,
        ConversionContext context,
        ISet<string>? emitFilePaths,
        NormalizationPlan plan)
    {
        // One syntax-tree rewrite pass replaces the former DEBUG strip,
        // global:: strip, CompileToAssembly rewrite and pattern-rewrite pass.
        (compilation, library) = NormalizeCompilation(compilation, library, plan);
        context.ProjectCompilation = compilation;

        var passMetrics = new List<Cs2jPassMetric>();
        var passState = new ProjectPassState
        {
            Library = library,
            Context = context,
            Compilation = compilation,
            EmitFilePaths = emitFilePaths,
        };

        try
        {
            Cs2jPassExecutor.Execute(passState, context, CreateProjectPasses(), passMetrics);
            LastPassMetrics = passMetrics.ToList();
            if (passState.Results.Count == 0 && context.Diagnostics.Messages.Any(m => m.Severity == Context.DiagnosticSeverity.Error))
            {
                passState.Results.Add(new ConversionResult
                {
                    Success = false,
                    FileName = library.Name,
                    Diagnostics = context.Diagnostics.Messages.ToList(),
                });
            }

            AttachPassMetrics(passState.Results, passMetrics);
            return passState.Results;
        }
        catch (Exception ex)
        {
            LastPassMetrics = passMetrics.ToList();
            context.Diagnostics.Error($"Project conversion failed: {ex.Message}");
            if (passState.Results.Count == 0)
            {
                passState.Results.Add(new ConversionResult
                {
                    Success = false,
                    FileName = library.Name,
                    Diagnostics = context.Diagnostics.Messages.ToList(),
                });
            }

            AttachPassMetrics(passState.Results, passMetrics);
            return passState.Results;
        }
    }

}

/// <summary>
/// 源代码文件
/// </summary>
public class SourceFile
{
    public required string FilePath { get; init; }
    public required string Content { get; init; }

    public static async Task<SourceFile> FromPath(string filePath)
    {
        var content = await File.ReadAllTextAsync(filePath);
        return new SourceFile
        {
            FilePath = filePath,
            Content = content
        };
    }
}
