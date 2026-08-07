using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that an enum with a member literally named "value" does not collide with
/// the synthetic value field emitted by EnumTransformer.
/// Reproduces ExceptionArgument: javac "variable value is already defined in enum".
/// </summary>
public class EnumValueMemberCollisionTests
{
    [Fact]
    public void EnumWithMemberNamedValue_SyntheticFieldRenamed()
    {
        var result = Convert(@"
internal enum ExceptionArgument
{
    array,
    count,
    value,
}

class User
{
    int Cast(ExceptionArgument a) => (int)a;
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // The enum constant keeps its name...
        Assert.Contains("value(", code);
        // ...and the synthetic backing field must not collide with it.
        Assert.DoesNotContain("private final int value;", code);
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
