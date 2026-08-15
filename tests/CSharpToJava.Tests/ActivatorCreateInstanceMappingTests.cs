using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// System.Activator.CreateInstance(Type, object) uses a params object[] overload;
/// a single non-array object argument (including null) must become one element of
/// the argument array, not a cast of the object itself to Object[].
/// </summary>
public class ActivatorCreateInstanceMappingTests
{
    [Fact]
    public void CreateInstanceWithSingleObjectArgument_WrapsInObjectArray()
    {
        var result = Convert("""
using System;

class C
{
    object M(Type type, object arg)
    {
        return Activator.CreateInstance(type, arg);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("Activator.createInstance(type, new Object[] { arg })", code, StringComparison.Ordinal);
        Assert.DoesNotContain("(Object[]) arg", code, StringComparison.Ordinal);
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
