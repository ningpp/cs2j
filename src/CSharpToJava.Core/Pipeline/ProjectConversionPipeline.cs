using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.LinqRewrite;
using CSharpToJava.Core.PartialType;
using CSharpToJava.Core.Transformers.Type;
using CSharpToJava.Core.Visitors;
using CSharpToJava.TypeMapping;
using DiagSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace CSharpToJava.Core.Pipeline;

/// <summary>
/// 项目级别的转换管道 - 支持合并 partial 类型
/// 使用完整的编译语义模型进行跨文件分析
/// </summary>
public class ProjectConversionPipeline
{
    private readonly ConversionOptions _options;
    private readonly TypeMappingRegistry _typeMappings;

    /// <summary>
    /// 创建项目转换管道
    /// </summary>
    /// <exception cref="TypeMappingConfigurationException">配置文件不存在或格式错误</exception>
    public ProjectConversionPipeline(ConversionOptions options)
    {
        _options = options;
        _typeMappings = new TypeMappingRegistry(options.TypeMappingConfigPath);
    }

    /// <summary>
    /// 转换整个项目，合并 partial 类型
    /// </summary>
    /// <param name="sourceFiles">源代码文件列表</param>
    /// <returns>转换结果列表</returns>
    public async Task<List<ConversionResult>> ConvertProjectAsync(
        IEnumerable<SourceFile> sourceFiles)
    {
        var context = new ConversionContext(_options, _typeMappings);
        var results = new List<ConversionResult>();

        try
        {
            // Phase 1: 构建完整的编译
            var compilation = BuildCompilation(sourceFiles, context);
            if (compilation == null)
            {
                context.Diagnostics.Error("Failed to build compilation");
                return CreateFailureResults(sourceFiles, context);
            }

            // Phase 2: 查找并合并 partial 类型
            var partialMerger = new PartialTypeMerger(context.Diagnostics);
            var mergedTypes = partialMerger.FindAndGroupTypes(compilation);

            // Phase 3: 转换每个类型
            foreach (var typeGroup in mergedTypes)
            {
                var result = ConvertTypeGroup(typeGroup, compilation, context);
                if (result != null)
                {
                    results.Add(result);
                }
            }

            return results;
        }
        catch (Exception ex)
        {
            context.Diagnostics.Error($"Project conversion failed: {ex.Message}");
            return CreateFailureResults(sourceFiles, context);
        }
    }

    /// <summary>
    /// 从源代码文件构建 CSharpCompilation
    /// </summary>
    private CSharpCompilation? BuildCompilation(
        IEnumerable<SourceFile> sourceFiles,
        ConversionContext context)
    {
        var syntaxTrees = new List<SyntaxTree>();
        var sourceFileList = sourceFiles.ToList();

        // Parse all source files
        foreach (var sourceFile in sourceFileList)
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(
                sourceFile.Content,
                CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest),
                sourceFile.FilePath);

            // Check for parse errors
            var diagnostics = syntaxTree.GetDiagnostics();
            foreach (var diagnostic in diagnostics)
            {
                if (diagnostic.Severity == DiagSeverity.Error)
                {
                    context.Diagnostics.Error(
                        diagnostic.GetMessage(),
                        diagnostic.Location);
                }
            }

            syntaxTrees.Add(syntaxTree);
        }

        if (context.Diagnostics.Messages.Any(m => m.Severity == Context.DiagnosticSeverity.Error))
        {
            return null;
        }

        // Create compilation with all syntax trees and references
        var compilation = CSharpCompilation.Create(
            "TempAssembly",
            syntaxTrees,
            GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // Store in context for cross-file semantic analysis
        context.ProjectCompilation = compilation;

        return compilation;
    }

    /// <summary>
    /// 获取元数据引用（系统程序集）
    /// </summary>
    private static List<MetadataReference> GetMetadataReferences()
    {
        var references = new List<MetadataReference>();
        var objectAssembly = typeof(object).Assembly.Location;
        var listAssembly = typeof(System.Collections.Generic.List<>).Assembly.Location;

        if (!string.IsNullOrEmpty(objectAssembly) && File.Exists(objectAssembly))
        {
            references.Add(MetadataReference.CreateFromFile(objectAssembly));
        }

        if (!string.IsNullOrEmpty(listAssembly) && File.Exists(listAssembly))
        {
            references.Add(MetadataReference.CreateFromFile(listAssembly));
        }

        // Add common .NET assemblies
        var dotnetAssemblies = new[]
        {
            "System.Runtime.dll",
            "System.Collections.dll",
            "System.Linq.dll",
            "System.Threading.Tasks.dll",
            "netstandard.dll",
        };

        var frameworkDir = Path.GetDirectoryName(objectAssembly);
        if (!string.IsNullOrEmpty(frameworkDir))
        {
            foreach (var assembly in dotnetAssemblies)
            {
                var path = Path.Combine(frameworkDir, assembly);
                if (File.Exists(path))
                {
                    references.Add(MetadataReference.CreateFromFile(path));
                }
            }
        }

        return references;
    }

    /// <summary>
    /// 转换单个类型组（可能是合并后的 partial 类型）
    /// </summary>
    private ConversionResult? ConvertTypeGroup(
        PartialTypeGroup typeGroup,
        CSharpCompilation compilation,
        ConversionContext context)
    {
        try
        {
            // Check if we have any syntax nodes
            if (typeGroup.SyntaxNodes.Count == 0)
            {
                context.Diagnostics.Error($"Type '{typeGroup.TypeSymbol.Name}' has no syntax nodes");
                return new ConversionResult
                {
                    Success = false,
                    FileName = $"{typeGroup.TypeSymbol.Name}.java",
                    Diagnostics = context.Diagnostics.Messages.ToList()
                };
            }

            // Get semantic model for the first syntax tree
            var semanticModel = compilation.GetSemanticModel(typeGroup.SyntaxNodes[0].SyntaxTree);

            // Create merged declaration
            var mergedDeclaration = MergedTypeDeclaration.FromPartialTypeGroup(typeGroup, semanticModel);

            // Register merged type in context
            context.RegisterMergedPartialType(mergedDeclaration);

            // Use ClassTransformer to convert
            var transformer = new ClassTransformer();
            var javaType = transformer.TransformMerged(mergedDeclaration, context);

            // Generate Java code
            if (javaType is Java.JavaClassDeclaration javaClass)
            {
                return new ConversionResult
                {
                    Success = context.Diagnostics.Messages.All(m => m.Severity != Context.DiagnosticSeverity.Error),
                    GeneratedCode = javaClass.ToString(""),
                    Diagnostics = context.Diagnostics.Messages.ToList(),
                    FileName = mergedDeclaration.OutputFileName
                };
            }

            return null;
        }
        catch (Exception ex)
        {
            context.Diagnostics.Error($"Failed to convert type '{typeGroup.TypeSymbol.Name}': {ex.Message}");
            return new ConversionResult
            {
                Success = false,
                FileName = $"{typeGroup.TypeSymbol.Name}.java",
                Diagnostics = context.Diagnostics.Messages.ToList()
            };
        }
    }

    /// <summary>
    /// 创建失败结果
    /// </summary>
    private static List<ConversionResult> CreateFailureResults(
        IEnumerable<SourceFile> sourceFiles,
        ConversionContext context)
    {
        return sourceFiles.Select(file => new ConversionResult
        {
            Success = false,
            FileName = file.FilePath,
            Diagnostics = context.Diagnostics.Messages.ToList()
        }).ToList();
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
