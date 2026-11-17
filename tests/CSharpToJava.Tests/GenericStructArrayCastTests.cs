using System;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C# 结构体数组元素默认已初始化，转换后走 ArrayHelper.filledValueArray compat
/// 工厂。工厂返回原始数组，若元素类型带泛型实参而表达式不带强转，`var` 声明点会
/// 推断成 raw 数组，元素成员退化为 Object（aglningpptests BalloonLayoutTests:
/// TupleT0T1.Item2 为 Object，赋给 Node 不兼容）。默认填充分支必须补回
/// `(Pair<Integer, String>[])` 强转。
/// </summary>
public class GenericStructArrayCastTests
{
    [Fact]
    public void GenericStructArray_ValueFill_CarriesTypeArgumentCast()
    {
        // C#: var a = new Pair<int, string>[3]; string s = a[0].Second;
        var result = Convert(@"
struct Pair<T0, T1> { public T0 First; public T1 Second; }
class Sample {
    void M() {
        var a = new Pair<int, string>[3];
        string s = a[0].Second;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        var code = result.GeneratedCode;
        Assert.Contains(
            "ArrayHelper.filledValueArray(Pair.class, 3)",
            System.Text.RegularExpressions.Regex.Replace(code, @"\s+", " "),
            StringComparison.Ordinal);
        Assert.Matches(
            @"\(Pair<Integer,\s*String>\[\]\)\s*ArrayHelper\.filledValueArray\(Pair\.class, 3\)",
            code);
        // 未加强转的 raw 形式不能作为数组声明初始化出现（`var a = ...`）。
        Assert.DoesNotContain("= ArrayHelper.filledValueArray(Pair.class, 3)", code, StringComparison.Ordinal);
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
