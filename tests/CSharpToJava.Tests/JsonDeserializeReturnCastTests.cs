using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// 泛型调用点的类型令牌重载（JsonConvert.DeserializeObject(json, Type)）会先给
/// 结果补一层原始 cast：<c>((CSharpList&lt;T&gt;) (CSharpList) ...)</c>。返回/局部
/// 声明槽位必须穿透这层 cast 仍走类型化重写，否则反序列化又退回无类型入口，元素
/// 变成 LinkedHashMap，调用方强转 DTO 时抛 ClassCastException。
/// </summary>
public class JsonDeserializeReturnCastTests
{
    private const string JsonConvertStub = """
        using System;
        using System.Collections.Generic;

        namespace Newtonsoft.Json
        {
            public static class JsonConvert
            {
                public static T DeserializeObject<T>(string json) { return default(T); }
                public static object DeserializeObject(string json, Type type) { return null; }
            }
        }
        """;

    [Fact]
    public void ListReturn_WithRawCastPrefix_UsesDeserializeList()
    {
        var result = Convert(JsonConvertStub + """

            class Dto
            {
                public int Id;
            }

            class Sample
            {
                public static List<Dto> Load(string json)
                {
                    return Newtonsoft.Json.JsonConvert.DeserializeObject<List<Dto>>(json);
                }
            }
            """);

        AssertConverted(result);
        Assert.Contains("JsonConvert.deserializeList(json, Dto.class)", result.GeneratedCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain("closedCallSiteGenericType", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("TypeHelper.parameterized", result.GeneratedCode, StringComparison.Ordinal);
        // 旧的擦除形态（无类型入口 + 原始 cast）不得残留。
        Assert.DoesNotContain("(CSharpList) JsonConvert.deserializeObject(", result.GeneratedCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ListLocalDeclaration_WithRawCastPrefix_UsesDeserializeList()
    {
        var result = Convert(JsonConvertStub + """

            class Dto
            {
                public int Id;
            }

            class Sample
            {
                public static int Count(string json)
                {
                    var items = Newtonsoft.Json.JsonConvert.DeserializeObject<List<Dto>>(json);
                    return items.Count;
                }
            }
            """);

        AssertConverted(result);
        Assert.Contains("JsonConvert.deserializeList(json, Dto.class)", result.GeneratedCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain("closedCallSiteGenericType", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("TypeHelper.parameterized", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static void AssertConverted(ConversionResult result)
    {
        Assert.True(result.Success,
            string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
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
