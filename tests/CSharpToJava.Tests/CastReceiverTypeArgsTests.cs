using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that member access on a CAST receiver keeps the cast's type arguments.
/// The generic receiver-strip (Type&lt;T&gt;.member() → Type.member()) only applies
/// to static type qualifiers; stripping instance cast receivers produced
/// ((Info)(x)).getValue() returning Object, failing assignments to TArg0.
/// Reproduces SmallObjectWithParameterizedConstructorConverter
/// (JsonParameterInfo&lt;TArg0&gt; cast + TypedDefaultValue property).
/// </summary>
public class CastReceiverTypeArgsTests
{
    [Fact]
    public void PropertyAccessOnCastReceiver_KeepsTypeArguments()
    {
        var result = Convert(@"
class Info<T>
{
    public T Value { get; set; }
}
class Arguments<TArg0> { public TArg0 Arg0; }
class Holder<TArg0>
{
    void M(object parameterInfo, Arguments<TArg0> arguments)
    {
        arguments.Arg0 = ((Info<TArg0>)parameterInfo).Value;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("((Info<TArg0>)(parameterInfo)).getValue()", code);
        Assert.DoesNotContain("((Info)(parameterInfo)).getValue()", code);
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
