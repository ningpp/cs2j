using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ReflectionGetMethodMappingTests
{
    [Fact]
    public void GetMethod_WithBindingFlags_ConvertsPascalCaseNameToCamelCase()
    {
        var result = Convert(@"
using System.Reflection;

public class EdgeLabelPlacement
{
    public static void Test()
    {
        typeof(EdgeLabelPlacement).GetMethod(
            ""GetPossibleSides"",
            BindingFlags.Static | BindingFlags.NonPublic);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("getPossibleSides", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("ReflectionHelper.getDeclaredMethodByName", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.ReflectionHelper", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("GetPossibleSides", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("getDeclaredMethod(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void GetMethod_WithoutBindingFlags_NoTypes_UsesHelper()
    {
        var result = Convert(@"
public class Sample
{
    public static void Test()
    {
        typeof(Sample).GetMethod(""DoWork"");
    }
}");

        Assert.True(result.Success);
        Assert.Contains("doWork", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("ReflectionHelper.getMethodByName", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.ReflectionHelper", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("DoWork", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void GetMethod_WithParameterTypes_UsesDirectGetMethod()
    {
        var result = Convert(@"
public class Sample
{
    public void DoWork(int x, string y) { }
    public static void Test()
    {
        typeof(Sample).GetMethod(""DoWork"", new[] { typeof(int), typeof(string) });
    }
}");

        Assert.True(result.Success);
        Assert.Contains("getMethod(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("doWork", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("DoWork", result.GeneratedCode, StringComparison.Ordinal);
        // Must NOT use the ReflectionHelper when types are explicit
        Assert.DoesNotContain("ReflectionHelper", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void GetMethod_SpecialCaseGetEnumerator_MapsToIterator()
    {
        var result = Convert(@"
using System.Reflection;

public class MyCollection
{
    public static void Test()
    {
        typeof(MyCollection).GetMethod(
            ""GetEnumerator"",
            BindingFlags.Instance | BindingFlags.Public);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("iterator", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("ReflectionHelper.getDeclaredMethodByName", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("GetEnumerator", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void GetMethod_SpecialCaseGetHashCode_MapsToHashCode()
    {
        var result = Convert(@"
using System.Reflection;

public class Sample
{
    public static void Test()
    {
        typeof(Sample).GetMethod(
            ""GetHashCode"",
            BindingFlags.Instance | BindingFlags.Public);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("hashCode", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("ReflectionHelper.getDeclaredMethodByName", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("GetHashCode", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void MethodInfoInvoke_CastToIEnumerableOfPrimitive_UsesRuntimeIterableBridge()
    {
        var result = Convert(@"
using System.Collections.Generic;
using System.Reflection;

public class Sample
{
    private static double[] Values() => new double[] { 1, 2 };

    public static IEnumerable<double> Test()
    {
        MethodInfo methodInfo = typeof(Sample).GetMethod(
            ""Values"",
            BindingFlags.Static | BindingFlags.NonPublic);
        return (IEnumerable<double>)methodInfo.Invoke(null, new object[] { });
    }
}");

        Assert.True(result.Success);
        Assert.Contains("ReflectionHelper.asIterable", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("(Iterable<Double>)(methodInfo.invoke", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TypeAssembly_GetManifestResourceStream_AsUnmanagedMemoryStream_UsesCompatHelper()
    {
        var result = Convert(@"
using System.IO;

public class XmlWriter { }

public class Sample
{
    public static unsafe void Test()
    {
        UnmanagedMemoryStream memStream = (UnmanagedMemoryStream)typeof(XmlWriter).Assembly.GetManifestResourceStream(""XmlCharType.bin"");
        byte* chProps = memStream.PositionPointer;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("import io.github.ningpp.compat.AssemblyCompat", code, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.UnmanagedMemoryStream", code, StringComparison.Ordinal);
        Assert.Contains("UnmanagedMemoryStream memStream = (UnmanagedMemoryStream)(AssemblyCompat.getManifestResourceStream(XmlWriter.class, \"XmlCharType.bin\"))", code, StringComparison.Ordinal);
        Assert.Contains("MemorySegment chProps = memStream.getPositionPointer()", code, StringComparison.Ordinal);
        Assert.DoesNotContain(".class.getPackage().getManifestResourceStream", code, StringComparison.Ordinal);
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
