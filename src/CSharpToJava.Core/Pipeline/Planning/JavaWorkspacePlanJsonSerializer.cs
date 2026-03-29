using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSharpToJava.Core.Pipeline.Planning;

/// <summary>
/// 把 <see cref="JavaWorkspacePlan"/> 序列化为结构化 JSON manifest。
/// </summary>
public sealed class JavaWorkspacePlanJsonSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public string Serialize(JavaWorkspacePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return JsonSerializer.Serialize(plan, SerializerOptions);
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}