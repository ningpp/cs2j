using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// 验证 ref-struct-lowering 的产物（普通 struct + 标记接口 + Helper 静态调用）
/// 只靠 TypeMappings.json 配置就能被 Java 转换器通用翻译，无需任何 ref struct 特判。
/// </summary>
public class ByRefLikeMarkerConversionTests
{
    private const string SupportTypes = """
        namespace Cs2jPrelude
        {
            public interface Cs2jByRefLikeMarker { }
            public static class Cs2jByRefLikeHelper
            {
                public static bool IsByRefLike(global::System.Type type) => false;
            }
        }
        """;

    [Fact]
    public void MarkerInterface_IsMappedToCompatInterface()
    {
        var result = Convert(SupportTypes + """

            struct MyRefStruct : global::Cs2jPrelude.Cs2jByRefLikeMarker { }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("implements Cs2jByRefLikeMarker", result.GeneratedCode);
        Assert.DoesNotContain("__suppress__", result.GeneratedCode);
        // The fixture declares the marker in the same conversion unit, so the
        // converter emits it in-package and skips the compat import. In a real
        // project Cs2jPrelude is an unreferenced-by-conversion support library,
        // so TypeMappings' import is used instead.
    }

    [Fact]
    public void HelperCall_IsMappedToTypeHelperGetIsByRefLike()
    {
        var result = Convert(SupportTypes + """

            class C
            {
                static bool Check(global::System.Type t)
                    => global::Cs2jPrelude.Cs2jByRefLikeHelper.IsByRefLike(t);
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("TypeHelper.getIsByRefLike(t)", result.GeneratedCode);
        Assert.Contains("io.github.ningpp.compat.TypeHelper", result.GeneratedCode);
        Assert.DoesNotContain("Cs2jByRefLikeHelper.IsByRefLike(t)", result.GeneratedCode);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions
            {
                TypeMappingConfigPath = System.IO.Path.Combine(
                    AppContext.BaseDirectory, "config", "TypeMappings.json"),
            },
        });
    }
}
