namespace CSharpToJava.Core.Context;

/// <summary>
/// 转换选项
/// </summary>
public class ConversionOptions
{
    /// <summary>
    /// 目标 Java 版本
    /// </summary>
    public JavaVersion TargetJavaVersion { get; set; } = JavaVersion.Java28;

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
    /// 是否使用 Java Value Class (JEP 401) 转换 C# readonly struct
    /// 需要 Java 28+ 并启用 --enable-preview
    /// </summary>
    public bool UseValueClass { get; set; } = false;

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
    /// C26 M4（D8）后为兼容性死开关：LINQ 输出策略已规范化——项目路径由
    /// linq-channel 预处理通道决定（Enumerable 展开为过程化、Queryable/表达式
    /// 冻结），单文件路径恒走嵌入式过程化 LinqRewriter，其余 LINQ 形态由 Java
    /// 发射期映射落地。保留该可空属性仅为了不破坏既有调用方/CLI 传参；赋值不再
    /// 改变结果。
    /// </summary>
    public bool? PreferStreamApi { get; set; }

    /// <summary>
    /// 恒为 false：过程化 LINQ 改写（单文件嵌入式 pass、linq-channel）始终启用，
    /// 不存在「优先 Stream API」的旧分叉。参见 <see cref="PreferStreamApi"/>。
    /// </summary>
    public bool EffectivePreferStreamApi => false;

    /// <summary>
    /// Optional signal to import the published csharptojava-compat runtime package into generated files.
    /// The package itself is fixed by the runtime artifact, not by the generated project's Maven groupId.
    /// </summary>
    public string? SharedCompatibilityPackage { get; set; }

    /// <summary>
    /// When enabled, project-level tree-local passes may process syntax trees in parallel
    /// while merging results back in deterministic order.
    /// </summary>
    public bool EnableParallelProjectPasses { get; set; } = true;

    /// <summary>
    /// Shared store for default factory methods registered during type-parameter
    /// default(T) conversion. Shared across all project conversions so that factory
    /// methods registered during base-class project conversion are visible when
    /// converting subclass projects in a different module.
    /// </summary>
    internal DefaultFactoryMethodStore DefaultFactoryMethods { get; } = new();
}

/// <summary>
/// Target language
/// </summary>
public enum TargetLanguage
{
    Java = 0,
}

/// <summary>
/// Java 版本枚举
/// </summary>
public enum JavaVersion
{
    Java28 = 28,
}

