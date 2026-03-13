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

            // Phase 4: Emit Holder classes for ref/out parameter pattern
            var basePackage = DetermineBasePackage(results);
            results.AddRange(GenerateHolderClasses(basePackage));

            // Phase 5: Add cross-package wildcard imports so all MSAGL types see each other
            AddCrossPackageImports(results);

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
        // 每次转换一个类型前清空导入集合，避免跨文件污染
        context.ClearImports();

        try
        {
            // Handle Enum types specially — EnumDeclarationSyntax is not TypeDeclarationSyntax
            if (typeGroup.TypeSymbol.TypeKind == TypeKind.Enum)
                return ConvertEnumTypeGroup(typeGroup, compilation, context);

            // Handle Delegate types specially
            if (typeGroup.TypeSymbol.TypeKind == TypeKind.Delegate)
                return ConvertDelegateTypeGroup(typeGroup, compilation, context);

            // Check if we have any syntax nodes
            if (typeGroup.SyntaxNodes.Count == 0)
            {
                return new ConversionResult
                {
                    Success = false,
                    FileName = $"{typeGroup.TypeSymbol.Name}.java",
                    Diagnostics = new List<Context.DiagnosticMessage>
                    {
                        new(Context.DiagnosticSeverity.Error, $"Type '{typeGroup.TypeSymbol.Name}' has no syntax nodes", null)
                    }
                };
            }

            // Find a syntax tree that is actually part of this compilation (guard against SourceLink / PDB trees)
            var validSyntaxTree = typeGroup.SyntaxNodes
                .Select(n => n.SyntaxTree)
                .FirstOrDefault(t => compilation.ContainsSyntaxTree(t));

            if (validSyntaxTree == null)
            {
                return new ConversionResult
                {
                    Success = false,
                    FileName = $"{typeGroup.TypeSymbol.Name}.java",
                    Diagnostics = new List<Context.DiagnosticMessage>
                    {
                        new(Context.DiagnosticSeverity.Error, $"Type '{typeGroup.TypeSymbol.Name}': no syntax tree belongs to current compilation", null)
                    }
                };
            }

            // Get semantic model for the first syntax tree
            var semanticModel = compilation.GetSemanticModel(validSyntaxTree);
            context.SemanticModel = semanticModel;

            // Create merged declaration
            var mergedDeclaration = MergedTypeDeclaration.FromPartialTypeGroup(typeGroup, semanticModel);

            // Register merged type in context
            context.RegisterMergedPartialType(mergedDeclaration);

            // Dispatch to the right transformer based on TypeKind
            Java.JavaTypeDeclaration? javaType;
            switch (typeGroup.TypeSymbol.TypeKind)
            {
                case TypeKind.Class:
                    javaType = new ClassTransformer().TransformMerged(mergedDeclaration, context);
                    break;
                case TypeKind.Struct:
                    // Use the first valid syntax node directly; structs are rarely partial
                    var structNode = typeGroup.SyntaxNodes
                        .FirstOrDefault(n => compilation.ContainsSyntaxTree(n.SyntaxTree));
                    javaType = structNode != null
                        ? new StructTransformer().Transform(structNode, context)
                        : null;
                    break;
                case TypeKind.Interface:
                    var ifaceNode = typeGroup.SyntaxNodes
                        .FirstOrDefault(n => compilation.ContainsSyntaxTree(n.SyntaxTree));
                    javaType = ifaceNode != null
                        ? new InterfaceTransformer().Transform(ifaceNode, context)
                        : null;
                    break;
                default:
                    return null;
            }

            // Generate Java code
            if (javaType != null)
            {
                // Determine package from namespace (empty string = global namespace, handled by NamespaceToPackage)
                var ns = typeGroup.TypeSymbol.ContainingNamespace?.ToDisplayString() ?? "";
                var rawPkg = context.NamespaceToPackage(ns);
                var pkg = string.IsNullOrEmpty(rawPkg) ? null : rawPkg;

                // Build file header: package declaration + standard imports
                var sb = new System.Text.StringBuilder();
                if (pkg != null)
                    sb.AppendLine($"package {pkg};").AppendLine();
                // Standard JDK wildcard imports
                sb.AppendLine("import java.util.*;");
                sb.AppendLine("import java.util.function.*;");
                sb.AppendLine("import java.util.stream.*;");
                sb.AppendLine("import java.io.*;");
                // Imports collected during conversion (type-specific)
                foreach (var imp in context.ImportedTypes.OrderBy(x => x))
                    sb.AppendLine($"import {imp};");
                if (context.ImportedTypes.Count > 0)
                    sb.AppendLine();

                sb.AppendLine(javaType.ToString(""));

                return new ConversionResult
                {
                    Success = true,
                    GeneratedCode = sb.ToString(),
                    Diagnostics = new List<Context.DiagnosticMessage>(),
                    FileName = mergedDeclaration.OutputFileName,
                    Package = pkg
                };
            }

            return null;
        }
        catch (Exception ex)
        {
            // Use a fresh diagnostics list (not the accumulated context diagnostics)
            return new ConversionResult
            {
                Success = false,
                FileName = $"{typeGroup.TypeSymbol.Name}.java",
                Diagnostics = new List<Context.DiagnosticMessage>
                {
                    new(Context.DiagnosticSeverity.Error, $"Failed to convert type '{typeGroup.TypeSymbol.Name}': {ex.Message}", null)
                }
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

    /// <summary>
    /// Converts an enum type group using EnumTransformer.
    /// </summary>
    private ConversionResult? ConvertEnumTypeGroup(
        PartialTypeGroup typeGroup,
        CSharpCompilation compilation,
        ConversionContext context)
    {
        foreach (var syntaxRef in typeGroup.TypeSymbol.DeclaringSyntaxReferences)
        {
            try
            {
                var syntax = syntaxRef.GetSyntax();
                if (syntax is not EnumDeclarationSyntax enumSyntax) continue;
                if (!compilation.ContainsSyntaxTree(syntax.SyntaxTree)) continue;

                context.SemanticModel = compilation.GetSemanticModel(syntax.SyntaxTree);
                var nsName = typeGroup.TypeSymbol.ContainingNamespace?.ToDisplayString() ?? "";
                context.EnterNamespace(nsName);
                try
                {
                    var transformer = new EnumTransformer();
                    var javaEnum = transformer.TransformEnum(enumSyntax, context);
                    var rawPkg2 = context.NamespaceToPackage(nsName);
                    var pkg = string.IsNullOrEmpty(rawPkg2) ? null : rawPkg2;

                    // Build file header: package declaration + standard imports
                    var enumSb = new System.Text.StringBuilder();
                    if (pkg != null) enumSb.AppendLine($"package {pkg};").AppendLine();
                    enumSb.AppendLine("import java.util.*;");
                    enumSb.AppendLine("import java.util.function.*;");
                    enumSb.AppendLine("import java.util.stream.*;");
                    enumSb.AppendLine("import java.io.*;");
                    enumSb.AppendLine();
                    enumSb.Append(javaEnum.ToString(""));

                    return new ConversionResult
                    {
                        Success = true,
                        GeneratedCode = enumSb.ToString(),
                        FileName = $"{typeGroup.TypeSymbol.Name}.java",
                        Package = pkg,
                        Diagnostics = context.Diagnostics.Messages.ToList()
                    };
                }
                finally
                {
                    context.LeaveNamespace();
                }
            }
            catch (Exception ex)
            {
                context.Diagnostics.Warning($"Failed to convert enum '{typeGroup.TypeSymbol.Name}': {ex.Message}");
            }
        }
        return null;
    }

    /// <summary>
    /// Converts a delegate type group using DelegateTransformer.
    /// </summary>
    private ConversionResult? ConvertDelegateTypeGroup(
        PartialTypeGroup typeGroup,
        CSharpCompilation compilation,
        ConversionContext context)
    {
        foreach (var syntaxRef in typeGroup.TypeSymbol.DeclaringSyntaxReferences)
        {
            try
            {
                var syntax = syntaxRef.GetSyntax();
                if (syntax is not DelegateDeclarationSyntax delegateSyntax) continue;
                if (!compilation.ContainsSyntaxTree(syntax.SyntaxTree)) continue;

                context.SemanticModel = compilation.GetSemanticModel(syntax.SyntaxTree);
                var delNsName = typeGroup.TypeSymbol.ContainingNamespace?.ToDisplayString() ?? "";
                context.EnterNamespace(delNsName);
                try
                {
                    var transformer = new DelegateTransformer();
                    var javaInterface = transformer.TransformDelegate(delegateSyntax, context);
                    if (javaInterface == null) return null;

                    var rawPkg3 = context.NamespaceToPackage(delNsName);
                    var pkg = string.IsNullOrEmpty(rawPkg3) ? null : rawPkg3;

                    // Build file header: package declaration + standard imports
                    var delSb = new System.Text.StringBuilder();
                    if (pkg != null) delSb.AppendLine($"package {pkg};").AppendLine();
                    delSb.AppendLine("import java.util.*;");
                    delSb.AppendLine("import java.util.function.*;");
                    delSb.AppendLine("import java.util.stream.*;");
                    delSb.AppendLine("import java.io.*;");
                    delSb.AppendLine();
                    delSb.Append(javaInterface.ToString(""));

                    return new ConversionResult
                    {
                        Success = true,
                        GeneratedCode = delSb.ToString(),
                        FileName = $"{typeGroup.TypeSymbol.Name}.java",
                        Package = pkg,
                        Diagnostics = new List<Context.DiagnosticMessage>()
                    };
                }
                finally
                {
                    context.LeaveNamespace();
                }
            }
            catch (Exception ex)
            {
                context.Diagnostics.Warning($"Failed to convert delegate '{typeGroup.TypeSymbol.Name}': {ex.Message}");
            }
        }
        return null;
    }

    /// <summary>
    /// <summary>
    /// Post-processes all generated Java files by adding wildcard imports for every MSAGL package.
    /// This ensures any class can reference any other MSAGL class without needing fully-qualified names.
    /// </summary>
    private static void AddCrossPackageImports(List<ConversionResult> results)
    {
        // Collect all unique non-null packages from generated results
        var allPackages = results
            .Where(r => !string.IsNullOrEmpty(r.Package))
            .Select(r => r.Package!)
            .Distinct()
            .OrderBy(p => p)
            .ToList();

        if (allPackages.Count == 0) return;

        // Build a map of simple class name -> list of packages containing that class.
        // Used to resolve ambiguity when the same class name appears in multiple MSAGL packages.
        var classNameToPackages = new Dictionary<string, List<string>>();
        foreach (var r in results)
        {
            if (string.IsNullOrEmpty(r.Package) || string.IsNullOrEmpty(r.FileName)) continue;
            var className = System.IO.Path.GetFileNameWithoutExtension(r.FileName);
            if (string.IsNullOrEmpty(className)) continue;
            if (!classNameToPackages.TryGetValue(className, out var pkgList))
                classNameToPackages[className] = pkgList = new List<string>();
            pkgList.Add(r.Package!);
        }

        // Classes appearing in exactly one MSAGL package that conflict with java.util.*
        // (Set, Iterator, etc.) need an explicit import so the MSAGL class takes precedence.
        var javaUtilNames = new HashSet<string> { "Set", "Iterator", "AbstractSet", "AbstractMap" };
        var javaUtilConflictImport = new Dictionary<string, string>(); // className → MSAGL pkg
        foreach (var (cn, pkgs) in classNameToPackages)
        {
            if (pkgs.Count == 1 && javaUtilNames.Contains(cn))
                javaUtilConflictImport[cn] = pkgs[0];
        }

        // Classes appearing in 2+ MSAGL packages: for each file, pick the "preferred" package.
        // Heuristic: the canonical package is the one with the shortest fully-qualified name
        // (i.e. the most central/core package).
        var msaglConflicts = classNameToPackages
            .Where(kv => kv.Value.Count > 1)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        var msaglConflictCanonical = msaglConflicts
            .ToDictionary(kv => kv.Key,
                kv => kv.Value.OrderBy(p => p.Length).ThenBy(p => p).First());

        // Build the cross-package import block
        var crossImports = new System.Text.StringBuilder();
        foreach (var pkg in allPackages)
            crossImports.AppendLine($"import {pkg}.*;");
        var crossImportBlock = crossImports.ToString();

        // Prepend cross-package imports after the first existing import/package lines
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            if (string.IsNullOrEmpty(r.GeneratedCode)) continue;
            // Find insertion point: after the last existing import/package line
            var lines = r.GeneratedCode.Split('\n').ToList();
            int lastImportIdx = -1;
            for (int li = 0; li < lines.Count; li++)
            {
                var trimmed = lines[li].TrimStart();
                if (trimmed.StartsWith("import ") || trimmed.StartsWith("package "))
                    lastImportIdx = li;
                else if (lastImportIdx >= 0 && !string.IsNullOrWhiteSpace(trimmed))
                    break;
            }
            // Insert cross-package imports after lastImportIdx
            var insertAt = lastImportIdx >= 0 ? lastImportIdx + 1 : 0;
            var toInsert = crossImportBlock.Split('\n').Select(l => l.TrimEnd()).ToList();

            // Add explicit single-type-imports for classes that conflict with java.util.*
            // These explicit imports take precedence over the java.util.* wildcard
            foreach (var (className, pkg) in javaUtilConflictImport)
            {
                if (r.Package != pkg)  // Don't add explicit import for the class's own package
                    toInsert.Add($"import {pkg}.{className};");
            }

            // Add explicit single-type-imports for MSAGL-MSAGL class name conflicts.
            // Files in one of the conflicting packages import their own version;
            // all other files import the canonical (shortest-named) package's version.
            foreach (var (className, packages) in msaglConflicts)
            {
                var preferredPkg = packages.Contains(r.Package!)
                    ? r.Package!
                    : msaglConflictCanonical[className];
                toInsert.Add($"import {preferredPkg}.{className};");
            }

            lines.InsertRange(insertAt, toInsert);
            r.GeneratedCode = string.Join("\n", lines);
        }
    }

    /// <summary>
    /// Determines the base package by examining the most common package prefix in results.
    /// </summary>
    private static string DetermineBasePackage(List<ConversionResult> results)
    {
        var packages = results
            .Where(r => !string.IsNullOrEmpty(r.Package))
            .Select(r => r.Package!)
            .GroupBy(p => p)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();

        if (packages != null)
        {
            var pkg = packages.Key;
            // Take only the first 2 root segments to form a stable base package for holder classes
            // (e.g. "Microsoft.Msagl.Core.Geometry" -> "Microsoft.Msagl")
            var parts = pkg.Split('.');
            return string.Join(".", parts.Take(Math.Min(parts.Length, 2)));
        }

        return "com.generated";
    }

    /// <summary>
    /// Generates Holder class files for ref/out parameter support.
    /// These are emitted unconditionally so all converted code can import them.
    /// </summary>
    private static List<ConversionResult> GenerateHolderClasses(string basePackage)
    {
        var holders = new (string name, string type, string defaultValue)[]
        {
            ("IntHolder", "int", "0"),
            ("LongHolder", "long", "0L"),
            ("DoubleHolder", "double", "0.0"),
            ("FloatHolder", "float", "0.0f"),
            ("BoolHolder", "boolean", "false"),
            ("CharHolder", "char", "'\\0'"),
            ("ShortHolder", "short", "(short)0"),
            ("ByteHolder", "byte", "(byte)0"),
        };

        var results = new List<ConversionResult>();

        foreach (var (name, type, defaultValue) in holders)
        {
            var code = $@"package {basePackage};

/** Holder for ref/out {type} parameters (generated by CSharpToJava converter). */
public final class {name} {{
    public {type} value;
    public {name}() {{ this.value = {defaultValue}; }}
    public {name}({type} value) {{ this.value = value; }}
}}
";
            results.Add(new ConversionResult
            {
                Success = true,
                GeneratedCode = code,
                FileName = $"{name}.java",
                Package = basePackage,
                Diagnostics = new List<Context.DiagnosticMessage>()
            });
        }

        // Generic ObjectHolder<T>
        var objectHolderCode = $@"package {basePackage};

/** Holder for ref/out object parameters (generated by CSharpToJava converter). */
public final class ObjectHolder<T> {{
    public T value;
    public ObjectHolder() {{ this.value = null; }}
    public ObjectHolder(T value) {{ this.value = value; }}
}}
";
        results.Add(new ConversionResult
        {
            Success = true,
            GeneratedCode = objectHolderCode,
            FileName = "ObjectHolder.java",
            Package = basePackage,
            Diagnostics = new List<Context.DiagnosticMessage>()
        });

        // StopwatchHelper (replaces System.Diagnostics.Stopwatch)
        var stopwatchCode = $@"package {basePackage};

/** Stopwatch helper class (generated by CSharpToJava converter, replacing System.Diagnostics.Stopwatch). */
public final class StopwatchHelper {{
    private long startNanos;
    private long elapsedNanos;
    private boolean running;

    public StopwatchHelper() {{}}

    public static StopwatchHelper startNew() {{
        StopwatchHelper sw = new StopwatchHelper();
        sw.start();
        return sw;
    }}

    public void start() {{
        if (!running) {{
            startNanos = System.nanoTime();
            running = true;
        }}
    }}

    public void stop() {{
        if (running) {{
            elapsedNanos += System.nanoTime() - startNanos;
            running = false;
        }}
    }}

    public void reset() {{
        elapsedNanos = 0;
        running = false;
    }}

    public void restart() {{
        reset();
        start();
    }}

    public long getElapsedMilliseconds() {{
        long elapsed = elapsedNanos;
        if (running) elapsed += System.nanoTime() - startNanos;
        return elapsed / 1_000_000L;
    }}

    public boolean isRunning() {{ return running; }}
}}
";
        results.Add(new ConversionResult
        {
            Success = true,
            GeneratedCode = stopwatchCode,
            FileName = "StopwatchHelper.java",
            Package = basePackage,
            Diagnostics = new List<Context.DiagnosticMessage>()
        });

        return results;
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
