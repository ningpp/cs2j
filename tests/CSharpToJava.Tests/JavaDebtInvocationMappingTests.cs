using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for the C05/C06 Java-half javac debt cleanup in
/// InvocationExpressionTransformer / IdentifierExpressionTransformer:
/// primitive static/instance call lowering, string BCL semantic call sites,
/// mapped static receivers, and Length shape. These exercise the shared
/// expression transformer through the single-file conversion pipeline (the
/// same resolution logic backs the project-mode text visitor).
/// </summary>
public class JavaDebtInvocationMappingTests
{
    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "JavaDebtProbe.cs",
            Options = new ConversionOptions(),
        });
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return result;
    }

    [Fact]
    public void ULong_TryParse_BareAndQualified_BothMapToMathHelper()
    {
        var code = Convert("""
using System;
class P
{
    void M()
    {
        ulong a; long b;
        ulong.TryParse("1", out a);
        System.UInt64.TryParse("1", out a);
        long.TryParse("1", out b);
        System.Int64.TryParse("1", out b);
    }
}
""").GeneratedCode;

        Assert.Contains("MathHelper.tryParseULong(", code, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(code, "MathHelper.tryParseULong("));
        Assert.Equal(2, CountOccurrences(code, "MathHelper.tryParseLong("));
    }

    [Fact]
    public void Primitive_GetHashCode_MapsToBoxedStatic()
    {
        var code = Convert("""
class P
{
    int M1(int i, long l) => i.GetHashCode() ^ l.GetHashCode();
}
""").GeneratedCode;

        Assert.Contains("Integer.hashCode(i)", code, StringComparison.Ordinal);
        Assert.Contains("Long.hashCode(l)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Primitive_ToString_NoArgs_MapsToBoxedStatic()
    {
        var code = Convert("""
class P
{
    string M(int i) => i.ToString();
}
""").GeneratedCode;

        Assert.Contains("Integer.toString(i)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void String_Split_CharArgs_MapsToHelperWithCharArray()
    {
        var code = Convert("""
class P
{
    string[] M(string s) => s.Split(',', ';');
}
""").GeneratedCode;

        Assert.Contains("StringHelper.split(s, new char[]{',', ';'})", code, StringComparison.Ordinal);
    }

    [Fact]
    public void String_Equals_WithStringComparison_StaticAndInstance_MapsToHelper()
    {
        var code = Convert("""
using System;
class P
{
    bool M(string a, string b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        || a.Equals(b, StringComparison.Ordinal);
}
""").GeneratedCode;

        Assert.Equal(2, CountOccurrences(code, "StringHelper.equals("));
        Assert.Contains("StringComparison.OrdinalIgnoreCase", code, StringComparison.Ordinal);
        // Must not be swallowed by the System.Object arity-2 Objects.equals mapping.
        Assert.DoesNotContain("Objects.equals(a, b,", code, StringComparison.Ordinal);
    }

    [Fact]
    public void String_Format_MapsToFormatCs()
    {
        var code = Convert("""
class P
{
    string M(int v) => string.Format("{0:X2}", v);
}
""").GeneratedCode;

        Assert.Contains("StringHelper.formatCs(\"{0:X2}\", v)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void MappedStaticReceiver_UnmappedMethod_RendersMappedSimpleNameAndImport()
    {
        var code = Convert("""
namespace Cs2jUnsigned
{
    public static class UnsignedMath
    {
        public static ulong AndUInt64(ulong a, ulong b) => a & b;
    }
}

class P
{
    ulong M() => Cs2jUnsigned.UnsignedMath.AndUInt64(1UL, 2UL);
}
""").GeneratedCode;

        Assert.Contains("UnsignedMath.andUInt64(1L, 2L)", code, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.UnsignedMath;", code, StringComparison.Ordinal);
    }

    [Fact]
    public void StringBuilderLength_IsMethod_ArrayLength_IsField()
    {
        var code = Convert("""
using System.Text;
class P
{
    int M(StringBuilder sb, int[] arr) => sb.Length + arr.Length;
}
""").GeneratedCode;

        Assert.Contains("sb.length()", code, StringComparison.Ordinal);
        Assert.Contains("arr.length", code, StringComparison.Ordinal);
        Assert.DoesNotContain("arr.length()", code, StringComparison.Ordinal);
    }

    [Fact]
    public void StringComparison_MemberAccess_QualifiesAndImports()
    {
        var code = Convert("""
using System;
class P
{
    StringComparison M() => StringComparison.Ordinal;
}
""").GeneratedCode;

        Assert.Contains("StringComparison.Ordinal", code, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.StringComparison;", code, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
