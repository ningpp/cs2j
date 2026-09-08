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
    /// <summary>
    /// The C# source file path that primarily defined this type (for diagnostic purposes).
    /// Set when the conversion pipeline can determine which .cs file the type came from.
    /// </summary>
    public string? SourceFilePath { get; set; }
    /// <summary>Java package for this output file (used by Holder class generation).</summary>
    public string? Package { get; set; }
    /// <summary>
    /// Java platform module names required by this output file (e.g. "java.base", "java.sql").
    /// Populated by the module dependency analysis pass when <c>JavaMetadataPath</c> is configured.
    /// </summary>
    public IReadOnlySet<string>? JavaModuleDependencies { get; set; }

    /// <summary>
    /// npm 包依赖（生成 TS 的 ESM 导入包名，如 "@csharptotypescript/compat"）。
    /// 由 ProjectNpmDependencyPass（TS 目标，Task 38）收集；CLI 据此写模块
    /// package.json dependencies（Task 40 接线）。
    /// </summary>
    public IReadOnlySet<string>? NpmPackageDependencies { get; set; }

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
    public GotoEliminator.GotoEliminatorStatistics? LastGotoStatistics { get; private set; }

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
                    // System.Buffers / Memory (ReadOnlySequence, BuffersExtensions, ArrayPool
                    // extension resolution) live in System.Private.CoreLib on modern .NET;
                    // referencing the type keeps BuffersExtensions.ToArray etc. resolvable
                    // in single-file conversions.
                    MetadataReference.CreateFromFile(typeof(System.Buffers.BuffersExtensions).Assembly.Location),
                    // System.Net.IPAddress is in System.Net.Primitives on .NET Core.
                    // Without this, Roslyn cannot resolve IPAddress.Loopback/IPv6Loopback
                    // so MapMethod never fires for IPAddress property mappings.
                    MetadataReference.CreateFromFile(typeof(System.Net.IPAddress).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(System.Text.RegularExpressions.Regex).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(System.Text.Json.JsonSerializer).Assembly.Location),
                    // System.Xml.Serialization types (XmlSerializer, XmlAttributeAttribute, etc.)
                    // are used by converted test projects and must resolve for semantic mapping.
                    MetadataReference.CreateFromFile(typeof(System.Xml.Serialization.XmlSerializer).Assembly.Location),
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
                    .WithAllowUnsafe(true)
            );
            var library = Cs2jLibraryFactory.CreateSingleFile(request.SourceCode, request.FileName, syntaxTree, compilation);
            context.ProjectCompilation = compilation;
            context.SemanticModel = library.PrimaryCompilation?.GetSemanticModel(syntaxTree) ?? compilation.GetSemanticModel(syntaxTree);
            var passState = new SingleFilePassState
            {
                Request = request,
                Context = context,
                SyntaxTree = syntaxTree,
                Compilation = compilation,
                Library = library,
            };

            Cs2jPassExecutor.Execute(passState, context, CreateSingleFilePasses(request.Options), passMetrics);

            if (passState.EmitSucceeded)
            {
                var code = StripGlobalAlias(passState.GeneratedCode);
                code = StripRedundantConditionalParens(code);
                var result = CreateSuccessResult(code, context, request.FileName, passMetrics);
                result.Compilation = passState.JavaCompilation;   // 保留 IR，供 TS 发射器直接发射（Task 10 绿测试）与后置 pass 使用
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
        IEnumerable<string>? additionalSemanticProjectPaths = null,
        string? projectName = null,
        string? projectFilePath = null,
        IReadOnlyList<string>? projectReferences = null,
        bool isTestProject = false)
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
        var results = await pipeline.ConvertProjectAsync(
            sourceFiles,
            emitFilePaths,
            projectName,
            projectFilePath,
            projectReferences,
            isTestProject);
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

    private IReadOnlyList<ICs2jPass<SingleFilePassState>> CreateSingleFilePasses(ConversionOptions options)
    {
        // Codegen 层分叉：TS 路径用 SingleFileTsEmitPass 替换 SingleFileJavaEmitPass
        //（仅跑用户注册 rewriter，Java 专属内置 rewriter 不进入 TS 管线）。
        if (options.TargetLanguage == Context.TargetLanguage.TypeScript)
        {
            return new ICs2jPass<SingleFilePassState>[]
            {
                new SingleFileLinqDesugarPass(),
                new SingleFileCompilationCheckPass(),
                new SingleFileUnsupportedDomainCheckPass(),
                new SingleFilePlatformBoundaryCheckPass(),
                new SingleFileNativeInteropCheckPass(),
                new SingleFileContextNormalizationPass(),
                new SingleFileTsEmitPass(_irRewriters),
            };
        }

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
    /// Strips the C# "global::" namespace alias qualifier from generated Java code.
    /// Some auto-generated sources (e.g. XSD.exe output) use global:: to fully qualify
    /// type names. The qualifier has no meaning in Java and must be removed.
    /// </summary>
    internal static string StripGlobalAlias(string code) =>
        code.Replace("global::", "", StringComparison.Ordinal);

    internal static string StripRedundantConditionalParens(string code)
    {
        // Handle lines where a bare ternary is used as a void statement body
        // Pattern: `Type method(params) { cond ? whenTrue : whenFalse; }`
        // Convert to: `Type method(params) {\n    if (cond) {\n        whenTrue;\n    }\n}`
        var lines = code.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = ConvertBareTernaryToIfElse(lines[i]);
        }
        var result = string.Join("\n", lines);
        // Fix Java-incompatible float literals like 1E11.0 → 1E11
        result = System.Text.RegularExpressions.Regex.Replace(result,
            @"(\d+(?:\.\d+)?[Ee][+-]?\d+)\.0(?![\d\.])",
            "$1");
        // Fix unbalanced parens in if-conditions from C# null-conditional + NOT
        result = BalanceIfConditionParens(result);
        return result;
    }

    /// <summary>
    /// Fixes unbalanced parentheses in `if (CONDITION) {` and similar patterns.
    /// C#'s `!` + null-conditional `?.` + null-coalescing `??` creates extra wraps
    /// that can leave the Java condition unbalanced. This function detects and corrects
    /// such imbalances by adjusting outer parens.
    /// </summary>
    private static string BalanceIfConditionParens(string code)
    {
        var lines = code.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = BalanceIfLineParens(lines[i]);
        }
        return string.Join("\n", lines);
    }

    private static string BalanceIfLineParens(string line)
    {
        var trimmed = line.TrimEnd();
        if (!trimmed.Contains("if (") && !trimmed.Contains("for (") && !trimmed.Contains("while ("))
            return line;

        // Build a mask of literal regions (char literals 'x', string literals "x")
        // so that parentheses inside literals are not counted as structural parens.
        var literalMask = BuildLiteralMask(trimmed);

        // Find first `(` for the if/while/for condition (skip parens inside literals)
        int outerOpen = -1;
        for (int i = 0; i < trimmed.Length; i++)
        {
            if (literalMask[i]) continue;
            if (trimmed[i] == '(' && (i == 0 || trimmed[i - 1] != '='))
            { outerOpen = i; break; }
        }
        if (outerOpen < 0) return line;

        // Find `{` (skip braces inside literals)
        int braceStart = -1;
        for (int i = outerOpen; i < trimmed.Length; i++)
        {
            if (literalMask[i]) continue;
            if (trimmed[i] == '{') { braceStart = i; break; }
        }
        if (braceStart <= outerOpen + 1) return line;

        // Find the closing `)` for the condition (last `)` before ` {`, skip literals)
        int outerClose = -1;
        for (int i = braceStart - 1; i > outerOpen; i--)
        {
            if (literalMask[i]) continue;
            if (trimmed[i] == ')') { outerClose = i; break; }
            else if (!char.IsWhiteSpace(trimmed[i]) && trimmed[i] != '(')
                break; // Not a clean `) {` pattern
        }
        if (outerClose <= outerOpen) return line;

        // Compute depth within the outer ( ), skipping literals
        int depth = 0;
        for (int i = outerOpen + 1; i < outerClose; i++)
        {
            if (literalMask[i]) continue;
            char c = trimmed[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
        }

        if (depth == 0) return line; // Balanced

        if (depth > 0)
        {
            // More `(` than `)` — add `)` after outerClose, before ` {`
            return trimmed.Substring(0, outerClose + 1) + new string(')', depth) + trimmed.Substring(outerClose + 1);
        }
        else
        {
            // More `)` than `(` — remove extra `(` from beginning of condition
            int toRemove = -depth;
            int skipStart = outerOpen + 1;
            while (toRemove > 0 && skipStart < outerClose)
            {
                // Skip literal regions when looking for leading `(`
                if (literalMask[skipStart]) { skipStart++; continue; }
                if (trimmed[skipStart] != '(') break;
                skipStart++;
                toRemove--;
            }
            return trimmed.Substring(0, outerOpen + 1) + trimmed.Substring(skipStart);
        }
    }

    /// <summary>
    /// Builds a boolean mask indicating which character positions are inside
    /// char literals ('x') or string literals ("x"). Positions inside literals
    /// should be ignored when counting structural parentheses, because a
    /// char literal like '(' or ')' contains paren characters that are not
    /// code-level parens.
    /// </summary>
    private static bool[] BuildLiteralMask(string line)
    {
        var mask = new bool[line.Length];
        bool inChar = false;
        bool inString = false;
        bool escaped = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (inChar)
            {
                mask[i] = true;
                if (c == '\\') { escaped = true; continue; }
                if (c == '\'') { inChar = false; }
                continue;
            }

            if (inString)
            {
                mask[i] = true;
                if (c == '\\') { escaped = true; continue; }
                if (c == '"') { inString = false; }
                continue;
            }

            // Not inside any literal — check for literal starts
            if (c == '\'') { inChar = true; mask[i] = true; continue; }
            if (c == '"') { inString = true; mask[i] = true; continue; }
        }

        return mask;
    }

    private static string ConvertBareTernaryToIfElse(string line)
    {
        var t = line.TrimEnd();
        if (!t.EndsWith(";}") && !t.EndsWith("; }")) return line;

        int closeBrace = line.LastIndexOf('}');
        if (closeBrace <= 0) return line;

        int depth = 0, openBrace = -1;
        for (int i = closeBrace; i >= 0; i--)
        {
            if (line[i] == '}') depth++;
            else if (line[i] == '{') { depth--; if (depth == 0) { openBrace = i; break; } }
        }
        if (openBrace <= 0) return line;

        var prefix = line.Substring(0, openBrace);
        var inside = line.Substring(openBrace + 1, closeBrace - openBrace - 1).Trim();
        if (!inside.EndsWith(";")) return line;
        var expr = inside.Substring(0, inside.Length - 1).Trim();

        var pt = prefix.Trim();
        if (!pt.Contains('(')) return line;
        // Skip if prefixes/keywords indicate this is NOT a simple void ternary
        if (pt.StartsWith("return ") || pt.StartsWith("var ") || pt.StartsWith("int ")
            || pt.StartsWith("long ") || pt.StartsWith("double ") || pt.StartsWith("float ")
            || pt.StartsWith("if ") || pt.StartsWith("while ") || pt.StartsWith("for ")
            || pt.StartsWith("throw "))
            return line;
        if (expr.Contains("return ") || expr.Contains("throw")) return line;

        // Skip if the expression contains a top-level assignment (e.g.
        // `_field = cond ? a : b`). The ternary is the RHS of an assignment,
        // not a standalone void ternary statement. Converting it to if-else
        // would produce invalid Java (bare expressions as statements).
        int d = 0;
        for (int i = 0; i < expr.Length; i++)
        {
            char c = expr[i];
            if (c == '(') d++;
            else if (c == ')') d--;
            else if (d == 0 && c == '=')
            {
                // Skip compound operators (==, !=, >=, <=, +=, -=, *=, /=, %=, &=, |=, ^=)
                char prev = i > 0 ? expr[i - 1] : '\0';
                char next = i + 1 < expr.Length ? expr[i + 1] : '\0';
                if (prev is '=' or '!' or '>' or '<' or '+' or '-' or '*' or '/' or '%' or '&' or '|' or '^')
                    continue;
                if (next is '=')
                    continue;
                return line; // standalone assignment at depth 0
            }
        }

        int qPos = -1;
        d = 0;
        for (int i = 0; i < expr.Length; i++)
        {
            char c = expr[i];
            if (c == '(') d++;
            else if (c == ')') d--;
            else if (c == '?' && d == 0 && i + 1 < expr.Length && expr[i + 1] != '.' && expr[i + 1] != '?')
            { qPos = i; break; }
        }
        if (qPos <= 0) return line;

        int cPos = -1;
        for (int i = qPos + 1; i < expr.Length; i++)
        {
            char c = expr[i];
            if (c == '(') d++;
            else if (c == ')') d--;
            else if (c == ':' && d == 0) { cPos = i; break; }
        }
        if (cPos <= qPos) return line;

        var cond = expr.Substring(0, qPos).Trim();
        var whenTrue = expr.Substring(qPos + 1, cPos - qPos - 1).Trim();
        var whenFalse = expr.Substring(cPos + 1).Trim();

        if (string.IsNullOrEmpty(whenTrue) || string.IsNullOrEmpty(whenFalse)) return line;

        int indent = 0;
        while (indent < line.Length && char.IsWhiteSpace(line[indent])) indent++;
        var ind1 = new string(' ', Math.Max(indent, 4));
        var ind2 = new string(' ', Math.Max(indent, 4) + 4);

        var outerIndent = new string(' ', indent);

        if (whenFalse == "null")
            return prefix + "{\n" + ind1 + "if (" + cond + ") {\n" + ind2 + whenTrue + ";\n" + ind1 + "}" + outerIndent + "}\n";

        return prefix + "{\n" + ind1 + "if (" + cond + ") {\n" + ind2 + whenTrue + ";\n" + ind1 + "} else {\n" + ind2 + whenFalse + ";\n" + ind1 + "}" + outerIndent + "}\n";
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

        var supplemental = new[] { "System.Runtime.dll", "System.Collections.dll", "netstandard.dll", "System.Threading.dll", "System.Threading.Tasks.Parallel.dll" };
        foreach (var name in supplemental)
        {
            var path = Path.Combine(frameworkDir, name);
            if (File.Exists(path))
                yield return MetadataReference.CreateFromFile(path);
        }
    }
}
