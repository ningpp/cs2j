using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that implicit delegate invocations whose callee is a null-forgiving
/// expression (<c>Get!(obj)</c>) lower to the Java SAM call instead of
/// <c>getGet()(obj)</c> (which is not valid Java).
/// </summary>
public class NullForgivingDelegateInvocationTests
{
    private readonly ITestOutputHelper _out;

    public NullForgivingDelegateInvocationTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task NullForgivingDelegateInvocation_LoweredToSamCall()
    {
        // Mirrors json/src/System/Text/Json/Serialization/JsonPropertyInfoOfT.cs:
        //   public Func<object, T>? Get { get; private set; }
        //   public Action<object, T>? Set { get; private set; }
        //   return Get!(obj);
        //   Set!(obj, value);
        var result = await ConvertAsync(new[]
        {
            new SourceFile { FilePath = "Test.cs", Content = """
                using System;

                namespace Demo
                {
                    public class Test
                    {
                        private Func<object, string>? Get { get; set; }
                        private Action<object, string>? Set { get; set; }
                        public string Read(object obj) { return Get!(obj); }
                        public void Write(object obj, string value) { Set!(obj, value); }
                    }
                }
                """ },
        });

        var test = Assert.Single(result, r => r.FileName == "Test.java");
        Assert.True(test.Success, string.Join("\n", test.Diagnostics));
        var code = test.GeneratedCode ?? "FAILED";
        _out.WriteLine(code);

        Assert.Contains("getGet().apply(obj)", code);
        Assert.Contains("getSet().accept(obj, value)", code);
        Assert.DoesNotContain("getGet()(obj)", code);
        Assert.DoesNotContain("getSet()(obj", code);
    }

    private static async Task<List<ConversionResult>> ConvertAsync(SourceFile[] files)
    {
        var pipeline = new ProjectConversionPipeline(new ConversionOptions());
        return await pipeline.ConvertProjectAsync(files, projectName: "Demo");
    }
}
