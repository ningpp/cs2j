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

    public static void AddImportsFromTypeUsings(
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
    public static List<ConversionResult> CreateFailureResults(
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
    public static ConversionResult? ConvertEnumTypeGroup(
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
    public static ConversionResult? ConvertDelegateTypeGroup(
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
}
