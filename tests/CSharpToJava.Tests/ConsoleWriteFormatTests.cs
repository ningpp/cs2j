using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ConsoleWriteFormatTests
{
    [Fact]
    public void ConsoleWrite_FormatOverload_UsesStringFormatInsidePrint()
    {
        const string code = """
            using System;

            public class C
            {
                void M(string s, int n)
                {
                    Console.Write("Value {0} / {1}", s, n);
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("System.out.print(String.format(\"Value %s / %s\", s, n))", result.GeneratedCode);
        Assert.DoesNotContain("System.out.print(\"Value {0} / {1}\", s, n)", result.GeneratedCode);
    }
}