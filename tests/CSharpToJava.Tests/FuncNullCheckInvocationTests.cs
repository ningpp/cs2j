using System.Diagnostics;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class FuncNullCheckInvocationTests
{
    [Fact]
    public void FuncBool_NullCheck_DumpOutput()
    {
        var result = Convert(@"
using System;

class TestClass
{
    public Func<bool> IsPassable;
    public bool CanPass()
    {
        return IsPassable == null || IsPassable();
    }
}");

        // The generated Java code must NOT contain a spurious (Boolean) cast
        // on the Supplier<Boolean> field. The original C# expression
        //   IsPassable == null || IsPassable()
        // should convert to
        //   IsPassable == null || IsPassable.get()
        // without any (Boolean) cast prefix.
        Assert.DoesNotContain("(Boolean)", result.GeneratedCode);
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
