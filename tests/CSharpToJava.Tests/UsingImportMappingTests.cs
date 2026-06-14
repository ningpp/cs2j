using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

public class UsingImportMappingTests
{
    private ConversionResult Convert(string src, string file = "Sample.cs")
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = src,
            FileName = file,
            Options = new ConversionOptions(),
        });
    }

    [Fact]
    public void UsingSystemXml_GeneratesDotnetXmlImport()
    {
        var result = Convert(@"
using System.Xml;
namespace Foo {
    public class Bar { }
}");
        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("import dotnet.xml.*;", result.GeneratedCode);
    }

    [Fact]
    public void UsingSystemXmlSchema_GeneratesDotnetXmlSchemaImport()
    {
        var result = Convert(@"
using System.Xml.Schema;
namespace Foo {
    public class Bar { }
}");
        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("import dotnet.xml.schema.*;", result.GeneratedCode);
    }

    [Fact]
    public void UsingSystemXmlXpath_GeneratesDotnetXmlXpathImport()
    {
        var result = Convert(@"
using System.Xml.XPath;
namespace Foo {
    public class Bar { }
}");
        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("import dotnet.xml.xpath.*;", result.GeneratedCode);
    }

    [Fact]
    public void UsingSystemXmlXsl_GeneratesDotnetXmlXslImport()
    {
        var result = Convert(@"
using System.Xml.Xsl;
namespace Foo {
    public class Bar { }
}");
        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("import dotnet.xml.xsl.*;", result.GeneratedCode);
    }

    [Fact]
    public void UsingSystem_CollectionsGeneric_DoesNotGenerateImport()
    {
        var result = Convert(@"
using System.Collections.Generic;
namespace Foo {
    public class Bar { }
}");
        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics.Select(d => d.Message))}");
        // System.Collections.Generic is explicitly mapped to null (no import)
        Assert.DoesNotContain("import System.Collections.Generic", result.GeneratedCode);
    }

    [Fact]
    public void UsingSystemLinq_DoesNotGenerateImport()
    {
        var result = Convert(@"
using System.Linq;
namespace Foo {
    public class Bar { }
}");
        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics.Select(d => d.Message))}");
        // System.Linq is explicitly mapped to null (no import)
        Assert.DoesNotContain("import System.Linq", result.GeneratedCode);
    }

    [Fact]
    public void UsingSystemIO_DoesNotGeneratePhantomImport()
    {
        var result = Convert(@"
using System.IO;
namespace Foo {
    public class Bar { }
}");
        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics.Select(d => d.Message))}");
        // System.IO has no explicit namespace mapping; the derived "dotnet.system.IO"
        // package does not exist, so no import should be generated.
        Assert.DoesNotContain("import dotnet.system.IO", result.GeneratedCode);
    }

    [Fact]
    public void UsingSystemGlobalization_DoesNotGeneratePhantomImport()
    {
        var result = Convert(@"
using System.Globalization;
namespace Foo {
    public class Bar { }
}");
        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics.Select(d => d.Message))}");
        // System.Globalization has no explicit namespace mapping; the derived
        // "dotnet.system.Globalization" package does not exist.
        Assert.DoesNotContain("import dotnet.system.Globalization", result.GeneratedCode);
    }
}
