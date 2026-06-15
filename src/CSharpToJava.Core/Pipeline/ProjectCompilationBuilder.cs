using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using CSharpToJava.Core.Context;
using DiagSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace CSharpToJava.Core.Pipeline;

/// <summary>
/// Builds CSharpCompilation from source files with all necessary metadata references.
/// Extracted from ProjectConversionPipeline for maintainability.
/// </summary>
public static class ProjectCompilationBuilder
{

    /// <summary>
    /// 从源代码文件构建 CSharpCompilation
    /// </summary>
    public static CSharpCompilation? BuildCompilation(
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
            var hasParseError = false;
            foreach (var diagnostic in diagnostics)
            {
                if (diagnostic.Severity == DiagSeverity.Error)
                {
                    hasParseError = true;
                    context.Diagnostics.Warning(
                        $"Skipping parse-invalid file: {diagnostic.GetMessage()}",
                        diagnostic.Location);
                }
            }

            if (!hasParseError)
            {
                syntaxTrees.Add(syntaxTree);
            }
        }

        if (syntaxTrees.Count == 0)
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
                })
                .WithAllowUnsafe(true));

        context.ProjectCompilation = compilation;

        return compilation;
    }

    /// <summary>
    /// 获取元数据引用（系统程序集）
    /// </summary>
    public static List<MetadataReference> GetMetadataReferences()
    {
        var references = new List<MetadataReference>();
        var objectAssembly = typeof(object).Assembly.Location;
        var listAssembly = typeof(System.Collections.Generic.List<>).Assembly.Location;
        var jsonAssembly = typeof(System.Text.Json.JsonSerializer).Assembly.Location;

        if (!string.IsNullOrEmpty(objectAssembly) && File.Exists(objectAssembly))
        {
            references.Add(MetadataReference.CreateFromFile(objectAssembly));
        }

        if (!string.IsNullOrEmpty(listAssembly) && File.Exists(listAssembly))
        {
            references.Add(MetadataReference.CreateFromFile(listAssembly));
        }

        if (!string.IsNullOrEmpty(jsonAssembly) && File.Exists(jsonAssembly))
        {
            references.Add(MetadataReference.CreateFromFile(jsonAssembly));
        }

        // Add common .NET assemblies
        var dotnetAssemblies = new[]
        {
            "System.Runtime.dll",
            "System.Collections.dll",
            "System.Collections.NonGeneric.dll",  // System.Collections.Queue, Stack, Hashtable, ArrayList
            "System.Linq.dll",
            "System.Threading.dll",
            "System.Threading.Tasks.dll",
            "System.Threading.Tasks.Parallel.dll", // ParallelOptions, Parallel.ForEach, etc.
            "netstandard.dll",
        };

        var frameworkDir = Path.GetDirectoryName(objectAssembly);
        if (string.IsNullOrEmpty(frameworkDir) || !Directory.Exists(frameworkDir))
        {
            return references;
        }

        foreach (var assembly in dotnetAssemblies)
        {
            var path = Path.Combine(frameworkDir, assembly);
            if (File.Exists(path))
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }
        }

        return references;
    }

}
