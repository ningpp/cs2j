using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.LinqRewrite;
using CSharpToJava.Core.PartialType;
using CSharpToJava.Core.Transformers;
using CSharpToJava.Core.Transformers.Type;
using CSharpToJava.Core.Visitors;
using CSharpToJava.TypeMapping;

namespace CSharpToJava.Core.Pipeline;

/// <summary>
/// Converts type groups (classes, enums, delegates) from C# to Java.
/// Handles partial type merging, LINQ rewriting, and import resolution.
/// Extracted from ProjectConversionPipeline for maintainability.
/// </summary>
public static class TypeGroupResolver
{

    /// <summary>
    /// 转换单个类型组（可能是合并后的 partial 类型）
    /// </summary>
    public static ConversionResult? ConvertTypeGroup(
        PartialTypeGroup typeGroup,
        CSharpCompilation compilation,
        ConversionContext context,
        IReadOnlyList<Java.JavaSyntaxRewriter>? irRewriters = null)
    {
        // 每次转换一个类型前清空导入集合和别名注册表，避免跨文件污染
        context.ClearImports();
        context.ClearAliases();

        // Skip nested types — they are processed through their parent class via ClassTransformer.TransformMerged
        if (typeGroup.TypeSymbol.ContainingType != null)
            return null;

        try
        {
            // Handle Enum types specially — EnumDeclarationSyntax is not TypeDeclarationSyntax
            if (typeGroup.TypeSymbol.TypeKind == TypeKind.Enum)
                return ConvertEnumTypeGroup(typeGroup, compilation, context, irRewriters);

            // Handle Delegate types specially
            if (typeGroup.TypeSymbol.TypeKind == TypeKind.Delegate)
                return ConvertDelegateTypeGroup(typeGroup, compilation, context, irRewriters);

            // Check if we have any syntax nodes
            if (typeGroup.SyntaxNodes.Count == 0)
            {
                var declaringRefs = typeGroup.TypeSymbol.DeclaringSyntaxReferences;
                var sourceLocations = new List<string>();
                foreach (var loc in typeGroup.TypeSymbol.Locations)
                {
                    if (loc.IsInSource)
                    {
                        var path = loc.GetLineSpan().Path;
                        if (!string.IsNullOrWhiteSpace(path) && !sourceLocations.Contains(path))
                        {
                            sourceLocations.Add(path);
                        }
                    }
                }

                var detail = $"Type '{typeGroup.TypeSymbol.Name}' has no syntax nodes. " +
                    $"PartialParts={typeGroup.PartialParts.Count}, " +
                    $"DeclaringSyntaxReferences={declaringRefs.Length}, " +
                    $"IsPartial={typeGroup.IsPartial}, " +
                    $"TypeKind={typeGroup.TypeSymbol.TypeKind}, " +
                    $"ContainingNamespace='{typeGroup.TypeSymbol.ContainingNamespace?.ToDisplayString() ?? "(global)"}'";
                if (sourceLocations.Count > 0)
                {
                    detail += $", SourceFiles=[{string.Join(", ", sourceLocations)}]";
                }
                if (declaringRefs.Length > 0)
                {
                    var refTrees = new List<string>();
                    foreach (var r in declaringRefs)
                    {
                        var fp = r.SyntaxTree?.FilePath ?? "<unknown>";
                        if (!refTrees.Contains(fp))
                        {
                            refTrees.Add(fp);
                        }
                    }
                    detail += $", RefSyntaxTrees=[{string.Join(", ", refTrees)}]";
                }
                bool containsAny = false;
                if (declaringRefs.Length > 0)
                {
                    foreach (var t in compilation.SyntaxTrees)
                    {
                        foreach (var r in declaringRefs)
                        {
                            if (ReferenceEquals(r.SyntaxTree, t))
                            {
                                containsAny = true;
                                break;
                            }
                        }
                        if (containsAny) break;
                    }
                }
                detail += $", CompilationContainsMyTree={containsAny}";
                int totalTreeCount = 0;
                foreach (var tmpT in compilation.SyntaxTrees)
                    totalTreeCount++;
                detail = detail + ", CompilationTreeCount=" + totalTreeCount;

                Location? firstSourceLoc = null;
                foreach (var loc in typeGroup.TypeSymbol.Locations)
                {
                    if (loc.IsInSource) { firstSourceLoc = loc; break; }
                }

                return new ConversionResult
                {
                    Success = false,
                    FileName = $"{typeGroup.TypeSymbol.Name}.java",
                    SourceFilePath = sourceLocations.Count > 0 ? sourceLocations[0] : null,
                    Diagnostics = new List<Context.DiagnosticMessage>
                    {
                        new(Context.DiagnosticSeverity.Error, detail, firstSourceLoc)
                    }
                };
            }

            // Find a syntax tree that is actually part of this compilation (guard against SourceLink / PDB trees)
            SyntaxTree? validSyntaxTree = null;
            foreach (var n in typeGroup.SyntaxNodes)
            {
                if (compilation.ContainsSyntaxTree(n.SyntaxTree))
                {
                    validSyntaxTree = n.SyntaxTree;
                    break;
                }
            }

            if (validSyntaxTree == null)
            {
                var sourceLocations = new List<string>();
                foreach (var n in typeGroup.SyntaxNodes)
                {
                    var fp = n.SyntaxTree.FilePath;
                    if (!string.IsNullOrWhiteSpace(fp) && !sourceLocations.Contains(fp))
                    {
                        sourceLocations.Add(fp);
                    }
                }

                var compilationTrees = new List<string>();
                int treeCount = 0;
                foreach (var t in compilation.SyntaxTrees)
                {
                    if (!string.IsNullOrWhiteSpace(t.FilePath))
                    {
                        if (treeCount < 10)
                        {
                            compilationTrees.Add(t.FilePath);
                        }
                        treeCount++;
                    }
                }

                var detail = "Type '" + typeGroup.TypeSymbol.Name + "': no syntax tree belongs to current compilation. " +
                    "NodeCount=" + typeGroup.SyntaxNodes.Count + ", " +
                    "NodeTrees=[" + string.Join(", ", sourceLocations) + "], " +
                    "CompilationTreeCount=" + treeCount;
                if (compilationTrees.Count > 0)
                {
                    detail += $", CompilationTrees=[{string.Join(", ", compilationTrees)}]";
                }

                return new ConversionResult
                {
                    Success = false,
                    FileName = $"{typeGroup.TypeSymbol.Name}.java",
                    SourceFilePath = sourceLocations.Count > 0 ? sourceLocations[0] : null,
                    Diagnostics = new List<Context.DiagnosticMessage>
                    {
                        new(Context.DiagnosticSeverity.Error, detail,
                            typeGroup.SyntaxNodes.Count > 0 ? typeGroup.SyntaxNodes[0].GetLocation() : null)
                    }
                };
            }

            // Get semantic model for the first syntax tree
            var semanticModel = compilation.GetSemanticModel(validSyntaxTree);
            var typeNamespace = typeGroup.TypeSymbol.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            context.EnterNamespace(typeNamespace);
            context.CurrentEnclosingRoslynType = typeGroup.TypeSymbol;
            try
            {
                context.SemanticModel = semanticModel;
                AddImportsFromTypeUsings(typeGroup, context, compilation);

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
                        TypeDeclarationSyntax? structNode = null;
                        foreach (var n in typeGroup.SyntaxNodes)
                        {
                            if (compilation.ContainsSyntaxTree(n.SyntaxTree))
                            {
                                structNode = n;
                                break;
                            }
                        }
                        javaType = structNode != null
                            ? new StructTransformer().Transform(structNode, context)
                            : null;
                        break;
                    case TypeKind.Interface:
                        TypeDeclarationSyntax? ifaceNode = null;
                        foreach (var n in typeGroup.SyntaxNodes)
                        {
                            if (compilation.ContainsSyntaxTree(n.SyntaxTree))
                            {
                                ifaceNode = n;
                                break;
                            }
                        }
                        javaType = ifaceNode != null
                            ? new InterfaceTransformer().Transform(ifaceNode, context)
                            : null;
                        break;
                    default:
                        return null;
                }

                // Emit any synthesized records (from anonymous types) as nested types
                if (javaType is JavaClassDeclaration classDecl && context.SynthesizedRecords.Count > 0)
                {
                    foreach (var rec in context.SynthesizedRecords)
                    {
                        var recordDecl = new JavaClassDeclaration
                        {
                            Name = rec.RecordName,
                            IsRecord = true,
                            Modifiers = JavaModifiers.Private | JavaModifiers.Static,
                        };
                        foreach (var field in rec.Fields)
                        {
                            recordDecl.RecordComponents.Add(new JavaRecordComponent(field.JavaType, field.Name));
                        }
                        classDecl.NestedTypes.Add(recordDecl);
                    }
                    context.ClearSynthesizedRecords();
                }

                // Generate Java code
                if (javaType != null)
                {
                    // Determine package from namespace (empty string = global namespace, handled by NamespaceToPackage)
                    var ns = typeGroup.TypeSymbol.ContainingNamespace?.ToDisplayString() ?? "";
                    var rawPkg = context.NamespaceToPackage(ns);
                    var pkg = string.IsNullOrEmpty(rawPkg) ? null : rawPkg;

                    // Get source file path for diagnostics
                    string? sourceFilePath = null;
                    foreach (var loc in typeGroup.TypeSymbol.Locations)
                    {
                        if (loc.IsInSource)
                        {
                            sourceFilePath = loc.GetLineSpan().Path;
                            if (!string.IsNullOrWhiteSpace(sourceFilePath))
                                break;
                        }
                    }

                    // Build a JavaCompilationUnit with structured imports
                    var javaCompilation = new Java.JavaCompilationUnit(pkg);

                    // Standard JDK wildcard imports
                    javaCompilation.Imports.Add(new Java.JavaImport("java.util", isWildcard: true));
                    javaCompilation.Imports.Add(new Java.JavaImport("java.util.function", isWildcard: true));
                    javaCompilation.Imports.Add(new Java.JavaImport("java.util.stream", isWildcard: true));
                    javaCompilation.Imports.Add(new Java.JavaImport("java.io", isWildcard: true));

                    // Imports collected during conversion (type-specific)
                    foreach (var imp in context.ImportedTypes)
                    {
                        if (imp.EndsWith(".*", StringComparison.Ordinal))
                            javaCompilation.Imports.Add(new Java.JavaImport(imp[..^2], isWildcard: true));
                        else
                            javaCompilation.Imports.Add(new Java.JavaImport(imp));
                    }

                    javaCompilation.TypeDeclarations.Add(javaType);

                    // IR-level post-processing
                    if (irRewriters != null)
                    {
                        foreach (var rewriter in irRewriters)
                            rewriter.VisitCompilationUnit(javaCompilation);
                    }

                    return new ConversionResult
                    {
                        Success = true,
                        GeneratedCode = javaCompilation.ToString(""),
                        Compilation = javaCompilation,
                        Diagnostics = new List<Context.DiagnosticMessage>(),
                        FileName = $"{javaType.Name}.java",
                        SourceFilePath = sourceFilePath,
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
            var sourceLocations = new List<string>();
            foreach (var loc in typeGroup.TypeSymbol.Locations)
            {
                if (loc.IsInSource)
                {
                    var span = loc.GetLineSpan();
                    var entry = $"{span.Path}:{span.StartLinePosition.Line + 1}";
                    if (!sourceLocations.Contains(entry))
                    {
                        sourceLocations.Add(entry);
                        if (sourceLocations.Count >= 5) break;
                    }
                }
            }

            var sourceInfo = sourceLocations.Count > 0
                ? $" Source: [{string.Join(", ", sourceLocations)}]"
                : $" Namespace: '{typeGroup.TypeSymbol.ContainingNamespace?.ToDisplayString() ?? "(global)"}'";

            string? srcFilePath = null;
            Location? firstLoc = null;
            foreach (var loc in typeGroup.TypeSymbol.Locations)
            {
                if (loc.IsInSource)
                {
                    firstLoc = loc;
                    srcFilePath = loc.GetLineSpan().Path;
                    break;
                }
            }

            return new ConversionResult
            {
                Success = false,
                FileName = $"{typeGroup.TypeSymbol.Name}.java",
                SourceFilePath = srcFilePath,
                Diagnostics = new List<Context.DiagnosticMessage>
                {
                    new(Context.DiagnosticSeverity.Error,
                        $"Failed to convert type '{typeGroup.TypeSymbol.Name}' ({typeGroup.TypeSymbol.TypeKind}): {ex.Message}{sourceInfo}\n--- STACK TRACE ---\n{ex.StackTrace}\n--- INNER ---\n{(ex.InnerException != null ? $"{ex.InnerException.Message}\n{ex.InnerException.StackTrace}" : "none")}",
                        firstLoc)
                }
            };
        }
    }

    public static void AddImportsFromTypeUsings(
        PartialTypeGroup typeGroup,
        ConversionContext context,
        CSharpCompilation compilation)
    {
        var syntaxTrees = new List<SyntaxTree>();
        foreach (var n in typeGroup.SyntaxNodes)
        {
            if (compilation.ContainsSyntaxTree(n.SyntaxTree) && !syntaxTrees.Contains(n.SyntaxTree))
            {
                syntaxTrees.Add(n.SyntaxTree);
            }
        }

        if (syntaxTrees.Count == 0)
        {
            foreach (var r in typeGroup.TypeSymbol.DeclaringSyntaxReferences)
            {
                if (r.SyntaxTree != null && compilation.ContainsSyntaxTree(r.SyntaxTree) && !syntaxTrees.Contains(r.SyntaxTree))
                {
                    syntaxTrees.Add(r.SyntaxTree);
                }
            }
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
                    // Fallback: when semantic model can't resolve the alias target,
                    // try to find the type in the compilation
                    if (aliasTargetSymbol == null)
                    {
                        var aliasFullName = usingDirective.Name.ToString();
                        aliasTargetSymbol = compilation.GetTypeByMetadataName(aliasFullName);
                        // GetTypeByMetadataName expects backtick format (e.g. "Foo`2"),
                        // not angle-bracket format (e.g. "Foo<T1,T2>"). Convert if needed.
                        if (aliasTargetSymbol == null)
                        {
                            var angleIdx = aliasFullName.IndexOf('<');
                            if (angleIdx > 0)
                            {
                                var closeIdx = aliasFullName.LastIndexOf('>');
                                if (closeIdx > angleIdx)
                                {
                                    var typeArgsStr = aliasFullName[(angleIdx + 1)..closeIdx];
                                    int arity = 1 + typeArgsStr.Count(c => c == ',');
                                    var baseName = aliasFullName[..angleIdx];
                                    aliasTargetSymbol = compilation.GetTypeByMetadataName($"{baseName}`{arity}");
                                }
                            }
                        }
                    }
                    // Register the alias so downstream type mapping can resolve it
                    if (aliasTargetSymbol is ITypeSymbol aliasType)
                    {
                        var aliasName = usingDirective.Alias.Name.Identifier.Text;
                        context.RegisterUsingAlias(aliasName, aliasType, usingDirective.Alias.GetLocation());
                    }

                    continue;
                }

                var ns = usingDirective.Name.ToString();
                if (string.IsNullOrWhiteSpace(ns))
                {
                    continue;
                }

                // Standard JDK wildcard imports are already emitted in file headers.
                // However, System.* namespaces with explicit namespace mappings
                // (e.g. "System.Xml" → "dotnet.xml") must still generate imports
                // because they map to compat runtime packages that actually exist.
                if (ns.StartsWith("System.", StringComparison.Ordinal) || ns == "System")
                {
                    if (ns != "System" && context.TypeMappings.HasExplicitNamespaceMapping(ns))
                    {
                        var systemMapped = context.NamespaceToPackage(ns);
                        if (!string.IsNullOrWhiteSpace(systemMapped))
                        {
                            if (!systemMapped.EndsWith(".*", StringComparison.Ordinal))
                                systemMapped += ".*";
                            context.ImportedTypes.Add(systemMapped);
                        }
                    }
                    continue;
                }

                // Skip imports for namespaces that have no types defined in project source.
                // Java wildcard imports fail when the target package has no classes.
                var nsSymbol = semanticModel.GetSymbolInfo(usingDirective.Name).Symbol as INamespaceSymbol;
                if (nsSymbol != null && !nsSymbol.GetTypeMembers().Any(t => t.Locations.Any(l => l.IsInSource)))
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
    public static List<ConversionResult> CreateFailureResults(
        IEnumerable<SourceFile> sourceFiles,
        ConversionContext context)
    {
        var results = new List<ConversionResult>();
        foreach (var file in sourceFiles)
        {
            results.Add(new ConversionResult
            {
                Success = false,
                FileName = file.FilePath,
                Diagnostics = context.Diagnostics.Messages.ToList()
            });
        }
        return results;
    }

    /// <summary>
    /// Converts an enum type group using EnumTransformer.
    /// </summary>
    public static ConversionResult? ConvertEnumTypeGroup(
        PartialTypeGroup typeGroup,
        CSharpCompilation compilation,
        ConversionContext context,
        IReadOnlyList<Java.JavaSyntaxRewriter>? irRewriters = null)
    {
        foreach (var syntaxRef in typeGroup.TypeSymbol.DeclaringSyntaxReferences)
        {
            try
            {
                var syntax = syntaxRef.GetSyntax();
                if (syntax is not EnumDeclarationSyntax enumSyntax) continue;
                if (!compilation.ContainsSyntaxTree(syntax.SyntaxTree)) continue;

                var nsName = typeGroup.TypeSymbol.ContainingNamespace?.ToDisplayString() ?? "";
                context.EnterNamespace(nsName);
                try
                {
                    context.SemanticModel = compilation.GetSemanticModel(syntax.SyntaxTree);
                    AddImportsFromTypeUsings(typeGroup, context, compilation);

                    var transformer = new EnumTransformer();
                    var javaEnum = transformer.TransformEnum(enumSyntax, context);
                    var rawPkg2 = context.NamespaceToPackage(nsName);
                    var pkg = string.IsNullOrEmpty(rawPkg2) ? null : rawPkg2;

                    // Build a JavaCompilationUnit with structured imports
                    var javaCompilation = new Java.JavaCompilationUnit(pkg);

                    javaCompilation.Imports.Add(new Java.JavaImport("java.util", isWildcard: true));
                    javaCompilation.Imports.Add(new Java.JavaImport("java.util.function", isWildcard: true));
                    javaCompilation.Imports.Add(new Java.JavaImport("java.util.stream", isWildcard: true));
                    javaCompilation.Imports.Add(new Java.JavaImport("java.io", isWildcard: true));

                    foreach (var imp in context.ImportedTypes)
                    {
                        if (imp.EndsWith(".*", StringComparison.Ordinal))
                            javaCompilation.Imports.Add(new Java.JavaImport(imp[..^2], isWildcard: true));
                        else
                            javaCompilation.Imports.Add(new Java.JavaImport(imp));
                    }

                    javaCompilation.TypeDeclarations.Add(javaEnum);

                    if (irRewriters != null)
                    {
                        foreach (var rewriter in irRewriters)
                            rewriter.VisitCompilationUnit(javaCompilation);
                    }

                    return new ConversionResult
                    {
                        Success = true,
                        GeneratedCode = javaCompilation.ToString(""),
                        Compilation = javaCompilation,
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
    public static ConversionResult? ConvertDelegateTypeGroup(
        PartialTypeGroup typeGroup,
        CSharpCompilation compilation,
        ConversionContext context,
        IReadOnlyList<Java.JavaSyntaxRewriter>? irRewriters = null)
    {
        foreach (var syntaxRef in typeGroup.TypeSymbol.DeclaringSyntaxReferences)
        {
            try
            {
                var syntax = syntaxRef.GetSyntax();
                if (syntax is not DelegateDeclarationSyntax delegateSyntax) continue;
                if (!compilation.ContainsSyntaxTree(syntax.SyntaxTree)) continue;

                var delNsName = typeGroup.TypeSymbol.ContainingNamespace?.ToDisplayString() ?? "";
                context.EnterNamespace(delNsName);
                try
                {
                    context.SemanticModel = compilation.GetSemanticModel(syntax.SyntaxTree);
                    AddImportsFromTypeUsings(typeGroup, context, compilation);

                    var transformer = new DelegateTransformer();
                    var javaInterface = transformer.TransformDelegate(delegateSyntax, context);
                    if (javaInterface == null) return null;

                    var rawPkg3 = context.NamespaceToPackage(delNsName);
                    var pkg = string.IsNullOrEmpty(rawPkg3) ? null : rawPkg3;

                    // Build a JavaCompilationUnit with structured imports
                    var javaCompilation = new Java.JavaCompilationUnit(pkg);

                    javaCompilation.Imports.Add(new Java.JavaImport("java.util", isWildcard: true));
                    javaCompilation.Imports.Add(new Java.JavaImport("java.util.function", isWildcard: true));
                    javaCompilation.Imports.Add(new Java.JavaImport("java.util.stream", isWildcard: true));
                    javaCompilation.Imports.Add(new Java.JavaImport("java.io", isWildcard: true));

                    foreach (var imp in context.ImportedTypes)
                    {
                        if (imp.EndsWith(".*", StringComparison.Ordinal))
                            javaCompilation.Imports.Add(new Java.JavaImport(imp[..^2], isWildcard: true));
                        else
                            javaCompilation.Imports.Add(new Java.JavaImport(imp));
                    }

                    javaCompilation.TypeDeclarations.Add(javaInterface);

                    if (irRewriters != null)
                    {
                        foreach (var rewriter in irRewriters)
                            rewriter.VisitCompilationUnit(javaCompilation);
                    }

                    return new ConversionResult
                    {
                        Success = true,
                        GeneratedCode = javaCompilation.ToString(""),
                        Compilation = javaCompilation,
                        FileName = $"{javaInterface.Name}.java",
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
}
