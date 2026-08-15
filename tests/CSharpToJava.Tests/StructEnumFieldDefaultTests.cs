using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// C# struct enum fields default to the zero-valued member, never null. Java
/// enum references default to null, so the generated struct default constructor
/// must initialize them (e.g. WriteStackFrame.propertyState).
/// </summary>
public class StructEnumFieldDefaultTests
{
    [Fact]
    public void StructDefaultConstructor_InitializesEnumFieldsToZeroMember()
    {
        var result = Convert(@"
enum E { A, B }

readonly struct S
{
    public E E { get; }
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("this.e = E.values()[0]", code, StringComparison.Ordinal);
        Assert.DoesNotContain("this.e = null", code, StringComparison.Ordinal);
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
