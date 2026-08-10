using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that forwarding a ref parameter into a call whose target slot is
/// holder-wrapped (abstract generic base) passes the holder itself, not the
/// unwrapped .value — otherwise the call passes Utf8JsonReader where
/// ObjectHolder&lt;Utf8JsonReader&gt; is expected. Reproduces
/// CharConverter.ReadWithQuotes "Utf8JsonReader cannot be converted to
/// ObjectHolder&lt;Utf8JsonReader&gt;".
/// </summary>
public class RefHolderForwardingToAbstractSlotTests
{
    [Fact]
    public void RefParamForwardedToAbstractOverrideSlot_PassesHolder()
    {
        var result = Convert(@"
struct Reader { }
abstract class Base<T>
{
    public abstract T Read(ref Reader reader);
    public abstract T ReadWithQuotes(ref Reader reader);
}
class Derived : Base<char>
{
    public override char Read(ref Reader reader) => 'a';
    public override char ReadWithQuotes(ref Reader reader) => Read(ref reader);
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("read(reader)", code);
        Assert.DoesNotContain("read(reader.value)", code);
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
