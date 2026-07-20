using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for the <c>GetResult()</c> → <c>join()</c> rewrite.
/// Only <c>Task.GetAwaiter().GetResult()</c> / <c>TaskAwaiter.GetResult()</c>
/// should become <c>.join()</c>; other <c>GetResult()</c> methods must keep
/// their normal camelCase name.
/// </summary>
public class GetResultConversionTests
{
    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }

    [Fact]
    public void StringConcat_GetResult_IsNotMappedToJoin()
    {
        var result = Convert(@"
namespace dotnet.xml
{
    public struct StringConcat
    {
        public string GetResult() { return string.Empty; }
    }

    public class C
    {
        public void M(StringConcat sc)
        {
            string s = sc.GetResult();
        }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains(".getResult()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".join()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskAwaiter_GetResult_IsMappedToJoin()
    {
        var result = Convert(@"
using System.Threading.Tasks;

class C
{
    void M()
    {
        Task.Delay(1).GetAwaiter().GetResult();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains(".join()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".getResult()", result.GeneratedCode, StringComparison.Ordinal);
    }
}
