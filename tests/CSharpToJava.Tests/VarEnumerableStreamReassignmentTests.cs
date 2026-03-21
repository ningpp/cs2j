using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class VarEnumerableStreamReassignmentTests
{
    [Fact]
    public void ImplicitVarIEnumerable_WithStreamChain_Reassignment_MaterializesInitializer()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;

            public class C {
                static IEnumerable<int> Build(IEnumerable<int> a, IEnumerable<int> b) {
                    var l = a.Select(x => x + 1);
                    l = l.Concat(b.Select(x => x + 2));
                    return l;
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));

        Assert.Contains("var l = StreamSupport.stream(a.spliterator(), false).map(x -> x + 1).collect(Collectors.toList());", result.GeneratedCode);
        Assert.Contains("l = java.util.stream.Stream.concat", result.GeneratedCode);
        Assert.Contains("return l;", result.GeneratedCode);
    }
}
