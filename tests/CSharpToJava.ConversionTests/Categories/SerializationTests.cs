using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of C# XML serialization types and attributes to Java.
/// </summary>
public class SerializationTests : ConversionTestBase
{
    [Fact]
    public void XmlSerializer_MappedToCompatClass()
    {
        var result = Convert("class C { public void M() { var s = new System.Xml.Serialization.XmlSerializer(typeof(C)); } }");
        AssertConversion(result,
            "import io.github.ningpp.compat.XmlSerializer;",
            "new XmlSerializer(C.class)");
    }

    [Fact]
    public void XmlAttribute_ConvertsToCompatAnnotation()
    {
        var result = Convert(@"
using System.Xml.Serialization;
public class AttributedItem {
    [XmlAttribute(""value"")] public string Value { get; set; }
}");
        AssertConversion(result,
            "import io.github.ningpp.compat.xml.XmlAttribute;",
            "@XmlAttribute(name=\"value\")");
    }

    [Fact]
    public void XmlIgnore_ConvertsToCompatAnnotation()
    {
        var result = Convert(@"
using System.Xml.Serialization;
public class IgnoredItem {
    [XmlIgnore] public string Value { get; set; }
}");
        AssertConversion(result,
            "import io.github.ningpp.compat.xml.XmlIgnore;",
            "@XmlIgnore");
    }

    [Fact]
    public void XmlArray_ConvertsToCompatAnnotation()
    {
        var result = Convert(@"
using System.Xml.Serialization;
public class ItemList {
    [XmlArray(""Items"")]
    [XmlArrayItem(""Item"")]
    public string[] Items { get; set; }
}");
        AssertConversion(result,
            "import io.github.ningpp.compat.xml.XmlArray;",
            "import io.github.ningpp.compat.xml.XmlArrayItem;",
            "@XmlArray(elementName=\"Items\")",
            "@XmlArrayItem(elementName=\"Item\")");
    }
}
