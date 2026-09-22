using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Tests;

/// <summary>
/// C# overloads that differ only in the signedness of the same-width primitive
/// (long/ulong, int/uint) collide after the unsigned→signed Java type mapping.
/// The later-declared overload must survive under the erasure suffix and call
/// sites must resolve by static argument type — otherwise every ulong payload
/// (e.g. the EnumConverter's UInt64 write path) silently formats as SIGNED,
/// producing "MyUInt64Enum":-1 where C# writes 18446744073709551615.
/// </summary>
public class UnsignedOverloadCollisionTests
{
    [Fact]
    public void UnsignedOverload_SameWidthCollide_SurvivesUnderErasureSuffix()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
class NumberWriter
{
    public void WriteValue(long value)
    {
    }

    public void WriteValue(ulong value)
    {
    }
}",
            FileName = "NumberWriter.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success,
            "Conversion failed: " + string.Join("; ", result.Diagnostics.Select(d => $"[{d.Severity}] {d.Message}")));

        var code = result.GeneratedCode;

        // The signed overload (first in source order) keeps the plain name; the
        // unsigned one must NOT be dropped or merged into it.
        Assert.Contains("void writeValue(long value)", code);
        Assert.Matches(@"void writeValue_erasure_2\(long value\)", code);
    }

    [Fact]
    public void UnsignedOverload_CallSiteBindsToTheSuffixedOverload()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
class NumberWriter
{
    public void WriteValue(long value)
    {
    }

    public void WriteValue(ulong value)
    {
    }

    public void Run(ulong payload)
    {
        WriteValue(payload);
    }
}",
            FileName = "NumberWriter.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success,
            "Conversion failed: " + string.Join("; ", result.Diagnostics.Select(d => $"[{d.Severity}] {d.Message}")));

        var code = result.GeneratedCode;

        // The call with a ulong static argument binds to the ulong overload — the
        // suffixed one — not to the signed overload with a narrowing cast.
        Assert.Contains("writeValue_erasure_2(payload)", code);
        Assert.DoesNotContain("writeValue((long) payload)", code);
    }

    [Fact]
    public void Utf8FormatterTryFormat_UlongValue_RoutesToUnsignedCompatHelper()
    {        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
namespace System.Buffers.Text
{
    public struct StandardFormat
    {
    }

    public static class Utf8Formatter
    {
        public static bool TryFormat(long value, Span<int> destination, out int bytesWritten)
        {
            bytesWritten = 0;
            return true;
        }

        public static bool TryFormat(ulong value, Span<int> destination, out int bytesWritten)
        {
            bytesWritten = 0;
            return true;
        }
    }
}

class FormatterUser
{
    public void Run(ulong payload, Span<int> destination)
    {
        System.Buffers.Text.Utf8Formatter.TryFormat(payload, destination, out int written);
    }
}",
            FileName = "FormatterUser.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success,
            "Conversion failed: " + string.Join("; ", result.Diagnostics.Select(d => $"[{d.Severity}] {d.Message}")));

        var code = result.GeneratedCode;

        // The compat Utf8Formatter is hand-written; bare tryFormat(long) formats
        // SIGNED. The ulong call must route to the explicit unsigned helper so
        // 18446744073709551615 formats as itself, not as -1.
        Assert.Contains("tryFormatUInt64(", code);
    }

    [Fact]
    public void ByteUshortUintOverloads_AllSurviveUnderDistinctErasureSuffixes()
    {
        // C# XmlConvert.ToString declares 8 integral overloads. Java widths:
        // sbyte→byte, short→short, int/uint/byte/ushort→int, long/ulong→long — so
        // Byte/UInt16/UInt32 all collide with Int32 and must receive _erasure_2/3/4
        // by source order. Normalizing by C#-name width (Byte→SByte, UInt16→Int16)
        // instead silently drops the Byte/UInt16 declarations and routes ushort/uint
        // callers onto the byte-width overload (4582 formatted as "230").
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
class NumberWriter
{
    public string Write(sbyte value) { return ""sbyte"" + value; }
    public string Write(short value) { return ""short"" + value; }
    public string Write(int value) { return ""int"" + value; }
    public string Write(long value) { return ""long"" + value; }
    public string Write(byte value) { return ""byte"" + value; }
    public string Write(ushort value) { return ""ushort"" + value; }
    public string Write(uint value) { return ""uint"" + value; }
    public string Write(ulong value) { return ""ulong"" + value; }

    public string CallSbyte(sbyte v) { return Write(v); }
    public string CallByte(byte v) { return Write(v); }
    public string CallUshort(ushort v) { return Write(v); }
    public string CallUint(uint v) { return Write(v); }
}",
            FileName = "NumberWriter.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success,
            "Conversion failed: " + string.Join("; ", result.Diagnostics.Select(d => $"[{d.Severity}] {d.Message}")));

        var code = result.GeneratedCode;

        // The int-width conflict group {int, byte, ushort, uint} in source order:
        // int keeps the plain name, byte→_erasure_2, ushort→_erasure_3, uint→_erasure_4.
        Assert.Contains("write(int value)", code);
        Assert.Matches(@"write_erasure_2\(int value\)", code);
        Assert.Matches(@"write_erasure_3\(int value\)", code);
        Assert.Matches(@"write_erasure_4\(int value\)", code);
        // long/ulong form their own group: long plain, ulong _erasure_2.
        Assert.Contains("write(long value)", code);
        Assert.Matches(@"write_erasure_2\(long value\)", code);

        // No silent drops — every overload's body marker survives exactly once.
        foreach (var marker in new[] { "sbyte", "short", "int", "long", "byte", "ushort", "uint", "ulong" })
        {
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(code, "\"" + marker + "\"").Count);
        }

        // Call sites bind by static argument width.
        Assert.Contains("write(v)", code);                        // sbyte → plain (Java byte width)
        Assert.Matches(@"write_erasure_2\(v & 0xFF\)", code);      // byte → masked, _erasure_2
        Assert.Matches(@"write_erasure_3\(v\)", code);             // ushort → _erasure_3
        Assert.Matches(@"write_erasure_4\(v\)", code);             // uint → _erasure_4
    }
}
