using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.TypeMapping;
using CSharpToJava.TypeMapping.JavaModel;

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

        // Inject Java standard-library metadata when the path is configured.
        if (!string.IsNullOrEmpty(options.JavaMetadataPath)
            && Directory.Exists(options.JavaMetadataPath))
        {
            _typeMappings.SetJavaLibraryIndex(new JavaLibraryIndex(options.JavaMetadataPath));
        }
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
        return Task.FromResult(ConvertCompilationCore(library, compilation, context, emitFilePaths));
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
            return Task.FromResult(ConvertCompilationCore(library, library.PrimaryCompilation!, context, emitFilePaths));
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
        if (!string.Equals(
                Environment.GetEnvironmentVariable("CS2J_KEEP_DEBUG"),
                "1",
                StringComparison.OrdinalIgnoreCase))
        {
            compilation = StripDebugPreprocessorSymbol(compilation);
        }
        else
        {
            // RegexAssemblyCompiler is debug-only dynamic assembly emission with no Java
            // equivalent (TypeBuilder/ILGenerator are stubs), and RegexCompiler's DEBUG
            // CompileToAssembly references it. Strip DEBUG from those two files. Regex.cs
            // keeps its DEBUG blocks (RegexRunner/Match reference Regex.IsDebug), but its
            // DEBUG CompileToAssembly branch must not call the stripped RegexCompiler
            // method — rewrite the body to throw PlatformNotSupportedException instead.
            compilation = StripDebugPreprocessorSymbol(
                compilation,
                t => t.EndsWith("RegexAssemblyCompiler.cs", StringComparison.OrdinalIgnoreCase)
                    || t.EndsWith("RegexCompiler.cs", StringComparison.OrdinalIgnoreCase));
            compilation = StripDebugCompileToAssemblyCall(compilation);
        }
        var library = Cs2jLibraryFactory.CreateFromCompilation(
            compilation,
            projectName,
            projectFilePath,
            projectReferences,
            isTestProject);
        return ConvertLibraryAsync(library, emitFilePaths);
    }

    /// <summary>
    /// Re-parses all syntax trees without the DEBUG preprocessor symbol so that
    /// #if DEBUG regions (debug-only helpers such as RegexAssemblyCompiler) are
    /// excluded from conversion. Workspace compilations loaded from a Debug
    /// configuration define DEBUG; the converter targets Release semantics
    /// (Debug.Assert stripping follows the same policy). Other symbols (TRACE,
    /// user-defined) are preserved.
    /// </summary>
    internal static CSharpCompilation StripDebugPreprocessorSymbol(CSharpCompilation compilation)
        => StripDebugPreprocessorSymbol(compilation, _ => true);

    internal static CSharpCompilation StripDebugPreprocessorSymbol(
        CSharpCompilation compilation,
        Func<string, bool> shouldStripFile)
    {
        var firstTree = compilation.SyntaxTrees.FirstOrDefault();
        if (firstTree?.Options is not CSharpParseOptions parseOptions
            || !parseOptions.PreprocessorSymbolNames.Contains("DEBUG"))
            return compilation;

        var remaining = parseOptions.PreprocessorSymbolNames
            .Where(s => !string.Equals(s, "DEBUG", StringComparison.Ordinal)).ToList();
        var newTrees = new List<Microsoft.CodeAnalysis.SyntaxTree>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            var options = tree.Options is CSharpParseOptions csharpOptions
                ? csharpOptions.WithPreprocessorSymbols(remaining)
                : CSharpParseOptions.Default.WithPreprocessorSymbols(remaining);
            newTrees.Add(shouldStripFile(tree.FilePath)
                ? CSharpSyntaxTree.ParseText(tree.GetText(), options, tree.FilePath)
                : tree);
        }

        return compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(newTrees.ToArray());
    }

    /// <summary>
    /// Rewrites Regex.CompileToAssembly method bodies to throw PlatformNotSupportedException.
    /// Called when DEBUG regions are kept: the DEBUG branch calls
    /// RegexCompiler.CompileToAssembly, which is stripped together with RegexAssemblyCompiler
    /// (dynamic assembly emission has no Java equivalent).
    /// </summary>
    private static CSharpCompilation StripDebugCompileToAssemblyCall(CSharpCompilation compilation)
    {
        var newTrees = new List<Microsoft.CodeAnalysis.SyntaxTree>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (!tree.FilePath.EndsWith("Regex.cs", StringComparison.OrdinalIgnoreCase))
            {
                newTrees.Add(tree);
                continue;
            }
            var root = tree.GetRoot();
            var rewritten = new CompileToAssemblyThrowingRewriter().Visit(root);
            if (!ReferenceEquals(rewritten, root))
            {
                newTrees.Add(CSharpSyntaxTree.Create(
                    (CSharpSyntaxNode)rewritten!,
                    (CSharpParseOptions)tree.Options,
                    tree.FilePath));
            }
            else
            {
                newTrees.Add(tree);
            }
        }
        return compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(newTrees.ToArray());
    }

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
            new ProjectExtensionMethodCheckPass(),
            new ProjectJavaPatternRewritePass(),
            new ProjectPartialTypeNormalizationPass(),
            new ProjectTypeEmitPass(_irRewriters),
            new ProjectJavaModuleDependencyPass(),
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
        ISet<string>? emitFilePaths)
    {
        // Strip C# `global::` namespace alias qualifier from all syntax trees.
        // Some auto-generated sources (e.g. XSD.exe output) use global:: to fully
        // qualify type names. The qualifier has no meaning in Java.
        compilation = StripGlobalAliasQualifier(compilation);
        // Phase 1: In multi-project mode, RewriteExtensionMethods (instance promotion)
        // is incompatible — it would require back-patching already-emitted projects.
        // Force-disable and emit a warning so callers get a clear signal.
        if (!library.IsSingleProject && context.Options.RewriteExtensionMethods)
        {
            context.Diagnostics.Warning(
                "RewriteExtensionMethods is not supported in multi-project mode and has been disabled. "
                + "Extension methods will be kept as static host methods with static call-site lowering.");
            context.Options.RewriteExtensionMethods = false;
        }

        var passMetrics = new List<Cs2jPassMetric>();
        var passState = new ProjectPassState
        {
            Library = library,
            Context = context,
            Compilation = compilation,
            EmitFilePaths = emitFilePaths,
        };

        // Phase 2: Build extension method index if not already populated.
        // In single-project mode, scan the current compilation.
        // In multi-project mode, the caller (CLI) should pre-populate library.ExtensionMethodIndex.
        if (library.ExtensionMethodIndex.Count == 0)
        {
            var index = new ExtensionMethodIndex();
            foreach (var project in library.Projects)
            {
                ExtensionMethodIndex.ScanCompilation(
                    index,
                    project.Compilation,
                    project.Name,
                    context.NamespaceToPackage);
            }
            library.ExtensionMethodIndex = index;
        }

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

    /// <summary>
    /// Strips the C# `global::` namespace alias qualifier from all syntax trees in a
    /// compilation. Some auto-generated sources (e.g. XSD.exe output) use global:: to
    /// fully qualify type names. The qualifier has no semantic meaning beyond the
    /// global namespace (which is the C# default), and must be removed before Java
    /// code generation to avoid invalid Java output like `global::System.Xml.XmlReader`.
    /// </summary>
    internal static CSharpCompilation StripGlobalAliasQualifier(CSharpCompilation compilation)
    {
        bool changed = false;
        var newTrees = new List<Microsoft.CodeAnalysis.SyntaxTree>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            var text = tree.GetText().ToString();
            if (!text.Contains("global::", StringComparison.Ordinal))
            {
                newTrees.Add(tree);
                continue;
            }
            var stripped = text.Replace("global::", "", StringComparison.Ordinal);
            changed = true;
            newTrees.Add(CSharpSyntaxTree.ParseText(
                stripped,
                tree.Options is CSharpParseOptions opts ? opts : CSharpParseOptions.Default,
                tree.FilePath));
        }
        return changed ? compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(newTrees.ToArray()) : compilation;
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
