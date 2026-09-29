using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that overrides of generic base methods use boxed types in signature
/// positions where the base renders its type parameter (e.g. JsonConverter&lt;bool&gt;
/// declares abstract T Read() / Write(T) which javac substitutes to Boolean).
/// An override declaring boolean read() / write(boolean) does not match and
/// javac reports "does not override abstract method".
/// </summary>
public class GenericOverrideBoxingTests
{

    [Fact]
    public void OverrideOfNonGenericBase_KeepsPrimitiveType()
    {
        var result = Convert(@"
class Base
{
    public virtual bool Read() => false;
}
class Derived : Base
{
    public override bool Read() => true;
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("boolean read()", code);
        Assert.DoesNotContain("Boolean read()", code);
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
