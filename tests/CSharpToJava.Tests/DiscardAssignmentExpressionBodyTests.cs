using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// C# 表达式体方法为丢弃赋值（<c>object M(IEnumerator e) =&gt; _ = e.Current;</c>）：
/// 赋值表达式的值就是右值，且 <c>_</c> 不是真实存储（Java 也不允许 `_` 标识符）。
/// 回读左值会产出未声明的 `_Value`，回归真实链 aglningpptests VertexEnumeratorsTest。
/// </summary>
public class DiscardAssignmentExpressionBodyTests
{
    [Fact]
    public void DiscardAssignmentExpressionBody_ReturnsRightValue()
    {
        var result = Convert(@"
using System.Collections;

class Holder
{
    public static object Lambda(IEnumerator e) => _ = e.Current;
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";
        Assert.Contains("return", code);
        Assert.True(
            code.Contains(".getCurrentNonGeneric()") || code.Contains(".getCurrent()"),
            "---Generated---\n" + code);
        Assert.DoesNotContain("_Value", code);
    }

    [Fact]
    public void PropertyAssignmentExpressionBody_StillReturnsReadBackValue()
    {
        var result = Convert(@"
class C
{
    public int P { get; set; }
}

class Holder
{
    public static object M(C c) => c.P = 5;
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";
        Assert.Contains("c.setP(5);", code);
        Assert.Contains("return c.getP();", code);
        Assert.DoesNotContain("return c.setP(5)", code);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
