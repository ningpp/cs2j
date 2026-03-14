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
            "System.Collections.NonGeneric.dll",  // System.Collections.Queue, Stack, Hashtable, ArrayList
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
        var javaUtilNames = new HashSet<string> { "Set", "Iterator", "AbstractSet", "AbstractMap", "Timer" };
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

        results.AddRange(GenerateXmlWrappers(basePackage));
        results.AddRange(GenerateJsonWrappers(basePackage));
        results.AddRange(GenerateUtilityClasses(basePackage));

        return results;
    }

    /// <summary>
    /// Generates thin Java wrapper classes for System.Xml.* types, backed by javax.xml.stream (StAX).
    /// These match the Java method names that the converter generates from C# XmlReader/XmlWriter usage.
    /// </summary>
    private static List<ConversionResult> GenerateXmlWrappers(string basePackage)
    {
        var results = new List<ConversionResult>();

        // ── XmlNodeType ──────────────────────────────────────────────────────────────
        var xmlNodeTypeCode = $@"package {basePackage};

import javax.xml.stream.XMLStreamConstants;

/** Replacement for System.Xml.XmlNodeType (generated by CSharpToJava converter). */
public class XmlNodeType {{
    public static final int None            = 0;
    public static final int Element         = XMLStreamConstants.START_ELEMENT;
    public static final int Attribute       = 10;
    public static final int Text            = XMLStreamConstants.CHARACTERS;
    public static final int CDATA           = XMLStreamConstants.CDATA;
    public static final int EndElement      = XMLStreamConstants.END_ELEMENT;
    public static final int EndDocument     = XMLStreamConstants.END_DOCUMENT;
    public static final int Comment         = XMLStreamConstants.COMMENT;
    public static final int ProcessingInstruction = XMLStreamConstants.PROCESSING_INSTRUCTION;

    /** Static getter — converter generates XmlNodeType.getNone() for enum-like access. */
    public static int getNone()             {{ return None; }}
    public static int getElement()          {{ return Element; }}
    public static int getAttribute()        {{ return Attribute; }}
    public static int getText()             {{ return Text; }}
    public static int getCDATA()            {{ return CDATA; }}
    public static int getEndElement()       {{ return EndElement; }}
    public static int getEndDocument()      {{ return EndDocument; }}
    public static int getComment()          {{ return Comment; }}
    public static int getProcessingInstruction() {{ return ProcessingInstruction; }}
}}
";
        results.Add(new ConversionResult
        {
            Success = true,
            GeneratedCode = xmlNodeTypeCode,
            FileName = "XmlNodeType.java",
            Package = basePackage,
            Diagnostics = new List<Context.DiagnosticMessage>()
        });

        // ── ReadState ─────────────────────────────────────────────────────────────────
        var readStateCode = $@"package {basePackage};

/** Replacement for System.Xml.ReadState (generated by CSharpToJava converter). */
public class ReadState {{
    public static final int Initial     = 0;
    public static final int Interactive = 1;
    public static final int Error       = 2;
    public static final int EndOfFile   = 3;
    public static final int Closed      = 4;

    public static int getInitial()     {{ return Initial; }}
    public static int getInteractive() {{ return Interactive; }}
    public static int getError()       {{ return Error; }}
    public static int getEndOfFile()   {{ return EndOfFile; }}
    public static int getClosed()      {{ return Closed; }}
}}
";
        results.Add(new ConversionResult
        {
            Success = true,
            GeneratedCode = readStateCode,
            FileName = "ReadState.java",
            Package = basePackage,
            Diagnostics = new List<Context.DiagnosticMessage>()
        });

        // ── XmlConvert ────────────────────────────────────────────────────────────────
        var xmlConvertCode = $@"package {basePackage};

/** Replacement for System.Xml.XmlConvert (generated by CSharpToJava converter). */
public class XmlConvert {{
    public static String toString(double  value) {{ return Double.toString(value); }}
    public static String toString(float   value) {{ return Float.toString(value); }}
    public static String toString(int     value) {{ return Integer.toString(value); }}
    public static String toString(long    value) {{ return Long.toString(value); }}
    public static String toString(boolean value) {{ return Boolean.toString(value); }}
    public static double  toDouble(String s)     {{ return Double.parseDouble(s); }}
    public static float   toSingle(String s)     {{ return Float.parseFloat(s); }}
    public static int     toInt32(String s)      {{ return Integer.parseInt(s); }}
    public static long    toInt64(String s)      {{ return Long.parseLong(s); }}
    public static boolean toBoolean(String s)    {{ return Boolean.parseBoolean(s); }}
}}
";
        results.Add(new ConversionResult
        {
            Success = true,
            GeneratedCode = xmlConvertCode,
            FileName = "XmlConvert.java",
            Package = basePackage,
            Diagnostics = new List<Context.DiagnosticMessage>()
        });

        // ── XmlReaderSettings ─────────────────────────────────────────────────────────
        var xmlReaderSettingsCode = $@"package {basePackage};

/** Replacement for System.Xml.XmlReaderSettings (generated by CSharpToJava converter). */
public class XmlReaderSettings {{
    private boolean ignoreWhitespace = false;
    private boolean checkCharacters  = true;
    public boolean isIgnoreWhitespace()          {{ return ignoreWhitespace; }}
    public void    setIgnoreWhitespace(boolean v) {{ ignoreWhitespace = v; }}
    public boolean isCheckCharacters()           {{ return checkCharacters; }}
    public void    setCheckCharacters(boolean v)  {{ checkCharacters = v; }}
}}
";
        results.Add(new ConversionResult
        {
            Success = true,
            GeneratedCode = xmlReaderSettingsCode,
            FileName = "XmlReaderSettings.java",
            Package = basePackage,
            Diagnostics = new List<Context.DiagnosticMessage>()
        });

        // ── XmlWriterSettings ─────────────────────────────────────────────────────────
        var xmlWriterSettingsCode = $@"package {basePackage};

/** Replacement for System.Xml.XmlWriterSettings (generated by CSharpToJava converter). */
public class XmlWriterSettings {{
    private String  encoding             = ""UTF-8"";
    private boolean indent               = false;
    private String  indentChars          = ""  "";
    private boolean omitXmlDeclaration   = false;
    public String  getEncoding()                    {{ return encoding; }}
    public void    setEncoding(String v)             {{ encoding = v; }}
    public boolean isIndent()                       {{ return indent; }}
    public void    setIndent(boolean v)              {{ indent = v; }}
    public String  getIndentChars()                 {{ return indentChars; }}
    public void    setIndentChars(String v)          {{ indentChars = v; }}
    public boolean isOmitXmlDeclaration()           {{ return omitXmlDeclaration; }}
    public void    setOmitXmlDeclaration(boolean v)  {{ omitXmlDeclaration = v; }}
}}
";
        results.Add(new ConversionResult
        {
            Success = true,
            GeneratedCode = xmlWriterSettingsCode,
            FileName = "XmlWriterSettings.java",
            Package = basePackage,
            Diagnostics = new List<Context.DiagnosticMessage>()
        });

        // ── XmlReader ─────────────────────────────────────────────────────────────────
        var xmlReaderCode = $@"package {basePackage};

import java.io.InputStream;
import java.io.Reader;
import javax.xml.stream.*;

/** Replacement for System.Xml.XmlReader backed by StAX (generated by CSharpToJava converter). */
public class XmlReader implements AutoCloseable {{
    protected XMLStreamReader reader;

    protected XmlReader() {{}}

    protected XmlReader(XMLStreamReader reader) {{
        this.reader = reader;
    }}

    /** Factory — mirrors XmlReader.Create(source, settings) in C#. */
    public static XmlReader create(Object source, Object settings) {{
        try {{
            XMLInputFactory factory = XMLInputFactory.newInstance();
            factory.setProperty(XMLInputFactory.IS_SUPPORTING_EXTERNAL_ENTITIES, false);
            factory.setProperty(XMLInputFactory.SUPPORT_DTD, false);
            if (source instanceof InputStream) {{
                return new XmlReader(factory.createXMLStreamReader((InputStream) source));
            }} else if (source instanceof Reader) {{
                return new XmlReader(factory.createXMLStreamReader((Reader) source));
            }} else if (source instanceof XmlReader) {{
                return (XmlReader) source;
            }}
            throw new RuntimeException(""Unsupported XmlReader source: "" + (source == null ? ""null"" : source.getClass().getName()));
        }} catch (XMLStreamException e) {{
            throw new RuntimeException(e);
        }}
    }}

    public int getNodeType() {{
        return reader != null ? reader.getEventType() : XmlNodeType.getNone();
    }}

    public boolean isStartElement() {{
        return reader != null && reader.isStartElement();
    }}

    public boolean isStartElement(String name) {{
        return reader != null && reader.isStartElement()
            && name.equalsIgnoreCase(reader.getLocalName());
    }}

    /** StAX has no direct IsEmptyElement concept; returns false (safe default for compilation). */
    public boolean getIsEmptyElement() {{ return false; }}

    public String getName() {{
        try {{ return reader != null ? reader.getLocalName() : """"; }}
        catch (Exception e) {{ return """"; }}
    }}

    public String getValue() {{
        try {{ return reader != null ? reader.getText() : """"; }}
        catch (Exception e) {{ return """"; }}
    }}

    public String getAttribute(String name) {{
        return reader != null ? reader.getAttributeValue(null, name) : null;
    }}

    public boolean read() {{
        try {{
            if (reader == null || !reader.hasNext()) return false;
            reader.next();
            return true;
        }} catch (XMLStreamException e) {{ return false; }}
    }}

    public void readEndElement() {{
        try {{
            if (reader == null) return;
            while (reader.hasNext() && !reader.isEndElement()) reader.next();
            if (reader.hasNext()) reader.next();
        }} catch (XMLStreamException e) {{ throw new RuntimeException(e); }}
    }}

    public double readElementContentAsDouble() {{
        try {{ return reader != null ? Double.parseDouble(reader.getElementText().trim()) : 0.0; }}
        catch (XMLStreamException e) {{ throw new RuntimeException(e); }}
    }}

    public int readElementContentAsInt() {{
        try {{ return reader != null ? Integer.parseInt(reader.getElementText().trim()) : 0; }}
        catch (XMLStreamException e) {{ throw new RuntimeException(e); }}
    }}

    public boolean readElementContentAsBoolean() {{
        try {{ return reader != null && Boolean.parseBoolean(reader.getElementText().trim()); }}
        catch (XMLStreamException e) {{ throw new RuntimeException(e); }}
    }}

    public void moveToContent() {{
        try {{
            if (reader == null) return;
            while (reader.hasNext()) {{
                int t = reader.getEventType();
                if (t == XMLStreamConstants.START_ELEMENT
                    || t == XMLStreamConstants.END_ELEMENT
                    || t == XMLStreamConstants.END_DOCUMENT) return;
                reader.next();
            }}
        }} catch (XMLStreamException e) {{ throw new RuntimeException(e); }}
    }}

    public boolean moveToFirstAttribute() {{
        return reader != null && reader.getAttributeCount() > 0;
    }}

    public void skip() {{
        try {{
            if (reader == null || !reader.isStartElement()) return;
            int depth = 1;
            while (depth > 0 && reader.hasNext()) {{
                reader.next();
                if (reader.isStartElement()) depth++;
                else if (reader.isEndElement()) depth--;
            }}
            if (reader.hasNext()) reader.next();
        }} catch (XMLStreamException e) {{ throw new RuntimeException(e); }}
    }}

    public int getReadState() {{
        if (reader == null) return ReadState.getClosed();
        try {{ return reader.hasNext() ? ReadState.getInteractive() : ReadState.getEndOfFile(); }}
        catch (Exception e) {{ return ReadState.getError(); }}
    }}

    @Override
    public void close() throws Exception {{
        if (reader != null) try {{ reader.close(); }} catch (XMLStreamException ignored) {{}}
    }}
}}
";
        results.Add(new ConversionResult
        {
            Success = true,
            GeneratedCode = xmlReaderCode,
            FileName = "XmlReader.java",
            Package = basePackage,
            Diagnostics = new List<Context.DiagnosticMessage>()
        });

        // ── XmlTextReader (alias of XmlReader) ───────────────────────────────────────
        var xmlTextReaderCode = $@"package {basePackage};

import java.io.InputStream;
import java.io.Reader;
import javax.xml.stream.*;

/** Replacement for System.Xml.XmlTextReader (generated by CSharpToJava converter). */
public class XmlTextReader extends XmlReader {{
    private int lineNumber = 0;
    private int linePosition = 0;

    public XmlTextReader() {{ super(); }}

    /** Constructs from a file path (mirrors XmlTextReader(string) in C#). */
    public XmlTextReader(String path) {{
        try {{
            XMLInputFactory factory = XMLInputFactory.newInstance();
            factory.setProperty(XMLInputFactory.IS_SUPPORTING_EXTERNAL_ENTITIES, false);
            factory.setProperty(XMLInputFactory.SUPPORT_DTD, false);
            java.io.FileInputStream fis = new java.io.FileInputStream(path);
            this.reader = factory.createXMLStreamReader(fis);
        }} catch (Exception e) {{ throw new RuntimeException(e); }}
    }}

    /** Constructs from an InputStream (mirrors XmlTextReader(Stream) in C#). */
    public XmlTextReader(InputStream stream) {{
        try {{
            XMLInputFactory factory = XMLInputFactory.newInstance();
            factory.setProperty(XMLInputFactory.IS_SUPPORTING_EXTERNAL_ENTITIES, false);
            factory.setProperty(XMLInputFactory.SUPPORT_DTD, false);
            this.reader = factory.createXMLStreamReader(stream);
        }} catch (Exception e) {{ throw new RuntimeException(e); }}
    }}

    /** Constructs from a Reader (mirrors XmlTextReader(TextReader) in C#). */
    public XmlTextReader(Reader r) {{
        try {{
            XMLInputFactory factory = XMLInputFactory.newInstance();
            factory.setProperty(XMLInputFactory.IS_SUPPORTING_EXTERNAL_ENTITIES, false);
            factory.setProperty(XMLInputFactory.SUPPORT_DTD, false);
            this.reader = factory.createXMLStreamReader(r);
        }} catch (Exception e) {{ throw new RuntimeException(e); }}
    }}

    public int getLineNumber() {{ return lineNumber; }}
    public int getLinePosition() {{ return linePosition; }}
}}
";
        results.Add(new ConversionResult
        {
            Success = true,
            GeneratedCode = xmlTextReaderCode,
            FileName = "XmlTextReader.java",
            Package = basePackage,
            Diagnostics = new List<Context.DiagnosticMessage>()
        });

        // ── XmlWriter ─────────────────────────────────────────────────────────────────
        var xmlWriterCode = $@"package {basePackage};

import java.io.InputStream;
import java.io.OutputStream;
import java.io.Writer;
import javax.xml.stream.*;

/** Replacement for System.Xml.XmlWriter backed by StAX (generated by CSharpToJava converter). */
public class XmlWriter implements AutoCloseable {{
    protected XMLStreamWriter writer;

    protected XmlWriter() {{}}

    protected XmlWriter(XMLStreamWriter writer) {{
        this.writer = writer;
    }}

    /** Factory — mirrors XmlWriter.Create(output, settings) in C#. */
    public static XmlWriter create(Object output, Object settings) {{
        try {{
            XMLOutputFactory factory = XMLOutputFactory.newInstance();
            if (output instanceof OutputStream) {{
                return new XmlWriter(factory.createXMLStreamWriter((OutputStream) output, ""UTF-8""));
            }} else if (output instanceof Writer) {{
                return new XmlWriter(factory.createXMLStreamWriter((Writer) output));
            }}
            // InputStream passed as output is a C# idiom mismatch — return no-op writer
            return new XmlWriter();
        }} catch (XMLStreamException e) {{
            throw new RuntimeException(e);
        }}
    }}

    public void writeStartElement(String localName) {{
        try {{ if (writer != null) writer.writeStartElement(localName); }}
        catch (XMLStreamException e) {{ throw new RuntimeException(e); }}
    }}

    public void writeEndElement() {{
        try {{ if (writer != null) writer.writeEndElement(); }}
        catch (XMLStreamException e) {{ throw new RuntimeException(e); }}
    }}

    public void writeAttributeString(String localName, String value) {{
        try {{ if (writer != null) writer.writeAttribute(localName, value != null ? value : """"); }}
        catch (XMLStreamException e) {{ throw new RuntimeException(e); }}
    }}

    public void writeElementString(String localName, String value) {{
        try {{
            if (writer != null) {{
                writer.writeStartElement(localName);
                writer.writeCharacters(value != null ? value : """");
                writer.writeEndElement();
            }}
        }} catch (XMLStreamException e) {{ throw new RuntimeException(e); }}
    }}

    public void writeString(String text) {{
        try {{ if (writer != null) writer.writeCharacters(text != null ? text : """"); }}
        catch (XMLStreamException e) {{ throw new RuntimeException(e); }}
    }}

    public void writeComment(String text) {{
        try {{ if (writer != null) writer.writeComment(text != null ? text : """"); }}
        catch (XMLStreamException e) {{ throw new RuntimeException(e); }}
    }}

    public void writeEndDocument() {{
        try {{ if (writer != null) writer.writeEndDocument(); }}
        catch (XMLStreamException e) {{ throw new RuntimeException(e); }}
    }}

    public void flush() {{
        try {{ if (writer != null) writer.flush(); }}
        catch (XMLStreamException e) {{ throw new RuntimeException(e); }}
    }}

    @Override
    public void close() throws Exception {{
        if (writer != null) {{
            try {{ writer.flush(); }}  catch (XMLStreamException ignored) {{}}
            try {{ writer.close(); }}  catch (XMLStreamException ignored) {{}}
        }}
    }}
}}
";
        results.Add(new ConversionResult
        {
            Success = true,
            GeneratedCode = xmlWriterCode,
            FileName = "XmlWriter.java",
            Package = basePackage,
            Diagnostics = new List<Context.DiagnosticMessage>()
        });

        return results;
    }

    /// <summary>
    /// Generates Java wrapper classes for System.Text.Json.* types, backed by Jackson.
    /// </summary>
    private static List<ConversionResult> GenerateJsonWrappers(string basePackage)
    {
        var results = new List<ConversionResult>();

        // ── JsonSerializerOptions ─────────────────────────────────────────────────────
        var jsonOptionsCode = $@"package {basePackage};

/** Replacement for System.Text.Json.JsonSerializerOptions (generated by CSharpToJava converter). */
public class JsonSerializerOptions {{
    private boolean writeIndented = false;
    public boolean isWriteIndented()          {{ return writeIndented; }}
    public void    setWriteIndented(boolean v) {{ writeIndented = v; }}
}}
";
        results.Add(new ConversionResult
        {
            Success = true,
            GeneratedCode = jsonOptionsCode,
            FileName = "JsonSerializerOptions.java",
            Package = basePackage,
            Diagnostics = new List<Context.DiagnosticMessage>()
        });

        // ── JsonSerializer ────────────────────────────────────────────────────────────
        var jsonSerializerCode = $@"package {basePackage};

import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.SerializationFeature;

/** Replacement for System.Text.Json.JsonSerializer (generated by CSharpToJava converter). */
public class JsonSerializer {{
    private static final ObjectMapper MAPPER = new ObjectMapper()
        .configure(SerializationFeature.FAIL_ON_EMPTY_BEANS, false);

    @SuppressWarnings(""unchecked"")
    public static <T> String serialize(T value, Object options) {{
        try {{ return MAPPER.writeValueAsString(value); }}
        catch (Exception e) {{ throw new RuntimeException(e); }}
    }}

    @SuppressWarnings(""unchecked"")
    public static <T> String serialize(T value) {{
        try {{ return MAPPER.writeValueAsString(value); }}
        catch (Exception e) {{ throw new RuntimeException(e); }}
    }}

    @SuppressWarnings(""unchecked"")
    public static <T> T deserialize(String json, Class<T> clazz) {{
        try {{ return MAPPER.readValue(json, clazz); }}
        catch (Exception e) {{ throw new RuntimeException(e); }}
    }}

    public static Object deserialize(String json) {{
        try {{ return MAPPER.readValue(json, Object.class); }}
        catch (Exception e) {{ throw new RuntimeException(e); }}
    }}
}}
";
        results.Add(new ConversionResult
        {
            Success = true,
            GeneratedCode = jsonSerializerCode,
            FileName = "JsonSerializer.java",
            Package = basePackage,
            Diagnostics = new List<Context.DiagnosticMessage>()
        });

        return results;
    }

    /// <summary>
    /// Generates general-purpose Java utility stubs that replace .NET BCL types/methods.
    /// </summary>
    private static List<ConversionResult> GenerateUtilityClasses(string basePackage)
    {
        var results = new List<ConversionResult>();

        // ── MathHelper ─ TryParse stubs for numeric types ───────────────────────────
        var mathHelperCode = $@"package {basePackage};

/** Numeric TryParse helpers (generated by CSharpToJava converter, replacing System numeric TryParse). */
public class MathHelper {{
    public static boolean tryParseDouble(String s, DoubleHolder result) {{
        try {{ result.value = Double.parseDouble(s); return true; }}
        catch (NumberFormatException e) {{ return false; }}
    }}
    public static boolean tryParseFloat(String s, FloatHolder result) {{
        try {{ result.value = Float.parseFloat(s); return true; }}
        catch (NumberFormatException e) {{ return false; }}
    }}
    public static boolean tryParseInt(String s, IntHolder result) {{
        try {{ result.value = Integer.parseInt(s); return true; }}
        catch (NumberFormatException e) {{ return false; }}
    }}
    public static boolean tryParseLong(String s, LongHolder result) {{
        try {{ result.value = Long.parseLong(s); return true; }}
        catch (NumberFormatException e) {{ return false; }}
    }}
    public static boolean tryParseBool(String s, BoolHolder result) {{
        if (""true"".equalsIgnoreCase(s))  {{ result.value = true;  return true; }}
        if (""false"".equalsIgnoreCase(s)) {{ result.value = false; return true; }}
        return false;
    }}
}}
";
        results.Add(new ConversionResult
        {
            Success = true,
            GeneratedCode = mathHelperCode,
            FileName = "MathHelper.java",
            Package = basePackage,
            Diagnostics = new List<Context.DiagnosticMessage>()
        });

        // ── StringHelper ─ static String utilities missing from java.lang.String ───
        var stringHelperCode = $@"package {basePackage};

import java.util.Locale;

/** String utilities (generated by CSharpToJava converter, replacing System.String static methods). */
public class StringHelper {{
    public static boolean isNullOrEmpty(String s) {{
        return s == null || s.isEmpty();
    }}
    public static boolean isNullOrWhiteSpace(String s) {{
        return s == null || s.trim().isEmpty();
    }}
    /** Mirrors C# String.Compare(s1, s2) */
    public static int compare(String s1, String s2) {{
        if (s1 == null && s2 == null) return 0;
        if (s1 == null) return -1;
        if (s2 == null) return  1;
        return s1.compareTo(s2);
    }}
    /** Mirrors C# String.Compare(s1, s2, ignoreCase) */
    public static int compare(String s1, String s2, boolean ignoreCase) {{
        if (s1 == null && s2 == null) return 0;
        if (s1 == null) return -1;
        if (s2 == null) return  1;
        return ignoreCase ? s1.compareToIgnoreCase(s2) : s1.compareTo(s2);
    }}
    /** Mirrors C# String.Compare(s1, s2, ignoreCase, culture) */
    public static int compare(String s1, String s2, boolean ignoreCase, Object culture) {{
        return compare(s1, s2, ignoreCase);
    }}
    /** Mirrors C# String.Compare(s1, s2, StringComparison) */
    public static int compare(String s1, String s2, int comparison) {{
        return compare(s1, s2, comparison == 1 || comparison == 3 || comparison == 5);
    }}
}}
";
        results.Add(new ConversionResult
        {
            Success = true,
            GeneratedCode = stringHelperCode,
            FileName = "StringHelper.java",
            Package = basePackage,
            Diagnostics = new List<Context.DiagnosticMessage>()
        });

        // ── EnumHelper ─ Enum.TryParse / Enum.Parse stubs ────────────────────────────
        var enumHelperCode = $@"package {basePackage};

/** Enum parsing utilities (generated by CSharpToJava converter, replacing System.Enum methods). */
public class EnumHelper {{
    /** Mirrors Enum.TryParse&lt;T&gt;(name, ignoreCase, out T). Returns false (type-erasure limitation). */
    @SuppressWarnings(""unchecked"")
    public static <T> boolean tryParse(String name, boolean ignoreCase, ObjectHolder<T> result) {{
        return false; // Runtime: use T.valueOf(name) in calling code when enum class is known.
    }}
    /** Mirrors Enum.TryParse&lt;T&gt;(name, out T). */
    @SuppressWarnings(""unchecked"")
    public static <T> boolean tryParse(String name, ObjectHolder<T> result) {{
        return false;
    }}
    /** Mirrors Enum.Parse(type, name). */
    public static Object parse(Class<?> enumType, String name) {{
        try {{ return Enum.valueOf((Class<Enum>) enumType, name); }}
        catch (Exception e) {{ throw new IllegalArgumentException(""No enum constant: "" + name, e); }}
    }}
    /** Mirrors Enum.Parse(type, name, ignoreCase). */
    public static Object parse(Class<?> enumType, String name, boolean ignoreCase) {{
        return parse(enumType, name);
    }}
    /** Mirrors Enum.GetValues(typeof(T)). */
    @SuppressWarnings(""unchecked"")
    public static Object[] getValues(Class<?> enumType) {{
        return enumType.getEnumConstants();
    }}
}}
";
        results.Add(new ConversionResult
        {
            Success = true,
            GeneratedCode = enumHelperCode,
            FileName = "EnumHelper.java",
            Package = basePackage,
            Diagnostics = new List<Context.DiagnosticMessage>()
        });

        // ── FileHelper ─ Replaces System.IO.File static methods ──────────────────────
        var fileHelperCode = $@"package {basePackage};

import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;

/** File I/O helpers (generated by CSharpToJava converter, replacing System.IO.File). */
public class FileHelper {{
    public static String readAllText(String path) {{
        try {{ return new String(Files.readAllBytes(Paths.get(path)), StandardCharsets.UTF_8); }}
        catch (IOException e) {{ throw new UncheckedIOException(e); }}
    }}
    public static void writeAllText(String path, String content) {{
        try {{ Files.write(Paths.get(path), content.getBytes(StandardCharsets.UTF_8)); }}
        catch (IOException e) {{ throw new UncheckedIOException(e); }}
    }}
    public static InputStream openRead(String path) {{
        try {{ return new FileInputStream(path); }}
        catch (FileNotFoundException e) {{ throw new UncheckedIOException(e); }}
    }}
    public static TextReader openText(String path) {{
        try {{ return new TextReader(new InputStreamReader(new FileInputStream(path), StandardCharsets.UTF_8)); }}
        catch (FileNotFoundException e) {{ throw new UncheckedIOException(e); }}
    }}
    /** Mirrors File.Open(path, FileMode) — returns an InputStream for reading. */
    public static InputStream open(String path, int fileMode) {{
        return openRead(path);
    }}
    public static boolean exists(String path) {{ return new File(path).exists(); }}
    public static void delete(String path) {{ new File(path).delete(); }}
}}
";
        results.Add(new ConversionResult
        {
            Success = true,
            GeneratedCode = fileHelperCode,
            FileName = "FileHelper.java",
            Package = basePackage,
            Diagnostics = new List<Context.DiagnosticMessage>()
        });

        // ── FileMode ─ System.IO.FileMode enum constants ─────────────────────────────
        var fileModeCode = $@"package {basePackage};

/** Replacement for System.IO.FileMode (generated by CSharpToJava converter). */
public class FileMode {{
    public static final int CreateNew  = 1;
    public static final int Create     = 2;
    public static final int Open       = 3;
    public static final int OpenOrCreate = 4;
    public static final int Truncate   = 5;
    public static final int Append     = 6;

    public static int getCreateNew()    {{ return CreateNew; }}
    public static int getCreate()       {{ return Create; }}
    public static int getOpen()         {{ return Open; }}
    public static int getOpenOrCreate() {{ return OpenOrCreate; }}
    public static int getTruncate()     {{ return Truncate; }}
    public static int getAppend()       {{ return Append; }}
}}
";
        results.Add(new ConversionResult
        {
            Success = true,
            GeneratedCode = fileModeCode,
            FileName = "FileMode.java",
            Package = basePackage,
            Diagnostics = new List<Context.DiagnosticMessage>()
        });

        // ── CultureInfo ─ Stub for System.Globalization.CultureInfo ──────────────────
        var cultureInfoCode = $@"package {basePackage};

import java.util.Locale;

/** Stub for System.Globalization.CultureInfo (generated by CSharpToJava converter). */
public class CultureInfo {{
    private final Locale locale;

    public CultureInfo(String name) {{
        this.locale = Locale.forLanguageTag(name.replace('_', '-'));
    }}

    public static CultureInfo getCurrentCulture() {{
        return new CultureInfo(Locale.getDefault().toLanguageTag());
    }}
    public static void setCurrentCulture(Locale locale) {{
        // no-op: JVM locale is JVM-wide setting, not thread-local
    }}
    public static CultureInfo getInvariantCulture() {{
        return new CultureInfo(""en-US"");
    }}
    public Locale toLocale() {{ return locale; }}
    @Override public String toString() {{ return locale.toString(); }}
}}
";
        results.Add(new ConversionResult
        {
            Success = true,
            GeneratedCode = cultureInfoCode,
            FileName = "CultureInfo.java",
            Package = basePackage,
            Diagnostics = new List<Context.DiagnosticMessage>()
        });

        // ── LinkedListNode<T> ─ Mirrors C# System.Collections.Generic.LinkedListNode<T> ──
        var linkedListNodeCode = $@"package {basePackage};

/**
 * Thin node wrapper that mirrors C# LinkedListNode&lt;T&gt; (generated by CSharpToJava converter).
 * Provides getValue(), getNext(), getPrevious(). Fields are package-private for LinkedListWithNodes.
 */
public class LinkedListNode<T> {{
    T value;
    LinkedListNode<T> next;
    LinkedListNode<T> prev;

    public LinkedListNode(T value) {{ this.value = value; }}

    public T getValue() {{ return value; }}
    public void setValue(T value) {{ this.value = value; }}
    public LinkedListNode<T> getNext() {{ return next; }}
    public LinkedListNode<T> getPrevious() {{ return prev; }}
}}
";
        results.Add(new ConversionResult
        {
            Success = true,
            GeneratedCode = linkedListNodeCode,
            FileName = "LinkedListNode.java",
            Package = basePackage,
            Diagnostics = new List<Context.DiagnosticMessage>()
        });

        // ── LinkedListWithNodes<T> ─ C#-style doubly-linked list with node-based API ──
        var linkedListWithNodesCode = $@"package {basePackage};

import java.util.Iterator;
import java.util.NoSuchElementException;

/**
 * Doubly-linked list mirroring C# System.Collections.Generic.LinkedList&lt;T&gt;.
 * Provides node-based access: getFirst()/getLast() return LinkedListNode&lt;T&gt;.
 * Generated by CSharpToJava converter.
 */
public class LinkedListWithNodes<T> implements Iterable<T> {{
    private LinkedListNode<T> head;
    private LinkedListNode<T> tail;
    private int count;

    public int size() {{ return count; }}
    public boolean isEmpty() {{ return count == 0; }}

    public LinkedListNode<T> getFirst() {{ return head; }}
    public LinkedListNode<T> getLast() {{ return tail; }}

    public LinkedListNode<T> addFirst(T value) {{
        LinkedListNode<T> node = new LinkedListNode<>(value);
        node.next = head;
        node.prev = null;
        if (head != null) head.prev = node;
        head = node;
        if (tail == null) tail = node;
        count++;
        return node;
    }}

    public void addFirst(LinkedListNode<T> node) {{
        removeNode(node);
        node.next = head;
        node.prev = null;
        if (head != null) head.prev = node;
        head = node;
        if (tail == null) tail = node;
        count++;
    }}

    public LinkedListNode<T> addLast(T value) {{
        LinkedListNode<T> node = new LinkedListNode<>(value);
        node.prev = tail;
        node.next = null;
        if (tail != null) tail.next = node;
        tail = node;
        if (head == null) head = node;
        count++;
        return node;
    }}

    public void addLast(LinkedListNode<T> node) {{
        removeNode(node);
        node.prev = tail;
        node.next = null;
        if (tail != null) tail.next = node;
        tail = node;
        if (head == null) head = node;
        count++;
    }}

    public boolean add(T value) {{ addLast(value); return true; }}

    public boolean remove(LinkedListNode<T> node) {{ return removeNode(node); }}

    public boolean remove(Object value) {{
        for (LinkedListNode<T> n = head; n != null; n = n.next) {{
            if ((value == null && n.value == null) || (value != null && value.equals(n.value))) {{
                removeNode(n);
                return true;
            }}
        }}
        return false;
    }}

    private boolean removeNode(LinkedListNode<T> node) {{
        if (node.prev == null && node.next == null && head != node) return false;
        if (node.prev != null) node.prev.next = node.next; else head = node.next;
        if (node.next != null) node.next.prev = node.prev; else tail = node.prev;
        node.prev = null;
        node.next = null;
        count--;
        return true;
    }}

    public boolean contains(Object value) {{
        for (LinkedListNode<T> n = head; n != null; n = n.next) {{
            if ((value == null && n.value == null) || (value != null && value.equals(n.value)))
                return true;
        }}
        return false;
    }}

    public void clear() {{ head = null; tail = null; count = 0; }}

    @Override
    public Iterator<T> iterator() {{
        return new Iterator<T>() {{
            LinkedListNode<T> current = head;
            @Override public boolean hasNext() {{ return current != null; }}
            @Override public T next() {{
                if (current == null) throw new NoSuchElementException();
                T val = current.value;
                current = current.next;
                return val;
            }}
        }};
    }}
}}
";
        results.Add(new ConversionResult
        {
            Success = true,
            GeneratedCode = linkedListWithNodesCode,
            FileName = "LinkedListWithNodes.java",
            Package = basePackage,
            Diagnostics = new List<Context.DiagnosticMessage>()
        });

        // ── InvalidDataException ─ missing from standard Java ────────────────────────
        var invalidDataExCode = $@"package {basePackage};

/** Replacement for System.IO.InvalidDataException (generated by CSharpToJava converter). */
public class InvalidDataException extends RuntimeException {{
    public InvalidDataException(String message) {{ super(message); }}
    public InvalidDataException(String message, Throwable cause) {{ super(message, cause); }}
}}
";
        results.Add(new ConversionResult
        {
            Success = true,
            GeneratedCode = invalidDataExCode,
            FileName = "InvalidDataException.java",
            Package = basePackage,
            Diagnostics = new List<Context.DiagnosticMessage>()
        });

        // ── TextReader ─ Replacement for System.IO.TextReader with Peek() support ──
        var textReaderCode = $@"package {basePackage};

import java.io.*;
import java.nio.charset.StandardCharsets;

/**
 * Replacement for System.IO.TextReader (generated by CSharpToJava converter).
 * Adds peek() support missing from java.io.Reader.
 */
public class TextReader implements AutoCloseable {{
    private final PushbackReader inner;

    public TextReader(Reader r) {{
        this.inner = (r instanceof PushbackReader) ? (PushbackReader) r
                   : new PushbackReader(new BufferedReader(r));
    }}

    /** Mirrors C# TextReader.Peek() — reads next char without consuming it. Returns -1 at end. */
    public int peek() {{
        try {{
            int c = inner.read();
            if (c != -1) inner.unread(c);
            return c;
        }} catch (IOException e) {{ return -1; }}
    }}

    /** Reads next character, returns -1 at end of stream. */
    public int read() {{
        try {{ return inner.read(); }} catch (IOException e) {{ return -1; }}
    }}

    /** Reads text until end of line. Returns null at end of stream. */
    public String readLine() {{
        try {{
            StringBuilder sb = new StringBuilder();
            int c;
            while ((c = inner.read()) != -1 && c != '\n') {{
                if (c != '\r') sb.append((char) c);
            }}
            return (sb.length() > 0 || c != -1) ? sb.toString() : null;
        }} catch (IOException e) {{ return null; }}
    }}

    /** Reads all remaining text. */
    public String readToEnd() {{
        try {{
            char[] buf = new char[4096];
            StringBuilder sb = new StringBuilder();
            int n;
            while ((n = inner.read(buf)) != -1) sb.append(buf, 0, n);
            return sb.toString();
        }} catch (IOException e) {{ return """"; }}
    }}

    @Override
    public void close() throws Exception {{ inner.close(); }}
}}
";
        results.Add(new ConversionResult
        {
            Success = true,
            GeneratedCode = textReaderCode,
            FileName = "TextReader.java",
            Package = basePackage,
            Diagnostics = new List<Context.DiagnosticMessage>()
        });

        // ── ThreadHelper ─ Stub for System.Threading.Thread culture methods ──────────
        var threadHelperCode = $@"package {basePackage};

import java.util.Locale;

/**
 * Stub for System.Threading.Thread (generated by CSharpToJava converter).
 * Provides getCurrentCulture()/setCurrentCulture() used in C# thread-locale patterns.
 */
public class ThreadHelper {{
    private static final ThreadHelper INSTANCE = new ThreadHelper();

    /** Mirrors Thread.CurrentThread (returns a ThreadHelper proxy). */
    public static ThreadHelper currentThread() {{ return INSTANCE; }}

    /** Mirrors Thread.CurrentThread.CurrentCulture getter. */
    public CultureInfo getCurrentCulture() {{ return CultureInfo.getCurrentCulture(); }}

    /** Mirrors Thread.CurrentThread.CurrentCulture setter with CultureInfo. */
    public void setCurrentCulture(CultureInfo culture) {{ /* locale-setting is global in JVM */ }}

    /** Mirrors Thread.CurrentThread.CurrentCulture setter with Locale. */
    public void setCurrentCulture(Locale locale) {{ Locale.setDefault(locale); }}

    /** Mirrors Thread.CurrentThread.CurrentCulture setter with Object (type-erased). */
    public void setCurrentCulture(Object culture) {{ /* no-op */ }}

    /** Thread name (stub). */
    public String getName() {{ return java.lang.Thread.currentThread().getName(); }}

    /** No-op join (stub). */
    public void join() throws InterruptedException {{ }}

    /** Start (stub — not a real Thread). */
    public void start() {{ }}
}}
";
        results.Add(new ConversionResult
        {
            Success = true,
            GeneratedCode = threadHelperCode,
            FileName = "ThreadHelper.java",
            Package = basePackage,
            Diagnostics = new List<Context.DiagnosticMessage>()
        });

        // ── ArrayHelper ─ Parallel array sort (C# Array.Sort(keys[], items[])) ───────
        var arrayHelperCode = $@"package {basePackage};

import java.util.Arrays;

/**
 * Array utilities (generated by CSharpToJava converter).
 * Provides parallel-sort to replace C# Array.Sort(keys, items).
 */
public class ArrayHelper {{
    /** Mirrors C# Array.Sort(double[] keys, int[] items): sorts keys ascending, reorders items to match. */
    public static void sortParallel(double[] keys, int[] items) {{
        int n = keys.length;
        Integer[] indices = new Integer[n];
        for (int i = 0; i < n; i++) indices[i] = i;
        Arrays.sort(indices, (a, b) -> Double.compare(keys[a], keys[b]));
        double[] sk = new double[n]; int[] sv = new int[n];
        for (int i = 0; i < n; i++) {{ sk[i] = keys[indices[i]]; sv[i] = items[indices[i]]; }}
        System.arraycopy(sk, 0, keys, 0, n); System.arraycopy(sv, 0, items, 0, n);
    }}
    /** Mirrors C# Array.Sort(double[] keys, T[] items): sorts keys ascending, reorders items to match. */
    @SuppressWarnings(""unchecked"")
    public static <T> void sortParallel(double[] keys, T[] items) {{
        int n = keys.length;
        Integer[] indices = new Integer[n];
        for (int i = 0; i < n; i++) indices[i] = i;
        Arrays.sort(indices, (a, b) -> Double.compare(keys[a], keys[b]));
        double[] sk = new double[n]; T[] sv = (T[]) new Object[n];
        for (int i = 0; i < n; i++) {{ sk[i] = keys[indices[i]]; sv[i] = items[indices[i]]; }}
        System.arraycopy(sk, 0, keys, 0, n); System.arraycopy(sv, 0, items, 0, n);
    }}
}}
";
        results.Add(new ConversionResult
        {
            Success = true,
            GeneratedCode = arrayHelperCode,
            FileName = "ArrayHelper.java",
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
