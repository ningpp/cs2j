using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class RefLambdaParameterTests
{
    private static ConversionResult Convert(string source)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = source,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }

    [Fact]
    public void RefLambdaParameter_IsWrappedInObjectHolder()
    {
        var result = Convert("""
delegate void D(ref (int a, int b) state);
class C {
    void Test() {
        var state = (a: 1, b: 2);
        D d = (ref (int a, int b) state) => { state.a = 5; };
        d(ref state);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // The lambda parameter is a ref parameter → its Java type must be ObjectHolder<T>,
        // matching the delegate's apply signature.
        Assert.Contains("(io.github.ningpp.compat.ObjectHolder<Tuple2<Integer, Integer>>", code);
        // The parameter must not shadow the enclosing local `state` (Java forbids lambda
        // parameters with the same name as a local variable in scope).
        Assert.DoesNotContain("Tuple2<Integer, Integer> state) ->", code);
        Assert.Contains("state = _stateRef.value;", code);
    }
}
