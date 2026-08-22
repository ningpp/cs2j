using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that System.Text.Json.JsonSerializer.Deserialize&lt;List&lt;T&gt;&gt;(json, options)
/// keeps the element type via a Jackson TypeReference instead of erasing it to a raw
/// CSharpList.class (which yields LinkedHashMap elements at runtime).
/// </summary>
public class SystemTextJsonDeserializeTypeReferenceTests
{
    [Fact]
    public void DeserializeGenericList_EmitsTypeReference()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Text.Json;
class C {
    List<SciPyResult> M(string json) {
        return JsonSerializer.Deserialize<List<SciPyResult>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
}
class SciPyResult { public string Name { get; set; } }");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("new com.fasterxml.jackson.core.type.TypeReference<CSharpList<SciPyResult>>() {}", code, StringComparison.Ordinal);
        Assert.DoesNotContain("deserialize(CSharpList.class, json)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void DeserializeConcreteClass_StillEmitsClassLiteral()
    {
        var result = Convert(@"
using System.Text.Json;
class C {
    SciPyResult M(string json) {
        return JsonSerializer.Deserialize<SciPyResult>(json);
    }
}
class SciPyResult { public string Name { get; set; } }");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("deserialize(SciPyResult.class, json)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("TypeReference", code, StringComparison.Ordinal);
    }

    [Fact]
    public void DeserializeGenericTypeParameter_EmitsTypeReferenceTokenAndCallSiteTypeReference()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Text.Json;
class C {
    T Load<T>(string json) {
        return JsonSerializer.Deserialize<T>(json);
    }
    List<SciPyResult> M(string json) {
        return Load<List<SciPyResult>>(json);
    }
}
class SciPyResult { public string Name { get; set; } }");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        // Definition: the injected runtime token is a TypeReference<T> parameter,
        // not a Class<T> parameter.
        Assert.Contains("com.fasterxml.jackson.core.type.TypeReference<T> _cs2j_T", code, StringComparison.Ordinal);
        Assert.Contains("deserialize(_cs2j_T, json)", code, StringComparison.Ordinal);
        // Call site: the constructed generic element type survives erasure.
        Assert.Contains("load(new com.fasterxml.jackson.core.type.TypeReference<CSharpList<SciPyResult>>() {}, json)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void DeserializeWithSnakeCaseOptions_PassesOptionsThrough()
    {
        var result = Convert(@"
using System.Text.Json;
class C {
    SciPyResult M(string json) {
        return JsonSerializer.Deserialize<SciPyResult>(json,
            new JsonSerializerOptions {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            });
    }
}
class SciPyResult { public string Name { get; set; } }");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains("JsonSerializerOptions", code, StringComparison.Ordinal);
        Assert.Contains("setPropertyNameCaseInsensitive(true)", code, StringComparison.Ordinal);
        Assert.Contains("setPropertyNamingPolicy(JsonNamingPolicy.getSnakeCaseLower())", code, StringComparison.Ordinal);
        Assert.Contains("deserialize(SciPyResult.class, json, _obj1)", code, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
