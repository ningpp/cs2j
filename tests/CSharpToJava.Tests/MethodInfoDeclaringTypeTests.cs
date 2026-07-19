using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# MethodInfo.DeclaringType maps to Java Method.getDeclaringClass().
/// The default property-name mapping produced getDeclaringType(), which does not
/// exist on java.lang.reflect.Method.
/// </summary>
public class MethodInfoDeclaringTypeTests
{
    [Fact]
    public void MethodInfo_DeclaringType_MapsToGetDeclaringClass()
    {
        var result = Convert(@"
using System;
using System.Reflection;

class Test {
    Type GetDeclaringType(MethodInfo methodInfo) {
        return methodInfo.DeclaringType;
    }
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("methodInfo.getDeclaringClass()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".getDeclaringType()", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
