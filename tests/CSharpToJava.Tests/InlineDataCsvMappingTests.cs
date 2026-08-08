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

    [Fact]
    public void InlineData_ControlCharacters_EscapedInCsv()
    {
        var result = Convert(@"
using Microsoft.VisualStudio.TestTools.UnitTesting;

class Test {
    [DataTestMethod]
    [InlineData(""\n"", ""\na"")]
    public void M(string a, string b) {}
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Raw newlines inside the literal would terminate the Java string — they must
        // be escaped as \\n inside the @CsvSource annotation.
        Assert.Contains("'\\n'", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("'\\na'", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void InlineData_FlagsEnumMember_ConvertedToIntegerValue()
    {
        var result = Convert(@"
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[Flags]
public enum MyOptions {
    None = 0,
    IgnoreCase = 1,
    CultureInvariant = 256
}

class Test {
    [DataTestMethod]
    [InlineData(""abc"", MyOptions.None)]
    [InlineData(""abc"", MyOptions.IgnoreCase)]
    [InlineData(""abc"", MyOptions.IgnoreCase | MyOptions.CultureInvariant)]
    public void M(string s, MyOptions options) {}
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Flags enum members should be converted to their integer values
        Assert.Contains(", 0\"", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(", 1\"", result.GeneratedCode, StringComparison.Ordinal);
        // 1 | 256 = 257
        Assert.Contains(", 257\"", result.GeneratedCode, StringComparison.Ordinal);
        // Should NOT contain the raw enum member names
        Assert.DoesNotContain("MyOptions.None", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("MyOptions.IgnoreCase", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void InlineData_RegularEnumMember_ConvertedToSimpleName()
    {
        var result = Convert(@"
using Microsoft.VisualStudio.TestTools.UnitTesting;

public enum Color {
    Red,
    Green,
    Blue
}

class Test {
    [DataTestMethod]
    [InlineData(""a"", Color.Red)]
    [InlineData(""b"", Color.Green)]
    [InlineData(""c"", Color.Blue)]
    public void M(string s, Color c) {}
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Regular enum members should be converted to simple names (without type prefix)
        Assert.Contains(", Red\"", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(", Green\"", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(", Blue\"", result.GeneratedCode, StringComparison.Ordinal);
        // Should NOT contain the qualified enum member names
        Assert.DoesNotContain("Color.Red", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Color.Green", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void InlineData_FlagsEnumWithCast_ConvertedToIntegerValue()
    {
        var result = Convert(@"
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[Flags]
public enum MyFlags {
    None = 0,
    A = 1,
    B = 2
}

class Test {
    [DataTestMethod]
    [InlineData(""x"", MyFlags.None | (MyFlags)0x80)]
    [InlineData(""y"", (MyFlags)0x80)]
    public void M(string s, MyFlags f) {}
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // 0 | 128 = 128
        Assert.Contains(", 128\"", result.GeneratedCode, StringComparison.Ordinal);
        // Should NOT contain the raw cast expression
        Assert.DoesNotContain("(MyFlags)0x80", result.GeneratedCode, StringComparison.Ordinal);
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
