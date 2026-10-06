using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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

/// <summary>
/// 未知 JSON 键摊入字典：JsonExtensionData 在转换器中同样无成员级
/// 物化分支，inventory 应判 BridgeGap（与 STJ 同名条目互为歧义短名）。
/// </summary>
public sealed class NewtonsoftEnvelope
{
    [JsonExtensionData]
    public IDictionary<string, JToken>? Extra { get; set; }
}
