using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class NullableGetUnderlyingTypeTests
{
    [Fact]
    public void NullableGetUnderlyingType_MapsToTypeHelper()
    {
        var result = Convert(@"
public class Sample
{
    public static void Test()
    {
        System.Type underlying = System.Nullable.GetUnderlyingType(typeof(int?));
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("TypeHelper.getUnderlyingType(Integer.class)", code, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.TypeHelper", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Object.getUnderlyingType", code, StringComparison.Ordinal);
    }

    [Fact]
    public void NullableGetUnderlyingType_OnReferenceType_StaysNullCheckable()
    {
        var result = Convert(@"
public class Sample
{
    public static bool IsNullableValueType(System.Type type)
    {
        return System.Nullable.GetUnderlyingType(type) != null;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("TypeHelper.getUnderlyingType(type) != null", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Object.getUnderlyingType", code, StringComparison.Ordinal);
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
