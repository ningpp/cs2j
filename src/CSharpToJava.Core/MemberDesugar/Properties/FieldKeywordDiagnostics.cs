namespace CSharpToJava.Core.MemberDesugar.Properties;

/// <summary>C50 field 关键字规则消息文案（CS2J5122/5125-5128，设计 §6）。
/// 码位常量在 <see cref="MemberDesugarDiagnosticCode"/>。</summary>
internal static class FieldKeywordDiagnostics
{
    public const string SkipSuffix =
        "the property declaration and all of its use sites were left unchanged";

    /// <summary>CS2J5122：子集开关关闭时的声明级跳过（沿用 C11 v1 文案）。</summary>
    public static string DisabledSkip(string name)
        => $"an accessor of property '{name}' uses the C# 'field' contextual keyword; {SkipSuffix}.";

    /// <summary>CS2J5125：归一留痕（Info，verbose）。</summary>
    public static string Normalized(
        string propertyName, string fieldName, bool isStatic, bool isReadOnly,
        int autoAccessorsExpanded, int referencesRewritten, bool initializerMoved)
        => $"field-backed property '{propertyName}' normalized to explicit backing field " +
           $"'{fieldName}' (static={isStatic.ToString().ToLowerInvariant()}, " +
           $"readonly={isReadOnly.ToString().ToLowerInvariant()}, " +
           $"{autoAccessorsExpanded} auto accessor(s) expanded, {referencesRewritten} reference(s), " +
           $"initializer={(initializerMoved ? "moved" : "none")})";

    /// <summary>CS2J5126：语义安全网（field 文本在但绑不到归属本属性的隐式后备字段）。</summary>
    public static string SafetyNet(string name, string detail)
        => $"an accessor of property '{name}' contains an unescaped 'field' token but semantic " +
           $"binding did not yield the property's implicit backing field ({detail}); no textual " +
           $"rewrite is performed; {SkipSuffix}.";

    /// <summary>CS2J5127：不收编容器（接口实例 DIM 等）。</summary>
    public static string UnsupportedContainer(string name, string container)
        => $"the 'field' contextual keyword in property '{name}' is not supported in container " +
           $"'{container}' by C50 v1; {SkipSuffix}.";

    /// <summary>CS2J5128：[field:] 特性迁移失败。</summary>
    public static string AttributeMigrationFailed(string name, string detail)
        => $"a [field:] attribute on property '{name}' could not be migrated to the synthesized " +
           $"backing field ({detail}); {SkipSuffix}.";

    /// <summary>CS2J5122：FK-9 residual 闸门。</summary>
    public static string Residual(string? name)
        => $"a 'field' token remains in the rewritten output for property '{name ?? "<unknown>"}' " +
           "without a corresponding skip diagnostic (FK-9 residual gate).";
}
