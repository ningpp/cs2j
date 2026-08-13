using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class GlobalAliasQualifiedNameTests
{
    [Fact]
    public void GlobalAliasQualifiedName_MemberAccess_MapsToJavaTypeName()
    {
        var result = Convert(@"
public class Sample
{
    public static int M()
    {
        return (int)global::System.Text.Json.JsonCommentHandling.Disallow;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.DoesNotContain("TODO: AliasQualifiedName", code, StringComparison.Ordinal);
    }

    [Fact]
    public void MethodNamedYield_IsEscaped()
    {
        var result = Convert(@"
public class Sample
{
    private static int Yield(int a, int b)
    {
        return a + b;
    }

    public static int M()
    {
        return Yield(1, 2);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.DoesNotContain("yield(", code, StringComparison.Ordinal);
        Assert.Contains("yieldValue(", code, StringComparison.Ordinal);
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
