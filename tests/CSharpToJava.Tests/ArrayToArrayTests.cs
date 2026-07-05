using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ArrayToArrayTests
{
    [Fact]
    public void NonGenericArrayListCreation_MapsToJavaArrayList()
    {
        var result = Convert("""
using System.Collections;

class Edge { }

class Graph
{
    void RemoveNode(Edge edge)
    {
        var delendi = new ArrayList();
        delendi.Add(edge);
        foreach (Edge e in delendi) { }
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("import io.github.ningpp.compat.CSharpArrayList;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("var delendi = new CSharpArrayList();", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("delendi.add(edge);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet.system.Collections.ArrayList", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NonGenericArrayListToArray_WithJaggedArrayElement_UsesJavaArrayPrototype()
    {
        var result = Convert("""
using System.Collections;

class Program
{
    int[][] Build()
    {
        ArrayList transitionTable = new ArrayList();
        transitionTable.Add(new int[1]);
        return (int[][])transitionTable.ToArray(typeof(int[]));
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("return (int[][])(transitionTable.toArray(new int[0][]));", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new int[][0]", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".toArray(int[].class)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ToArray_OnReferenceTypeArray_UsesArrayHelperCopy()
    {
        var result = Convert("""
using System;
using System.Linq;

class Point
{
    public int X { get; set; }
    public int Y { get; set; }

    public override string ToString()
    {
        return $"({X}, {Y})";
    }
}

class Program
{
    static void Main()
    {
        Point[] points =
        {
            new Point { X = 10, Y = 20 },
            new Point { X = 30, Y = 40 }
        };

        Point[] newPoints = points.ToArray();

        System.Console.WriteLine(newPoints.Length);
        System.Console.WriteLine(newPoints[0]);
        System.Console.WriteLine(newPoints[1]);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Point[] newPoints = ArrayHelper.copyArray(points);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("System.out.println(newPoints.length);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("System.out.println(newPoints[0]);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("System.out.println(newPoints[1]);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("ArrayHelper.toList(points)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Arrays.stream(points)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ToArray_OnStructArray_UsesStructCopyAndPreservesNullElements()
    {
        var result = Convert("""
using System;
using System.Linq;

struct Point
{
    public int X { get; set; }
}

class Program
{
    static void Main()
    {
        Point[] points =
        {
            new Point { X = 10 },
            null
        };

        Point[] newPoints = points.ToArray();
        System.Console.WriteLine(newPoints[0]);
        System.Console.WriteLine(newPoints[1]);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Point[] newPoints = ArrayHelper.copyStructArray(points, Point[]::new, item -> item.clone());", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Arrays.stream(points)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ToArray_OnIntArray_UsesPrimitiveArrayHelperCopy()
    {
        var result = Convert("""
using System;
using System.Linq;

class Program
{
    static void Main()
    {
        int[] values = { 1, 2, 3 };
        int[] newValues = values.ToArray();
        System.Console.WriteLine(newValues[0]);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("int[] newValues = ArrayHelper.copyArray(values);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Arrays.stream(Arrays.stream(values)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ToArray_OnGenericReferenceArrayReturn_UsesFactoryCopyWithoutObjectArrayCast()
    {
        var result = Convert("""
using System;
using System.Linq;

class Point { public int X { get; set; } }

class Line<T>
{
    private T[] items;
    public Line(T[] items) { this.items = items; }
    public T[] GetAll() { return items; }
}

class Program
{
    void M(Line<Point> line)
    {
        Point[] points = line.GetAll().ToArray();
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Point[] points = ArrayHelper.copyArray(line.getAll(), Point[]::new);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new Object[size]", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Arrays.stream(line.getAll())", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ToArray_OnGenericStructArrayReturn_UsesStructFactoryCopy()
    {
        var result = Convert("""
using System;
using System.Linq;

struct Point { public int X { get; set; } }

class Line<T>
{
    private T[] items;
    public Line(T[] items) { this.items = items; }
    public T[] GetAll() { return items; }
}

class Program
{
    void M(Line<Point> line)
    {
        Point[] points = line.GetAll().ToArray();
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Point[] points = ArrayHelper.copyStructArray(line.getAll(), Point[]::new, item -> item.clone());", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new Object[size]", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Arrays.stream(line.getAll())", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ToArray_OnGenericIntArrayReturn_UnboxesWithArrayHelper()
    {
        var result = Convert("""
using System;
using System.Linq;

class Line<T>
{
    private T[] items;
    public Line(T[] items) { this.items = items; }
    public T[] GetAll() { return items; }
}

class Program
{
    void M(Line<int> line)
    {
        int[] values = line.GetAll().ToArray();
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("int[] values = ArrayHelper.toIntArray(line.getAll());", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Arrays.stream(Arrays.stream(line.getAll())", result.GeneratedCode, StringComparison.Ordinal);
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
