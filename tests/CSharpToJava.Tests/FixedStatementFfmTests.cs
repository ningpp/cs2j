using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class FixedStatementFfmTests
{

    [Fact]
    public void FixedBufferField_Byte()
    {
        var result = Convert(@"
unsafe class Test {
    fixed byte buffer[256];
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment buffer", result.GeneratedCode);
        Assert.Contains("Arena.ofAuto().allocate(256, ValueLayout.JAVA_BYTE)", result.GeneratedCode);
    }

    [Fact]
    public void FixedBufferField_Char()
    {
        var result = Convert(@"
unsafe class Test {
    fixed char buffer[128];
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment buffer", result.GeneratedCode);
        Assert.Contains("Arena.ofAuto().allocate(256, ValueLayout.JAVA_CHAR)", result.GeneratedCode);
    }

    [Fact]
    public void FixedBufferField_Int()
    {
        var result = Convert(@"
unsafe class Test {
    fixed int buffer[64];
}");
        Assert.True(result.Success);
        Assert.Contains("MemorySegment buffer", result.GeneratedCode);
        Assert.Contains("Arena.ofAuto().allocate(256, ValueLayout.JAVA_INT)", result.GeneratedCode);
    }

    [Fact]
    public void FixedBufferField_Imports()
    {
        var result = Convert(@"
unsafe class Test {
    fixed byte buffer[256];
}");
        Assert.True(result.Success);
        Assert.Contains("import java.lang.foreign.Arena", result.GeneratedCode);
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
