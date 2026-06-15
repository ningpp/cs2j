using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class XmlLinqCompatibilityTests
{
    [Fact]
    public void XDocumentDescendants_WithNameAndAttribute_MapsToCompatTypes()
    {
        var result = Convert("""
using System.Linq;
using System.Xml.Linq;

class Sample
{
    static void Parse(string filename)
    {
        XDocument doc = XDocument.Load(filename);
        var nodes = doc.Descendants().Where(e => e.Name.LocalName == "Node");
        foreach (var nodeElement in nodes)
        {
            string id = nodeElement.Attribute("Id")?.Value;
        }
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("import io.github.ningpp.compat.XDocument;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("XDocument doc = XDocument.load(filename);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("doc.descendants()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("e.getName().LocalName", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("nodeElement.attribute(\"Id\")", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet.system.Xml.Linq", result.GeneratedCode, StringComparison.Ordinal);
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
