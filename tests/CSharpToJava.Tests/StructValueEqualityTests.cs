using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// C# struct 的默认相等是 ValueType.Equals（逐字段值比较）。移植成 Java class 后
/// equals 若退化为引用相等，struct 作为字典键（CSharpDictionary&lt;TKey, …&gt;）时
/// 查找恒 miss（真实链 xml-tiny 命名空间轴：XPathNodeRef 作键）。转换器为
/// 「覆写 GetHashCode、但未覆写 Equals」的 struct 补逐字段 value equals。
/// </summary>
public class StructValueEqualityTests
{
    [Fact]
    public void Struct_OverridesGetHashCodeOnly_EmitsFieldWiseEquals()
    {
        var result = Convert("""
public struct NodeRef
{
    private object[] _page;
    private int _idx;

    public NodeRef(object[] page, int idx) { _page = page; _idx = idx; }

    public override int GetHashCode() { return _idx; }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;
        Assert.Contains("public boolean equals(Object obj)", code);
        Assert.Contains("getClass() != obj.getClass()", code);
        // Reference field compares through C# object.Equals semantics; int field by ==.
        Assert.Contains("ObjectHelper.objectEquals(this._page, other._page)", code);
        Assert.Contains("this._idx == other._idx", code);
        Assert.Contains("import io.github.ningpp.compat.ObjectHelper;", code);
    }

    [Fact]
    public void Struct_WithExplicitEquals_KeepsSourceEqualsOnly()
    {
        var result = Convert("""
public struct NodeRef
{
    private int _idx;

    public NodeRef(int idx) { _idx = idx; }

    public override int GetHashCode() { return _idx; }
    public override bool Equals(object obj) { return obj is NodeRef other && other._idx == _idx; }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;
        Assert.Equal(1, code.Split("public boolean equals(Object obj)").Length - 1);
    }

    [Fact]
    public void Struct_WithoutGetHashCode_DoesNotGainEquals()
    {
        var result = Convert("""
public struct Point
{
    public int X;
    public int Y;
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain("public boolean equals(Object obj)", result.GeneratedCode);
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
