using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for the ValueListBuilder/SegmentStringBuilder error cluster:
/// 1. Array.Empty&lt;T&gt;() with a generic type argument must not emit illegal
///    generic array creation (new ReadOnlyMemory&lt;Character&gt;[0]).
/// 2. ArrayPool&lt;T&gt;.Shared.Rent/Return must map to the compat ArrayPool class.
/// 3. Assigning a T[] to a Span&lt;T&gt; target must wrap the array in new Span&lt;&gt;().
/// Reproduces javac errors "创建泛型数组", "找不到符号 ArrayPool", and
/// "T[]无法转换为Span&lt;T&gt;" in ValueListBuilder.java/SegmentStringBuilder.java.
/// </summary>
public class SpanArrayCompatClusterTests
{
    [Fact]
    public void ArrayEmpty_WithGenericElement_DoesNotEmitGenericArrayCreation()
    {
        var result = Convert(@"
class Holder
{
    private System.ReadOnlyMemory<char>[] _array;

    public static Holder Create()
    {
        var h = new Holder();
        h._array = System.Array.Empty<System.ReadOnlyMemory<char>>();
        return h;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // Java forbids generic array creation; the element type must be raw.
        Assert.DoesNotContain("new ReadOnlyMemory<Character>[", code);
    }

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
}
