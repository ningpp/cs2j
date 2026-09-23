using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class MutableTupleAssignmentTests
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
    public void TupleElementAssignment_UsesUpdateMethod()
    {
        var result = Convert("""
class C {
    string Test() {
        var state = (segments: new System.Text.StringBuilder(), prevat: 0, count: 2);
        state.prevat = 5;
        state.count = state.count - 1;
        return state.segments.ToString();
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // vavr TupleN is immutable — element assignment must rebuild via updateN().
        Assert.DoesNotContain("state._2() = 5", code);
        Assert.DoesNotContain("_3() = state._3() - 1", code);
        Assert.Contains("state = state.update2(5)", code);
        Assert.Contains("state = state.update3(state._3() - 1)", code);
    }

    [Fact]
    public void PreDecrementOnTupleElement_UsesUpdateMethod()
    {
        var result = Convert("""
class C {
    bool Test(int n) {
        var state = (segments: new System.Text.StringBuilder(), count: n);
        return --state.count != 0;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.DoesNotContain("--state._2()", code);
        Assert.Contains("update2", code);
    }
}
