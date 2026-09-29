using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that ref/out ObjectHolder wrapping is consistent between an abstract
/// base declaration and its overrides. Abstract methods have no body, so the
/// read-only-ref analysis is conservative (holder-wrapped); an override whose
/// body never reassigns the param would render the plain type instead, making
/// javac report "does not override abstract method createCollection(...)".
/// Reproduces ArrayConverter/ListOfTConverter/QueueOfTConverter/... errors.
/// </summary>
public class RefHolderOverrideConsistencyTests
{
    [Fact]
    public void OverrideRefParams_MatchAbstractBaseHolderSignature()
    {
        var result = Convert(@"
struct Reader { }
struct Stack { }
abstract class Base
{
    protected abstract void Create(ref Reader reader, ref Stack state);
}
class Derived : Base
{
    protected override void Create(ref Reader reader, ref Stack state)
    {
        state.Value = 5;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("abstract void create(io.github.ningpp.compat.ObjectHolder<Reader> reader, io.github.ningpp.compat.ObjectHolder<Stack> state)", code);
        Assert.Contains("protected void create(io.github.ningpp.compat.ObjectHolder<Reader> reader, io.github.ningpp.compat.ObjectHolder<Stack> state)", code);
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
