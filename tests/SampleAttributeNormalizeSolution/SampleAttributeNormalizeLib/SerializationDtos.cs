using System.Text.Json.Serialization;

namespace SampleAttributeNormalizeLib;

/// <summary>
/// STJ DTO：三个落在转换器消费面上的注解（属性级），外加一个字段级
/// JsonIgnore——转换器仅消费属性级 JsonIgnore，该使用点 Condition 未读，
/// inventory 应判 BridgeGap（M2 只盘点不改写，源字节保持不动）。
/// </summary>
public sealed class StjPerson
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Secret { get; set; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int Score { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    private string _fieldLevel = string.Empty;
}

/// <summary>白名单外自定义注解 → Unknown/CS2J1320（M2 保留原样）。</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class CustomMarkerAttribute : Attribute
{
}

public sealed class CustomAnnotated
{
    [CustomMarker]
    public string? Tag { get; set; }
}
