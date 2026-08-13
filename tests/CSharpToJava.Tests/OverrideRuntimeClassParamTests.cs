using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class OverrideRuntimeClassParamTests
{
    [Fact]
    public void OverrideWithMethodLevelTypeofT_KeepsBaseErasureSignature()
    {
        var result = Convert(@"
using System;

public abstract class MemberAccessor
{
    public abstract Func<object, TProperty> CreateFieldSetter<TProperty>(FieldInfo fieldInfo);
}

public class ReflectionEmitMemberAccessor : MemberAccessor
{
    private static dynamic CreateFieldSetter(FieldInfo fieldInfo, Type runtimeFieldType)
    {
        return null;
    }

    public override Func<object, TProperty> CreateFieldSetter<TProperty>(FieldInfo fieldInfo)
    {
        return CreateFieldSetter(fieldInfo, typeof(TProperty));
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("createFieldSetter(FieldInfo fieldInfo)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("createFieldSetter(Class<TProperty>", code, StringComparison.Ordinal);
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
