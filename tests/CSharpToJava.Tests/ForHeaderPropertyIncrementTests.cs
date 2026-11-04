using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that a property increment used as a for-statement incrementor is emitted
/// as a self-contained setter call instead of a temp variable with pre-statements
/// (those would be hoisted out of the loop, leaving a bare identifier as the update).
/// </summary>
public class ForHeaderPropertyIncrementTests
{
    private readonly ITestOutputHelper _out;

    public ForHeaderPropertyIncrementTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task ForIncrementorOnProperty_EmitsSelfContainedSetterCall()
    {
        var result = await ConvertAsync(new[]
        {
            new SourceFile { FilePath = "Test.cs", Content = """
                namespace Demo
                {
                    public class Holder { public int Value { get; set; } }
                    public class Test
                    {
                        public int Count(int[] data)
                        {
                            var h = new Holder();
                            int counter = 0;
                            for (; h.Value < data.Length; h.Value++)
                            {
                                counter++;
                            }
                            return counter;
                        }
                    }
                }
                """ },
        });

        var test = Assert.Single(result, r => r.FileName == "Test.java");
        Assert.True(test.Success, string.Join("\n", test.Diagnostics));
        _out.WriteLine(test.GeneratedCode ?? "FAILED");
        Assert.Contains("h.setValue(h.getValue() + 1)", test.GeneratedCode);
        Assert.DoesNotContain("var _inc", test.GeneratedCode);
        Assert.Contains("for (; h.getValue() < data.length; h.setValue(h.getValue() + 1))", test.GeneratedCode);
    }

    private static async Task<List<ConversionResult>> ConvertAsync(SourceFile[] files)
    {
        var pipeline = new ProjectConversionPipeline(new ConversionOptions());
        return await pipeline.ConvertProjectAsync(files, projectName: "Demo");
    }
}
