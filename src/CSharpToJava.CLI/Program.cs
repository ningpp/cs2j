using CommandLine;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.TypeMapping;

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
                EnableLinqRewrite = opts.EnableLinqRewrite
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
                    Console.WriteLine($"  [{prefix}] {diag.Message}");
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
            if (!Directory.Exists(opts.Source))
            {
                Console.Error.WriteLine($"Error: Source directory not found: {opts.Source}");
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
                EnableLinqRewrite = opts.EnableLinqRewrite
            };

            // 确定输出根目录（Maven 标准目录结构）
            var outputRoot = opts.GeneratePom
                ? Path.Combine(opts.Destination, "src", "main", "java")
                : opts.Destination;

            // 当 --force 时清空 Java 源码目录以避免残留旧文件（如 Holder 类）
            if (opts.Force && Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }

            // 创建输出目录
            if (!Directory.Exists(outputRoot))
            {
                Directory.CreateDirectory(outputRoot);
            }

            // 执行项目转换 (uses ProjectConversionPipeline for partial type merging + Holder generation)
            var pipeline = new ConversionPipeline();
            var results = await pipeline.ConvertProjectWithPartialMergeAsync(opts.Source, options);

            // 保存结果
            int successCount = 0;
            int failureCount = 0;

            foreach (var result in results)
            {
                if (result.FileName == null) continue;

                string outputPath;

                // Holder files and other generated files have FileName without a source path prefix
                // and have a Package set. Use the Package to determine the output directory.
                bool isGeneratedFile = !string.IsNullOrEmpty(result.Package) &&
                    !Path.IsPathFullyQualified(result.FileName) &&
                    !result.FileName.StartsWith(opts.Source);

                if (isGeneratedFile && !string.IsNullOrEmpty(result.Package))
                {
                    var packageDir = result.Package.Replace('.', Path.DirectorySeparatorChar);
                    outputPath = Path.Combine(outputRoot, packageDir, result.FileName);
                    if (!result.FileName.EndsWith(".java"))
                        outputPath = Path.ChangeExtension(outputPath, ".java");
                }
                else
                {
                    var relativePath = Path.IsPathFullyQualified(result.FileName)
                        ? Path.GetRelativePath(opts.Source, result.FileName)
                        : result.FileName;
                    outputPath = Path.Combine(outputRoot, Path.ChangeExtension(relativePath, ".java"));
                }

                // 创建输出目录
                var outputDir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                }

                if (!string.IsNullOrEmpty(result.GeneratedCode))
                {
                    // 使用 UTF-8 without BOM 编码写入Java文件
                    await File.WriteAllTextAsync(outputPath, result.GeneratedCode, new System.Text.UTF8Encoding(false));
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
                        Console.Error.WriteLine($"  [{diag.Severity}] {diag.Message}");
                    }
                }
            }

            // 生成 Maven pom.xml
            if (opts.GeneratePom)
            {
                var artifactId = new DirectoryInfo(opts.Destination).Name;
                var pomContent = GenerateMavenPom(artifactId, opts.MavenGroupId, opts.MavenVersion, opts.JavaVersion);
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

    private static string GenerateMavenPom(string artifactId, string groupId, string version, string javaVersion)
    {
        int javaVer = int.Parse(javaVersion.Replace("Java", ""));
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
                </configuration>
            </plugin>
        </plugins>
    </build>

    <dependencies>
        <dependency>
            <groupId>io.vavr</groupId>
            <artifactId>vavr</artifactId>
            <version>0.10.4</version>
        </dependency>
    </dependencies>

</project>
";
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

    // 便捷属性
    public bool UseRecords => !NoRecords;
    public bool GenerateJavaDoc => !NoJavaDoc;
    public bool EnableLinqRewrite => !NoLinqRewrite;
}

[Verb("convert-project", HelpText = "Convert a C# project to Java")]
class ConvertProjectOptions
{
    [Option('s', "source", Required = true, HelpText = "Source directory path")]
    public string Source { get; set; } = string.Empty;

    [Option('d', "destination", Required = true, HelpText = "Destination directory path")]
    public string Destination { get; set; } = string.Empty;

    [Option('m', "mapping", Required = false, HelpText = "Path to type mapping configuration file")]
    public string? MappingConfig { get; set; }

    [Option('j', "java-version", Default = "Java25", HelpText = "Target Java version")]
    public string JavaVersion { get; set; } = "Java25";

    [Option('f', "force", Default = false, HelpText = "Overwrite existing files")]
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

    [Option("generate-pom", Default = true, HelpText = "Generate Maven pom.xml file with standard project structure")]
    public bool GeneratePom { get; set; } = true;

    [Option("maven-group-id", Default = "io.github.ningpp", HelpText = "Maven groupId")]
    public string MavenGroupId { get; set; } = "io.github.ningpp";

    [Option("maven-version", Default = "0.0.1-SNAPSHOT", HelpText = "Maven version")]
    public string MavenVersion { get; set; } = "0.0.1-SNAPSHOT";

    public bool UseRecords => !NoRecords;
    public bool GenerateJavaDoc => !NoJavaDoc;
    public bool EnableLinqRewrite => !NoLinqRewrite;
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
