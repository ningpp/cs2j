using CSharpToJava.Core.ArrayMethodRewriter;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// Array.Sort&lt;T&gt; / Array.BinarySearch&lt;T&gt; 等显式泛型静态调用改写为
/// Cs2jArray.ArrayHelper.XXX&lt;T&gt;(...) 时必须保留类型实参。
/// 丢失 &lt;T&gt; 会落到非泛型 Sort(Array, IComparer) 重载，而链上前序阶段已把
/// 非泛型 IComparer 映射为 IComparer&lt;object&gt;，导致 CS1503 无法转换。
/// 现场：xml-tiny XmlTextReaderImpl 的 Array.Sort&lt;object&gt;(nameSortedAttributes, comparer)。
/// </summary>
public class ArrayGenericStaticCallTypeArgTests
{
    private static ArrayMethodRewriterResult RewriteWithSemantics(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
        };
        var compilation = CSharpCompilation.Create(
            "test",
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        var rewriter = new ArrayMethodRewriter();
        return rewriter.Rewrite(source, model);
    }

    [Fact]
    public void GenericSort_ExplicitTypeArg_Preserved()
    {
        var source = "using System; using System.Collections.Generic; class C { void M(object[] a, IComparer<object> c) { Array.Sort<object>(a, c); } }\n";
        var result = RewriteWithSemantics(source);
        Assert.True(result.Changed);
        Assert.Contains("Cs2jArray.ArrayHelper.Sort<object>(a, c)", result.OutputCode, StringComparison.Ordinal);
    }

    [Fact]
    public void GenericBinarySearch_ExplicitTypeArg_Preserved()
    {
        var source = "using System; using System.Collections.Generic; class C { void M(object[] a, object v, IComparer<object> c) { var i = Array.BinarySearch<object>(a, v, c); } }\n";
        var result = RewriteWithSemantics(source);
        Assert.True(result.Changed);
        Assert.Contains("Cs2jArray.ArrayHelper.BinarySearch<object>(a, v, c)", result.OutputCode, StringComparison.Ordinal);
    }
}
