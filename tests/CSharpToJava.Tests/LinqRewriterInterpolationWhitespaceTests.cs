using CSharpToJava.Core.Context;
using CSharpToJava.Core.LinqRewrite;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C26 M4 门回归：sdk 语料（System.Text.Json 的 NumberHandlingTests）在
/// linq-channel 下出现 2 个行为回归，根因是 Roslyn 的 NormalizeWhitespace
/// 会改写插值字符串——verbatim 插值串首段为转义引号（""）时插入前导空格，
/// 普通插值串含转义引号时会被拆碎。LinqRewriter 在把生成的过程化方法加回
/// 类型时对整个类型声明调用 NormalizeWhitespace，污染了同类型中未改写的方法。
/// 这里直接驱动 LinqRewriter，锁定占位符保护（SafeNormalizeWhitespace）行为。
/// </summary>
public class LinqRewriterInterpolationWhitespaceTests
{
    private static string RewriteLinq(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src);
        var refs = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
        };
        var runtimePath = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!,
            "System.Runtime.dll");
        if (System.IO.File.Exists(runtimePath))
            refs.Add(MetadataReference.CreateFromFile(runtimePath));

        var compilation = CSharpCompilation.Create("Test", new[] { tree }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var semantic = compilation.GetSemanticModel(tree);
        var rewriter = new LinqRewriter(semantic, new ConversionOptions());
        var result = rewriter.Visit(tree.GetRoot());
        Assert.True(rewriter.RewrittenLinqQueries > 0, "夹具必须触发至少一次 LINQ 改写");
        return result.ToFullString();
    }

    [Fact]
    public void VerbatimInterpolation_InUnrelatedMethod_IsNotGivenLeadingSpace()
    {
        var rewritten = RewriteLinq(@"
using System.Collections.Generic;
using System.Linq;
class Sample {
    void M1(List<int> numbers) {
        var q = numbers.Select(n => n + 1);
    }
    string M2(int number) {
        return @$""""""{number}"""""";
    }
}");
        Assert.Contains("@$\"\"\"{number}\"\"\"", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("@$\" \"", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void RegularEscapedInterpolation_InUnrelatedMethod_IsNotShattered()
    {
        var rewritten = RewriteLinq(@"
using System.Collections.Generic;
using System.Linq;
class Sample {
    void M1(List<int> numbers) {
        var q = numbers.Select(n => n + 1);
    }
    string M2(int x) {
        return $""""""{x}"""""";
    }
}");
        Assert.Contains("$\"\"\"{x}\"\"\"", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void Interpolations_InsideRewrittenLambdaBodies_ArePreserved()
    {
        var rewritten = RewriteLinq(@"
using System.Collections.Generic;
using System.Linq;
class Sample {
    void M(List<int> numbers) {
        var regular = numbers.Select(n => $""""""{n}"""""" );
        var verbatim = numbers.Select(n => @$""""""{n}"""""" );
    }
}");
        Assert.Contains("$\"\"\"{_linqitem}\"\"\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("@$\"\"\"{_linqitem}\"\"\"", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("@$\" \"", rewritten, StringComparison.Ordinal);
    }
}
