using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C# 枚举互转按底层**数值**语义。源枚举带显式值（value enum）时，Java 侧
/// ordinal() 是声明序而非 C# 值，用它索引目标 values() 会错位——真实链
/// XPathDocumentBuilder 的 (XPathNodeType)textType：Whitespace(C# 6, ordinal 3)
/// 被索引成 Namespace，文本节点类型全乱（xpath 两个模块各 593 例失败）。
/// </summary>
public class EnumToEnumValueCastTests
{
    [Fact]
    public void ValueEnumToOrdinalEnumCast_UsesValueNotOrdinal()
    {
        var result = Convert("""
public enum NodeKind { Root, Element, Attribute, Namespace, Text, SignificantWhitespace, Whitespace }

public class BlockType
{
    public const int None = 0;
    public const int Text = 4;
    public const int SignificantWhitespace = 5;
    public const int Whitespace = 6;
}

class Host
{
    public static NodeKind Convert(BlockType textType) => (NodeKind)textType;
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;
        // value-enum 源必须用数值，不能是声明序 ordinal()。
        Assert.DoesNotContain("values()[textType.ordinal()]", code);
    }

    [Fact]
    public void ValueEnumWithExplicitValues_CastUsesGetValue()
    {
        var result = Convert("""
public enum BlockType2 { None = 0, Text = 4, SignificantWhitespace = 5, Whitespace = 6 }
public enum NodeKind2 { Root, Element, Attribute, Namespace, Text, SignificantWhitespace, Whitespace }

class Host
{
    public static NodeKind2 Convert(BlockType2 textType) => (NodeKind2)textType;
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;
        Assert.Contains("NodeKind2.values()[textType.getValue()]", code);
        Assert.DoesNotContain("values()[textType.ordinal()]", code);
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
