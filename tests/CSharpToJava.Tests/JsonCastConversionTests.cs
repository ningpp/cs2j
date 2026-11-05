using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// C# <c>JsonConvert.DeserializeObject&lt;T&gt;(json)</c> erases its generic
/// argument in Java, so the no-type compat entry answers a generic structure
/// (CSharpDictionary / CSharpList&lt;Object&gt;) and the caller's cast to the DTO
/// throws ClassCastException. The converter must emit the typed compat entry
/// (deserializeList for List-like targets, deserializeObject(json, Class) for
/// concrete classes and arrays).
/// </summary>
public class JsonCastConversionTests
{
    private const string JsonConvertStub = """
        using System;
        using System.Collections.Generic;

        namespace Newtonsoft.Json
        {
            public class JsonSerializerSettings { }

            public static class JsonConvert
            {
                public static T DeserializeObject<T>(string json) { return default(T); }
                public static T DeserializeObject<T>(string json, JsonSerializerSettings settings) { return default(T); }
            }
        }
        """;

    [Fact]
    public void ListReturn_UsesDeserializeList()
    {
        var result = Convert(JsonConvertStub + """

            class Foo
            {
                public int Id;
            }

            class Sample
            {
                public static List<Foo> Load(string json)
                {
                    return Newtonsoft.Json.JsonConvert.DeserializeObject<List<Foo>>(json);
                }
            }
            """);

        AssertConverted(result, "JsonConvert.deserializeList(json, Foo.class)");
    }

    [Fact]
    public void ListSettingsOverload_UsesDeserializeListAndDropsSettings()
    {
        var result = Convert(JsonConvertStub + """

            class Foo
            {
                public int Id;
            }

            class Sample
            {
                public static List<Foo> Load(string json, Newtonsoft.Json.JsonSerializerSettings settings)
                {
                    return Newtonsoft.Json.JsonConvert.DeserializeObject<List<Foo>>(json, settings);
                }
            }
            """);

        AssertConverted(result, "JsonConvert.deserializeList(json, Foo.class)");
    }

    [Fact]
    public void ConcreteReturn_UsesClassToken()
    {
        var result = Convert(JsonConvertStub + """

            class Foo
            {
                public int Id;
            }

            class Sample
            {
                public static Foo Load(string json)
                {
                    return Newtonsoft.Json.JsonConvert.DeserializeObject<Foo>(json);
                }
            }
            """);

        AssertConverted(result, "JsonConvert.deserializeObject(json, Foo.class)");
    }

    [Fact]
    public void ArrayTarget_UsesArrayClassLiteral()
    {
        var result = Convert(JsonConvertStub + """

            class Sample
            {
                public static int[] Load(string json)
                {
                    return Newtonsoft.Json.JsonConvert.DeserializeObject<int[]>(json);
                }
            }
            """);

        AssertConverted(result, "JsonConvert.deserializeObject(json, int[].class)");
    }

    [Fact]
    public void LocalDeclaration_UsesDeserializeList()
    {
        var result = Convert(JsonConvertStub + """

            class Foo
            {
                public int Id;
            }

            class Sample
            {
                public static int Count(string json)
                {
                    var items = Newtonsoft.Json.JsonConvert.DeserializeObject<List<Foo>>(json);
                    return items.Count;
                }
            }
            """);

        AssertConverted(result, "JsonConvert.deserializeList(json, Foo.class)");
    }

    [Fact]
    public void TypeParameterTarget_KeepsErasureFallback()
    {
        var result = Convert(JsonConvertStub + """

            class Sample
            {
                public static List<T> Load<T>(string json)
                {
                    return Newtonsoft.Json.JsonConvert.DeserializeObject<List<T>>(json);
                }
            }
            """);

        AssertConverted(result, "JsonConvert.deserializeObject(json)");
    }

    [Fact]
    public void GenericHelperCallSite_RetypesClosedType()
    {
        // RoutingTestSupport.LoadJson<T>: the helper's erased (T) cast cannot
        // name the closed type, so the caller re-types the generic structure.
        var result = Convert(JsonConvertStub + """

            class Support
            {
                public static T LoadJson<T>(string json)
                {
                    return Newtonsoft.Json.JsonConvert.DeserializeObject<T>(json);
                }
            }

            class Case
            {
                public int Id;
            }

            class Sample
            {
                public static void Run(string json)
                {
                    var cases = Support.LoadJson<List<Case>>(json);
                    foreach (var item in cases) { }
                }
            }
            """);

        AssertConverted(result, "JsonConvert.convertToType(Support.loadJson(json), CSharpList.class, Case.class)");
        Assert.Contains("CSharpList<Case> cases", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static void AssertConverted(ConversionResult result, string expected)
    {
        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains(expected, result.GeneratedCode, StringComparison.Ordinal);
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
