using Microsoft.CodeAnalysis.CSharp;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.PartialType;
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
    /// </summary>
    /// <param name="sourceFiles">源代码文件列表</param>
    /// <returns>转换结果列表</returns>
    public async Task<List<ConversionResult>> ConvertProjectAsync(
        IEnumerable<SourceFile> sourceFiles,
        ISet<string>? emitFilePaths = null)
    {
        var context = new ConversionContext(_options, _typeMappings);
        var results = new List<ConversionResult>();

        try
        {
            // Phase 1: Build compilation from all source files.
            var compilation = ProjectCompilationBuilder.BuildCompilation(sourceFiles, context);
            if (compilation == null)
            {
                return TypeGroupResolver.CreateFailureResults(sourceFiles, context);
            }

            // Phase 2: 查找并合并 partial 类型
            var partialMerger = new PartialTypeMerger(context.Diagnostics);
            var mergedTypes = partialMerger.FindAndGroupTypes(compilation);

            // Phase 3: 转换每个类型
            foreach (var typeGroup in mergedTypes)
            {
                if (emitFilePaths != null && emitFilePaths.Count > 0)
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

                    if (candidatePaths.Count == 0)
                    {
                        foreach (var syntaxRef in typeGroup.TypeSymbol.DeclaringSyntaxReferences)
                        {
                            var path = syntaxRef.SyntaxTree.FilePath;
                            if (!string.IsNullOrWhiteSpace(path))
                            {
                                candidatePaths.Add(path);
                            }
                        }
                    }

                    var shouldEmit = candidatePaths.Any(path => emitFilePaths.Contains(Path.GetFullPath(path)));

                    if (!shouldEmit)
                    {
                        continue;
                    }
                }

                var result = TypeGroupResolver.ConvertTypeGroup(typeGroup, compilation, context, _irRewriters);
                if (result != null)
                {
                    results.Add(result);
                }
            }

            // Phase 4: Emit compatibility helper classes when requested.
            if (_options.EmitCompatibilityHelpers)
            {
                var basePackage = CompatibilityClassGenerator.DetermineBasePackage(results);
                if (_options.UseCompatibilityPacks)
                {
                    var packRegistry = CompatibilityPackRegistry.CreateDefault();
                    results.AddRange(packRegistry.GenerateApplicable(results, basePackage));
                }
                else
                {
                    var includeTestContext = CompatibilityClassGenerator.RequiresTestContext(results);
                    results.AddRange(CompatibilityClassGenerator.GenerateCompatibilitySupport(basePackage, includeTestContext));
                }
            }

            // Phase 5: Add cross-package wildcard imports so all MSAGL types see each other
            CrossPackageImportResolver.AddCrossPackageImports(results, _options.SharedCompatibilityPackage);

            // Phase 6: Apply compatibility rewrites for unresolved C#-style API remnants.
            PostGenerationRewriteEngine.ApplyCompatibilityRewrites(results);

            return results;
        }
        catch (Exception ex)
        {
            context.Diagnostics.Error($"Project conversion failed: {ex.Message}");
            return TypeGroupResolver.CreateFailureResults(sourceFiles, context);
        }
    }

    /// <summary>
    /// Converts a project using a pre-built CSharpCompilation (e.g. from MSBuildWorkspace).
    /// Skips the manual compilation step — the provided compilation already has full references.
    /// </summary>
    public async Task<List<ConversionResult>> ConvertProjectAsync(
        CSharpCompilation compilation,
        ISet<string>? emitFilePaths = null)
    {
        var context = new ConversionContext(_options, _typeMappings);
        context.ProjectCompilation = compilation;
        var results = new List<ConversionResult>();

        try
        {
            var partialMerger = new PartialTypeMerger(context.Diagnostics);
            var mergedTypes = partialMerger.FindAndGroupTypes(compilation);

            foreach (var typeGroup in mergedTypes)
            {
                if (emitFilePaths != null && emitFilePaths.Count > 0)
                {
                    var candidatePaths = new List<string>();
                    foreach (var syntaxNode in typeGroup.SyntaxNodes)
                    {
                        var path = syntaxNode.SyntaxTree.FilePath;
                        if (!string.IsNullOrWhiteSpace(path))
                            candidatePaths.Add(path);
                    }
                    foreach (var syntaxRef in typeGroup.TypeSymbol.DeclaringSyntaxReferences)
                    {
                        var path = syntaxRef.SyntaxTree.FilePath;
                        if (!string.IsNullOrWhiteSpace(path))
                            candidatePaths.Add(path);
                    }
                    if (!candidatePaths.Any(path => emitFilePaths.Contains(Path.GetFullPath(path))))
                        continue;
                }

                var result = TypeGroupResolver.ConvertTypeGroup(typeGroup, compilation, context, _irRewriters);
                if (result != null)
                    results.Add(result);
            }

            if (_options.EmitCompatibilityHelpers)
            {
                var basePackage = CompatibilityClassGenerator.DetermineBasePackage(results);
                if (_options.UseCompatibilityPacks)
                {
                    var packRegistry = CompatibilityPackRegistry.CreateDefault();
                    results.AddRange(packRegistry.GenerateApplicable(results, basePackage));
                }
                else
                {
                    var includeTestContext = CompatibilityClassGenerator.RequiresTestContext(results);
                    results.AddRange(CompatibilityClassGenerator.GenerateCompatibilitySupport(basePackage, includeTestContext));
                }
            }

            CrossPackageImportResolver.AddCrossPackageImports(results, _options.SharedCompatibilityPackage);
            PostGenerationRewriteEngine.ApplyCompatibilityRewrites(results);

            return results;
        }
        catch (Exception ex)
        {
            context.Diagnostics.Error($"Project conversion failed: {ex.Message}");
            return results;
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
