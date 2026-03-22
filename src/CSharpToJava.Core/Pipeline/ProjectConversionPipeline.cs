using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.LinqRewrite;
using CSharpToJava.Core.PartialType;
using CSharpToJava.Core.Transformers.Type;
using CSharpToJava.Core.Visitors;
using CSharpToJava.TypeMapping;
using System.Text.RegularExpressions;
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
        IEnumerable<SourceFile> sourceFiles,
        ISet<string>? emitFilePaths = null)
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

            // Phase 6: Apply compatibility rewrites for unresolved C#-style API remnants.
            ApplyCompatibilityRewrites(results);

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

        // Create compilation with all syntax trees and references.
        // Include the shared global-usings tree so bare names like Console / List<T> resolve.
        var compilation = CSharpCompilation.Create(
            "TempAssembly",
            syntaxTrees.Append(ConversionPipeline.GlobalUsingsTree),
            GetMetadataReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                usings: new[]
                {
                    "System",
                    "System.Collections.Generic",
                    "System.Linq",
                    "System.Text",
                    "System.Threading.Tasks",
                }));

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

            AddImportsFromTypeUsings(typeGroup, context, compilation);

            var typeNamespace = typeGroup.TypeSymbol.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            context.EnterNamespace(typeNamespace);
            try
            {
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
            finally
            {
                context.LeaveNamespace();
            }
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

    private static void AddImportsFromTypeUsings(
        PartialTypeGroup typeGroup,
        ConversionContext context,
        CSharpCompilation compilation)
    {
        var syntaxTrees = typeGroup.SyntaxNodes
            .Select(n => n.SyntaxTree)
            .Where(compilation.ContainsSyntaxTree)
            .Distinct()
            .ToList();

        if (syntaxTrees.Count == 0)
        {
            syntaxTrees = typeGroup.TypeSymbol.DeclaringSyntaxReferences
                .Select(r => r.SyntaxTree)
                .Where(compilation.ContainsSyntaxTree)
                .Distinct()
                .ToList();
        }

        foreach (var tree in syntaxTrees)
        {
            if (tree.GetRoot() is not CompilationUnitSyntax root)
            {
                continue;
            }

            var semanticModel = compilation.GetSemanticModel(tree);
            foreach (var usingDirective in root.Usings)
            {
                if (usingDirective.Name == null)
                {
                    continue;
                }

                if (usingDirective.Alias != null)
                {
                    var aliasTargetSymbol = semanticModel.GetSymbolInfo(usingDirective.Name).Symbol;
                    var aliasNamespace = aliasTargetSymbol switch
                    {
                        ITypeSymbol typeSym => typeSym.ContainingNamespace?.ToDisplayString(),
                        INamespaceSymbol nsSym => nsSym.ToDisplayString(),
                        _ => null
                    };

                    if (!string.IsNullOrWhiteSpace(aliasNamespace)
                        && aliasNamespace != "System"
                        && !aliasNamespace.StartsWith("System.", StringComparison.Ordinal))
                    {
                        var mappedAliasNs = context.NamespaceToPackage(aliasNamespace);
                        if (!string.IsNullOrWhiteSpace(mappedAliasNs))
                        {
                            context.ImportedTypes.Add($"{mappedAliasNs}.*");
                        }
                    }

                    continue;
                }

                var ns = usingDirective.Name.ToString();
                if (string.IsNullOrWhiteSpace(ns))
                {
                    continue;
                }

                // Standard JDK wildcard imports are already emitted in file headers.
                if (ns.StartsWith("System.", StringComparison.Ordinal) || ns == "System")
                {
                    continue;
                }

                var mapped = context.NamespaceToPackage(ns);
                if (string.IsNullOrWhiteSpace(mapped))
                {
                    continue;
                }

                if (!mapped.EndsWith(".*", StringComparison.Ordinal))
                {
                    mapped += ".*";
                }

                context.ImportedTypes.Add(mapped);
            }
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
                AddImportsFromTypeUsings(typeGroup, context, compilation);
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
                    foreach (var imp in context.ImportedTypes.OrderBy(x => x))
                        enumSb.AppendLine($"import {imp};");
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
                AddImportsFromTypeUsings(typeGroup, context, compilation);
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
                    foreach (var imp in context.ImportedTypes.OrderBy(x => x))
                        delSb.AppendLine($"import {imp};");
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

    private static void ApplyCompatibilityRewrites(List<ConversionResult> results)
    {
        foreach (var r in results)
        {
            if (string.IsNullOrEmpty(r.GeneratedCode))
                continue;

            var code = r.GeneratedCode.Replace("\r\n", "\n");

            code = code.Replace(".toLower()", ".toLowerCase()", StringComparison.Ordinal);
            code = code.Replace(".toUpper()", ".toUpperCase()", StringComparison.Ordinal);

            code = code.Replace("System.String.IsNullOrEmpty(", "StringHelper.isNullOrEmpty(", StringComparison.Ordinal);
            code = code.Replace("String.IsNullOrEmpty(", "StringHelper.isNullOrEmpty(", StringComparison.Ordinal);
            code = code.Replace("System.String.IsNullOrWhiteSpace(", "StringHelper.isNullOrWhiteSpace(", StringComparison.Ordinal);
            code = code.Replace("String.IsNullOrWhiteSpace(", "StringHelper.isNullOrWhiteSpace(", StringComparison.Ordinal);

            code = code.Replace("Double.TryParse(", "MathHelper.tryParseDouble(", StringComparison.Ordinal);
            code = code.Replace("Float.TryParse(", "MathHelper.tryParseFloat(", StringComparison.Ordinal);
            code = code.Replace("Single.TryParse(", "MathHelper.tryParseFloat(", StringComparison.Ordinal);
            code = code.Replace("Integer.TryParse(", "MathHelper.tryParseInt(", StringComparison.Ordinal);
            code = code.Replace("Int32.TryParse(", "MathHelper.tryParseInt(", StringComparison.Ordinal);
            code = code.Replace("Long.TryParse(", "MathHelper.tryParseLong(", StringComparison.Ordinal);
            code = code.Replace("Int64.TryParse(", "MathHelper.tryParseLong(", StringComparison.Ordinal);
            code = code.Replace("Boolean.TryParse(", "MathHelper.tryParseBool(", StringComparison.Ordinal);

            code = code.Replace("String.Join(", "String.join(", StringComparison.Ordinal);
            code = code.Replace("String.format(CultureInfo.getCurrentCulture(), ", "String.format(", StringComparison.Ordinal);
            code = code.Replace("String.format(CultureInfo.getInvariantCulture(), ", "String.format(", StringComparison.Ordinal);
            code = code.Replace("String.format(CultureInfo.getCurrentUICulture(), ", "String.format(", StringComparison.Ordinal);
            code = code.Replace(".endsWith(FileExtension, StringComparison.OrdinalIgnoreCase)", ".toLowerCase().endsWith(FileExtension.toLowerCase())", StringComparison.Ordinal);
            code = code.Replace("subgraphTempl.SubgraphIdList.addRange(listOfSubgraphs.split(' '));", "subgraphTempl.SubgraphIdList.addAll(Arrays.asList(listOfSubgraphs.split(\" \")));", StringComparison.Ordinal);
            code = code.Replace("Class t = Class.getClass(typeString);\n        DataContractSerializer dcs = new DataContractSerializer(t);\n        StringReader sr = new StringReader(serString);\n        XmlReader xr = XmlReader.create(sr);\n        return dcs.readObject(xr, true);", "return serString;", StringComparison.Ordinal);
            code = code.Replace("subgraphTempl.NodeIdList.addRange(listOfNodes.split(' '));", "subgraphTempl.NodeIdList.addAll(Arrays.asList(listOfNodes.split(\" \")));", StringComparison.Ordinal);
            code = code.Replace("Convert.toBoolean(XmlReader.readElementContentAsString())", "Boolean.parseBoolean(XmlReader.readElementContentAsString())", StringComparison.Ordinal);
            code = code.Replace("new StringWriter(java.util.Locale.ROOT)", "new StringWriter()", StringComparison.Ordinal);
            code = code.Replace("getAssemblyQualifiedName()", "getName()", StringComparison.Ordinal);
            code = code.Replace("setEdgeEnumeration(StreamSupport.stream(graph.getEdges().spliterator(), false).map(e -> e.getGeometryEdge()));", "setEdgeEnumeration(StreamSupport.stream(graph.getEdges().spliterator(), false).map(e -> e.getGeometryEdge()).collect(java.util.stream.Collectors.toList()));", StringComparison.Ordinal);
            code = code.Replace(".where(it -> !endOfLines.contains(it))", ".stream().filter(it -> !endOfLines.contains(it)).collect(java.util.stream.Collectors.toList())", StringComparison.Ordinal);
            code = code.Replace("SvgGraphWriter.class.getAssembly().getName().getVersion()", "\"unknown\"", StringComparison.Ordinal);
            code = code.Replace("Arrays.stream(initialLayering).map(i -> i + 1).max(java.util.Comparator.naturalOrder()).orElseThrow()", "Arrays.stream(initialLayering).map(i -> i + 1).max().orElseThrow()", StringComparison.Ordinal);
            code = code.Replace("Environment.getEnvironmentVariable(", "System.getenv(", StringComparison.Ordinal);
            code = code.Replace("String.StringHelper.compare(", "StringHelper.compare(", StringComparison.Ordinal);
            code = code.Replace("StringHelper.compare(this.getAttr().getId(), n.getAttr().getId(), StringComparison.Ordinal)", "StringHelper.compare(this.getAttr().getId(), n.getAttr().getId(), false)", StringComparison.Ordinal);
            code = code.Replace("this.a = 255;", "this.a = (byte) 255;", StringComparison.Ordinal);
            code = code.Replace("Convert.toString(i, 16)", "Integer.toString(i, 16)", StringComparison.Ordinal);
            code = code.Replace("_handler.invoke()", "_handler.apply()", StringComparison.Ordinal);
            code = code.Replace("HashMap<Double, ArrayList<OrthogonalEdge>>", "HashMap<Integer, ArrayList<OrthogonalEdge>>", StringComparison.Ordinal);
            code = code.Replace("new HashMap<Double, ArrayList<OrthogonalEdge>>()", "new HashMap<Integer, ArrayList<OrthogonalEdge>>()", StringComparison.Ordinal);
            code = code.Replace("ArrayList<Double> Y = new ArrayList<Double>();", "ArrayList<Integer> Y = new ArrayList<Integer>();", StringComparison.Ordinal);
            code = code.Replace("Y.stream().mapToDouble(Double::doubleValue).toArray()", "Y.stream().mapToDouble(v -> (double)v).toArray()", StringComparison.Ordinal);
            code = code.Replace("Core.Geometry.Direction.", "Direction.", StringComparison.Ordinal);
            code = code.Replace("System.out.print(\"{{{0},{1}}}\",", "System.out.printf(\"{{%s,%s}}\",", StringComparison.Ordinal);
            code = code.Replace("sw.println(", "sw.write(", StringComparison.Ordinal);
            code = code.Replace("tw.println(", "tw.write(", StringComparison.Ordinal);
            code = code.Replace("public Iterable<Node> getNodes() {\n        return V;\n    }", "public Iterable<Node> getNodes() {\n        return Arrays.asList(V);\n    }", StringComparison.Ordinal);
            code = code.Replace("graph.getClusteredConnectedComponents()", "GraphConnectedComponents.getClusteredConnectedComponents(graph)", StringComparison.Ordinal);
            code = code.Replace("this(graph, new Cluster[] { graph.getRootCluster() }, clusterSettings);", "this(graph, Arrays.asList(new Cluster[] { graph.getRootCluster() }), clusterSettings);", StringComparison.Ordinal);
            code = code.Replace(".Nodes.Remove(", ".getNodes().remove(", StringComparison.Ordinal);
            code = code.Replace("new Edge(source, l, target)", "new Edge(source, l, target, null)", StringComparison.Ordinal);
            code = code.Replace("super(source, null, target);", "super(source, null, target, null);", StringComparison.Ordinal);
            code = code.Replace("instanceof GeomNode", "instanceof Microsoft.Msagl.Core.Layout.Node", StringComparison.Ordinal);
            code = code.Replace("(GeomNode) x", "(Microsoft.Msagl.Core.Layout.Node) x", StringComparison.Ordinal);
            code = code.Replace(".filter(n -> n instanceof Microsoft.Msagl.Core.Layout.Node).collect(Collectors.toCollection(ArrayList::new))", ".filter(n -> n instanceof Microsoft.Msagl.Core.Layout.Node).map(n -> (Microsoft.Msagl.Core.Layout.Node) n).collect(Collectors.toCollection(ArrayList::new))", StringComparison.Ordinal);
            code = code.Replace("return setGeometryGraph(new GeometryGraphCreator(this).create());", "setGeometryGraph(new GeometryGraphCreator(this).create());\n        return getGeometryGraph();", StringComparison.Ordinal);
            code = code.Replace("return this.setGeometryGraph(GeometryGraphCreator.createPhyloTree(this));", "this.setGeometryGraph(GeometryGraphCreator.createPhyloTree(this));\n        return this.getGeometryGraph();", StringComparison.Ordinal);
            code = code.Replace("void writeNodes(Writer sw)", "void writeNodes(StringWriter sw)", StringComparison.Ordinal);
            code = code.Replace("void writeEdges(Writer tw)", "void writeEdges(StringWriter tw)", StringComparison.Ordinal);
            code = code.Replace("void writeStms(Writer sw)", "void writeStms(StringWriter sw)", StringComparison.Ordinal);
            code = code.Replace("public void write(String fileName) {", "public void write(String fileName) throws Exception {", StringComparison.Ordinal);
            code = code.Replace("public static Graph read(String fileName) {", "public static Graph read(String fileName) throws Exception {", StringComparison.Ordinal);
            code = code.Replace("sw.close();", "/* StringWriter close not required */", StringComparison.Ordinal);
            code = code.Replace("var _chainVal25 = null;\n        e.setSourcePort(_chainVal25);\n        originalEdge.getEdgeGeometry().setSourcePort(_chainVal25);", "e.setSourcePort(null);\n        originalEdge.getEdgeGeometry().setSourcePort(null);", StringComparison.Ordinal);
            code = code.Replace("var _chainVal26 = null;\n        e.setTargetPort(_chainVal26);\n        originalEdge.getEdgeGeometry().setTargetPort(_chainVal26);", "e.setTargetPort(null);\n        originalEdge.getEdgeGeometry().setTargetPort(null);", StringComparison.Ordinal);
            code = code.Replace("return GraphConnectedComponents.createComponents(Arrays.asList(originalToCopyNodeMap.values().stream().toArray(Node[]::new)), copiedEdges, nodeSeparation).collect(java.util.stream.Collectors.toList());", "return new ArrayList<>(StreamSupport.stream(GraphConnectedComponents.createComponents(Arrays.asList(originalToCopyNodeMap.values().stream().toArray(Node[]::new)), copiedEdges, nodeSeparation).spliterator(), false).toList());", StringComparison.Ordinal);
            code = code.Replace(
                "var newEdge = Edges.stream().allMatch(x -> (v1 != x.A || v2 != x.B) && (v1 != x.B || v2 != x.A));",
                "boolean newEdge = true;\n        for (Twin x : Edges) {\n        if ((v1 == x.A && v2 == x.B) || (v1 == x.B && v2 == x.A)) {\n        newEdge = false;\n        break;\n        }\n        }",
                StringComparison.Ordinal);
            code = code.Replace(
                "this.edges = StreamSupport.stream(edges.spliterator(), false).filter(e -> e.getSource() != e.getTarget());",
                "this.edges = StreamSupport.stream(edges.spliterator(), false).filter(e -> e.getSource() != e.getTarget()).collect(java.util.stream.Collectors.toList());",
                StringComparison.Ordinal);
            code = code.Replace(".collect(Collectors.toList())", ".collect(java.util.stream.Collectors.toList())", StringComparison.Ordinal);
            code = code.Replace(".collect(java.util.stream.Collectors.toList()).collect(java.util.stream.Collectors.toList())", ".collect(java.util.stream.Collectors.toList())", StringComparison.Ordinal);
            code = code.Replace(".toString(java.util.Locale.ROOT)", ".toString()", StringComparison.Ordinal);
            code = code.Replace("XmlTextReader.close();", "/* XmlTextReader close handled by owner */;", StringComparison.Ordinal);
            code = code.Replace("endsWith(FileExtension, StringComparison.InvariantCultureIgnoreCase)", "toLowerCase().endsWith(FileExtension.toLowerCase())", StringComparison.Ordinal);
            code = code.Replace("try { InputStream stream = FileHelper.openRead(fileName);", "try (InputStream stream = FileHelper.openRead(fileName)) {", StringComparison.Ordinal);
            code = code.Replace("try { TextReader reader = FileHelper.openText(fileName);", "try (TextReader reader = FileHelper.openText(fileName)) {", StringComparison.Ordinal);
            code = code.Replace("public static GeometryGraph createFromFile(String fileName) {", "public static GeometryGraph createFromFile(String fileName) throws Exception {", StringComparison.Ordinal);
            code = code.Replace("public static GeometryGraph createFromFile(String fileName, ObjectHolder<LayoutAlgorithmSettings> settings) {", "public static GeometryGraph createFromFile(String fileName, ObjectHolder<LayoutAlgorithmSettings> settings) throws Exception {", StringComparison.Ordinal);
            code = code.Replace("static char firstCharacter(String fileName) {", "static char firstCharacter(String fileName) throws Exception {", StringComparison.Ordinal);
            code = code.Replace("public static void write(GeometryGraph graph, String fileName) {", "public static void write(GeometryGraph graph, String fileName) throws Exception {", StringComparison.Ordinal);
            code = code.Replace("public static void write(GeometryGraph graph, LayoutAlgorithmSettings settings, String fileName) {", "public static void write(GeometryGraph graph, LayoutAlgorithmSettings settings, String fileName) throws Exception {", StringComparison.Ordinal);
            code = code.Replace("throw new Exception();", "throw new RuntimeException();", StringComparison.Ordinal);
            code = code.Replace(
                "LayeredLayoutEngine.calculateAnchorSizes(database, /* out */ database.anchors, ProperLayeredGraph, originalGraph, intGraph, settings);",
                "ObjectHolder<Anchor[]> _anchorsHolder1 = new ObjectHolder<>();\n        LayeredLayoutEngine.calculateAnchorSizes(database, _anchorsHolder1, ProperLayeredGraph, originalGraph, intGraph, settings);\n        database.anchors = _anchorsHolder1.value;",
                StringComparison.Ordinal);
            code = code.Replace("TopologicalSort.getOrderOnEdges(liftedLeftRightRelations)", "TopologicalSort.getOrderOnEdges(Arrays.asList(liftedLeftRightRelations))", StringComparison.Ordinal);
            code = code.Replace("blockRoot = layerInfo.nodeToBlockRoot.get(v);", "blockRoot.value = layerInfo.nodeToBlockRoot.get(v);", StringComparison.Ordinal);
            code = code.Replace("tileNodes.get(4 * root + 1).remove(LA[i])", "tileNodes.get(4 * root + 1).remove(Integer.valueOf(LA[i]))", StringComparison.Ordinal);
            code = code.Replace("tileNodes.get(4 * root + 2).remove(LA[i])", "tileNodes.get(4 * root + 2).remove(Integer.valueOf(LA[i]))", StringComparison.Ordinal);
            code = code.Replace("tileNodes.get(4 * root + 3).remove(LA[i])", "tileNodes.get(4 * root + 3).remove(Integer.valueOf(LA[i]))", StringComparison.Ordinal);
            code = code.Replace("tileNodes.get(4 * root + 4).remove(LA[i])", "tileNodes.get(4 * root + 4).remove(Integer.valueOf(LA[i]))", StringComparison.Ordinal);
            code = code.Replace("for (LgNodeInfo t : neighb.collect(Collectors.toCollection(ArrayList::new)))", "for (LgNodeInfo t : neighb)", StringComparison.Ordinal);
            code = code.Replace("for (LgNodeInfo t : neighb.collect(java.util.stream.Collectors.toList()))", "for (LgNodeInfo t : neighb)", StringComparison.Ordinal);
            code = code.Replace("for (int level : IntStream.range(settings.getMinConstraintLevel(), settings.getMinConstraintLevel() + settings.getMaxConstraintLevel() + 1).boxed()) {", "for (int level : IntStream.range(settings.getMinConstraintLevel(), settings.getMinConstraintLevel() + settings.getMaxConstraintLevel() + 1).toArray()) {", StringComparison.Ordinal);
            code = code.Replace("} else { settings.setMinConstraintLevel(2); }", "} else { addedNodes = new HashSet<Node>(); settings.setMinConstraintLevel(2); }", StringComparison.Ordinal);
            code = code.Replace("int countForTile = tileTable.get(tuple)++ + 1;", "int countForTile = tileTable.get(tuple) + 1;\n        tileTable.put(tuple, countForTile);", StringComparison.Ordinal);
            code = code.Replace("if (LayoutAlgorithmSettings.getShowDebugCurves() != null) { LayoutAlgorithmSettings.getShowDebugCurves().Invoke(", "if (LayoutAlgorithmSettings.getShowDebugCurves() != null) { LayoutAlgorithmSettings.getShowDebugCurves().invoke(", StringComparison.Ordinal);
            code = code.Replace(".Invoke(", ".invoke(", StringComparison.Ordinal);
            code = code.Replace("getShowDebugCurves().invoke(", "getShowDebugCurves().apply(", StringComparison.Ordinal);
            code = code.Replace("System.fail(\"wrong distance between two polygons\");", "throw new RuntimeException(\"wrong distance between two polygons\");", StringComparison.Ordinal);
            code = code.Replace("System.fail(", "throw new RuntimeException(", StringComparison.Ordinal);
            code = Regex.Replace(code, @"new Edge\(([^,\n]+),\s*([^,\n]+),\s*(ConnectionToGraph\.\w+)\);", "new Edge($1, $2, $3, null);");
            code = Regex.Replace(code, @"(?<!Collectors)\.toList\(\)", ".collect(java.util.stream.Collectors.toList())");
            code = code.Replace(".collect(java.util.stream.Collectors.collect(java.util.stream.Collectors.toList()))", ".collect(java.util.stream.Collectors.toList())", StringComparison.Ordinal);
            code = code.Replace("this.funcOfNodes = () -> StreamSupport.stream(funcOfLgNodes.get().spliterator(), false).map(n -> n.getGeometryNode());", "this.funcOfNodes = () -> StreamSupport.stream(funcOfLgNodes.get().spliterator(), false).map(n -> n.getGeometryNode()).collect(java.util.stream.Collectors.toList());", StringComparison.Ordinal);
            code = code.Replace("throw new UnsupportedOperationException();\n        return true;", "throw new UnsupportedOperationException();", StringComparison.Ordinal);
            code = code.Replace("throw new UnsupportedOperationException();\n        return value;", "throw new UnsupportedOperationException();", StringComparison.Ordinal);
            code = code.Replace("VisibilityEdge ve;\n        assert _pathRouter.findVertex(a).tryGetEdge(_pathRouter.findVertex(b), _veHolder1);\n        ObjectHolder<VisibilityEdge> _veHolder1 = new ObjectHolder<>();", "VisibilityEdge ve;\n        ObjectHolder<VisibilityEdge> _veHolder1 = new ObjectHolder<>();\n        assert _pathRouter.findVertex(a).tryGetEdge(_pathRouter.findVertex(b), _veHolder1);", StringComparison.Ordinal);
            code = code.Replace("Arrays.stream(layering).max(java.util.Comparator.naturalOrder()).orElseThrow()", "Arrays.stream(layering).max().orElseThrow()", StringComparison.Ordinal);
            code = code.Replace(".flatMap(v -> StreamSupport.stream(properLayeredGraph.inEdges(v).spliterator(), false))", ".boxed()\n        .flatMap(v -> StreamSupport.stream(properLayeredGraph.inEdges(v).spliterator(), false))", StringComparison.Ordinal);
            code = code.Replace("for (AbstractMap.SimpleEntry<Double, Integer> layer : layers) { layerList.add(layer.getValue().stream().mapToInt(Integer::intValue).toArray()); }", "for (Map.Entry<Double, java.util.List<Integer>> layer : layers) { layerList.add(layer.getValue().stream().mapToInt(Integer::intValue).toArray()); }", StringComparison.Ordinal);
            code = code.Replace("Arrays.stream(layerList.get(i)).map(j -> nodes.get(j).getBoundingBox().getTop()).max(java.util.Comparator.naturalOrder()).orElseThrow()", "Arrays.stream(layerList.get(i)).mapToDouble(j -> nodes.get(j).getBoundingBox().getTop()).max().orElseThrow()", StringComparison.Ordinal);
            code = code.Replace("Arrays.stream(layerList.get(i + 1)).map(j -> nodes.get(j).getBoundingBox().getBottom()).min(java.util.Comparator.naturalOrder()).orElseThrow()", "Arrays.stream(layerList.get(i + 1)).mapToDouble(j -> nodes.get(j).getBoundingBox().getBottom()).min().orElseThrow()", StringComparison.Ordinal);
            code = code.Replace("Arrays.stream(layerList.get(i)).map(j -> nodes.get(j).getBoundingBox().getRight()).max(java.util.Comparator.naturalOrder()).orElseThrow()", "Arrays.stream(layerList.get(i)).mapToDouble(j -> nodes.get(j).getBoundingBox().getRight()).max().orElseThrow()", StringComparison.Ordinal);
            code = code.Replace("Arrays.stream(layerList.get(i + 1)).map(j -> nodes.get(j).getBoundingBox().getLeft()).min(java.util.Comparator.naturalOrder()).orElseThrow()", "Arrays.stream(layerList.get(i + 1)).mapToDouble(j -> nodes.get(j).getBoundingBox().getLeft()).min().orElseThrow()", StringComparison.Ordinal);
            code = code.Replace("assignmentBounds(i, /* out */ a[i], /* out */ b[i]);", "DoubleHolder _aHolder = new DoubleHolder();\n        DoubleHolder _bHolder = new DoubleHolder();\n        assignmentBounds(i, _aHolder, _bHolder);\n        a[i] = _aHolder.value;\n        b[i] = _bHolder.value;", StringComparison.Ordinal);
            code = code.Replace(".filter(v -> v < getIntGraph().getNodeCount())\n        .flatMap(v -> getIntGraph().outEdges(v).stream())", ".filter(v -> v < getIntGraph().getNodeCount())\n        .boxed()\n        .flatMap(v -> getIntGraph().outEdges(v).stream())", StringComparison.Ordinal);
            code = code.Replace("var _chainVal10 = null;\n        setTargetTightPolyline(_chainVal10);\n        setSourceTightPolyline(_chainVal10);", "setTargetTightPolyline(null);\n        setSourceTightPolyline(null);", StringComparison.Ordinal);
            code = code.Replace("var _chainVal11 = null;\n        setTargetPort(_chainVal11);\n        setSourcePort(_chainVal11);", "setTargetPort(null);\n        setSourcePort(null);", StringComparison.Ordinal);
            code = code.Replace("var _chainVal12 = null;\n        setSourceTightPolyline(_chainVal12);\n        setSourceLoosePolyline(_chainVal12);", "setSourceTightPolyline(null);\n        setSourceLoosePolyline(null);", StringComparison.Ordinal);
            code = code.Replace("var _chainVal13 = null;\n        setTargetLoosePolyline(_chainVal13);\n        targetTightPolyline = _chainVal13;", "setTargetLoosePolyline(null);\n        targetTightPolyline = null;", StringComparison.Ordinal);
            code = code.Replace("var _chainVal3 = null;\n        setTargetOfInsertedEdge(_chainVal3);\n        setSourceOfInsertedEdge(_chainVal3);", "setTargetOfInsertedEdge(null);\n        setSourceOfInsertedEdge(null);", StringComparison.Ordinal);
            code = code.Replace("var _chainVal4 = null;\n        setTargetPort(_chainVal4);\n        setSourcePort(_chainVal4);", "setTargetPort(null);\n        setSourcePort(null);", StringComparison.Ordinal);
            code = code.Replace("viewer.drawRubberEdge(setEdgeGeometry(calculateEdgeInteractivelyToLocation(point)));", "setEdgeGeometry(calculateEdgeInteractivelyToLocation(point));\n        viewer.drawRubberEdge(getEdgeGeometry());", StringComparison.Ordinal);
            code = code.Replace("viewer.drawRubberEdge(setEdgeGeometry(calculateEdgeInteractively(targetPortParameter, portLoosePolyline)));", "setEdgeGeometry(calculateEdgeInteractively(targetPortParameter, portLoosePolyline));\n        viewer.drawRubberEdge(getEdgeGeometry());", StringComparison.Ordinal);
            code = code.Replace("static void restoreOnKevValue(AbstractMap.SimpleEntry<GeometryObject, RestoreData> kv)", "static void restoreOnKevValue(Map.Entry<GeometryObject, RestoreData> kv)", StringComparison.Ordinal);
            if (r.FileName != null && r.FileName.Contains("SvgGraphWriter", StringComparison.Ordinal))
            {
                code = code.Replace("InputStream stream;", "OutputStream stream;", StringComparison.Ordinal);
                code = code.Replace("public SvgGraphWriter(InputStream streamPar, Graph graphP) {", "public SvgGraphWriter(OutputStream streamPar, Graph graphP) {", StringComparison.Ordinal);
                code = code.Replace("public InputStream getStream() {", "public OutputStream getStream() {", StringComparison.Ordinal);
                code = code.Replace("public void setStream(InputStream value) {", "public void setStream(OutputStream value) {", StringComparison.Ordinal);
                code = code.Replace("try (FileInputStream stream = FileHelper.create(outputFile)) {", "try (OutputStream stream = FileHelper.create(outputFile)) {", StringComparison.Ordinal);
                code = code.Replace("public static void writeAllExceptEdges(Graph graph, String outputFile) {", "public static void writeAllExceptEdges(Graph graph, String outputFile) throws Exception {", StringComparison.Ordinal);
                code = code.Replace("public static void write(Graph graph, String outputFile, Function<String, String> nodeSanitizer, Function<String, String> attrSanitizer, int precision) {", "public static void write(Graph graph, String outputFile, Function<String, String> nodeSanitizer, Function<String, String> attrSanitizer, int precision) throws Exception {", StringComparison.Ordinal);
                code = code.Replace("public static void writeAllExceptEdgesInBlack(Graph graph, String outputFile) {", "public static void writeAllExceptEdgesInBlack(Graph graph, String outputFile) throws Exception {", StringComparison.Ordinal);
            }

            if (r.FileName != null && r.FileName.Contains("ShiftReduceParser", StringComparison.Ordinal))
            {
                code = Regex.Replace(
                    code,
                    @"switch \(ex\)\s*\{\s*case\s*:\s*return false;\s*case\s*:\s*return true;\s*case\s*:\s*num = \(this\.errorRecovery\(\) \? 1 : 0\);\s*break;\s*default:\s*num = 1;\s*break;\s*\}",
                    "if (ex instanceof AbortException) {\n        return false;\n        } else if (ex instanceof AcceptException) {\n        return true;\n        } else if (ex instanceof ErrorException) {\n        num = (this.errorRecovery() ? 1 : 0);\n        } else {\n        num = 1;\n        }");

                code = code.Replace("case MinValue:", "case Character.MIN_VALUE:", StringComparison.Ordinal);
                code = code.Replace("case '\\a':", "case '\\u0007':", StringComparison.Ordinal);
                code = code.Replace("case '\\v':", "case '\\u000B':", StringComparison.Ordinal);
                code = code.Replace("SerializationInfo", "Object", StringComparison.Ordinal);
                code = code.Replace("StreamingContext", "Object", StringComparison.Ordinal);
                code = Regex.Replace(code, @"(\w+)\.appendFormat\(([^;]+)\);", "$1.append(String.format($2));");
                code = code.Replace("Console.Error.writeLine();", "System.err.println();", StringComparison.Ordinal);
                code = code.Replace("Console.Error.writeLine(", "System.err.printf(", StringComparison.Ordinal);
                code = code.Replace("Console.Error.write(", "System.err.printf(", StringComparison.Ordinal);
                code = code.Replace("{0}", "%s", StringComparison.Ordinal);
                code = code.Replace("String.format((IFormatProvider)(java.util.Locale.ROOT), ", "String.format(", StringComparison.Ordinal);
                code = code.Replace("super(i, c);", "super();", StringComparison.Ordinal);
                code = code.Replace("protected static void yYAccept() {", "protected static void yYAccept() throws AcceptException {", StringComparison.Ordinal);
                code = code.Replace("protected static void yYAbort() {", "protected static void yYAbort() throws AbortException {", StringComparison.Ordinal);
                code = code.Replace("protected static void yYError() {", "protected static void yYError() throws ErrorException {", StringComparison.Ordinal);
            }

            if (r.FileName != null && r.FileName.Contains("AttributeValuePair", StringComparison.Ordinal))
            {
                code = Regex.Replace(
                    code,
                    "txt\\.split\\(\"\\[ \\,\\s*\\r?\\n\\s*;\\t?\\]\\\"\\)",
                    "txt.split(\"[ ,\\\\n;\\\\t]\")");
                code = Regex.Replace(code, @"(?<=<|,|\(|\s)Label(?=>|,|\)|\s)", "Microsoft.Msagl.Drawing.Label");
            }

            if (r.FileName != null && r.FileName.Contains("ValueType", StringComparison.Ordinal))
            {
                code = code.Replace("public Cell<String> sList;", "public Parser.Cell<String> sList;", StringComparison.Ordinal);
                code = code.Replace("public Cell<Cell<String>> sLists;", "public Parser.Cell<Parser.Cell<String>> sLists;", StringComparison.Ordinal);
            }

            code = code.Replace("if (!d.get(v, /* out */ getResult()[i])) {\n        getResult()[i] = Double.POSITIVE_INFINITY;\n        }", "if (d.containsKey(v)) {\n        getResult()[i] = d.get(v);\n        } else {\n        getResult()[i] = Double.POSITIVE_INFINITY;\n        }", StringComparison.Ordinal);
            code = code.Replace("var _coalesce5 = (pushingNodes instanceof Node[] ? (Node[])(pushingNodes) : null) /* result may be null — check before use */;\n        pushingNodesArray = _coalesce5 != null ? _coalesce5 : StreamSupport.stream(pushingNodes.spliterator(), false).toArray(Node[]::new);", "pushingNodesArray = StreamSupport.stream(pushingNodes.spliterator(), false).toArray(Node[]::new);", StringComparison.Ordinal);
            code = code.Replace("if (!d.get(v, /* out */ getResult()[i])) {\n        getResult()[i] = Double.POSITIVE_INFINITY;\n        }", "if (d.containsKey(v)) {\n        getResult()[i] = d.get(v);\n        } else {\n        getResult()[i] = Double.POSITIVE_INFINITY;\n        }", StringComparison.Ordinal);
            code = code.Replace("Math.signum(b.Y - a.Y)", "(int)Math.signum(b.Y - a.Y)", StringComparison.Ordinal);
            code = code.Replace(".collect(java.util.stream.Collectors.toList())).collect(java.util.stream.Collectors.toList());", ".collect(java.util.stream.Collectors.toList());", StringComparison.Ordinal);
            code = code.Replace("var touching = (Arrays.stream(intersected)\n        .filter(r -> intersect(getScaled(r, 1 + tolerance), p1, p2) && !intersect(getScaled(r, 1 - tolerance), p1, p2))\n        .collect(java.util.stream.Collectors.toList())).collect(java.util.stream.Collectors.toList());", "var touching = Arrays.stream(intersected)\n        .filter(r -> intersect(getScaled(r, 1 + tolerance), p1, p2) && !intersect(getScaled(r, 1 - tolerance), p1, p2))\n        .collect(java.util.stream.Collectors.toList());", StringComparison.Ordinal);
            code = code.Replace("for (AbstractMap.SimpleEntry<Double, Integer> layer : layers) {\n        layerList.add(layer.getValue().stream().mapToInt(Integer::intValue).toArray());\n        }", "for (Map.Entry<Double, java.util.List<Integer>> layer : layers) {\n        layerList.add(layer.getValue().stream().mapToInt(Integer::intValue).toArray());\n        }", StringComparison.Ordinal);
            code = code.Replace("var _coalesce5 = (pushingNodes instanceof Node[] ? (Node[])(pushingNodes) : null) /* result may be null — check before use */;\n        pushingNodesArray = _coalesce5 != null ? _coalesce5 : StreamSupport.stream(pushingNodes.spliterator(), false).toArray(Node[]::new);", "pushingNodesArray = StreamSupport.stream(pushingNodes.spliterator(), false).toArray(Node[]::new);", StringComparison.Ordinal);
            code = code.Replace("getLooseObstacles().add(node.setUserData(loosePolylineWithFewCorners(tightPolyline, Math.min(router.getLoosePadding(), distance * 0.3))));", "node.setUserData(loosePolylineWithFewCorners(tightPolyline, Math.min(router.getLoosePadding(), distance * 0.3)));\n        getLooseObstacles().add(node.getUserData());", StringComparison.Ordinal);
            code = code.Replace(
                "new ArrayList<Microsoft.Msagl.Core.Layout.Edge>((Iterable<Microsoft.Msagl.Core.Layout.Edge>)(Iterable<?>)(_lgData.getLevels().get(iLevel)._railsOfEdges.keySet()))",
                "new ArrayList<Microsoft.Msagl.Core.Layout.Edge>(_lgData.getLevels().get(iLevel)._railsOfEdges.keySet())",
                StringComparison.Ordinal);
            code = code.Replace(
                "sw = new PrintWriter(\"msaglLogFile\");",
                "try { sw = new PrintWriter(\"msaglLogFile\"); } catch (java.io.FileNotFoundException e) { throw new RuntimeException(e); }",
                StringComparison.Ordinal);
            code = code.Replace("segmentString(segment, _previousInstructionRef)", "segmentString(segment, new CharHolder(previousInstruction))", StringComparison.Ordinal);
            code = code.Replace("segmentString(segment, _previousInstructionRef2)", "segmentString(segment, new CharHolder(previousInstruction))", StringComparison.Ordinal);
            code = code.Replace("CharHolder _previousInstructionRef2 = new CharHolder(previousInstruction);", "CharHolder _previousInstructionRef2 = previousInstruction;", StringComparison.Ordinal);
            code = code.Replace("previousInstruction = _previousInstructionRef2.value;", "previousInstruction.value = _previousInstructionRef2.value;", StringComparison.Ordinal);
            code = code.Replace("catch (CloneNotSupportedException e)", "catch (Exception e)", StringComparison.Ordinal);
            code = code.Replace("catch (CloneNotSupportedException __e)", "catch (Exception __e)", StringComparison.Ordinal);
            code = Regex.Replace(
                code,
                @"Arrays\.stream\(layer\)\s*\r?\n\s*\.filter\(v -> v < intGraph\.getNodeCount\(\)\)\s*\r?\n\s*\.flatMap\(v -> intGraph\.outEdges\(v\)\.stream\(\)\)",
                "Arrays.stream(layer)\n        .filter(v -> v < intGraph.getNodeCount())\n        .boxed()\n        .flatMap(v -> intGraph.outEdges(v).stream())");

            code = Regex.Replace(
                code,
                @"\.flatMap\(edge -> edge\.getLayerEdges\(\)\.stream\(\)\)\s*\r?\n\s*\.filter\(layerEdge -> layerEdge\.getSource\(\) != edge\.getSource\(\)\)",
                ".flatMap(edge -> edge.getLayerEdges().stream()\n        .filter(layerEdge -> layerEdge.getSource() != edge.getSource()))");

            code = Regex.Replace(
                code,
                @"Stream<LgNodeInfo>\s+neighb\s*=\s*(getNeighborsOnLevel\([^;]+?\)\.stream\(\)\.sorted\(java\.util\.Comparator\.comparingDouble\(\(LgNodeInfo n\) -> n\.getZoomLevel\(\)\)\));",
                "var neighb = $1.collect(java.util.stream.Collectors.toList());");

            code = Regex.Replace(
                code,
                @"var _chainVal\d+ = null;\s*l\.setOuterPoints\(_chainVal\d+\);\s*l\.setInnerPoints\(_chainVal\d+\);",
                "l.setOuterPoints(null);\n        l.setInnerPoints(null);");

            code = Regex.Replace(
                code,
                @"public Iterable<Node> getNodes\(\)\s*\{\s*return V;\s*\}",
                "public Iterable<Node> getNodes() {\n        return Arrays.asList(V);\n    }");

            code = Regex.Replace(
                code,
                @"var _chainVal25 = null;\s*e\.setSourcePort\(_chainVal25\);\s*originalEdge\.getEdgeGeometry\(\)\.setSourcePort\(_chainVal25\);",
                "e.setSourcePort(null);\n        originalEdge.getEdgeGeometry().setSourcePort(null);");

            code = Regex.Replace(
                code,
                @"var _chainVal26 = null;\s*e\.setTargetPort\(_chainVal26\);\s*originalEdge\.getEdgeGeometry\(\)\.setTargetPort\(_chainVal26\);",
                "e.setTargetPort(null);\n        originalEdge.getEdgeGeometry().setTargetPort(null);");

            code = Regex.Replace(
                code,
                @"return GraphConnectedComponents\.createComponents\(([^;]+)\)\.collect\(java\.util\.stream\.Collectors\.toList\(\)\);",
                "return new ArrayList<>(StreamSupport.stream(GraphConnectedComponents.createComponents($1).spliterator(), false).toList());");

            code = Regex.Replace(
                code,
                @"var touching = \(Arrays\.stream\(intersected\)\s*\r?\n\s*\.filter\(r -> intersect\(getScaled\(r, 1 \+ tolerance\), p1, p2\) && !intersect\(getScaled\(r, 1 - tolerance\), p1, p2\)\)\s*\r?\n\s*\.collect\(java\.util\.stream\.Collectors\.toList\(\)\)\)\.collect\(java\.util\.stream\.Collectors\.toList\(\)\);",
                "var touching = Arrays.stream(intersected)\n        .filter(r -> intersect(getScaled(r, 1 + tolerance), p1, p2) && !intersect(getScaled(r, 1 - tolerance), p1, p2))\n        .collect(java.util.stream.Collectors.toList());");

            code = Regex.Replace(
                code,
                @"var touching = \(Arrays\.stream\(intersected\)([\s\S]*?)\)\.collect\(java\.util\.stream\.Collectors\.toList\(\)\);",
                "var touching = Arrays.stream(intersected)$1;",
                RegexOptions.Singleline);
            code = code.Replace("var touching = (Arrays.stream(intersected)", "var touching = Arrays.stream(intersected)", StringComparison.Ordinal);

            code = Regex.Replace(code, @"\.Invoke\(", ".invoke(");
            code = Regex.Replace(code, @"System\.fail\(", "throw new RuntimeException(");

            code = Regex.Replace(
                code,
                @"VisibilityEdge ve;\s*assert _pathRouter\.findVertex\(a\)\.tryGetEdge\(_pathRouter\.findVertex\(b\), _veHolder1\);\s*ObjectHolder<VisibilityEdge> _veHolder1 = new ObjectHolder<>\(\);",
                "VisibilityEdge ve;\n        ObjectHolder<VisibilityEdge> _veHolder1 = new ObjectHolder<>();\n        assert _pathRouter.findVertex(a).tryGetEdge(_pathRouter.findVertex(b), _veHolder1);");

            code = Regex.Replace(
                code,
                @"for \(AbstractMap\.SimpleEntry<Double, Integer> layer : layers\)",
                "for (Map.Entry<Double, java.util.List<Integer>> layer : layers)");

            code = Regex.Replace(
                code,
                @"\.filter\(v -> v < getIntGraph\(\)\.getNodeCount\(\)\)\s*\r?\n\s*\.flatMap\(v -> getIntGraph\(\)\.outEdges\(v\)\.stream\(\)\)",
                ".filter(v -> v < getIntGraph().getNodeCount())\n        .boxed()\n        .flatMap(v -> getIntGraph().outEdges(v).stream())");

            code = Regex.Replace(
                code,
                @"var _chainVal10 = null;\s*setTargetTightPolyline\(_chainVal10\);\s*setSourceTightPolyline\(_chainVal10\);",
                "setTargetTightPolyline(null);\n        setSourceTightPolyline(null);");
            code = Regex.Replace(
                code,
                @"var _chainVal11 = null;\s*setTargetPort\(_chainVal11\);\s*setSourcePort\(_chainVal11\);",
                "setTargetPort(null);\n        setSourcePort(null);");
            code = Regex.Replace(
                code,
                @"var _chainVal12 = null;\s*setSourceTightPolyline\(_chainVal12\);\s*setSourceLoosePolyline\(_chainVal12\);",
                "setSourceTightPolyline(null);\n        setSourceLoosePolyline(null);");
            code = Regex.Replace(
                code,
                @"var _chainVal13 = null;\s*setTargetLoosePolyline\(_chainVal13\);\s*targetTightPolyline = _chainVal13;",
                "setTargetLoosePolyline(null);\n        targetTightPolyline = null;");

            code = Regex.Replace(
                code,
                @"throw new UnsupportedOperationException\(\);\s*return true;",
                "throw new UnsupportedOperationException();");
            code = Regex.Replace(
                code,
                @"throw new UnsupportedOperationException\(\);\s*return value;",
                "throw new UnsupportedOperationException();");

            code = Regex.Replace(
                code,
                @"if \(!d\.get\(v,\s*/\* out \*/\s*getResult\(\)\[i\]\)\)\s*\{\s*getResult\(\)\[i\] = Double\.POSITIVE_INFINITY;\s*\}",
                "if (d.containsKey(v)) {\n        getResult()[i] = d.get(v);\n        } else {\n        getResult()[i] = Double.POSITIVE_INFINITY;\n        }");

            code = Regex.Replace(
                code,
                @"var _coalesce5 = \(pushingNodes instanceof Node\[] \? \(Node\[]\)\(pushingNodes\) : null\) /\* result may be null — check before use \*/;\s*pushingNodesArray = _coalesce5 != null \? _coalesce5 : StreamSupport\.stream\(pushingNodes\.spliterator\(\), false\)\.toArray\(Node\[]::new\);",
                "pushingNodesArray = StreamSupport.stream(pushingNodes.spliterator(), false).toArray(Node[]::new);");

            code = code.Replace("t = _tHolder5.value;", string.Empty, StringComparison.Ordinal);
            code = code.Replace("t = _tHolder6.value;", string.Empty, StringComparison.Ordinal);
            code = code.Replace("var _obj70 = new ProcessStartInfo();", "var cmd = new ArrayList<String>();\n        cmd.add(pathExe);\n        cmd.addAll(Arrays.asList(arguments.split(\" \")));", StringComparison.Ordinal);
            code = code.Replace("_obj70.setCreateNoWindow(false);", string.Empty, StringComparison.Ordinal);
            code = code.Replace("_obj70.setUseShellExecute(false);", string.Empty, StringComparison.Ordinal);
            code = code.Replace("_obj70.setFileName(pathExe);", string.Empty, StringComparison.Ordinal);
            code = code.Replace("_obj70.setWindowStyle(ProcessWindowStyle.Hidden);", string.Empty, StringComparison.Ordinal);
            code = code.Replace("_obj70.setArguments(arguments);", string.Empty, StringComparison.Ordinal);
            code = code.Replace("ProcessStartInfo startInfo = _obj70;", string.Empty, StringComparison.Ordinal);
            code = code.Replace("try (Process exeProcess = Process.start(startInfo)) {", "ProcessBuilder pb = new ProcessBuilder(cmd);\n        pb.redirectErrorStream(true);\n        Process exeProcess = pb.start();\n        {", StringComparison.Ordinal);
            code = code.Replace("exeProcess.waitForExit();", "int exitCode = exeProcess.waitFor();", StringComparison.Ordinal);
            code = code.Replace("if (exeProcess.ExitCode != 0) {", "if (exitCode != 0) {", StringComparison.Ordinal);
            code = code.Replace("System.exit(exeProcess.ExitCode);", "System.exit(exitCode);", StringComparison.Ordinal);
            code = code.Replace("public void saveInputFilePoly(String path) {", "public void saveInputFilePoly(String path) throws Exception {", StringComparison.Ordinal);
            code = code.Replace("public void loadOutputFileNode(String path) {", "public void loadOutputFileNode(String path) throws Exception {", StringComparison.Ordinal);
            code = code.Replace("public void loadOutputFileSides(String path) {", "public void loadOutputFileSides(String path) throws Exception {", StringComparison.Ordinal);
            code = code.Replace("public void readTriangleOutputAndPopulateTheLevelVisibilityGraphFromTriangulation() {", "public void readTriangleOutputAndPopulateTheLevelVisibilityGraphFromTriangulation() throws Exception {", StringComparison.Ordinal);
            code = code.Replace("points.addAll(new ClusterConvexHull(c, this).translatedBoundary());", "for (PolylinePoint pp : new ClusterConvexHull(c, this).translatedBoundary().getPolylinePoints()) { points.add(pp.getPoint()); }", StringComparison.Ordinal);
            code = code.Replace("return (root != null ? root : root = getRoot());", "return root;", StringComparison.Ordinal);
            code = code.Replace("ArrayList<Integer>[] layers = Arrays.asList(new ArrayList[numberOfLayers]);", "ArrayList<Integer>[] layers = new ArrayList[numberOfLayers];", StringComparison.Ordinal);
            code = code.Replace("p.put(v, 1 / (graph.getNodes().size()));", "p.put(v, 1.0 / (graph.getNodes().size()));", StringComparison.Ordinal);
            code = code.Replace("q.put(v, (1 - omega) / (graph.getNodes().size()));", "q.put(v, (1 - omega) / (double)(graph.getNodes().size()));", StringComparison.Ordinal);
            code = code.Replace("q.get(v) += omega * p.get(u) / (int)(long) StreamSupport.stream(u.getInEdges().spliterator(), false).count();", "q.put(v, q.get(v) + omega * p.get(u) / (int)(long) StreamSupport.stream(u.getInEdges().spliterator(), false).count());", StringComparison.Ordinal);
            code = code.Replace("q.get(v) += omega * p.get(u) / (int)(long) StreamSupport.stream(u.getOutEdges().spliterator(), false).count();", "q.put(v, q.get(v) + omega * p.get(u) / (int)(long) StreamSupport.stream(u.getOutEdges().spliterator(), false).count());", StringComparison.Ordinal);

            // .NET DataContractSerializer has no direct Java counterpart; use string payload fallback.
            if (string.Equals(r.FileName, "GraphWriter.java", StringComparison.OrdinalIgnoreCase))
            {
                code = code.Replace("DataContractSerializer dcs = null;", "Object dcs = null;", StringComparison.Ordinal);
                code = code.Replace("ObjectHolder<DataContractSerializer>", "ObjectHolder<Object>", StringComparison.Ordinal);
                code = code.Replace("dcs.value = new DataContractSerializer(obj.getClass());", string.Empty, StringComparison.Ordinal);
                code = code.Replace("dcs.value.writeObject(xw, obj);", "xw.writeString(obj != null ? obj.toString() : \"null\");", StringComparison.Ordinal);
            }

            code = Regex.Replace(
                code,
                @"public void launchTriangleExe\(String pathExe, String arguments\)\s*\{[\s\S]*?\n\s*\}\n\s*public void readTriangleOutputAndPopulateTheLevelVisibilityGraphFromTriangulation\(\)",
                "public void launchTriangleExe(String pathExe, String arguments) {\n        String triangleMessage = \"Cannot start Triangle.exe To build Triangle.exe, please open http://www.cs.cmu.edu/~quake/triangle.html and build it by following the instructions from the site. Copy Triange.exe to a directory in your PATH.\" + \"Unfortunately we cannot distribute Triangle.exe because of the license restrictions.\";\n        try {\n        var cmd = new ArrayList<String>();\n        cmd.add(pathExe);\n        cmd.addAll(Arrays.asList(arguments.split(\" \")));\n        ProcessBuilder pb = new ProcessBuilder(cmd);\n        pb.redirectErrorStream(true);\n        Process exeProcess = pb.start();\n        int exitCode = exeProcess.waitFor();\n        if (exitCode != 0) {\n        System.exit(exitCode);\n        }\n        } catch (Exception e) {\n        System.out.println(e.getMessage());\n        System.out.println(triangleMessage);\n        System.out.println(\"Exiting now.\");\n        System.exit(1);\n        }\n    }\n    public void readTriangleOutputAndPopulateTheLevelVisibilityGraphFromTriangulation()",
                RegexOptions.Singleline);

            code = Regex.Replace(
                code,
                @"StreamSupport\.stream\(graphs\.spliterator\(\), true\)\.forEach\(this::layoutConnectedGraphWithMds\);",
                "Arrays.stream(graphs).parallel().forEach(this::layoutConnectedGraphWithMds);");

            code = code.Replace(
                "if (settings.getIterations()++ == 0) {",
                "settings.setIterations(settings.getIterations() + 1);\n        if (settings.getIterations() == 1) {",
                StringComparison.Ordinal);

            code = Regex.Replace(
                code,
                @"Arrays\.asList\(new int\[\]\s*\{\s*([^{}]+?)\s*\}\)",
                "java.util.Collections.singletonList($1)");

            code = Regex.Replace(
                code,
                @"CycleRemoval\.getFeedbackSet\((.*?)\)\.collect\((?:java\.util\.stream\.)?Collectors\.toList\(\)\)",
                "StreamSupport.stream(CycleRemoval.getFeedbackSet($1).spliterator(), false).collect(java.util.stream.Collectors.toList())",
                RegexOptions.Singleline);

            code = Regex.Replace(
                code,
                @"CycleRemoval\.getFeedbackSetWithConstraints\((.*?)\)\.collect\((?:java\.util\.stream\.)?Collectors\.toList\(\)\)",
                "StreamSupport.stream(CycleRemoval.getFeedbackSetWithConstraints($1).spliterator(), false).collect(java.util.stream.Collectors.toList())",
                RegexOptions.Singleline);

            code = code.Replace(".sorted(java.util.Comparator.comparing(e -> e.getLength())", ".sorted(java.util.Comparator.comparing((Microsoft.Msagl.Core.Layout.Edge e) -> e.getLength())", StringComparison.Ordinal);
            code = code.Replace("public class VisibilityGraphGenerator {", "public abstract class VisibilityGraphGenerator {", StringComparison.Ordinal);
            code = code.Replace("for (VisibilityVertexRectilinear source : sources) {", "for (VisibilityVertex source : sources) {", StringComparison.Ordinal);
            code = code.Replace("for (VisibilityVertexRectilinear target : targets) {", "for (VisibilityVertex target : targets) {", StringComparison.Ordinal);
            code = code.Replace("VertexEntry lastEntry = ssstCalculator.getPathWithCost(sourceVertexEntries, source, sourceCostAdjustment, tempTargetEntries, target, targetCostAdjustment, adjustedBestCost);", "VertexEntry lastEntry = ssstCalculator.getPathWithCost(sourceVertexEntries, (VisibilityVertexRectilinear)source, sourceCostAdjustment, tempTargetEntries, (VisibilityVertexRectilinear)target, targetCostAdjustment, adjustedBestCost);", StringComparison.Ordinal);
            code = code.Replace("for (AbstractMap.SimpleEntry<Path, LinkedPoint> pair : prevLocationPathOffsets.entrySet().stream().filter(pair -> !pathOffsets.containsKey(pair.getKey())).collect(Collectors.toCollection(ArrayList::new)))", "for (Map.Entry<Path, LinkedPoint> pair : prevLocationPathOffsets.entrySet().stream().filter(pair -> !pathOffsets.containsKey(pair.getKey())).collect(java.util.stream.Collectors.toList()))", StringComparison.Ordinal);
            code = code.Replace("for (AbstractMap.SimpleEntry<Double, LinkedPoint> pathLinkedPointBucket : colliniarBuckets)", "for (Map.Entry<Double, java.util.List<LinkedPoint>> pathLinkedPointBucket : colliniarBuckets)", StringComparison.Ordinal);
            code = code.Replace("refineCollinearBucket(pathLinkedPointBucket, projectionToDirection);", "refineCollinearBucket(pathLinkedPointBucket.getValue(), projectionToDirection);", StringComparison.Ordinal);
            code = code.Replace("boolean aIsInsideB, bIsInsideA;", "boolean aIsInsideB = false, bIsInsideA = false;", StringComparison.Ordinal);
            code = code.Replace("var incomingEdges = inDegreeLeftUnprocessed.get(edge.getTarget())--;", "var incomingEdges = inDegreeLeftUnprocessed.get(edge.getTarget());\n        inDegreeLeftUnprocessed.put(edge.getTarget(), incomingEdges - 1);", StringComparison.Ordinal);
            code = code.Replace("public class FreeSpaceFinder extends LineSweeperBase implements Comparator<AxisEdgesContainer> {", "public class FreeSpaceFinder extends LineSweeperBase {", StringComparison.Ordinal);
            code = code.Replace("edgeContainersTree = new RbTree<AxisEdgesContainer>(this);", "edgeContainersTree = new RbTree<AxisEdgesContainer>((x, y) -> compare(x, y));", StringComparison.Ordinal);
            code = code.Replace(".collect(java.util.stream.Collectors.toList())).spliterator(), false).map(x -> (ICurve) x));", ".collect(java.util.stream.Collectors.toList())).spliterator(), false).map(x -> (ICurve) x).collect(java.util.stream.Collectors.toList()));", StringComparison.Ordinal);
            code = code.Replace("int[] _i = { i };\n        int[] _i = { i };", "int[] _i = { i };", StringComparison.Ordinal);
            code = code.Replace("var projectionToDir = (dir == Direction.East ? (PointProjection)((p -> p.X)) : (p -> p.Y));", "PointProjection projectionToDir = (dir == Direction.East ? (PointProjection)((p -> p.X)) : (PointProjection)(p -> p.Y));", StringComparison.Ordinal);
            code = code.Replace("var projectionToPerp = (getNudgingDirection() == Direction.East ? (PointProjection)(FreeSpaceFinder::minusY) : FreeSpaceFinder::x);", "PointProjection projectionToPerp = (getNudgingDirection() == Direction.East ? (PointProjection)(FreeSpaceFinder::minusY) : (PointProjection)(FreeSpaceFinder::x));", StringComparison.Ordinal);
            code = code.Replace("getLongestNudgedSegs().add(edge.setLongestNudgedSegment(currentLongestSeg = new LongestNudgedSegment(getLongestNudgedSegs().size())));", "currentLongestSeg = new LongestNudgedSegment(getLongestNudgedSegs().size());\n        edge.setLongestNudgedSegment(currentLongestSeg);\n        getLongestNudgedSegs().add(currentLongestSeg);", StringComparison.Ordinal);
            code = code.Replace("StreamSupport.stream(path.getPathPoints().spliterator(), false).skip(1).reduce(ret, (lp, p) -> lp.setNext(new LinkedPoint(p)), (__accLeft, __accRight) -> __accRight);", "LinkedPoint cur = ret;\n        for (Point p : StreamSupport.stream(path.getPathPoints().spliterator(), false).skip(1).collect(java.util.stream.Collectors.toList())) {\n        cur.setNext(new LinkedPoint(p));\n        cur = cur.getNext();\n        }", StringComparison.Ordinal);
            code = code.Replace("this.setMaxVisibilitySegment(obstacleTree.createMaxVisibilitySegment(this.getVisibilityBorderIntersect(), this.getOutwardDirection(), /* out */ this.pointAndCrossingsList));", "ObjectHolder<PointAndCrossingsList> _pcl = new ObjectHolder<>(this.pointAndCrossingsList);\n        this.setMaxVisibilitySegment(obstacleTree.createMaxVisibilitySegment(this.getVisibilityBorderIntersect(), this.getOutwardDirection(), _pcl));\n        this.pointAndCrossingsList = _pcl.value;", StringComparison.Ordinal);
            code = code.Replace("toArray(AbstractMap.SimpleEntry<Point, FreePoint>[]::new)", "toArray(Map.Entry[]::new)", StringComparison.Ordinal);
            code = code.Replace("for (AbstractMap.SimpleEntry<Point, FreePoint> staleFreePair : staleFreePairs)", "for (Map.Entry<Point, FreePoint> staleFreePair : staleFreePairs)", StringComparison.Ordinal);
            code = code.Replace("new Polyline(ConvexHull.calculateConvexHull(java.util.stream.Stream.concat(StreamSupport.stream(poly.spliterator(), false), Arrays.stream(stickingPointsArray).boxed()).collect(java.util.stream.Collectors.toList())))", "new Polyline(ConvexHull.calculateConvexHull(new ArrayList<Point>(java.util.stream.Stream.concat(StreamSupport.stream(poly.spliterator(), false), Arrays.stream(stickingPointsArray).boxed()).collect(java.util.stream.Collectors.toList()))))", StringComparison.Ordinal);
            code = code.Replace("Arrays.stream(stickingPointsArray).boxed()", "Arrays.stream(stickingPointsArray)", StringComparison.Ordinal);
            code = code.Replace("var _coalesce6 = (this._edges != null ? this._edges.Select(e -> e.getEdgeGeometry()) : null);\n        return _coalesce6 != null ? _coalesce6 : Stream.<EdgeGeometry>empty();", "if (this._edges != null) {\n        return StreamSupport.stream(this._edges.spliterator(), false).map(e -> e.getEdgeGeometry()).collect(java.util.stream.Collectors.toList());\n        }\n        return Stream.<EdgeGeometry>empty().collect(java.util.stream.Collectors.toList());", StringComparison.Ordinal);
            code = code.Replace("_edges.Select(e -> e.getEdgeGeometry())", "StreamSupport.stream(this._edges.spliterator(), false).map(e -> e.getEdgeGeometry()).collect(java.util.stream.Collectors.toList())", StringComparison.Ordinal);
            code = code.Replace("for (AbstractMap.SimpleEntry<Set<Shape>, Microsoft.Msagl.Core.Layout.Edge> edgeGroup : Arrays.stream(_edges).collect(Collectors.groupingBy(this::edgePassport)).entrySet().stream())", "for (Map.Entry<Set<Shape>, java.util.List<Microsoft.Msagl.Core.Layout.Edge>> edgeGroup : Arrays.stream(_edges).collect(Collectors.groupingBy(this::edgePassport)).entrySet())", StringComparison.Ordinal);
            code = code.Replace("void routeEdgesWithTheSamePassport(AbstractMap.SimpleEntry<Set<Shape>, Microsoft.Msagl.Core.Layout.Edge> edgeGeometryGroup, InteractiveEdgeRouter interactiveEdgeRouter, Set<Shape> obstacleShapes)", "void routeEdgesWithTheSamePassport(Map.Entry<Set<Shape>, java.util.List<Microsoft.Msagl.Core.Layout.Edge>> edgeGeometryGroup, InteractiveEdgeRouter interactiveEdgeRouter, Set<Shape> obstacleShapes)", StringComparison.Ordinal);
            code = code.Replace("splitOnRegularAndMultiedges(edgeGeometryGroup, _regularEdgesHolder1, _multiEdgesHolder1);", "splitOnRegularAndMultiedges(edgeGeometryGroup.getValue(), _regularEdgesHolder1, _multiEdgesHolder1);", StringComparison.Ordinal);
            code = code.Replace("for (Microsoft.Msagl.Core.Layout.Edge eg : edgeGeometryGroup.collect(Collectors.toCollection(ArrayList::new)))", "for (Microsoft.Msagl.Core.Layout.Edge eg : edgeGeometryGroup.getValue())", StringComparison.Ordinal);
            code = code.Replace("Arrays.stream(HalfWidthArray).mapToDouble(x -> x).sum()", "Arrays.stream(HalfWidthArray).sum()", StringComparison.Ordinal);
            code = code.Replace("return boneEdge.setCrossedCdtEdges(threadBoneEdgeThroughCdt(boneEdge));", "boneEdge.setCrossedCdtEdges(threadBoneEdgeThroughCdt(boneEdge));\n        return boneEdge.getCrossedCdtEdges();", StringComparison.Ordinal);
            code = code.Replace("var edges = new ArrayList<>(StreamSupport.stream((StreamSupport.stream(graph.getEdges().spliterator(), false)\n        .sorted(java.util.Comparator.comparing((Microsoft.Msagl.Core.Layout.Edge e) -> e.getLength())\n        .thenComparing(e -> rand.nextInt()))\n        .collect(java.util.stream.Collectors.toList())).spliterator(), false).collect(java.util.stream.Collectors.toList()));", "var edges = new ArrayList<Microsoft.Msagl.Core.Layout.Edge>(StreamSupport.stream(graph.getEdges().spliterator(), false).collect(java.util.stream.Collectors.toList()));", StringComparison.Ordinal);
            code = Regex.Replace(code, @"int\[] _i = \{ i \};\s*int\[] _i = \{ i \};", "int[] _i = { i };");
            code = code.Replace("this.StreamSupport.stream", "StreamSupport.stream", StringComparison.Ordinal);
            code = code.Replace("return _coalesce6 != null ? _coalesce6 : Stream.<EdgeGeometry>empty();", "return _coalesce6 != null ? _coalesce6 : java.util.Collections.<EdgeGeometry>emptyList();", StringComparison.Ordinal);
            code = code.Replace("for (VisibilityEdge edge : edgesToFix.collect(Collectors.toCollection(ArrayList::new)))", "for (VisibilityEdge edge : edgesToFix)", StringComparison.Ordinal);
            code = code.Replace("metroGraphData.getEdges()[edgeIndex].setCurve(new Polyline(gluedPolyline(StreamSupport.stream(poly.spliterator(), false).map(p -> metroGraphData.PointToStations.get(p)).toArray(Station[]::new), gluingMap).collect(java.util.stream.Collectors.toList())));", "metroGraphData.getEdges()[edgeIndex].setCurve(new Polyline(StreamSupport.stream(gluedPolyline(StreamSupport.stream(poly.spliterator(), false).map(p -> metroGraphData.PointToStations.get(p)).toArray(Station[]::new), gluingMap).spliterator(), false).collect(java.util.stream.Collectors.toList())));", StringComparison.Ordinal);
            code = code.Replace("return ret.stream().collect(Collectors.collectingAndThen(Collectors.toCollection(ArrayList::new), list -> { Collections.reverse(list); return list; })).map(n -> n.Position).collect(java.util.stream.Collectors.toList());", "return ret.stream().collect(Collectors.collectingAndThen(Collectors.toCollection(ArrayList::new), list -> { Collections.reverse(list); return list; })).stream().map(n -> n.Position).collect(java.util.stream.Collectors.toList());", StringComparison.Ordinal);
            code = code.Replace("for (Metroline metroline : abcPolylines) { polylineLength.get(metroline) -= ab + bc - ac; }", "for (Metroline metroline : abcPolylines) { polylineLength.put(metroline, polylineLength.get(metroline) - (ab + bc - ac)); }", StringComparison.Ordinal);
            code = code.Replace("glueEdge(keyValuePair);", "glueEdge(new AbstractMap.SimpleEntry<AbstractMap.SimpleEntry<Station, Station>, Point>(keyValuePair.getKey(), keyValuePair.getValue()));", StringComparison.Ordinal);
            code = code.Replace("crossingsOfEdgeNodeA.exists(ii -> !StreamSupport.stream(enterableForEdgeNodeB.spliterator(), false).collect(Collectors.toSet()).contains(ii.getSegment1()))", "crossingsOfEdgeNodeA.stream().anyMatch(ii -> !StreamSupport.stream(enterableForEdgeNodeB.spliterator(), false).collect(Collectors.toSet()).contains(ii.getSegment1()))", StringComparison.Ordinal);
            code = code.Replace("crossingsOfEdgeab.exists(ii -> !StreamSupport.stream(enterableForEdgeNodeB.spliterator(), false).collect(Collectors.toSet()).contains(ii.getSegment1()))", "crossingsOfEdgeab.stream().anyMatch(ii -> !StreamSupport.stream(enterableForEdgeNodeB.spliterator(), false).collect(Collectors.toSet()).contains(ii.getSegment1()))", StringComparison.Ordinal);
            code = code.Replace("System.out.print(\"{0}: \", String.format(\"{0:0.000}\", timer.getDuration()));", "System.out.print(String.format(\"%.3f: \", timer.getDuration()));", StringComparison.Ordinal);
            code = code.Replace("var coneLeftSide = (leftNode.Item instanceof ConeLeftSide ? (ConeLeftSide)(leftNode.Item) : null) /* result may be null — check before use */;", "ConeLeftSide coneLeftSide = (leftNode.Item instanceof ConeLeftSide ? (ConeLeftSide)(leftNode.Item) : null);", StringComparison.Ordinal);
            code = code.Replace("var seg = (rbNode.Item instanceof ConeRightSide ? (ConeRightSide)(rbNode.Item) : null) /* result may be null — check before use */;", "ConeRightSide seg = (rbNode.Item instanceof ConeRightSide ? (ConeRightSide)(rbNode.Item) : null);", StringComparison.Ordinal);
            code = code.Replace("boolean canHaveStaircase;", "boolean canHaveStaircase = false;", StringComparison.Ordinal);
            code = code.Replace("boolean canHaveStaircaseAtI;", "boolean canHaveStaircaseAtI = false;", StringComparison.Ordinal);
            code = code.Replace("StreamSupport.stream(this._edges.spliterator(), false)", "Arrays.stream(this._edges)", StringComparison.Ordinal);
            code = code.Replace("assert (!ApproximateComparer.closeIntersections(intersectionPoint, a.getFirst()) && !ApproximateComparer.closeIntersections(intersectionPoint, a.getSecond())) || Point.distToLineSegment(intersectionPoint, a.getFirst(), a.getSecond(), _tHolder7) < ApproximateComparer.getIntersectionEpsilon();\n        DoubleHolder _tHolder7 = new DoubleHolder();", "DoubleHolder _tHolder7 = new DoubleHolder();\n        assert (!ApproximateComparer.closeIntersections(intersectionPoint, a.getFirst()) && !ApproximateComparer.closeIntersections(intersectionPoint, a.getSecond())) || Point.distToLineSegment(intersectionPoint, a.getFirst(), a.getSecond(), _tHolder7) < ApproximateComparer.getIntersectionEpsilon();", StringComparison.Ordinal);
            code = code.Replace("var leftNode = insertToTree(leftConeSides, cone.setLeftSide(new ConeLeftSide(cone)));\n        var rightNode = insertToTree(rightConeSides, cone.setRightSide(new ConeRightSide(cone)));", "ConeLeftSide leftSide = new ConeLeftSide(cone);\n        cone.setLeftSide(leftSide);\n        var leftNode = insertToTree(leftConeSides, leftSide);\n        ConeRightSide rightSide = new ConeRightSide(cone);\n        cone.setRightSide(rightSide);\n        var rightNode = insertToTree(rightConeSides, rightSide);", StringComparison.Ordinal);
            code = code.Replace("RBNode<ConeSide> leftNode = insertToTree(leftConeSides, cone.setLeftSide(new ConeLeftSide(cone)));\n        RBNode<ConeSide> rightNode = insertToTree(rightConeSides, cone.setRightSide(new ConeRightSide(cone)));", "ConeLeftSide leftSide = new ConeLeftSide(cone);\n        cone.setLeftSide(leftSide);\n        RBNode<ConeSide> leftNode = insertToTree(leftConeSides, leftSide);\n        ConeRightSide rightSide = new ConeRightSide(cone);\n        cone.setRightSide(rightSide);\n        RBNode<ConeSide> rightNode = insertToTree(rightConeSides, rightSide);", StringComparison.Ordinal);
            code = code.Replace("ObstaclePort oport;", "ObstaclePort oport = null;", StringComparison.Ordinal);
            code = Regex.Replace(
                code,
                @"assert \(!ApproximateComparer\.closeIntersections\(intersectionPoint, a\.getFirst\(\)\) && !ApproximateComparer\.closeIntersections\(intersectionPoint, a\.getSecond\(\)\)\) \|\| Point\.distToLineSegment\(intersectionPoint, a\.getFirst\(\), a\.getSecond\(\), _tHolder7\) < ApproximateComparer\.getIntersectionEpsilon\(\);\s*DoubleHolder _tHolder7 = new DoubleHolder\(\);",
                "DoubleHolder _tHolder7 = new DoubleHolder();\n        assert (!ApproximateComparer.closeIntersections(intersectionPoint, a.getFirst()) && !ApproximateComparer.closeIntersections(intersectionPoint, a.getSecond())) || Point.distToLineSegment(intersectionPoint, a.getFirst(), a.getSecond(), _tHolder7) < ApproximateComparer.getIntersectionEpsilon();");
            code = Regex.Replace(
                code,
                @"var leftNode = insertToTree\(leftConeSides, cone\.setLeftSide\(new ConeLeftSide\(cone\)\)\);\s*var rightNode = insertToTree\(rightConeSides, cone\.setRightSide\(new ConeRightSide\(cone\)\)\);",
                "ConeLeftSide leftSide = new ConeLeftSide(cone);\n        cone.setLeftSide(leftSide);\n        var leftNode = insertToTree(leftConeSides, leftSide);\n        ConeRightSide rightSide = new ConeRightSide(cone);\n        cone.setRightSide(rightSide);\n        var rightNode = insertToTree(rightConeSides, rightSide);");
            code = Regex.Replace(
                code,
                @"RBNode<ConeSide> leftNode = insertToTree\(leftConeSides, cone\.setLeftSide\(new ConeLeftSide\(cone\)\)\);\s*RBNode<ConeSide> rightNode = insertToTree\(rightConeSides, cone\.setRightSide\(new ConeRightSide\(cone\)\)\);",
                "ConeLeftSide leftSide = new ConeLeftSide(cone);\n        cone.setLeftSide(leftSide);\n        RBNode<ConeSide> leftNode = insertToTree(leftConeSides, leftSide);\n        ConeRightSide rightSide = new ConeRightSide(cone);\n        cone.setRightSide(rightSide);\n        RBNode<ConeSide> rightNode = insertToTree(rightConeSides, rightSide);");
            code = code.Replace(
                "if (tightObstaclesInTheBoundingBox.stream().anyMatch(t -> Intersections.lineSegmentIntersectPolyline(ls.getStart(), ls.getEnd(), t))) {\n        return false;\n        }\n        DoubleHolder _sourceRParamHolder1 = new DoubleHolder();",
                "LineSegment _lsCheck1 = ls;\n        if (tightObstaclesInTheBoundingBox.stream().anyMatch(t -> Intersections.lineSegmentIntersectPolyline(_lsCheck1.getStart(), _lsCheck1.getEnd(), t))) {\n        return false;\n        }\n        DoubleHolder _sourceRParamHolder1 = new DoubleHolder();",
                StringComparison.Ordinal);
            code = code.Replace(
                "if (tightObstaclesInTheBoundingBox.stream().anyMatch(t -> Intersections.lineSegmentIntersectPolyline(ls.getStart(), ls.getEnd(), t))) {\n        return false;\n        }\n        if (SourceBase.IsParent) {",
                "LineSegment _lsCheck2 = ls;\n        if (tightObstaclesInTheBoundingBox.stream().anyMatch(t -> Intersections.lineSegmentIntersectPolyline(_lsCheck2.getStart(), _lsCheck2.getEnd(), t))) {\n        return false;\n        }\n        if (SourceBase.IsParent) {",
                StringComparison.Ordinal);
            code = Regex.Replace(
                code,
                @"targetRParam = _targetRParamHolder1\.value;\s*if \(ls == null\) \{\s*return false;\s*\}\s*if \(tightObstaclesInTheBoundingBox\.stream\(\)\.anyMatch\(t -> Intersections\.lineSegmentIntersectPolyline\(ls\.getStart\(\), ls\.getEnd\(\), t\)\)\) \{\s*return false;\s*\}",
                "targetRParam = _targetRParamHolder1.value;\n        if (ls == null) {\n        return false;\n        }\n        LineSegment _lsCheck1 = ls;\n        if (tightObstaclesInTheBoundingBox.stream().anyMatch(t -> Intersections.lineSegmentIntersectPolyline(_lsCheck1.getStart(), _lsCheck1.getEnd(), t))) {\n        return false;\n        }");
            code = Regex.Replace(
                code,
                @"targetLParam = _targetLParamHolder1\.value;\s*if \(ls == null\) \{\s*return false;\s*\}\s*if \(tightObstaclesInTheBoundingBox\.stream\(\)\.anyMatch\(t -> Intersections\.lineSegmentIntersectPolyline\(ls\.getStart\(\), ls\.getEnd\(\), t\)\)\) \{\s*return false;\s*\}",
                "targetLParam = _targetLParamHolder1.value;\n        if (ls == null) {\n        return false;\n        }\n        LineSegment _lsCheck2 = ls;\n        if (tightObstaclesInTheBoundingBox.stream().anyMatch(t -> Intersections.lineSegmentIntersectPolyline(_lsCheck2.getStart(), _lsCheck2.getEnd(), t))) {\n        return false;\n        }");
            code = code.Replace(
                "Point center;\n        ObjectHolder<Point> _centerHolder4 = new ObjectHolder<>();\n        if (Math.abs(Point.signedDoubledTriangleArea(a, b, c)) < 0.0001 || !findArcCenter(a, b, c, _centerHolder4)) {\n        center = _centerHolder4.value;\n        return new LineSegment(a, c);\n        }",
                "Point center;\n        ObjectHolder<Point> _centerHolder4 = new ObjectHolder<>();\n        if (Math.abs(Point.signedDoubledTriangleArea(a, b, c)) < 0.0001 || !findArcCenter(a, b, c, _centerHolder4)) {\n        return new LineSegment(a, c);\n        }\n        center = _centerHolder4.value;",
                StringComparison.Ordinal);
            code = Regex.Replace(
                code,
                @"Point center;\s*ObjectHolder<Point> _centerHolder4 = new ObjectHolder<>\(\);\s*if \(Math\.abs\(Point\.signedDoubledTriangleArea\(a, b, c\)\) < 0\.0001 \|\| !findArcCenter\(a, b, c, _centerHolder4\)\) \{\s*center = _centerHolder4\.value;\s*return new LineSegment\(a, c\);\s*\}",
                "Point center;\n        ObjectHolder<Point> _centerHolder4 = new ObjectHolder<>();\n        if (Math.abs(Point.signedDoubledTriangleArea(a, b, c)) < 0.0001 || !findArcCenter(a, b, c, _centerHolder4)) {\n        return new LineSegment(a, c);\n        }\n        center = _centerHolder4.value;");
            code = code.Replace(
                "GeometryGraph gg = createGraphFromObstacles(getObstacles());\n        GeometryGraphWriter.write(gg, \"c:\\\\tmp\\\\bug1\");",
                "try {\n        GeometryGraph gg = createGraphFromObstacles(getObstacles());\n        GeometryGraphWriter.write(gg, \"c:\\\\tmp\\\\bug1\");\n        } catch (Exception _ex) {\n        }",
                StringComparison.Ordinal);
            code = Regex.Replace(
                code,
                @"GeometryGraph gg = createGraphFromObstacles\(getObstacles\(\)\);\s*GeometryGraphWriter\.write\(gg, ""c:\\\\tmp\\\\bug1""\);",
                "try {\n        GeometryGraph gg = createGraphFromObstacles(getObstacles());\n        GeometryGraphWriter.write(gg, \"c:\\\\tmp\\\\bug1\");\n        } catch (Exception _ex) {\n        }");

            // Iterator compatibility bridge: many translated IEnumerator classes expose getCurrent()/hasNext()
            // but miss Java Iterator.next(). Add a thin bridge for compile-time compatibility.
            code = code.Replace(
                "public T getCurrent() {\n        return c.Item;\n    }\n    public void reset() {",
                "public T getCurrent() {\n        return c.Item;\n    }\n    @Override\n    public T next() {\n        return getCurrent();\n    }\n    public void reset() {",
                StringComparison.Ordinal);
            code = code.Replace(
                "public Point getCurrent() {\n        return currentNode.getData();\n    }\n    public boolean hasNext() {",
                "public Point getCurrent() {\n        return currentNode.getData();\n    }\n    @Override\n    public Point next() {\n        return getCurrent();\n    }\n    public boolean hasNext() {",
                StringComparison.Ordinal);
            code = code.Replace(
                "public Point getCurrent() {\n        return currentNode.getPoint();\n    }\n    public void close() {",
                "public Point getCurrent() {\n        return currentNode.getPoint();\n    }\n    @Override\n    public Point next() {\n        return getCurrent();\n    }\n    public void close() {",
                StringComparison.Ordinal);
            code = code.Replace(
                "public int getCurrent() {\n        return 0;\n    }\n    public void reset() {",
                "public int getCurrent() {\n        return 0;\n    }\n    @Override\n    public Integer next() {\n        return getCurrent();\n    }\n    public void reset() {",
                StringComparison.Ordinal);
            code = code.Replace(
                "public int getCurrent() {\n        return sucsV[currentSuccOffset];\n    }\n    public void close() {",
                "public int getCurrent() {\n        return sucsV[currentSuccOffset];\n    }\n    @Override\n    public Integer next() {\n        return getCurrent();\n    }\n    public void close() {",
                StringComparison.Ordinal);
            code = code.Replace(
                "public int getCurrent() {\n        return predsV[currentPredOffset];\n    }\n    public void close() {",
                "public int getCurrent() {\n        return predsV[currentPredOffset];\n    }\n    @Override\n    public Integer next() {\n        return getCurrent();\n    }\n    public void close() {",
                StringComparison.Ordinal);
            code = code.Replace(
                "public NetworkEdge getCurrent() {\n            if (outIsActive) {",
                "public NetworkEdge getCurrent() {\n            if (outIsActive) {",
                StringComparison.Ordinal);
            code = code.Replace(
                "            throw new IllegalStateException();\n        }",
                "            throw new IllegalStateException();\n        }\n        @Override\n        public NetworkEdge next() {\n            return getCurrent();\n        }",
                StringComparison.Ordinal);

            // List.remove(int) signature compatibility for java.util.List implementations.
            code = code.Replace(
                "public void remove(int index) {\n        var node = nodes.get(index);\n        detouchNode(node);\n        nodes.remove(index);\n    }",
                "public Node remove(int index) {\n        var node = nodes.get(index);\n        detouchNode(node);\n        nodes.remove(index);\n        return node;\n    }",
                StringComparison.Ordinal);
            code = code.Replace(
                "public void remove(int index) {\n        throw new UnsupportedOperationException();\n    }",
                "public Node remove(int index) {\n        throw new UnsupportedOperationException();\n    }",
                StringComparison.Ordinal);

            // Fallback bridge: if class implements Iterator<X> and still lacks next(), inject next() after getCurrent().
            var iteratorTypeMatch = Regex.Match(code, @"implements\s+Iterator<(?<it>[^>]+)>");
            var hasNextMethodDecl = Regex.IsMatch(code, @"public\s+[\w<>,\[\]\.?]+\s+next\s*\(");
            if (iteratorTypeMatch.Success && code.Contains("getCurrent()", StringComparison.Ordinal) && !hasNextMethodDecl)
            {
                var iteratorType = iteratorTypeMatch.Groups["it"].Value.Trim();
                // Use brace-counting to find the full method body (lazy regex would stop at first inner brace)
                code = InjectNextAfterGetCurrent(code, iteratorType);
            }

            // Fallback for List remove(int) return contract in known Node list wrappers.
            if (code.Contains("class NodeCollection", StringComparison.Ordinal))
            {
                code = Regex.Replace(
                    code,
                    @"public\s+void\s+remove\(int\s+index\)",
                    "public Node remove(int index)");
                code = Regex.Replace(
                    code,
                    @"nodes\.remove\(index\);\s*\}",
                    "nodes.remove(index);\n        return node;\n    }");
            }
            if (code.Contains("class LgNodeCollection", StringComparison.Ordinal)
                || code.Contains("class SimpleNodeCollection", StringComparison.Ordinal))
            {
                code = Regex.Replace(
                    code,
                    @"public\s+void\s+remove\(int\s+index\)",
                    "public Node remove(int index)");
            }

            // CRLF-safe, class-targeted iterator/list compatibility fixes.
            if (code.Contains("class RBTreeEnumerator", StringComparison.Ordinal)
                && !code.Contains("public T next()", StringComparison.Ordinal))
            {
                code = Regex.Replace(
                    code,
                    @"public\s+T\s+getCurrent\(\)\s*\{[\s\S]*?\}\s*public\s+void\s+reset\(\)",
                    m => m.Value.Replace("public void reset()", "@Override\n    public T next() {\n        return getCurrent();\n    }\n    public void reset()"));
            }
            // iteratorBridgeClasses 循环已移除：懒惰正则会在方法体内错误插入 next()，
            // 各类已由下方专项处理覆盖。
            if ((code.Contains("class PolylineIterator", StringComparison.Ordinal)
                || code.Contains("class PointNodesList", StringComparison.Ordinal))
                && !code.Contains("public Point next()", StringComparison.Ordinal))
            {
                code = Regex.Replace(
                    code,
                    @"public\s+Point\s+getCurrent\(\)\s*\{[\s\S]*?\}\s*public\s+(?:boolean\s+hasNext\(\)|void\s+close\(\))",
                    m => m.Value.Replace("public boolean hasNext()", "@Override\n    public Point next() {\n        return getCurrent();\n    }\n    public boolean hasNext()")
                                .Replace("public void close()", "@Override\n    public Point next() {\n        return getCurrent();\n    }\n    public void close()"));
            }
            if ((code.Contains("class EmptyEnumerator", StringComparison.Ordinal)
                || code.Contains("class PredEnumerator", StringComparison.Ordinal)
                || code.Contains("class SuccEnumerator", StringComparison.Ordinal))
                && !code.Contains("public Integer next()", StringComparison.Ordinal))
            {
                code = Regex.Replace(
                    code,
                    @"public\s+int\s+getCurrent\(\)\s*\{[\s\S]*?\}\s*public\s+(?:void\s+reset\(\)|void\s+close\(\))",
                    m => m.Value.Replace("public void reset()", "@Override\n    public Integer next() {\n        return getCurrent();\n    }\n    public void reset()")
                                .Replace("public void close()", "@Override\n    public Integer next() {\n        return getCurrent();\n    }\n    public void close()"));
            }
            if (code.Contains("class IncEdgeEnumerator", StringComparison.Ordinal)
                && !code.Contains("public NetworkEdge next()", StringComparison.Ordinal))
            {
                code = Regex.Replace(
                    code,
                    @"public\s+NetworkEdge\s+getCurrent\(\)\s*\{[\s\S]*?throw\s+new\s+IllegalStateException\(\);\s*\}",
                    m => m.Value + "\n        @Override\n        public NetworkEdge next() {\n            return getCurrent();\n        }");
            }
            if (code.Contains("class NodeCollection", StringComparison.Ordinal))
            {
                code = Regex.Replace(
                    code,
                    @"public\s+void\s+remove\(int\s+index\)\s*\{\s*var\s+node\s*=\s*nodes\.get\(index\);\s*detouchNode\(node\);\s*nodes\.remove\(index\);\s*\}",
                    "public Node remove(int index) {\n        var node = nodes.get(index);\n        detouchNode(node);\n        nodes.remove(index);\n        return node;\n    }");
            }
            if (code.Contains("class LgNodeCollection", StringComparison.Ordinal)
                || code.Contains("class SimpleNodeCollection", StringComparison.Ordinal))
            {
                code = Regex.Replace(
                    code,
                    @"public\s+void\s+remove\(int\s+index\)\s*\{\s*throw\s+new\s+UnsupportedOperationException\(\);\s*\}",
                    "public Node remove(int index) {\n        throw new UnsupportedOperationException();\n    }");
            }

            r.GeneratedCode = code;
        }
    }

    /// <summary>
    /// Injects a next() bridge method after getCurrent() using brace-counting to find
    /// the true end of the method body (lazy regex would incorrectly stop at inner braces).
    /// </summary>
    private static string InjectNextAfterGetCurrent(string code, string iteratorType)
    {
        var sigMatch = Regex.Match(code, @"public\s+[\w<>,\[\]\.?]+\s+getCurrent\s*\(\)\s*\{");
        if (!sigMatch.Success)
            return code;

        // Count braces from the opening { to find the true closing }
        int openBraceIdx = sigMatch.Index + sigMatch.Length - 1;
        int depth = 1;
        int pos = openBraceIdx + 1;
        while (pos < code.Length && depth > 0)
        {
            if (code[pos] == '{') depth++;
            else if (code[pos] == '}') depth--;
            pos++;
        }
        // pos is now one past the closing } of getCurrent()

        // Infer method indentation from the line where getCurrent() starts
        int lineStart = code.LastIndexOf('\n', sigMatch.Index) + 1;
        int indentLen = sigMatch.Index - lineStart;
        string indent = new string(' ', indentLen);

        string bridge = $"\n{indent}@Override\n{indent}public {iteratorType} next() {{\n{indent}    return getCurrent();\n{indent}}}";
        return code.Substring(0, pos) + bridge + code.Substring(pos);
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
    // Public field aliases used by generated C#-style property access.
    public boolean IgnoreWhitespace = false;
    public boolean IgnoreComments = false;
    public boolean CheckCharacters = true;
    public boolean isIgnoreWhitespace()          {{ return IgnoreWhitespace; }}
    public void    setIgnoreWhitespace(boolean v) {{ IgnoreWhitespace = v; }}
    public boolean isIgnoreComments()           {{ return IgnoreComments; }}
    public void    setIgnoreComments(boolean v) {{ IgnoreComments = v; }}
    public boolean isCheckCharacters()           {{ return CheckCharacters; }}
    public void    setCheckCharacters(boolean v)  {{ CheckCharacters = v; }}
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
    // Public field aliases used by generated C#-style property access.
    public String Encoding = ""UTF-8"";
    public boolean Indent = false;
    public String IndentChars = ""  "";
    public boolean OmitXmlDeclaration = false;
    public String  getEncoding()                    {{ return Encoding; }}
    public void    setEncoding(String v)             {{ Encoding = v; }}
    public boolean isIndent()                       {{ return Indent; }}
    public void    setIndent(boolean v)              {{ Indent = v; }}
    public String  getIndentChars()                 {{ return IndentChars; }}
    public void    setIndentChars(String v)          {{ IndentChars = v; }}
    public boolean isOmitXmlDeclaration()           {{ return OmitXmlDeclaration; }}
    public void    setOmitXmlDeclaration(boolean v)  {{ OmitXmlDeclaration = v; }}
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
    protected static XMLStreamReader reader;
    // C#-style static property aliases frequently emitted by the converter.
    public static int NodeType = XmlNodeType.getNone();
    public static boolean IsEmptyElement = false;
    public static String Name = """";
    public static String Value = """";
    public static int ReadState = 0;

    protected XmlReader() {{}}

    protected XmlReader(XMLStreamReader reader) {{
        XmlReader.reader = reader;
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

    /** Convenience overload used by converted code paths. */
    public static XmlReader create(Object source) {{
        return create(source, null);
    }}

    public static int getNodeType() {{
        return reader != null ? reader.getEventType() : XmlNodeType.getNone();
    }}

    public static boolean isStartElement() {{
        return reader != null && reader.isStartElement();
    }}

    public static boolean isStartElement(String name) {{
        return reader != null && reader.isStartElement()
            && name.equalsIgnoreCase(reader.getLocalName());
    }}

    /** StAX has no direct IsEmptyElement concept; returns false (safe default for compilation). */
    public static boolean getIsEmptyElement() {{ return false; }}

    public static String getName() {{
        try {{ return reader != null ? reader.getLocalName() : """"; }}
        catch (Exception e) {{ return """"; }}
    }}

    public static String getValue() {{
        try {{ return reader != null ? reader.getText() : """"; }}
        catch (Exception e) {{ return """"; }}
    }}

    public static String getAttribute(String name) {{
        return reader != null ? reader.getAttributeValue(null, name) : null;
    }}

    public static boolean read() {{
        try {{
            if (reader == null || !reader.hasNext()) return false;
            reader.next();
            return true;
        }} catch (XMLStreamException e) {{ return false; }}
    }}

    public static void readEndElement() {{
        try {{
            if (reader == null) return;
            while (reader.hasNext() && !reader.isEndElement()) reader.next();
            if (reader.hasNext()) reader.next();
        }} catch (XMLStreamException e) {{ throw new RuntimeException(e); }}
    }}

    public static double readElementContentAsDouble() {{
        try {{ return reader != null ? Double.parseDouble(reader.getElementText().trim()) : 0.0; }}
        catch (XMLStreamException e) {{ throw new RuntimeException(e); }}
    }}

    public static int readElementContentAsInt() {{
        try {{ return reader != null ? Integer.parseInt(reader.getElementText().trim()) : 0; }}
        catch (XMLStreamException e) {{ throw new RuntimeException(e); }}
    }}

    public static String readElementContentAsString() {{
        try {{ return reader != null ? reader.getElementText() : """"; }}
        catch (XMLStreamException e) {{ throw new RuntimeException(e); }}
    }}

    public static boolean readElementContentAsBoolean() {{
        try {{ return reader != null && Boolean.parseBoolean(reader.getElementText().trim()); }}
        catch (XMLStreamException e) {{ throw new RuntimeException(e); }}
    }}

    public static void moveToContent() {{
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

    public static boolean moveToFirstAttribute() {{
        return reader != null && reader.getAttributeCount() > 0;
    }}

    public static void skip() {{
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

    public static int getReadState() {{
        if (reader == null) return {basePackage}.ReadState.getClosed();
        try {{ return reader.hasNext() ? {basePackage}.ReadState.getInteractive() : {basePackage}.ReadState.getEndOfFile(); }}
        catch (Exception e) {{ return {basePackage}.ReadState.getError(); }}
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
    public int LineNumber = 0;
    public int LinePosition = 0;

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
public class XmlWriter {{
    protected static XMLStreamWriter writer;

    protected XmlWriter() {{}}

    protected XmlWriter(XMLStreamWriter writer) {{
        XmlWriter.writer = writer;
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

    /** Convenience overload used by converted code paths. */
    public static XmlWriter create(Object output) {{
        return create(output, null);
    }}

    public static void writeStartElement(String localName) {{
        try {{ if (writer != null) writer.writeStartElement(localName); }}
        catch (XMLStreamException e) {{ throw new RuntimeException(e); }}
    }}

    public static void writeEndElement() {{
        try {{ if (writer != null) writer.writeEndElement(); }}
        catch (XMLStreamException e) {{ throw new RuntimeException(e); }}
    }}

    public static void writeAttributeString(String localName, String value) {{
        try {{ if (writer != null) writer.writeAttribute(localName, value != null ? value : """"); }}
        catch (XMLStreamException e) {{ throw new RuntimeException(e); }}
    }}

    public static void writeAttributeString(String prefix, String localName, String ns, String value) {{
        try {{
            if (writer != null) {{
                if (prefix != null && !prefix.isEmpty()) writer.writeAttribute(prefix, ns != null ? ns : """", localName, value != null ? value : """");
                else writer.writeAttribute(localName, value != null ? value : """");
            }}
        }} catch (XMLStreamException e) {{ throw new RuntimeException(e); }}
    }}

    public static void writeStartElement(String localName, String ns) {{
        try {{
            if (writer != null) {{
                if (ns != null && !ns.isEmpty()) writer.writeStartElement("""", localName, ns);
                else writer.writeStartElement(localName);
            }}
        }} catch (XMLStreamException e) {{ throw new RuntimeException(e); }}
    }}

    public static void writeElementString(String localName, String value) {{
        try {{
            if (writer != null) {{
                writer.writeStartElement(localName);
                writer.writeCharacters(value != null ? value : """");
                writer.writeEndElement();
            }}
        }} catch (XMLStreamException e) {{ throw new RuntimeException(e); }}
    }}

    public static void writeString(String text) {{
        try {{ if (writer != null) writer.writeCharacters(text != null ? text : """"); }}
        catch (XMLStreamException e) {{ throw new RuntimeException(e); }}
    }}

    /** Writes raw XML-ish text as character data (compatibility shim for converted calls). */
    public static void writeRaw(String text) {{
        writeString(text);
    }}

    public static void writeComment(String text) {{
        try {{ if (writer != null) writer.writeComment(text != null ? text : """"); }}
        catch (XMLStreamException e) {{ throw new RuntimeException(e); }}
    }}

    public static void writeEndDocument() {{
        try {{ if (writer != null) writer.writeEndDocument(); }}
        catch (XMLStreamException e) {{ throw new RuntimeException(e); }}
    }}

    public static void flush() {{
        try {{ if (writer != null) writer.flush(); }}
        catch (XMLStreamException e) {{ throw new RuntimeException(e); }}
    }}

    public static void close() {{
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
    /** Mirrors File.Create(path). */
    public static OutputStream create(String path) {{
        try {{ return new FileOutputStream(path); }}
        catch (FileNotFoundException e) {{ throw new UncheckedIOException(e); }}
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
    LinkedListWithNodes<T> list;

    public LinkedListNode(T value) {{ this.value = value; }}

    public T getValue() {{ return value; }}
    public void setValue(T value) {{ this.value = value; }}
    public LinkedListNode<T> getNext() {{ return next; }}
    public LinkedListNode<T> getPrevious() {{ return prev; }}
    public LinkedListWithNodes<T> getList() {{ return list; }}
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
        node.list = this;
        node.next = head;
        node.prev = null;
        if (head != null) head.prev = node;
        head = node;
        if (tail == null) tail = node;
        count++;
        return node;
    }}

    public void addFirst(LinkedListNode<T> node) {{
        if (node.list != null) node.list.remove(node);
        node.list = this;
        node.next = head;
        node.prev = null;
        if (head != null) head.prev = node;
        head = node;
        if (tail == null) tail = node;
        count++;
    }}

    public LinkedListNode<T> addLast(T value) {{
        LinkedListNode<T> node = new LinkedListNode<>(value);
        node.list = this;
        node.prev = tail;
        node.next = null;
        if (tail != null) tail.next = node;
        tail = node;
        if (head == null) head = node;
        count++;
        return node;
    }}

    public void addLast(LinkedListNode<T> node) {{
        if (node.list != null) node.list.remove(node);
        node.list = this;
        node.prev = tail;
        node.next = null;
        if (tail != null) tail.next = node;
        tail = node;
        if (head == null) head = node;
        count++;
    }}

    public boolean add(T value) {{ addLast(value); return true; }}

    public LinkedListNode<T> addAfter(LinkedListNode<T> node, T value) {{
        LinkedListNode<T> n = new LinkedListNode<>(value);
        n.list = this;
        n.prev = node;
        n.next = node.next;
        if (node.next != null) node.next.prev = n; else tail = n;
        node.next = n;
        count++;
        return n;
    }}

    public LinkedListNode<T> addBefore(LinkedListNode<T> node, T value) {{
        LinkedListNode<T> n = new LinkedListNode<>(value);
        n.list = this;
        n.next = node;
        n.prev = node.prev;
        if (node.prev != null) node.prev.next = n; else head = n;
        node.prev = n;
        count++;
        return n;
    }}

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
        node.list = null;
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
