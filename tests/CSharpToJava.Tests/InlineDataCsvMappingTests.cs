using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class InlineDataCsvMappingTests
{
    [Fact]
    public void InlineData_NullValue_UsesNullLiteralInCsv()
    {
        var result = Convert(@"
using Microsoft.VisualStudio.TestTools.UnitTesting;

class Test {
    [DataTestMethod]
    [InlineData(""http"", null, ""http"", """")]
    public void Ctor(string scheme, string host, string expectedScheme, string expectedHost) {}
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // null should appear as literal "null" in CSV, not empty field
        Assert.Contains("null", result.GeneratedCode, StringComparison.Ordinal);
        // nullValues should only contain "null", not ""
        Assert.DoesNotContain("nullValues = {\"\", \"null\"}", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("nullValues = {\"null\"}", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void InlineData_EmptyString_UsesSingleQuotedEmptyInCsv()
    {
        var result = Convert(@"
using Microsoft.VisualStudio.TestTools.UnitTesting;

class Test {
    [DataTestMethod]
    [InlineData("""")]
    public void M(string s) {}
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Empty string should be represented as '' in CSV
        Assert.Contains("''", result.GeneratedCode, StringComparison.Ordinal);
        // No nullValues attribute when there's no null
        Assert.DoesNotContain("nullValues", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void InlineData_StringWithSpaces_WrappedInSingleQuotes()
    {
        var result = Convert(@"
using Microsoft.VisualStudio.TestTools.UnitTesting;

class Test {
    [DataTestMethod]
    [InlineData("" "", ""127.0.0.1 "", ""normal"")]
    public void M(string a, string b, string c) {}
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Strings with leading/trailing spaces should be wrapped in single quotes
        Assert.Contains("' '", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("'127.0.0.1 '", result.GeneratedCode, StringComparison.Ordinal);
        // Normal string without spaces should not be wrapped
        Assert.Contains("normal", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void InlineData_NullAndEmptyString_DistinctInCsv()
    {
        var result = Convert(@"
using Microsoft.VisualStudio.TestTools.UnitTesting;

class Test {
    [DataTestMethod]
    [InlineData(null, """")]
    [InlineData("""", null)]
    public void M(string a, string b) {}
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // nullValues should only contain "null"
        Assert.Contains("nullValues = {\"null\"}", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("nullValues = {\"\", \"null\"}", result.GeneratedCode, StringComparison.Ordinal);
        // Both null literal and '' should be present in the generated code
        // null → "null" in CSV, empty string → '' in CSV
        Assert.Contains("null", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("''", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
