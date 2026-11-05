using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Assignment-position coverage for the erased generic JSON deserialization
/// (defined at local-declaration/return positions in
/// <c>JsonCastConversionTests</c>). A field or property assignment whose target
/// type is known must re-type the value as well; otherwise the assignment site
/// inserts an implicit runtime cast of the generic CSharpDictionary to the DTO
/// (AglNingppTests BugsRegressionTest marked <c>@BeforeAll</c> failed with
/// "CSharpDictionary cannot be cast to BugsReference").
/// </summary>
public class JsonAssignmentConversionTests
{
    private const string Stubs = """
        using System;
        using System.Collections.Generic;
        using Microsoft.VisualStudio.TestTools.UnitTesting;

        namespace Microsoft.VisualStudio.TestTools.UnitTesting
        {
            public class TestClassAttribute : System.Attribute { }
            public class TestMethodAttribute : System.Attribute { }
            public class ClassInitializeAttribute : System.Attribute { }
            public class TestContext { }
        }

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
    public void ClassInitialize_StaticFieldAssignmentFromGenericHelper_UsesConvertToType()
    {
        var result = Convert(Stubs + """

            public class Foo
            {
                public int Id;
            }

            public static class Support
            {
                public static T LoadJson<T>(string path)
                {
                    return Newtonsoft.Json.JsonConvert.DeserializeObject<T>(path);
                }
            }

            [TestClass]
            public class BugsRegressionTest
            {
                private static Foo reference;

                [ClassInitialize]
                public static void ClassInitialize(TestContext context)
                {
                    reference = Support.LoadJson<Foo>("data.json");
                }

                [TestMethod]
                public void UsesReference()
                {
                }
            }
            """);

        AssertConverted(result, "JsonConvert.convertToType(");
        Assert.Contains("@BeforeAll", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Support.loadJson(", result.GeneratedCode, StringComparison.Ordinal);
        // 泛型包装方法的裸结果不再被运行期强转：值先经 convertToType 重定型。
        Assert.DoesNotContain("(Foo) Support.loadJson(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Method_FieldAndLocalAssignmentFromGenericHelper_UsesConvertToType()
    {
        var result = Convert(Stubs + """

            public class Foo
            {
                public int Id;
            }

            public static class Support
            {
                public static T LoadJson<T>(string path)
                {
                    return Newtonsoft.Json.JsonConvert.DeserializeObject<T>(path);
                }
            }

            public class Sample
            {
                private Foo field;

                public void Run()
                {
                    this.field = Support.LoadJson<Foo>("data.json");
                    Foo local = null;
                    local = Support.LoadJson<Foo>("data.json");
                    local.ToString();
                }
            }
            """);

        AssertConverted(result, "JsonConvert.convertToType(");
        Assert.Contains("Support.loadJson(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("(Foo) Support.loadJson(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void FieldAssignment_DirectDeserialize_UsesTypedDeserializeObject()
    {
        var result = Convert(Stubs + """

            public class Foo
            {
                public int Id;
            }

            public class Sample
            {
                private static Foo reference;

                public static void Run()
                {
                    reference = Newtonsoft.Json.JsonConvert.DeserializeObject<Foo>("data.json");
                }
            }
            """);

        AssertConverted(result, "JsonConvert.deserializeObject(\"data.json\", Foo.class)");
        Assert.DoesNotContain("JsonConvert.deserializeObject(\"data.json\")", result.GeneratedCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PropertyAssignment_FromGenericHelper_UsesConvertToType()
    {
        var result = Convert(Stubs + """

            public class Foo
            {
                public int Id;
            }

            public static class Support
            {
                public static T LoadJson<T>(string path)
                {
                    return Newtonsoft.Json.JsonConvert.DeserializeObject<T>(path);
                }
            }

            public class Holder
            {
                public Foo Value { get; set; }
            }

            public class Sample
            {
                public static void Run(Holder holder)
                {
                    holder.Value = Support.LoadJson<Foo>("data.json");
                }
            }
            """);

        AssertConverted(result, "JsonConvert.convertToType(");
        Assert.DoesNotContain("(Foo) Support.loadJson(", result.GeneratedCode, StringComparison.Ordinal);
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
