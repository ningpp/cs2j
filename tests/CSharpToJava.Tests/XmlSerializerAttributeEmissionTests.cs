using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// [XmlAttribute]/[XmlIgnore]/[XmlArray]/[XmlArrayItem] 挂在普通字段上时同样要落成
/// compat 注解：XmlSerializer 运行时按反射读取它们（真实链 serializer-tests 的 POCO
/// 特性在 public 字段上，属性是 member-desugar 展开的 get_X/set_X 方法）。
/// </summary>
public class XmlSerializerAttributeEmissionTests
{
    [Fact]
    public void FieldLevelXmlAttributes_EmitCompatAnnotations()
    {
        var result = Convert("""
using System.Xml.Serialization;

public class AttributedItem
{
    [XmlAttribute("value")]
    public string valueValue;
}

public class WithIgnored
{
    public string included;
    [XmlIgnore]
    public string ignored;
}

public class ItemList
{
    [XmlArray("Items")]
    [XmlArrayItem("Item")]
    public string[] items;
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;
        Assert.Contains("@XmlAttribute(name=\"value\")", code);
        Assert.Contains("@XmlIgnore", code);
        Assert.Contains("@XmlArray(elementName=\"Items\")", code);
        Assert.Contains("@XmlArrayItem(elementName=\"Item\")", code);
        Assert.Contains("import io.github.ningpp.compat.xml.XmlAttribute;", code);
        Assert.Contains("import io.github.ningpp.compat.xml.XmlIgnore;", code);
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
