using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class EnumTypedFieldNameShadowTests
{
    [Fact]
    public void EnumTypedField_WithoutInitializer_QualifiesEnumTypeName()
    {
        var result = Convert("""
            namespace Demo
            {
                public enum Direction
                {
                    None,
                    East,
                    North
                }

                public class Sample
                {
                    public Direction Direction;
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.DoesNotContain("Direction Direction = Direction.values()[0]", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Direction Direction = Demo.Direction.values()[0]", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstEnumTypedField_Initializer_QualifiesEnumTypeName()
    {
        var result = Convert("""
            namespace Demo
            {
                public enum Direction
                {
                    None,
                    East,
                    North
                }

                public class Sample
                {
                    public const Direction Direction = Direction.North;
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("Demo.Direction.values()[2]", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("= Direction.values()[", result.GeneratedCode, StringComparison.Ordinal);
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
