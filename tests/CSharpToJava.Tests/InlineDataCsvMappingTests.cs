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
    public void InlineData_ControlCharacters_RouteToMethodSourceDataMethod()
    {
        var result = Convert(@"
using Microsoft.VisualStudio.TestTools.UnitTesting;

class Test {
    [DataTestMethod]
    [InlineData(""\n"", ""\na"")]
    public void M(string a, string b) {}
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Strings with control characters are invalid CSV (a raw newline would
        // terminate the record), so the rows route to a synthetic @MethodSource
        // data method that emits the strings with Java escapes.
        Assert.Contains("MethodSource(\"m_data1\")", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import org.junit.jupiter.params.provider.MethodSource;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("\"\\n\", \"\\na\"", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("CsvSource", result.GeneratedCode, StringComparison.Ordinal);
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

    [Fact]
    public void InlineData_FloatingSpecialValues_ConvertedToJUnitCsvLiterals()
    {
        var result = Convert(@"
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

class Test {
    [DataTestMethod]
    [InlineData(""-1"", double.NegativeInfinity)]
    [InlineData(""1"", double.PositiveInfinity)]
    [InlineData(""nan"", double.NaN)]
    public void M(string s, double d) {}

    [DataTestMethod]
    [InlineData(""-1"", float.NegativeInfinity)]
    public void N(string s, float f) {}
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // JUnit's CSV-to-double/float conversion accepts "Infinity", "-Infinity"
        // and "NaN", but not the C# member-access expression text.
        Assert.Contains("-Infinity", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Infinity", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("NaN", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("double.NegativeInfinity", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("float.NegativeInfinity", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void InlineData_FloatingMinMaxValues_ConvertedToNumericLiterals()
    {
        var result = Convert(@"
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

class Test {
    [DataTestMethod]
    [InlineData(double.MinValue)]
    [InlineData(double.MaxValue)]
    public void M(double d) {}

    [DataTestMethod]
    [InlineData(float.MinValue)]
    [InlineData(float.MaxValue)]
    public void N(float f) {}
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // JUnit's CSV-to-double/float conversion cannot parse "double.MinValue"
        // member-access text; emit the numeric literals instead.
        Assert.Contains("-1.7976931348623157E308", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("1.7976931348623157E308", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("-3.4028235E38", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("3.4028235E38", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("double.MinValue", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("double.MaxValue", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("float.MinValue", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("float.MaxValue", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void InlineData_ConstantExpression_RouteToMethodSourceDataMethod()
    {
        var result = Convert(@"
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

class Test {
    [DataTestMethod]
    [InlineData(int.MaxValue / 32 + 1)]
    public void M(int x) {}
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // `int.MaxValue / 32 + 1` is a C# constant expression, but raw C# text
        // cannot round-trip through @CsvSource. It must be emitted through the
        // synthetic data method as a real Java expression.
        Assert.Contains("MethodSource(\"m_data1\")", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import org.junit.jupiter.params.provider.MethodSource;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("CsvSource", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("int.MaxValue / 32 + 1\"", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void InlineData_TypeofArgument_RouteToMethodSourceDataMethod()
    {
        var result = Convert(@"
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

class Test {
    [DataTestMethod]
    [InlineData(typeof(string))]
    public void M(Type t) {}
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // JUnit's CSV String→Class conversion is not built-in, so typeof(X) must
        // go through the data method and become a real Java class literal.
        Assert.Contains("MethodSource", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("String.class", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("CsvSource", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void InlineData_TypeofNestedGenericArgument_RouteToMethodSourceDataMethod()
    {
        var result = Convert(@"
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

class Test {
    public class Box<T> {}

    [DataTestMethod]
    [InlineData(typeof(Box<string>))]
    public void M(Type t) {}
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // The nested generic class literal must be built from the simple name
        // (Outer.Box.class), not from the fully-qualified generic argument.
        Assert.Contains("MethodSource", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Box.class", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("CsvSource", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void InlineData_ConstIdentifierExpression_RouteToMethodSourceDataMethod()
    {
        var result = Convert(@"
using Microsoft.VisualStudio.TestTools.UnitTesting;

class Test {
    const int Threshold = 10;

    [DataTestMethod]
    [InlineData(Threshold - 3)]
    public void M(int x) {}
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // `Threshold - 3` is a compile-time constant but cannot be evaluated by
        // JUnit's CSV converter; it must be routed to the data method.
        Assert.Contains("MethodSource", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("CsvSource", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Threshold - 3\"", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void InlineData_NegativeLongConstant_GetsLongSuffix()
    {
        var result = Convert(@"
using Microsoft.VisualStudio.TestTools.UnitTesting;

class Test {
    [DataTestMethod]
    [InlineData(-2147483649)]
    public void M(double x) {}
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // -2147483649 is below int range; the Java literal must carry the L suffix
        // (or a .0 double form when the parameter is double), otherwise javac
        // reports "integer number too large".
        Assert.Contains("MethodSource", result.GeneratedCode, StringComparison.Ordinal);
        bool hasValidLiteral = result.GeneratedCode.Contains("-2147483649.0", StringComparison.Ordinal)
            || result.GeneratedCode.Contains("-2147483649L", StringComparison.Ordinal);
        Assert.True(hasValidLiteral, "expected -2147483649.0 or -2147483649L");
        Assert.DoesNotContain("CsvSource", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void InlineData_RegularEnumToNumericParameter_RouteToMethodSourceDataMethod()
    {
        var result = Convert(@"
using Microsoft.VisualStudio.TestTools.UnitTesting;

public enum SampleEnumByte : byte {
    A = 1,
    B = 2
}

class Test {
    [DataTestMethod]
    [InlineData(SampleEnumByte.A)]
    public void M(long x) {}
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // A regular (non-flags) enum member name cannot be converted by JUnit into
        // a numeric parameter; the expression must go through the data method and
        // be unwrapped to its numeric value.
        Assert.Contains("MethodSource", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("getValue()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("CsvSource", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void InlineData_LongExpressionToIntParameter_AddsNarrowingCast()
    {
        var result = Convert(@"
using Microsoft.VisualStudio.TestTools.UnitTesting;

class Test {
    const long Threshold = 349525L;

    [DataTestMethod]
    [InlineData(Threshold - 3)]
    public void M(int x) {}
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // `Threshold - 3` is a long constant expression converted to an int
        // parameter; the data method must narrow it so JUnit receives an Integer.
        Assert.Contains("MethodSource", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("(int) (Threshold - 3)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("CsvSource", result.GeneratedCode, StringComparison.Ordinal);
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
