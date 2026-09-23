using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Java cannot emit IL, so ReflectionEmitMemberAccessor must be replaced by the
/// reflection-based MemberAccessor implementation, and Activator.CreateInstance's
/// (Type, bool nonPublic) overload must not be treated as a params object[] arg.
/// Regression: System.Text.Json ArrayTests ILGenerator.Emit failures.
/// </summary>
public class MemberAccessorStrategyConversionTests
{
    [Fact]
    public void ReflectionEmitMemberAccessorInstantiation_UsesReflectionMemberAccessor()
    {
        var result = Convert(@"
using System;

namespace dotnet.text.json.Serialization
{
    public class ReflectionEmitMemberAccessor { }
    public class ReflectionMemberAccessor { }

    public class Options
    {
        public object M() => new ReflectionEmitMemberAccessor();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("new ReflectionMemberAccessor()", code, StringComparison.Ordinal);
        Assert.DoesNotContain("new ReflectionEmitMemberAccessor()", code, StringComparison.Ordinal);
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
