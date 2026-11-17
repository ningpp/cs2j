using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// ToArray 的 Java 发射必须避免 target-typed 的多态 lambda。
/// 复现 aglningpptests LayeredTestSupport：第一个 params 重载把
/// Select(...).ToArray() 的结果转调第二个 params 重载。若发射
/// <c>toArray(size -> (Tuple...#lt;...&gt;[]) new Tuple...[size])</c>，
/// 这个多态实参让 javac 对两个 varargs 候选分别推断 <c>IntFunction&lt;A[]&gt;</c>
/// 的 A，得到互相冲突的上限，报“对 createGraph 的引用不明确”+
/// “推论变量 A 具有不兼容的上限”。改成原始组件类型的数组构造器方法
/// 引用后 A 在本地即确定，重载解析恢复正常。
/// </summary>
public class VarargsGenericArrayOverloadTests
{
    [Fact]
    public void GenericElementToArray_InVarargsOverloadForwarding_UsesRawComponentFactory()
    {
        var result = Convert("""
using System;
using System.Linq;

public class Demo
{
    public static object CreateGraph(int nodeCount, params Tuple<int, int>[] edges)
        => CreateGraph(nodeCount, edges.Select(Convert).ToArray());

    public static object CreateGraph(int nodeCount, params Tuple<int, int, int, int, bool, double>[] edges)
        => null;

    private static Tuple<int, int, int, int, bool, double> Convert(Tuple<int, int> e)
        => Tuple.Create(e.Item1, e.Item2, 1, 1, false, 1.0);
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        // 泛型元素不再走 `size -> (X<...>[]) new X[size]` 的多态 lambda。
        Assert.DoesNotContain("size -> (", code);
        Assert.DoesNotContain("toArray(size ->", code);
        // 改为原始组件类型的数组构造器方法引用：A 可本地确定，不参与重载推断。
        Assert.Contains(".toArray(Tuple6[]::new)", code);
        // 整体加参数化数组强转，保住静态元素类型并切断推断变量传播。
        Assert.Contains("(Tuple6<Integer, Integer, Integer, Integer, Boolean, Double>[])", code);
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
