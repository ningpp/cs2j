namespace CSharpToJava.Core.VarianceUseSite;

/// <summary>方案 §6.4-R4 的 VR 规则标签。</summary>
public static class VarianceUseSiteRules
{
    /// <summary>隐式 <c>IEnumerable&lt;Derived&gt;</c> → <c>IEnumerable&lt;Base&gt;</c>（producer）。</summary>
    public const string ProducerConversion = "VR-01";

    /// <summary>隐式 <c>Func&lt;Base,R&gt;</c> → <c>Func&lt;Derived,R&gt;</c>（consumer）。</summary>
    public const string ConsumerConversion = "VR-02";

    /// <summary>嵌套组合：中间层 invariant。</summary>
    public const string NestedInvariant = "VR-03";

    /// <summary>引用类型数组协变；Java 数组协变，保留原样。</summary>
    public const string ArrayCovariance = "VR-04";

    /// <summary>Both 流向 / 无法重推方向的保守分类。</summary>
    public const string BothFlow = "VR-05";

    /// <summary>值类型实参（装箱/擦除不等价）。</summary>
    public const string ValueTypeArgument = "VR-06";

    /// <summary>泛型委托方法组转换（需先显式 lambda）。</summary>
    public const string MethodGroupConversion = "VR-07";

    /// <summary>动态/未绑定类型。</summary>
    public const string DynamicOrUnbound = "VR-08";
}

/// <summary>站点处置动作。</summary>
public static class VarianceUseSiteActions
{
    public const string Rewritten = "Rewritten";
    public const string Preserved = "Preserved";
    public const string DeclaredUnsupported = "DeclaredUnsupported";
}

/// <summary>
/// One classified variance use site in the VR report: rule, source/target types,
/// audit direction, the safety justification, and whether it was rewritten.
/// </summary>
public sealed record VarianceUseSiteRecord(
    string Rule,
    string SourceType,
    string TargetType,
    string Direction,
    string Safety,
    string Action,
    bool Rewritten,
    string? Code,
    string? Reason,
    string? FilePath = null,
    int Line = 0,
    int Column = 0,
    string? NormCode = null);
