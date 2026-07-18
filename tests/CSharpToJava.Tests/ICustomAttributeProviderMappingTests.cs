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
}
