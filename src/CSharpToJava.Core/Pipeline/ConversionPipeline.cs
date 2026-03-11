using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.LinqRewrite;
using CSharpToJava.Core.Visitors;
using CSharpToJava.Core.PartialType;
using CSharpToJava.TypeMapping;
using DiagSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace CSharpToJava.Core.Pipeline;

/// <summary>
/// 转换请求
/// </summary>
public class ConversionRequest
{
    public string SourceCode { get; set; } = string.Empty;
    public ConversionOptions Options { get; set; } = new();
    public string? FileName { get; set; }
}

/// <summary>
/// 转换结果
/// </summary>
public class ConversionResult
{
    public bool Success { get; set; }
    public string GeneratedCode { get; set; } = string.Empty;
    public IReadOnlyList<DiagnosticMessage> Diagnostics { get; set; } = Array.Empty<DiagnosticMessage>();
    public string? FileName { get; set; }
}

/// <summary>
/// 转换阶段接口
/// </summary>
public interface IConversionPhase
{
    string Name { get; }
    void Execute(ConversionContext context);
}

/// <summary>
/// 主转换管道
/// </summary>
public class ConversionPipeline
{
    private readonly List<IConversionPhase> _phases = new();

    public ConversionPipeline()
    {
        InitializePhases();
    }

    private void InitializePhases()
    {
        _phases.Add(new Phases.ParsingPhase());
        _phases.Add(new Phases.TransformationPhase());
        _phases.Add(new Phases.CodeGenerationPhase());
    }

    /// <summary>
    /// 转换单个文件
    /// </summary>
    public ConversionResult Convert(ConversionRequest request)
    {
        TypeMappingRegistry typeMappings;
        try
        {
            typeMappings = new TypeMappingRegistry(request.Options.TypeMappingConfigPath);
        }
        catch (TypeMapping.TypeMappingConfigurationException ex)
        {
            var errorContext = new ConversionContext(request.Options, new TypeMappingRegistry(new TypeMapping.TypeMappingConfig()));
            errorContext.Diagnostics.Error($"Configuration error: {ex.Message}", null);
            return CreateFailureResult(errorContext, request.FileName);
        }

        var context = new ConversionContext(request.Options, typeMappings);

        try
        {
            // 解析阶段
            var syntaxTree = CSharpSyntaxTree.ParseText(request.SourceCode);
            if (syntaxTree.GetDiagnostics().Any(d => d.Severity == DiagSeverity.Error))
            {
                foreach (var diag in syntaxTree.GetDiagnostics())
                {
                    context.Diagnostics.Error(diag.GetMessage(), diag.Location);
                }
                return CreateFailureResult(context, request.FileName);
            }

            var compilation = CSharpCompilation.Create(
                "TempAssembly",
                new[] { syntaxTree },
                references: new[]
                {
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
                }
            );
            context.SemanticModel = compilation.GetSemanticModel(syntaxTree);

            // LINQ 预处理：将 LINQ 转换为过程化代码
            if (request.Options.EnableLinqRewrite)
            {
                try
                {
                    var rewriter = new LinqRewriter(context.SemanticModel);
                    var rewrittenRoot = (CompilationUnitSyntax)rewriter.Visit(syntaxTree.GetRoot());
                    syntaxTree = syntaxTree.WithRootAndOptions(rewrittenRoot, syntaxTree.Options);

                    // 重新创建编译和语义模型
                    compilation = CSharpCompilation.Create(
                        "TempAssembly",
                        new[] { syntaxTree },
                        compilation.References
                    );
                    context.SemanticModel = compilation.GetSemanticModel(syntaxTree);
                }
                catch (Exception ex)
                {
                    // Log LINQ rewrite failure but continue with original syntax tree
                    context.Diagnostics.Warning($"LINQ rewrite skipped due to error: {ex.Message}");
                }
            }

            // 转换阶段
            var visitor = new CSharpToJavaVisitor(context);
            var compilationUnit = visitor.Visit(syntaxTree.GetRoot());

            if (compilationUnit is Java.JavaCompilationUnit javaCompilation)
            {
                // Files with only attributes produce empty compilation units
                // Treat them as successful but with minimal output
                var code = javaCompilation.ToString("");
                if (string.IsNullOrWhiteSpace(code) && request.FileName != null)
                {
                    // Check if the original file had only attributes/usings (no type declarations)
                    var root = syntaxTree.GetRoot() as CompilationUnitSyntax;
                    if (root != null && root.Members.Count == 0)
                    {
                        return CreateSuccessResult("// " + Path.GetFileName(request.FileName) + " - contains no convertible code", context, request.FileName);
                    }
                }

                return CreateSuccessResult(code, context, request.FileName);
            }

            return CreateFailureResult(context, request.FileName);
        }
        catch (Exception ex)
        {
            context.Diagnostics.Error($"Conversion failed: {ex.Message}");
            return CreateFailureResult(context, request.FileName);
        }
    }

    /// <summary>
    /// 转换项目中的所有文件（独立处理，不合并 partial 类型）
    /// </summary>
    public async Task<List<ConversionResult>> ConvertProjectAsync(string projectPath, ConversionOptions options)
    {
        var results = new List<ConversionResult>();

        // 查找所有 .cs 文件
        var csFiles = Directory.GetFiles(projectPath, "*.cs", SearchOption.AllDirectories);

        foreach (var file in csFiles)
        {
            try
            {
                var sourceCode = await File.ReadAllTextAsync(file);
                var result = Convert(new ConversionRequest
                {
                    SourceCode = sourceCode,
                    Options = options,
                    FileName = file
                });

                results.Add(result);
            }
            catch (Exception ex)
            {
                results.Add(new ConversionResult
                {
                    Success = false,
                    FileName = file,
                    Diagnostics = new List<DiagnosticMessage>
                    {
                        new(Context.DiagnosticSeverity.Error, $"File processing failed: {ex.Message}", null)
                    }
                });
            }
        }

        return results;
    }

    /// <summary>
    /// 转换项目中的所有文件，合并 partial 类型
    /// 使用完整的编译语义模型进行跨文件分析
    /// </summary>
    public async Task<List<ConversionResult>> ConvertProjectWithPartialMergeAsync(
        string projectPath,
        ConversionOptions options)
    {
        // 查找所有 .cs 文件
        var csFiles = Directory.GetFiles(projectPath, "*.cs", SearchOption.AllDirectories);

        // 创建源文件列表
        var sourceFiles = new List<SourceFile>();
        foreach (var file in csFiles)
        {
            try
            {
                var content = await File.ReadAllTextAsync(file);
                sourceFiles.Add(new SourceFile
                {
                    FilePath = file,
                    Content = content
                });
            }
            catch (Exception ex)
            {
                return new List<ConversionResult>
                {
                    new ConversionResult
                    {
                        Success = false,
                        FileName = file,
                        Diagnostics = new List<DiagnosticMessage>
                        {
                            new(Context.DiagnosticSeverity.Error, $"File read failed: {ex.Message}", null)
                        }
                    }
                };
            }
        }

        // 使用 ProjectConversionPipeline 进行转换
        var pipeline = new ProjectConversionPipeline(options);
        return await pipeline.ConvertProjectAsync(sourceFiles);
    }

    private ConversionResult CreateSuccessResult(string code, ConversionContext context, string? fileName)
    {
        return new ConversionResult
        {
            Success = context.Diagnostics.Messages.All(m => m.Severity != Context.DiagnosticSeverity.Error),
            GeneratedCode = code,
            Diagnostics = context.Diagnostics.Messages.ToList(),
            FileName = fileName
        };
    }

    private ConversionResult CreateFailureResult(ConversionContext context, string? fileName)
    {
        return new ConversionResult
        {
            Success = false,
            GeneratedCode = string.Empty,
            Diagnostics = context.Diagnostics.Messages.ToList(),
            FileName = fileName
        };
    }
}
