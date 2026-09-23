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
