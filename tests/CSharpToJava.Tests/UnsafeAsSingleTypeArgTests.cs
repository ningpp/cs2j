using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class UnsafeAsSingleTypeArgTests
{
    [Fact]
    public void UnsafeAs_ObjectOverload_MapsWithTargetTypeCast()
    {
        var result = Convert(@"
using System.Runtime.CompilerServices;

public class Sample
{
    public static string M(object o)
    {
        return Unsafe.As<string>(o);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("import io.github.ningpp.compat.Unsafe", code, StringComparison.Ordinal);
        Assert.Contains("(String)(Object)Unsafe.as(o)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsafeAs_FieldInfo_MemberAccessOnResultResolves()
    {
        var result = Convert(@"
using System.Reflection;
using System.Runtime.CompilerServices;

public class Sample
{
    public static System.Type M(MemberInfo memberInfo)
    {
        return Unsafe.As<FieldInfo>(memberInfo)!.FieldType;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("(FieldInfo)(Object)Unsafe.as(memberInfo).getFieldType()", code, StringComparison.Ordinal);
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
