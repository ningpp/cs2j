using Microsoft.CodeAnalysis.CSharp;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline.Compatibility;
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
    {
        ArgumentNullException.ThrowIfNull(library);

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

        context.ProjectCompilation = compilation;
        return Task.FromResult(ConvertCompilationCore(library, compilation, context, emitFilePaths));
    }

    /// <summary>
    /// </summary>
    /// <param name="sourceFiles">源代码文件列表</param>
    /// <returns>转换结果列表</returns>
    public Task<List<ConversionResult>> ConvertProjectAsync(
        IEnumerable<SourceFile> sourceFiles,
        ISet<string>? emitFilePaths = null)
    {
        var context = new ConversionContext(_options, _typeMappings);
        var sourceFileList = sourceFiles.ToList();

        try
        {
            // Phase 1: Build compilation from all source files.
            var compilation = ProjectCompilationBuilder.BuildCompilation(sourceFileList, context);
            if (compilation == null)
            {
                return Task.FromResult(TypeGroupResolver.CreateFailureResults(sourceFileList, context));
            }

            var library = Cs2jLibraryFactory.CreateFromSourceFiles(sourceFileList, compilation);
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
        ISet<string>? emitFilePaths = null)
    {
        var library = Cs2jLibraryFactory.CreateFromCompilation(compilation);
        return ConvertLibraryAsync(library, emitFilePaths);
    }

    private IReadOnlyList<ICs2jPass<ProjectPassState>> CreateProjectPasses()
    {
        return new ICs2jPass<ProjectPassState>[]
        {
            new ProjectLinqDesugarPass(),
            new ProjectCompilationCheckPass(),
            new ProjectUnsupportedDomainCheckPass(),
            new ProjectPartialTypeNormalizationPass(),
            new ProjectTypeEmitPass(_irRewriters),
            new ProjectCompatibilityEmitPass(),
            new ProjectCrossPackageImportEmitPass(),
            new ProjectPostGenerationRewriteEmitPass(),
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

    // Forwarding method for backward compatibility (tests use reflection to access this)
    private static void ApplyCompatibilityRewrites(List<ConversionResult> results)
        => PostGenerationRewriteEngine.ApplyCompatibilityRewrites(results);

    public static string ApplyCompatibilityRewritesForTesting(string fileName, string generatedCode)
        => PostGenerationRewriteEngine.ApplyCompatibilityRewritesForTesting(fileName, generatedCode);


    public static List<ConversionResult> GenerateCompatibilitySupport(string compatibilityPackage, bool includeTestContext)
        => CompatibilityClassGenerator.GenerateCompatibilitySupport(compatibilityPackage, includeTestContext);
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
