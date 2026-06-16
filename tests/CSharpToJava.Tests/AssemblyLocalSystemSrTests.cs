using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class AssemblyLocalSystemSrTests
{
    [Fact]
    public async Task InternalSystemSr_UsesProjectScopedJavaNameAndReferences()
    {
        var srFile = """
namespace System
{
    internal static class SR
    {
        internal static string Xml_InvalidRootData => "Data at the root level is invalid.";
    }
}
""";

        var readerFile = """
namespace System.Xml
{
    public class Reader
    {
        public string Read()
        {
            return SR.Xml_InvalidRootData;
        }
    }
}
""";

        var results = await new ProjectConversionPipeline(new ConversionOptions())
            .ConvertProjectAsync(
                new[]
                {
                    new SourceFile { FilePath = "SR.cs", Content = srFile },
                    new SourceFile { FilePath = "Reader.cs", Content = readerFile },
                },
                projectName: "System.Private.Xml");

        var srResult = Assert.Single(results, r => r.FileName == "SystemPrivateXmlSR.java");
        Assert.True(srResult.Success, string.Join("\n", srResult.Diagnostics));
        Assert.Contains("public final class SystemPrivateXmlSR", srResult.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("private SystemPrivateXmlSR()", srResult.GeneratedCode, StringComparison.Ordinal);

        var readerResult = Assert.Single(results, r => r.FileName == "Reader.java");
        Assert.True(readerResult.Success, string.Join("\n", readerResult.Diagnostics));
        Assert.Contains("SystemPrivateXmlSR.getXml_InvalidRootData()", readerResult.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("return SR.getXml_InvalidRootData();", readerResult.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InternalDotnetSystemSr_UsesProjectScopedJavaNameAndReferences()
    {
        var srFile = """
namespace dotnet.system
{
    internal static class SR
    {
        internal static string net_uri_BadPort => "Invalid URI: Invalid port specified.";
    }
}
""";

        var uriFile = """
namespace dotnet.system
{
    public class UriParser
    {
        public string Read()
        {
            return SR.net_uri_BadPort;
        }
    }
}
""";

        var results = await new ProjectConversionPipeline(new ConversionOptions())
            .ConvertProjectAsync(
                new[]
                {
                    new SourceFile { FilePath = "SR.cs", Content = srFile },
                    new SourceFile { FilePath = "UriParser.cs", Content = uriFile },
                },
                projectName: "System.Private.Uri");

        var srResult = Assert.Single(results, r => r.FileName == "SystemPrivateUriSR.java");
        Assert.True(srResult.Success, string.Join("\n", srResult.Diagnostics));
        Assert.Contains("public final class SystemPrivateUriSR", srResult.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("private SystemPrivateUriSR()", srResult.GeneratedCode, StringComparison.Ordinal);

        var uriResult = Assert.Single(results, r => r.FileName == "UriParser.java");
        Assert.True(uriResult.Success, string.Join("\n", uriResult.Diagnostics));
        Assert.Contains("SystemPrivateUriSR.getNet_uri_BadPort()", uriResult.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("return SR.getNet_uri_BadPort();", uriResult.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InternalDotnetSystemSr_StaticMethodCallsUseProjectScopedJavaName()
    {
        var srFile = """
namespace dotnet.system
{
    internal static class SR
    {
        internal static string net_uri_BadPort => "Invalid URI: Invalid port specified.";

        internal static string Format(string resourceFormat, object arg)
        {
            return resourceFormat + arg;
        }
    }
}
""";

        var uriFile = """
namespace dotnet.system
{
    public class UriParser
    {
        public string Read(object value)
        {
            return SR.Format(SR.net_uri_BadPort, value);
        }
    }
}
""";

        var results = await new ProjectConversionPipeline(new ConversionOptions())
            .ConvertProjectAsync(
                new[]
                {
                    new SourceFile { FilePath = "SR.cs", Content = srFile },
                    new SourceFile { FilePath = "UriParser.cs", Content = uriFile },
                },
                projectName: "System.Private.Uri");

        var uriResult = Assert.Single(results, r => r.FileName == "UriParser.java");
        Assert.True(uriResult.Success, string.Join("\n", uriResult.Diagnostics));
        Assert.Contains(
            "SystemPrivateUriSR.format(SystemPrivateUriSR.getNet_uri_BadPort(), value)",
            uriResult.GeneratedCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain("return SR.format(", uriResult.GeneratedCode, StringComparison.Ordinal);
    }
}
