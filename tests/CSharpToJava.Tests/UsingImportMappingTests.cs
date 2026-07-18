using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System;
using System.IO;
using Xunit;

namespace CSharpToJava.Tests;

public class UsingImportMappingTests
{
    private static ConversionOptions CreateProjectOptions()
        => new()
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
            PreferStreamApi = false,
        };

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

    [Fact]
    public void UsingSystem_DoesNotGeneratePhantomImport()
    {
        var result = Convert(@"
using System;
namespace Foo {
    public class Bar { }
}");
        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics.Select(d => d.Message))}");
        // The System namespace mapping is a catch-all for type/package fallback,
        // not a real compat package that can be imported as dotnet.system.*.
        Assert.DoesNotContain("import dotnet.system.*;", result.GeneratedCode);
    }

    [Fact]
    public void MSTestTestContextField_ImportsCompatType()
    {
        var result = Convert(@"
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.Msagl.UnitTests.Constraints {
    internal class ClusterDef {
        internal static TestContext TestContext { get; set; }
    }
}");

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("import Microsoft.VisualStudio.TestTools.UnitTesting.TestContext;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("private static TestContext testContext;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectRegexOptionsStaticMember_UsesCompatImportOnly()
    {
        var pipeline = new ProjectConversionPipeline(CreateProjectOptions());
        var results = await pipeline.ConvertProjectAsync(new[]
        {
            new SourceFile
            {
                FilePath = "TestFileStrings.cs",
                Content = @"
using System.Text.RegularExpressions;

namespace Microsoft.Msagl.UnitTests.Constraints {
    internal struct TestFileStrings {
        internal static Regex ParseSeed = new Regex(
            @""^Seed\s+(?<" + "seed" + @">(0x)?\S+)"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }
}"
            }
        });

        var result = Assert.Single(results, r => r.FileName == "TestFileStrings.java");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("import io.github.ningpp.compat.RegexOptions;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet.system.Text.RegularExpressions.RegexOptions", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectCompareOptionsStaticMember_UsesCompatImportOnly()
    {
        var pipeline = new ProjectConversionPipeline(CreateProjectOptions());
        var results = await pipeline.ConvertProjectAsync(new[]
        {
            new SourceFile
            {
                FilePath = "CompareOptionsUsage.cs",
                Content = @"
using System.Globalization;

internal class CompareOptionsUsage {
    private static CompareInfo s_compareInfo = CultureInfo.InvariantCulture.CompareInfo;
    public static int FindOrdinal(string s1, string s2) {
        return s_compareInfo.IndexOf(s1, s2, CompareOptions.Ordinal);
    }
}"
            }
        });

        var result = Assert.Single(results, r => r.FileName == "CompareOptionsUsage.java");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("import io.github.ningpp.compat.CompareOptions;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Globalization.CompareOptions", result.GeneratedCode, StringComparison.Ordinal);
    }
}
