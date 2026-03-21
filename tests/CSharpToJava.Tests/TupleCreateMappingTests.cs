using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class TupleCreateMappingTests
{
    [Fact]
    public void SystemTupleCreate_TwoArgs_MapsToSimpleEntryConstructor()
    {
        const string code = """
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public class C {
                public IEnumerable<object> Build(IEnumerable<int> nums) {
                    return nums.Select((p, index) => Tuple.Create(p, (object)index));
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("new AbstractMap.SimpleEntry<>(p, (Object)(index))", result.GeneratedCode);
        Assert.DoesNotContain("Tuple.create(", result.GeneratedCode);
    }
}
