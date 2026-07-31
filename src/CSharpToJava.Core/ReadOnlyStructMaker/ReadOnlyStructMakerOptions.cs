namespace CSharpToJava.Core.ReadOnlyStructMaker;

public sealed class ReadOnlyStructMakerOptions
{
    /// <summary>禁止转换特性的名称（不含 Attribute 后缀），命名空间不限</summary>
    public string OptOutAttributeName { get; init; } = "DoNotMakeReadOnly";

    /// <summary>是否输出 Info 级跳过诊断（默认 true）</summary>
    public bool ReportSkipped { get; init; } = true;

    /// <summary>不可转换的 struct 是否视为致命错误（对应 CLI --strict）</summary>
    public bool Strict { get; init; }

    /// <summary>是否启用方法迁移（L5 转换），默认 true</summary>
    public bool EnableMethodMigration { get; init; } = true;

    /// <summary>是否启用 DTO 转换（L3 转换），默认 true</summary>
    public bool EnableDtoConversion { get; init; } = true;

    /// <summary>是否启用公共字段转属性转换（L4 转换），默认 true</summary>
    public bool EnablePublicFieldConversion { get; init; } = true;

    /// <summary>方法迁移时是否更新调用点，默认 true</summary>
    public bool UpdateCallSites { get; init; } = true;

    /// <summary>转换级别白名单，null 则全部允许</summary>
    public IReadOnlySet<ConversionLevel>? AllowedLevels { get; init; }
}
