using Newtonsoft.Json;

namespace SampleAttributeNormalizeLib;

/// <summary>
/// Newtonsoft DTO：成员级注解在转换器中无消费分支，inventory 应判
/// BridgeGap（JsonProperty 的 propertyName / JsonIgnore 均未物化）。
/// </summary>
public sealed class NewtonsoftPerson
{
    [JsonProperty("person_id")]
    public int Id { get; set; }

    [JsonIgnore]
    public string? Secret { get; set; }
}
