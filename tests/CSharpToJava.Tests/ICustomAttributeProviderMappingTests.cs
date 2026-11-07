using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ICustomAttributeProviderMappingTests
{
    [Fact]
    public void ICustomAttributeProvider_Parameter_MappedToCompatType()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = """
using System;
using System.Reflection;

class Sample
{
    public void CollectAttributes(ICustomAttributeProvider provider)
    {
        object[] attrs = provider.GetCustomAttributes(false);
        object[] typedAttrs = provider.GetCustomAttributes(typeof(ObsoleteAttribute), false);
        bool defined = provider.IsDefined(typeof(ObsoleteAttribute), false);
    }
}
""",
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.DoesNotContain("System.Reflection.ICustomAttributeProvider", code, StringComparison.Ordinal);
        Assert.Contains("ICustomAttributeProvider provider", code, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.ICustomAttributeProvider;", code, StringComparison.Ordinal);
        Assert.Contains("provider.getCustomAttributes(false)", code, StringComparison.Ordinal);
        Assert.Contains("provider.getCustomAttributes(", code, StringComparison.Ordinal);
        Assert.Contains(".class, false)", code, StringComparison.Ordinal);
        Assert.Contains("provider.isDefined(", code, StringComparison.Ordinal);
    }

    [Fact]
    public void MemberInfo_GetCustomAttributes_StaysInstanceCall_NotTypeHelperStatic()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = """
using System;
using System.Reflection;

class Sample
{
    private static object[] Read(MemberInfo memberInfo, Type attributeType)
    {
        return memberInfo.GetCustomAttributes(attributeType, false);
    }
}
""",
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        // TypeHelper.getCustomAttributes 的入参是 Class：只有 System.Type/TypeInfo 接收者
        // 能走该静态助手。MemberInfo 接收者必须保留 compat 自身的实例重载，且「无映射名」
        // 回退点不能把带点的助手名直接拼到接收者后面（memberInfo.TypeHelper.… 非法）。
        Assert.Contains("memberInfo.getCustomAttributes(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("memberInfo.TypeHelper", code, StringComparison.Ordinal);
    }
}
