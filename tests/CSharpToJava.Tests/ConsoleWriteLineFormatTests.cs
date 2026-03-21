using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ConsoleWriteLineFormatTests
{
    [Fact]
    public void ConsoleWriteLine_FormatOverload_UsesStringFormatInsidePrintln()
    {
        const string code = """
            using System;

            public class C {
                public void M(string s, int n) {
                    Console.WriteLine("Value {0} / {1}", s, n);
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("System.out.println(String.format(\"Value %s / %s\", s, n))", result.GeneratedCode);
    }
}
