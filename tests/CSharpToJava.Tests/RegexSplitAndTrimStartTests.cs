using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class RegexSplitAndTrimStartTests
{
    [Fact]
    public void RegexSplit_And_StringTrimStart_MapToJavaApis()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Text.RegularExpressions;

            public class C {
                List<string> M(string line) {
                    line = line.TrimStart(' ');
                    return new List<string>(Regex.Split(line, "\\s{2,}"));
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("line.stripLeading()", result.GeneratedCode);
        Assert.Contains("Arrays.asList(line.split", result.GeneratedCode);
    }
}
