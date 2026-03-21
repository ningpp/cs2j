using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class TupleCreateArity4MappingTests
{
    [Fact]
    public void SystemTupleCreate_FourArgs_MapsToTuple4Of()
    {
        const string code = """
            using System;
            using System.Collections.Generic;

            public class C {
                public Tuple<int,int,double,double> M(int a, int b, double c, double d) {
                    return Tuple.Create(a, b, c, d);
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(di => di.Message)));
        Assert.Contains("Tuple.of(a, b, c, d)", result.GeneratedCode);
        Assert.DoesNotContain("Tuple.create(a, b, c, d)", result.GeneratedCode);
    }
}
