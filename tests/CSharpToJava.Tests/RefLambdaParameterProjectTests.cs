using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class RefLambdaParameterProjectTests
{
    [Fact]
    public async Task RefLambdaParameter_ProjectPath_RenamesShadowedParameter()
    {
        var source = """
using System.Text;
delegate bool MatchCallback<TState>(ref TState state, Match match);
class Match { public int Index { get; set; } public int Length { get; set; } }
class Regex2 {
    void Run<TState>(string input, int startat, ref TState state, MatchCallback<TState> callback, bool reuseMatchObject) { }
    static string Replace(string input, int count, int startat) {
        var state = (segments: new StringBuilder(), prevat: 0, input, count);
        if (startat >= 0) {
            Regex2 regex = new Regex2();
            regex.Run(input, startat, ref state, (ref (StringBuilder segments, int prevat, string input, int count) state, Match match) => {
                state.prevat = match.Index + match.Length;
                return --state.count != 0;
            }, false);
        }
        return state.segments.ToString();
    }
}
""";
        var options = new ConversionOptions();
        var pipeline = new ProjectConversionPipeline(options);
        var sourceFiles = new List<SourceFile>
        {
            new SourceFile { FilePath = "RefLambdaSample.cs", Content = source }
        };
        var results = await pipeline.ConvertProjectAsync(sourceFiles,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { System.IO.Path.GetFullPath("RefLambdaSample.cs") });

        var result = results.FirstOrDefault(r => r.FileName?.EndsWith("Regex2.java", StringComparison.OrdinalIgnoreCase) == true)
            ?? results.Last();
        Assert.NotNull(result);
        var allInfo = string.Join("\n---\n", results.Select(r =>
            $"FILE={r.FileName} SUCCESS={r.Success}\n{r.GeneratedCode}"));
        System.IO.File.WriteAllText(
            @"C:\Users\15097\AppData\Local\Temp\opencode\projtest-gen.txt",
            allInfo);
        Assert.True(result.Success, $"{string.Join("\n", result.Diagnostics)}\nCODE:\n{result.GeneratedCode}");
        var code = result.GeneratedCode ?? "";

        Assert.Contains("io.github.ningpp.compat.ObjectHolder<Tuple4<StringBuilder, Integer, String, Integer>>", code);
        Assert.DoesNotContain("Tuple4<StringBuilder, Integer, String, Integer> state, Match match", code);
    }
}
