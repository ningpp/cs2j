using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java.Rewriters;
using CSharpToJava.Core.LinqRewrite;
using CSharpToJava.Core.Visitors;
using CSharpToJava.TypeMapping;
using CSharpToJava.TypeMapping.JavaModel;
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
    public IReadOnlyList<Cs2jPassMetric> PassMetrics { get; set; } = Array.Empty<Cs2jPassMetric>();
    public LinqRewrite.LinqRewriteStatistics? LinqStatistics { get; set; }
    public string? FileName { get; set; }
    /// <summary>Java package for this output file (used by Holder class generation).</summary>
    public string? Package { get; set; }
    /// <summary>
    /// Java platform module names required by this output file (e.g. "java.base", "java.sql").
    /// Populated by the module dependency analysis pass when <c>JavaMetadataPath</c> is configured.
    /// </summary>
    public IReadOnlySet<string>? JavaModuleDependencies { get; set; }

    /// <summary>
    /// The Java IR compilation unit for this output file.  Preserved after the emit pass so
    /// that downstream passes (e.g. cross-package import resolution, module dependency analysis)
    /// can operate on structured IR nodes rather than re-parsing generated code strings.
    /// <para>May be <c>null</c> for compatibility-generated files or failed conversions.</para>
    /// </summary>
    public Java.JavaCompilationUnit? Compilation { get; set; }

    /// <summary>
    /// Regenerates <see cref="GeneratedCode"/> from the preserved <see cref="Compilation"/> IR.
    /// Call this after modifying the IR in post-emit passes to keep the string in sync.
    /// No-op if <see cref="Compilation"/> is <c>null</c>.
    /// </summary>
    public void SyncGeneratedCodeFromIR()
    {
        if (Compilation is not null)
            GeneratedCode = Compilation.ToString("");
    }
}

/// <summary>
/// 主转换管道
/// </summary>
public class ConversionPipeline
{
    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".vs",
        ".idea",
        "bin",
        "obj",
        "node_modules",
    };

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

    private readonly List<Java.JavaSyntaxRewriter> _irRewriters = new();

    public IReadOnlyList<Cs2jPassMetric> LastProjectPassMetrics { get; private set; } = Array.Empty<Cs2jPassMetric>();
    public LinqRewrite.LinqRewriteStatistics? LastLinqStatistics { get; private set; }

    public ConversionPipeline()
    {
    }

    /// <summary>
    /// 注册 IR 层后处理重写器。在 ToString 生成代码之前对结构化 Java IR 进行变换。
    /// </summary>
    public ConversionPipeline AddIRRewriter(Java.JavaSyntaxRewriter rewriter)
    {
        _irRewriters.Add(rewriter);
        return this;
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

        // Inject Java standard-library metadata when the path is configured.
        if (!string.IsNullOrEmpty(request.Options.JavaMetadataPath)
            && Directory.Exists(request.Options.JavaMetadataPath))
        {
            typeMappings.SetJavaLibraryIndex(new JavaLibraryIndex(request.Options.JavaMetadataPath));
        }

        var context = new ConversionContext(request.Options, typeMappings);
        var passMetrics = new List<Cs2jPassMetric>();

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
                return CreateFailureResult(context, request.FileName, passMetrics);
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
            var library = Cs2jLibraryFactory.CreateSingleFile(request.SourceCode, request.FileName, syntaxTree, compilation);
            context.SemanticModel = library.PrimaryCompilation?.GetSemanticModel(syntaxTree) ?? compilation.GetSemanticModel(syntaxTree);
            var passState = new SingleFilePassState
            {
                Request = request,
                Context = context,
                SyntaxTree = syntaxTree,
                Compilation = compilation,
                Library = library,
            };

            Cs2jPassExecutor.Execute(passState, context, CreateSingleFilePasses(), passMetrics);

            if (passState.EmitSucceeded)
            {
                var result = CreateSuccessResult(passState.GeneratedCode, context, request.FileName, passMetrics);
                result.LinqStatistics = passState.LinqStatistics;
                return result;
            }

            return CreateFailureResult(context, request.FileName, passMetrics);
        }
        catch (Exception ex)
        {
            context.Diagnostics.Error($"Conversion failed: {ex.Message}");
            return CreateFailureResult(context, request.FileName, passMetrics);
        }
    }

    /// <summary>
    /// 转换项目中的所有文件（独立处理，不合并 partial 类型）
    /// </summary>
    public async Task<List<ConversionResult>> ConvertProjectAsync(string projectPath, ConversionOptions options)
    {
        var results = new List<ConversionResult>();

        // 查找所有 .cs 文件
        var csFiles = EnumerateCSharpFiles(projectPath);

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
        LastProjectPassMetrics = Array.Empty<Cs2jPassMetric>();

        // 查找所有 .cs 文件
        var primaryProjectPath = Path.GetFullPath(projectPath);
        var csFiles = EnumerateCSharpFiles(primaryProjectPath);

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

                foreach (var extraFile in EnumerateCSharpFiles(fullExtra))
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
        var results = await pipeline.ConvertProjectAsync(sourceFiles, emitFilePaths);
        LastProjectPassMetrics = pipeline.LastPassMetrics.ToList();
        LastLinqStatistics = pipeline.LastLinqStatistics;
        return results;
    }

    private static List<string> EnumerateCSharpFiles(string rootPath)
    {
        var fullRootPath = Path.GetFullPath(rootPath);
        if (!Directory.Exists(fullRootPath))
        {
            return [];
        }

        var files = new List<string>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(fullRootPath);

        while (pendingDirectories.Count > 0)
        {
            var currentDirectory = pendingDirectories.Pop();

            foreach (var file in Directory.EnumerateFiles(currentDirectory, "*.cs", SearchOption.TopDirectoryOnly))
            {
                files.Add(Path.GetFullPath(file));
            }

            foreach (var subDirectory in Directory.EnumerateDirectories(currentDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                if (IgnoredDirectoryNames.Contains(Path.GetFileName(subDirectory)))
                {
                    continue;
                }

                pendingDirectories.Push(subDirectory);
            }
        }

        return files;
    }

    private IReadOnlyList<ICs2jPass<SingleFilePassState>> CreateSingleFilePasses()
    {
        return new ICs2jPass<SingleFilePassState>[]
        {
            new SingleFileLinqDesugarPass(),
            new SingleFileCompilationCheckPass(),
            new SingleFileUnsupportedDomainCheckPass(),
            new SingleFilePlatformBoundaryCheckPass(),
            new SingleFileNativeInteropCheckPass(),
            new SingleFileContextNormalizationPass(),
            new SingleFileJavaEmitPass(_irRewriters),
        };
    }

    private ConversionResult CreateSuccessResult(
        string code,
        ConversionContext context,
        string? fileName,
        IReadOnlyList<Cs2jPassMetric>? passMetrics = null)
    {
        return new ConversionResult
        {
            Success = context.Diagnostics.Messages.All(m => m.Severity != Context.DiagnosticSeverity.Error),
            GeneratedCode = code,
            Diagnostics = context.Diagnostics.Messages.ToList(),
            PassMetrics = passMetrics != null ? passMetrics.ToList() : Array.Empty<Cs2jPassMetric>(),
            FileName = fileName
        };
    }

    private ConversionResult CreateFailureResult(
        ConversionContext context,
        string? fileName,
        IReadOnlyList<Cs2jPassMetric>? passMetrics = null)
    {
        return new ConversionResult
        {
            Success = false,
            GeneratedCode = string.Empty,
            Diagnostics = context.Diagnostics.Messages.ToList(),
            PassMetrics = passMetrics != null ? passMetrics.ToList() : Array.Empty<Cs2jPassMetric>(),
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

        var supplemental = new[] { "System.Runtime.dll", "System.Collections.dll", "netstandard.dll" };
        foreach (var name in supplemental)
        {
            var path = Path.Combine(frameworkDir, name);
            if (File.Exists(path))
                yield return MetadataReference.CreateFromFile(path);
        }
    }
}
