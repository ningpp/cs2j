using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ArrayCopyStaticOverloadTests
{
    [Fact]
    public void ArrayCopy_ThreeArgumentOverload_UsesSystemArraycopy()
    {
        const string code = """
            using System;

            public class C {
                public void M(int[] src, int[] dst, int n) {
                    Array.Copy(src, dst, n);
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("System.arraycopy(src, 0, dst, 0, n)", result.GeneratedCode);
        Assert.DoesNotContain("Object.copy", result.GeneratedCode);
    }
}
