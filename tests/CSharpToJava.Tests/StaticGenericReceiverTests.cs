using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that static member access on a generic type uses the raw Java type as
/// the receiver. Java forbids accessing static members through a parameterized
/// type (ReadOnlySpan&lt;int&gt;.getEmpty() does not compile).
/// </summary>
public class StaticGenericReceiverTests
{
    private readonly ITestOutputHelper _out;

    public StaticGenericReceiverTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task ReadOnlySpanEmpty_UsesRawReceiver()
    {
        var result = await ConvertAsync(new[]
        {
            new SourceFile { FilePath = "Test.cs", Content = """
                using System;

                namespace Demo
                {
                    public class Test
                    {
                        public int GetLength()
                        {
                            ReadOnlySpan<byte> span = ReadOnlySpan<byte>.Empty;
                            return span.Length;
                        }
                    }
                }
                """ },
        });

        var test = Assert.Single(result, r => r.FileName == "Test.java");
        Assert.True(test.Success, string.Join("\n", test.Diagnostics));
        _out.WriteLine(test.GeneratedCode ?? "FAILED");
        Assert.Contains("ReadOnlySpan.getEmpty()", test.GeneratedCode);
        Assert.DoesNotContain(">.getEmpty()", test.GeneratedCode);
    }

    [Fact]
    public async Task ReadOnlyMemoryEmpty_UsesRawReceiver()
    {
        var result = await ConvertAsync(new[]
        {
            new SourceFile { FilePath = "Test.cs", Content = """
                using System;

                namespace Demo
                {
                    public class Test
                    {
                        public int GetLength()
                        {
                            ReadOnlyMemory<byte> memory = ReadOnlyMemory<byte>.Empty;
                            return memory.Length;
                        }
                    }
                }
                """ },
        });

        var test = Assert.Single(result, r => r.FileName == "Test.java");
        Assert.True(test.Success, string.Join("\n", test.Diagnostics));
        _out.WriteLine(test.GeneratedCode ?? "FAILED");
        Assert.Contains("ReadOnlyMemory.getEmpty()", test.GeneratedCode);
        Assert.DoesNotContain(">.getEmpty()", test.GeneratedCode);
    }

    private static async Task<List<ConversionResult>> ConvertAsync(SourceFile[] files)
    {
        var pipeline = new ProjectConversionPipeline(new ConversionOptions());
        return await pipeline.ConvertProjectAsync(files, projectName: "Demo");
    }
}
