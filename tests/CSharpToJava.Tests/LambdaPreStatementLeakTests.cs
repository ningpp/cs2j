using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class LambdaPreStatementLeakTests
{
    [Fact]
    public void ObjectInitializerPreservesMapDeclarationOutsideNestedLambda()
    {
        const string code = """
            using System;
            using System.Collections.Generic;

            public class C {
                public void M() {
                    var invokers = new Dictionary<bool, Action<Action[]>> {
                        { false, actions => Array.ForEach(actions, a => a()) }
                    };
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain("actions -> Arrays.stream(actions).forEach(a -> {\n        var _map", result.GeneratedCode);
    }
}
