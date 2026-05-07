namespace CSharpToJava.Core.Pipeline.Compatibility;

/// <summary>
/// 兼容层 Pack 接口 — 每个 Pack 描述一组相关的 Java 兼容类需求。
/// </summary>
public interface ICompatibilityPack
{
    /// <summary>
    /// Pack 标识符（例如 "ref-holder", "dotnet-core", "xml"）
    /// </summary>
    string Id { get; }

    /// <summary>
    /// 人类可读的描述
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Pack 需要的 Maven 依赖坐标（例如 "com.fasterxml.jackson.core:jackson-databind:2.17.0"）
    /// </summary>
    IReadOnlyList<string> MavenDependencies { get; }

    /// <summary>
    /// 根据已转换代码判断此 Pack 是否需要。
    /// </summary>
    bool IsApplicable(CompatibilityPackContext context);
}

/// <summary>
/// 兼容层 Pack 评估上下文 — 包含已转换代码的元数据用于按需生成判断。
/// </summary>
public class CompatibilityPackContext
{
    /// <summary>
    /// 已转换代码的聚合文本（用于类型引用检测）
    /// </summary>
    public IReadOnlyList<ConversionResult> ConvertedResults { get; }

    /// <summary>
    /// 目标基础包名
    /// </summary>
    public string BasePackage { get; }

    public CompatibilityPackContext(IReadOnlyList<ConversionResult> results, string basePackage)
    {
        ConvertedResults = results;
        BasePackage = basePackage;
    }

    /// <summary>
    /// 检查已转换代码是否引用了指定的类型名。
    /// </summary>
    public bool ReferencesType(string typeName)
    {
        foreach (var r in ConvertedResults)
        {
            if (!string.IsNullOrEmpty(r.GeneratedCode) &&
                r.GeneratedCode.Contains(typeName, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 检查已转换代码是否引用了指定的任一类型名。
    /// </summary>
    public bool ReferencesAnyType(params string[] typeNames)
    {
        foreach (var typeName in typeNames)
        {
            if (ReferencesType(typeName))
                return true;
        }
        return false;
    }
}
