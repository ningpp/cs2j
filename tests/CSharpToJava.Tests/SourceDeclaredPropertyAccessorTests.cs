using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// 被移植成源内类的 BCL 类型（System.Xml.XmlReader → dotnet.xml.XmlReader）把 C#
/// 属性声明为 C11 访问器方法（LocalName()）；使用点必须发同一形态，不能按 Java bean
/// getter（getLocalName）——真实链 root-tests CorrectnessTest 的 33 行编译错误。
/// </summary>
public class SourceDeclaredPropertyAccessorTests
{
    [Fact]
    public void BclPropertyOnSourceDeclaredPort_EmitsC11Accessor()
    {
        var result = Convert("""
namespace dotnet.xml
{
    public abstract class XmlReader
    {
        // member-desugar 展开后的访问器形态（get_X/set_X），Java 侧发射为 X()/X(v)。
        public abstract string get_LocalName();
        public abstract int get_AttributeCount();
    }
}

class Host
{
    public static void Use(System.Xml.XmlReader reader)
    {
        var name = reader.LocalName;
        var count = reader.AttributeCount;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;
        Assert.Contains("reader.LocalName()", code);
        Assert.Contains("reader.AttributeCount()", code);
        Assert.DoesNotContain("getLocalName()", code);
        Assert.DoesNotContain("getAttributeCount()", code);
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
