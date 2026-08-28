using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// C# uint/ulong values format UNSIGNED digits in string contexts, but the Java
/// mapping reuses int/long bit patterns — StringBuilder.Append(uint) emitted as
/// append(int) writes the SIGNED form (uint.MaxValue → "-1"). The converted
/// JsonNumberTestData then built JSON like {"uint0": -1} and every reader Get
/// call failed with FormatException. Must route through the JDK unsigned
/// formatters.
/// </summary>
public class UnsignedNumberFormattingTests
{
    [Fact]
    public void StringBuilderAppendUInt_EmitsUnsignedFormatting()
    {
        var result = Convert(@"
using System.Text;

class Sample
{
    string Build(uint value)
    {
        var builder = new StringBuilder();
        builder.Append(value);
        return builder.ToString();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // uint.MaxValue stored as the Java int bit pattern (-1) — masked to unsigned.
        Assert.Contains("append((value & 0xFFFFFFFFL))", code, StringComparison.Ordinal);
        Assert.DoesNotContain("toUnsignedString", code, StringComparison.Ordinal);
    }

    [Fact]
    public void StringBuilderAppendUlong_EmitsUnsignedFormatting()
    {
        var result = Convert(@"
using System.Text;

class Sample
{
    string Build(ulong value)
    {
        var builder = new StringBuilder();
        builder.Append(value);
        return builder.ToString();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("Long.toUnsignedString(", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ToStringOnUInt_EmitsUnsignedFormatting()
    {
        var result = Convert(@"
class Sample
{
    string Show(uint value)
    {
        return value.ToString();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // uint constants may surface as Java long literals, so the mask widens to
        // long and formats via String.valueOf — unsigned digits either way.
        Assert.Contains("String.valueOf(value & 0xFFFFFFFFL)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void StringBuilderAppendInt_KeepsSignedAppend()
    {
        var result = Convert(@"
using System.Text;

class Sample
{
    string Build(int value)
    {
        var builder = new StringBuilder();
        builder.Append(value);
        return builder.ToString();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // Signed ints keep the plain numeric append — no unsigned wrapping.
        Assert.DoesNotContain("toUnsignedString(", code, StringComparison.Ordinal);
        Assert.Contains(".append(value)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void StringBuilderAppendUshortAndByte_EmitsWidthMask()
    {
        var result = Convert(@"
using System.Text;

class Sample
{
    string Build(ushort u, byte b)
    {
        var builder = new StringBuilder();
        builder.Append(u);
        builder.Append(b);
        return builder.ToString();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains(" & 0xFFFF)", code, StringComparison.Ordinal);
        Assert.Contains(" & 0xFF)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void InterpolationWithUInt_MasksToUnsigned()
    {
        var result = Convert(@"
class Sample
{
    string Show(uint value)
    {
        return $""value={value}"";
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains(" & 0xFFFFFFFFL", code, StringComparison.Ordinal);
    }

    [Fact]
    public void StringConcatWithUlong_WrapsUnsigned()
    {
        var result = Convert(@"
class Sample
{
    string Show(ulong value)
    {
        return string.Concat(""v="", value);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("Long.toUnsignedString(value)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void StringBuilderAppendSbyte_KeepsSignedAppend()
    {
        var result = Convert(@"
using System.Text;

class Sample
{
    string Build(sbyte value)
    {
        var builder = new StringBuilder();
        builder.Append(value);
        return builder.ToString();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // sbyte is SIGNED — no unsigned masking may be applied.
        Assert.DoesNotContain("0xFF", code, StringComparison.Ordinal);
        Assert.DoesNotContain("toUnsignedString", code, StringComparison.Ordinal);
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
