using CSharpToJava.Core.ArrayMethodRewriter;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.ProjectTests;

public class ArrayMethodRewriterUnitTests
{
    private static ArrayMethodRewriterResult RewriteWithSemantics(
        string source, ArrayMethodRewriterOptions? options = null)
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
        return rewriter.Rewrite(source, model, options);
    }

    [Fact]
    public void CleanFile_NoArrayMembers_BytePreserved()
    {
        var source = "class C { void M() { var x = 1 + 2; } }\n";
        var result = RewriteWithSemantics(source);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
    }

    [Fact]
    public void OneDimLength_Whitelisted_NotRewritten()
    {
        var source = "class C { void M() { int[] a = {1,2,3}; var n = a.Length; } }\n";
        var result = RewriteWithSemantics(source);
        Assert.False(result.Changed);
    }

    [Fact]
    public void MultiDimLength_RewrittenToGetLength()
    {
        var source = "class C { void M() { int[,] a = new int[2,3]; var n = a.Length; } }\n";
        var result = RewriteWithSemantics(source);
        Assert.True(result.Changed);
        Assert.Contains("Cs2jArray.ArrayHelper.GetLength", result.OutputCode);
        Assert.Equal(1, result.Statistics.MultiDimLengthRewrites);
    }

    [Fact]
    public void LongLength_RewrittenToGetLongLength()
    {
        var source = "class C { void M() { int[] a = {1,2,3}; var n = a.LongLength; } }\n";
        var result = RewriteWithSemantics(source);
        Assert.True(result.Changed);
        Assert.Contains("Cs2jArray.ArrayHelper.GetLongLength", result.OutputCode);
    }

    [Fact]
    public void Rank_InlinedAsConstant()
    {
        var source = "class C { void M() { int[,] a = new int[2,3]; var r = a.Rank; } }\n";
        var result = RewriteWithSemantics(source);
        Assert.True(result.Changed);
        Assert.Contains("2", result.OutputCode);
        Assert.Equal(1, result.Statistics.RankInlined);
    }

    [Fact]
    public void Clone_InstanceCallRewritten()
    {
        var source = "class C { void M() { int[] a = {1,2,3}; var b = a.Clone(); } }\n";
        var result = RewriteWithSemantics(source);
        Assert.True(result.Changed);
        Assert.Contains("Cs2jArray.ArrayHelper.Clone", result.OutputCode);
    }

    [Fact]
    public void ArraySort_StaticCallRewritten()
    {
        var source = "using System; class C { void M() { int[] a = {3,1,2}; Array.Sort(a); } }\n";
        var result = RewriteWithSemantics(source);
        Assert.True(result.Changed);
        Assert.Contains("Cs2jArray.ArrayHelper.Sort", result.OutputCode);
        Assert.Equal(1, result.Statistics.StaticArrayCalls);
    }

    [Fact]
    public void ArrayResize_StatementRewritten()
    {
        var source = "using System; class C { void M() { int[] a = {1,2,3}; Array.Resize(ref a, 5); } }\n";
        var result = RewriteWithSemantics(source);
        Assert.True(result.Changed);
        Assert.Contains("Cs2jArray.ArrayHelper.Resize", result.OutputCode);
        Assert.Contains("a =", result.OutputCode);
        Assert.Equal(1, result.Statistics.ResizeStatements);
    }

    [Fact]
    public void StringLength_NotRewritten()
    {
        var source = "class C { void M() { string s = \"hello\"; var n = s.Length; } }\n";
        var result = RewriteWithSemantics(source);
        Assert.False(result.Changed);
    }

    [Fact]
    public void Foreach_MultiDimArray_Flattened()
    {
        var source = "class C { void M() { int[,] a = new int[2,3]; foreach (var x in a) { } } }\n";
        var result = RewriteWithSemantics(source);
        Assert.True(result.Changed);
        Assert.Contains("__cs2j_row0", result.OutputCode);
        Assert.Equal(1, result.Statistics.ForeachFlattened);
    }

    [Fact]
    public void Foreach_OneDimArray_NotFlattened()
    {
        var source = "class C { void M() { int[] a = {1,2,3}; foreach (var x in a) { } } }\n";
        var result = RewriteWithSemantics(source);
        Assert.False(result.Changed);
    }
}
