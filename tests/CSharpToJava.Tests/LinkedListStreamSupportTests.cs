using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class LinkedListStreamSupportTests
{
    [Fact]
    public void LinkedListWhere_UsesStreamSupport_NotDirectStreamCall()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;

            public class C {
                public IEnumerable<int> F(LinkedList<int> locks) {
                    return locks.Where(l => l > 0);
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain("locks.stream().filter", result.GeneratedCode);
    }
}
