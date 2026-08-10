using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that (T[])(IEnumerable)x casts compile: the source is an enumerable
/// interface (not a Java Collection subtype), so a plain (T[]) cast on the
/// CSharpGenericIterable<?> mapping is rejected by javac. The C# cast is a
/// reference cast (the runtime object IS a TElement[]), so Java needs the
/// identity hop through Object. Reproduces ArrayConverter.OnWriteResume
/// "incompatible types: CSharpGenericIterable cannot be converted to TElement[]".
/// </summary>
public class EnumerableInterfaceToArrayCastTests
{
    [Fact]
    public void CastIEnumerableInterfaceToArray_EmitsObjectHop()
    {
        var result = Convert(@"
abstract class Holder<TCollection, TElement> where TCollection : System.Collections.IEnumerable
{
    protected bool Write(TCollection value)
    {
        TElement[] array = (TElement[])(System.Collections.IEnumerable)value;
        return array.Length > 0;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("(TElement[]) (Object) ((CSharpGenericIterable<?>)(value))", code);
        Assert.DoesNotContain("(TElement[])((CSharpGenericIterable<?>)(value))", code);
    }

    [Fact]
    public void CastGenericIEnumerableInterfaceToArray_EmitsObjectHop()
    {
        var result = Convert(@"
abstract class Holder<TElement>
{
    protected bool Write(System.Collections.Generic.IEnumerable<TElement> value)
    {
        TElement[] array = (TElement[])(System.Collections.Generic.IEnumerable<TElement>)value;
        return array.Length > 0;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("(TElement[]) (Object)", code);
    }


    [Fact]
    public void CastConcreteIEnumerablePropertyToArray_ConvertsViaToArray()
    {
        // Regression for agl-master StaircaseRemover.InsertPathSegs:
        // (Point[])path.PathPoints where PathPoints is IEnumerable<Point>.
        // The Java representation of the property is a collection wrapper
        // (CSharpList/CSharpGenericIterable), so the identity hop through Object
        // throws "CSharpList cannot be cast to [LMicrosoft.Msagl.Core.Geometry.Point;".
        // The cast must convert the wrapper to a Point[] instead.
        var result = Convert(@"
class Point { }
class Path
{
    public System.Collections.Generic.IEnumerable<Point> PathPoints { get; set; }
}
class StaircaseRemover
{
    void InsertPathSegs(Path path)
    {
        InsertSegs((Point[])path.PathPoints);
    }
    void InsertSegs(Point[] pts) { }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("toArray(new Point[0])", code);
        Assert.Contains("instanceof Point[]", code);
        // The identity hop is only legal inside the instanceof-guarded ternary
        // (the value really is a Point[]), never as an unconditional cast. The
        // (Object) hop on the instanceof operand is required because javac rejects
        // "p instanceof Point[]" when p statically is an interface type.
        Assert.Contains("((Object) (path.getPathPoints())) instanceof Point[] ? (Point[]) (Object) (path.getPathPoints()) : (Point[]) (path.getPathPoints()).toArray(new Point[0])", code);
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
