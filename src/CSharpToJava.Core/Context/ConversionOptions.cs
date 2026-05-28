namespace CSharpToJava.Core.Context;

/// <summary>
/// 转换选项
/// </summary>
public class ConversionOptions
{
    /// <summary>
    /// 目标 Java 版本
    /// </summary>
    public JavaVersion TargetJavaVersion { get; set; } = JavaVersion.Java25;

    /// <summary>
    /// 类型映射配置文件路径
    /// </summary>
    public string? TypeMappingConfigPath { get; set; }

    /// <summary>
    /// Path to the root directory containing Java standard-library metadata JSON files
    /// (e.g. <c>config/java/</c>).  When set, enables metadata-driven method mapping
    /// auto-deduction and semantic validation of mapped Java types.
    /// </summary>
    public string? JavaMetadataPath { get; set; }

    /// <summary>
    /// 是否生成 JavaDoc 注释
    /// </summary>
    public bool GenerateJavaDoc { get; set; } = true;

    /// <summary>
    /// 是否使用 Java Record（对于 C# record）
    /// </summary>
    public bool UseRecords { get; set; } = true;

    /// <summary>
    /// 是否使用 Optional 替代 nullable
    /// </summary>
    public bool UseOptionalForNullable { get; set; } = false;

    /// <summary>
    /// 命名空间到包的映射规则
    /// </summary>
    public Dictionary<string, string> NamespaceMappings { get; set; } = new();

    /// <summary>
    /// 是否启用 LINQ 预处理（将 LINQ 转换为过程化代码）
    /// </summary>
    public bool EnableLinqRewrite { get; set; } = true;

    /// <summary>
    /// When true, LINQ chains are converted to Java Stream API calls (.stream().filter().map()...)
    /// instead of procedural loops via LinqRewriter. Default: true for Java 25.
    /// When set explicitly, overrides the version-based default.
    /// </summary>
    public bool? PreferStreamApi { get; set; }

    /// <summary>
    /// Resolved value: uses explicit setting if provided, otherwise defaults to Stream API for Java 25.
    /// </summary>
    /// <summary>
    /// Always use LINQ desugarer (procedural conversion). Stream API mode is disabled.
    /// </summary>
    public bool EffectivePreferStreamApi => false;

    /// <summary>
    /// When enabled, extension methods on known types are promoted to instance methods on those types,
    /// and the receiver ('this') parameter is stripped from the Java method signature.
    /// </summary>
    public bool RewriteExtensionMethods { get; set; } = false;

    /// <summary>
    /// Optional shared compatibility package that should be imported into all generated files.
    /// </summary>
    public string? SharedCompatibilityPackage { get; set; }

    /// <summary>
    /// When enabled, project-level tree-local passes may process syntax trees in parallel
    /// while merging results back in deterministic order.
    /// </summary>
    public bool EnableParallelProjectPasses { get; set; } = true;

    internal RuntimeClassParameterRegistry RuntimeClassParameters { get; } = new();

    /// <summary>
    /// Shared store for default factory methods registered during type-parameter
    /// default(T) conversion. Shared across all project conversions so that factory
    /// methods registered during base-class project conversion are visible when
    /// converting subclass projects in a different module.
    /// </summary>
    internal DefaultFactoryMethodStore DefaultFactoryMethods { get; } = new();
}

/// <summary>
/// Java 版本枚举
/// </summary>
public enum JavaVersion
{
    Java25 = 25,
}
