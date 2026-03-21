using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// 测试数组赋值给 IList<T> 类型时的转换
///
/// 在 C# 中，数组实现了 IList<T>，所以可以将数组直接赋值给 IList<T> 类型的变量或属性。
/// 但在 Java 中，数组不是 List<T> 的子类型，因此需要使用 Arrays.asList() 进行包装。
///
/// Bug 修复：
///   LayerEdges = new LayerEdge[span];  // C# 中有效
///   错误: LayerEdge[] 无法转换为 java.util.List<LayerEdge>
///   修正: setLayerEdges(Arrays.asList(new LayerEdge[span]));
/// </summary>
public class ArrayToListAssignmentTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    private static string ConvertAndGetCode(string code, ConversionOptions? options = null)
    {
        var result = Convert(code);
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        return result.GeneratedCode;
    }

    // ── 属性赋值：数组赋值给 IList<T> 属性 ────────────────────────────────────────

    [Fact]
    public void ArrayAssignment_ToListProperty_WrapsWithArraysAsList()
    {
        const string code = """
            using System.Collections.Generic;

            public class LayerEdge
            {
                public int Source { get; set; }
                public int Target { get; set; }
            }

            public class Graph
            {
                public IList<LayerEdge> LayerEdges { get; set; }

                public void Init(int span)
                {
                    LayerEdges = new LayerEdge[span];
                }
            }
            """;

        var java = ConvertAndGetCode(code);

        // 应该使用 Arrays.asList() 包装数组
        Assert.Contains("Arrays.asList(new LayerEdge[span])", java);
        // 不应该直接使用未包装的数组
        Assert.DoesNotContain("= new LayerEdge[span];", java);
    }

    [Fact]
    public void ArrayLiteralAssignment_ToListProperty_WrapsWithArraysAsList()
    {
        const string code = """
            using System.Collections.Generic;

            public class Test
            {
                public IList<string> Items { get; set; }

                public void Init()
                {
                    Items = new string[] { "a", "b", "c" };
                }
            }
            """;

        var java = ConvertAndGetCode(code);

        // 应该使用 Arrays.asList() 包装
        Assert.Contains("Arrays.asList(new String[] { \"a\", \"b\", \"c\" })", java);
    }

    // ── 变量赋值：数组赋值给 IList<T> 变量 ────────────────────────────────────────

    [Fact]
    public void ArrayAssignment_ToListVariable_WrapsWithArraysAsList()
    {
        const string code = """
            using System.Collections.Generic;

            public class Test
            {
                public void Method()
                {
                    IList<string> list;
                    list = new string[] { "hello", "world" };
                }
            }
            """;

        var java = ConvertAndGetCode(code);

        // 应该使用 Arrays.asList() 包装
        Assert.Contains("list = Arrays.asList(new String[] { \"hello\", \"world\" });", java);
    }

    // ── 基本类型数组：需要装箱 ───────────────────────────────────────────────────

    [Fact]
    public void PrimitiveArrayAssignment_ToListProperty_BoxesWithStream()
    {
        const string code = """
            using System.Collections.Generic;

            public class Test
            {
                public IList<int> Numbers { get; set; }

                public void Init()
                {
                    Numbers = new int[] { 1, 2, 3 };
                }
            }
            """;

        var java = ConvertAndGetCode(code);

        // 基本类型数组需要使用 stream().boxed().collect() 进行装箱
        Assert.Contains("Arrays.stream(new int[] { 1, 2, 3 }).boxed()", java);
        Assert.Contains(".collect(java.util.stream.Collectors.toList())", java);
    }

    [Fact]
    public void PrimitiveArrayAssignment_ToListVariable_BoxesWithStream()
    {
        const string code = """
            using System.Collections.Generic;

            public class Test
            {
                public void Method()
                {
                    IList<int> nums;
                    nums = new int[] { 1, 2, 3 };
                }
            }
            """;

        var java = ConvertAndGetCode(code);

        // 基本类型数组需要使用 stream().boxed().collect() 进行装箱
        Assert.Contains("Arrays.stream(new int[] { 1, 2, 3 }).boxed()", java);
    }

    // ── 使用 this. 的属性赋值 ───────────────────────────────────────────────────────

    [Fact]
    public void ArrayAssignment_ToListPropertyViaThis_WrapsWithArraysAsList()
    {
        const string code = """
            using System.Collections.Generic;

            public class Test
            {
                public IList<string> Items { get; set; }

                public void Init()
                {
                    this.Items = new string[] { "x", "y", "z" };
                }
            }
            """;

        var java = ConvertAndGetCode(code);

        // 应该使用 Arrays.asList() 包装
        Assert.Contains("Arrays.asList(new String[] { \"x\", \"y\", \"z\" })", java);
    }

    // ── 集合接口类型（ICollection、IEnumerable）────────────────────────────────────────

    [Fact]
    public void ArrayAssignment_ToICollectionProperty_WrapsWithArraysAsList()
    {
        const string code = """
            using System.Collections.Generic;

            public class Test
            {
                public ICollection<string> Items { get; set; }

                public void Init()
                {
                    Items = new string[] { "a", "b" };
                }
            }
            """;

        var java = ConvertAndGetCode(code);

        // ICollection<T> 也应该使用 Arrays.asList() 包装
        Assert.Contains("Arrays.asList(new String[] { \"a\", \"b\" })", java);
    }

    [Fact]
    public void ArrayAssignment_ToIEnumerableProperty_WrapsWithArraysAsList()
    {
        const string code = """
            using System.Collections.Generic;

            public class Test
            {
                public IEnumerable<string> Items { get; set; }

                public void Init()
                {
                    Items = new string[] { "a", "b" };
                }
            }
            """;

        var java = ConvertAndGetCode(code);

        // IEnumerable<T> 应该使用 Arrays.asList() 包装（转换为 List）
        Assert.Contains("Arrays.asList(new String[] { \"a\", \"b\" })", java);
    }

    // ── 多维数组不应该被包装 ───────────────────────────────────────────────────────

    [Fact]
    public void MultiDimensionalArrayAssignment_NotWrapped()
    {
        const string code = """
            public class Test
            {
                public void Method()
                {
                    int[,] matrix = new int[2, 2];
                }
            }
            """;

        var java = ConvertAndGetCode(code);

        // 多维数组不应该使用 Arrays.asList() 包装
        Assert.Contains("int[][]", java);
        Assert.DoesNotContain("Arrays.asList(new int[", java);
    }
}
