using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ParallelInvokeMethodGroupTests
{
    [Fact]
    public void ParallelInvoke_MethodGroup_AsDelegateValue_MapsToRunnableArrayLambda()
    {
        const string code = """
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;

            public class C {
                public void M() {
                    var invokers = new Dictionary<bool, Action<Action[]>> {
                        { true, Parallel.Invoke }
                    };
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("actions -> Arrays.stream(actions).forEach(Runnable::run)", result.GeneratedCode);
        Assert.DoesNotContain("Parallel.Invoke", result.GeneratedCode);
    }
}
