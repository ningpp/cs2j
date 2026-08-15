using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// System.Type.IsAssignableFrom must route through TypeHelper.isAssignableFrom so
/// Java arrays (which do not implement CSharpGenericIterable) still satisfy the
/// C# "IEnumerable.IsAssignableFrom(array)" check used by
/// IEnumerableConverterFactory.CanConvert.
/// </summary>
public class TypeIsAssignableFromTests
{
    [Fact]
    public void IEnumerableIsAssignableFrom_RoutesThroughTypeHelper()
    {
        var result = Convert(@"
using System;
using System.Collections;

class C
{
    bool M(Type type)
    {
        return typeof(IEnumerable).IsAssignableFrom(type);
    }
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("TypeHelper.isAssignableFrom(CSharpGenericIterable.class, type)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("CSharpGenericIterable.class.isAssignableFrom(type)", code, StringComparison.Ordinal);
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
