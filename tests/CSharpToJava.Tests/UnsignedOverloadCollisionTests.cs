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
}
