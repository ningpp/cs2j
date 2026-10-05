using SampleDynamicDesugarLib;

namespace SampleDynamicDesugarApp;

/// <summary>
/// C25 cross-file dynamic boundary surface: App consumes Lib's dynamic-returning
/// signature and hosts the JSON-style boundary stand-in.
/// </summary>
public static class CrossFileDynamic
{
    /// <summary>
    /// Calls the Lib dynamic-return method across the project boundary. Residual
    /// sites (all registered in Cs2jDynamicAudit.json): DS010 local
    /// (dynamic assignment), DS016 argument, DS015 operation, DS002 cast.
    /// </summary>
    public static int CrossFileCompute(int seed)
    {
        dynamic value = DynamicSignatures.EchoDynamic(seed);
        return (int)(value + 2);
    }

    /// <summary>
    /// JSON-style boundary stand-in (deliberately no Newtonsoft dependency): a
    /// <c>Deserialize*</c>-style method returning dynamic whose values are
    /// genuinely multi-shaped, so no single static type can be proven. The real
    /// Newtonsoft.Json shape (JsonConvert.DeserializeObject&lt;dynamic&gt;,
    /// JObject/JToken) is covered by the DS017 conservative name recognition in
    /// the Core unit tests.
    /// </summary>
    public static dynamic DeserializeObjectDynamic(string json)
    {
        object value = int.TryParse(json, out var number) ? number : (object)json;
        return value;
    }

    /// <summary>DS010 local (open object type) plus a DS013 dynamic invocation.</summary>
    public static int JsonBoundaryNumber()
    {
        dynamic parsed = DeserializeObjectDynamic("41");
        string text = parsed.ToString();
        return int.Parse(text) + 1;
    }
}
