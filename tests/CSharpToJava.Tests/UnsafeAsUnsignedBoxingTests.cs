using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for the EnumConverter-family fixes:
/// 1. Unsafe.As&lt;uint, T&gt;(ref x) boxing chain must include the unsigned
///    wrapper (UInt32 → Integer), otherwise (T)(Unsafe.as(...)) emits
///    "int cannot be converted to T".
/// 2. System.Enum.GetNames(Type) maps to EnumHelper.getNames (java.lang.Enum
///    has no such member).
/// </summary>
public class UnsafeAsUnsignedBoxingTests
{
    [Fact]
    public void UnsafeAsUInt32ToTypeParam_BoxesThroughInteger()
    {
        var result = Convert(@"
using System.Runtime.CompilerServices;
class Holder<T>
{
    public T M(ref uint value)
    {
        return Unsafe.As<uint, T>(ref value);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("(T)(Object)(Integer)(Unsafe.as(", code);
    }

    [Fact]
    public void UnsafeAsUInt16ToTypeParam_BoxesThroughShort()
    {
        var result = Convert(@"
using System.Runtime.CompilerServices;
class Holder<T>
{
    public T M(ref ushort value)
    {
        return Unsafe.As<ushort, T>(ref value);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("(T)(Object)(Short)(((short)(Unsafe.as(", code);
    }

    [Fact]
    public void UnsafeAsByteToTypeParam_NarrowsThroughPrimitiveByte()
    {
        var result = Convert(@"
using System.Runtime.CompilerServices;
class Holder<T>
{
    public T M(ref byte value)
    {
        return Unsafe.As<byte, T>(ref value);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // byte maps to int (IntHolder), so the box chain must narrow: (Byte)((byte)(intExpr)).
        Assert.Contains("(T)(Object)(Byte)(((byte)(Unsafe.as(", code);
    }

    [Fact]
    public void EnumGetNames_MapsToEnumHelper()
    {
        var result = Convert(@"
class Holder
{
    public string[] M()
    {
        return System.Enum.GetNames(typeof(System.DayOfWeek));
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("EnumHelper.getNames(", code);
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
