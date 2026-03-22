using System.IO;
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
    /// <summary>Java package for this output file (used by Holder class generation).</summary>
    public string? Package { get; set; }
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
    /// <summary>
    /// A synthetic syntax tree that contributes global using directives to every compilation,
    /// mirroring the default C# project template implicit usings.
    /// This ensures bare identifiers like 'Console' and 'List&lt;T&gt;' resolve to their
    /// fully-qualified types so the semantic type-mapping path fires correctly
    /// (e.g. Console.WriteLine → System.out.println, list.Count → list.size()).
    /// </summary>
    internal static readonly SyntaxTree GlobalUsingsTree = CSharpSyntaxTree.ParseText(
        "global using System;\n" +
        "global using System.Collections.Generic;\n" +
        "global using System.Linq;\n" +
        "global using System.Text;\n" +
        "global using System.Threading.Tasks;\n",
        path: "<global-usings>");

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
                new[] { syntaxTree, GlobalUsingsTree },
                references: new[]
                {
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
                    // Non-generic legacy collections (System.Collections.Queue, Stack, Hashtable, etc.)
                    // are in a separate assembly on .NET Core. Without this, Roslyn resolves bare 'Queue'
                    // to the generic System.Collections.Generic.Queue<T> producing incorrect type mappings.
                    MetadataReference.CreateFromFile(typeof(System.Collections.Queue).Assembly.Location),
                    // System.Console is in its own assembly on .NET Core; without this Roslyn cannot
                    // resolve Console/System.Console and method-name mapping (WriteLine → out.println) fails.
                    MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
                    // System.Linq is in its own assembly on .NET Core. Without this, Roslyn cannot
                    // resolve IEnumerable<T> extension methods (Any, Count, First, etc.) to
                    // System.Linq.Enumerable, so argument-count-aware special-casing of
                    // e.g. Any() → iterator().hasNext() would never trigger.
                    MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
                    // System.Linq.Queryable is in a separate assembly on .NET Core.
                    // Without this, Roslyn cannot resolve IQueryable<T> extension methods
                    // (AsQueryable, Queryable.Where, Queryable.Select, etc.).
                    MetadataReference.CreateFromFile(typeof(System.Linq.Queryable).Assembly.Location),
                    // System.Linq.Expressions is in a separate assembly. Without this, Roslyn
                    // cannot resolve Expression<TDelegate> so the type-strip logic never fires.
                    MetadataReference.CreateFromFile(typeof(System.Linq.Expressions.Expression).Assembly.Location),
                }.Concat(GetFrameworkSupplementalReferences()).ToArray(),
                options: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    // Implicit global usings mirror the default C# project template.
                    // This lets bare identifiers like 'Console' and 'List<T>' resolve
                    // to their fully-qualified types so the semantic mapping path fires
                    // (e.g. Console.WriteLine → System.out.println, list.Count → list.size()).
                    usings: new[]
                    {
                        "System",
                        "System.Collections.Generic",
                        "System.Linq",
                        "System.Text",
                        "System.Threading.Tasks",
                    })
            );
            context.SemanticModel = compilation.GetSemanticModel(syntaxTree);

            // LINQ 预处理：将 LINQ 转换为过程化代码
            // When PreferStreamApi is active, skip procedural rewriting — let the Stream API
            // fallback in InvocationExpressionTransformer handle LINQ chains instead.
            var runLinqRewrite = request.Options.EnableLinqRewrite && !request.Options.EffectivePreferStreamApi;
            if (runLinqRewrite)
            {
                try
                {
                    var rewriter = new LinqRewriter(context.SemanticModel, request.Options);
                    var rewrittenRoot = (CompilationUnitSyntax)rewriter.Visit(syntaxTree.GetRoot());
                    syntaxTree = syntaxTree.WithRootAndOptions(rewrittenRoot, syntaxTree.Options);

                    // Report any LINQ chains that were skipped (fell back to Stream API)
                    foreach (var skipped in rewriter.SkippedLinqChains)
                    {
                        context.Diagnostics.Warning($"LINQ rewrite skipped: {skipped} (will use Stream API fallback)");
                    }

                    // 重新创建编译和语义模型；保留 Options（含隐式 usings）和全局 using 树
                    compilation = CSharpCompilation.Create(
                        "TempAssembly",
                        new[] { syntaxTree, GlobalUsingsTree },
                        compilation.References,
                        compilation.Options
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
        ConversionOptions options,
        IEnumerable<string>? additionalSemanticProjectPaths = null)
    {
        // 查找所有 .cs 文件
        var primaryProjectPath = Path.GetFullPath(projectPath);
        var csFiles = Directory.GetFiles(primaryProjectPath, "*.cs", SearchOption.AllDirectories);

        var allSourceFiles = new HashSet<string>(csFiles, StringComparer.OrdinalIgnoreCase);
        if (additionalSemanticProjectPaths != null)
        {
            foreach (var extraProjectPath in additionalSemanticProjectPaths)
            {
                if (string.IsNullOrWhiteSpace(extraProjectPath))
                {
                    continue;
                }

                var fullExtra = Path.GetFullPath(extraProjectPath);
                if (!Directory.Exists(fullExtra) || string.Equals(fullExtra, primaryProjectPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var extraFile in Directory.GetFiles(fullExtra, "*.cs", SearchOption.AllDirectories))
                {
                    allSourceFiles.Add(extraFile);
                }
            }
        }

        // 创建源文件列表
        var sourceFiles = new List<SourceFile>();
        foreach (var file in allSourceFiles)
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
        var emitFilePaths = new HashSet<string>(csFiles.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        return await pipeline.ConvertProjectAsync(sourceFiles, emitFilePaths);
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

    /// <summary>
    /// Returns supplemental framework references (System.Runtime.dll and related) from the
    /// same directory as System.Private.CoreLib so that type identities unify correctly.
    /// Without System.Runtime.dll, extension methods in System.Linq.dll that reference
    /// IEnumerable&lt;T&gt; from System.Runtime cannot be matched to the CoreLib version, preventing
    /// LINQ extension methods from being resolved semantically.
    /// </summary>
    private static IEnumerable<MetadataReference> GetFrameworkSupplementalReferences()
    {
        var frameworkDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (string.IsNullOrEmpty(frameworkDir))
            yield break;

        var supplemental = new[] { "System.Runtime.dll", "netstandard.dll" };
        foreach (var name in supplemental)
        {
            var path = Path.Combine(frameworkDir, name);
            if (File.Exists(path))
                yield return MetadataReference.CreateFromFile(path);
        }
    }
}
