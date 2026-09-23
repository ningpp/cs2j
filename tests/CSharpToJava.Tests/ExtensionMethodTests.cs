using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for extension method support:
/// - Declaration side: extension methods are kept as static methods in their host class
/// - Call side: extension method calls are lowered to static calls (HostClass.method(receiver, args...))
/// - Cross-project: extension method index scanning and validation
/// - Edge cases: generics, named arguments, overloads, using aliases
/// </summary>
public class ExtensionMethodTests
{
    /// <summary>
    /// Basic extension method declaration should keep the receiver parameter
    /// and output as a static method in the host class.
    /// </summary>
    [Fact]
    public void Declaration_KeepsReceiverAsFirstParameter()
    {
        var result = Convert(@"
public static class StringExtensions
{
    public static string Repeat(this string s, int count)
    {
        return string.Join("""", Enumerable.Repeat(s, count));
    }
}");

        Assert.True(result.Success);
        // The 'this string s' parameter should remain as the first parameter (without 'this')
        Assert.Contains("static String repeat(String s, int count)", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// RewriteExtensionMethods option should strip receiver parameter when enabled (single-project mode).
    /// </summary>
    [Fact]
    public void RewriteExtensionMethods_StripsReceiver_WhenEnabled()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
public static class StringExtensions
{
    public static string Repeat(this string s, int count)
    {
        return s + count;
    }
}",
            FileName = "Test.cs",
            Options = new ConversionOptions { RewriteExtensionMethods = true },
        });

        Assert.True(result.Success);
        // When RewriteExtensionMethods=true, the 'this string s' parameter is stripped
        Assert.Contains("static String repeat(int count)", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// ExtensionMethodIndex scanning should discover extension methods from compilation.
    /// </summary>
    [Fact]
    public void Index_ScansCompilation_FindsExtensionMethods()
    {
        var code = @"
namespace MyNamespace
{
    public static class StringExtensions
    {
        public static string Reverse(this string s) { return s; }
        public static int WordCount(this string s) { return 0; }
    }
    public static class ListExtensions
    {
        public static void Shuffle<T>(this System.Collections.Generic.List<T> list) { }
    }
}";
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(code, path: "Test.cs");
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("TestAssembly")
            .AddReferences(Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddSyntaxTrees(tree);

        var index = new ExtensionMethodIndex();
        ExtensionMethodIndex.ScanCompilation(index, compilation, "TestProject", ns => ns.ToLowerInvariant());

        Assert.Equal(3, index.Count);

        var reverseDescriptors = index.FindByReceiverType("string");
        Assert.Contains(reverseDescriptors, d => d.MethodName == "Reverse");
        Assert.Contains(reverseDescriptors, d => d.MethodName == "WordCount");

        var byProject = index.FindByProject("TestProject");
        Assert.Equal(3, byProject.Count);

        var shuffle = byProject.FirstOrDefault(d => d.MethodName == "Shuffle");
        Assert.NotNull(shuffle);
        Assert.Equal(1, shuffle.GenericArity);
        Assert.Equal("mynamespace", shuffle.JavaPackage);
        Assert.Equal("ListExtensions", shuffle.JavaHostClassName);
    }

    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = csharpCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
