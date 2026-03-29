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
    public bool EffectivePreferStreamApi => PreferStreamApi ?? true;

    /// <summary>
    /// When enabled, extension methods on known types are promoted to instance methods on those types,
    /// and the receiver ('this') parameter is stripped from the Java method signature.
    /// </summary>
    public bool RewriteExtensionMethods { get; set; } = false;

    /// <summary>
    /// When false, project conversion skips emitting generated compatibility helper classes
    /// such as ObjectHolder, StringHelper, and XML/JSON shims into the current output.
    /// This is used by multi-module conversion when those helpers are centralized in a
    /// shared compatibility module.
    /// </summary>
    public bool EmitCompatibilityHelpers { get; set; } = true;

    /// <summary>
    /// Optional shared compatibility package that should be imported into all generated files.
    /// Used together with EmitCompatibilityHelpers=false when helper classes live in a
    /// dedicated shared module.
    /// </summary>
    public string? SharedCompatibilityPackage { get; set; }
}

/// <summary>
/// Java 版本枚举
/// </summary>
public enum JavaVersion
{
    Java25 = 25,
}
