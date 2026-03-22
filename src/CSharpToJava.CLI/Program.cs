using CommandLine;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.TypeMapping;
using System.Text.RegularExpressions;

namespace CSharpToJava.CLI;

class Program
{
    static async Task<int> Main(string[] args)
    {
        return await Parser.Default.ParseArguments<ConvertOptions, ConvertProjectOptions, AnalyzeOptions>(args)
            .MapResult(
                (ConvertOptions opts) => ConvertFile(opts),
                (ConvertProjectOptions opts) => ConvertProject(opts),
                (AnalyzeOptions opts) => AnalyzeProject(opts),
                errs => Task.FromResult(1)
            );
    }

    private static async Task<int> ConvertFile(ConvertOptions opts)
    {
        try
        {
            // 读取源文件
            if (!File.Exists(opts.Input))
            {
                Console.Error.WriteLine($"Error: Input file not found: {opts.Input}");
                return 1;
            }

            var sourceCode = await File.ReadAllTextAsync(opts.Input);

            // 配置转换选项
            var options = new ConversionOptions
            {
                TargetJavaVersion = Enum.Parse<JavaVersion>(opts.JavaVersion, true),
                TypeMappingConfigPath = opts.MappingConfig,
                GenerateJavaDoc = opts.GenerateJavaDoc,
                UseRecords = opts.UseRecords,
                UseOptionalForNullable = opts.UseOptionalForNullable,
                EnableLinqRewrite = opts.EnableLinqRewrite,
                PreferStreamApi = opts.PreferStreamApi
            };

            // 执行转换
            var pipeline = new ConversionPipeline();
            var result = pipeline.Convert(new ConversionRequest
            {
                SourceCode = sourceCode,
                Options = options,
                FileName = opts.Input
            });

            // 输出结果
            if (opts.Output != null)
            {
                // 使用 UTF-8 without BOM 编码写入Java文件
                await File.WriteAllTextAsync(opts.Output, result.GeneratedCode, new System.Text.UTF8Encoding(false));
                Console.WriteLine($"Successfully converted to: {opts.Output}");
            }
            else
            {
                Console.WriteLine(result.GeneratedCode);
            }

            // 输出诊断信息
            if (opts.Verbose && result.Diagnostics.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Diagnostics:");
                foreach (var diag in result.Diagnostics)
                {
                    var prefix = diag.Severity switch
                    {
                        DiagnosticSeverity.Error => "ERROR",
                        DiagnosticSeverity.Warning => "WARNING",
                        _ => "INFO"
                    };
                    Console.WriteLine($"  [{prefix}] {FormatDiagnostic(diag)}");
                }
            }

            return result.Success ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (opts.Verbose)
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return 1;
        }
    }

    private static async Task<int> ConvertProject(ConvertProjectOptions opts)
    {
        try
        {
            var hasDirectorySource = Directory.Exists(opts.Source);
            var hasProjectSource = File.Exists(opts.Source) && Path.GetExtension(opts.Source).Equals(".csproj", StringComparison.OrdinalIgnoreCase);
            if (!hasDirectorySource && !hasProjectSource)
            {
                Console.Error.WriteLine($"Error: Source not found: {opts.Source}");
                return 1;
            }

            // 创建输出目录
            if (Directory.Exists(opts.Destination))
            {
                if (!opts.Force)
                {
                    Console.Error.WriteLine($"Error: Destination directory already exists: {opts.Destination}. Use --force to overwrite.");
                    return 1;
                }
            }
            else
            {
                Directory.CreateDirectory(opts.Destination);
            }

            // 配置转换选项
            var options = new ConversionOptions
            {
                TargetJavaVersion = Enum.Parse<JavaVersion>(opts.JavaVersion, true),
                TypeMappingConfigPath = opts.MappingConfig,
                GenerateJavaDoc = opts.GenerateJavaDoc,
                UseRecords = opts.UseRecords,
                UseOptionalForNullable = opts.UseOptionalForNullable,
                EnableLinqRewrite = opts.EnableLinqRewrite,
                PreferStreamApi = opts.PreferStreamApi
            };

            // 优先走 .csproj 模式（支持项目引用图、测试分类和 resources 分流）
            if (ProjectDiscovery.TryResolveProjectEntry(opts.Source, out var entryProject))
            {
                return await ConvertFromProjectGraph(opts, options, entryProject);
            }

            // 兼容旧目录模式
            var outputRoot = opts.GeneratePom
                ? Path.Combine(opts.Destination, "src", "main", "java")
                : opts.Destination;

            if (opts.Force && Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }

            if (!Directory.Exists(outputRoot))
            {
                Directory.CreateDirectory(outputRoot);
            }

            var pipeline = new ConversionPipeline();
            var results = await pipeline.ConvertProjectWithPartialMergeAsync(opts.Source, options);
            int successCount = 0;
            int failureCount = 0;

            foreach (var result in results)
            {
                if (result.FileName == null) continue;

                if (TryWriteConvertedFile(result, opts.Source, outputRoot, out var outputPath))
                {
                    successCount++;
                    if (opts.Verbose)
                    {
                        Console.WriteLine($"Converted: {result.FileName} -> {outputPath}");
                    }
                }
                else
                {
                    failureCount++;
                    Console.Error.WriteLine($"Failed: {result.FileName}");
                    foreach (var diag in result.Diagnostics)
                    {
                        Console.Error.WriteLine($"  [{diag.Severity}] {FormatDiagnostic(diag)}");
                    }
                }
            }

            // 生成 Maven pom.xml
            if (opts.GeneratePom)
            {
                var artifactId = new DirectoryInfo(opts.Destination).Name;
                var pomContent = GenerateMavenPom(artifactId, opts.MavenGroupId, opts.MavenVersion, opts.JavaVersion, includeTests: false);
                var pomPath = Path.Combine(opts.Destination, "pom.xml");
                // pom.xml 使用 UTF-8 without BOM
                await File.WriteAllTextAsync(pomPath, pomContent, new System.Text.UTF8Encoding(false));
                Console.WriteLine($"Generated Maven pom.xml: {pomPath}");
            }

            Console.WriteLine();
            Console.WriteLine($"Conversion complete: {successCount} succeeded, {failureCount} failed");

            return failureCount > 0 ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (opts.Verbose)
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return 1;
        }
    }

    private static async Task<int> ConvertFromProjectGraph(ConvertProjectOptions opts, ConversionOptions options, string entryProject)
    {
        var graph = ProjectDiscovery.LoadProjectGraph(entryProject);
        if (graph.ProjectsInTopologicalOrder.Count == 0)
        {
            Console.Error.WriteLine($"Error: No project discovered from {entryProject}");
            return 1;
        }

        if (opts.Mode.Equals("multi-module", StringComparison.OrdinalIgnoreCase))
        {
            return await ConvertFromProjectGraphMultiModule(opts, options, graph);
        }

        return await ConvertFromProjectGraphSingleModule(opts, options, graph);
    }

    private static async Task<int> ConvertFromProjectGraphSingleModule(ConvertProjectOptions opts, ConversionOptions options, ProjectGraph graph)
    {

        var mainJavaRoot = opts.GeneratePom
            ? Path.Combine(opts.Destination, "src", "main", "java")
            : opts.Destination;
        var testJavaRoot = opts.GeneratePom
            ? Path.Combine(opts.Destination, "src", "test", "java")
            : Path.Combine(opts.Destination, "test");
        var mainResourcesRoot = opts.GeneratePom
            ? Path.Combine(opts.Destination, "src", "main", "resources")
            : Path.Combine(opts.Destination, "resources");
        var testResourcesRoot = opts.GeneratePom
            ? Path.Combine(opts.Destination, "src", "test", "resources")
            : Path.Combine(opts.Destination, "test-resources");

        if (opts.Force)
        {
            DeleteIfExists(mainJavaRoot);
            DeleteIfExists(testJavaRoot);
            DeleteIfExists(mainResourcesRoot);
            DeleteIfExists(testResourcesRoot);
        }

        Directory.CreateDirectory(mainJavaRoot);
        Directory.CreateDirectory(mainResourcesRoot);
        if (opts.IncludeTests)
        {
            Directory.CreateDirectory(testJavaRoot);
            Directory.CreateDirectory(testResourcesRoot);
        }

        var pipeline = new ConversionPipeline();
        int successCount = 0;
        int failureCount = 0;
        int copiedResourceCount = 0;

        foreach (var project in graph.ProjectsInTopologicalOrder)
        {
            if (!opts.IncludeTests && project.Kind == ProjectKind.Test)
            {
                continue;
            }

            var isTest = project.Kind == ProjectKind.Test;
            if (opts.Verbose)
            {
                Console.WriteLine($"Converting project [{project.Kind}]: {project.Name}");
            }

            var targetJavaRoot = isTest ? testJavaRoot : mainJavaRoot;
            var targetResourcesRoot = isTest ? testResourcesRoot : mainResourcesRoot;

            var semanticContextDirs = GetReferencedProjectDirectories(project, graph);
            var results = await pipeline.ConvertProjectWithPartialMergeAsync(project.ProjectDirectory, options, semanticContextDirs);
            foreach (var result in results)
            {
                if (result.FileName == null) continue;

                if (TryWriteConvertedFile(result, project.ProjectDirectory, targetJavaRoot, out var outputPath))
                {
                    successCount++;
                    if (opts.Verbose)
                    {
                        Console.WriteLine($"Converted: {result.FileName} -> {outputPath}");
                    }
                }
                else
                {
                    failureCount++;
                    Console.Error.WriteLine($"Failed: {result.FileName}");
                    foreach (var diag in result.Diagnostics)
                    {
                        Console.Error.WriteLine($"  [{diag.Severity}] {FormatDiagnostic(diag)}");
                    }
                }
            }

            foreach (var resource in project.ResourceItems)
            {
                var destination = Path.Combine(targetResourcesRoot, resource.RelativePath);
                var destinationDir = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(destinationDir))
                {
                    Directory.CreateDirectory(destinationDir);
                }

                File.Copy(resource.SourcePath, destination, overwrite: true);
                copiedResourceCount++;

                if (opts.Verbose)
                {
                    Console.WriteLine($"Resource: {resource.SourcePath} -> {destination}");
                }
            }
        }

        if (opts.GeneratePom)
        {
            var artifactId = new DirectoryInfo(opts.Destination).Name;
            var pomContent = GenerateMavenPom(artifactId, opts.MavenGroupId, opts.MavenVersion, opts.JavaVersion, includeTests: opts.IncludeTests);
            var pomPath = Path.Combine(opts.Destination, "pom.xml");
            await File.WriteAllTextAsync(pomPath, pomContent, new System.Text.UTF8Encoding(false));
            Console.WriteLine($"Generated Maven pom.xml: {pomPath}");
        }

        Console.WriteLine();
        Console.WriteLine($"Conversion complete: {successCount} succeeded, {failureCount} failed, {copiedResourceCount} resources copied");

        return failureCount > 0 ? 1 : 0;
    }

    private static async Task<int> ConvertFromProjectGraphMultiModule(ConvertProjectOptions opts, ConversionOptions options, ProjectGraph graph)
    {
        var plan = MultiModulePlanner.Build(graph, opts.IncludeTests);
        if (plan.ModulesInBuildOrder.Count == 0)
        {
            Console.Error.WriteLine("Error: No modules planned for conversion.");
            return 1;
        }

        var sharedCompatibilityPackage = BuildSharedCompatibilityPackage(opts.MavenGroupId);
        var sharedCompatibilityModuleName = MakeUniqueModuleName("csharptojava-compat", plan.ModulesInBuildOrder.Select(m => m.Name));
        var compatModule = new PlannedModule
        {
            Name = sharedCompatibilityModuleName,
            IsTestOnly = false,
        };

        foreach (var module in plan.ModulesInBuildOrder)
        {
            module.CompileDependencies.Add(sharedCompatibilityModuleName);
        }

        var modulesInBuildOrder = new List<PlannedModule> { compatModule };
        modulesInBuildOrder.AddRange(plan.ModulesInBuildOrder);

        options.EmitCompatibilityHelpers = false;
        options.SharedCompatibilityPackage = sharedCompatibilityPackage;

        if (opts.Force && Directory.Exists(opts.Destination))
        {
            Directory.Delete(opts.Destination, recursive: true);
            Directory.CreateDirectory(opts.Destination);
        }

        int successCount = 0;
        int failureCount = 0;
        int copiedResourceCount = 0;

        var pipeline = new ConversionPipeline();

        foreach (var module in modulesInBuildOrder)
        {
            var moduleRoot = Path.Combine(opts.Destination, module.Name);
            var mainJavaRoot = Path.Combine(moduleRoot, "src", "main", "java");
            var testJavaRoot = Path.Combine(moduleRoot, "src", "test", "java");
            var mainResourcesRoot = Path.Combine(moduleRoot, "src", "main", "resources");
            var testResourcesRoot = Path.Combine(moduleRoot, "src", "test", "resources");

            if (!module.IsTestOnly)
            {
                Directory.CreateDirectory(mainJavaRoot);
                Directory.CreateDirectory(mainResourcesRoot);
            }

            if (module.HasTestSources)
            {
                Directory.CreateDirectory(testJavaRoot);
                Directory.CreateDirectory(testResourcesRoot);
            }

            if (opts.Verbose)
            {
                Console.WriteLine($"Converting module: {module.Name}");
            }

            if (ReferenceEquals(module, compatModule))
            {
                foreach (var result in ProjectConversionPipeline.GenerateCompatibilitySupport(sharedCompatibilityPackage, opts.IncludeTests))
                {
                    if (TryWriteConvertedFile(result, opts.Source, mainJavaRoot, out var outputPath))
                    {
                        successCount++;
                        if (opts.Verbose)
                        {
                            Console.WriteLine($"Converted: {result.FileName} -> {outputPath}");
                        }
                    }
                    else
                    {
                        failureCount++;
                        Console.Error.WriteLine($"Failed: {result.FileName}");
                    }
                }

                if (opts.GeneratePom)
                {
                    var modulePom = GenerateChildModulePom(
                        module,
                        opts.MavenGroupId,
                        opts.MavenVersion,
                        opts.JavaVersion,
                        new DirectoryInfo(opts.Destination).Name);
                    var modulePomPath = Path.Combine(moduleRoot, "pom.xml");
                    await File.WriteAllTextAsync(modulePomPath, modulePom, new System.Text.UTF8Encoding(false));
                }

                continue;
            }

            foreach (var assignment in module.Assignments)
            {
                var targetJavaRoot = assignment.AsTestSources ? testJavaRoot : mainJavaRoot;
                var targetResourcesRoot = assignment.AsTestSources ? testResourcesRoot : mainResourcesRoot;

                if (opts.Verbose)
                {
                    var slot = assignment.AsTestSources ? "test" : "main";
                    Console.WriteLine($"  Project [{assignment.Project.Kind}] -> {slot}: {assignment.Project.Name}");
                }

                var semanticContextDirs = GetReferencedProjectDirectories(assignment.Project, graph);
                var results = await pipeline.ConvertProjectWithPartialMergeAsync(assignment.Project.ProjectDirectory, options, semanticContextDirs);
                foreach (var result in results)
                {
                    if (result.FileName == null) continue;

                    if (TryWriteConvertedFile(result, assignment.Project.ProjectDirectory, targetJavaRoot, out var outputPath))
                    {
                        successCount++;
                        if (opts.Verbose)
                        {
                            Console.WriteLine($"Converted: {result.FileName} -> {outputPath}");
                        }
                    }
                    else
                    {
                        failureCount++;
                        Console.Error.WriteLine($"Failed: {result.FileName}");
                        foreach (var diag in result.Diagnostics)
                        {
                            Console.Error.WriteLine($"  [{diag.Severity}] {FormatDiagnostic(diag)}");
                        }
                    }
                }

                foreach (var resource in assignment.Project.ResourceItems)
                {
                    var destination = Path.Combine(targetResourcesRoot, resource.RelativePath);
                    var destinationDir = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(destinationDir))
                    {
                        Directory.CreateDirectory(destinationDir);
                    }

                    File.Copy(resource.SourcePath, destination, overwrite: true);
                    copiedResourceCount++;

                    if (opts.Verbose)
                    {
                        Console.WriteLine($"Resource: {resource.SourcePath} -> {destination}");
                    }
                }
            }

            if (opts.GeneratePom)
            {
                var modulePom = GenerateChildModulePom(
                    module,
                    opts.MavenGroupId,
                    opts.MavenVersion,
                    opts.JavaVersion,
                    new DirectoryInfo(opts.Destination).Name);
                var modulePomPath = Path.Combine(moduleRoot, "pom.xml");
                await File.WriteAllTextAsync(modulePomPath, modulePom, new System.Text.UTF8Encoding(false));
            }
        }

        if (opts.GeneratePom)
        {
            var parentArtifactId = new DirectoryInfo(opts.Destination).Name;
            var parentPom = GenerateParentPom(
                parentArtifactId,
                opts.MavenGroupId,
                opts.MavenVersion,
                opts.JavaVersion,
                modulesInBuildOrder.Select(m => m.Name).ToList());
            var parentPomPath = Path.Combine(opts.Destination, "pom.xml");
            await File.WriteAllTextAsync(parentPomPath, parentPom, new System.Text.UTF8Encoding(false));
            Console.WriteLine($"Generated parent Maven pom.xml: {parentPomPath}");
        }

        Console.WriteLine();
        Console.WriteLine($"Conversion complete: {successCount} succeeded, {failureCount} failed, {copiedResourceCount} resources copied, modules={modulesInBuildOrder.Count}");

        return failureCount > 0 ? 1 : 0;
    }

    private static string BuildSharedCompatibilityPackage(string mavenGroupId)
    {
        var sanitized = Regex.Replace(mavenGroupId.ToLowerInvariant(), "[^a-z0-9.]", ".");
        while (sanitized.Contains("..", StringComparison.Ordinal))
        {
            sanitized = sanitized.Replace("..", ".", StringComparison.Ordinal);
        }

        sanitized = sanitized.Trim('.');
        return string.IsNullOrWhiteSpace(sanitized) ? "generated.compat" : sanitized + ".compat";
    }

    private static string MakeUniqueModuleName(string preferredName, IEnumerable<string> existingNames)
    {
        var used = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        if (used.Add(preferredName))
        {
            return preferredName;
        }

        var index = 2;
        while (true)
        {
            var candidate = preferredName + "-" + index;
            if (used.Add(candidate))
            {
                return candidate;
            }

            index++;
        }
    }

    private static bool TryWriteConvertedFile(ConversionResult result, string sourceRoot, string outputRoot, out string outputPath)
    {
        outputPath = string.Empty;

        if (string.IsNullOrEmpty(result.GeneratedCode) || string.IsNullOrEmpty(result.FileName))
        {
            return false;
        }

        bool isGeneratedFile = !string.IsNullOrEmpty(result.Package) &&
            !Path.IsPathFullyQualified(result.FileName) &&
            !result.FileName.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase);

        if (isGeneratedFile && !string.IsNullOrEmpty(result.Package))
        {
            var packageDir = result.Package.Replace('.', Path.DirectorySeparatorChar);
            outputPath = Path.Combine(outputRoot, packageDir, result.FileName);
            if (!result.FileName.EndsWith(".java", StringComparison.OrdinalIgnoreCase))
            {
                outputPath = Path.ChangeExtension(outputPath, ".java");
            }
        }
        else
        {
            var relativePath = Path.IsPathFullyQualified(result.FileName)
                ? Path.GetRelativePath(sourceRoot, result.FileName)
                : result.FileName;
            outputPath = Path.Combine(outputRoot, Path.ChangeExtension(relativePath, ".java"));
        }

        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        File.WriteAllText(outputPath, result.GeneratedCode, new System.Text.UTF8Encoding(false));
        return true;
    }

    private static void DeleteIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static IReadOnlyList<string> GetReferencedProjectDirectories(DiscoveredProject project, ProjectGraph graph)
    {
        var byProjectPath = graph.ProjectsInTopologicalOrder
            .ToDictionary(p => p.ProjectFilePath, p => p, StringComparer.OrdinalIgnoreCase);

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(project.ProjectReferences);

        while (queue.Count > 0)
        {
            var referencePath = queue.Dequeue();
            if (!visited.Add(referencePath))
            {
                continue;
            }

            if (!byProjectPath.TryGetValue(referencePath, out var referencedProject))
            {
                continue;
            }

            result.Add(referencedProject.ProjectDirectory);
            foreach (var next in referencedProject.ProjectReferences)
            {
                queue.Enqueue(next);
            }
        }

        return result.ToList();
    }

    private static async Task<int> AnalyzeProject(AnalyzeOptions opts)
    {
        try
        {
            if (!Directory.Exists(opts.Source))
            {
                Console.Error.WriteLine($"Error: Source directory not found: {opts.Source}");
                return 1;
            }

            TypeMappingRegistry registry;
            try
            {
                registry = new TypeMappingRegistry(opts.MappingConfig);
            }
            catch (TypeMappingConfigurationException ex)
            {
                Console.Error.WriteLine($"Configuration Error: {ex.Message}");
                Console.Error.WriteLine($"Expected location: {ex.ConfigPath}");
                return 1;
            }

            // 分析项目
            var csFiles = Directory.GetFiles(opts.Source, "*.cs", SearchOption.AllDirectories);

            var typeUsage = new Dictionary<string, HashSet<string>>();
            var missingMappings = new HashSet<string>();

            Console.WriteLine("Analyzing project...");
            Console.WriteLine();

            foreach (var file in csFiles)
            {
                try
                {
                    var sourceCode = await File.ReadAllTextAsync(file);
                    var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(sourceCode);
                    var root = tree.GetRoot();

                    // 收集类型使用
                    var typeNames = root.DescendantNodes()
                        .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeSyntax>()
                        .Select(t => t.ToString())
                        .Distinct();

                    foreach (var typeName in typeNames)
                    {
                        var mappedType = registry.MapType(typeName);
                        if (mappedType == typeName)
                        {
                            // 可能缺少映射
                            if (typeName.Contains(".") && !typeName.StartsWith("System."))
                            {
                                missingMappings.Add(typeName);
                            }
                        }

                        if (!typeUsage.ContainsKey(typeName))
                        {
                            typeUsage[typeName] = new HashSet<string>();
                        }
                        typeUsage[typeName].Add(file);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Warning: Could not analyze {file}: {ex.Message}");
                }
            }

            // 生成报告
            Console.WriteLine("Type Mapping Report");
            Console.WriteLine("===================");
            Console.WriteLine();

            Console.WriteLine("Most Common Types:");
            foreach (var (type, files) in typeUsage.OrderByDescending(kvp => kvp.Value.Count).Take(20))
            {
                var mapped = registry.MapType(type);
                var hasMapping = mapped != type;
                Console.WriteLine($"  {type} -> {mapped} {(hasMapping ? "OK" : "?")} ({files.Count} files)");
            }

            if (missingMappings.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Potential Missing Mappings:");
                foreach (var type in missingMappings.OrderBy(t => t))
                {
                    Console.WriteLine($"  {type}");
                }
            }

            // 保存报告
            if (opts.Report != null)
            {
                var report = new
                {
                    GeneratedAt = DateTime.UtcNow,
                    TypeUsage = typeUsage.ToDictionary(
                        kvp => kvp.Key,
                        kvp => new { Count = kvp.Value.Count, Files = kvp.Value.ToList() }
                    ),
                    MissingMappings = missingMappings.ToList()
                };

                var json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                });

                await File.WriteAllTextAsync(opts.Report, json, new System.Text.UTF8Encoding(false));
                Console.WriteLine();
                Console.WriteLine($"Report saved to: {opts.Report}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static string GenerateMavenPom(string artifactId, string groupId, string version, string javaVersion, bool includeTests)
    {
        int javaVer = int.Parse(javaVersion.Replace("Java", ""));
        var testDependencies = includeTests
            ? @"
        <dependency>
            <groupId>org.junit.jupiter</groupId>
            <artifactId>junit-jupiter</artifactId>
            <version>5.11.4</version>
            <scope>test</scope>
        </dependency>
        <dependency>
            <groupId>org.junit.jupiter</groupId>
            <artifactId>junit-jupiter-params</artifactId>
            <version>5.11.4</version>
            <scope>test</scope>
        </dependency>"
            : string.Empty;

        var surefirePlugin = includeTests
            ? @"
            <plugin>
                <groupId>org.apache.maven.plugins</groupId>
                <artifactId>maven-surefire-plugin</artifactId>
                <version>3.3.1</version>
            </plugin>"
            : string.Empty;

        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<project xmlns=""http://maven.apache.org/POM/4.0.0""
    xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance""
    xsi:schemaLocation=""http://maven.apache.org/POM/4.0.0 http://maven.apache.org/xsd/maven-4.0.0.xsd"">

    <modelVersion>4.0.0</modelVersion>
    <groupId>{groupId}</groupId>
    <artifactId>{artifactId}</artifactId>
    <version>{version}</version>
    <packaging>jar</packaging>

    <properties>
        <java.version>{javaVer}</java.version>
        <project.build.sourceEncoding>UTF-8</project.build.sourceEncoding>
        <maven.compiler.source>${{java.version}}</maven.compiler.source>
        <maven.compiler.target>${{java.version}}</maven.compiler.target>
    </properties>

    <build>
        <plugins>
            <plugin>
                <groupId>org.apache.maven.plugins</groupId>
                <artifactId>maven-compiler-plugin</artifactId>
                <configuration>
                    <source>${{java.version}}</source>
                    <target>${{java.version}}</target>
                    <encoding>UTF-8</encoding>
                    <maxerrs>1000000</maxerrs>
                    <maxwarns>0</maxwarns>
                    <compilerArgs>
                        <arg>-Xmaxerrs</arg>
                        <arg>1000000</arg>
                    </compilerArgs>
                </configuration>
            </plugin>
            <plugin>
                <groupId>com.diffplug.spotless</groupId>
                <artifactId>spotless-maven-plugin</artifactId>
                <version>3.4.0</version>
                <configuration>
                    <java>
                        <googleJavaFormat>
                            <version>1.35.0</version>
                            <style>GOOGLE</style>
                            <reflowLongStrings>false</reflowLongStrings>
                            <formatJavadoc>false</formatJavadoc>
                        </googleJavaFormat>
                    </java>
                </configuration>
                <executions>
                    <execution>
                        <goals>
                            <goal>apply</goal>
                        </goals>
                        <phase>process-sources</phase>
                    </execution>
                </executions>
            </plugin>
{surefirePlugin}
        </plugins>
    </build>

    <dependencies>
        <dependency>
            <groupId>io.vavr</groupId>
            <artifactId>vavr</artifactId>
            <version>0.10.4</version>
        </dependency>
        <dependency>
            <groupId>com.fasterxml.jackson.core</groupId>
            <artifactId>jackson-databind</artifactId>
            <version>2.17.2</version>
        </dependency>
{testDependencies}
    </dependencies>

</project>
";
    }

    private static string GenerateParentPom(
        string artifactId,
        string groupId,
        string version,
        string javaVersion,
        IReadOnlyList<string> modules)
    {
        int javaVer = int.Parse(javaVersion.Replace("Java", ""));
        var moduleSection = string.Join(Environment.NewLine, modules.Select(m => $"        <module>{m}</module>"));

        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<project xmlns=""http://maven.apache.org/POM/4.0.0""
    xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance""
    xsi:schemaLocation=""http://maven.apache.org/POM/4.0.0 http://maven.apache.org/xsd/maven-4.0.0.xsd"">

    <modelVersion>4.0.0</modelVersion>
    <groupId>{groupId}</groupId>
    <artifactId>{artifactId}</artifactId>
    <version>{version}</version>
    <packaging>pom</packaging>

    <properties>
        <java.version>{javaVer}</java.version>
        <project.build.sourceEncoding>UTF-8</project.build.sourceEncoding>
        <maven.compiler.source>${{java.version}}</maven.compiler.source>
        <maven.compiler.target>${{java.version}}</maven.compiler.target>
    </properties>

    <modules>
{moduleSection}
    </modules>

</project>
";
    }

    private static string GenerateChildModulePom(
        PlannedModule module,
        string groupId,
        string version,
        string javaVersion,
        string parentArtifactId)
    {
        int javaVer = int.Parse(javaVersion.Replace("Java", ""));

        var depLines = new List<string>
        {
            @"        <dependency>
            <groupId>io.vavr</groupId>
            <artifactId>vavr</artifactId>
            <version>0.10.4</version>
        </dependency>",
            @"        <dependency>
            <groupId>com.fasterxml.jackson.core</groupId>
            <artifactId>jackson-databind</artifactId>
            <version>2.17.2</version>
        </dependency>"
        };

        foreach (var dep in module.CompileDependencies.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            depLines.Add($@"        <dependency>
            <groupId>{groupId}</groupId>
            <artifactId>{dep}</artifactId>
            <version>${{project.version}}</version>
        </dependency>");
        }

        foreach (var dep in module.TestDependencies.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            depLines.Add($@"        <dependency>
            <groupId>{groupId}</groupId>
            <artifactId>{dep}</artifactId>
            <version>${{project.version}}</version>
            <scope>test</scope>
        </dependency>");
        }

        if (module.HasTestSources)
        {
            depLines.Add(@"        <dependency>
            <groupId>org.junit.jupiter</groupId>
            <artifactId>junit-jupiter</artifactId>
            <version>5.11.4</version>
            <scope>test</scope>
        </dependency>");
            depLines.Add(@"        <dependency>
            <groupId>org.junit.jupiter</groupId>
            <artifactId>junit-jupiter-params</artifactId>
            <version>5.11.4</version>
            <scope>test</scope>
        </dependency>");
        }

        var surefirePlugin = module.HasTestSources
            ? @"
            <plugin>
                <groupId>org.apache.maven.plugins</groupId>
                <artifactId>maven-surefire-plugin</artifactId>
                <version>3.3.1</version>
            </plugin>"
            : string.Empty;

        var deps = string.Join(Environment.NewLine, depLines);

        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<project xmlns=""http://maven.apache.org/POM/4.0.0""
    xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance""
    xsi:schemaLocation=""http://maven.apache.org/POM/4.0.0 http://maven.apache.org/xsd/maven-4.0.0.xsd"">

    <modelVersion>4.0.0</modelVersion>

    <parent>
        <groupId>{groupId}</groupId>
        <artifactId>{parentArtifactId}</artifactId>
        <version>{version}</version>
    </parent>

    <artifactId>{module.Name}</artifactId>
    <packaging>jar</packaging>

    <properties>
        <java.version>{javaVer}</java.version>
        <project.build.sourceEncoding>UTF-8</project.build.sourceEncoding>
        <maven.compiler.source>${{java.version}}</maven.compiler.source>
        <maven.compiler.target>${{java.version}}</maven.compiler.target>
    </properties>

    <build>
        <plugins>
            <plugin>
                <groupId>org.apache.maven.plugins</groupId>
                <artifactId>maven-compiler-plugin</artifactId>
                <configuration>
                    <source>${{java.version}}</source>
                    <target>${{java.version}}</target>
                    <encoding>UTF-8</encoding>
                </configuration>
            </plugin>
{surefirePlugin}
        </plugins>
    </build>

    <dependencies>
{deps}
    </dependencies>

</project>
";
    }

    private static string FormatDiagnostic(DiagnosticMessage diag)
    {
        if (diag.Location == null || !diag.Location.IsInSource)
        {
            return diag.Message;
        }

        var lineSpan = diag.Location.GetLineSpan();
        var line = lineSpan.StartLinePosition.Line + 1;
        var column = lineSpan.StartLinePosition.Character + 1;
        return $"{diag.Message} ({lineSpan.Path}:{line}:{column})";
    }
}

// 命令行选项
[Verb("convert", HelpText = "Convert a C# file to Java")]
class ConvertOptions
{
    [Option('i', "input", Required = true, HelpText = "Input C# file path")]
    public string Input { get; set; } = string.Empty;

    [Option('o', "output", Required = false, HelpText = "Output Java file path (default: stdout)")]
    public string? Output { get; set; }

    [Option('m', "mapping", Required = false, HelpText = "Path to type mapping configuration file")]
    public string? MappingConfig { get; set; }

    [Option('j', "java-version", Default = "Java17", HelpText = "Target Java version (Java8, Java11, Java17, Java21)")]
    public string JavaVersion { get; set; } = "Java17";

    [Option("no-records", Default = false, HelpText = "Don't use Java records for C# records")]
    public bool NoRecords { get; set; }

    [Option("use-optional", Default = false, HelpText = "Use Optional for nullable types")]
    public bool UseOptionalForNullable { get; set; }

    [Option("no-javadoc", Default = false, HelpText = "Don't generate JavaDoc comments")]
    public bool NoJavaDoc { get; set; }

    [Option('v', "verbose", Default = false, HelpText = "Show diagnostic messages")]
    public bool Verbose { get; set; }

    [Option("no-linq-rewrite", Default = false, HelpText = "Don't pre-process LINQ to procedural code")]
    public bool NoLinqRewrite { get; set; }

    [Option("prefer-stream-api", Default = false, SetName = "linq-strategy", HelpText = "Prefer Java Stream API for LINQ conversion (default for Java 9+)")]
    public bool PreferStreamApiFlag { get; set; }

    [Option("prefer-procedural", Default = false, SetName = "linq-strategy", HelpText = "Prefer procedural loops for LINQ conversion (default for Java 8)")]
    public bool PreferProceduralFlag { get; set; }

    // 便捷属性
    public bool UseRecords => !NoRecords;
    public bool GenerateJavaDoc => !NoJavaDoc;
    public bool EnableLinqRewrite => !NoLinqRewrite;

    /// <summary>Resolve PreferStreamApi: explicit flags override, otherwise null (version-based default).</summary>
    public bool? PreferStreamApi => PreferStreamApiFlag ? true : PreferProceduralFlag ? false : null;
}

[Verb("convert-project", HelpText = "Convert a C# project to Java")]
class ConvertProjectOptions
{
    [Option('s', "source", Required = true, HelpText = "Source directory path or .csproj path")]
    public string Source { get; set; } = string.Empty;

    [Option('d', "destination", Required = true, HelpText = "Destination directory path")]
    public string Destination { get; set; } = string.Empty;

    [Option('m', "mapping", Required = false, HelpText = "Path to type mapping configuration file")]
    public string? MappingConfig { get; set; }

    [Option('j', "java-version", Default = "Java25", HelpText = "Target Java version")]
    public string JavaVersion { get; set; } = "Java25";

    [Option('f', "force", Default = true, HelpText = "Overwrite existing files")]
    public bool Force { get; set; }

    [Option("no-records", Default = false, HelpText = "Don't use Java records")]
    public bool NoRecords { get; set; }

    [Option("use-optional", Default = false, HelpText = "Use Optional for nullable types")]
    public bool UseOptionalForNullable { get; set; }

    [Option("no-javadoc", Default = false, HelpText = "Don't generate JavaDoc")]
    public bool NoJavaDoc { get; set; }

    [Option('v', "verbose", Default = false, HelpText = "Show detailed progress")]
    public bool Verbose { get; set; }

    [Option("no-linq-rewrite", Default = false, HelpText = "Don't pre-process LINQ to procedural code")]
    public bool NoLinqRewrite { get; set; }

    [Option("prefer-stream-api", Default = false, SetName = "linq-strategy", HelpText = "Prefer Java Stream API for LINQ conversion (default for Java 9+)")]
    public bool PreferStreamApiFlag { get; set; }

    [Option("prefer-procedural", Default = false, SetName = "linq-strategy", HelpText = "Prefer procedural loops for LINQ conversion (default for Java 8)")]
    public bool PreferProceduralFlag { get; set; }

    [Option("generate-pom", Default = true, HelpText = "Generate Maven pom.xml file with standard project structure")]
    public bool GeneratePom { get; set; } = true;

    [Option("maven-group-id", Default = "io.github.ningpp", HelpText = "Maven groupId")]
    public string MavenGroupId { get; set; } = "io.github.ningpp";

    [Option("maven-version", Default = "0.0.1-SNAPSHOT", HelpText = "Maven version")]
    public string MavenVersion { get; set; } = "0.0.1-SNAPSHOT";

    [Option("include-tests", Default = true, HelpText = "Include discovered test projects and write them to src/test/java")]
    public bool IncludeTests { get; set; } = true;

    [Option("mode", Default = "multi-module", HelpText = "Output mode: single-module or multi-module")]
    public string Mode { get; set; } = "multi-module";

    public bool UseRecords => !NoRecords;
    public bool GenerateJavaDoc => !NoJavaDoc;
    public bool EnableLinqRewrite => !NoLinqRewrite;

    /// <summary>Resolve PreferStreamApi: explicit flags override, otherwise null (version-based default).</summary>
    public bool? PreferStreamApi => PreferStreamApiFlag ? true : PreferProceduralFlag ? false : null;
}

[Verb("analyze", HelpText = "Analyze a C# project and generate type mapping report")]
class AnalyzeOptions
{
    [Option('s', "source", Required = true, HelpText = "Source directory path")]
    public string Source { get; set; } = string.Empty;

    [Option('m', "mapping", Required = false, HelpText = "Path to type mapping configuration file")]
    public string? MappingConfig { get; set; }

    [Option('r', "report", Required = true, HelpText = "Output report file path")]
    public string Report { get; set; } = string.Empty;
}
