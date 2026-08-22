using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# arrays returned from methods with IReadOnlyList&lt;T&gt; return
/// types are properly wrapped with CSharpList in Java. C# arrays implement
/// IReadOnlyList&lt;T&gt;, but Java arrays do not implement CSharpReadOnlyList&lt;T&gt;.
/// </summary>
public class ArrayToReadOnlyListReturnTests
{
    private readonly ITestOutputHelper _out;

    public ArrayToReadOnlyListReturnTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task ArrayReturnedAsIReadOnlyList_WrappedWithCSharpList()
    {
        // Mirrors MultipoleCoefficientsTest.GetCoefficients:
        //   private static IReadOnlyList<(double Re, double Im)> GetCoefficients(...)
        //   {
        //       var result = new (double Re, double Im)[array.Length];
        //       ...
        //       return result;  // array → IReadOnlyList
        //   }
        var result = await ConvertAsync(new[]
        {
            new SourceFile { FilePath = "Test.cs", Content = """
                using System.Collections.Generic;

                namespace Demo
                {
                    public class Test
                    {
                        public IReadOnlyList<int> GetValues(int count)
                        {
                            var result = new int[count];
                            for (int i = 0; i < count; i++)
                                result[i] = i * 2;
                            return result;
                        }
                    }
                }
                """ },
        });

        var test = Assert.Single(result, r => r.FileName == "Test.java");
        Assert.True(test.Success, string.Join("\n", test.Diagnostics));
        _out.WriteLine(test.GeneratedCode ?? "FAILED");
        // The return statement must wrap the array with CSharpList.
        Assert.Contains("new CSharpList<Integer>(ArrayHelper.toList(result))", test.GeneratedCode);
    }

    [Fact]
    public async Task TupleArrayReturnedAsIReadOnlyList_WrappedWithCSharpList()
    {
        // Mirrors the exact MultipoleCoefficientsTest scenario:
        // IReadOnlyList<(double Re, double Im)> with tuple array return.
        var result = await ConvertAsync(new[]
        {
            new SourceFile { FilePath = "Test.cs", Content = """
                using System.Collections.Generic;

                namespace Demo
                {
                    public class Test
                    {
                        public IReadOnlyList<(double Re, double Im)> GetCoefficients(double[] array)
                        {
                            var result = new (double Re, double Im)[array.Length];
                            for (int i = 0; i < array.Length; i++)
                                result[i] = (array[i], array[i]);
                            return result;
                        }
                    }
                }
                """ },
        });

        var test = Assert.Single(result, r => r.FileName == "Test.java");
        Assert.True(test.Success, string.Join("\n", test.Diagnostics));
        _out.WriteLine(test.GeneratedCode ?? "FAILED");
        // The return statement must wrap the tuple array with CSharpList.
        Assert.Contains("new CSharpList<Tuple2<Double, Double>>(ArrayHelper.toList(result))", test.GeneratedCode);
    }

    private static async Task<List<ConversionResult>> ConvertAsync(SourceFile[] files)
    {
        var pipeline = new ProjectConversionPipeline(new ConversionOptions());
        return await pipeline.ConvertProjectAsync(files, projectName: "Demo");
    }
}
