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
    /// Extension method call should be lowered to a static call:
    /// receiver.Method(arg) → HostClass.method(receiver, arg)
    /// </summary>
    [Fact]
    public void CallSite_LoweredToStaticCall()
    {
        var result = Convert(@"
public static class StringExtensions
{
    public static string Repeat(this string s, int count)
    {
        return s + count;
    }
}
public class Consumer
{
    void Test()
    {
        string s = ""hello"";
        var result = s.Repeat(3);
    }
}");

        Assert.True(result.Success);
        // Call site should be: StringExtensions.repeat(s, 3)
        Assert.Contains("StringExtensions.repeat(s, 3)", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Extension method with no extra arguments (only the receiver).
    /// </summary>
    [Fact]
    public void CallSite_ReceiverOnly_NoExtraArgs()
    {
        var result = Convert(@"
public static class ListExtensions
{
    public static bool IsEmpty<T>(this System.Collections.Generic.List<T> list)
    {
        return list.Count == 0;
    }
}
public class Consumer
{
    void Test()
    {
        var list = new System.Collections.Generic.List<int>();
        var empty = list.IsEmpty();
    }
}");

        Assert.True(result.Success);
        Assert.Contains("ListExtensions.isEmpty(list)", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Extension method with multiple arguments.
    /// </summary>
    [Fact]
    public void CallSite_MultipleArguments()
    {
        var result = Convert(@"
public static class MathExtensions
{
    public static int Clamp(this int value, int min, int max)
    {
        return value < min ? min : value > max ? max : value;
    }
}
public class Consumer
{
    void Test()
    {
        int x = 5;
        var clamped = x.Clamp(0, 10);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("MathExtensions.clamp(x, 0, 10)", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Generic extension method should preserve type parameters.
    /// </summary>
    [Fact]
    public void Generic_ExtensionMethod_PreservesTypeParams()
    {
        var result = Convert(@"
using System.Collections.Generic;
public static class CollectionExtensions
{
    public static void AddIfNotNull<T>(this List<T> list, T item) where T : class
    {
        if (item != null) list.Add(item);
    }
}
public class Consumer
{
    void Test()
    {
        var list = new List<string>();
        list.AddIfNotNull(""hello"");
    }
}");

        Assert.True(result.Success);
        Assert.Contains("CollectionExtensions.addIfNotNull(list, \"hello\")", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Extension method on a custom type (not a built-in type).
    /// </summary>
    [Fact]
    public void ExtensionOn_CustomType()
    {
        var result = Convert(@"
public class Point
{
    public double X { get; set; }
    public double Y { get; set; }
}
public static class PointExtensions
{
    public static double DistanceTo(this Point p, Point other)
    {
        return System.Math.Sqrt((p.X - other.X) * (p.X - other.X) + (p.Y - other.Y) * (p.Y - other.Y));
    }
}
public class Consumer
{
    void Test()
    {
        var a = new Point();
        var b = new Point();
        var dist = a.DistanceTo(b);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("PointExtensions.distanceTo(a, b)", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Extension method chaining should produce nested static calls.
    /// </summary>
    [Fact]
    public void Chained_ExtensionMethods()
    {
        var result = Convert(@"
public static class IntExtensions
{
    public static int Double(this int x) { return x * 2; }
    public static int AddOne(this int x) { return x + 1; }
}
public class Consumer
{
    void Test()
    {
        int x = 5;
        var result = x.Double().AddOne();
    }
}");

        Assert.True(result.Success);
        // Chained: x.Double().AddOne() → IntExtensions.addOne(IntExtensions.double_(x))
        // Note: 'double' is a Java keyword, so it gets escaped
        Assert.Contains("IntExtensions.addOne(", result.GeneratedCode, StringComparison.Ordinal);
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

    /// <summary>
    /// Extension method with void return type should work correctly.
    /// </summary>
    [Fact]
    public void CallSite_VoidReturnType()
    {
        var result = Convert(@"
using System.Collections.Generic;
public static class ListExtensions
{
    public static void AddRange<T>(this List<T> list, params T[] items)
    {
        foreach (var item in items) list.Add(item);
    }
}
public class Consumer
{
    void Test()
    {
        var list = new List<string>();
        list.AddRange(""a"", ""b"", ""c"");
    }
}");

        Assert.True(result.Success);
        Assert.Contains("ListExtensions.addRange(list,", result.GeneratedCode, StringComparison.Ordinal);
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
